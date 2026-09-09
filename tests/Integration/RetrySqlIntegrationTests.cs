using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ControlDoor.Configuration;
using ControlDoor.Database;
using ControlDoor.Permissions;

namespace ControlEntradaSalida.Tests
{
    public static class RetrySqlIntegrationTests
    {
        [TestCase]
        public static void RetrySql_MigrationAndStaleTransitions_PreserveNewIntent()
        {
            if (Environment.GetEnvironmentVariable("CONTROLDOOR_RETRY_SQL_INTEGRATION") != "1")
            {
                Console.WriteLine("[SKIP] Set CONTROLDOOR_RETRY_SQL_INTEGRATION=1 and CONTROLDOOR_STAGE14_CONNECTION_STRING for a disposable Docker database.");
                return;
            }

            var connection = Environment.GetEnvironmentVariable("CONTROLDOOR_STAGE14_CONNECTION_STRING");
            Assert.False(string.IsNullOrWhiteSpace(connection), "A disposable Docker database connection is required.");
            using (var database = new SqlServerDatabase(new DatabaseOptions { ConnectionString = connection }))
            {
                var migration = File.ReadAllText(Path.Combine("database", "专项_20260309_设备操作重试状态表.sql"));
                for (var run = 0; run < 2; run++)
                {
                    foreach (var batch in Regex.Split(migration, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(batch)) continue;
                        Check(database.ExecuteNonQuery("RetrySql.Migrate", batch, new DatabaseParameter[0]));
                    }
                }

                var employeeId = "retry-test-" + Guid.NewGuid().ToString("N");
                var store = new DeviceOperationRetryStore(database);
                try
                {
                    var oldIntent = new DeviceOperationRetryIntent { DeviceId = 1, EmployeeId = employeeId, Operation = "DeletePerson", NextRetryAt = DateTime.Now.AddMinutes(-1) };
                    Assert.True(store.UpsertIntent(oldIntent).Success);
                    var oldState = store.LoadIntent(oldIntent);
                    Assert.NotNull(oldState);
                    var newIntent = new DeviceOperationRetryIntent
                    {
                        DeviceId = 1, EmployeeId = employeeId, Operation = "SyncPerson", PayloadJson = "{\"name\":\"NewName\"}",
                        RelatedFacePayloadJson = "{\"faceBase64\":\"/9gBAv/Z\"}", NextRetryAt = DateTime.Now.AddMinutes(-1)
                    };
                    Assert.True(store.UpsertIntent(newIntent).Success);
                    Assert.False(store.IsCurrent(oldState));
                    Assert.False(store.TryClaimDueState(oldState, DateTime.Now));
                    store.MarkOperationSuccess(oldState, RetryOperation.DeletePerson);
                    store.ScheduleRetry(oldState, "OLD_ERROR", "old failure", DateTime.Now);
                    store.DeferOffline(oldState, "DEVICE_OFFLINE", "old offline", DateTime.Now);
                    store.MarkTerminalFailure(oldState, "OLD_TERMINAL", "old terminal", DateTime.Now);
                    store.DeleteIfCompleted(oldState);
                    store.DeleteEmptyState(oldState);

                    var current = store.LoadIntent(newIntent);
                    Assert.NotNull(current);
                    Assert.True(current.PersonPending);
                    Assert.True(current.FacePending);
                    Assert.Contains("NewName", current.PersonPayloadJson);
                    Assert.False(current.ExhaustedAt.HasValue);
                    Assert.Equal(0, current.AttemptCount);
                    Assert.True(store.TryClaimDueState(current, DateTime.Now));
                    store.MarkOperationSuccess(current, RetryOperation.Person);
                    store.MarkOperationSuccess(current, RetryOperation.Face);
                    store.DeleteIfCompleted(current);
                    Assert.Equal(null, store.LoadIntent(newIntent));

                    var writer = new FailingUserWriter();
                    var completionStore = new DeviceOperationRetryStore(database, userSyncWriter: writer);
                    var person = new DeviceOperationRetryIntent
                    {
                        DeviceId = 1, EmployeeId = employeeId, Operation = "SyncPerson", PayloadJson = "{\"name\":\"Old\",\"enabled\":true}"
                    };
                    Assert.True(completionStore.UpsertIntent(person).Success);
                    var firstPermission = PermissionIntent(1, employeeId);
                    var secondPermission = PermissionIntent(2, employeeId);
                    var prepared = completionStore.PreparePermissionBatch(new[] { firstPermission, secondPermission });
                    Assert.True(prepared[1].PermissionPending);
                    Assert.True(prepared[2].PermissionPending);
                    completionStore.MarkTerminalFailure(prepared[2], "SDK_ERROR", "terminal", DateTime.Now);
                    completionStore.MarkOperationSuccess(prepared[1], RetryOperation.Permission);
                    Assert.Equal(0, writer.Completed);
                    var retained = completionStore.LoadIntent(firstPermission);
                    Assert.True(retained.PersonPending);
                    Assert.False(retained.PermissionPending);
                    Assert.False(new RetryPayloadParser().ParseEffectivePerson(retained, "").Enabled);
                    completionStore.MarkOperationSuccess(retained, RetryOperation.Person);

                    secondPermission = PermissionIntent(2, employeeId);
                    Assert.True(completionStore.UpsertIntent(secondPermission).Success);
                    var second = completionStore.LoadIntent(secondPermission);
                    writer.Fail = true;
                    var threw = false;
                    try { completionStore.MarkOperationSuccess(second, RetryOperation.Permission); }
                    catch (InvalidOperationException) { threw = true; }
                    Assert.True(threw);
                    Assert.True(completionStore.LoadIntent(secondPermission).PermissionPending);
                    writer.Fail = false;
                    completionStore.MarkOperationSuccess(second, RetryOperation.Permission);
                    Assert.False(completionStore.LoadIntent(secondPermission).PermissionPending);
                    Assert.Equal(1, writer.Completed);
                }
                finally
                {
                    Check(database.ExecuteNonQuery("RetrySql.Cleanup",
                        "DELETE FROM dbo.device_operation_retry_states WHERE employee_id = @employeeId;",
                        new DatabaseParameter("@employeeId", employeeId)));
                }
            }
        }

