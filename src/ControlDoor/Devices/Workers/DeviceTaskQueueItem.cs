using System;
using ControlDoor.Devices.Tasks;

namespace ControlDoor.Devices.Workers
{
    public sealed class DeviceTaskQueueItem
    {
        public DeviceTaskQueueItem(DeviceSdkTask task, long sequence, DateTime enqueuedAt)
        {
            Task = task ?? throw new ArgumentNullException(nameof(task));
            Sequence = sequence;
            EnqueuedAt = enqueuedAt;
        }

        public DeviceSdkTask Task { get; private set; }

        public long Sequence { get; private set; }

        public DateTime EnqueuedAt { get; private set; }

        public bool Cancelled { get; private set; }

        public string CancellationReason { get; private set; }

        public void Cancel()
        {
            Cancel("Task was cancelled.");
        }

        public void Cancel(string reason)
        {
            Cancelled = true;
            CancellationReason = reason ?? string.Empty;
        }
    }
}
