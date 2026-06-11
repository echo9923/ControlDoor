using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Tasks;
using ControlDoor.Devices.Workers;

namespace ControlEntradaSalida.Tests
{
    public static class Stage2AdvancedWorkerStressTests
    {
        [TestCase]
        public static void DeviceTaskQueue_TryCancel_RemovesMiddleItemAndPreservesFifoOrder()
        {
            var queue = new DeviceTaskQueue(capacity: 5);
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var first = NewTask(1, DeviceTaskType.HealthCheck);
            var cancelled = NewTask(1, DeviceTaskType.SyncPerson);
            var third = NewTask(1, DeviceTaskType.UploadFace);

            Assert.True(queue.TryEnqueue(first, now, 30000, out _));
            Assert.True(queue.TryEnqueue(cancelled, now.AddMilliseconds(1), 30000, out _));
            Assert.True(queue.TryEnqueue(third, now.AddMilliseconds(2), 30000, out _));
            Assert.True(queue.TryCancel(cancelled.TaskId, "removed", out var cancelledItem));
            Assert.Equal(cancelled.TaskId, cancelledItem.Task.TaskId);
            Assert.Equal(2, queue.Count);

            Assert.True(queue.TryDequeue(now, out var firstItem, out _));
            Assert.True(queue.TryDequeue(now, out var thirdItem, out _));

            Assert.Equal(first.TaskId, firstItem.Task.TaskId);
            Assert.Equal(third.TaskId, thirdItem.Task.TaskId);
            Assert.Equal(0, queue.Count);
        }

        [TestCase]
        public static void DeviceTaskQueue_TryCancel_OnlyAffectsMatchingPriorityBucket()
        {
            var queue = new DeviceTaskQueue(capacity: 5);
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var high = NewTask(1, DeviceTaskType.SyncPerson);
            high.Priority = DeviceTaskPriority.High;
            var low = NewTask(1, DeviceTaskType.HealthCheck);
            low.Priority = DeviceTaskPriority.Low;
            var retry = NewTask(1, DeviceTaskType.RetryDeviceOperation);
            retry.Priority = DeviceTaskPriority.Retry;

            queue.TryEnqueue(high, now, 30000, out _);
            queue.TryEnqueue(low, now.AddMilliseconds(1), 30000, out _);
            queue.TryEnqueue(retry, now.AddMilliseconds(2), 30000, out _);

            Assert.True(queue.TryCancel(low.TaskId, "drop low", out _));
            var snapshot = queue.GetPrioritySnapshot(now);

            Assert.Equal(1, snapshot.GetQueueLength(DeviceTaskPriority.High));
            Assert.Equal(0, snapshot.GetQueueLength(DeviceTaskPriority.Low));
            Assert.Equal(1, snapshot.GetQueueLength(DeviceTaskPriority.Retry));
            Assert.Equal(2, queue.Count);
        }

        [TestCase]
        public static void DeviceSdkDispatcher_UnknownDeviceRejection_DoesNotStartWorkers()
        {
            var registry = NewRegistry(workerCount: 2);
            var dispatcher = NewDispatcher(registry, workerCount: 2, queueCapacity: 5);
            var task = NewTask(404, DeviceTaskType.HealthCheck);

            var result = dispatcher.Submit(task);
            var snapshots = dispatcher.GetWorkerSnapshots();

            Assert.False(result.Accepted);
            Assert.Equal("DEVICE_NOT_FOUND", result.ImmediateResult.Code);
            Assert.Equal(DeviceTaskExecutionState.Rejected, task.ExecutionState);
            Assert.True(snapshots.All(snapshot => snapshot.Status == DeviceWorkerStatus.Created), "Unknown device submission must not start worker loops.");
            Assert.True(snapshots.All(snapshot => snapshot.CompletedTaskCount == 0), "Rejected routing lookup must not touch worker counters.");
        }

