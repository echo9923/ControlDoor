using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Tasks;
using ControlDoor.Devices.Workers;

namespace ControlEntradaSalida.Tests
{
    public static class Stage2Task06DevicePriorityQueueTests
    {
        [TestCase]
        public static void DeviceTaskQueue_HighPriorityRunsBeforeQueuedLowPriority()
        {
            var queue = new DeviceTaskQueue(10);
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var low = NewTask(1, DeviceTaskType.HealthCheck, DeviceTaskPriority.Low);
            var high = NewTask(1, DeviceTaskType.Login, DeviceTaskPriority.High);

            queue.TryEnqueue(low, now, 30000, out _);
            queue.TryEnqueue(high, now.AddMilliseconds(1), 30000, out _);
            queue.TryDequeue(now.AddMilliseconds(2), out var first, out var selection);

            Assert.Equal(high.TaskId, first.Task.TaskId);
            Assert.Equal(DeviceTaskPriority.High, selection.Priority);
            Assert.False(selection.FairnessApplied);
        }

        [TestCase]
        public static void DeviceTaskQueue_SamePriorityKeepsFifoSequence()
        {
            var queue = new DeviceTaskQueue(10);
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var first = NewTask(1, DeviceTaskType.SyncPerson, DeviceTaskPriority.Normal);
            var second = NewTask(1, DeviceTaskType.UploadFace, DeviceTaskPriority.Normal);

            queue.TryEnqueue(first, now, 30000, out _);
            queue.TryEnqueue(second, now.AddMilliseconds(1), 30000, out _);
            queue.TryDequeue(now.AddMilliseconds(2), out var firstOut, out _);
            queue.TryDequeue(now.AddMilliseconds(3), out var secondOut, out _);

            Assert.Equal(first.TaskId, firstOut.Task.TaskId);
            Assert.Equal(second.TaskId, secondOut.Task.TaskId);
        }

        [TestCase]
        public static void DeviceTaskQueue_CriticalAlwaysBypassesFairness()
        {
            var queue = new DeviceTaskQueue(10, new DeviceTaskQueuePolicy
            {
                MaxHighPriorityBurst = 0,
                RetryAgingMilliseconds = 1,
                LowPriorityAgingMilliseconds = 1
            });
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var retry = NewTask(1, DeviceTaskType.RetryDeviceOperation, DeviceTaskPriority.Retry);
            var critical = NewTask(1, DeviceTaskType.Logout, DeviceTaskPriority.Critical);

            queue.TryEnqueue(retry, now, 30000, out _);
            queue.TryEnqueue(critical, now.AddMilliseconds(1), 30000, out _);
            queue.TryDequeue(now.AddSeconds(1), out var item, out var selection);

            Assert.Equal(critical.TaskId, item.Task.TaskId);
            Assert.Equal(DeviceTaskPriority.Critical, selection.Priority);
            Assert.False(selection.FairnessApplied);
        }

        [TestCase]
        public static void DeviceTaskQueue_AgedRetryCanRunAfterHighPriorityBurst()
        {
            var queue = new DeviceTaskQueue(10, new DeviceTaskQueuePolicy
            {
                MaxHighPriorityBurst = 1,
                RetryAgingMilliseconds = 1,
                LowPriorityAgingMilliseconds = 60000
            });
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var high1 = NewTask(1, DeviceTaskType.Login, DeviceTaskPriority.High);
            var high2 = NewTask(1, DeviceTaskType.SetupAlarm, DeviceTaskPriority.High);
            var retry = NewTask(1, DeviceTaskType.RetryDeviceOperation, DeviceTaskPriority.Retry);

            queue.TryEnqueue(high1, now, 30000, out _);
            queue.TryEnqueue(retry, now, 30000, out _);
            queue.TryEnqueue(high2, now.AddMilliseconds(1), 30000, out _);
            queue.TryDequeue(now.AddMilliseconds(2), out var first, out _);
            queue.TryDequeue(now.AddSeconds(1), out var second, out var selection);

            Assert.Equal(high1.TaskId, first.Task.TaskId);
            Assert.Equal(retry.TaskId, second.Task.TaskId);
            Assert.True(selection.FairnessApplied);
            Assert.Equal(1L, queue.GetPrioritySnapshot().FairnessSelectionCount);
        }

