using System;
using System.Collections.Generic;
using System.Linq;
using ControlDoor.Configuration;
using ControlDoor.Permissions;

namespace ControlEntradaSalida.Tests
{
    // 代码复核 K3：离线设备补偿记录不得占用全局扫描名额、不得读取人脸载荷；
    // 在线记录应立即被领取；积压时使用短间隔连续扫描；维护巡检清理已移除设备的终态。
    public static class CodeReviewK03RetryScanTests
    {
        [TestCase]
        public static void DeviceOperationRetryManager_LoadDueSummaries_FiltersByOnlineDevicesAndSkipsPayloadColumns()
        {
            var database = new RecordingDatabaseClient();
            var store = new DeviceOperationRetryStore(database, new DeviceOperationRetryOptions { BatchSize = 100 });

            var summaries = store.LoadDueSummaries(new DateTime(2026, 1, 1), 10, new[] { 7, 3, 7 });

            Assert.Equal(0, summaries.Count);
            var command = database.Commands.Single(item => item.OperationName == "DeviceOperationRetryStore.LoadDueSummaries");
            Assert.Contains("device_id IN (@deviceId0, @deviceId1)", command.CommandText);
            Assert.Contains("@deviceId0=3", command.CommandText);
            Assert.Contains("@deviceId1=7", command.CommandText);
            // 复核 L3：配额在数据库侧按设备分区排名施加，先于任何全局截断。
            Assert.Contains("ROW_NUMBER() OVER (PARTITION BY device_id", command.CommandText);
            Assert.Contains("WHERE __device_rank <= @perDeviceQuota", command.CommandText);
            Assert.Contains("@perDeviceQuota=10", command.CommandText);
            Assert.False(command.CommandText.Contains("SELECT TOP"), "候选查询不得在配额前全局截断。");
            // 摘要不读取三个 payload 大列。
            Assert.False(command.CommandText.Contains("face_payload"));
            Assert.False(command.CommandText.Contains("person_payload"));
            Assert.False(command.CommandText.Contains("permission_payload"));
            Assert.Contains("exhausted_at IS NULL", command.CommandText);
        }

        [TestCase]
        public static void DeviceOperationRetryStore_LoadStatesByIds_LoadsFullRowsByPrimaryKey()
        {
            var database = new RecordingDatabaseClient();
            var store = new DeviceOperationRetryStore(database, new DeviceOperationRetryOptions());

            var states = store.LoadStatesByIds(new long[] { 12, 5, 12 });

            Assert.Equal(0, states.Count);
            var command = database.Commands.Single(item => item.OperationName == "DeviceOperationRetryStore.LoadStatesByIds");
            Assert.Contains("WHERE id IN (@id0, @id1)", command.CommandText);
            Assert.Contains("@id0=5", command.CommandText);
            Assert.Contains("@id1=12", command.CommandText);
            Assert.Contains("SELECT *", command.CommandText);
        }

        [TestCase]
        public static void DeviceOperationRetryStore_LoadMaintenanceSummaries_UsesIdCursor()
        {
            var database = new RecordingDatabaseClient();
            var store = new DeviceOperationRetryStore(database, new DeviceOperationRetryOptions());

            var summaries = store.LoadMaintenanceSummaries(new DateTime(2026, 1, 1), 345, 50);

            Assert.Equal(0, summaries.Count);
            var command = database.Commands.Single(item => item.OperationName == "DeviceOperationRetryStore.LoadMaintenanceSummaries");
            Assert.Contains("AND id > @afterId", command.CommandText);
            Assert.Contains("@afterId=345", command.CommandText);
            Assert.Contains("ORDER BY id ASC", command.CommandText);
            Assert.False(command.CommandText.Contains("face_payload"));
        }

