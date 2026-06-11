using System;

namespace ControlDoor.Devices.Workers
{
    public sealed class DelayedTaskScheduleResult
    {
        private DelayedTaskScheduleResult(
            DelayedTaskScheduleStatus status,
            string code,
            string message,
            DelayedDeviceTask task,
            DelayedDeviceTask replacedTask)
        {
            Status = status;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
            Task = task;
            ReplacedTask = replacedTask;
            DelayedTaskId = task?.DelayedTaskId ?? string.Empty;
            TaskKey = task?.TaskKey ?? string.Empty;
            DueAt = task?.DueAt;
        }

        public DelayedTaskScheduleStatus Status { get; }

        public bool Accepted => Status == DelayedTaskScheduleStatus.Accepted || Status == DelayedTaskScheduleStatus.Coalesced;

        public bool Coalesced => Status == DelayedTaskScheduleStatus.Coalesced;

        public string Code { get; }

        public string Message { get; }

        public string DelayedTaskId { get; }

        public string TaskKey { get; }

        public DateTime? DueAt { get; }

        public DelayedDeviceTask Task { get; }

        public DelayedDeviceTask ReplacedTask { get; }

        public static DelayedTaskScheduleResult AcceptedResult(DelayedDeviceTask task)
        {
            return new DelayedTaskScheduleResult(DelayedTaskScheduleStatus.Accepted, "ACCEPTED", "Delayed task scheduled.", task, null);
        }

        public static DelayedTaskScheduleResult CoalescedResult(DelayedDeviceTask retainedTask, DelayedDeviceTask replacedTask, string message)
        {
            return new DelayedTaskScheduleResult(DelayedTaskScheduleStatus.Coalesced, "COALESCED", message, retainedTask, replacedTask);
        }

        public static DelayedTaskScheduleResult Rejected(DelayedDeviceTask task, string code, string message)
        {
            return new DelayedTaskScheduleResult(DelayedTaskScheduleStatus.Rejected, code, message, task, null);
        }

        public static DelayedTaskScheduleResult Disabled(DelayedDeviceTask task)
        {
            return new DelayedTaskScheduleResult(DelayedTaskScheduleStatus.Disabled, "SCHEDULER_DISABLED", "Delayed scheduler is disabled.", task, null);
        }

        public static DelayedTaskScheduleResult Stopped(DelayedDeviceTask task)
        {
            return new DelayedTaskScheduleResult(DelayedTaskScheduleStatus.Stopped, "SCHEDULER_STOPPED", "Delayed scheduler is stopped.", task, null);
        }
    }
}
