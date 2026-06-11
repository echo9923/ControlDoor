using System;
using ControlDoor.Devices.Tasks;

namespace ControlDoor.Devices.Workers
{
    public sealed class DeviceTaskTimeoutSnapshot
    {
        public int WorkerIndex { get; set; }

        public string TaskId { get; set; }

        public int? DeviceId { get; set; }

        public DeviceTaskType? TaskType { get; set; }

        public string OperationName { get; set; }

        public DateTime? StartedAt { get; set; }

        public long CurrentTaskDurationMilliseconds { get; set; }

        public bool IsLongRunning { get; set; }

        public int LongRunningWarningCount { get; set; }
    }
}
