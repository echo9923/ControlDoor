using System;
using System.Collections.Generic;
using System.Linq;

namespace ControlDoor.Devices.Workers
{
    public sealed class DeviceWorkerWatchdog
    {
        private readonly Dictionary<string, int> warningCountsByTaskId = new Dictionary<string, int>();
        private readonly int longRunningWarningMilliseconds;

        public DeviceWorkerWatchdog(int longRunningWarningMilliseconds)
        {
            this.longRunningWarningMilliseconds = longRunningWarningMilliseconds <= 0 ? 60000 : longRunningWarningMilliseconds;
        }

        public IReadOnlyList<DeviceTaskTimeoutSnapshot> Scan(IEnumerable<DeviceWorkerRuntimeSnapshot> workers, DateTime now)
        {
            return (workers ?? Enumerable.Empty<DeviceWorkerRuntimeSnapshot>())
                .Where(worker => !string.IsNullOrEmpty(worker.CurrentTaskId) && worker.CurrentTaskStartedAt.HasValue)
                .Select(worker => BuildSnapshot(worker, now))
                .ToList();
        }

        private DeviceTaskTimeoutSnapshot BuildSnapshot(DeviceWorkerRuntimeSnapshot worker, DateTime now)
        {
            var duration = Math.Max(0, (long)(now - worker.CurrentTaskStartedAt.Value).TotalMilliseconds);
            var longRunning = duration >= longRunningWarningMilliseconds;
            var warningCount = 0;
            if (longRunning)
            {
                warningCountsByTaskId.TryGetValue(worker.CurrentTaskId, out warningCount);
                warningCount++;
                warningCountsByTaskId[worker.CurrentTaskId] = warningCount;
            }

            return new DeviceTaskTimeoutSnapshot
            {
                WorkerIndex = worker.WorkerIndex,
                TaskId = worker.CurrentTaskId,
                DeviceId = worker.CurrentDeviceId,
                TaskType = worker.CurrentTaskType,
                OperationName = worker.CurrentTaskType.HasValue ? worker.CurrentTaskType.Value.ToString() : string.Empty,
                StartedAt = worker.CurrentTaskStartedAt,
                CurrentTaskDurationMilliseconds = duration,
                IsLongRunning = longRunning,
                LongRunningWarningCount = warningCount
            };
        }
    }
}
