using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ControlDoor.Configuration;
using ControlDoor.Database;
using ControlDoor.FaceEvents;
using ControlDoor.GrpcApi;
using ControlDoor.Permissions;
using ControlDoor.Runtime;

namespace ControlEntradaSalida.Tests
{
    public static class FinalPersistenceRegressionTests
    {
        [TestCase]
        public static void SyncPersons_UserStatusWriteFails_ReturnsDatabaseFailure()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.Options.AlarmEnabled = false;
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(false);
                Assert.True(fixture.Lifecycle.SubmitLogin(1, true, "prepare").Success);
                var database = new RecordingDatabaseClient { FailOperationName = "SystemUserSyncStatus.MarkPersonSynced" };
                var service = new PermissionSyncGrpcService(fixture.Registry, fixture.Dispatcher, fixture.Gateway,
                    userSyncWriter: new SystemUserSyncStatusWriter(database));
                var json = service.SyncPersons("{\"people\":[{\"employeeId\":\"E1\",\"name\":\"Employee\"}]}");
                Assert.Contains("\"code\":\"DB_ERROR\"", json);
                Assert.Contains("\"success\":false", json);
            }
        }

        [TestCase]
        public static void Retry_UserWriteFails_RollsBackPendingAcknowledgement()
        {
            var database = new TransactionalRetryDatabase();
            database.Inner.FailOperationName = "SystemUserSyncStatus.MarkPermissionSynced";
            var store = new DeviceOperationRetryStore(database, userSyncWriter: new SystemUserSyncStatusWriter(database));
            var state = new DeviceOperationRetryState
            {
                Id = 1, DeviceId = 1, EmployeeId = "E1", IntentVersion = Guid.NewGuid(), PermissionPending = true, PermissionLevel = 7
            };
            var failed = false;
            try { store.MarkOperationSuccess(state, RetryOperation.Permission); }
            catch (InvalidOperationException) { failed = true; }
            Assert.True(failed);
            Assert.True(database.Pending);
            Assert.Equal(1, database.Rollbacks);
            database.Inner.FailOperationName = null;
            store.MarkOperationSuccess(state, RetryOperation.Permission);
            Assert.False(database.Pending);
            Assert.Equal(1, database.Commits);
        }

        [TestCase]
        public static void Retry_TerminalPermissionOnAnotherDevice_BlocksGlobalCompletion()
        {
            var database = new RecordingDatabaseClient { RowsAffected = 1 };
            database.QueryRowsByOperation["DeviceOperationRetryStore.HasBlockingPermissionStateForEmployee"] =
                new List<IReadOnlyDictionary<string, object>> { new Dictionary<string, object> { ["id"] = 2L, ["exhausted_at"] = DateTime.Now } };
            var writer = new RecordingUserSyncStatusWriter();
            var store = new DeviceOperationRetryStore(database, userSyncWriter: writer);
            store.MarkOperationSuccess(new DeviceOperationRetryState
            {
                Id = 1, DeviceId = 1, EmployeeId = "E1", IntentVersion = Guid.NewGuid(), PermissionLevel = 7
            }, RetryOperation.Permission);
            Assert.False(writer.PermissionLevels.ContainsKey("E1"));
            var sql = database.Commands.Single(x => x.OperationName == "DeviceOperationRetryStore.HasBlockingPermissionStateForEmployee").CommandText;
            Assert.False(sql.Contains("exhausted_at IS NULL"));
        }

        [TestCase]
        public static void Retry_NewPerson_SupersedesOlderPermissionFields()
        {
            var merger = new RetryStateMerger();
            var old = merger.Merge(null, new DeviceOperationRetryIntent
            {
                DeviceId = 1, EmployeeId = "E1", Operation = "SyncPermission", PermissionLevel = 0,
                PayloadJson = "{\"name\":\"Old\",\"permission_code\":0}"
            }, DateTime.Now, true).State;
            var current = merger.Merge(old, new DeviceOperationRetryIntent
            {
                DeviceId = 1, EmployeeId = "E1", Operation = "SyncPerson", PayloadJson = "{\"name\":\"New\",\"enabled\":true}"
            }, DateTime.Now, true).State;
            Assert.False(current.PermissionPending);
            Assert.False(current.PermissionSyncCompletionBlocked);
            var person = new RetryPayloadParser().ParseEffectivePerson(current, "");
            Assert.Equal("New", person.Name);
            Assert.True(person.Enabled);
        }

        [TestCase]
        public static void Enrollment_Retention_ExpiresCompletedAndPreservesRunning()
        {
            var now = new DateTime(2026, 9, 7);
            var store = new EnrollmentTaskStore(2, TimeSpan.FromHours(1), () => now);
            store.Start("running", "E1");
            store.Start("done", "E2");
            store.Succeed("done", "ok");
            now = now.AddHours(2);
            Assert.NotNull(store.GetByTaskId("running"));
            Assert.Equal(null, store.GetByTaskId("done"));
            Assert.Equal(null, store.GetLatestByEmployeeId("E2"));
            Assert.Equal(1, store.GetAll().Count);
        }

        [TestCase]
        public static void FaceEvents_StopAndRestart_ReplaysPayloadAndSnapshot()
        {
            var temp = TestWorkspace.Create();
            {
                var context = new BackgroundTaskContext("spool", CancellationToken.None, null);
                var raw = new RawAcsAlarmEvent
                {
                    DeviceId = 7, DeviceSerialNo = "SERIAL", ReceivedAt = new DateTime(2026, 9, 7, 10, 20, 30),
                    AlarmInfoBytes = new byte[] { 1, 2, 3 }, PictureBytes = new byte[] { 255, 216, 255, 217 }
                };
                raw.Values["employeeId"] = "0007";
                raw.Values["dwSerialNo"] = "345";
                using (var service = new FaceEventIngestionService(new FaceEventLoggingOptions(), retryDirectory: temp))
                {
                    Assert.True(service.TryEnqueue(raw).Accepted);
                    service.StopAsync(context).GetAwaiter().GetResult();
                }
                Assert.Equal(1, Directory.GetFiles(temp, "*.json").Length);
                RawAcsAlarmEvent restored = null;
                var processor = new DelegateProcessor(item => { restored = item; return FaceEventProcessResult.Ok(); });
                using (var service = new FaceEventIngestionService(new FaceEventLoggingOptions { BatchSize = 1, FlushIntervalMs = 10 }, processor, retryDirectory: temp))
                {
                    service.StartAsync(context).GetAwaiter().GetResult();
                    Assert.True(SpinWait.SpinUntil(() => restored != null, 2000));
                    service.StopAsync(context).GetAwaiter().GetResult();
                    Assert.Equal("0007", restored.Values["employeeId"]);
                    Assert.Equal(raw.ReceivedAt, restored.ReceivedAt);
                    Assert.True(raw.PictureBytes.SequenceEqual(restored.PictureBytes));
                    Assert.True(raw.AlarmInfoBytes.SequenceEqual(restored.AlarmInfoBytes));
                    Assert.Equal(70000000345L, new AcsEventParser().Parse(restored).Event.EventId);
                    Assert.Equal(0, Directory.GetFiles(temp, "*.json").Length);
                }
            }
        }

        [TestCase]
        public static void FaceEvents_PartialBatch_RetriesOnlyFailedItems()
        {
            var attempts = new Dictionary<int, int>();
            var processor = new DelegateProcessor(item =>
            {
                attempts.TryGetValue(item.DeviceId, out var previous);
                attempts[item.DeviceId] = previous + 1;
                return item.DeviceId == 2 && previous == 0
                    ? FaceEventProcessResult.Failed("RETRYABLE_FAILURE", "transient") : FaceEventProcessResult.Ok();
            });
            var context = new BackgroundTaskContext("partial", CancellationToken.None, null);
            using (var service = new FaceEventIngestionService(new FaceEventLoggingOptions { BatchSize = 2, FlushIntervalMs = 10 }, processor))
            {
                service.TryEnqueue(new RawAcsAlarmEvent { DeviceId = 1 });
                service.TryEnqueue(new RawAcsAlarmEvent { DeviceId = 2 });
                service.StartAsync(context).GetAwaiter().GetResult();
                service.StopAsync(context).GetAwaiter().GetResult();
                Assert.Equal(1, attempts[1]);
                Assert.Equal(2, attempts[2]);
            }
        }

        private sealed class DelegateProcessor : IAcsFaceEventProcessor
        {
            private readonly Func<RawAcsAlarmEvent, FaceEventProcessResult> process;
            public DelegateProcessor(Func<RawAcsAlarmEvent, FaceEventProcessResult> process) { this.process = process; }
            public FaceEventProcessResult Process(RawAcsAlarmEvent item) { return process(item); }
            public IReadOnlyList<FaceEventBatchItemResult> ProcessBatch(IReadOnlyList<RawAcsAlarmEvent> items)
            {
                return items.Select(item =>
                {
                    var result = process(item);
                    return result.Success ? FaceEventBatchItemResult.Ok(1, result.Code, result.Message) : FaceEventBatchItemResult.Failed(1, result.Code, result.Message);
                }).ToArray();
            }
        }

        private sealed class TransactionalRetryDatabase : ITransactionalDatabaseClient
        {
            public readonly RecordingDatabaseClient Inner = new RecordingDatabaseClient { RowsAffected = 1 };
            public bool Pending = true;
            public int Commits;
            public int Rollbacks;
            public void ExecuteTransaction(Action action)
            {
                var saved = Pending;
                try { action(); Commits++; }
                catch { Pending = saved; Rollbacks++; throw; }
            }
            public DatabaseCommandRecord ExecuteScalar(string name, string sql) { return Inner.ExecuteScalar(name, sql); }
            public DatabaseCommandRecord ExecuteNonQuery(string name, string sql) { return Inner.ExecuteNonQuery(name, sql); }
            public DatabaseCommandRecord ExecuteNonQuery(string name, string sql, params DatabaseParameter[] parameters)
            {
                if (name == "DeviceOperationRetryStore.MarkOperationSuccess") Pending = false;
                return Inner.ExecuteNonQuery(name, sql, parameters);
            }
            public IReadOnlyList<IReadOnlyDictionary<string, object>> ExecuteQuery(string name, string sql, params DatabaseParameter[] parameters) { return Inner.ExecuteQuery(name, sql, parameters); }
            public void Dispose() { }
        }
    }
}
