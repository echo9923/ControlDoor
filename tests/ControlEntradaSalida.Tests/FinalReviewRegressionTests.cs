using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Configuration;
using ControlDoor.Devices.Management;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Tasks;
using ControlDoor.FaceEvents;
using ControlDoor.GrpcApi;
using ControlDoor.Hikvision;
using ControlDoor.Permissions;
using ControlDoor.Runtime;

namespace ControlEntradaSalida.Tests
{
    public static class FinalReviewRegressionTests
    {
        private static void Prepare(Stage4Fixture fixture, bool online)
        {
            fixture.Options.AlarmEnabled = false;
            fixture.AddRecord();
            fixture.Lifecycle.LoadEnabledDevices(false);
            if (online) Assert.True(fixture.Lifecycle.SubmitLogin(1, true, "review").Success);
        }

        private static DeviceSdkTask Barrier(DeviceTaskPriority priority = DeviceTaskPriority.Retry)
        {
            return new DeviceSdkTask(1, DeviceTaskType.HealthCheck, "Barrier", context =>
                Task.FromResult(DeviceTaskResult.FromTask(context.Task, true, "OK", "done", DeviceConnectionStatus.Online, DateTime.Now, DateTime.Now)))
            { Priority = priority, TimeoutMilliseconds = 5000 };
        }

        private static void Drain(Stage4Fixture fixture)
        {
            Assert.True(fixture.Dispatcher.SubmitAndWaitAsync(Barrier()).GetAwaiter().GetResult().Success);
        }

        private static TaskCompletionSource<bool> Block(Stage4Fixture fixture)
        {
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new ManualResetEventSlim();
            var blocker = new DeviceSdkTask(1, DeviceTaskType.HealthCheck, "Blocker", async context =>
            {
                entered.Set();
                await release.Task.ConfigureAwait(false);
                return DeviceTaskResult.FromTask(context.Task, true, "OK", "done", DeviceConnectionStatus.Online, DateTime.Now, DateTime.Now);
            }) { TimeoutMilliseconds = 5000 };
            fixture.Dispatcher.Submit(blocker);
            Assert.True(entered.Wait(2000));
            return release;
        }

        [TestCase]
        public static void Retry_PersonPending_PreservesLatestPermissionAndName()
        {
            using (var fixture = new Stage4Fixture())
            {
                Prepare(fixture, false);
                var store = new InMemoryRetryExecutionStore();
                var service = new PermissionSyncGrpcService(fixture.Registry, fixture.Dispatcher, fixture.Gateway, store);
                var queued = service.SyncPersonsToDevices("{\"deviceIds\":[1],\"people\":[{\"employeeId\":\"E1\",\"name\":\"OldName\",\"enabled\":true}]}");
                Assert.True(store.Snapshot.PersonPending, queued);
                Assert.True(fixture.Lifecycle.SubmitLogin(1, true, "reconnect").Success);
                var revoked = service.SyncPermissions("{\"items\":[{\"employee_id\":\"E1\",\"name\":\"CurrentName\",\"permission_code\":0}]}");
                var pending = store.Snapshot;
                Assert.NotNull(pending, revoked);
                Assert.True(pending.PersonPending);
                Assert.False(pending.PermissionPending);
                Assert.True(store.IsCurrent(pending));
                var execution = new RetryExecutionCoordinator(fixture.Dispatcher, fixture.Gateway, isCurrent: store.IsCurrent)
                    .ExecuteAsync(new RetryCommandPlanner().Plan(pending), "retry-old-person", CancellationToken.None).GetAwaiter().GetResult();
                Assert.True(execution.AllSucceeded, execution.Code);
                var requests = fixture.Gateway.Calls.Where(x => x.MethodName == "UpsertPersonAsync")
                    .Select(x => (UpsertPersonRequest)x.Request).ToArray();
                Assert.Equal(2, requests.Length);
                Assert.False(requests[0].Person.Enabled);
                Assert.False(requests[1].Person.Enabled);
                Assert.Equal("CurrentName", requests[1].Person.Name);
            }
        }

