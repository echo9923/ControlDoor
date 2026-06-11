using System;
using System.Collections.Generic;
using System.Linq;
using ControlDoor.Devices.Tasks;

namespace ControlDoor.Devices.Workers
{
    public sealed class DeviceTaskQueue
    {
        private readonly Queue<DeviceTaskQueueItem> items = new Queue<DeviceTaskQueueItem>();
        private readonly int capacity;
        private long nextSequence;

        public DeviceTaskQueue(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), "Queue capacity must be greater than 0.");
            }

            this.capacity = capacity;
        }

        public int Count => items.Count;

        public int Capacity => capacity;

        public bool TryEnqueue(DeviceSdkTask task, DateTime enqueuedAt, int effectiveTimeoutMilliseconds, out DeviceTaskQueueItem item)
        {
            item = null;
            if (items.Count >= capacity)
            {
                return false;
            }

            item = new DeviceTaskQueueItem(task, ++nextSequence, enqueuedAt);
            task.MarkQueued(enqueuedAt, item.Sequence, effectiveTimeoutMilliseconds);
            items.Enqueue(item);
            return true;
        }

        public bool TryDequeue(out DeviceTaskQueueItem item)
        {
            if (items.Count == 0)
            {
                item = null;
                return false;
            }

            item = items.Dequeue();
            return true;
        }

        public IReadOnlyList<DeviceTaskQueueItem> Drain()
        {
            var drained = items.ToList();
            items.Clear();
            return drained;
        }

        public DateTime? GetOldestEnqueuedAt()
        {
            return items.Count == 0 ? (DateTime?)null : items.Peek().EnqueuedAt;
        }
    }
}
