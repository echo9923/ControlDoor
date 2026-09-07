using System;
using ControlDoor.Database;

namespace ControlDoor.Permissions
{
    public sealed class SystemUserSyncStatusWriter : IUserSyncStatusWriter
    {
        private readonly IDatabaseClient database;

        public SystemUserSyncStatusWriter(IDatabaseClient database)
        {
            this.database = database ?? throw new ArgumentNullException(nameof(database));
        }

        public void MarkPermissionSynced(string employeeId, int permissionLevel)
        {
            if (string.IsNullOrWhiteSpace(employeeId))
            {
                return;
            }

            Check(database.ExecuteNonQuery(
                "SystemUserSyncStatus.MarkPermissionSynced",
                @"UPDATE dbo.system_users
SET access_permission = @permissionLevel,
    last_synced_level = @permissionLevel,
    permission_updated_at = SYSDATETIME(),
    last_synced_at = SYSDATETIME()
WHERE username = @employeeId;",
                new DatabaseParameter("@employeeId", employeeId),
                new DatabaseParameter("@permissionLevel", permissionLevel)));
        }

        public void MarkPersonSynced(string employeeId)
        {
            if (string.IsNullOrWhiteSpace(employeeId))
            {
                return;
            }

            Check(database.ExecuteNonQuery(
                "SystemUserSyncStatus.MarkPersonSynced",
                @"UPDATE dbo.system_users
SET last_synced_at = SYSDATETIME()
WHERE username = @employeeId;",
                new DatabaseParameter("@employeeId", employeeId)));
        }

        public void MarkPersonDeleted(string employeeId)
        {
            if (string.IsNullOrWhiteSpace(employeeId))
            {
                return;
            }

            Check(database.ExecuteNonQuery(
                "SystemUserSyncStatus.MarkPersonDeleted",
                @"UPDATE dbo.system_users
SET last_synced_level = NULL,
    last_synced_at = SYSDATETIME()
WHERE username = @employeeId;",
                new DatabaseParameter("@employeeId", employeeId)));
        }

        private static void Check(DatabaseCommandRecord record)
        {
            if (record.Error != null) throw new InvalidOperationException(record.Error.Message);
        }
    }
}
