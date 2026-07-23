using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Devices.Tasks;
using ControlDoor.Observability;
using ControlDoor.Runtime;

namespace ControlDoor.Devices.Workers
{
    public sealed class DelayedDeviceTaskScheduler : IBackgroundTask, IDisposable
    {
        private readonly object gate = new object();
        private readonly DeviceSdkDispatcher dispatcher;
        private readonly DelayedDeviceTaskSchedulerOptions options;
        private readonly DelayedTaskQueue queue;
        private readonly ServiceLogger logger;
        private readonly List<DelayedTaskDispatchResult> recentDispatchResults = new List<DelayedTaskDispatchResult>();
        private readonly Dictionary<string, DelayedDeviceTask> inFlightDueTasks = new Dictionary<string, DelayedDeviceTask>(StringComparer.OrdinalIgnoreCase);
        private readonly BackgroundTaskStatus status = new BackgroundTaskStatus("DelayedDeviceTaskScheduler", false);
        private CancellationTokenSource stopSource;
        private Task loopTask;
        private bool running;
        private bool stopping;
        private bool stopped;
        private bool faulted;
        private bool disposed;
        private long dispatchSuccessCount;
        private long dispatchFailureCount;
        private long cancelledDelayedTaskCount;
        private long expiredDelayedTaskCount;
        private long coalescedDelayedTaskCount;
        private long rejectedDelayedTaskCount;

        public DelayedDeviceTaskScheduler(
            DeviceSdkDispatcher dispatcher,
            DelayedDeviceTaskSchedulerOptions options = null,
            ServiceLogger logger = null)
        {
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            this.options = options ?? new DelayedDeviceTaskSchedulerOptions();
            if (this.options.MaxDelayedTaskCount <= 0)
            {
                this.options.MaxDelayedTaskCount = 10000;
            }

            if (this.options.DispatchBatchSize <= 0)
            {
                this.options.DispatchBatchSize = 100;
            }

            if (this.options.WakeupMaxSleepMilliseconds <= 0)
            {
                this.options.WakeupMaxSleepMilliseconds = 30000;
            }

            if (this.options.DispatchRetryDelayMilliseconds <= 0)
            {
                this.options.DispatchRetryDelayMilliseconds = 1000;
            }

            this.logger = logger;
            queue = new DelayedTaskQueue(this.options.MaxDelayedTaskCount);
        }

        public string Name => "DelayedDeviceTaskScheduler";

        public bool IsCritical => false;

        public bool IsRunning
        {
            get
            {
                lock (gate)
                {
                    return running && !stopping && !stopped;
                }
            }
        }

        public DelayedTaskScheduleResult Schedule(DelayedDeviceTask task)
        {
            if (task == null)
            {
                throw new ArgumentNullException(nameof(task));
            }

            DelayedTaskScheduleResult result;
            lock (gate)
            {
                if (!options.Enabled)
                {
                    return DelayedTaskScheduleResult.Disabled(task);
                }

                if (stopping || stopped)
                {
                    return DelayedTaskScheduleResult.Stopped(task);
                }

                var inFlightTask = FindInFlightTaskByKeyLocked(task.TaskKey);
                if (inFlightTask != null)
                {
                    result = options.CoalesceByTaskKey && task.MergeMode != DelayedTaskMergeMode.None
                        ? DelayedTaskScheduleResult.CoalescedResult(inFlightTask, task, "Delayed task was coalesced with the task already being dispatched.")
                        : DelayedTaskScheduleResult.Rejected(task, "DUPLICATE_DELAYED_TASK_KEY", "Delayed task key is already being dispatched.");
                }
                else
                {
                    result = queue.TryEnqueue(task, options.CoalesceByTaskKey);
                }

                if (result.Status == DelayedTaskScheduleStatus.Coalesced)
                {
                    coalescedDelayedTaskCount++;
                }
                else if (result.Status == DelayedTaskScheduleStatus.Rejected)
                {
                    rejectedDelayedTaskCount++;
                }

                Monitor.PulseAll(gate);
            }

            if (result.Accepted)
            {
                logger?.Debug("DelayedDeviceTaskScheduler", result.Coalesced ? "延迟任务已合并。" : "延迟任务已调度。", new LogFields
                {
                    DeviceId = task.DeviceId,
                    OperationName = task.TaskType.ToString()
                });
            }

            return result;
        }