        // 复核 L3：真实 SQL 验证候选查询的按设备分区配额——设备 1 塞入大量更早到期记录后，
        // 设备 2 的单条新记录仍必须在配额候选内出现（模拟适配器无法执行窗口排名，此用例补齐该语义）。
        [TestCase]
        public static void RetrySql_LoadDueSummaries_PerDeviceQuotaKeepsFreshDeviceVisible()
        {
            if (Environment.GetEnvironmentVariable("CONTROLDOOR_RETRY_SQL_INTEGRATION") != "1")
            {
                Console.WriteLine("[SKIP] Set CONTROLDOOR_RETRY_SQL_INTEGRATION=1 and CONTROLDOOR_STAGE14_CONNECTION_STRING for a disposable Docker database.");
                return;
            }

            var connection = Environment.GetEnvironmentVariable("CONTROLDOOR_STAGE14_CONNECTION_STRING");
            Assert.False(string.IsNullOrWhiteSpace(connection), "A disposable Docker database connection is required.");
            using (var database = new SqlServerDatabase(new DatabaseOptions { ConnectionString = connection }))
            {
                var migration = File.ReadAllText(Path.Combine("database", "专项_20260309_设备操作重试状态表.sql"));
                foreach (var batch in Regex.Split(migration, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(batch)) continue;
                    Check(database.ExecuteNonQuery("RetrySql.Migrate", batch, new DatabaseParameter[0]));
                }

                var employeePrefix = "l3-quota-" + Guid.NewGuid().ToString("N");
                var store = new DeviceOperationRetryStore(database);
                try
                {
                    for (var index = 0; index < 20; index++)
                    {
                        var intent = new DeviceOperationRetryIntent
                        {
                            DeviceId = 1, EmployeeId = employeePrefix + "-a" + index, Operation = "SyncPermission", PermissionLevel = 7,
                            PayloadJson = "{\"name\":\"A\",\"permission_code\":7}", NextRetryAt = DateTime.Now.AddHours(-index - 1)
                        };
                        Assert.True(store.UpsertIntent(intent).Success);
                    }

                    var freshIntent = new DeviceOperationRetryIntent
                    {
                        DeviceId = 2, EmployeeId = employeePrefix + "-fresh", Operation = "SyncPermission", PermissionLevel = 7,
                        PayloadJson = "{\"name\":\"B\",\"permission_code\":7}", NextRetryAt = DateTime.Now.AddMinutes(-1)
                    };
                    Assert.True(store.UpsertIntent(freshIntent).Success);

                    var summaries = store.LoadDueSummaries(DateTime.Now, 10, new[] { 1, 2 });

                    // 设备 1 受配额约束（20 条到期只贡献 10 条），设备 2 的单条新记录仍然可见。
                    Assert.Equal(10, summaries.Count(item => item.DeviceId == 1));
                    Assert.True(summaries.Any(item => item.DeviceId == 2 && item.EmployeeId == employeePrefix + "-fresh"),
                        "单台设备的大量更早到期记录不得把其他设备挡在候选窗口外（L3）。");
                }
                finally
                {
                    Check(database.ExecuteNonQuery("RetrySql.Cleanup",
                        "DELETE FROM dbo.device_operation_retry_states WHERE employee_id LIKE @employeePrefix;",
                        new DatabaseParameter("@employeePrefix", employeePrefix + "%")));
                }
            }
        }

        private static void Check(DatabaseCommandRecord record)
        {
            Assert.Equal(null, record.Error, record.Error?.Message);
        }

        private static DeviceOperationRetryIntent PermissionIntent(int deviceId, string employeeId)
        {
            return new DeviceOperationRetryIntent
            {
                DeviceId = deviceId, EmployeeId = employeeId, Operation = "SyncPermission", PermissionLevel = 0,
                PayloadJson = "{\"name\":\"Current\",\"permission_code\":0}"
            };
        }

        private sealed class FailingUserWriter : IUserSyncStatusWriter
        {
            public bool Fail;
            public int Completed;
            public void MarkPermissionSynced(string employeeId, int level)
            {
                if (Fail) throw new InvalidOperationException("forced user status failure");
                Completed++;
            }
            public void MarkPersonSynced(string employeeId) { }
            public void MarkPersonDeleted(string employeeId) { }
        }
    }
}
