using System;

namespace ControlDoor.Devices.Runtime
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

        public DateTime? LastCheckedAt { get; set; }

        public string LastError { get; set; }

        public static DeviceCapabilities Unknown()
        {
            return new DeviceCapabilities();
        }

        public DeviceCapabilities Clone()
        {
            return new DeviceCapabilities
            {
                Known = Known,
                SupportsAcs = SupportsAcs,
                SupportsAlarm = SupportsAlarm,
                SupportsPersonConfig = SupportsPersonConfig,
                SupportsFaceConfig = SupportsFaceConfig,
                SupportsFaceCapture = SupportsFaceCapture,
                SupportsHistoryEventQuery = SupportsHistoryEventQuery,
                SupportsIsapi = SupportsIsapi,
                LastCheckedAt = LastCheckedAt,
                LastError = LastError
            };
        }
    }
}
