using System.Collections.Generic;

namespace ControlDoor.Devices.Management
{
    public sealed class DeviceLoadSummary
    {
        public int LoadedCount { get; set; }

        public int SkippedCount { get; set; }

        public int ConflictCount { get; set; }

        public int InvalidCount { get; set; }

        public IList<DeviceRecord> LoadedDevices { get; } = new List<DeviceRecord>();

        public IList<string> Warnings { get; } = new List<string>();

        public bool Success => InvalidCount == 0 && ConflictCount == 0;
    }
}
