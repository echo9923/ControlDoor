using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Tasks;
using ControlDoor.Devices.Workers;

namespace ControlEntradaSalida.Tests
{
    public static class Stage2Task08DelayedSchedulerTests
    {
        [TestCase]
        public static void DelayedDeviceTaskScheduler_NotDueTask_DoesNotEnterWorkerQueue()
        {
            var dispatcher = NewDispatcher(queueCapacity: 10);
            var scheduler = NewScheduler(dispatcher);
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var delayed = NewDelayedTask(now.AddMinutes(1), "health:1", DeviceTaskType.HealthCheck, DeviceTaskPriority.Low);

            try
            {
                var schedule = scheduler.Schedule(delayed);
                var results = scheduler.DispatchDueTasks(now);
                var worker = dispatcher.GetWorkerSnapshots()[0];
                var snapshot = scheduler.GetSnapshot(now);

                Assert.True(schedule.Accepted);
                Assert.Equal(0, results.Count);
                Assert.Equal(0, worker.QueueLength);
                Assert.Equal(1, snapshot.DelayedTaskCount);
                Assert.Equal(0, snapshot.DueTaskCount);
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
        }

        [TestCase]
        public static void DelayedDeviceTaskScheduler_DueTask_DispatchesThroughFixedDispatcher()
        {
            var dispatcher = NewDispatcher(queueCapacity: 10);
            var scheduler = NewScheduler(dispatcher);
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            DeviceSdkTask createdTask = null;
            var delayed = NewDelayedTask(now, "sync:1", DeviceTaskType.SyncPerson, DeviceTaskPriority.High, task =>
            {
                createdTask = task;
            });

            try
            {
                scheduler.Schedule(delayed);
                var results = scheduler.DispatchDueTasks(now);
                var final = WaitForCompletion(createdTask);
                var snapshot = scheduler.GetSnapshot(now);

                Assert.Equal(1, results.Count);
                Assert.Equal(DelayedTaskDispatchStatus.Dispatched, results[0].Status);
                Assert.Equal(0, results[0].WorkerIndex.Value);
                Assert.True(final.Success);
                Assert.Equal(DeviceTaskPriority.High, createdTask.Priority);
                Assert.Equal(1L, snapshot.DispatchSuccessCount);
                Assert.Equal(0, snapshot.DelayedTaskCount);
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
        }

        [TestCase]
        public static void DelayedDeviceTaskScheduler_CancelByTaskKey_PreventsDispatch()
        {
            var dispatcher = NewDispatcher(queueCapacity: 10);
            var scheduler = NewScheduler(dispatcher);
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var invoked = false;
            var delayed = NewDelayedTask(now.AddSeconds(1), "retry:1", DeviceTaskType.RetryDeviceOperation, DeviceTaskPriority.Retry, task =>
            {
                invoked = true;
            });

            try
            {
                scheduler.Schedule(delayed);
                var cancelled = scheduler.CancelByTaskKey("retry:1", "state changed");
                var results = scheduler.DispatchDueTasks(now.AddSeconds(2));
                var snapshot = scheduler.GetSnapshot(now.AddSeconds(2));

                Assert.True(cancelled);
                Assert.False(invoked);
                Assert.Equal(0, results.Count);
                Assert.Equal(0, snapshot.DelayedTaskCount);
                Assert.Equal(1L, snapshot.CancelledDelayedTaskCount);
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
        }

        [TestCase]
        public static void DelayedDeviceTaskScheduler_NewEarlierTask_WakesBackgroundLoop()
        {
            var dispatcher = NewDispatcher(queueCapacity: 10);
            var scheduler = new DelayedDeviceTaskScheduler(dispatcher, new DelayedDeviceTaskSchedulerOptions
            {
                MaxDelayedTaskCount = 100,
                DispatchBatchSize = 100,
                WakeupMaxSleepMilliseconds = 5000
            });
            var earlyInvoked = false;
            var late = NewDelayedTask(DateTime.Now.AddSeconds(5), "wake:late", DeviceTaskType.HealthCheck, DeviceTaskPriority.Low);
            var early = NewDelayedTask(DateTime.Now.AddMilliseconds(50), "wake:early", DeviceTaskType.HealthCheck, DeviceTaskPriority.Low, task =>
            {
                earlyInvoked = true;
            });

            try
            {
                scheduler.Start();
                scheduler.Schedule(late);
                scheduler.Schedule(early);
                WaitUntil(() => earlyInvoked, "earlier delayed task was not dispatched after wakeup.");

                var snapshot = scheduler.GetSnapshot();
                Assert.Equal(1L, snapshot.DispatchSuccessCount);
                Assert.True(snapshot.DelayedTaskCount <= 1);
            }
            finally
            {
                scheduler.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
        }

        [TestCase]
        public static void DelayedDeviceTaskScheduler_SameTaskKey_CoalescesAndKeepsEarlierDueAt()
        {
            var dispatcher = NewDispatcher(queueCapacity: 10);
            var scheduler = NewScheduler(dispatcher);
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var laterInvoked = false;
            var earlierInvoked = false;
            var later = NewDelayedTask(now.AddMinutes(5), "health:1", DeviceTaskType.HealthCheck, DeviceTaskPriority.Low, task =>
            {
                laterInvoked = true;
            });
            var earlier = NewDelayedTask(now.AddMinutes(1), "health:1", DeviceTaskType.HealthCheck, DeviceTaskPriority.Low, task =>
            {
                earlierInvoked = true;
            });

            try
            {
                scheduler.Schedule(later);
                var coalesced = scheduler.Schedule(earlier);
                var snapshot = scheduler.GetSnapshot(now);
                var results = scheduler.DispatchDueTasks(now.AddMinutes(1));

                Assert.True(coalesced.Coalesced);
                Assert.Equal(1, snapshot.DelayedTaskCount);
                Assert.Equal(now.AddMinutes(1), snapshot.EarliestDueAt.Value);
                Assert.Equal(1L, snapshot.CoalescedDelayedTaskCount);
                Assert.Equal(1, results.Count);
                Assert.True(earlierInvoked);
                Assert.False(laterInvoked);
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
        }

        [TestCase]
        public static void DelayedDeviceTaskScheduler_DuplicateKeyWithoutMerge_IsRejected()
        {
            var dispatcher = NewDispatcher(queueCapacity: 10);
            var scheduler = NewScheduler(dispatcher, coalesceByTaskKey: false);
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var first = NewDelayedTask(now.AddMinutes(1), "health:duplicate", DeviceTaskType.HealthCheck, DeviceTaskPriority.Low);
            var duplicate = NewDelayedTask(now.AddMinutes(2), "health:duplicate", DeviceTaskType.HealthCheck, DeviceTaskPriority.Low);

            try
            {
                var firstResult = scheduler.Schedule(first);
                var duplicateResult = scheduler.Schedule(duplicate);
                var snapshot = scheduler.GetSnapshot(now);

                Assert.True(firstResult.Accepted);
                Assert.False(duplicateResult.Accepted);
                Assert.Equal("DUPLICATE_DELAYED_TASK_KEY", duplicateResult.Code);
                Assert.Equal(1, snapshot.DelayedTaskCount);
                Assert.Equal(1L, snapshot.RejectedDelayedTaskCount);
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
        }

        [TestCase]
        public static void DelayedDeviceTaskScheduler_ReplaceMerge_ReplacesExistingTask()
        {
            var dispatcher = NewDispatcher(queueCapacity: 10);
            var scheduler = NewScheduler(dispatcher);
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var firstInvoked = false;
            var replacementInvoked = false;
            var first = NewDelayedTask(now.AddMinutes(1), "replace:1", DeviceTaskType.HealthCheck, DeviceTaskPriority.Low, task =>
            {
                firstInvoked = true;
            });
            var replacement = NewDelayedTask(now.AddMinutes(2), "replace:1", DeviceTaskType.HealthCheck, DeviceTaskPriority.Low, task =>
            {
                replacementInvoked = true;
            });
            replacement.MergeMode = DelayedTaskMergeMode.Replace;

            try
            {
                scheduler.Schedule(first);
                var result = scheduler.Schedule(replacement);
                var dispatch = scheduler.DispatchDueTasks(now.AddMinutes(2));

                Assert.True(result.Coalesced);
                Assert.Equal(1, dispatch.Count);
                Assert.False(firstInvoked);
                Assert.True(replacementInvoked);
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
        }

        [TestCase]
        public static void DelayedDeviceTaskScheduler_QueueFull_RecordsDispatchFailure()
        {
            var dispatcher = NewDispatcher(queueCapacity: 1);
            var scheduler = NewScheduler(dispatcher);
            var release = NewSignal();
            var started = NewSignal();
            var running = NewAsyncTask(DeviceTaskType.HealthCheck, async context =>
            {
                started.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return Success(context.Task);
            });
            var filler = NewAsyncTask(DeviceTaskType.SyncPerson, context => Task.FromResult(Success(context.Task)));
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var delayed = NewDelayedTask(now, "face:queue-full", DeviceTaskType.UploadFace, DeviceTaskPriority.Normal);

            try
            {
                dispatcher.Submit(running);
                WaitForSignal(started, "running task did not start.");
                dispatcher.Submit(filler);
                scheduler.Schedule(delayed);
                var results = scheduler.DispatchDueTasks(now);
                var snapshot = scheduler.GetSnapshot(now);

                Assert.Equal(1, results.Count);
                Assert.Equal(DelayedTaskDispatchStatus.QueueFull, results[0].Status);
                Assert.Equal("QUEUE_FULL", results[0].Code);
                Assert.Equal(1L, snapshot.DispatchFailureCount);
            }
            finally
            {
                release.TrySetResult(true);
                WaitForCompletion(running);
                WaitForCompletion(filler);
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
        }

        [TestCase]
        public static void DelayedDeviceTaskScheduler_Stop_PreventsFurtherDispatch()
        {
            var dispatcher = NewDispatcher(queueCapacity: 10);
            var scheduler = NewScheduler(dispatcher);
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var invoked = false;
            var delayed = NewDelayedTask(now, "stop:1", DeviceTaskType.HealthCheck, DeviceTaskPriority.Low, task =>
            {
                invoked = true;
            });

            try
            {
                scheduler.Schedule(delayed);
                scheduler.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
                var results = scheduler.DispatchDueTasks(now);
                var snapshot = scheduler.GetSnapshot(now);

                Assert.Equal(0, results.Count);
                Assert.False(invoked);
                Assert.Equal(1, snapshot.DelayedTaskCount);
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
        }

        [TestCase]
        public static void DelayedDeviceTaskScheduler_StopAsync_DisposesStopSource()
        {
            var dispatcher = NewDispatcher(queueCapacity: 10);
            var scheduler = NewScheduler(dispatcher);

            try
            {
                scheduler.Start();
                Assert.NotNull(GetStopSource(scheduler), "Scheduler should create a stop source when it starts.");

                scheduler.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();

                Assert.Equal<CancellationTokenSource>(null, GetStopSource(scheduler));
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
        }

        [TestCase]
        public static void DelayedDeviceTaskScheduler_BatchSize_LimitsDueDispatch()
        {
            var dispatcher = NewDispatcher(queueCapacity: 10);
            var scheduler = NewScheduler(dispatcher, dispatchBatchSize: 2);
            var now = new DateTime(2026, 1, 1, 8, 0, 0);

            try
            {
                scheduler.Schedule(NewDelayedTask(now, "batch:1", DeviceTaskType.HealthCheck, DeviceTaskPriority.Low));
                scheduler.Schedule(NewDelayedTask(now, "batch:2", DeviceTaskType.SyncPerson, DeviceTaskPriority.Normal));
                scheduler.Schedule(NewDelayedTask(now, "batch:3", DeviceTaskType.UploadFace, DeviceTaskPriority.Normal));

                var first = scheduler.DispatchDueTasks(now);
                var afterFirst = scheduler.GetSnapshot(now);
                var second = scheduler.DispatchDueTasks(now);
                var afterSecond = scheduler.GetSnapshot(now);

                Assert.Equal(2, first.Count);
                Assert.Equal(1, afterFirst.DelayedTaskCount);
                Assert.Equal(1, second.Count);
                Assert.Equal(0, afterSecond.DelayedTaskCount);
                Assert.Equal(3L, afterSecond.DispatchSuccessCount);
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
        }

        [TestCase]
        public static void DelayedDeviceTaskScheduler_ExpiredTask_DoesNotDispatchAndRecordsSnapshot()
        {
            var dispatcher = NewDispatcher(queueCapacity: 10);
            var scheduler = NewScheduler(dispatcher);
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var invoked = false;
            var delayed = NewDelayedTask(now, "expired:1", DeviceTaskType.HealthCheck, DeviceTaskPriority.Low, task =>
            {
                invoked = true;
            });
            delayed.ExpiresAt = now.AddMilliseconds(-1);

            try
            {
                scheduler.Schedule(delayed);
                var results = scheduler.DispatchDueTasks(now);
                var snapshot = scheduler.GetSnapshot(now);

                Assert.Equal(1, results.Count);
                Assert.Equal(DelayedTaskDispatchStatus.Expired, results[0].Status);
                Assert.False(invoked);
                Assert.Equal(1L, snapshot.ExpiredDelayedTaskCount);
                Assert.Equal(0, dispatcher.GetWorkerSnapshots()[0].QueueLength);
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
        }

        [TestCase]
        public static void DelayedDeviceTaskScheduler_TaskFactoryException_RecordsDispatchFailure()
        {
            var dispatcher = NewDispatcher(queueCapacity: 10);
            var scheduler = NewScheduler(dispatcher);
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var delayed = new DelayedDeviceTask(
                1,
                DeviceTaskType.HealthCheck,
                DeviceTaskPriority.Low,
                now,
                "factory-error:1",
                "stage2-test",
                () => throw new InvalidOperationException("factory boom"),
                now.AddMinutes(-1));

            try
            {
                scheduler.Schedule(delayed);
                var results = scheduler.DispatchDueTasks(now);
                var snapshot = scheduler.GetSnapshot(now);

                Assert.Equal(1, results.Count);
                Assert.Equal(DelayedTaskDispatchStatus.FactoryError, results[0].Status);
                Assert.Equal("INTERNAL_ERROR", results[0].Code);
                Assert.Contains("factory boom", results[0].Message);
                Assert.Equal(1L, snapshot.DispatchFailureCount);
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
        }

        [TestCase]
        public static void RetryBackoffPolicy_CalculatesFixedAndExponentialDelay()
        {
            var fixedDelay = RetryBackoffPolicy.Fixed(TimeSpan.FromSeconds(5));
            var exponential = RetryBackoffPolicy.Exponential(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));

            Assert.Equal(TimeSpan.FromSeconds(5), fixedDelay.CalculateDelay(3));
            Assert.Equal(TimeSpan.FromSeconds(5), exponential.CalculateDelay(1));
            Assert.Equal(TimeSpan.FromSeconds(10), exponential.CalculateDelay(2));
            Assert.Equal(TimeSpan.FromSeconds(20), exponential.CalculateDelay(3));
            Assert.Equal(TimeSpan.FromSeconds(30), exponential.CalculateDelay(4));
        }

        private static DelayedDeviceTaskScheduler NewScheduler(DeviceSdkDispatcher dispatcher, int dispatchBatchSize = 100, bool coalesceByTaskKey = true)
        {
            return new DelayedDeviceTaskScheduler(dispatcher, new DelayedDeviceTaskSchedulerOptions
            {
                MaxDelayedTaskCount = 100,
                DispatchBatchSize = dispatchBatchSize,
                WakeupMaxSleepMilliseconds = 10,
                CoalesceByTaskKey = coalesceByTaskKey
            });
        }

        private static DeviceSdkDispatcher NewDispatcher(int queueCapacity)
        {
            var registry = new DeviceRuntimeRegistry(new DeviceRuntimeRegistryOptions { WorkerCount = 1 });
            var result = registry.Register(new DeviceRuntimeCreationOptions
            {
                DeviceId = 1,
                DeviceName = "device-1",
                IpAddress = "10.0.8.1",
                Port = 8000,
                Username = "admin",
                Password = "pwd",
                Enabled = true,
                CreatedAt = DateTime.Now
            });
            Assert.True(result.Success, result.Message);

            return new DeviceSdkDispatcher(registry, new DeviceSdkDispatcherOptions
            {
                WorkerCount = 1,
                QueueCapacityPerWorker = queueCapacity,
                DefaultTaskTimeoutMilliseconds = 30000
            });
        }

        private static DelayedDeviceTask NewDelayedTask(DateTime dueAt, string taskKey, DeviceTaskType type, DeviceTaskPriority priority, Action<DeviceSdkTask> onCreated = null)
        {
            return new DelayedDeviceTask(1, type, priority, dueAt, taskKey, "stage2-test", () =>
            {
                var task = NewAsyncTask(type, context => Task.FromResult(Success(context.Task)));
                onCreated?.Invoke(task);
                return task;
            }, dueAt.AddMinutes(-1));
        }

        private static DeviceSdkTask NewAsyncTask(DeviceTaskType type, Func<DeviceTaskContext, Task<DeviceTaskResult>> execute)
        {
            return new DeviceSdkTask(1, type, type.ToString(), execute);
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

        private static void WaitUntil(Func<bool> condition, string message)
        {
            var deadline = DateTime.Now.AddSeconds(2);
            while (DateTime.Now < deadline)
            {
                if (condition())
                {
                    return;
                }

                Task.Delay(10).GetAwaiter().GetResult();
            }

            Assert.True(false, message);
        }

        private static TaskCompletionSource<bool> NewSignal()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static CancellationTokenSource GetStopSource(DelayedDeviceTaskScheduler scheduler)
        {
            var field = typeof(DelayedDeviceTaskScheduler).GetField("stopSource", BindingFlags.Instance | BindingFlags.NonPublic);
            return (CancellationTokenSource)field.GetValue(scheduler);
        }
    }
}
