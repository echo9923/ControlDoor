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
                "dbo.devices",
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
            var sql = ReadSqlFileContaining("20260309");

            foreach (var token in new[]
            {
                "device_operation_retry_states",
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
    }
}
