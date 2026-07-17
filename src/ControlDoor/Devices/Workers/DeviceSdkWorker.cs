using System;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Tasks;
using ControlDoor.Observability;

namespace ControlDoor.Devices.Workers
{
    public sealed class DeviceSdkWorker : IDisposable
    {
        private readonly object gate = new object();
        private readonly DeviceTaskQueue queue;
        private readonly DeviceRuntimeRegistry registry;
        private readonly int defaultTaskTimeoutMilliseconds;
        private readonly ServiceLogger logger;
        private CancellationTokenSource stopSource;
        private Task loopTask;
        private DeviceWorkerStatus status = DeviceWorkerStatus.Created;
        private DateTime? startedAt;
        private DateTime? stoppedAt;
        private string currentTaskId;
        private int? currentDeviceId;
        private DeviceTaskType? currentTaskType;
        private DateTime? currentTaskStartedAt;
        private long completedTaskCount;
        private long failedTaskCount;
        private long cancelledTaskCount;
        private DateTime? lastTaskCompletedAt;
        private string lastError;
        private bool acceptingTasks = true;
        private bool disposed;

        public DeviceSdkWorker(
            int workerIndex,
            int queueCapacity,
            int defaultTaskTimeoutMilliseconds,
            DeviceRuntimeRegistry registry,
            ServiceLogger logger = null)
        {
            if (workerIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(workerIndex), "Worker index must be non-negative.");
            }

            WorkerIndex = workerIndex;
            queue = new DeviceTaskQueue(queueCapacity);
            this.defaultTaskTimeoutMilliseconds = defaultTaskTimeoutMilliseconds;
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.logger = logger;
        }

        public int WorkerIndex { get; private set; }

        public int QueueLength
        {
            get
            {
                lock (gate)
                {
                    return queue.Count;
                }
            }
        }

        public bool TryCancelQueuedTask(string taskId, string reason)
        {
            DeviceTaskQueueItem cancelledItem;
            DeviceQueueInfo queueInfo;
            DeviceTaskResult result;
            lock (gate)
            {
                if (!queue.TryCancel(taskId, reason, out cancelledItem))
                {
                    return false;
                }

                result = DeviceTaskResult.Cancelled(cancelledItem.Task, string.IsNullOrEmpty(reason) ? "Task was cancelled before execution." : reason);
                cancelledItem.Task.MarkCancelled(result.CompletedAt);
                cancelledItem.Task.Completion.TrySetResult(result);
                completedTaskCount++;
                cancelledTaskCount++;
                lastTaskCompletedAt = result.CompletedAt;
                queueInfo = BuildQueueInfoLocked();
                Monitor.PulseAll(gate);
            }

            registry.UpdateQueueInfo(cancelledItem.Task.DeviceId, queueInfo);
            return true;
        }

        public void Start()
        {
            CancellationTokenSource previousStopSource = null;
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                if (status == DeviceWorkerStatus.Running || status == DeviceWorkerStatus.Starting || status == DeviceWorkerStatus.Stopping)
                {
                    return;
                }

                previousStopSource = stopSource;
                status = DeviceWorkerStatus.Starting;
                startedAt = DateTime.Now;
                stoppedAt = null;
                acceptingTasks = true;
                stopSource = new CancellationTokenSource();
                loopTask = Task.Run(() => RunLoopGuardedAsync(stopSource.Token));
                status = DeviceWorkerStatus.Running;
                Monitor.PulseAll(gate);
            }

