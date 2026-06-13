using System;
using System.Collections.Generic;

namespace ControlDoor.Permissions
{
    public sealed class DeviceOperationRetryIntent
    {
        public int DeviceId { get; set; }

        public string EmployeeId { get; set; } = string.Empty;

        public string Operation { get; set; } = string.Empty;

        public int? PermissionLevel { get; set; }

        public string PayloadJson { get; set; }

        public string PersonPayloadJson { get; set; }

        public string FacePayloadJson { get; set; }

        public string ReasonCode { get; set; }

        public string ReasonMessage { get; set; }

        public string RequestId { get; set; }

        public string LastError { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public DateTime? NextRetryAt { get; set; }

        public IDictionary<string, object> ToDetail(string code = "OK", string message = null)
        {
            return new Dictionary<string, object>
            {
                ["deviceId"] = DeviceId,
                ["employeeId"] = EmployeeId ?? string.Empty,
                ["operation"] = Operation ?? string.Empty,
                ["createdAt"] = CreatedAt.ToString("yyyy-MM-ddTHH:mm:ss"),
                ["nextRetryAt"] = NextRetryAt.HasValue ? NextRetryAt.Value.ToString("yyyy-MM-ddTHH:mm:ss") : null,
                ["code"] = code ?? string.Empty,
                ["message"] = message ?? string.Empty
            };
        }
    }
}
