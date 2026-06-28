using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Tasks;
using ControlDoor.Devices.Workers;

namespace ControlEntradaSalida.Tests
{
    public static class Stage2Task05DeviceSdkWorkerTests
    {
        [TestCase]
        public static void DeviceSdkDispatcher_SubmitRejectsUnknownDevice()
        {
            var registry = NewRegistry(workerCount: 2);
            var dispatcher = NewDispatcher(registry, workerCount: 2, queueCapacity: 2);
            var task = NewAsyncTask(99, DeviceTaskType.HealthCheck, context => Task.FromResult(Success(context.Task)));

            var result = dispatcher.Submit(task);

            Assert.False(result.Accepted);
            Assert.Equal("DEVICE_NOT_FOUND", result.ImmediateResult.Code);
            Assert.Equal(DeviceTaskExecutionState.Rejected, task.ExecutionState);
        }

        [TestCase]
        public static void DeviceSdkWorker_ExecutesSameDeviceTasksSeriallyInFifoOrder()
        {
            var registry = NewRegistry(workerCount: 1);
            Register(registry, 1);
            var dispatcher = NewDispatcher(registry, workerCount: 1, queueCapacity: 10);
            var events = new List<string>();

            var first = NewAsyncTask(1, DeviceTaskType.SyncPerson, async context =>
            {
                events.Add("start:first");
                await Task.Delay(20).ConfigureAwait(false);
                events.Add("end:first");
                return Success(context.Task);
            });
            var second = NewAsyncTask(1, DeviceTaskType.UploadFace, async context =>
            {
                events.Add("start:second");
                await Task.Delay(1).ConfigureAwait(false);
                events.Add("end:second");
                return Success(context.Task);
            });

            try
            {
                dispatcher.Submit(first);
                dispatcher.Submit(second);
                WaitForCompletion(first);
                WaitForCompletion(second);
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }

            Assert.Equal("start:first", events[0]);
            Assert.Equal("end:first", events[1]);
            Assert.Equal("start:second", events[2]);
            Assert.Equal("end:second", events[3]);
        }

        [TestCase]
        public static void DeviceSdkWorker_DifferentWorkersCanRunInParallel()
        {
            var registry = NewRegistry(workerCount: 2);
            Register(registry, 1);
            Register(registry, 2);
            var dispatcher = NewDispatcher(registry, workerCount: 2, queueCapacity: 10);
            var release = NewSignal();
            var firstStarted = NewSignal();
            var secondStarted = NewSignal();

            var first = NewAsyncTask(1, DeviceTaskType.SyncPerson, async context =>
            {
                firstStarted.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return Success(context.Task);
            });
            var second = NewAsyncTask(2, DeviceTaskType.SyncPerson, async context =>
            {
                secondStarted.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return Success(context.Task);
            });

            try
            {
                dispatcher.Submit(first);
                dispatcher.Submit(second);
                WaitForSignal(firstStarted, "first task did not start.");
                WaitForSignal(secondStarted, "second task did not start.");
                release.SetResult(true);
                WaitForCompletion(first);
                WaitForCompletion(second);
            }
            finally
            {
                release.TrySetResult(true);
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }

            Assert.Equal(DeviceTaskExecutionState.Succeeded, first.ExecutionState);
            Assert.Equal(DeviceTaskExecutionState.Succeeded, second.ExecutionState);
        }

        [TestCase]
        public static void DeviceSdkWorker_TaskExceptionDoesNotStopWorker()
        {
            var registry = NewRegistry(workerCount: 1);
            Register(registry, 1);
            var dispatcher = NewDispatcher(registry, workerCount: 1, queueCapacity: 10);
            var bad = NewAsyncTask(1, DeviceTaskType.HealthCheck, context => throw new InvalidOperationException("boom"));
            var good = NewAsyncTask(1, DeviceTaskType.HealthCheck, context => Task.FromResult(Success(context.Task)));

            DeviceTaskResult badResult;
            DeviceTaskResult goodResult;
            DeviceWorkerRuntimeSnapshot snapshot;
            try
            {
                dispatcher.Submit(bad);
                dispatcher.Submit(good);
                badResult = WaitForCompletion(bad);
                goodResult = WaitForCompletion(good);
                snapshot = dispatcher.GetWorkerSnapshots()[0];
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }

            Assert.False(badResult.Success);
            Assert.Equal("INTERNAL_ERROR", badResult.Code);
            Assert.True(goodResult.Success);
            Assert.Equal(1L, snapshot.FailedTaskCount);
            Assert.Equal(2L, snapshot.CompletedTaskCount);
        }

