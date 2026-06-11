using System.Collections.Generic;
using System.Linq;
using ControlDoor.Devices.Tasks;

namespace ControlDoor.Devices.Workers
{
    public sealed class DevicePriorityQueueSnapshot
    {
        public DevicePriorityQueueSnapshot(IReadOnlyDictionary<DeviceTaskPriority, int> queueLengthByPriority, long fairnessSelectionCount, long coalescedTaskCount)
        {
            QueueLengthByPriority = queueLengthByPriority == null
                ? new Dictionary<DeviceTaskPriority, int>()
                : queueLengthByPriority.ToDictionary(item => item.Key, item => item.Value);
            FairnessSelectionCount = fairnessSelectionCount;
            CoalescedTaskCount = coalescedTaskCount;
        }

        public IReadOnlyDictionary<DeviceTaskPriority, int> QueueLengthByPriority { get; private set; }

        public long FairnessSelectionCount { get; private set; }

        public long CoalescedTaskCount { get; private set; }

        public int GetQueueLength(DeviceTaskPriority priority)
        {
            return QueueLengthByPriority.ContainsKey(priority) ? QueueLengthByPriority[priority] : 0;
        }
    }
}