        [TestCase]
        public static void DeviceSdkDispatcher_TryCancelRunningCompletedAndMissingTasks_ReturnsFalseWithoutCounterDrift()
        {
            var registry = NewRegistry(workerCount: 1);
            Register(registry, 1);
            var dispatcher = NewDispatcher(registry, workerCount: 1, queueCapacity: 10);
            var release = NewSignal();
            var started = NewSignal();
            var running = NewTask(1, DeviceTaskType.HealthCheck, async context =>
            {
                started.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return Success(context.Task);
            });

            DeviceTaskResult final;
            DeviceWorkerRuntimeSnapshot snapshot;
            try
            {
                dispatcher.Submit(running);
                WaitForSignal(started, "running task did not start.");

                Assert.False(dispatcher.TryCancelQueuedTask(running.TaskId, "already running"));
                Assert.False(dispatcher.TryCancelQueuedTask("missing-task", "missing"));

                release.SetResult(true);
                final = WaitForCompletion(running);
                Assert.False(dispatcher.TryCancelQueuedTask(running.TaskId, "already completed"));
                snapshot = dispatcher.GetWorkerSnapshots()[0];
            }
            finally
            {
                release.TrySetResult(true);
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }

            Assert.True(final.Success);
            Assert.Equal(DeviceTaskExecutionState.Succeeded, running.ExecutionState);
            Assert.Equal(1L, snapshot.CompletedTaskCount);
            Assert.Equal(0L, snapshot.CancelledTaskCount);
            Assert.Equal(0L, snapshot.FailedTaskCount);
        }

        [TestCase]
        public static void DeviceSdkDispatcher_StopDuringRunningTask_CancelsQueuedTasksButKeepsRunningResult()
        {
            var registry = NewRegistry(workerCount: 1);
            Register(registry, 1);
            var dispatcher = NewDispatcher(registry, workerCount: 1, queueCapacity: 10);
            var release = NewSignal();
            var started = NewSignal();
            var running = NewTask(1, DeviceTaskType.HealthCheck, async context =>
            {
                started.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return Success(context.Task);
            });
            var queued = Enumerable.Range(0, 3)
                .Select(index => NewTask(1, DeviceTaskType.SyncPerson))
                .ToList();

            DeviceTaskResult runningResult;
            var queuedResults = new List<DeviceTaskResult>();
            try
            {
                dispatcher.Submit(running);
                WaitForSignal(started, "running task did not start.");
                foreach (var task in queued)
                {
                    Assert.True(dispatcher.Submit(task).Accepted);
                }

                var stopTask = dispatcher.StopAsync(TimeSpan.FromMilliseconds(20));
                foreach (var task in queued)
                {
                    queuedResults.Add(WaitForCompletion(task));
                }

                release.SetResult(true);
                runningResult = WaitForCompletion(running);
                stopTask.GetAwaiter().GetResult();
            }
            finally
            {
                release.TrySetResult(true);
            }

            Assert.True(runningResult.Success);
            Assert.Equal(DeviceTaskExecutionState.Succeeded, running.ExecutionState);
            Assert.Equal(3, queuedResults.Count(result => result.Code == "CANCELLED"));
            Assert.True(queued.All(task => task.ExecutionState == DeviceTaskExecutionState.Cancelled));
        }

        [TestCase]
        public static void DeviceSdkDispatcher_ConcurrentSameDeviceSubmissions_NeverOverlapAndAllComplete()
        {
            var registry = NewRegistry(workerCount: 2);
            Register(registry, 1);
            var dispatcher = NewDispatcher(registry, workerCount: 2, queueCapacity: 50);
            var active = 0;
            var maxActive = 0;
            var tasks = Enumerable.Range(0, 20)
                .Select(index => NewTask(1, DeviceTaskType.HealthCheck, async context =>
                {
                    var current = System.Threading.Interlocked.Increment(ref active);
                    UpdateMax(ref maxActive, current);
                    await Task.Delay(1).ConfigureAwait(false);
                    System.Threading.Interlocked.Decrement(ref active);
                    return Success(context.Task);
                }))
                .ToList();

            try
            {
                Parallel.ForEach(tasks, task =>
                {
                    var result = dispatcher.Submit(task);
                    Assert.True(result.Accepted, "Concurrent submit should be accepted.");
                });

                foreach (var task in tasks)
                {
                    var result = WaitForCompletion(task);
                    Assert.True(result.Success);
                }
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }

            Assert.Equal(1, maxActive);
            Assert.True(tasks.All(task => task.ExecutionState == DeviceTaskExecutionState.Succeeded));
        }

