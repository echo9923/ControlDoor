using System;

namespace ControlDoor.Permissions
{
    public sealed class DeviceOperationRetryScanResult
    {
        public string RequestId { get; set; } = string.Empty;

        public int Due { get; set; }

        public int Submitted { get; set; }

        public int InFlightSkipped { get; set; }

        public int OfflineDeferred { get; set; }

        public int Terminal { get; set; }

        public int EmptyDeleted { get; set; }

        public int Succeeded { get; set; }

        public int Failed { get; set; }

        public int CleanupDeleted { get; set; }

        public long ElapsedMs { get; set; }

        public DateTime ScannedAt { get; set; }
    }
}
