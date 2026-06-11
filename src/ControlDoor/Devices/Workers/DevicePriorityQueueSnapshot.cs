using System.Collections.Generic;
using System.Linq;
using ControlDoor.Devices.Tasks;

namespace ControlDoor.Devices.Workers
{
    public sealed class DevicePriorityQueueSnapshot
    {
        public DevicePriorityQueueSnapshot(
            IReadOnlyDictionary<DeviceTaskPriority, int> queueLengthByPriority,
            IReadOnlyDictionary<DeviceTaskPriority, long?> oldestTaskAgeMillisecondsByPriority,
            int highPriorityBurstCount,
            long fairnessSelectionCount,
            long coalescedTaskCount,
            long droppedLowPriorityTaskCount)
        {
            QueueLengthByPriority = queueLengthByPriority == null
                ? new Dictionary<DeviceTaskPriority, int>()
                : queueLengthByPriority.ToDictionary(item => item.Key, item => item.Value);
            OldestTaskAgeMillisecondsByPriority = oldestTaskAgeMillisecondsByPriority == null
                ? new Dictionary<DeviceTaskPriority, long?>()
                : oldestTaskAgeMillisecondsByPriority.ToDictionary(item => item.Key, item => item.Value);
            HighPriorityBurstCount = highPriorityBurstCount;
            FairnessSelectionCount = fairnessSelectionCount;
            CoalescedTaskCount = coalescedTaskCount;
            DroppedLowPriorityTaskCount = droppedLowPriorityTaskCount;
        }

        public IReadOnlyDictionary<DeviceTaskPriority, int> QueueLengthByPriority { get; private set; }

        public IReadOnlyDictionary<DeviceTaskPriority, long?> OldestTaskAgeMillisecondsByPriority { get; private set; }

        public int HighPriorityBurstCount { get; private set; }

        public long FairnessSelectionCount { get; private set; }

        public long CoalescedTaskCount { get; private set; }

        public long DroppedLowPriorityTaskCount { get; private set; }

        public int GetQueueLength(DeviceTaskPriority priority)
        {
            return QueueLengthByPriority.ContainsKey(priority) ? QueueLengthByPriority[priority] : 0;
        }

        public long? GetOldestTaskAgeMilliseconds(DeviceTaskPriority priority)
        {
            return OldestTaskAgeMillisecondsByPriority.ContainsKey(priority) ? OldestTaskAgeMillisecondsByPriority[priority] : null;
        }
    }
}
