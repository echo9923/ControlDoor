using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Tasks;
using ControlDoor.Observability;

namespace ControlDoor.Devices.Workers
{
    public sealed class DeviceSdkDispatcher
    {
        private readonly object gate = new object();
        private readonly DeviceRuntimeRegistry registry;
        private readonly DeviceSdkWorker[] workers;
        private readonly int defaultTaskTimeoutMilliseconds;
        private bool started;
        private bool stopping;
        private bool stopped;

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
                if (started || stopped)
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

            var shouldStart = false;
            lock (gate)
            {
                if (stopping)
                {
                    var rejected = DeviceTaskResult.Rejected(task, "DISPATCHER_STOPPING", "Dispatcher is stopping.");
                    task.MarkRejected(rejected);
                    task.Completion.TrySetResult(rejected);
                    return DeviceTaskSubmissionResult.Rejected(task, rejected);
                }

                if (stopped)
                {
                    var rejected = DeviceTaskResult.Rejected(task, "DISPATCHER_STOPPED", "Dispatcher is stopped.");
                    task.MarkRejected(rejected);
                    task.Completion.TrySetResult(rejected);
                    return DeviceTaskSubmissionResult.Rejected(task, rejected);
                }

                shouldStart = !started;
            }

            if (shouldStart)
            {
                Start();
            }

            var route = registry.TryGetWorkerRoute(task.DeviceId);
            if (!route.Found || !route.WorkerIndex.HasValue)
            {
                var rejected = DeviceTaskResult.Rejected(task, "DEVICE_NOT_FOUND", "Device runtime was not found.");
                task.MarkRejected(rejected);
                task.Completion.TrySetResult(rejected);
                return DeviceTaskSubmissionResult.Rejected(task, rejected);
            }

            if (route.WorkerIndex.Value < 0 || route.WorkerIndex.Value >= workers.Length)
            {
                var rejected = DeviceTaskResult.Rejected(task, "INTERNAL_ERROR", "Worker route is outside dispatcher range.");
                task.MarkRejected(rejected);
                task.Completion.TrySetResult(rejected);
                return DeviceTaskSubmissionResult.Rejected(task, rejected);
            }

            return workers[route.WorkerIndex.Value].Enqueue(task);
        }

        public async Task<DeviceTaskResult> SubmitAndWaitAsync(DeviceSdkTask task)
        {
            var submitResult = Submit(task);
            if (!submitResult.Accepted || task.WaitMode != DeviceTaskWaitMode.WaitForResult)
            {
                return submitResult.ImmediateResult;
            }

            var timeout = task.GetEffectiveTimeoutMilliseconds(defaultTaskTimeoutMilliseconds);
            if (timeout <= 0)
            {
                return await task.Completion.Task.ConfigureAwait(false);
            }

            var completed = await Task.WhenAny(task.Completion.Task, Task.Delay(timeout)).ConfigureAwait(false);
            if (completed == task.Completion.Task)
            {
                return await task.Completion.Task.ConfigureAwait(false);
            }

            TryCancelQueuedTask(task.TaskId, "Caller wait timed out before task started.");
            return DeviceTaskResult.Timeout(task);
        }

        public bool TryCancelQueuedTask(string taskId, string reason)
        {
            return workers.Any(worker => worker.TryCancelQueuedTask(taskId, reason));
        }

        public async Task StopAsync(TimeSpan waitTimeout)
        {
            lock (gate)
            {
                if (!started || stopping)
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
            }
        }

        public IReadOnlyList<DeviceWorkerRuntimeSnapshot> GetWorkerSnapshots()
        {
            return workers.Select(worker => worker.GetSnapshot()).ToList();
        }
    }
}