        [TestCase]
        public static void DeviceTaskQueue_CoalescesDuplicateLowPriorityHealthChecks()
        {
            var queue = new DeviceTaskQueue(10);
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var first = NewTask(1, DeviceTaskType.HealthCheck, DeviceTaskPriority.Low);
            var duplicate = NewTask(1, DeviceTaskType.HealthCheck, DeviceTaskPriority.Low);

            var firstAccepted = queue.TryEnqueue(first, now, 30000, out _);
            var secondAccepted = queue.TryEnqueue(duplicate, now.AddMilliseconds(1), 30000, out var duplicateItem);

            Assert.True(firstAccepted);
            Assert.True(secondAccepted);
            Assert.Equal(null, duplicateItem);
            Assert.Equal(1, queue.Count);
            Assert.Equal(1L, queue.GetPrioritySnapshot().CoalescedTaskCount);
            Assert.Equal(DeviceTaskExecutionState.Rejected, duplicate.ExecutionState);
        }

        [TestCase]
        public static void DeviceSdkWorker_DoesNotPreemptRunningLowPriorityTask()
        {
            var registry = NewRegistry();
            Register(registry, 1);
            var dispatcher = new DeviceSdkDispatcher(registry, new DeviceSdkDispatcherOptions
            {
                WorkerCount = 1,
                QueueCapacityPerWorker = 10,
                DefaultTaskTimeoutMilliseconds = 30000
            });
            var events = new List<string>();
            var release = NewSignal();
            var started = NewSignal();
            var low = NewAsyncTask(1, DeviceTaskType.HealthCheck, DeviceTaskPriority.Low, async context =>
            {
                events.Add("start:low");
                started.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                events.Add("end:low");
                return Success(context.Task);
            });
            var critical = NewAsyncTask(1, DeviceTaskType.Logout, DeviceTaskPriority.Critical, context =>
            {
                events.Add("critical");
                return Task.FromResult(Success(context.Task));
            });

            try
            {
                dispatcher.Submit(low);
                WaitForSignal(started, "low task did not start.");
                dispatcher.Submit(critical);
                release.SetResult(true);
                WaitForCompletion(low);
                WaitForCompletion(critical);
            }
            finally
            {
                release.TrySetResult(true);
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }

            Assert.Equal("start:low", events[0]);
            Assert.Equal("end:low", events[1]);
            Assert.Equal("critical", events[2]);
        }

        private static DeviceSdkTask NewTask(int deviceId, DeviceTaskType type, DeviceTaskPriority priority)
        {
            return NewAsyncTask(deviceId, type, priority, context => Task.FromResult(Success(context.Task)));
        }

        private static DeviceSdkTask NewAsyncTask(int deviceId, DeviceTaskType type, DeviceTaskPriority priority, Func<DeviceTaskContext, Task<DeviceTaskResult>> execute)
        {
            var task = new DeviceSdkTask(deviceId, type, type.ToString(), execute);
            task.Priority = priority;
            return task;
        }

        private static DeviceTaskResult Success(DeviceSdkTask task)
        {
            var now = DateTime.Now;
            return DeviceTaskResult.FromTask(task, true, "OK", "ok", DeviceConnectionStatus.Online, now, now);
        }

        private static DeviceRuntimeRegistry NewRegistry()
        {
            return new DeviceRuntimeRegistry(new DeviceRuntimeRegistryOptions { WorkerCount = 1 });
        }

        private static void Register(DeviceRuntimeRegistry registry, int deviceId)
        {
            var result = registry.Register(new DeviceRuntimeCreationOptions
            {
                DeviceId = deviceId,
                DeviceName = "device-" + deviceId,
                IpAddress = "10.0.6." + deviceId,
                Port = 8000,
                Username = "admin",
                Password = "pwd",
                Enabled = true,
                CreatedAt = DateTime.Now
            });
            Assert.True(result.Success, result.Message);
        }

        private static void WaitForCompletion(DeviceSdkTask task)
        {
            Assert.True(task.Completion.Task.Wait(TimeSpan.FromSeconds(2)), "Task did not complete in time: " + task.OperationName);
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
