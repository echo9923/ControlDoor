using System;
using System.Collections.Generic;

namespace ControlDoor.Permissions
{
    public sealed class EnrollmentTaskRecord
    {
        public string TaskId { get; set; } = string.Empty;

        public string EmployeeId { get; set; } = string.Empty;

        public string Action { get; set; } = "CaptureFaceStream";

        public EnrollmentTaskStatus Status { get; set; } = EnrollmentTaskStatus.Running;

        public string Message { get; set; } = string.Empty;

        public string ErrorCode { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public DateTime UpdatedAt { get; set; } = DateTime.Now;

        public IDictionary<string, object> ToResponseFields()
        {
            return new Dictionary<string, object>
            {
                ["taskId"] = TaskId ?? string.Empty,
                ["employeeId"] = EmployeeId ?? string.Empty,
                ["action"] = Action ?? string.Empty,
                ["status"] = Status.ToString(),
                ["message"] = Message ?? string.Empty,
                ["errorCode"] = ErrorCode ?? string.Empty
            };
        }

        public EnrollmentTaskRecord Clone()
        {
            return new EnrollmentTaskRecord
            {
                TaskId = TaskId,
                EmployeeId = EmployeeId,
                Action = Action,
                Status = Status,
                Message = Message,
                ErrorCode = ErrorCode,
                CreatedAt = CreatedAt,
                UpdatedAt = UpdatedAt
            };
        }
    }
}