        [TestCase]
        public static void DeviceOperationRetryManager_OnlineRecordSelectedImmediately_DespiteOfflineBacklog()
        {
            using (var fixture = new Stage6Fixture())
            {
                fixture.AddOnlineDevice(deviceId: 1);
                fixture.AddOfflineDevice(deviceId: 2);
                // 模拟数据库有 4000 条设备 2（离线）的旧记录 + 1 条设备 1（在线）记录：
                // SQL 的在线过滤保证只有设备 1 的摘要被读出（RecordingDatabaseClient 忽略参数返回预置行，
                // 这里预置的就是"过滤后"的结果）。
                fixture.Database.QueryRowsByOperation["DeviceOperationRetryStore.LoadDueSummaries"] = new List<IReadOnlyDictionary<string, object>>
                {
                    Row(id: 9001, deviceId: 1, employeeId: "10001", permissionPending: true, permissionLevel: 7)
                };
                fixture.Database.QueryRowsByOperation["DeviceOperationRetryStore.LoadStatesByIds"] = new List<IReadOnlyDictionary<string, object>>
                {
                    Row(id: 9001, deviceId: 1, employeeId: "10001", permissionPending: true, permissionLevel: 7)
                };

                var result = fixture.Manager.RunOnceAsync("k03-online-first").GetAwaiter().GetResult();

                // 在线记录在第一轮即被领取执行，而不是等 40 轮之后。
                Assert.Equal(1, result.Due);
                Assert.Equal(1, result.Submitted);
                var summaryCommand = fixture.Database.Commands.Single(item => item.OperationName == "DeviceOperationRetryStore.LoadDueSummaries");
                Assert.Contains("device_id IN", summaryCommand.CommandText);
                Assert.Contains("@deviceId0=1", summaryCommand.CommandText);
                Assert.False(summaryCommand.CommandText.Contains("@deviceId1=2"), "离线设备不应出现在在线过滤参数中。");
                // 离线设备记录零写入：没有领取、没有离线延期。
                Assert.False(fixture.Database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.DeferOffline"));
                var byIdsCommand = fixture.Database.Commands.Single(item => item.OperationName == "DeviceOperationRetryStore.LoadStatesByIds");
                Assert.Contains("@id0=9001", byIdsCommand.CommandText);
            }
        }

        [TestCase]
        public static void DeviceOperationRetryManager_PerDeviceQuota_OnlineDeviceWithFreshRecordSelectedInFirstScan()
        {
            using (var fixture = new Stage6Fixture())
            {
                fixture.Options.BatchSize = 100;
                fixture.AddOnlineDevice(deviceId: 1);
                fixture.AddOnlineDevice(deviceId: 2);

                // 模拟数据库分区排名后的候选输出（RecordingDatabaseClient 忽略 WHERE/窗口排名，
                // 返回预置行）：设备 1 有 4000 条更早到期记录，候选窗口内为其配额前 10 条；
                // 设备 2 只有 1 条新记录。旧实现在全局 TOP(BatchSize*4) 截断下，设备 2 的记录
                // 排在第 4001 位、约 38 轮后才首次入选。
                var candidateRows = new List<IReadOnlyDictionary<string, object>>();
                for (var index = 0; index < 10; index++)
                {
                    candidateRows.Add(Row(id: 100 + index, deviceId: 1, employeeId: "E1-" + index, permissionPending: true, permissionLevel: 7));
                }

                candidateRows.Add(Row(id: 9001, deviceId: 2, employeeId: "E2-fresh", permissionPending: true, permissionLevel: 7));
                fixture.Database.QueryRowsByOperation["DeviceOperationRetryStore.LoadDueSummaries"] = candidateRows;
                fixture.Database.QueryRowsByOperation["DeviceOperationRetryStore.LoadStatesByIds"] = candidateRows;

                var result = fixture.Manager.RunOnceAsync("l3-quota").GetAwaiter().GetResult();

                Assert.Equal(11, result.Due);
                Assert.Equal(11, result.Submitted);
                // 设备 2 的新记录在第一轮即被领取执行。
                Assert.True(fixture.Database.Commands.Any(item =>
                    item.OperationName == "DeviceOperationRetryStore.TryClaimDueState" &&
                    item.CommandText.Contains("@id=9001")), "设备 2 的新记录未在首轮获得名额（L3）。");
                var summaryCommand = fixture.Database.Commands.Single(item => item.OperationName == "DeviceOperationRetryStore.LoadDueSummaries");
                Assert.Contains("PARTITION BY device_id", summaryCommand.CommandText);
                Assert.Contains("@perDeviceQuota=10", summaryCommand.CommandText);
            }
        }

        [TestCase]
        public static void DeviceOperationRetryManager_GetPerDeviceSummaryQuota_ScalesWithBatchSize()
        {
            Assert.Equal(10, DeviceOperationRetryManager.GetPerDeviceSummaryQuota(100));
            Assert.Equal(5, DeviceOperationRetryManager.GetPerDeviceSummaryQuota(50));
            Assert.Equal(2, DeviceOperationRetryManager.GetPerDeviceSummaryQuota(20));
            Assert.Equal(2, DeviceOperationRetryManager.GetPerDeviceSummaryQuota(5));
            Assert.Equal(2, DeviceOperationRetryManager.GetPerDeviceSummaryQuota(0));
        }

        [TestCase]
        public static void DeviceOperationRetryManager_NoOnlineDevices_SkipsDueLoadEntirely()
        {
            using (var fixture = new Stage6Fixture())
            {
                fixture.AddOfflineDevice(deviceId: 1);

                var result = fixture.Manager.RunOnceAsync("k03-no-online").GetAwaiter().GetResult();

                Assert.Equal(0, result.Due);
                Assert.False(fixture.Database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.LoadDueSummaries"));
                Assert.False(fixture.Database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.LoadStatesByIds"));
                Assert.False(fixture.Database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.TryClaimDueState"));
            }
        }

        [TestCase]
        public static void DeviceOperationRetryManager_DeviceOfflineAtProcessTime_StillDefersAfterClaim()
        {
            using (var fixture = new Stage6Fixture())
            {
                // 模拟竞态：过滤时设备 2 在线（在线集合非空触发加载），处理时已离线。
                fixture.AddOnlineDevice(deviceId: 1);
                fixture.AddOfflineDevice(deviceId: 2);
                fixture.Database.QueryRowsByOperation["DeviceOperationRetryStore.LoadDueSummaries"] = new List<IReadOnlyDictionary<string, object>>
                {
                    Row(id: 21, deviceId: 2, employeeId: "10001", permissionPending: true, permissionLevel: 7)
                };
                fixture.Database.QueryRowsByOperation["DeviceOperationRetryStore.LoadStatesByIds"] = new List<IReadOnlyDictionary<string, object>>
                {
                    Row(id: 21, deviceId: 2, employeeId: "10001", permissionPending: true, permissionLevel: 7)
                };

                var result = fixture.Manager.RunOnceAsync("k03-race-offline").GetAwaiter().GetResult();

                Assert.Equal(1, result.OfflineDeferred);
                Assert.True(fixture.Database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.DeferOffline"));
                Assert.False(fixture.Gateway.Calls.Any(call => call.MethodName == "SetPermissionAsync"));
            }
        }

        [TestCase]
        public static void DeviceOperationRetryManager_MaintenanceMarksMissingDeviceTerminal_AndCursorAdvances()
        {
            using (var fixture = new Stage6Fixture())
            {
                fixture.AddOnlineDevice(deviceId: 1);
                fixture.Options.BatchSize = 1;
                fixture.Database.QueryRowsByOperation["DeviceOperationRetryStore.LoadMaintenanceSummaries"] = new List<IReadOnlyDictionary<string, object>>
                {
                    Row(id: 31, deviceId: 99, employeeId: "10001", permissionPending: true, permissionLevel: 7),
                    Row(id: 32, deviceId: 1, employeeId: "10002", permissionPending: true, permissionLevel: 7)
                };

                fixture.Manager.RunMaintenanceScan("k03-maint-1");

                // 设备 99 不在运行时 → 终态；在线设备 1 的记录不做任何写。
                Assert.True(fixture.Database.Commands.Any(item =>
                    item.OperationName == "DeviceOperationRetryStore.MarkTerminalFailure" &&
                    item.CommandText.Contains("@lastError=DEVICE_NOT_FOUND")));

                fixture.Manager.RunMaintenanceScan("k03-maint-2");
                var maintenanceCommands = fixture.Database.Commands.Where(item => item.OperationName == "DeviceOperationRetryStore.LoadMaintenanceSummaries").ToList();
                Assert.Equal(2, maintenanceCommands.Count);
                Assert.Contains("@afterId=0", maintenanceCommands[0].CommandText);
                Assert.Contains("@afterId=32", maintenanceCommands[1].CommandText);
            }
        }

        [TestCase]
        public static void DeviceOperationRetryManager_SelectFairlyAcrossDevices_InterleavesDevices()
        {
            var summaries = new List<DeviceOperationRetryState>();
            for (var index = 0; index < 5; index++)
            {
                summaries.Add(State(id: 100 + index, deviceId: 1, employeeId: "E1-" + index));
            }

            for (var index = 0; index < 3; index++)
            {
                summaries.Add(State(id: 200 + index, deviceId: 2, employeeId: "E2-" + index));
            }

            var selected = DeviceOperationRetryManager.SelectFairlyAcrossDevices(summaries, batchSize: 4).ToList();

            Assert.Equal(4, selected.Count);
            // 轮转交错：前几个选择必须交替来自不同设备，单一设备不能连续占满整批。
            Assert.Equal(1, selected[0].DeviceId);
            Assert.Equal(2, selected[1].DeviceId);
            Assert.Equal(1, selected[2].DeviceId);
            Assert.Equal(2, selected[3].DeviceId);
            // 每设备内部保持摘要顺序（先到期先执行）。
            Assert.Equal("E1-0", selected[0].EmployeeId);
            Assert.Equal("E1-1", selected[2].EmployeeId);
        }

        [TestCase]
        public static void DeviceOperationRetryManager_GetScanDelay_BacklogUsesShortInterval()
        {
            var options = new DeviceOperationRetryOptions { ScanIntervalSeconds = 30, BacklogScanIntervalSeconds = 2 };
            Assert.Equal(TimeSpan.FromSeconds(2), DeviceOperationRetryManager.GetScanDelay(options, hasBacklog: true));
            Assert.Equal(TimeSpan.FromSeconds(30), DeviceOperationRetryManager.GetScanDelay(options, hasBacklog: false));
        }

        private static DeviceOperationRetryState State(long id, int deviceId, string employeeId)
        {
            return DeviceOperationRetryState.FromRow(Row(id: id, deviceId: deviceId, employeeId: employeeId, permissionPending: true));
        }

        private static IReadOnlyDictionary<string, object> Row(
            long id,
            int deviceId,
            string employeeId,
            int? permissionLevel = null,
            string permissionPayload = null,
            bool permissionPending = false,
            int attemptCount = 0)
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = id,
                ["device_id"] = deviceId,
                ["employee_id"] = employeeId,
                ["permission_level"] = permissionLevel,
                ["permission_payload"] = permissionPayload,
                ["permission_pending"] = permissionPending,
                ["permission_sync_completion_blocked"] = permissionPending,
                ["person_payload"] = null,
                ["person_pending"] = false,
                ["face_payload"] = null,
                ["face_pending"] = false,
                ["delete_person_pending"] = false,
                ["delete_face_pending"] = false,
                ["attempt_count"] = attemptCount,
                ["next_retry_at"] = null,
                ["last_error"] = null,
                ["last_attempt_at"] = null,
                ["exhausted_at"] = null,
                ["created_at"] = new DateTime(2026, 1, 1),
                ["updated_at"] = new DateTime(2026, 1, 1)
            };
        }
    }
}
