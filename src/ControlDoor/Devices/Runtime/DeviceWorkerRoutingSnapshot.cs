using System;
using System.Collections.Generic;
using System.Linq;

namespace ControlDoor.Devices.Runtime
{
    public sealed class DeviceWorkerRoutingSnapshot
    {
        public DeviceWorkerRoutingSnapshot(int workerCount, int routeFailureCount, IReadOnlyDictionary<int, int> deviceCountByWorker, DateTime createdAt)
        {
            WorkerCount = workerCount;
            RouteFailureCount = routeFailureCount;
            DeviceCountByWorker = deviceCountByWorker == null
                ? new Dictionary<int, int>()
                : deviceCountByWorker.ToDictionary(item => item.Key, item => item.Value);
            CreatedAt = createdAt;
        }

        public int WorkerCount { get; private set; }

        public int RouteFailureCount { get; private set; }

        public IReadOnlyDictionary<int, int> DeviceCountByWorker { get; private set; }

        public DateTime CreatedAt { get; private set; }

        public int GetDeviceCount(int workerIndex)
        {
            return DeviceCountByWorker.ContainsKey(workerIndex) ? DeviceCountByWorker[workerIndex] : 0;
        }
    }
}
