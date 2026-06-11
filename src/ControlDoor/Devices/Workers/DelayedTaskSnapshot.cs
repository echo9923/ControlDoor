using System;
using System.Collections.Generic;
using System.Linq;
using ControlDoor.Devices.Tasks;

namespace ControlDoor.Devices.Workers
{
    public sealed class DelayedTaskSnapshot
    {
        public DelayedTaskSnapshot(
            int delayedTaskCount,
            DateTime? earliestDueAt,
            int dueTaskCount,
            IReadOnlyDictionary<string, int> taskCountBySource,
            IReadOnlyDictionary<DeviceTaskPriority, int> taskCountByPriority,
            long dispatchSuccessCount,
            long dispatchFailureCount,
            long cancelledDelayedTaskCount,
            long expiredDelayedTaskCount,
            long coalescedDelayedTaskCount,
            long rejectedDelayedTaskCount)
        {
            DelayedTaskCount = delayedTaskCount;
            EarliestDueAt = earliestDueAt;
            DueTaskCount = dueTaskCount;
            TaskCountBySource = taskCountBySource == null
                ? new Dictionary<string, int>()
                : taskCountBySource.ToDictionary(item => item.Key, item => item.Value);
            TaskCountByPriority = taskCountByPriority == null
                ? new Dictionary<DeviceTaskPriority, int>()
                : taskCountByPriority.ToDictionary(item => item.Key, item => item.Value);
            DispatchSuccessCount = dispatchSuccessCount;
            DispatchFailureCount = dispatchFailureCount;
            CancelledDelayedTaskCount = cancelledDelayedTaskCount;
            ExpiredDelayedTaskCount = expiredDelayedTaskCount;
            CoalescedDelayedTaskCount = coalescedDelayedTaskCount;
            RejectedDelayedTaskCount = rejectedDelayedTaskCount;
        }

        public int DelayedTaskCount { get; }

        public DateTime? EarliestDueAt { get; }

        public int DueTaskCount { get; }

        public IReadOnlyDictionary<string, int> TaskCountBySource { get; }

        public IReadOnlyDictionary<DeviceTaskPriority, int> TaskCountByPriority { get; }

        public long DispatchSuccessCount { get; }

        public long DispatchFailureCount { get; }

        public long CancelledDelayedTaskCount { get; }

        public long ExpiredDelayedTaskCount { get; }

        public long CoalescedDelayedTaskCount { get; }

        public long RejectedDelayedTaskCount { get; }

        public int GetSourceCount(string source)
        {
            var key = string.IsNullOrWhiteSpace(source) ? "Unknown" : source;
            return TaskCountBySource.ContainsKey(key) ? TaskCountBySource[key] : 0;
        }

        public int GetPriorityCount(DeviceTaskPriority priority)
        {
            return TaskCountByPriority.ContainsKey(priority) ? TaskCountByPriority[priority] : 0;
        }
    }
}
