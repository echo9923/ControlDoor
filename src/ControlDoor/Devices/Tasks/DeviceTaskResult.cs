using System;
using System.Collections.Generic;
using ControlDoor.Devices.Runtime;

namespace ControlDoor.Devices.Tasks
{
    public sealed class DeviceTaskResult
    {
        public string TaskId { get; set; } = string.Empty;

        public string RequestId { get; set; } = string.Empty;

        public int DeviceId { get; set; }

        public DeviceTaskType TaskType { get; set; }

        public string OperationName { get; set; } = string.Empty;

        public bool Success { get; set; }

        public string Code { get; set; } = "OK";

        public string Message { get; set; } = string.Empty;

        public int? SdkErrorCode { get; set; }

        public DeviceConnectionStatus DeviceStatusAfter { get; set; } = DeviceConnectionStatus.Unknown;

        public bool Retryable { get; set; }

        public object Data { get; set; }

        public List<string> Errors { get; set; } = new List<string>();

        public string ExceptionType { get; set; } = string.Empty;

        public DateTime StartedAt { get; set; }

        public DateTime CompletedAt { get; set; }

        public long DurationMilliseconds { get; set; }

        public static DeviceTaskResult FromTask(DeviceSdkTask task, bool success, string code, string message, DeviceConnectionStatus statusAfter, DateTime startedAt, DateTime completedAt)
        {
            if (task == null)
            {
                throw new ArgumentNullException(nameof(task));
            }

            return new DeviceTaskResult
            {
                TaskId = task.TaskId,
                RequestId = task.RequestId,
                DeviceId = task.DeviceId,
                TaskType = task.TaskType,
                OperationName = task.OperationName,
                Success = success,
                Code = code ?? string.Empty,
                Message = message ?? string.Empty,
                DeviceStatusAfter = statusAfter,
                StartedAt = startedAt,
                CompletedAt = completedAt,
                DurationMilliseconds = Math.Max(0, (long)(completedAt - startedAt).TotalMilliseconds)
            };
        }

        public static DeviceTaskResult Queued(DeviceSdkTask task, string message = "Task queued.")
        {
            var now = DateTime.Now;
            return FromTask(task, true, "QUEUED", message, DeviceConnectionStatus.Unknown, now, now);
        }

        public static DeviceTaskResult Rejected(DeviceSdkTask task, string code, string message)
        {
            var now = DateTime.Now;
            return FromTask(task, false, code, message, DeviceConnectionStatus.Unknown, now, now);
        }

        public static DeviceTaskResult Timeout(DeviceSdkTask task, string message = "Task wait timeout.")
        {
            var now = DateTime.Now;
            return FromTask(task, false, "TIMEOUT", message, DeviceConnectionStatus.Unknown, now, now);
        }

        public static DeviceTaskResult Cancelled(DeviceSdkTask task, string message = "Task cancelled.")
        {
            var now = DateTime.Now;
            return FromTask(task, false, "CANCELLED", message, DeviceConnectionStatus.Unknown, now, now);
        }

        public static DeviceTaskResult FromException(DeviceSdkTask task, Exception exception, DateTime startedAt, DateTime completedAt)
        {
            var result = FromTask(task, false, "INTERNAL_ERROR", exception == null ? "Internal error." : exception.Message, DeviceConnectionStatus.Unknown, startedAt, completedAt);
            result.ExceptionType = exception?.GetType().Name ?? string.Empty;
            return result;
        }

        public DeviceTaskResult WithCompletionTiming(DateTime startedAt, DateTime completedAt)
        {
            StartedAt = startedAt;
            CompletedAt = completedAt;
            DurationMilliseconds = Math.Max(0, (long)(completedAt - startedAt).TotalMilliseconds);
            return this;
        }
    }
}
