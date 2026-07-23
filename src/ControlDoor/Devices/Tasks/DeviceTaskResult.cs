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

        // 等待层超时/取消不等同于设备执行终态；调用方可据此继续观察 Completion。
        public bool IsWaitOutcome { get; set; }

        // Compatibility snapshot fields for callers that do not hold the task object.
        public bool TaskStarted { get; set; }

        public bool TaskCompleted { get; set; }

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
                TaskStarted = task.TaskStarted,
                TaskCompleted = task.TaskCompleted,
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
            return Timeout(task, message, isWaitOutcome: true);
        }

        public static DeviceTaskResult Timeout(DeviceSdkTask task, string message, bool isWaitOutcome)
        {
            var now = DateTime.Now;
            var result = FromTask(task, false, "TIMEOUT", message, DeviceConnectionStatus.Unknown, now, now);
            result.IsWaitOutcome = isWaitOutcome;
            return result;
        }

        public static DeviceTaskResult Cancelled(DeviceSdkTask task, string message = "Task cancelled.")
        {
            return Cancelled(task, message, isWaitOutcome: false);
        }

        public static DeviceTaskResult Cancelled(DeviceSdkTask task, string message, bool isWaitOutcome)
        {
            var now = DateTime.Now;
            var result = FromTask(task, false, "CANCELLED", message, DeviceConnectionStatus.Unknown, now, now);
            result.IsWaitOutcome = isWaitOutcome;
            return result;
        }

        public static DeviceTaskResult FromException(DeviceSdkTask task, Exception exception, DateTime startedAt, DateTime completedAt)
        {
            var result = FromTask(task, false, "INTERNAL_ERROR", exception == null ? "Internal error." : exception.Message, DeviceConnectionStatus.Unknown, startedAt, completedAt);
            result.ExceptionType = exception?.GetType().Name ?? string.Empty;
            return result;
        }

        internal DeviceTaskResult CopyFrom(DeviceTaskResult source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (object.ReferenceEquals(this, source))
            {
                return this;
            }

            TaskId = source.TaskId;
            RequestId = source.RequestId;
            DeviceId = source.DeviceId;
            TaskType = source.TaskType;
            OperationName = source.OperationName;
            Success = source.Success;
            Code = source.Code;
            Message = source.Message;
            SdkErrorCode = source.SdkErrorCode;
            DeviceStatusAfter = source.DeviceStatusAfter;
            Retryable = source.Retryable;
            IsWaitOutcome = source.IsWaitOutcome;
            TaskStarted = source.TaskStarted;
            TaskCompleted = source.TaskCompleted;
            Data = source.Data;
            Errors = source.Errors;
            ExceptionType = source.ExceptionType;
            StartedAt = source.StartedAt;
            CompletedAt = source.CompletedAt;
            DurationMilliseconds = source.DurationMilliseconds;
            return this;
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
