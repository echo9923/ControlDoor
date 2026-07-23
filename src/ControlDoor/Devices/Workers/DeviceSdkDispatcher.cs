using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Tasks;
using ControlDoor.Observability;

namespace ControlDoor.Devices.Workers
{
    public sealed class DeviceSdkDispatcher : IDisposable
    {
        private readonly object gate = new object();
        private readonly DeviceRuntimeRegistry registry;
        private readonly DeviceSdkWorker[] workers;
        private readonly int defaultTaskTimeoutMilliseconds;
        private bool started;
        private bool stopping;
        private bool stopped;
        private bool disposed;

        public DeviceSdkDispatcher(DeviceRuntimeRegistry registry, DeviceSdkDispatcherOptions options = null, ServiceLogger logger = null)
        {
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            options = options ?? new DeviceSdkDispatcherOptions();
            if (options.WorkerCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options.WorkerCount), "WorkerCount must be greater than 0.");
            }

            if (options.QueueCapacityPerWorker <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options.QueueCapacityPerWorker), "QueueCapacityPerWorker must be greater than 0.");
            }

            defaultTaskTimeoutMilliseconds = options.DefaultTaskTimeoutMilliseconds;
            workers = Enumerable.Range(0, options.WorkerCount)
                .Select(index => new DeviceSdkWorker(index, options.QueueCapacityPerWorker, options.DefaultTaskTimeoutMilliseconds, registry, logger))
                .ToArray();
        }

        public DeviceSdkDispatcher(DeviceRuntimeRegistry registry, int workerCount, int queueCapacityPerWorker, int defaultTaskTimeoutMilliseconds)
            : this(
                registry,
                new DeviceSdkDispatcherOptions
                {
                    WorkerCount = workerCount,
                    QueueCapacityPerWorker = queueCapacityPerWorker,
                    DefaultTaskTimeoutMilliseconds = defaultTaskTimeoutMilliseconds
                })
        {
        }

        public int WorkerCount => workers.Length;

        public void Start()
        {
            lock (gate)
            {
                if (disposed)
                {
                    throw new ObjectDisposedException(nameof(DeviceSdkDispatcher));
                }

                if (started || stopping || stopped)
                {
                    return;
                }

                started = true;
                stopping = false;
                foreach (var worker in workers)
                {
                    worker.Start();
                }
            }
        }

        public DeviceTaskSubmissionResult Submit(DeviceSdkTask task)
        {
            if (task == null)
            {
                throw new ArgumentNullException(nameof(task));
            }

            lock (gate)
            {
                if (disposed)
                {
                    var rejected = DeviceTaskResult.Rejected(task, "DISPATCHER_STOPPED", "Dispatcher is disposed.");
                    task.TryRejectBeforeSubmission(rejected);
                    return DeviceTaskSubmissionResult.Rejected(task, rejected);
                }

                if (stopping)
                {
                    var rejected = DeviceTaskResult.Rejected(task, "DISPATCHER_STOPPING", "Dispatcher is stopping.");
                    task.TryRejectBeforeSubmission(rejected);
                    return DeviceTaskSubmissionResult.Rejected(task, rejected);
                }

                if (stopped)
                {
                    var rejected = DeviceTaskResult.Rejected(task, "DISPATCHER_STOPPED", "Dispatcher is stopped.");
                    task.TryRejectBeforeSubmission(rejected);
                    return DeviceTaskSubmissionResult.Rejected(task, rejected);
                }
            }

            var route = registry.TryGetWorkerRoute(task.DeviceId);
            if (!route.Found || !route.WorkerIndex.HasValue)
            {
                var rejected = DeviceTaskResult.Rejected(task, "DEVICE_NOT_FOUND", "Device runtime was not found.");
                task.TryRejectBeforeSubmission(rejected);
                return DeviceTaskSubmissionResult.Rejected(task, rejected);
            }

            if (route.WorkerIndex.Value < 0 || route.WorkerIndex.Value >= workers.Length)
            {
                var rejected = DeviceTaskResult.Rejected(task, "INTERNAL_ERROR", "Worker route is outside dispatcher range.");
                task.TryRejectBeforeSubmission(rejected);
                return DeviceTaskSubmissionResult.Rejected(task, rejected);
            }

            lock (gate)
            {
                if (disposed)
                {
                    var rejected = DeviceTaskResult.Rejected(task, "DISPATCHER_STOPPED", "Dispatcher is disposed.");
                    task.TryRejectBeforeSubmission(rejected);
                    return DeviceTaskSubmissionResult.Rejected(task, rejected);
                }

                if (stopping)
                {
                    var rejected = DeviceTaskResult.Rejected(task, "DISPATCHER_STOPPING", "Dispatcher is stopping.");
                    task.TryRejectBeforeSubmission(rejected);
                    return DeviceTaskSubmissionResult.Rejected(task, rejected);
                }

                if (stopped)
                {
                    var rejected = DeviceTaskResult.Rejected(task, "DISPATCHER_STOPPED", "Dispatcher is stopped.");
                    task.TryRejectBeforeSubmission(rejected);
                    return DeviceTaskSubmissionResult.Rejected(task, rejected);
                }

                if (!started)
                {
                    Start();
                }

                return workers[route.WorkerIndex.Value].Enqueue(task);
            }
        }

        public async Task<DeviceTaskResult> SubmitAndWaitAsync(DeviceSdkTask task)
        {
            return await SubmitAndWaitAsync(task, CancellationToken.None).ConfigureAwait(false);
        }

        public async Task<DeviceTaskResult> SubmitAndWaitAsync(DeviceSdkTask task, CancellationToken cancellationToken)
        {
            if (task == null)
            {
                throw new ArgumentNullException(nameof(task));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                var cancelled = DeviceTaskResult.Cancelled(task, "Caller cancelled before task submission.");
                if (task.TryCancelBeforeSubmission(cancelled))
                {
                    return task.TerminalResult;
                }

                return task.TerminalResult ?? cancelled;
            }

            task.AttachCallerCancellationToken(cancellationToken);
            var submitResult = Submit(task);
            if (!submitResult.Accepted || task.WaitMode != DeviceTaskWaitMode.WaitForResult)
            {
                return submitResult.ImmediateResult;
            }

            var timeout = task.GetEffectiveTimeoutMilliseconds(defaultTaskTimeoutMilliseconds);
            var waitTask = task.Completion.Task;
            if (timeout <= 0 && !cancellationToken.CanBeCanceled)
            {
                return await waitTask.ConfigureAwait(false);
            }

            var timeoutTask = timeout > 0 ? Task.Delay(timeout) : null;
            var cancellationTask = cancellationToken.CanBeCanceled
                ? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                : null;
            var waiters = new List<Task> { waitTask };
            if (timeoutTask != null)
            {
                waiters.Add(timeoutTask);
            }

            if (cancellationTask != null)
            {
                waiters.Add(cancellationTask);
            }

            var completed = await Task.WhenAny(waiters).ConfigureAwait(false);
            if (completed == waitTask || waitTask.IsCompleted)
            {
                return await waitTask.ConfigureAwait(false);
            }

            if (completed == cancellationTask)
            {
                var removed = TryCancelQueuedTask(task.TaskId, "Caller cancelled before task started.");
                if (!removed && waitTask.IsCompleted)
                {
                    return await waitTask.ConfigureAwait(false);
                }

                return DeviceTaskResult.Cancelled(task, "Caller cancelled while waiting for task result.", isWaitOutcome: true);
            }

            var removedBeforeExecution = TryCancelQueuedTask(task.TaskId, "Caller wait timed out before task started.");
            if (!removedBeforeExecution && waitTask.IsCompleted)
            {
                return await waitTask.ConfigureAwait(false);
            }

            return DeviceTaskResult.Timeout(task, "Caller wait timed out before task completed.", isWaitOutcome: true);
        }

        public bool TryCancelQueuedTask(string taskId, string reason)
        {
            return workers.Any(worker => worker.TryCancelQueuedTask(taskId, reason));
        }

        public async Task StopAsync(TimeSpan waitTimeout)
        {
            lock (gate)
            {
                if (disposed || !started || stopping)
                {
                    return;
                }

                stopping = true;
            }

            await Task.WhenAll(workers.Select(worker => worker.StopAsync(waitTimeout))).ConfigureAwait(false);

            lock (gate)
            {
                started = false;
                stopped = true;
                stopping = false;
            }
        }

        public IReadOnlyList<DeviceWorkerRuntimeSnapshot> GetWorkerSnapshots()
        {
            return workers.Select(worker => worker.GetSnapshot()).ToList();
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                stopping = true;
            }

            foreach (var worker in workers)
            {
                worker.Dispose();
            }

            lock (gate)
            {
                started = false;
                stopped = true;
                stopping = false;
            }
        }
    }
}