        [TestCase]
        public static void DeviceSdkWorker_QueueFullRejectsExtraTask()
        {
            var registry = NewRegistry(workerCount: 1);
            Register(registry, 1);
            var dispatcher = NewDispatcher(registry, workerCount: 1, queueCapacity: 1);
            var release = NewSignal();
            var firstStarted = NewSignal();
            var running = NewAsyncTask(1, DeviceTaskType.HealthCheck, async context =>
            {
                firstStarted.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return Success(context.Task);
            });
            var queued = NewAsyncTask(1, DeviceTaskType.HealthCheck, context => Task.FromResult(Success(context.Task)));
            var rejected = NewAsyncTask(1, DeviceTaskType.HealthCheck, context => Task.FromResult(Success(context.Task)));

            DeviceTaskSubmissionResult rejectResult = null;
            try
            {
                dispatcher.Submit(running);
                WaitForSignal(firstStarted, "running task did not start.");
                dispatcher.Submit(queued);
                rejectResult = dispatcher.Submit(rejected);
                release.SetResult(true);
                WaitForCompletion(running);
                WaitForCompletion(queued);
            }
            finally
            {
                release.TrySetResult(true);
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }

            Assert.False(rejectResult.Accepted);
            Assert.Equal("QUEUE_FULL", rejectResult.ImmediateResult.Code);
            Assert.Equal(DeviceTaskExecutionState.Rejected, rejected.ExecutionState);
        }

        [TestCase]
        public static void DeviceSdkDispatcher_SubmitAndWaitReturnsTimeoutButTaskCanFinishLater()
        {
            var registry = NewRegistry(workerCount: 1);
            Register(registry, 1);
            var dispatcher = NewDispatcher(registry, workerCount: 1, queueCapacity: 10);
            var release = NewSignal();
            var task = NewAsyncTask(1, DeviceTaskType.HealthCheck, async context =>
            {
                await release.Task.ConfigureAwait(false);
                return Success(context.Task);
            });
            task.TimeoutMilliseconds = 10;

            DeviceTaskResult waitResult = null;
            DeviceTaskResult finalResult = null;
            try
            {
                var waitTask = dispatcher.SubmitAndWaitAsync(task);
                waitResult = WaitForTask(waitTask, "SubmitAndWait did not return timeout.");
                release.SetResult(true);
                finalResult = WaitForCompletion(task);
            }
            finally
            {
                release.TrySetResult(true);
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }

            Assert.False(waitResult.Success);
            Assert.Equal("TIMEOUT", waitResult.Code);
            Assert.True(finalResult.Success);
            Assert.Equal(DeviceTaskExecutionState.Succeeded, task.ExecutionState);
        }

        [TestCase]
        public static void DeviceSdkWorker_StopCancelsQueuedTasks()
        {
            var registry = NewRegistry(workerCount: 1);
            Register(registry, 1);
            var dispatcher = NewDispatcher(registry, workerCount: 1, queueCapacity: 10);
            var release = NewSignal();
            var runningStarted = NewSignal();
            var running = NewAsyncTask(1, DeviceTaskType.HealthCheck, async context =>
            {
                runningStarted.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return Success(context.Task);
            });
            var queued = NewAsyncTask(1, DeviceTaskType.HealthCheck, context => Task.FromResult(Success(context.Task)));

            DeviceTaskResult queuedResult = null;
            try
            {
                dispatcher.Submit(running);
                WaitForSignal(runningStarted, "running task did not start.");
                dispatcher.Submit(queued);
                var stopTask = dispatcher.StopAsync(TimeSpan.FromMilliseconds(10));
                queuedResult = WaitForCompletion(queued);
                release.SetResult(true);
                stopTask.GetAwaiter().GetResult();
            }
            finally
            {
                release.TrySetResult(true);
            }

            Assert.Equal("CANCELLED", queuedResult.Code);
            Assert.Equal(DeviceTaskExecutionState.Cancelled, queued.ExecutionState);
        }