            DisposeStopSource(previousStopSource);
        }

        public DeviceTaskSubmissionResult Enqueue(DeviceSdkTask task)
        {
            if (task == null)
            {
                throw new ArgumentNullException(nameof(task));
            }

            DeviceQueueInfo queueInfo;
            DeviceTaskSubmissionResult result;
            lock (gate)
            {
                if (!acceptingTasks || status == DeviceWorkerStatus.Stopping || status == DeviceWorkerStatus.Stopped)
                {
                    var rejected = DeviceTaskResult.Rejected(task, "DISPATCHER_STOPPING", "Worker is stopping.");
                    task.MarkRejected(rejected);
                    task.Completion.TrySetResult(rejected);
                    return DeviceTaskSubmissionResult.Rejected(task, rejected);
                }

                var now = DateTime.Now;
                var effectiveTimeout = task.GetEffectiveTimeoutMilliseconds(defaultTaskTimeoutMilliseconds);
                if (!queue.TryEnqueue(task, now, effectiveTimeout, out var item))
                {
                    var rejected = DeviceTaskResult.Rejected(task, "QUEUE_FULL", "Worker queue is full.");
                    task.MarkRejected(rejected);
                    task.Completion.TrySetResult(rejected);
                    return DeviceTaskSubmissionResult.Rejected(task, rejected);
                }

                queueInfo = BuildQueueInfoLocked();
                Monitor.PulseAll(gate);
                if (item == null)
                {
                    var coalesced = DeviceTaskResult.Rejected(task, "COALESCED", "Background task was coalesced.");
                    task.Completion.TrySetResult(coalesced);
                    result = DeviceTaskSubmissionResult.AcceptedResult(task, WorkerIndex, null, coalesced);
                }
                else
                {
                    result = DeviceTaskSubmissionResult.AcceptedResult(task, WorkerIndex, task.Sequence.Value, DeviceTaskResult.Queued(task));
                }
            }

            registry.UpdateQueueInfo(task.DeviceId, queueInfo);
            return result;
        }

        public async Task StopAsync(TimeSpan waitTimeout)
        {
            Task runningLoop;
            CancellationTokenSource source;
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                if (status == DeviceWorkerStatus.Stopped || status == DeviceWorkerStatus.Created)
                {
                    status = DeviceWorkerStatus.Stopped;
                    stoppedAt = DateTime.Now;
                    source = stopSource;
                    stopSource = null;
                    Monitor.PulseAll(gate);
                    DisposeStopSource(source);
                    return;
                }

                acceptingTasks = false;
                status = DeviceWorkerStatus.Stopping;
                CancelQueuedTasksLocked();
                source = stopSource;
                CancelStopSource(source);
                Monitor.PulseAll(gate);
                runningLoop = loopTask;
            }

            if (runningLoop != null)
            {
                var completed = await Task.WhenAny(runningLoop, Task.Delay(waitTimeout)).ConfigureAwait(false);
                if (completed != runningLoop)
                {
                    lock (gate)
                    {
                        lastError = "Worker stop timed out.";
                    }
                }
            }

            lock (gate)
            {
                status = DeviceWorkerStatus.Stopped;
                stoppedAt = DateTime.Now;
                currentTaskId = null;
                currentDeviceId = null;
                currentTaskType = null;
                currentTaskStartedAt = null;
                if (object.ReferenceEquals(stopSource, source))
                {
                    stopSource = null;
                }

                Monitor.PulseAll(gate);
            }

            DisposeStopSource(source);
        }

        public void Dispose()
        {
            Task runningLoop;
            CancellationTokenSource source;
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                acceptingTasks = false;
                if (status != DeviceWorkerStatus.Stopped && status != DeviceWorkerStatus.Created)
                {
                    status = DeviceWorkerStatus.Stopping;
                    CancelQueuedTasksLocked();
                }

                source = stopSource;
                CancelStopSource(source);
                runningLoop = loopTask;
                Monitor.PulseAll(gate);
            }

            WaitForLoopBestEffort(runningLoop);

            lock (gate)
            {
                status = DeviceWorkerStatus.Stopped;
                stoppedAt = DateTime.Now;
                currentTaskId = null;
                currentDeviceId = null;
                currentTaskType = null;
                currentTaskStartedAt = null;
                if (object.ReferenceEquals(stopSource, source))
                {
                    stopSource = null;
                }

                Monitor.PulseAll(gate);
            }

            DisposeStopSource(source);
        }

        public DeviceWorkerRuntimeSnapshot GetSnapshot()
        {
            lock (gate)
            {
                return BuildSnapshotLocked();
            }
        }

        private async Task RunLoopGuardedAsync(CancellationToken cancellationToken)
        {
            try
            {
                await RunLoopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (gate)
                {
                    status = DeviceWorkerStatus.Faulted;
                    acceptingTasks = false;
                    lastError = ex.GetType().Name + ": " + ex.Message;
                    Monitor.PulseAll(gate);
                }
            }
        }

        private async Task RunLoopAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                DeviceTaskQueueItem item = null;
                DeviceQueueInfo queueInfo = null;
                lock (gate)
                {
                    while (!cancellationToken.IsCancellationRequested && !queue.TryDequeue(DateTime.Now, out item, out _))
                    {
                        Monitor.Wait(gate, TimeSpan.FromMilliseconds(100));
                    }

                    if (item == null && cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    queueInfo = BuildQueueInfoLocked();
                }

                try
                {
                    registry.UpdateQueueInfo(item.Task.DeviceId, queueInfo);
                    await ExecuteQueueItemAsync(item, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var now = DateTime.Now;
                    CompleteTask(item.Task, DeviceTaskExceptionMapper.Map(item.Task, ex, now, now));
                }
            }
        }

        private async Task ExecuteQueueItemAsync(DeviceTaskQueueItem item, CancellationToken workerCancellationToken)
        {
            var task = item.Task;
            if (item.Cancelled || workerCancellationToken.IsCancellationRequested)
            {
                CompleteCancelled(task, string.IsNullOrEmpty(item.CancellationReason) ? "Task was cancelled before execution." : item.CancellationReason);
                return;
            }

            var startedAt = DateTime.Now;
            DeviceTaskResult result;
            DeviceRuntimeSnapshot snapshotBeforeExecution;
            DeviceQueueInfo queueInfo;
            lock (gate)
            {
                currentTaskId = task.TaskId;
                currentDeviceId = task.DeviceId;
                currentTaskType = task.TaskType;
                currentTaskStartedAt = startedAt;
                task.MarkRunning(startedAt);
                queueInfo = BuildQueueInfoLocked();
            }

            snapshotBeforeExecution = registry.TryGetByDeviceId(task.DeviceId).Snapshot;
            registry.UpdateQueueInfo(task.DeviceId, queueInfo);

            try
            {
                if (snapshotBeforeExecution == null)
                {
                    result = DeviceTaskResult.Rejected(task, "DEVICE_NOT_FOUND", "Device runtime was not found.");
                }
                else if (snapshotBeforeExecution.IsDeleting && !task.AllowWhenDeleting)
                {
                    result = DeviceTaskResult.Rejected(task, "DEVICE_DELETING", "Device is deleting.");
                }
                else if (!task.AllowWhenManualDisconnected &&
                    (snapshotBeforeExecution.Reconnect.ManualDisconnected || snapshotBeforeExecution.Status == DeviceConnectionStatus.Disconnected))
                {
                    // 操作员已手动断开：已进入 worker 的延迟重连/登录等任务不得再把设备拉回在线。
                    result = DeviceTaskResult.Rejected(task, "DEVICE_MANUAL_DISCONNECTED", "Device is manually disconnected.");
                }
                else if (task.RequiresOnline && !snapshotBeforeExecution.IsConnected)
                {
                    result = DeviceTaskResult.Rejected(task, "DEVICE_OFFLINE", "Device is offline.");
                    result.Retryable = true;
                }
                else if (task.DeadlineAt.HasValue && task.DeadlineAt.Value <= startedAt)
                {
                    result = DeviceTaskResult.Timeout(task, "Task expired before execution.");
                }
                else
                {
                    using (var cancellationScope = DeviceTaskCancellationScope.Create(task, workerCancellationToken, startedAt))
                    {
                        var context = new DeviceTaskContext(task, registry, snapshotBeforeExecution, RequestContext.Background(task.OperationName), logger, cancellationScope.Token);
                        result = await task.ExecuteAsync(context).ConfigureAwait(false);
                    }

                    if (result == null)
                    {
                        result = DeviceTaskResult.Rejected(task, "INTERNAL_ERROR", "Task returned null result.");
                    }
                }
            }
            catch (Exception ex)
            {
                result = DeviceTaskExceptionMapper.Map(task, ex, startedAt, DateTime.Now);
            }

            CompleteTask(task, result.WithCompletionTiming(startedAt, DateTime.Now));
        }

        private void CompleteTask(DeviceSdkTask task, DeviceTaskResult result)
        {
            DeviceQueueInfo queueInfo;
            lock (gate)
            {
                task.MarkCompleted(result);
                completedTaskCount++;
                if (!result.Success)
                {
                    failedTaskCount++;
                    lastError = result.Code + ": " + result.Message;
                }

                if (result.Code == "CANCELLED")
                {
                    cancelledTaskCount++;
                }

                lastTaskCompletedAt = result.CompletedAt;
                currentTaskId = null;
                currentDeviceId = null;
                currentTaskType = null;
                currentTaskStartedAt = null;
                queueInfo = BuildQueueInfoLocked();
                Monitor.PulseAll(gate);
            }

            registry.UpdateQueueInfo(task.DeviceId, queueInfo);
            task.Completion.TrySetResult(result);
        }

        private void CompleteCancelled(DeviceSdkTask task, string message)
        {
            var result = DeviceTaskResult.Cancelled(task, message);
            DeviceQueueInfo queueInfo;
            task.MarkCancelled(result.CompletedAt);
            lock (gate)
            {
                completedTaskCount++;
                cancelledTaskCount++;
                lastTaskCompletedAt = result.CompletedAt;
                queueInfo = BuildQueueInfoLocked();
                Monitor.PulseAll(gate);
            }

            registry.UpdateQueueInfo(task.DeviceId, queueInfo);
            task.Completion.TrySetResult(result);
        }

        private void CancelQueuedTasksLocked()
        {
            foreach (var item in queue.Drain())
            {
                item.Cancel("Worker stopped before task started.");
                var result = DeviceTaskResult.Cancelled(item.Task, "Worker stopped before task started.");
                item.Task.MarkCancelled(result.CompletedAt);
                item.Task.Completion.TrySetResult(result);
                completedTaskCount++;
                cancelledTaskCount++;
                lastTaskCompletedAt = result.CompletedAt;
            }
        }

        private DeviceQueueInfo BuildQueueInfoLocked()
        {
            return new DeviceQueueInfo
            {
                WorkerIndex = WorkerIndex,
                QueuedTaskCount = queue.Count,
                CurrentTaskId = currentTaskId,
                CurrentTaskOperationName = currentTaskType.HasValue ? currentTaskType.Value.ToString() : null,
                CurrentTaskStartedAt = currentTaskStartedAt,
                LastTaskCompletedAt = lastTaskCompletedAt
            };
        }

        private DeviceWorkerRuntimeSnapshot BuildSnapshotLocked()
        {
            var oldest = queue.GetOldestEnqueuedAt();
            return new DeviceWorkerRuntimeSnapshot
            {
                WorkerIndex = WorkerIndex,
                Status = status,
                StartedAt = startedAt,
                StoppedAt = stoppedAt,
                CurrentTaskId = currentTaskId,
                CurrentDeviceId = currentDeviceId,
                CurrentTaskType = currentTaskType,
                CurrentTaskStartedAt = currentTaskStartedAt,
                QueueLength = queue.Count,
                CompletedTaskCount = completedTaskCount,
                FailedTaskCount = failedTaskCount,
                CancelledTaskCount = cancelledTaskCount,
                LastTaskCompletedAt = lastTaskCompletedAt,
                LastError = lastError,
                OldestQueuedTaskAgeMilliseconds = oldest.HasValue ? (long?)Math.Max(0, (DateTime.Now - oldest.Value).TotalMilliseconds) : null,
                PriorityQueue = queue.GetPrioritySnapshot()
            };
        }

        private static void CancelStopSource(CancellationTokenSource source)
        {
            if (source == null)
            {
                return;
            }

            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static void DisposeStopSource(CancellationTokenSource source)
        {
            if (source == null)
            {
                return;
            }

            try
            {
                source.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static void WaitForLoopBestEffort(Task runningLoop)
        {
            if (runningLoop == null)
            {
                return;
            }

            try
            {
                runningLoop.Wait(TimeSpan.FromMilliseconds(100));
            }
            catch (AggregateException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
