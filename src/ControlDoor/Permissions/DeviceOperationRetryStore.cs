using System;
using ControlDoor.Database;

namespace ControlDoor.Permissions
{
    public sealed class DeviceOperationRetryStore : IDeviceOperationRetryWriter
    {
        private readonly IDatabaseClient database;

        public DeviceOperationRetryStore(IDatabaseClient database)
        {
            this.database = database ?? throw new ArgumentNullException(nameof(database));
        }

        public DeviceOperationRetryWriteResult UpsertIntent(DeviceOperationRetryIntent intent)
        {
            if (intent == null)
            {
                return DeviceOperationRetryWriteResult.Failed(null, "INVALID_ARGUMENT", "补偿意图不能为空。");
            }

            if (intent.DeviceId <= 0 || string.IsNullOrWhiteSpace(intent.EmployeeId))
            {
                return DeviceOperationRetryWriteResult.Failed(intent, "INVALID_ARGUMENT", "补偿意图缺少设备或员工编号。");
            }

            var flags = RetryIntentFlags.FromOperation(intent.Operation);
            if (!flags.Valid)
            {
                return DeviceOperationRetryWriteResult.Failed(intent, "INVALID_ARGUMENT", "不支持的补偿操作: " + intent.Operation);
            }

            var record = database.ExecuteNonQuery(
                "DeviceOperationRetryStore.UpsertIntent",
                Sql,
                new DatabaseParameter("@deviceId", intent.DeviceId),
                new DatabaseParameter("@employeeId", intent.EmployeeId),
                new DatabaseParameter("@permissionLevel", (object)intent.PermissionLevel ?? DBNull.Value),
                new DatabaseParameter("@permissionPending", flags.PermissionPending),
                new DatabaseParameter("@personPayload", flags.PersonPending ? (object)(intent.PayloadJson ?? string.Empty) : DBNull.Value),
                new DatabaseParameter("@personPending", flags.PersonPending),
                new DatabaseParameter("@facePayload", flags.FacePending ? (object)(intent.PayloadJson ?? string.Empty) : DBNull.Value),
                new DatabaseParameter("@facePending", flags.FacePending),
                new DatabaseParameter("@deletePersonPending", flags.DeletePersonPending),
                new DatabaseParameter("@deleteFacePending", flags.DeleteFacePending),
                new DatabaseParameter("@lastError", (object)Trim(intent.LastError, 2000) ?? DBNull.Value),
                new DatabaseParameter("@nextRetryAt", (object)intent.NextRetryAt ?? DBNull.Value));

            if (record.Error != null)
            {
                return DeviceOperationRetryWriteResult.Failed(intent, "DB_ERROR", record.Error.Message);
            }

            return DeviceOperationRetryWriteResult.Ok(intent);
        }

        private const string Sql = @"
MERGE dbo.device_operation_retry_states AS target
USING (SELECT @deviceId AS device_id, @employeeId AS employee_id) AS source
ON target.device_id = source.device_id AND target.employee_id = source.employee_id
WHEN MATCHED THEN
    UPDATE SET
        permission_level = CASE WHEN @permissionPending = 1 THEN @permissionLevel ELSE target.permission_level END,
        permission_pending = CASE WHEN @permissionPending = 1 THEN 1 ELSE target.permission_pending END,
        permission_sync_completion_blocked = CASE WHEN @permissionPending = 1 THEN 1 ELSE target.permission_sync_completion_blocked END,
        person_payload = CASE WHEN @personPending = 1 THEN @personPayload ELSE target.person_payload END,
        person_pending = CASE WHEN @personPending = 1 THEN 1 ELSE target.person_pending END,
        face_payload = CASE WHEN @facePending = 1 THEN @facePayload ELSE target.face_payload END,
        face_pending = CASE WHEN @facePending = 1 THEN 1 ELSE target.face_pending END,
        delete_person_pending = CASE WHEN @deletePersonPending = 1 THEN 1 ELSE target.delete_person_pending END,
        delete_face_pending = CASE WHEN @deleteFacePending = 1 THEN 1 ELSE target.delete_face_pending END,
        next_retry_at = @nextRetryAt,
        last_error = @lastError,
        exhausted_at = NULL,
        updated_at = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (
        device_id,
        employee_id,
        permission_level,
        permission_pending,
        permission_sync_completion_blocked,
        person_payload,
        person_pending,
        face_payload,
        face_pending,
        delete_person_pending,
        delete_face_pending,
        attempt_count,
        next_retry_at,
        last_error,
        created_at,
        updated_at)
    VALUES (
        @deviceId,
        @employeeId,
        @permissionLevel,
        @permissionPending,
        CASE WHEN @permissionPending = 1 THEN 1 ELSE 0 END,
        @personPayload,
        @personPending,
        @facePayload,
        @facePending,
        @deletePersonPending,
        @deleteFacePending,
        0,
        @nextRetryAt,
        @lastError,
        SYSDATETIME(),
        SYSDATETIME());";

        private static string Trim(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, maxLength);
        }

        private sealed class RetryIntentFlags
        {
            public bool Valid { get; set; }

            public bool PermissionPending { get; set; }

            public bool PersonPending { get; set; }

            public bool FacePending { get; set; }

            public bool DeletePersonPending { get; set; }

            public bool DeleteFacePending { get; set; }

            public static RetryIntentFlags FromOperation(string operation)
            {
                var flags = new RetryIntentFlags();
                switch ((operation ?? string.Empty).Trim())
                {
                    case "SyncPermission":
                        flags.Valid = true;
                        flags.PermissionPending = true;
                        break;
                    case "SyncPerson":
                        flags.Valid = true;
                        flags.PersonPending = true;
                        break;
                    case "UploadFace":
                        flags.Valid = true;
                        flags.FacePending = true;
                        break;
                    case "DeletePerson":
                        flags.Valid = true;
                        flags.DeletePersonPending = true;
                        break;
                    case "DeleteFace":
                        flags.Valid = true;
                        flags.DeleteFacePending = true;
                        break;
                }

                return flags;
            }
        }
    }
}