        public bool CancelByTaskId(string delayedTaskId, string reason)
        {
            DelayedDeviceTask task;
            lock (gate)
            {
                if (!queue.CancelByTaskId(delayedTaskId, reason, out task))
                {
                    return false;
                }

                cancelledDelayedTaskCount++;
                Monitor.PulseAll(gate);
            }

            logger?.Info("DelayedDeviceTaskScheduler", "Delayed task cancelled.", new LogFields { DeviceId = task.DeviceId, OperationName = task.TaskType.ToString() });
            return true;
        }

        public bool CancelByTaskKey(string taskKey, string reason)
        {
            DelayedDeviceTask task;
            lock (gate)
            {
                if (!queue.CancelByTaskKey(taskKey, reason, out task))
                {
                    return false;
                }

                cancelledDelayedTaskCount++;
                Monitor.PulseAll(gate);
            }

            logger?.Info("DelayedDeviceTaskScheduler", "Delayed task cancelled.", new LogFields { DeviceId = task.DeviceId, OperationName = task.TaskType.ToString() });
            return true;
        }

        public IReadOnlyList<DelayedDeviceTask> TakeByDevice(int deviceId)
        {
            lock (gate)
            {
                var tasks = queue.TakeByDevice(deviceId);
                if (tasks.Count > 0)
                {
                    Monitor.PulseAll(gate);
                }

                return tasks;
            }
        }

        public void RestoreTasks(IEnumerable<DelayedDeviceTask> tasks)
        {
            if (tasks == null)
            {
                return;
            }

            lock (gate)
            {
                var schedulerUnavailable = stopping || stopped || disposed;
                foreach (var task in tasks)
                {
                    if (task == null || task.Cancelled)
                    {
                        continue;
                    }

                    var result = queue.Restore(task, options.CoalesceByTaskKey);
                    if (result.Status == DelayedTaskScheduleStatus.Coalesced)
                    {
                        coalescedDelayedTaskCount++;
                    }
                    else if (!result.Accepted)
                    {
                        rejectedDelayedTaskCount++;
                        logger?.Warn("DelayedDeviceTaskScheduler", "Delayed task checkpoint restore was rejected.", new LogFields
                        {
                            DeviceId = task.DeviceId,
                            OperationName = task.TaskType.ToString(),
                            ErrorCode = result.Code
                        });
                    }

                    if (schedulerUnavailable)
                    {
                        logger?.Warn("DelayedDeviceTaskScheduler", "Delayed task checkpoint was restored while scheduler was unavailable; task retained for recovery.", new LogFields
                        {
                            DeviceId = task.DeviceId,
                            OperationName = task.TaskType.ToString(),
                            ErrorCode = "SCHEDULER_UNAVAILABLE"
                        });
                    }
                }

                Monitor.PulseAll(gate);
            }
        }

        public void DiscardTasks(IEnumerable<DelayedDeviceTask> tasks, string reason)
        {
            if (tasks == null)
            {
                return;
            }

            lock (gate)
            {
                foreach (var task in tasks)
                {
                    if (task == null || task.Cancelled)
                    {
                        continue;
                    }

                    task.Cancel(reason);
                    cancelledDelayedTaskCount++;
                }
            }
        }