        [TestCase]
        public static void DeviceSdkDispatcher_ConcurrentMultiDeviceSubmissions_CompleteAcrossFixedRoutes()
        {
            var registry = NewRegistry(workerCount: 3);
            Register(registry, 1);
            Register(registry, 2);
            Register(registry, 3);
            var dispatcher = NewDispatcher(registry, workerCount: 3, queueCapacity: 50);
            var tasks = Enumerable.Range(0, 30)
                .Select(index =>
                {
                    var deviceId = (index % 3) + 1;
                    return NewTask(deviceId, DeviceTaskType.SyncPerson, async context =>
                    {
                        await Task.Delay(1).ConfigureAwait(false);
                        return Success(context.Task);
                    });
                })
                .ToList();

            try
            {
                Parallel.ForEach(tasks, task =>
                {
                    var result = dispatcher.Submit(task);
                    Assert.True(result.Accepted, "Multi-device submit should be accepted.");
                    Assert.Equal(DeviceWorkerRouter.CalculateWorkerIndex(task.DeviceId, 3), result.WorkerIndex.Value);
                });

                foreach (var task in tasks)
                {
                    Assert.True(WaitForCompletion(task).Success);
                }
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }

            var snapshots = dispatcher.GetWorkerSnapshots();
            Assert.Equal(30L, snapshots.Sum(snapshot => snapshot.CompletedTaskCount));
            Assert.Equal(0L, snapshots.Sum(snapshot => snapshot.FailedTaskCount));
        }

        private static DeviceRuntimeRegistry NewRegistry(int workerCount)
        {
            return new DeviceRuntimeRegistry(new DeviceRuntimeRegistryOptions { WorkerCount = workerCount });
        }

        private static DeviceSdkDispatcher NewDispatcher(DeviceRuntimeRegistry registry, int workerCount, int queueCapacity)
        {
            return new DeviceSdkDispatcher(registry, new DeviceSdkDispatcherOptions
            {
                WorkerCount = workerCount,
                QueueCapacityPerWorker = queueCapacity,
                DefaultTaskTimeoutMilliseconds = 30000
            });
        }

        private static void Register(DeviceRuntimeRegistry registry, int deviceId)
        {
            var result = registry.Register(new DeviceRuntimeCreationOptions
            {
                DeviceId = deviceId,
                DeviceName = "device-" + deviceId,
                IpAddress = "10.2.1." + deviceId,
                Port = 8000,
                Username = "admin",
                Password = "pwd",
                Enabled = true,
                CreatedAt = DateTime.Now
            });
            Assert.True(result.Success, result.Message);
        }

        private static DeviceSdkTask NewTask(int deviceId, DeviceTaskType type)
        {
            return NewTask(deviceId, type, context => Task.FromResult(Success(context.Task)));
        }

        private static DeviceSdkTask NewTask(int deviceId, DeviceTaskType type, Func<DeviceTaskContext, Task<DeviceTaskResult>> execute)
        {
            return new DeviceSdkTask(deviceId, type, type.ToString(), execute);
        }

        private static DeviceTaskResult Success(DeviceSdkTask task)
        {
            var now = DateTime.Now;
            return DeviceTaskResult.FromTask(task, true, "OK", "ok", DeviceConnectionStatus.Online, now, now);
        }

        private static DeviceTaskResult WaitForCompletion(DeviceSdkTask task)
        {
            Assert.True(task.Completion.Task.Wait(TimeSpan.FromSeconds(2)), "Task did not complete in time: " + task.OperationName);
            return task.Completion.Task.GetAwaiter().GetResult();
        }

        private static void WaitForSignal(TaskCompletionSource<bool> signal, string message)
        {
            Assert.True(signal.Task.Wait(TimeSpan.FromSeconds(2)), message);
        }

        private static TaskCompletionSource<bool> NewSignal()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static void UpdateMax(ref int target, int value)
        {
            while (true)
            {
                var current = target;
                if (value <= current)
                {
                    return;
                }

                if (System.Threading.Interlocked.CompareExchange(ref target, value, current) == current)
                {
                    return;
                }
            }
        }
    }
}
