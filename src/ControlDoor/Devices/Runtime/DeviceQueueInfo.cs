using System;

namespace ControlDoor.Devices.Runtime
{
    public sealed class DeviceQueueInfo
    {
        public int WorkerIndex { get; set; }

        public int QueuedTaskCount { get; set; }

        public string CurrentTaskId { get; set; }

        public string CurrentTaskOperationName { get; set; }

        public DateTime? CurrentTaskStartedAt { get; set; }

        public string LastTaskId { get; set; }

        public DateTime? LastTaskCompletedAt { get; set; }

        public DeviceQueueInfo Clone()
        {
            return new DeviceQueueInfo
            {
                WorkerIndex = WorkerIndex,
                QueuedTaskCount = QueuedTaskCount,
                CurrentTaskId = CurrentTaskId,
                CurrentTaskOperationName = CurrentTaskOperationName,
                CurrentTaskStartedAt = CurrentTaskStartedAt,
                LastTaskId = LastTaskId,
                LastTaskCompletedAt = LastTaskCompletedAt
            };
        }
    }
}
