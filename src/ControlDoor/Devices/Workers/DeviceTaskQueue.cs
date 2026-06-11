using System;
using System.Collections.Generic;
using System.Linq;
using ControlDoor.Devices.Tasks;

namespace ControlDoor.Devices.Workers
{
    public sealed class DeviceTaskQueue
    {
        private static readonly DeviceTaskPriority[] PriorityOrder =
        {
            DeviceTaskPriority.Critical,
            DeviceTaskPriority.High,
            DeviceTaskPriority.Normal,
            DeviceTaskPriority.Retry,
            DeviceTaskPriority.Low
        };

        private readonly Dictionary<DeviceTaskPriority, Queue<DeviceTaskQueueItem>> buckets = PriorityOrder.ToDictionary(priority => priority, priority => new Queue<DeviceTaskQueueItem>());
        private readonly int capacity;
        private readonly DeviceTaskQueuePolicy policy;
        private long nextSequence;
        private int count;
        private int highPriorityBurst;
        private long fairnessSelectionCount;
        private long coalescedTaskCount;

        public DeviceTaskQueue(int capacity, DeviceTaskQueuePolicy policy = null)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), "Queue capacity must be greater than 0.");
            }

            this.capacity = capacity;
            this.policy = policy ?? new DeviceTaskQueuePolicy();
        }

        public int Count => count;

        public int Capacity => capacity;

        public long FairnessSelectionCount => fairnessSelectionCount;

        public long CoalescedTaskCount => coalescedTaskCount;

        public bool TryEnqueue(DeviceSdkTask task, DateTime enqueuedAt, int effectiveTimeoutMilliseconds, out DeviceTaskQueueItem item)
        {
            item = null;
            if (policy.CoalesceBackgroundTasks && TryCoalesce(task))
            {
                coalescedTaskCount++;
                task.MarkRejected(DeviceTaskResult.Rejected(task, "COALESCED", "Background task was coalesced."));
                return true;
            }

            if (count >= capacity)
            {
                return false;
            }

            item = new DeviceTaskQueueItem(task, ++nextSequence, enqueuedAt);
            task.MarkQueued(enqueuedAt, item.Sequence, effectiveTimeoutMilliseconds);
            buckets[item.Task.Priority].Enqueue(item);
            count++;
            return true;
        }

        public bool TryDequeue(out DeviceTaskQueueItem item)
        {
            return TryDequeue(DateTime.Now, out item, out _);
        }

        public bool TryDequeue(DateTime now, out DeviceTaskQueueItem item, out DeviceQueueSelectionResult selection)
        {
            item = null;
            selection = null;
            if (count == 0)
            {
                return false;
            }

            var priority = SelectPriority(now, out var fairnessApplied);
            item = buckets[priority].Dequeue();
            count--;
            selection = new DeviceQueueSelectionResult(priority, fairnessApplied);
            if (fairnessApplied)
            {
                fairnessSelectionCount++;
                highPriorityBurst = 0;
            }
            else if (priority == DeviceTaskPriority.Critical || priority == DeviceTaskPriority.High || priority == DeviceTaskPriority.Normal)
            {
                highPriorityBurst++;
            }
            else
            {
                highPriorityBurst = 0;
            }

            return true;
        }

        public IReadOnlyList<DeviceTaskQueueItem> Drain()
        {
            var drained = buckets.Values.SelectMany(bucket => bucket).OrderBy(item => item.Sequence).ToList();
            foreach (var bucket in buckets.Values)
            {
                bucket.Clear();
            }

            count = 0;
            return drained;
        }

        public DateTime? GetOldestEnqueuedAt()
        {
            var oldest = buckets.Values
                .Where(bucket => bucket.Count > 0)
                .Select(bucket => bucket.Peek().EnqueuedAt)
                .OrderBy(value => value)
                .FirstOrDefault();
            return count == 0 ? (DateTime?)null : oldest;
        }

        public DevicePriorityQueueSnapshot GetPrioritySnapshot()
        {
            return new DevicePriorityQueueSnapshot(
                buckets.ToDictionary(item => item.Key, item => item.Value.Count),
                fairnessSelectionCount,
                coalescedTaskCount);
        }

        private DeviceTaskPriority SelectPriority(DateTime now, out bool fairnessApplied)
        {
            fairnessApplied = false;
            if (buckets[DeviceTaskPriority.Critical].Count > 0)
            {
                return DeviceTaskPriority.Critical;
            }

            var agedPriority = SelectAgedPriority(now);
            if (agedPriority.HasValue && highPriorityBurst >= policy.MaxHighPriorityBurst)
            {
                fairnessApplied = true;
                return agedPriority.Value;
            }

            foreach (var priority in PriorityOrder)
            {
                if (buckets[priority].Count > 0)
                {
                    return priority;
                }
            }

            return DeviceTaskPriority.Low;
        }

        private DeviceTaskPriority? SelectAgedPriority(DateTime now)
        {
            if (buckets[DeviceTaskPriority.Retry].Count > 0 &&
                (now - buckets[DeviceTaskPriority.Retry].Peek().EnqueuedAt).TotalMilliseconds >= policy.RetryAgingMilliseconds)
            {
                return DeviceTaskPriority.Retry;
            }

            if (buckets[DeviceTaskPriority.Low].Count > 0 &&
                (now - buckets[DeviceTaskPriority.Low].Peek().EnqueuedAt).TotalMilliseconds >= policy.LowPriorityAgingMilliseconds)
            {
                return DeviceTaskPriority.Low;
            }

            return null;
        }

        private bool TryCoalesce(DeviceSdkTask task)
        {
            if (task == null || task.Priority != DeviceTaskPriority.Low)
            {
                return false;
            }

            if (task.TaskType != DeviceTaskType.HealthCheck && task.TaskType != DeviceTaskType.ProbeCapabilities)
            {
                return false;
            }

            return buckets.Values
                .SelectMany(bucket => bucket)
                .Any(item => item.Task.DeviceId == task.DeviceId && item.Task.TaskType == task.TaskType && !item.Cancelled);
        }
    }
}
