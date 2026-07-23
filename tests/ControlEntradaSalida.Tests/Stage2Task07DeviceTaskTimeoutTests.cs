using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Tasks;
using ControlDoor.Devices.Workers;

namespace ControlEntradaSalida.Tests
{
    public static class Stage2Task07DeviceTaskTimeoutTests
    {
        [TestCase]
        public static void DeviceSdkDispatcher_QueuedWaitTimeout_CancelsTaskBeforeExecution()
        {
            var dispatcher = NewDispatcher(queueCapacity: 10);
            var release = NewSignal();
            var runningStarted = NewSignal();
            var running = NewAsyncTask(1, DeviceTaskType.HealthCheck, async context =>
            {
                runningStarted.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return Success(context.Task);
            });
            var queued = NewAsyncTask(1, DeviceTaskType.SyncPerson, context => Task.FromResult(Success(context.Task)));
            queued.TimeoutMilliseconds = 10;

            try
            {
                dispatcher.Submit(running);
                WaitForSignal(runningStarted, "running task did not start.");
                var timeout = WaitForTask(dispatcher.SubmitAndWaitAsync(queued), "queued wait did not time out.");
                release.SetResult(true);
                WaitForCompletion(running);
                var queuedFinal = WaitForCompletion(queued);

                Assert.Equal("TIMEOUT", timeout.Code);
                Assert.True(timeout.IsWaitOutcome);
                Assert.Equal("CANCELLED", queuedFinal.Code);
                Assert.False(queuedFinal.IsWaitOutcome);
                Assert.True(queuedFinal.TaskCompleted);
                Assert.Equal(DeviceTaskExecutionState.Cancelled, queued.ExecutionState);
            }
            finally
            {
                release.TrySetResult(true);
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
        }

        [TestCase]
        public static void DeviceSdkDispatcher_CancelledQueuedTask_ReleasesWorkerCapacity()
        {
            var dispatcher = NewDispatcher(queueCapacity: 1);
            var release = NewSignal();
            var runningStarted = NewSignal();
            var running = NewAsyncTask(1, DeviceTaskType.HealthCheck, async context =>
            {
                runningStarted.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return Success(context.Task);
            });
            var queued = NewAsyncTask(1, DeviceTaskType.SyncPerson, context => Task.FromResult(Success(context.Task)));
            var replacement = NewAsyncTask(1, DeviceTaskType.UploadFace, context => Task.FromResult(Success(context.Task)));
            queued.TimeoutMilliseconds = 10;

            try
            {
                dispatcher.Submit(running);
                WaitForSignal(runningStarted, "running task did not start.");
                var timeout = WaitForTask(dispatcher.SubmitAndWaitAsync(queued), "queued wait did not time out.");
                var replacementSubmit = dispatcher.Submit(replacement);
                release.SetResult(true);
                WaitForCompletion(running);
                WaitForCompletion(replacement);

                Assert.Equal("TIMEOUT", timeout.Code);
                Assert.True(replacementSubmit.Accepted);
                Assert.Equal(DeviceTaskExecutionState.Cancelled, queued.ExecutionState);
                Assert.Equal(DeviceTaskExecutionState.Succeeded, replacement.ExecutionState);
            }
            finally
            {
                release.TrySetResult(true);
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
        }

        [TestCase]
        public static void DeviceSdkDispatcher_RunningWaitTimeout_DoesNotKillRunningTask()
        {
            var dispatcher = NewDispatcher(queueCapacity: 10);
            var release = NewSignal();
            var started = NewSignal();
            var task = NewAsyncTask(1, DeviceTaskType.HealthCheck, async context =>
            {
                started.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return Success(context.Task);
            });
            task.TimeoutMilliseconds = 10;

            try
            {
                var wait = dispatcher.SubmitAndWaitAsync(task);
                WaitForSignal(started, "task did not start.");
                var timeout = WaitForTask(wait, "running wait did not time out.");
                release.SetResult(true);
                var final = WaitForCompletion(task);

                Assert.Equal("TIMEOUT", timeout.Code);
                Assert.True(final.Success);
                Assert.Equal(DeviceTaskExecutionState.Succeeded, task.ExecutionState);
            }
            finally
            {
                release.TrySetResult(true);
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
        }

        [TestCase]
        public static void DeviceTaskExceptionMapper_MapsArgumentAndCancellationExceptions()
        {
            var task = NewAsyncTask(1, DeviceTaskType.SyncPerson, context => Task.FromResult(Success(context.Task)));
            var now = new DateTime(2026, 1, 1, 8, 0, 0);

            var invalid = DeviceTaskExceptionMapper.Map(task, new ArgumentException("bad payload"), now, now);
            var cancelled = DeviceTaskExceptionMapper.Map(task, new OperationCanceledException(), now, now);

            Assert.Equal("INVALID_ARGUMENT", invalid.Code);
            Assert.Equal("ArgumentException", invalid.ExceptionType);
            Assert.Equal("CANCELLED", cancelled.Code);
            Assert.Equal("OperationCanceledException", cancelled.ExceptionType);
        }

        [TestCase]
        public static void DeviceSdkWorker_MapsTaskExceptionsToUnifiedCodes()
        {
            var registry = NewRegistry();
            Register(registry, 1);
            var dispatcher = NewDispatcher(queueCapacity: 10);
            var invalidTask = NewAsyncTask(1, DeviceTaskType.SyncPerson, context => throw new ArgumentException("payload"));
            var cancelledTask = NewAsyncTask(1, DeviceTaskType.SyncPerson, context => throw new OperationCanceledException());

            try
            {
                dispatcher.Submit(invalidTask);
                dispatcher.Submit(cancelledTask);
                var invalid = WaitForCompletion(invalidTask);
                var cancelled = WaitForCompletion(cancelledTask);

                Assert.Equal("INVALID_ARGUMENT", invalid.Code);
                Assert.Equal("CANCELLED", cancelled.Code);
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
        }

        [TestCase]
        public static void DeviceSdkDispatcher_PreCancelledCallerToken_DoesNotSubmitTask()
        {
            var dispatcher = NewDispatcher(queueCapacity: 10);
            var task = NewAsyncTask(1, DeviceTaskType.HealthCheck, context => Task.FromResult(Success(context.Task)));
            var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var result = WaitForTask(dispatcher.SubmitAndWaitAsync(task, cancellation.Token), "pre-cancelled wait did not complete.");

            Assert.Equal("CANCELLED", result.Code);
            Assert.Equal(result.Code, WaitForCompletion(task).Code);
            Assert.Equal(result.Message, task.Completion.Task.GetAwaiter().GetResult().Message);
            Assert.True(result.TaskCompleted);
            Assert.Equal(DeviceTaskExecutionState.Cancelled, task.ExecutionState);
            Assert.Equal(0, dispatcher.GetWorkerSnapshots()[0].CompletedTaskCount);
        }

        [TestCase]
        public static void DeviceSdkWorker_PassesLinkedCallerCancellationTokenToRunningTask()
        {
            var dispatcher = NewDispatcher(queueCapacity: 10);
            var cancellation = new CancellationTokenSource();
            var started = NewSignal();
            var observedCancellation = NewSignal();
            var task = NewAsyncTask(1, DeviceTaskType.HealthCheck, async context =>
            {
                started.TrySetResult(true);
                while (!context.CancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(1).ConfigureAwait(false);
                }

                observedCancellation.TrySetResult(true);
                throw new OperationCanceledException(context.CancellationToken);
            });
            task.TimeoutMilliseconds = 0;

            try
            {
                var wait = dispatcher.SubmitAndWaitAsync(task, cancellation.Token);
                WaitForSignal(started, "running task did not start.");
                cancellation.Cancel();
                var waitResult = WaitForTask(wait, "caller cancellation did not return.");
                WaitForSignal(observedCancellation, "running task did not observe cancellation.");
                var final = WaitForCompletion(task);

                Assert.Equal("CANCELLED", waitResult.Code);
                Assert.Equal("CANCELLED", final.Code);
                Assert.Equal(DeviceTaskExecutionState.Cancelled, task.ExecutionState);
            }
            finally
            {
                cancellation.Cancel();
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
                cancellation.Dispose();
            }
        }

        [TestCase]
        public static void DeviceSdkWorker_PassesDeadlineCancellationTokenToManagedTask()
        {
            var dispatcher = NewDispatcher(queueCapacity: 10);
            var started = NewSignal();
            var observedCancellation = NewSignal();
            var task = NewAsyncTask(1, DeviceTaskType.HealthCheck, async context =>
            {
                started.TrySetResult(true);
                while (!context.CancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(1).ConfigureAwait(false);
                }

                observedCancellation.TrySetResult(true);
                throw new OperationCanceledException(context.CancellationToken);
            });
            task.TimeoutMilliseconds = 10;

            try
            {
                var submit = dispatcher.Submit(task);
                WaitForSignal(started, "running task did not start.");
                WaitForSignal(observedCancellation, "managed task did not observe deadline cancellation.");
                var final = WaitForCompletion(task);

                Assert.True(submit.Accepted);
                Assert.Equal("CANCELLED", final.Code);
                Assert.Equal(DeviceTaskExecutionState.Cancelled, task.ExecutionState);
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
        }

        [TestCase]
        public static void DeviceWorkerWatchdog_ReportsLongRunningCurrentTaskWithoutStoppingIt()
        {
            var snapshot = new DeviceWorkerRuntimeSnapshot
            {
                WorkerIndex = 2,
                CurrentTaskId = "task-1",
                CurrentDeviceId = 7,
                CurrentTaskType = DeviceTaskType.HealthCheck,
                CurrentTaskStartedAt = new DateTime(2026, 1, 1, 8, 0, 0)
            };
            var watchdog = new DeviceWorkerWatchdog(longRunningWarningMilliseconds: 1000);

            var first = watchdog.Scan(new[] { snapshot }, new DateTime(2026, 1, 1, 8, 0, 2)).Single();
            var second = watchdog.Scan(new[] { snapshot }, new DateTime(2026, 1, 1, 8, 0, 3)).Single();

            Assert.True(first.IsLongRunning);
            Assert.Equal(2000L, first.CurrentTaskDurationMilliseconds);
            Assert.Equal(1, first.LongRunningWarningCount);
            Assert.Equal(2, second.LongRunningWarningCount);
            Assert.Equal("task-1", second.TaskId);
        }

        private static DeviceRuntimeRegistry NewRegistry()
        {
            return new DeviceRuntimeRegistry(new DeviceRuntimeRegistryOptions { WorkerCount = 1 });
        }

        private static DeviceSdkDispatcher NewDispatcher(int queueCapacity)
        {
            return new DeviceSdkDispatcher(NewRegistryWithDevice(), new DeviceSdkDispatcherOptions
            {
                WorkerCount = 1,
                QueueCapacityPerWorker = queueCapacity,
                DefaultTaskTimeoutMilliseconds = 30000
            });
        }

        private static DeviceRuntimeRegistry NewRegistryWithDevice()
        {
            var registry = NewRegistry();
            Register(registry, 1);
            return registry;
        }

        private static void Register(DeviceRuntimeRegistry registry, int deviceId)
        {
            var existing = registry.TryGetByDeviceId(deviceId);
            if (existing.Found)
            {
                return;
            }

            var result = registry.Register(new DeviceRuntimeCreationOptions
            {
                DeviceId = deviceId,
                DeviceName = "device-" + deviceId,
                IpAddress = "10.0.7." + deviceId,
                Port = 8000,
                Username = "admin",
                Password = "pwd",
                Enabled = true,
                CreatedAt = DateTime.Now
            });
            Assert.True(result.Success, result.Message);
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

        private static T WaitForTask<T>(Task<T> task, string message)
        {
            Assert.True(task.Wait(TimeSpan.FromSeconds(2)), message);
            return task.GetAwaiter().GetResult();
        }

        private static void WaitForSignal(TaskCompletionSource<bool> signal, string message)
        {
            Assert.True(signal.Task.Wait(TimeSpan.FromSeconds(2)), message);
        }

        private static TaskCompletionSource<bool> NewSignal()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