        public IReadOnlyList<DelayedTaskDispatchResult> DispatchDueTasks(DateTime now)
        {
            IReadOnlyList<DelayedDeviceTask> dueTasks;
            lock (gate)
            {
                if (!options.Enabled || stopping || stopped)
                {
                    return new List<DelayedTaskDispatchResult>();
                }

                dueTasks = queue.TakeDue(now, options.DispatchBatchSize);
                foreach (var task in dueTasks)
                {
                    inFlightDueTasks[task.DelayedTaskId] = task;
                }
            }

            if (dueTasks.Count == 0)
            {
                return new List<DelayedTaskDispatchResult>();
            }

            var results = new List<DelayedTaskDispatchResult>(dueTasks.Count);
            foreach (var task in dueTasks)
            {
                try
                {
                    results.Add(DispatchOne(task, now));
                }
                catch (Exception ex)
                {
                    lock (gate)
                    {
                        RestoreInFlightTaskLocked(task, "unexpected dispatch failure");
                    }

                    results.Add(DelayedTaskDispatchResult.FactoryError(task, ex, now));
                }
            }

            lock (gate)
            {
                foreach (var result in results)
                {
                    RecordDispatchResultLocked(result);
                }

                Monitor.PulseAll(gate);
            }

            return results;
        }

        public Task<IReadOnlyList<DelayedTaskDispatchResult>> DispatchDueTasksAsync(DateTime now)
        {
            return Task.FromResult(DispatchDueTasks(now));
        }

        public void Start()
        {
            CancellationTokenSource previousStopSource = null;
            lock (gate)
            {
                if (disposed || !options.Enabled || running || stopping || stopped || faulted)
                {
                    return;
                }

                status.MarkStarting();
                stopping = false;
                running = true;
                previousStopSource = stopSource;
                var source = new CancellationTokenSource();
                stopSource = source;
                loopTask = Task.Run(() => RunLoop(source, source.Token));
                status.MarkStarted();
                Monitor.PulseAll(gate);
            }

            DisposeStopSource(previousStopSource);
        }

        public Task StartAsync(BackgroundTaskContext context)
        {
            Start();
            return Task.CompletedTask;
        }

        public async Task StopAsync(BackgroundTaskContext context)
        {
            await StopAsync(TimeSpan.FromMilliseconds(options.WakeupMaxSleepMilliseconds)).ConfigureAwait(false);
        }

        public async Task StopAsync(TimeSpan waitTimeout)
        {
            Task runningLoop;
            CancellationTokenSource source;
            lock (gate)
            {
                if (stopped)
                {
                    return;
                }

                stopping = true;
                RestoreInFlightTasksLocked("scheduler stopped");
                source = stopSource;
                CancelStopSource(source);
                Monitor.PulseAll(gate);
                runningLoop = loopTask;
            }

            var loopCompleted = runningLoop == null;
            if (runningLoop != null)
            {
                loopCompleted = (await Task.WhenAny(runningLoop, Task.Delay(waitTimeout)).ConfigureAwait(false)) == runningLoop;
                if (!loopCompleted)
                {
                    lock (gate)
                    {
                        status.MarkStopTimedOut();
                    }
                }
            }

            var disposeSource = false;
            lock (gate)
            {
                running = false;
                stopped = true;
                stopping = false;
                if (object.ReferenceEquals(stopSource, source) && loopCompleted)
                {
                    stopSource = null;
                    disposeSource = true;
                }

                if (!faulted)
                {
                    status.MarkStopped();
                }

                Monitor.PulseAll(gate);
            }

            if (disposeSource)
            {
                DisposeStopSource(source);
            }
        }

        public DelayedTaskSnapshot GetSnapshot(DateTime? now = null)
        {
            var snapshotAt = now ?? DateTime.Now;
            lock (gate)
            {
                return new DelayedTaskSnapshot(
                    queue.Count,
                    queue.GetEarliestDueAt(),
                    queue.CountDue(snapshotAt),
                    queue.GetCountBySource(),
                    queue.GetCountByPriority(),
                    dispatchSuccessCount,
                    dispatchFailureCount,
                    cancelledDelayedTaskCount,
                    expiredDelayedTaskCount,
                    coalescedDelayedTaskCount,
                    rejectedDelayedTaskCount);
            }
        }

