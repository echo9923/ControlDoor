using System;
using ControlDoor.Devices.Tasks;

namespace ControlDoor.Devices.Workers
{
    public sealed class DelayedTaskDispatchResult
    {
        private DelayedTaskDispatchResult(
            DelayedDeviceTask delayedTask,
            DelayedTaskDispatchStatus status,
            string code,
            string message,
            DateTime dispatchedAt,
            string taskId,
            int? workerIndex)
        {
            DelayedTaskId = delayedTask?.DelayedTaskId ?? string.Empty;
            TaskKey = delayedTask?.TaskKey ?? string.Empty;
            DeviceId = delayedTask?.DeviceId ?? 0;
            TaskType = delayedTask?.TaskType;
            Priority = delayedTask?.Priority;
            DueAt = delayedTask?.DueAt;
            Status = status;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
            DispatchedAt = dispatchedAt;
            TaskId = taskId ?? string.Empty;
            WorkerIndex = workerIndex;
        }

        public string DelayedTaskId { get; }

        public string TaskKey { get; }

        public int DeviceId { get; }

        public DeviceTaskType? TaskType { get; }

        public DeviceTaskPriority? Priority { get; }

        public DateTime? DueAt { get; }

        public DateTime DispatchedAt { get; }

        public DelayedTaskDispatchStatus Status { get; }

        public bool Success => Status == DelayedTaskDispatchStatus.Dispatched;

        public string Code { get; }

        public string Message { get; }

        public string TaskId { get; }

        public int? WorkerIndex { get; }

        public static DelayedTaskDispatchResult FromSubmission(DelayedDeviceTask delayedTask, DeviceSdkTask task, DeviceTaskSubmissionResult submission, DateTime dispatchedAt)
        {
            if (submission != null && submission.Accepted)
            {
                return new DelayedTaskDispatchResult(delayedTask, DelayedTaskDispatchStatus.Dispatched, "DISPATCHED", "Delayed task dispatched.", dispatchedAt, task.TaskId, submission.WorkerIndex);
            }

            var code = submission?.ImmediateResult?.Code ?? "DISPATCH_FAILED";
            var message = submission?.ImmediateResult?.Message ?? "Delayed task dispatch failed.";
            var status = code == "QUEUE_FULL" ? DelayedTaskDispatchStatus.QueueFull : DelayedTaskDispatchStatus.Rejected;
            return new DelayedTaskDispatchResult(delayedTask, status, code, message, dispatchedAt, task?.TaskId, submission?.WorkerIndex);
        }

        public static DelayedTaskDispatchResult Cancelled(DelayedDeviceTask delayedTask, DateTime dispatchedAt)
        {
            return new DelayedTaskDispatchResult(delayedTask, DelayedTaskDispatchStatus.Cancelled, "CANCELLED", delayedTask?.CancellationReason ?? "Delayed task cancelled.", dispatchedAt, string.Empty, null);
        }

        public static DelayedTaskDispatchResult Expired(DelayedDeviceTask delayedTask, DateTime dispatchedAt)
        {
            return new DelayedTaskDispatchResult(delayedTask, DelayedTaskDispatchStatus.Expired, "EXPIRED", "Delayed task expired before dispatch.", dispatchedAt, string.Empty, null);
        }

        public static DelayedTaskDispatchResult FactoryError(DelayedDeviceTask delayedTask, Exception exception, DateTime dispatchedAt)
        {
            return new DelayedTaskDispatchResult(delayedTask, DelayedTaskDispatchStatus.FactoryError, "INTERNAL_ERROR", exception == null ? "Delayed task factory failed." : exception.Message, dispatchedAt, string.Empty, null);
        }

        public static DelayedTaskDispatchResult SchedulerStopped(DelayedDeviceTask delayedTask, DateTime dispatchedAt)
        {
            return new DelayedTaskDispatchResult(delayedTask, DelayedTaskDispatchStatus.SchedulerStopped, "SCHEDULER_STOPPED", "Delayed scheduler is stopped.", dispatchedAt, string.Empty, null);
        }
    }
}
