using System;
using ControlDoor.Devices.Tasks;

namespace ControlDoor.Devices.Workers
{
    public sealed class DelayedDeviceTask
    {
        public DelayedDeviceTask(
            int deviceId,
            DeviceTaskType taskType,
            DeviceTaskPriority priority,
            DateTime dueAt,
            string taskKey,
            string source,
            Func<DeviceSdkTask> taskFactory,
            DateTime? createdAt = null)
        {
            if (deviceId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(deviceId), "DeviceId must be greater than zero.");
            }

            DeviceId = deviceId;
            TaskType = taskType;
            Priority = priority;
            DueAt = dueAt;
            CreatedAt = createdAt ?? DateTime.Now;
            TaskKey = string.IsNullOrWhiteSpace(taskKey) ? BuildDefaultTaskKey(deviceId, taskType) : taskKey.Trim();
            Source = string.IsNullOrWhiteSpace(source) ? "Unknown" : source.Trim();
            TaskFactory = taskFactory ?? throw new ArgumentNullException(nameof(taskFactory));
            DelayedTaskId = Guid.NewGuid().ToString("N");
            MaxAttempts = 0;
            BackoffPolicy = RetryBackoffPolicy.Fixed(TimeSpan.Zero);
            MergeMode = DelayedTaskMergeMode.KeepEarliest;
        }

        public string DelayedTaskId { get; }

        public string TaskKey { get; }

        public int DeviceId { get; }

        public DeviceTaskType TaskType { get; }

        public DeviceTaskPriority Priority { get; }

        public DateTime DueAt { get; private set; }

        public DateTime CreatedAt { get; }

        public DateTime? ExpiresAt { get; set; }

        public int Attempt { get; set; }

        public int MaxAttempts { get; set; }

        public RetryBackoffPolicy BackoffPolicy { get; set; }

        public Func<DeviceSdkTask> TaskFactory { get; }

        public string CancellationReason { get; private set; } = string.Empty;

        public string Source { get; }

        public DelayedTaskMergeMode MergeMode { get; set; }

        public bool Cancelled { get; private set; }

        public bool IsExpired(DateTime now)
        {
            return ExpiresAt.HasValue && ExpiresAt.Value <= now;
        }

        public void MoveDueAt(DateTime dueAt)
        {
            DueAt = dueAt;
        }

        public void Cancel(string reason)
        {
            Cancelled = true;
            CancellationReason = string.IsNullOrWhiteSpace(reason) ? "Delayed task cancelled." : reason;
        }

        public DeviceSdkTask CreateTask()
        {
            var task = TaskFactory();
            if (task == null)
            {
                throw new InvalidOperationException("Delayed task factory returned null.");
            }

            if (task.DeviceId != DeviceId)
            {
                throw new InvalidOperationException("Delayed task factory returned a task for a different device.");
            }

            if (task.TaskType != TaskType)
            {
                throw new InvalidOperationException("Delayed task factory returned a task with a different task type.");
            }

            task.Priority = Priority;
            return task;
        }

        private static string BuildDefaultTaskKey(int deviceId, DeviceTaskType taskType)
        {
            return deviceId + ":" + taskType;
        }
    }
}
