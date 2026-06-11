using System;
using ControlDoor.Devices.Tasks;

namespace ControlDoor.Devices.Workers
{
    public sealed class DeviceWorkerRuntimeSnapshot
    {
        public int WorkerIndex { get; set; }

        public DeviceWorkerStatus Status { get; set; }

        public DateTime? StartedAt { get; set; }

        public DateTime? StoppedAt { get; set; }

        public string CurrentTaskId { get; set; }

        public int? CurrentDeviceId { get; set; }

        public DeviceTaskType? CurrentTaskType { get; set; }

        public DateTime? CurrentTaskStartedAt { get; set; }

        public int QueueLength { get; set; }

        public long CompletedTaskCount { get; set; }

        public long FailedTaskCount { get; set; }

        public long CancelledTaskCount { get; set; }

        public DateTime? LastTaskCompletedAt { get; set; }

        public string LastError { get; set; }

        public long? OldestQueuedTaskAgeMilliseconds { get; set; }
    }
}
