using System;
using System.Collections.Generic;
using ControlDoor.Database;

namespace ControlDoor.Permissions
{
    public sealed class DeviceOperationRetryState
    {
        public long Id { get; set; }

        public int DeviceId { get; set; }

        public string EmployeeId { get; set; } = string.Empty;

        public int? PermissionLevel { get; set; }

        public string PermissionPayloadJson { get; set; }

        public bool PermissionPending { get; set; }

        public bool PermissionSyncCompletionBlocked { get; set; }

        public string PersonPayloadJson { get; set; }

        public bool PersonPending { get; set; }

        public string FacePayloadJson { get; set; }

        public bool FacePending { get; set; }

        public bool DeletePersonPending { get; set; }

        public bool DeleteFacePending { get; set; }

        public int AttemptCount { get; set; }

        public DateTime? NextRetryAt { get; set; }

        public string LastError { get; set; }

        public DateTime? LastAttemptAt { get; set; }

        public DateTime? ExhaustedAt { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime UpdatedAt { get; set; }

        public bool HasPending =>
            PermissionPending ||
            PersonPending ||
            FacePending ||
            DeletePersonPending ||
            DeleteFacePending;

        public string StateKey => DeviceId + ":" + EmployeeId;

        public DeviceOperationRetryState Clone()
        {
            return (DeviceOperationRetryState)MemberwiseClone();
        }

        public static DeviceOperationRetryState FromRow(IReadOnlyDictionary<string, object> row)
        {
            if (row == null)
            {
                throw new ArgumentNullException(nameof(row));
            }

            return new DeviceOperationRetryState
            {
                Id = ToInt64(Get(row, "id")),
                DeviceId = ToInt32(Get(row, "device_id")),
                EmployeeId = Convert.ToString(Get(row, "employee_id")) ?? string.Empty,
                PermissionLevel = ToNullableInt32(Get(row, "permission_level")),
                PermissionPayloadJson = Convert.ToString(Get(row, "permission_payload")),
                PermissionPending = ToBool(Get(row, "permission_pending")),
                PermissionSyncCompletionBlocked = ToBool(Get(row, "permission_sync_completion_blocked")),
                PersonPayloadJson = Convert.ToString(Get(row, "person_payload")),
                PersonPending = ToBool(Get(row, "person_pending")),
                FacePayloadJson = Convert.ToString(Get(row, "face_payload")),
                FacePending = ToBool(Get(row, "face_pending")),
                DeletePersonPending = ToBool(Get(row, "delete_person_pending")),
                DeleteFacePending = ToBool(Get(row, "delete_face_pending")),
                AttemptCount = ToInt32(Get(row, "attempt_count")),
                NextRetryAt = ToNullableDateTime(Get(row, "next_retry_at")),
                LastError = Convert.ToString(Get(row, "last_error")),
                LastAttemptAt = ToNullableDateTime(Get(row, "last_attempt_at")),
                ExhaustedAt = ToNullableDateTime(Get(row, "exhausted_at")),
                CreatedAt = ToDateTime(Get(row, "created_at")),
                UpdatedAt = ToDateTime(Get(row, "updated_at"))
            };
        }

        public static DatabaseParameter[] KeyParameters(DeviceOperationRetryState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            return new[]
            {
                new DatabaseParameter("@id", state.Id),
                new DatabaseParameter("@deviceId", state.DeviceId),
                new DatabaseParameter("@employeeId", state.EmployeeId)
            };
        }

        private static object Get(IReadOnlyDictionary<string, object> row, string name)
        {
            object value;
            return row.TryGetValue(name, out value) ? value : null;
        }

        private static bool ToBool(object value)
        {
            if (value == null || value == DBNull.Value)
            {
                return false;
            }

            if (value is bool)
            {
                return (bool)value;
            }

            return Convert.ToInt32(value) != 0;
        }

        private static int ToInt32(object value)
        {
            if (value == null || value == DBNull.Value)
            {
                return 0;
            }

            return Convert.ToInt32(value);
        }

        private static long ToInt64(object value)
        {
            if (value == null || value == DBNull.Value)
            {
                return 0;
            }

            return Convert.ToInt64(value);
        }

        private static int? ToNullableInt32(object value)
        {
            if (value == null || value == DBNull.Value)
            {
                return null;
            }

            return Convert.ToInt32(value);
        }

        private static DateTime ToDateTime(object value)
        {
            if (value == null || value == DBNull.Value)
            {
                return DateTime.MinValue;
            }

            return Convert.ToDateTime(value);
        }

        private static DateTime? ToNullableDateTime(object value)
        {
            if (value == null || value == DBNull.Value)
            {
                return null;
            }

            return Convert.ToDateTime(value);
        }
    }
}