        public IReadOnlyList<DelayedTaskDispatchResult> GetRecentDispatchResults()
        {
            lock (gate)
            {
                return recentDispatchResults.ToList();
            }
        }

        public BackgroundTaskStatus GetStatus()
        {
            lock (gate)
            {
                return status.Clone();
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
                stopping = true;
                RestoreInFlightTasksLocked("scheduler disposed");
                source = stopSource;
                CancelStopSource(source);
                runningLoop = loopTask;
                Monitor.PulseAll(gate);
            }

            var loopCompleted = WaitForLoopBestEffort(runningLoop);

            var disposeSource = false;
            lock (gate)
            {
                running = false;
                stopped = true;
                stopping = false;
                if (loopCompleted && object.ReferenceEquals(stopSource, source))
                {
                    stopSource = null;
                    disposeSource = true;
                }

                status.MarkStopped();
                Monitor.PulseAll(gate);
            }

            if (disposeSource)
            {
                DisposeStopSource(source);
            }
        }

        private void RunLoop(CancellationTokenSource source, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    DispatchDueTasks(DateTime.Now);
                    var wait = ComputeWait(DateTime.Now);
                    lock (gate)
                    {
                        if (cancellationToken.IsCancellationRequested || stopping || stopped || disposed)
                        {
                            return;
                        }

                        Monitor.Wait(gate, wait);
                    }
                }
            }
            catch (Exception ex)
            {
                lock (gate)
                {
                    running = false;
                    stopping = false;
                    stopped = true;
                    faulted = true;
                    RestoreInFlightTasksLocked("scheduler faulted");
                    status.MarkFailed(ex);
                    Monitor.PulseAll(gate);
                }
            }
            finally
            {
                var disposeSource = false;
                lock (gate)
                {
                    if (object.ReferenceEquals(stopSource, source))
                    {
                        stopSource = null;
                        disposeSource = true;
                    }

                    if (!faulted && (stopping || stopped || cancellationToken.IsCancellationRequested || disposed))
                    {
                        running = false;
                        stopped = true;
                        stopping = false;
                        status.MarkStopped();
                    }

                    Monitor.PulseAll(gate);
                }

                if (disposeSource)
                {
                    DisposeStopSource(source);
                }
            }
        }

        private TimeSpan ComputeWait(DateTime now)
        {
            lock (gate)
            {
                var max = TimeSpan.FromMilliseconds(options.WakeupMaxSleepMilliseconds);
                var earliest = queue.GetEarliestDueAt();
                if (!earliest.HasValue)
                {
                    return max;
                }

                var wait = earliest.Value - now;
                if (wait <= TimeSpan.Zero)
                {
                    return TimeSpan.Zero;
                }

                return wait < max ? wait : max;
            }
        }

        private DelayedTaskDispatchResult DispatchOne(DelayedDeviceTask delayedTask, DateTime now)
        {
            if (delayedTask.Cancelled)
            {
                CompleteInFlight(delayedTask);
                return DelayedTaskDispatchResult.Cancelled(delayedTask, now);
            }

            if (delayedTask.IsExpired(now))
            {
                CompleteInFlight(delayedTask);
                return DelayedTaskDispatchResult.Expired(delayedTask, now);
            }

            DeviceSdkTask task = null;
            try
            {
                task = delayedTask.CreateTask();
                lock (gate)
                {
                    if (stopping || stopped || disposed || !inFlightDueTasks.ContainsKey(delayedTask.DelayedTaskId))
                    {
                        RestoreInFlightTaskLocked(delayedTask, "scheduler stopped before submission");
                        return DelayedTaskDispatchResult.SchedulerStopped(delayedTask, now);
                    }

                    // Keep the scheduler gate through Submit and the result handling. Stop then has a
                    // clear linearization point: it either waits for this submission or restores it.
                    var submission = dispatcher.Submit(task);
                    var result = DelayedTaskDispatchResult.FromSubmission(delayedTask, task, submission, now);
                    if (!result.Success && IsDeviceDeletingDispatchFailure(result.Code))
                    {
                        // 删除中的设备拒绝普通任务：按重试延迟重新入队，不受普通瞬时失败重排上限限制。
                        RequeueAfterDeletingLocked(delayedTask, now);
                    }
                    else if (!result.Success && IsTransientDispatchFailure(result.Code))
                    {
                        // 瞬时背压：按重试延迟重新入队，避免到期任务被静默丢弃。
                        RequeueAfterTransientFailureLocked(delayedTask, now);
                    }
                    else if (!result.Success)
                    {
                        // 非瞬时失败（如 DEVICE_NOT_FOUND / INTERNAL_ERROR）：任务已从延迟队列取出且无后续补偿，记录告警保证可观测。
                        logger?.Warn("DelayedDeviceTaskScheduler", "Delayed task dispatch failed terminally; task dropped from delayed queue.", new LogFields
                        {
                            DeviceId = delayedTask.DeviceId,
                            OperationName = delayedTask.TaskType.ToString(),
                            ErrorCode = result.Code
                        });
                    }

                    inFlightDueTasks.Remove(delayedTask.DelayedTaskId);
                    Monitor.PulseAll(gate);
                    return result;
                }
            }
            catch (Exception ex)
            {
                lock (gate)
                {
                    if (stopping || stopped || disposed)
                    {
                        RestoreInFlightTaskLocked(delayedTask, "scheduler stopped after submission failure");
                    }
                    else
                    {
                        inFlightDueTasks.Remove(delayedTask.DelayedTaskId);
                    }

                    Monitor.PulseAll(gate);
                }

                return DelayedTaskDispatchResult.FactoryError(delayedTask, ex, now);
            }
        }

        // 重排上限：避免 dispatcher 持续故障（停止/队列满）时形成固定频率重试风暴。完整退避/抖动见 P2 #4。
        private const int MaxTransientRequeueAttempts = 5;

        private static bool IsDeviceDeletingDispatchFailure(string code)
        {
            return string.Equals(code, "DEVICE_DELETING", StringComparison.Ordinal);
        }

        private static bool IsTransientDispatchFailure(string code)
        {
            return string.Equals(code, "QUEUE_FULL", StringComparison.Ordinal) ||
                string.Equals(code, "DISPATCHER_STOPPING", StringComparison.Ordinal) ||
                string.Equals(code, "DISPATCHER_STOPPED", StringComparison.Ordinal);
        }

        private DelayedDeviceTask FindInFlightTaskByKeyLocked(string taskKey)
        {
            if (string.IsNullOrWhiteSpace(taskKey))
            {
                return null;
            }

            return inFlightDueTasks.Values.FirstOrDefault(task =>
                string.Equals(task.TaskKey, taskKey, StringComparison.OrdinalIgnoreCase));
        }

        private void CompleteInFlight(DelayedDeviceTask delayedTask)
        {
            lock (gate)
            {
                if (delayedTask != null)
                {
                    inFlightDueTasks.Remove(delayedTask.DelayedTaskId);
                }

                Monitor.PulseAll(gate);
            }
        }

        private void RestoreInFlightTaskLocked(DelayedDeviceTask delayedTask, string reason)
        {
            if (delayedTask == null || !inFlightDueTasks.Remove(delayedTask.DelayedTaskId))
            {
                return;
            }

            RestoreTaskLocked(delayedTask, reason);
            Monitor.PulseAll(gate);
        }

        private void RestoreInFlightTasksLocked(string reason)
        {
            if (inFlightDueTasks.Count == 0)
            {
                return;
            }

            var tasks = inFlightDueTasks.Values.ToList();
            inFlightDueTasks.Clear();
            foreach (var task in tasks)
            {
                RestoreTaskLocked(task, reason);
            }

            Monitor.PulseAll(gate);
        }

        private void RestoreTaskLocked(DelayedDeviceTask delayedTask, string reason)
        {
            if (delayedTask == null || delayedTask.Cancelled)
            {
                return;
            }

            var requeue = queue.Restore(delayedTask, coalesceByTaskKey: false);
            if (!requeue.Accepted && requeue.Code != "DUPLICATE_DELAYED_TASK_KEY")
            {
                logger?.Warn("DelayedDeviceTaskScheduler", "Delayed task restoration was rejected.", new LogFields
                {
                    DeviceId = delayedTask.DeviceId,
                    OperationName = delayedTask.TaskType.ToString(),
                    ErrorCode = requeue.Code,
                    Extra =
                    {
                        ["reason"] = reason ?? string.Empty
                    }
                });
            }
        }

        private void RequeueAfterDeletingLocked(DelayedDeviceTask delayedTask, DateTime now)
        {
            delayedTask.Attempt++;
            delayedTask.MoveDueAt(now.AddMilliseconds(options.DispatchRetryDelayMilliseconds));
            var requeue = queue.Restore(delayedTask, coalesceByTaskKey: false);
            if (!requeue.Accepted && requeue.Code != "DUPLICATE_DELAYED_TASK_KEY")
            {
                // 同 key 已有更新任务属于正常合并不再告警；其余重排失败记录告警，任务丢失可观测。
                logger?.Warn("DelayedDeviceTaskScheduler", "Delayed task requeue after device deleting dispatch failure was rejected.", new LogFields
                {
                    DeviceId = delayedTask.DeviceId,
                    OperationName = delayedTask.TaskType.ToString(),
                    ErrorCode = requeue.Code
                });
            }

            Monitor.PulseAll(gate);
        }

        private void RequeueAfterTransientFailureLocked(DelayedDeviceTask delayedTask, DateTime now)
        {
            if (delayedTask.Attempt >= MaxTransientRequeueAttempts)
            {
                // 超过重排上限：记录告警后放弃，避免 dispatcher 持续故障时形成固定频率重试风暴。
                logger?.Warn("DelayedDeviceTaskScheduler", "Delayed task requeue attempts exhausted after transient dispatch failures; task dropped.", new LogFields
                {
                    DeviceId = delayedTask.DeviceId,
                    OperationName = delayedTask.TaskType.ToString(),
                    ErrorCode = "REQUEUE_ATTEMPTS_EXHAUSTED"
                });
                return;
            }

            delayedTask.Attempt++;
            delayedTask.MoveDueAt(now.AddMilliseconds(options.DispatchRetryDelayMilliseconds));
            var requeue = queue.Restore(delayedTask, coalesceByTaskKey: false);
            if (!requeue.Accepted && requeue.Code != "DUPLICATE_DELAYED_TASK_KEY")
            {
                // 同 key 已有更新任务属于正常合并不再告警；其余重排失败记录告警，任务丢失可观测。
                logger?.Warn("DelayedDeviceTaskScheduler", "Delayed task requeue after transient dispatch failure was rejected.", new LogFields
                {
                    DeviceId = delayedTask.DeviceId,
                    OperationName = delayedTask.TaskType.ToString(),
                    ErrorCode = requeue.Code
                });
            }

            Monitor.PulseAll(gate);
        }

        private void RecordDispatchResultLocked(DelayedTaskDispatchResult result)
        {
            recentDispatchResults.Add(result);
            if (recentDispatchResults.Count > 100)
            {
                recentDispatchResults.RemoveAt(0);
            }

            if (result.Success)
            {
                dispatchSuccessCount++;
            }
            else if (result.Status == DelayedTaskDispatchStatus.Cancelled)
            {
                cancelledDelayedTaskCount++;
            }
            else if (result.Status == DelayedTaskDispatchStatus.Expired)
            {
                expiredDelayedTaskCount++;
            }
            else
            {
                dispatchFailureCount++;
            }
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

        private static bool WaitForLoopBestEffort(Task runningLoop)
        {
            if (runningLoop == null)
            {
                return true;
            }

            try
            {
                return runningLoop.Wait(TimeSpan.FromMilliseconds(100));
            }
            catch (AggregateException)
            {
                return true;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }
    }
}
