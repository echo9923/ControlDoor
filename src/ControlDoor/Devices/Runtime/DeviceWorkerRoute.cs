using System;

namespace ControlDoor.Devices.Runtime
{
    public sealed class DeviceWorkerRoute
    {
        public DeviceWorkerRoute(int deviceId, int workerIndex, int workerCount, DateTime assignedAt)
        {
            DeviceId = deviceId;
            WorkerIndex = workerIndex;
            WorkerCount = workerCount;
            AssignedAt = assignedAt;
        }

        public int DeviceId { get; }

        public int WorkerIndex { get; }

        public int WorkerCount { get; }

        public string RouteKey => DeviceId.ToString();

        public DateTime AssignedAt { get; }
    }
}