        [TestCase]
        public static void DeviceSdkWorker_TaskCanUpdateRuntimeThroughRegistry()
        {
            var registry = NewRegistry(workerCount: 1);
            Register(registry, 1);
            var dispatcher = NewDispatcher(registry, workerCount: 1, queueCapacity: 10);
            var task = NewAsyncTask(1, DeviceTaskType.SyncPerson, context =>
            {
                context.Registry.TryGetByDeviceId(1).Snapshot.Capabilities.SupportsAcs = true;
                context.Registry.RegisterSdkUserId(1, 44, "SN44", DateTime.Now);
                return Task.FromResult(Success(context.Task));
            });

            DeviceTaskResult result = null;
            try
            {
                result = WaitForTask(dispatcher.SubmitAndWaitAsync(task), "SubmitAndWait did not return final result.");
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }

            Assert.True(result.Success);
            Assert.Equal(44, registry.TryGetByDeviceId(1).Snapshot.SdkUserId.Value);
            Assert.False(registry.TryGetByDeviceId(1).Snapshot.Capabilities.SupportsAcs);
        }

        [TestCase]
        public static void DeviceSdkWorker_StopAsync_DisposesStopSource()
        {
            var registry = NewRegistry(workerCount: 1);
            Register(registry, 1);
            var dispatcher = NewDispatcher(registry, workerCount: 1, queueCapacity: 10);
            var task = NewAsyncTask(1, DeviceTaskType.HealthCheck, context => Task.FromResult(Success(context.Task)));

            dispatcher.Submit(task);
            WaitForCompletion(task);
            Assert.NotNull(GetWorkerStopSource(dispatcher, 0), "Worker should create a stop source when it starts.");

            dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();

            Assert.Equal<CancellationTokenSource>(null, GetWorkerStopSource(dispatcher, 0));
        }

        [TestCase]
        public static void DeviceSdkDispatcher_Dispose_IsIdempotentAndDisposesWorkers()
        {
            var registry = NewRegistry(workerCount: 1);
            Register(registry, 1);
            var dispatcher = NewDispatcher(registry, workerCount: 1, queueCapacity: 10);
            var task = NewAsyncTask(1, DeviceTaskType.HealthCheck, context => Task.FromResult(Success(context.Task)));

            dispatcher.Submit(task);
            WaitForCompletion(task);
            Assert.NotNull(GetWorkerStopSource(dispatcher, 0), "Worker should create a stop source when it starts.");
            Assert.True(dispatcher is IDisposable, "Dispatcher should expose IDisposable for lifecycle cleanup.");

            ((IDisposable)dispatcher).Dispose();
            ((IDisposable)dispatcher).Dispose();

            Assert.Equal<CancellationTokenSource>(null, GetWorkerStopSource(dispatcher, 0));
        }

        [TestCase]
        public static void DeviceSdkDispatcher_DisposeWithoutStart_IsSafe()
        {
            var registry = NewRegistry(workerCount: 1);
            var dispatcher = NewDispatcher(registry, workerCount: 1, queueCapacity: 10);

            Assert.True(dispatcher is IDisposable, "Dispatcher should expose IDisposable for lifecycle cleanup.");

            ((IDisposable)dispatcher).Dispose();
            ((IDisposable)dispatcher).Dispose();
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
                IpAddress = "10.0.5." + deviceId,
                Port = 8000,
                Username = "admin",
                Password = "pwd",
                Enabled = true,
                CreatedAt = DateTime.Now
            });
            Assert.True(result.Success, result.Message);
        }

        private static DeviceSdkTask NewTask(int deviceId, DeviceTaskType type, Func<DeviceTaskContext, DeviceTaskResult> execute)
        {
            return new DeviceSdkTask(deviceId, type, type.ToString(), context => Task.FromResult(execute(context)));
        }

        private static DeviceSdkTask NewAsyncTask(int deviceId, DeviceTaskType type, Func<DeviceTaskContext, Task<DeviceTaskResult>> execute)
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

        private static T WaitForTask<T>(Task<T> task, string message)
        {
            Assert.True(task.Wait(TimeSpan.FromSeconds(2)), message);
            return task.GetAwaiter().GetResult();
        }

        private static TaskCompletionSource<bool> NewSignal()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static CancellationTokenSource GetWorkerStopSource(DeviceSdkDispatcher dispatcher, int workerIndex)
        {
            var workersField = typeof(DeviceSdkDispatcher).GetField("workers", BindingFlags.Instance | BindingFlags.NonPublic);
            var workers = (DeviceSdkWorker[])workersField.GetValue(dispatcher);
            var stopSourceField = typeof(DeviceSdkWorker).GetField("stopSource", BindingFlags.Instance | BindingFlags.NonPublic);
            return (CancellationTokenSource)stopSourceField.GetValue(workers[workerIndex]);
        }
    }
}
