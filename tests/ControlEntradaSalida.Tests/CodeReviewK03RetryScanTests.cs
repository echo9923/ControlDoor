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
        public static void DeviceOperationRetryManager_LoadDueSummaries_FiltersByOnlineDevicesAndSkipsPayloadColumns_InterleavedTake()
        {
            var database = new RecordingDatabaseClient();
            var store = new DeviceOperationRetryStore(database, new DeviceOperationRetryOptions { BatchSize = 100 });

            var summaries = store.LoadDueSummaries(new DateTime(2026, 1, 1), 101, new[] { 7, 3, 7 });

            Assert.Equal(0, summaries.Count);
            var command = database.Commands.Single(item => item.OperationName == "DeviceOperationRetryStore.LoadDueSummaries");
            Assert.Contains("device_id IN (@deviceId0, @deviceId1)", command.CommandText);
            Assert.Contains("@deviceId0=3", command.CommandText);
            Assert.Contains("@deviceId1=7", command.CommandText);
            // 复核 M1：按设备分区排名 + 交错排序，全局 TOP 位于交错排序之后。
            Assert.Contains("ROW_NUMBER() OVER (PARTITION BY device_id", command.CommandText);
            Assert.Contains("SELECT TOP (@take)", command.CommandText);
            Assert.Contains("@take=101", command.CommandText);
            Assert.Contains("ORDER BY __device_rank ASC, next_retry_at ASC", command.CommandText);
            // 交错排序之前不得出现全局截断：整条 SQL 只允许外层一个 TOP（作用于排名后的交错结果）。
            var topOccurrences = System.Text.RegularExpressions.Regex.Matches(command.CommandText, "TOP").Count;
            Assert.Equal(1, topOccurrences);
            var rankedIndex = command.CommandText.IndexOf(") AS ranked", StringComparison.Ordinal);
            var rankStart = command.CommandText.IndexOf("ROW_NUMBER() OVER (PARTITION BY device_id", StringComparison.Ordinal);
            Assert.True(rankedIndex > rankStart && rankStart >= 0, "排名子查询结构不完整。");
            Assert.False(command.CommandText.Substring(rankStart, rankedIndex - rankStart).Contains("TOP"), "排名子查询内部不得截断。");
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
        public static void DeviceOperationRetryManager_InterleavedCandidates_OnlineDeviceWithFreshRecordSelectedInFirstScan()
        {
            using (var fixture = new Stage6Fixture())
            {
                fixture.Options.BatchSize = 100;
                fixture.AddOnlineDevice(deviceId: 1);
                fixture.AddOnlineDevice(deviceId: 2);

                // 模拟数据库交错排名后的候选输出（RecordingDatabaseClient 忽略 WHERE/窗口排名，
                // 返回预置行，预置顺序即"各设备第 1 条、各设备第 2 条……"）：设备 1 有 4000 条更早
                // 到期记录、设备 2 只有 1 条新记录。旧全局截断实现下设备 2 排在第 4001 位、约 38 轮
                // 后才首次入选；交错排序下其第 1 条在本轮即入选，剩余名额由设备 1 取满。
                var candidateRows = new List<IReadOnlyDictionary<string, object>>
                {
                    Row(id: 1001, deviceId: 1, employeeId: "E1-1", permissionPending: true, permissionLevel: 7),
                    Row(id: 9001, deviceId: 2, employeeId: "E2-fresh", permissionPending: true, permissionLevel: 7)
                };
                for (var index = 2; index <= 100; index++)
                {
                    candidateRows.Add(Row(id: 1000 + index, deviceId: 1, employeeId: "E1-" + index, permissionPending: true, permissionLevel: 7));
                }

                // 第 101 条仅用于积压判定（HasMoreDue），不进入本轮执行。
                candidateRows.Add(Row(id: 1101, deviceId: 1, employeeId: "E1-101", permissionPending: true, permissionLevel: 7));
                fixture.Database.QueryRowsByOperation["DeviceOperationRetryStore.LoadDueSummaries"] = candidateRows;
                // 载荷预置 = 按前 100 个主键命中（RecordingDatabaseClient 忽略 IN 过滤；
                // 实际请求的主键集合记录在命令参数中，由下方断言第 101 条未被请求）。
                fixture.Database.QueryRowsByOperation["DeviceOperationRetryStore.LoadStatesByIds"] = candidateRows.Take(100).ToList();

                var result = fixture.Manager.RunOnceAsync("m1-interleaved-fresh").GetAwaiter().GetResult();

                Assert.Equal(100, result.Due);
                Assert.Equal(100, result.Submitted);
                Assert.True(result.HasMoreDue, "候选读到第 101 条应判定仍有积压。");
                Assert.True(fixture.Database.Commands.Any(item =>
                    item.OperationName == "DeviceOperationRetryStore.TryClaimDueState" &&
                    item.CommandText.Contains("@id=9001")), "设备 2 的新记录未在首轮获得名额（M1）。");
                // 第 101 条不进入本轮：完整载荷只按前 100 条主键读取。
                var byIdsCommand = fixture.Database.Commands.Single(item => item.OperationName == "DeviceOperationRetryStore.LoadStatesByIds");
                Assert.False(byIdsCommand.CommandText.Contains("=1101"), "第 101 条摘要仅用于积压判定，不得进入本轮执行。");
                Assert.Equal(TimeSpan.FromSeconds(2), DeviceOperationRetryManager.GetScanDelay(fixture.Options, result.HasMoreDue));
                var summaryCommand = fixture.Database.Commands.Single(item => item.OperationName == "DeviceOperationRetryStore.LoadDueSummaries");
                Assert.Contains("PARTITION BY device_id", summaryCommand.CommandText);
                Assert.Contains("@take=101", summaryCommand.CommandText);
            }
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