        [TestCase]
        public static void Retry_UserStatusFailure_DoesNotDeletePendingState()
        {
            var database = new RecordingDatabaseClient
            {
                RowsAffected = 1,
                FailOperationName = "SystemUserSyncStatus.MarkPermissionSynced",
                FailCanRetry = true
            };
            var store = new DeviceOperationRetryStore(database, userSyncWriter: new SystemUserSyncStatusWriter(database));
            var state = new DeviceOperationRetryState
            {
                Id = 1, DeviceId = 1, EmployeeId = "E1", IntentVersion = Guid.NewGuid(), PermissionPending = true, PermissionLevel = 2
            };
            var threw = false;
            try { store.ApplyExecutionResult(new RetryExecutionResult(state, new[] { RetryOperation.Permission }, null, false, "OK", "ok", null), DateTime.Now); }
            catch (InvalidOperationException) { threw = true; }
            Assert.True(threw);
            Assert.True(database.Commands.Any(x => x.OperationName == database.FailOperationName && x.Error != null));
            Assert.False(database.Commands.Any(x => x.OperationName == "DeviceOperationRetryStore.DeleteIfCompleted"));
        }

        [TestCase]
        public static void Lifecycle_Delete_RejectsQueuedLogin()
        {
            using (var fixture = new Stage4Fixture())
            {
                Prepare(fixture, false);
                var release = Block(fixture);
                var repository = new DeleteBarrierRepository(fixture.Repository, () =>
                    Assert.True(fixture.Registry.TryGetByDeviceId(1).Snapshot.IsDeleting));
                using (var lifecycle = new DeviceLifecycleService(fixture.Registry, fixture.Dispatcher, fixture.DelayedScheduler, repository, fixture.Gateway, fixture.Options))
                {
                    fixture.Lifecycle.SubmitLogin(1, false, "queued-login");
                    var deletion = Task.Run(() => lifecycle.DeleteDevice(1, true, "delete"));
                    try
                    {
                        Assert.True(SpinWait.SpinUntil(() => fixture.Dispatcher.GetWorkerSnapshots().Any(x => x.QueueLength >= 2), 2000));
                    }
                    finally { release.TrySetResult(true); }
                    Assert.True(deletion.GetAwaiter().GetResult().Success);
                    Assert.False(fixture.Registry.TryGetByDeviceId(1).Found);
                    Assert.Equal(0, fixture.Gateway.Calls.Count(x => x.MethodName == "LoginAsync"));
                    Assert.Equal(0, fixture.Gateway.Calls.Count(x => x.MethodName == "LogoutAsync"));
                }
            }
        }

        [TestCase]
        public static void Lifecycle_ManualDisarm_SupersedesQueuedArm()
        {
            using (var fixture = new Stage4Fixture())
            {
                Prepare(fixture, true);
                var release = Block(fixture);
                Task<DeviceOperationResult> disarm = null;
                try
                {
                    var factory = typeof(DeviceLifecycleService).GetMethod("CreateArmAlarmTask", BindingFlags.Instance | BindingFlags.NonPublic);
                    var arm = (DeviceSdkTask)factory.Invoke(fixture.Lifecycle, new object[] { 1, "queued-arm" });
                    Assert.True(fixture.Dispatcher.Submit(arm).Accepted);
                    disarm = Task.Run(() => fixture.Lifecycle.DisarmDeviceAlarm(1, "manual-disarm"));
                    Assert.True(SpinWait.SpinUntil(() => fixture.Dispatcher.GetWorkerSnapshots().Any(x => x.QueueLength >= 2), 2000));
                }
                finally { release.TrySetResult(true); }
                Assert.True(disarm.GetAwaiter().GetResult().Success);
                Drain(fixture);
                var state = fixture.Registry.TryGetByDeviceId(1).Snapshot;
                Assert.False(state.AlarmHandle.HasValue);
                Assert.True(state.AlarmManuallyDisarmed);
                Assert.Equal(0, fixture.Gateway.Calls.Count(x => x.MethodName == "SetAlarmAsync"));
            }
        }

