using System;
using System.Collections.Generic;
using System.Linq;
using ControlDoor.Permissions;

namespace ControlEntradaSalida.Tests
{
    // 代码复核 M1：补偿扫描"公平取满一批"——候选在数据库侧按设备分区排名后以
    // "各设备第 1 条、各设备第 2 条……"交错排序，全局取前 BatchSize+1 条；
    // 第 101 条仅判定积压（HasMoreDue）驱动 2 秒快速间隔，取不满一批恢复 30 秒普通间隔。
    // 五个场景对应问题清单的期望行为表；预置行模拟交错排名输出（模拟适配器不执行窗口排名，
    // 排名语义由 SQL 文本断言与 tests/Integration 的真实 SQL 用例覆盖）。
    public static class CodeReviewM01FairBatchScanTests
    {
        private const int BatchSize = 100;

        [TestCase]
        public static void FairScan_SingleDeviceWith4000Backlog_FillsFullBatchAndUsesFastInterval()
        {
            using (var fixture = NewFixture())
            {
                // 仅一台设备有 4000 条积压：本轮取满 100 条（不再受每设备 10 条配额限制），
                // 第 101 条判定仍有积压 → 下一轮 2 秒快速间隔。
                var rows = RowsForDevice(1, 101);
                Preset(fixture, rows);

                var result = Run(fixture, "m1-single");

                Assert.Equal(BatchSize, result.Due);
                Assert.Equal(BatchSize, result.Submitted);
                Assert.True(result.HasMoreDue, "取满一批且读到第 101 条，应判定仍有积压。");
                Assert.Equal(TimeSpan.FromSeconds(2), DeviceOperationRetryManager.GetScanDelay(fixture.Options, result.HasMoreDue));
                // 第 101 条不进入本轮执行（完整载荷只按前 100 条主键读取）。
                var byIds = fixture.Database.Commands.Single(item => item.OperationName == "DeviceOperationRetryStore.LoadStatesByIds");
                Assert.Contains("=1100", byIds.CommandText);
                Assert.False(byIds.CommandText.Contains("=1101"), "第 101 条摘要（id=1101）仅用于积压判定，不得进入本轮执行。");
            }
        }

        [TestCase]
        public static void FairScan_TwoDevicesWithLargeBacklogs_SplitBatchRoughlyEvenly()
        {
            using (var fixture = NewFixture())
            {
                // 两台设备各有大量积压：交错排序下大致各取 50 条。
                fixture.AddOnlineDevice(deviceId: 2);
                var rows = Interleave(RowsForDevice(1, 51), RowsForDevice(2, 51, startId: 2001));
                Preset(fixture, rows);

                var result = Run(fixture, "m1-two-large");

                Assert.Equal(BatchSize, result.Due);
                Assert.True(result.HasMoreDue);
                // 交错输出前 100 条 = 每台 50 条；两台的边界记录入选、各自第 51 条被截掉。
                var byIds = fixture.Database.Commands.Single(item => item.OperationName == "DeviceOperationRetryStore.LoadStatesByIds");
                Assert.Contains("=1050", byIds.CommandText);
                Assert.Contains("=2050", byIds.CommandText);
                Assert.False(byIds.CommandText.Contains("=1051"), "第 101 条全局名额之外的记录不得进入本轮。");
                Assert.False(byIds.CommandText.Contains("=2051"), "第 101 条全局名额之外的记录不得进入本轮。");
            }
        }

        [TestCase]
        public static void FairScan_OneDeviceBacklogAndOtherSingleRecord_SmallDeviceSelectedFirstRound()
        {
            // 一台有 4000 条积压、另一台只有 1 条：小批设备的 1 条首轮入选，其余名额给大批设备。
            using (var fixture = NewFixture())
            {
                fixture.AddOnlineDevice(deviceId: 2);
                var rows = new List<IReadOnlyDictionary<string, object>> { Row(1001, 1), Row(9001, 2) };
                rows.AddRange(RowsForDevice(1, 99, startId: 1002));
                rows.Add(Row(1101, 1));
                Preset(fixture, rows);

                var result = Run(fixture, "m1-big-small");

                Assert.Equal(BatchSize, result.Due);
                Assert.True(result.HasMoreDue);
                Assert.True(fixture.Database.Commands.Any(item =>
                    item.OperationName == "DeviceOperationRetryStore.TryClaimDueState" &&
                    item.CommandText.Contains("@id=9001")), "小批设备的唯一记录未在首轮入选。");
                Assert.Equal(TimeSpan.FromSeconds(2), DeviceOperationRetryManager.GetScanDelay(fixture.Options, result.HasMoreDue));
            }
        }

