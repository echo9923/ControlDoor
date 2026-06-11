using System;
using System.Collections.Generic;
using System.Linq;

namespace ControlDoor.Devices.Runtime
{
    public sealed class DeviceWorkerRouter
    {
        private readonly object gate = new object();
        private readonly Dictionary<int, DeviceWorkerRoute> routes = new Dictionary<int, DeviceWorkerRoute>();
        private readonly int workerCount;
        private int routeFailureCount;

        public DeviceWorkerRouter(DeviceWorkerRoutingOptions options = null)
        {
            options = options ?? new DeviceWorkerRoutingOptions();
            if (options.WorkerCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options.WorkerCount), "WorkerCount must be greater than 0.");
            }

            workerCount = options.WorkerCount;
        }

        public int WorkerCount => workerCount;

        public DeviceWorkerRoute Route(int deviceId, DateTime? assignedAt = null)
        {
            if (deviceId <= 0)
            {
                lock (gate)
                {
                    routeFailureCount++;
                }

                throw new ArgumentOutOfRangeException(nameof(deviceId), "DeviceId must be greater than 0.");
            }

            var workerIndex = CalculateWorkerIndex(deviceId, workerCount);
            return new DeviceWorkerRoute(deviceId, workerIndex, workerCount, assignedAt ?? DateTime.Now);
        }

        public DeviceWorkerRoute Assign(int deviceId, DateTime? assignedAt = null)
        {
            var route = Route(deviceId, assignedAt);
            lock (gate)
            {
                routes[deviceId] = route;
            }

            return route;
        }

        public DeviceWorkerRoute TryGetAssignedRoute(int deviceId)
        {
            lock (gate)
            {
                return routes.TryGetValue(deviceId, out var route) ? route : null;
            }
        }

        public bool Remove(int deviceId)
        {
            lock (gate)
            {
                return routes.Remove(deviceId);
            }
        }

        public DeviceWorkerRoutingSnapshot GetSnapshot(DateTime? now = null)
        {
            lock (gate)
            {
                var counts = routes.Values
                    .GroupBy(route => route.WorkerIndex)
                    .ToDictionary(group => group.Key, group => group.Count());
                return new DeviceWorkerRoutingSnapshot(workerCount, routeFailureCount, counts, now ?? DateTime.Now);
            }
        }

        public static int CalculateWorkerIndex(int deviceId, int workerCount)
        {
            if (deviceId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(deviceId), "DeviceId must be greater than 0.");
            }

            if (workerCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(workerCount), "WorkerCount must be greater than 0.");
            }

            return (int)(Math.Abs((long)deviceId) % workerCount);
        }
    }
}
