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
        private long droppedLowPriorityTaskCount;

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
                if (policy.DropLowPriorityWhenQueueFull && task.Priority == DeviceTaskPriority.Low)
                {
                    droppedLowPriorityTaskCount++;
                    task.MarkRejected(DeviceTaskResult.Rejected(task, "LOW_PRIORITY_DROPPED", "Low priority task was dropped because the queue is full."));
                }

                return false;
            }

            item = new DeviceTaskQueueItem(task, ++nextSequence, enqueuedAt);
            task.MarkQueued(enqueuedAt, item.Sequence, effectiveTimeoutMilliseconds);
            buckets[NormalizePriority(item.Task.Priority)].Enqueue(item);
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
            highPriorityBurst = 0;
            return drained;
        }

        public bool TryCancel(string taskId, string reason)
        {
            foreach (var item in buckets.Values.SelectMany(bucket => bucket))
            {
                if (item.Task.TaskId == taskId && !item.Cancelled)
                {
                    item.Cancel(reason);
                    return true;
                }
            }

            return false;
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

        public DevicePriorityQueueSnapshot GetPrioritySnapshot(DateTime? now = null)
        {
            var snapshotAt = now ?? DateTime.Now;
            return new DevicePriorityQueueSnapshot(
                buckets.ToDictionary(item => item.Key, item => item.Value.Count),
                buckets.ToDictionary(
                    item => item.Key,
                    item => item.Value.Count == 0 ? (long?)null : Math.Max(0, (long)(snapshotAt - item.Value.Peek().EnqueuedAt).TotalMilliseconds)),
                highPriorityBurst,
                fairnessSelectionCount,
                coalescedTaskCount,
                droppedLowPriorityTaskCount);
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

        private static DeviceTaskPriority NormalizePriority(DeviceTaskPriority priority)
        {
            return PriorityOrder.Contains(priority) ? priority : DeviceTaskPriority.Normal;
        }
    }
}
