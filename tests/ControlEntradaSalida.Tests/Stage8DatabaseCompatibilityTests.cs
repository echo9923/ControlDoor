using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ControlEntradaSalida.Tests
{
    public static class Stage8DatabaseCompatibilityTests
    {
        [TestCase]
        public static void Stage8DatabaseScripts_CoverEveryCompatibilityTableNamedByStage8()
        {
            var sql = ReadAllSql();

            foreach (var table in new[]
            {
                "dbo.system_users",
                "dbo.attendance_gate_v2",
                "dbo.device_operation_retry_states",
                "dbo.face_event_checkpoint"
            })
            {
                Assert.Contains(table, sql);
            }
        }

        [TestCase]
        public static void Stage8RuntimeDatabaseHealth_DoesNotRequireLegacyDevicesTable()
        {
            var legacyDevicesTable = "dbo." + "devices";
            Assert.False(ControlDoor.Database.DatabaseHealthCheck.RequiredTables.Contains(legacyDevicesTable));
            Assert.True(ControlDoor.Database.DatabaseHealthCheck.RequiredTables.Contains("dbo.system_users"));
            Assert.True(ControlDoor.Database.DatabaseHealthCheck.RequiredTables.Contains("dbo.attendance_gate_v2"));
            Assert.True(ControlDoor.Database.DatabaseHealthCheck.RequiredTables.Contains("dbo.device_operation_retry_states"));
        }

        [TestCase]
        public static void Stage8Documentation_DoesNotDescribeLegacyDevicesAsRuntimeSource()
        {
            foreach (var file in CoreDocumentationFiles())
            {
                var text = File.ReadAllText(file, Encoding.UTF8);

                foreach (var forbidden in new[]
                {
                    "新增设备到" + "数据库",
                    "删除数据库" + "记录",
                    "设备表" + "读写"
                })
                {
                    Assert.False(text.Contains(forbidden), file + " should not contain legacy device-source wording: " + forbidden);
                }
            }
        }

        [TestCase]
        public static void Stage8DatabaseSpecialMigrationScripts_AreGuardedAndDoNotDropExistingTables()
        {
            foreach (var file in Directory.GetFiles("database", "*.sql").Where(IsSpecialMigrationScript))
            {
                var sql = File.ReadAllText(file, Encoding.UTF8).ToLowerInvariant();

                Assert.True(sql.Contains("if object_id") || sql.Contains("if col_length"), file);
                Assert.False(sql.Contains("drop table"), file);
                Assert.False(sql.Contains("truncate table"), file);
                Assert.False(sql.Contains("delete from dbo."), file);
            }
        }

        [TestCase]
        public static void Stage8DatabaseRetryStateScript_ContainsColumnsAndIndexesUsedByRetryEngine()
        {
            var sql = ReadSqlFile("专项_20260309_设备操作重试状态表.sql");

            foreach (var token in new[]
            {
                "device_operation_retry_states",
                "intent_version",
                "claim_token",
                "claim_until",
                "device_id",
                "employee_id",
                "permission_level",
                "permission_pending",
                "permission_sync_completion_blocked",
                "person_payload",
                "person_pending",
                "face_payload",
                "face_pending",
                "delete_person_pending",
                "delete_face_pending",
                "attempt_count",
                "next_retry_at",
                "last_error",
                "last_attempt_at",
                "exhausted_at",
                "IX_device_operation_retry_states_next_retry_at",
                "IX_device_operation_retry_states_employee_permission",
                "IX_device_operation_retry_states_exhausted_at"
            })
            {
                Assert.Contains(token.ToLowerInvariant(), sql);
            }
        }

        [TestCase]
        public static void Stage8DatabaseRetryStateSeed_MatchesSpecialScriptVersionAndLeaseColumns()
        {
            var seed = ReadSqlFile("stage1_4_integration_seed.sql");
            var special = ReadSqlFile("专项_20260309_设备操作重试状态表.sql");

            foreach (var token in new[]
            {
                "intent_version",
                "claim_token",
                "claim_until"
            })
            {
                Assert.Contains(token, seed);
                Assert.Contains(token, special);
                Assert.Contains("if col_length(n'dbo.device_operation_retry_states', n'" + token + "') is null", seed);
                Assert.Contains("if col_length(n'dbo.device_operation_retry_states', n'" + token + "') is null", special);
            }
        }

        [TestCase]
        public static void Stage8DatabaseFaceEventScripts_KeepUniqueSerialAndCheckpointFields()
        {
            var attendance = ReadSqlFileContaining("20260105");
            var checkpoint = ReadSqlFileContaining("face_event_checkpoint");

            foreach (var token in new[]
            {
                "attendance_gate_v2",
                "ux_gate_v2_id",
                "snapshot_path",
                "raw_payload",
                "process_status",
                "record_datetime"
            })
            {
                Assert.Contains(token.ToLowerInvariant(), attendance);
            }

            foreach (var token in new[]
            {
                "face_event_checkpoint",
                "DeviceIP",
                "LastSerialNo",
                "LastEventTime",
                "UpdatedAt"
            })
            {
                Assert.Contains(token.ToLowerInvariant(), checkpoint);
            }
        }

        private static bool IsSpecialMigrationScript(string path)
        {
            var name = Path.GetFileName(path);
            return name != null && name.StartsWith("\u4e13\u9879_", System.StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadAllSql()
        {
            return string.Join(
                "\n",
                Directory.GetFiles("database", "*.sql")
                    .Select(file => File.ReadAllText(file, Encoding.UTF8)))
                .ToLowerInvariant();
        }

        private static string ReadSqlFileContaining(string marker)
        {
            var file = Directory.GetFiles("database", "*.sql")
                .FirstOrDefault(item =>
                    Path.GetFileName(item).IndexOf(marker, System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    File.ReadAllText(item, Encoding.UTF8).IndexOf(marker, System.StringComparison.OrdinalIgnoreCase) >= 0);

            Assert.NotNull(file, marker);
            return File.ReadAllText(file, Encoding.UTF8).ToLowerInvariant();
        }

        private static string ReadSqlFile(string fileName)
        {
            return File.ReadAllText(Path.Combine("database", fileName), Encoding.UTF8).ToLowerInvariant();
        }

        private static IEnumerable<string> CoreDocumentationFiles()
        {
            yield return "AGENTS.md";
            yield return "\u76ee\u6807.md";
            yield return Path.Combine("docs", "\u95e8\u7981\u7cfb\u7edf\u8bbe\u8ba1\u65b9\u6848.md");
            yield return Path.Combine("docs", "\u5e95\u5c42\u6570\u636e\u5e93\u6587\u6863.md");
            yield return Path.Combine("docs", "gRPC\u63a5\u53e3\u6e05\u5355.md");
            yield return Path.Combine("docs", "api\u6587\u6863.md");
            yield return Path.Combine("docs", "stage4", "task01.md");
            yield return Path.Combine("docs", "stage4", "task02.md");
            yield return Path.Combine("docs", "stage4", "task06.md");
            yield return Path.Combine("docs", "stage4", "task07.md");
            yield return Path.Combine("docs", "stage4", "task08.md");
            yield return Path.Combine("docs", "stage4", "task09.md");
            yield return Path.Combine("docs", "stage4", "task10.md");
            yield return Path.Combine("docs", "stage8", "task04.md");
            yield return Path.Combine("docs", "stage8", "package-docs", "\u90e8\u7f72\u8bf4\u660e.md");
            yield return Path.Combine("docs", "stage8", "package-docs", "\u8fd0\u884c\u524d\u68c0\u67e5.md");
            yield return Path.Combine("docs", "stage10", "task01.md");
        }
    }
}
