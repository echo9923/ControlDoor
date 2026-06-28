using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        private readonly BackgroundTaskStatus status = new BackgroundTaskStatus("DelayedDeviceTaskScheduler", false);
        private CancellationTokenSource stopSource;
        private Task loopTask;
        private bool running;
        private bool stopping;
        private bool stopped;
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

                result = queue.TryEnqueue(task, options.CoalesceByTaskKey);
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
            }

            if (dueTasks.Count == 0)
            {
                return new List<DelayedTaskDispatchResult>();
            }

            var results = dueTasks.Select(task => DispatchOne(task, now)).ToList();
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
                if (disposed || !options.Enabled || running || stopped)
                {
                    return;
                }

                stopping = false;
                running = true;
                previousStopSource = stopSource;
                stopSource = new CancellationTokenSource();
                loopTask = Task.Run(() => RunLoop(stopSource.Token));
                Monitor.PulseAll(gate);
            }

            DisposeStopSource(previousStopSource);
        }

        public Task StartAsync(BackgroundTaskContext context)
        {
            status.MarkStarting();
            Start();
            status.MarkStarted();
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
                    status.MarkStopTimedOut();
                }
            }

            lock (gate)
            {
                running = false;
                stopped = true;
                stopping = false;
                if (object.ReferenceEquals(stopSource, source))
                {
                    stopSource = null;
                }

                status.MarkStopped();
                Monitor.PulseAll(gate);
            }

            DisposeStopSource(source);
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
            return status.Clone();
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
                source = stopSource;
                CancelStopSource(source);
                runningLoop = loopTask;
                Monitor.PulseAll(gate);
            }

            WaitForLoopBestEffort(runningLoop);

            lock (gate)
            {
                running = false;
                stopped = true;
                stopping = false;
                if (object.ReferenceEquals(stopSource, source))
                {
                    stopSource = null;
                }

                status.MarkStopped();
                Monitor.PulseAll(gate);
            }

            DisposeStopSource(source);
        }

        private void RunLoop(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    DispatchDueTasks(DateTime.Now);
                    var wait = ComputeWait(DateTime.Now);
                    lock (gate)
                    {
                        if (cancellationToken.IsCancellationRequested || stopping || stopped)
                        {
                            return;
                        }

                        Monitor.Wait(gate, wait);
                    }

                }
            }
            catch (Exception ex)
            {
                status.MarkFailed(ex);
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
                return DelayedTaskDispatchResult.Cancelled(delayedTask, now);
            }

            if (delayedTask.IsExpired(now))
            {
                return DelayedTaskDispatchResult.Expired(delayedTask, now);
            }

            try
            {
                var task = delayedTask.CreateTask();
                var submission = dispatcher.Submit(task);
                return DelayedTaskDispatchResult.FromSubmission(delayedTask, task, submission, now);
            }
            catch (Exception ex)
            {
                return DelayedTaskDispatchResult.FactoryError(delayedTask, ex, now);
            }
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
