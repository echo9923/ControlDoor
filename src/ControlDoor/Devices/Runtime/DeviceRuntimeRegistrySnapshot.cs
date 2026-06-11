using System;
using System.Collections.Generic;
using System.Linq;

namespace ControlDoor.Devices.Runtime
{
    public sealed class DeviceRuntimeRegistrySnapshot
    {
        public DeviceRuntimeRegistrySnapshot(
            int deviceCount,
            int ipIndexCount,
            int sdkUserIdIndexCount,
            int alarmHandleIndexCount,
            int workerRouteIndexCount,
            int conflictCount,
            int deletedCount,
            IReadOnlyDictionary<DeviceConnectionStatus, int> statusCounts,
            DateTime createdAt)
        {
            DeviceCount = deviceCount;
            IpIndexCount = ipIndexCount;
            SdkUserIdIndexCount = sdkUserIdIndexCount;
            AlarmHandleIndexCount = alarmHandleIndexCount;
            WorkerRouteIndexCount = workerRouteIndexCount;
            ConflictCount = conflictCount;
            DeletedCount = deletedCount;
            StatusCounts = statusCounts == null
                ? new Dictionary<DeviceConnectionStatus, int>()
                : statusCounts.ToDictionary(item => item.Key, item => item.Value);
            CreatedAt = createdAt;
        }

        public int DeviceCount { get; private set; }

        public int IpIndexCount { get; private set; }

        public int SdkUserIdIndexCount { get; private set; }

        public int AlarmHandleIndexCount { get; private set; }

        public int WorkerRouteIndexCount { get; private set; }

        public int ConflictCount { get; private set; }

        public int DeletedCount { get; private set; }

        public IReadOnlyDictionary<DeviceConnectionStatus, int> StatusCounts { get; private set; }

        public DateTime CreatedAt { get; private set; }

        public int GetStatusCount(DeviceConnectionStatus status)
        {
            return StatusCounts.ContainsKey(status) ? StatusCounts[status] : 0;
        }

        public string StatusSummary()
        {
            return string.Join(",", StatusCounts.OrderBy(item => item.Key).Select(item => item.Key + "=" + item.Value));
        }
    }
}
