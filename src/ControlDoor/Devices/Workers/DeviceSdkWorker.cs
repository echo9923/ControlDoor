using System;
using System.Collections.Generic;
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
            bool completed;
            lock (gate)
            {
                if (!queue.TryCancel(taskId, reason, out cancelledItem))
                {
                    return false;
                }

                var result = DeviceTaskResult.Cancelled(
                    cancelledItem.Task,
                    string.IsNullOrEmpty(reason) ? "Task was cancelled before execution." : reason);
                completed = CompleteTaskLocked(cancelledItem.Task, result, clearCurrentTask: false);
                queueInfo = BuildQueueInfoLocked();
                Monitor.PulseAll(gate);
            }

            registry.UpdateQueueInfo(cancelledItem.Task.DeviceId, queueInfo);
            return completed || cancelledItem.Task.Completion.IsCompleted;
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

                if (status == DeviceWorkerStatus.Running ||
                    status == DeviceWorkerStatus.Starting ||
                    status == DeviceWorkerStatus.Stopping ||
                    status == DeviceWorkerStatus.Stopped)
                {
                    return;
                }

                previousStopSource = stopSource;
                status = DeviceWorkerStatus.Starting;
                startedAt = DateTime.Now;
                stoppedAt = null;
                acceptingTasks = true;
                var source = new CancellationTokenSource();
                stopSource = source;
                loopTask = Task.Run(() => RunLoopGuardedAsync(source, source.Token));
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
                    task.TryRejectBeforeSubmission(rejected);
                    return DeviceTaskSubmissionResult.Rejected(task, rejected);
                }

                if (!task.TryReserveForQueue())
                {
                    var rejected = DeviceTaskResult.Rejected(task, "TASK_ALREADY_SUBMITTED", "Task has already been submitted.");
                    return DeviceTaskSubmissionResult.Rejected(task, rejected);
                }

                var now = DateTime.Now;
                var effectiveTimeout = task.GetEffectiveTimeoutMilliseconds(defaultTaskTimeoutMilliseconds);
                if (!queue.TryEnqueue(task, now, effectiveTimeout, out var item))
                {
                    var rejected = DeviceTaskResult.Rejected(task, "QUEUE_FULL", "Worker queue is full.");
                    task.ReleaseQueueReservation();
                    task.TryComplete(rejected);
                    var finalResult = task.TerminalResult ?? rejected;
                    return DeviceTaskSubmissionResult.Rejected(task, finalResult);
                }

                queueInfo = BuildQueueInfoLocked();
                Monitor.PulseAll(gate);
                if (item == null)
                {
                    var coalesced = task.TerminalResult ?? DeviceTaskResult.Rejected(task, "COALESCED", "Background task was coalesced.");
                    SetCompletionResult(task, coalesced);
                    result = DeviceTaskSubmissionResult.AcceptedResult(task, WorkerIndex, null, task.TerminalResult ?? coalesced);
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
            IReadOnlyList<int> cancelledDeviceIds;
            DeviceQueueInfo queueInfo;
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
                cancelledDeviceIds = CancelQueuedTasksLocked();
                queueInfo = BuildQueueInfoLocked();
                source = stopSource;
                CancelStopSource(source);
                Monitor.PulseAll(gate);
                runningLoop = loopTask;
            }

            foreach (var deviceId in cancelledDeviceIds)
            {
                registry.UpdateQueueInfo(deviceId, queueInfo);
            }

            var loopCompleted = runningLoop == null;
            if (runningLoop != null)
            {
                loopCompleted = (await Task.WhenAny(runningLoop, Task.Delay(waitTimeout)).ConfigureAwait(false)) == runningLoop;
                if (!loopCompleted)
                {
                    lock (gate)
                    {
                        lastError = "Worker stop timed out.";
                    }
                }
            }

            lock (gate)
            {
                if (loopCompleted)
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
                }

                Monitor.PulseAll(gate);
            }

            if (loopCompleted)
            {
                DisposeStopSource(source);
            }
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

        private async Task RunLoopGuardedAsync(CancellationTokenSource source, CancellationToken cancellationToken)
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
            finally
            {
                lock (gate)
                {
                    if (object.ReferenceEquals(stopSource, source) &&
                        status == DeviceWorkerStatus.Stopping &&
                        cancellationToken.IsCancellationRequested)
                    {
                        status = DeviceWorkerStatus.Stopped;
                        stoppedAt = DateTime.Now;
                        stopSource = null;
                        Monitor.PulseAll(gate);
                    }
                }

                DisposeStopSource(source);
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
            DeviceTaskResult boundaryResult = null;
            var canRun = false;
            lock (gate)
            {
                if (item.Cancelled || workerCancellationToken.IsCancellationRequested)
                {
                    boundaryResult = DeviceTaskResult.Cancelled(
                        task,
                        string.IsNullOrEmpty(item.CancellationReason) ? "Task was cancelled before execution." : item.CancellationReason);
                }
                else if (task.CallerCancellationToken.IsCancellationRequested)
                {
                    boundaryResult = DeviceTaskResult.Cancelled(task, "Caller cancelled before task started.");
                }
                else if (task.DeadlineAt.HasValue && task.DeadlineAt.Value <= startedAt)
                {
                    boundaryResult = DeviceTaskResult.Timeout(task, "Task expired before execution.", isWaitOutcome: false);
                }
                else
                {
                    canRun = task.TryMarkRunning(startedAt);
                }

                if (canRun)
                {
                    currentTaskId = task.TaskId;
                    currentDeviceId = task.DeviceId;
                    currentTaskType = task.TaskType;
                    currentTaskStartedAt = startedAt;
                }

                queueInfo = BuildQueueInfoLocked();
            }

            if (!canRun)
            {
                CompleteTask(
                    task,
                    task.TerminalResult ?? boundaryResult ?? DeviceTaskResult.Cancelled(task, "Task was completed before execution."),
                    clearCurrentTask: false);
                return;
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

        private void CompleteTask(DeviceSdkTask task, DeviceTaskResult result, bool clearCurrentTask = true)
        {
            DeviceQueueInfo queueInfo;
            lock (gate)
            {
                CompleteTaskLocked(task, result, clearCurrentTask);
                queueInfo = BuildQueueInfoLocked();
                Monitor.PulseAll(gate);
            }

            registry.UpdateQueueInfo(task.DeviceId, queueInfo);
        }

        private bool CompleteTaskLocked(DeviceSdkTask task, DeviceTaskResult result, bool clearCurrentTask)
        {
            if (result == null)
            {
                var now = DateTime.Now;
                result = DeviceTaskResult.FromTask(task, false, "INTERNAL_ERROR", "Task returned null result.", DeviceConnectionStatus.Unknown, now, now);
            }

            DeviceTaskResult finalResult;
            var completed = task.TryFinalizeFromWorker(result, out finalResult);
            if (task.TryClaimCompletionCount())
            {
                completedTaskCount++;
                if (finalResult != null && finalResult.Code == "CANCELLED")
                {
                    cancelledTaskCount++;
                }
                else if (finalResult != null && !finalResult.Success)
                {
                    failedTaskCount++;
                    lastError = finalResult.Code + ": " + finalResult.Message;
                }

                if (finalResult != null)
                {
                    lastTaskCompletedAt = finalResult.CompletedAt;
                }
            }

            if (clearCurrentTask && string.Equals(currentTaskId, task.TaskId, StringComparison.Ordinal))
            {
                currentTaskId = null;
                currentDeviceId = null;
                currentTaskType = null;
                currentTaskStartedAt = null;
            }

            return completed;
        }

        private void CompleteNonRunningTaskLocked(DeviceSdkTask task, DeviceTaskResult result, DeviceTaskExecutionState expectedState)
        {
            if (task.ExecutionState == DeviceTaskExecutionState.Rejected && expectedState == DeviceTaskExecutionState.Rejected)
            {
                task.TrySetCompletion(result);
                return;
            }

            task.TryComplete(result);
        }

        private static void SetCompletionResult(DeviceSdkTask task, DeviceTaskResult result)
        {
            task.TryComplete(result);
        }

        private void CompleteCancelled(DeviceSdkTask task, string message)
        {
            CompleteTask(task, DeviceTaskResult.Cancelled(task, message));
        }

        private IReadOnlyList<int> CancelQueuedTasksLocked()
        {
            var deviceIds = new HashSet<int>();
            foreach (var item in queue.Drain())
            {
                deviceIds.Add(item.Task.DeviceId);
                item.Cancel("Worker stopped before task started.");
                CompleteTaskLocked(
                    item.Task,
                    DeviceTaskResult.Cancelled(item.Task, "Worker stopped before task started."),
                    clearCurrentTask: false);
            }

            return new List<int>(deviceIds);
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
