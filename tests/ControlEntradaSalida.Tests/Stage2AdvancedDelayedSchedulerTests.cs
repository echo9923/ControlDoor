using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Tasks;
using ControlDoor.Devices.Workers;

namespace ControlEntradaSalida.Tests
{
    public static class Stage2AdvancedDelayedSchedulerTests
    {
        [TestCase]
        public static void DelayedDeviceTaskScheduler_CancelByTaskId_PreventsDispatchAndKeepsFactoryCold()
        {
            var dispatcher = NewDispatcher(queueCapacity: 10);
            var scheduler = NewScheduler(dispatcher);
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var factoryInvoked = false;
            var delayed = NewDelayedTask(now, "cancel-id:1", DeviceTaskType.HealthCheck, DeviceTaskPriority.Low, 0, task =>
            {
                factoryInvoked = true;
            });

            try
            {
                var scheduled = scheduler.Schedule(delayed);
                var cancelled = scheduler.CancelByTaskId(scheduled.DelayedTaskId, "operation superseded");
                var results = scheduler.DispatchDueTasks(now);
                var snapshot = scheduler.GetSnapshot(now);

                Assert.True(scheduled.Accepted);
                Assert.True(cancelled);
                Assert.Equal(0, results.Count);
                Assert.False(factoryInvoked);
                Assert.Equal(0, snapshot.DelayedTaskCount);
                Assert.Equal(1L, snapshot.CancelledDelayedTaskCount);
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
        }

        [TestCase]
        public static void DelayedDeviceTaskScheduler_DisabledSchedule_ReturnsDisabledAndDoesNotQueue()
        {
            var dispatcher = NewDispatcher(queueCapacity: 10);
            var scheduler = new DelayedDeviceTaskScheduler(dispatcher, new DelayedDeviceTaskSchedulerOptions
            {
                Enabled = false,
                MaxDelayedTaskCount = 10,
                DispatchBatchSize = 10,
                WakeupMaxSleepMilliseconds = 10
            });
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var factoryInvoked = false;
            var delayed = NewDelayedTask(now, "disabled:1", DeviceTaskType.HealthCheck, DeviceTaskPriority.Low, 0, task =>
            {
                factoryInvoked = true;
            });

            try
            {
                var schedule = scheduler.Schedule(delayed);
                scheduler.Start();
                var results = scheduler.DispatchDueTasks(now);
                var snapshot = scheduler.GetSnapshot(now);

                Assert.False(schedule.Accepted);
                Assert.Equal(DelayedTaskScheduleStatus.Disabled, schedule.Status);
                Assert.Equal("SCHEDULER_DISABLED", schedule.Code);
                Assert.Equal(0, results.Count);
                Assert.False(factoryInvoked);
                Assert.Equal(0, snapshot.DelayedTaskCount);
                Assert.Equal(0, snapshot.DueTaskCount);
                Assert.Equal(0L, snapshot.DispatchSuccessCount);
            }
            finally
            {
                scheduler.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
        }

        [TestCase]
        public static void DelayedDeviceTaskScheduler_RecentDispatchResults_RetainsLatestHundredInDispatchOrder()
        {
            var dispatcher = NewDispatcher(queueCapacity: 200);
            var scheduler = NewScheduler(dispatcher, maxDelayedTaskCount: 120, dispatchBatchSize: 120);
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var createdTasks = new List<DeviceSdkTask>();

            try
            {
                for (var index = 0; index < 105; index++)
                {
                    scheduler.Schedule(NewDelayedTask(
                        now,
                        "recent:" + index,
                        DeviceTaskType.SyncPerson,
                        DeviceTaskPriority.Normal,
                        index,
                        task => createdTasks.Add(task)));
                }

                var results = scheduler.DispatchDueTasks(now);
                foreach (var task in createdTasks)
                {
                    Assert.True(WaitForCompletion(task).Success);
                }

                var recent = scheduler.GetRecentDispatchResults();
                var snapshot = scheduler.GetSnapshot(now);

                Assert.Equal(105, results.Count);
                Assert.Equal(100, recent.Count);
                Assert.Equal("recent:5", recent[0].TaskKey);
                Assert.Equal("recent:104", recent[recent.Count - 1].TaskKey);
                Assert.Equal(105L, snapshot.DispatchSuccessCount);
                Assert.Equal(0L, snapshot.DispatchFailureCount);
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
        }

        [TestCase]
        public static void DelayedDeviceTaskScheduler_TaskFactoryDifferentDevice_RecordsFactoryError()
        {
            var dispatcher = NewDispatcher(queueCapacity: 10);
            var scheduler = NewScheduler(dispatcher);
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var delayed = new DelayedDeviceTask(
                1,
                DeviceTaskType.HealthCheck,
                DeviceTaskPriority.Low,
                now,
                "factory-device-mismatch:1",
                "advanced-test",
                () => NewTask(2, DeviceTaskType.HealthCheck),
                now.AddMinutes(-1));

            try
            {
                scheduler.Schedule(delayed);
                var results = scheduler.DispatchDueTasks(now);
                var snapshot = scheduler.GetSnapshot(now);

                Assert.Equal(1, results.Count);
                Assert.Equal(DelayedTaskDispatchStatus.FactoryError, results[0].Status);
                Assert.Equal("INTERNAL_ERROR", results[0].Code);
                Assert.Contains("different device", results[0].Message);
                Assert.Equal(1L, snapshot.DispatchFailureCount);
                Assert.Equal(0L, snapshot.DispatchSuccessCount);
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
        }

        [TestCase]
        public static void DelayedDeviceTaskScheduler_TaskFactoryDifferentType_RecordsFactoryError()
        {
            var dispatcher = NewDispatcher(queueCapacity: 10);
            var scheduler = NewScheduler(dispatcher);
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var delayed = new DelayedDeviceTask(
                1,
                DeviceTaskType.HealthCheck,
                DeviceTaskPriority.Low,
                now,
                "factory-type-mismatch:1",
                "advanced-test",
                () => NewTask(1, DeviceTaskType.SyncPerson),
                now.AddMinutes(-1));

            try
            {
                scheduler.Schedule(delayed);
                var results = scheduler.DispatchDueTasks(now);
                var snapshot = scheduler.GetSnapshot(now);

                Assert.Equal(1, results.Count);
                Assert.Equal(DelayedTaskDispatchStatus.FactoryError, results[0].Status);
                Assert.Equal("INTERNAL_ERROR", results[0].Code);
                Assert.Contains("different task type", results[0].Message);
                Assert.Equal(1L, snapshot.DispatchFailureCount);
                Assert.Equal(0L, snapshot.DispatchSuccessCount);
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
        }

        [TestCase]
        public static void DelayedDeviceTaskScheduler_MaxDelayedTaskCountRejectsOverflowAndKeepsSnapshotCoherent()
        {
            var dispatcher = NewDispatcher(queueCapacity: 10);
            var scheduler = NewScheduler(dispatcher, maxDelayedTaskCount: 2);
            var now = new DateTime(2026, 1, 1, 8, 0, 0);

            try
            {
                var first = scheduler.Schedule(NewDelayedTask(now.AddMinutes(1), "cap:1", DeviceTaskType.HealthCheck, DeviceTaskPriority.Low, 1));
                var second = scheduler.Schedule(NewDelayedTask(now.AddMinutes(2), "cap:2", DeviceTaskType.SyncPerson, DeviceTaskPriority.Normal, 2));
                var overflow = scheduler.Schedule(NewDelayedTask(now.AddMinutes(3), "cap:3", DeviceTaskType.UploadFace, DeviceTaskPriority.High, 3));
                var snapshot = scheduler.GetSnapshot(now);

                Assert.True(first.Accepted);
                Assert.True(second.Accepted);
                Assert.False(overflow.Accepted);
                Assert.Equal("DELAYED_QUEUE_FULL", overflow.Code);
                Assert.Equal(2, snapshot.DelayedTaskCount);
                Assert.Equal(1, snapshot.GetPriorityCount(DeviceTaskPriority.Low));
                Assert.Equal(1, snapshot.GetPriorityCount(DeviceTaskPriority.Normal));
                Assert.Equal(0, snapshot.GetPriorityCount(DeviceTaskPriority.High));
                Assert.Equal(1L, snapshot.RejectedDelayedTaskCount);
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
        }

        [TestCase]
        public static void DelayedDeviceTaskScheduler_StopThenSchedule_ReturnsStoppedAndDoesNotDispatch()
        {
            var dispatcher = NewDispatcher(queueCapacity: 10);
            var scheduler = NewScheduler(dispatcher);
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var factoryInvoked = false;

            try
            {
                scheduler.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
                var schedule = scheduler.Schedule(NewDelayedTask(now, "stopped:1", DeviceTaskType.HealthCheck, DeviceTaskPriority.Low, 0, task =>
                {
                    factoryInvoked = true;
                }));
                var results = scheduler.DispatchDueTasks(now);
                var snapshot = scheduler.GetSnapshot(now);

                Assert.False(schedule.Accepted);
                Assert.Equal(DelayedTaskScheduleStatus.Stopped, schedule.Status);
                Assert.Equal("SCHEDULER_STOPPED", schedule.Code);
                Assert.Equal(0, results.Count);
                Assert.False(factoryInvoked);
                Assert.Equal(0, snapshot.DelayedTaskCount);
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
        }

        private static DelayedDeviceTaskScheduler NewScheduler(
            DeviceSdkDispatcher dispatcher,
            int maxDelayedTaskCount = 100,
            int dispatchBatchSize = 100)
        {
            return new DelayedDeviceTaskScheduler(dispatcher, new DelayedDeviceTaskSchedulerOptions
            {
                MaxDelayedTaskCount = maxDelayedTaskCount,
                DispatchBatchSize = dispatchBatchSize,
                WakeupMaxSleepMilliseconds = 10,
                CoalesceByTaskKey = true
            });
        }

        private static DeviceSdkDispatcher NewDispatcher(int queueCapacity)
        {
            var registry = new DeviceRuntimeRegistry(new DeviceRuntimeRegistryOptions { WorkerCount = 3 });
            Register(registry, 1);
            Register(registry, 2);
            Register(registry, 3);
            return new DeviceSdkDispatcher(registry, new DeviceSdkDispatcherOptions
            {
                WorkerCount = 3,
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
                IpAddress = "10.2.8." + deviceId,
                Port = 8000,
                Username = "admin",
                Password = "pwd",
                Enabled = true,
                CreatedAt = DateTime.Now
            });
            Assert.True(result.Success, result.Message);
        }

        private static DelayedDeviceTask NewDelayedTask(
            DateTime dueAt,
            string taskKey,
            DeviceTaskType type,
            DeviceTaskPriority priority,
            int order,
            Action<DeviceSdkTask> onCreated = null)
        {
            return new DelayedDeviceTask(1, type, priority, dueAt, taskKey, "advanced-test", () =>
            {
                var task = NewTask(1, type);
                onCreated?.Invoke(task);
                return task;
            }, dueAt.AddMinutes(-1).AddTicks(order));
        }

        private static DeviceSdkTask NewTask(int deviceId, DeviceTaskType type)
        {
            return new DeviceSdkTask(deviceId, type, type.ToString(), context => Task.FromResult(Success(context.Task)));
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
    }
}
