using System;

namespace ControlDoor.Hikvision
{
    public sealed class DeviceCapabilities
    {
        public bool Known { get; set; }

        public bool SupportsAcs { get; set; }

        public bool SupportsAlarm { get; set; }

        public bool SupportsPersonConfig { get; set; }

        public bool SupportsFaceConfig { get; set; }

        public bool SupportsFaceCapture { get; set; }

        public bool SupportsHistoryEventQuery { get; set; }

        public bool SupportsIsapi { get; set; }

        public bool SupportsAiop { get; set; }

        public int DoorCount { get; set; }

        public int ChannelCount { get; set; }

        public string Model { get; set; }

        public string FirmwareVersion { get; set; }

        public string RawCapabilities { get; set; }

        public DateTime? LastCheckedAt { get; set; }
    }
}