        [TestCase]
        public static void FaceEvents_TransientFailure_RetriesAcceptedEvent()
        {
            var processor = new RecoveringProcessor();
            var context = new BackgroundTaskContext("review", CancellationToken.None, null);
            using (var service = new FaceEventIngestionService(new FaceEventLoggingOptions { BatchSize = 1, FlushIntervalMs = 10 }, processor))
            {
                service.StartAsync(context).GetAwaiter().GetResult();
                Assert.True(service.TryEnqueue(new RawAcsAlarmEvent { DeviceId = 1 }).Accepted);
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref processor.Attempts) > 0, 2000));
                Thread.Sleep(200);
                service.StopAsync(context).GetAwaiter().GetResult();
                Assert.Equal(2, processor.Attempts);
                Assert.Equal(1, processor.Stored);
                Assert.Equal(0, service.Count);
            }
        }

        [TestCase]
        public static void Retry_LateSuccess_IsAcknowledgedAfterSdkCompletes()
        {
            using (var fixture = new Stage4Fixture(defaultTaskTimeoutMilliseconds: 100))
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                Prepare(fixture, true);
                var finished = false;
                fixture.Gateway.ConfigureResult("UpsertPersonAsync", request =>
                {
                    entered.Set();
                    Assert.True(release.Wait(3000));
                    finished = true;
                    return 0;
                });
                var state = new DeviceOperationRetryState
                {
                    Id = 1, DeviceId = 1, EmployeeId = "E1", IntentVersion = Guid.NewGuid(), PersonPending = true,
                    PersonPayloadJson = "{\"employeeId\":\"E1\",\"name\":\"Slow\"}", AttemptCount = 1
                };
                var database = new RecordingDatabaseClient { RowsAffected = 1 };
                var store = new DeviceOperationRetryStore(database, new DeviceOperationRetryOptions { MaxRetryAttempts = 2 });
                var execution = new RetryExecutionCoordinator(fixture.Dispatcher, fixture.Gateway)
                    .ExecuteAsync(new RetryCommandPlanner().Plan(state), "slow-retry", CancellationToken.None);
                try
                {
                    Assert.True(entered.Wait(2000));
                    Assert.False(execution.Wait(250), "Retry must observe actual SDK completion.");
                    Assert.False(finished);
                }
                finally { release.Set(); }
                var result = execution.GetAwaiter().GetResult();
                Assert.True(result.AllSucceeded, result.Code);
                store.ApplyExecutionResult(result, DateTime.Now);
                Drain(fixture);
                Assert.True(finished);
                Assert.True(database.Commands.Any(x => x.OperationName == "DeviceOperationRetryStore.MarkOperationSuccess"));
                Assert.False(database.Commands.Any(x => x.OperationName == "DeviceOperationRetryStore.MarkTerminalFailure"));
            }
        }

        [TestCase]
        public static void Enrollment_CompletedRecords_AreBounded()
        {
            var store = new EnrollmentTaskStore();
            for (var i = 0; i < 10000; i++)
            {
                var id = Guid.NewGuid().ToString("N");
                store.Start(id, "E1");
                store.Succeed(id, "done");
            }
            Assert.Equal(1000, store.GetAll().Count);
        }

        [TestCase]
        public static void EventId_LargeDeviceIds_RemainDistinct()
        {
            var generator = new EventIdGenerator();
            var first = generator.CreateFromSerial(1000000000, 7);
            var second = generator.CreateFromSerial(1000000001, 7);
            Assert.True(first != second);
            Assert.Equal(first, generator.CreateFromSerial(1000000000, 7));
            Assert.Equal(70000000345L, generator.CreateFromSerial(7, 345));
        }

        private sealed class RecoveringProcessor : IAcsFaceEventProcessor
        {
            public int Attempts;
            public int Stored;
            public FaceEventProcessResult Process(RawAcsAlarmEvent item)
            {
                if (Interlocked.Increment(ref Attempts) == 1)
                    return FaceEventProcessResult.Failed("RETRYABLE_FAILURE", "temporary database outage");
                Interlocked.Increment(ref Stored);
                return FaceEventProcessResult.Ok();
            }
            public IReadOnlyList<FaceEventBatchItemResult> ProcessBatch(IReadOnlyList<RawAcsAlarmEvent> items)
            {
                throw new InvalidOperationException("BatchSize=1 expected");
            }
        }

        private sealed class DeleteBarrierRepository : IDeviceRepository
        {
            private readonly IDeviceRepository inner;
            private readonly Action beforeDelete;
            public DeleteBarrierRepository(IDeviceRepository inner, Action beforeDelete)
            {
                this.inner = inner;
                this.beforeDelete = beforeDelete;
            }
            public IReadOnlyList<DeviceRecord> LoadEnabledDevices() { return inner.LoadEnabledDevices(); }
            public IReadOnlyList<DeviceRecord> LoadAllDevices() { return inner.LoadAllDevices(); }
            public DeviceRecord GetByDeviceId(int id) { return inner.GetByDeviceId(id); }
            public bool ExistsDeviceId(int id) { return inner.ExistsDeviceId(id); }
            public bool ExistsIpAddress(string ip) { return inner.ExistsIpAddress(ip); }
            public DeviceStoreWriteResult InsertDevice(DeviceRecord record) { return inner.InsertDevice(record); }
            public DeviceStoreWriteResult DeleteDevice(int id)
            {
                beforeDelete();
                return inner.DeleteDevice(id);
            }
        }
    }
}