        [TestCase]
        public static void FairScan_AllDevicesBacklogged_EveryDeviceGetsSlotsThenRemainder()
        {
            // 27 台都有积压：每台先获得名额，再轮转分配剩余名额（每台 3~4 条）。
            using (var fixture = NewFixture())
            {
                for (var deviceId = 2; deviceId <= 27; deviceId++)
                {
                    fixture.AddOnlineDevice(deviceId: deviceId);
                }

                var rows = new List<IReadOnlyDictionary<string, object>>();
                for (var rank = 1; rank <= 4; rank++)
                {
                    for (var deviceId = 1; deviceId <= 27; deviceId++)
                    {
                        rows.Add(Row(deviceId * 100 + rank, deviceId));
                    }
                }

                Preset(fixture, rows);

                var result = Run(fixture, "m1-all-27");

                Assert.Equal(BatchSize, result.Due);
                Assert.True(result.HasMoreDue);
                // 每台的 rank-1 记录全部入选（先保证每台有名额）。
                var byIds = fixture.Database.Commands.Single(item => item.OperationName == "DeviceOperationRetryStore.LoadStatesByIds");
                for (var deviceId = 1; deviceId <= 27; deviceId++)
                {
                    Assert.Contains("=" + (deviceId * 100 + 1), byIds.CommandText);
                }
            }
        }

        [TestCase]
        public static void FairScan_OnlySevenDueRecords_ProcessesAllAndReturnsToNormalInterval()
        {
            // 全部设备合计只有 7 条到期：处理 7 条，读不到第 101 条 → 恢复 30 秒普通间隔。
            using (var fixture = NewFixture())
            {
                var rows = RowsForDevice(1, 7);
                Preset(fixture, rows);

                var result = Run(fixture, "m1-seven");

                Assert.Equal(7, result.Due);
                Assert.Equal(7, result.Submitted);
                Assert.False(result.HasMoreDue, "不足一批时不应判定积压。");
                Assert.Equal(TimeSpan.FromSeconds(30), DeviceOperationRetryManager.GetScanDelay(fixture.Options, result.HasMoreDue));
            }
        }

        [TestCase]
        public static void FairScan_BacklogExhausts_LastPartialBatchReturnsToNormalInterval()
        {
            // 积压收尾：最后一批不足 100 条时恢复普通间隔（配合单设备取满场景验证节奏切换）。
            using (var fixture = NewFixture())
            {
                var rows = RowsForDevice(1, 43);
                Preset(fixture, rows);

                var result = Run(fixture, "m1-tail");

                Assert.Equal(43, result.Due);
                Assert.False(result.HasMoreDue);
                Assert.Equal(TimeSpan.FromSeconds(30), DeviceOperationRetryManager.GetScanDelay(fixture.Options, result.HasMoreDue));
            }
        }

        private static Stage6Fixture NewFixture()
        {
            var fixture = new Stage6Fixture();
            fixture.Options.BatchSize = BatchSize;
            fixture.Options.ScanIntervalSeconds = 30;
            fixture.Options.BacklogScanIntervalSeconds = 2;
            fixture.AddOnlineDevice(deviceId: 1);
            return fixture;
        }

        private static DeviceOperationRetryScanResult Run(Stage6Fixture fixture, string requestId)
        {
            return fixture.Manager.RunOnceAsync(requestId).GetAwaiter().GetResult();
        }

        private static void Preset(Stage6Fixture fixture, List<IReadOnlyDictionary<string, object>> rows)
        {
            // 摘要预置 = 数据库交错排名输出（含第 101 条积压判定条目）；
            // 载荷预置 = 按前 BatchSize 个主键命中（RecordingDatabaseClient 忽略 IN 过滤，
            // 预置即"过滤后"结果；实际请求的主键集合记录在 LoadStatesByIds 命令参数中，
            // 由各用例断言第 101 条未被请求）。
            fixture.Database.QueryRowsByOperation["DeviceOperationRetryStore.LoadDueSummaries"] = rows;
            fixture.Database.QueryRowsByOperation["DeviceOperationRetryStore.LoadStatesByIds"] = rows.Take(BatchSize).ToList();
        }

        private static List<IReadOnlyDictionary<string, object>> RowsForDevice(int deviceId, int count, int startId = 1001)
        {
            var rows = new List<IReadOnlyDictionary<string, object>>();
            for (var index = 0; index < count; index++)
            {
                rows.Add(Row(startId + index, deviceId));
            }

            return rows;
        }

        private static List<IReadOnlyDictionary<string, object>> Interleave(List<IReadOnlyDictionary<string, object>> first, List<IReadOnlyDictionary<string, object>> second)
        {
            var rows = new List<IReadOnlyDictionary<string, object>>();
            var index = 0;
            while (index < first.Count || index < second.Count)
            {
                if (index < first.Count) rows.Add(first[index]);
                if (index < second.Count) rows.Add(second[index]);
                index++;
            }

            return rows;
        }

        private static IReadOnlyDictionary<string, object> Row(long id, int deviceId)
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = id,
                ["device_id"] = deviceId,
                ["employee_id"] = "E" + deviceId + "-" + id,
                ["permission_level"] = 7,
                ["permission_payload"] = null,
                ["permission_pending"] = true,
                ["permission_sync_completion_blocked"] = true,
                ["person_payload"] = null,
                ["person_pending"] = false,
                ["face_payload"] = null,
                ["face_pending"] = false,
                ["delete_person_pending"] = false,
                ["delete_face_pending"] = false,
                ["attempt_count"] = 0,
                ["next_retry_at"] = null,
                ["last_error"] = null,
                ["last_attempt_at"] = null,
                ["exhausted_at"] = null,
                ["created_at"] = new DateTime(2026, 9, 10),
                ["updated_at"] = new DateTime(2026, 9, 10)
            };
        }
    }
}
