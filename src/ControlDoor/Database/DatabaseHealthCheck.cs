using System;
using System.Collections.Generic;

namespace ControlDoor.Database
{
    public sealed class DatabaseHealthCheck
    {
        private readonly IDatabaseClient database;

        public DatabaseHealthCheck(IDatabaseClient database)
        {
            this.database = database ?? throw new ArgumentNullException(nameof(database));
        }

        public DatabaseHealthReport Run()
        {
            var report = new DatabaseHealthReport();
            report.Commands.Add(database.ExecuteScalar("ConnectionTest", "SELECT 1"));
            report.Commands.Add(database.ExecuteScalar("CurrentDatabase", "SELECT DB_NAME()"));

            foreach (var table in RequiredTables)
            {
                report.Commands.Add(database.ExecuteNonQuery("ReadTable:" + table, "SELECT TOP 0 * FROM " + table));
            }

            foreach (var table in OptionalTables)
            {
                var record = database.ExecuteNonQuery("ReadOptionalTable:" + table, "SELECT TOP 0 * FROM " + table);
                report.Commands.Add(record);
            }

            return report;
        }

        public static IReadOnlyList<string> RequiredTables { get; } = new[] { "dbo.devices", "dbo.system_users" };

        public static IReadOnlyList<string> OptionalTables { get; } = new[] { "dbo.attendance_gate_v2", "dbo.device_operation_retry_states", "dbo.face_event_checkpoint" };
    }

    public sealed class DatabaseHealthReport
    {
        public IList<DatabaseCommandRecord> Commands { get; } = new List<DatabaseCommandRecord>();

        public bool Success
        {
            get
            {
                foreach (var command in Commands)
                {
                    if (command.Error != null &&
                        !command.OperationName.StartsWith("ReadOptionalTable:", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }
}
