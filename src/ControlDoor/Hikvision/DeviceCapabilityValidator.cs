using System.Collections.Generic;

namespace ControlDoor.Hikvision
{
    public static class DeviceCapabilityValidator
    {
        public static IList<DeviceCapability> GetMissingCapabilities(DeviceCapabilities capabilities, IEnumerable<DeviceCapability> required)
        {
            var missing = new List<DeviceCapability>();
            if (required == null)
            {
                return missing;
            }

            foreach (var capability in required)
            {
                if (!Supports(capabilities, capability))
                {
                    missing.Add(capability);
                }
            }

            return missing;
        }

        public static void ValidateCapabilities(DeviceCapabilities capabilities, IEnumerable<DeviceCapability> required)
        {
            var missing = GetMissingCapabilities(capabilities, required);
            if (missing.Count > 0)
            {
                throw new DeviceGatewayException("ValidateCapabilities", SdkError.FromCode(23, "设备缺少能力: " + string.Join(",", missing)));
            }
        }

        public static bool Supports(DeviceCapabilities capabilities, DeviceCapability required)
        {
            if (capabilities == null || !capabilities.Known)
            {
                return false;
            }

            switch (required)
            {
                case DeviceCapability.Acs:
                    return capabilities.SupportsAcs;
                case DeviceCapability.Alarm:
                    return capabilities.SupportsAlarm;
                case DeviceCapability.PersonConfig:
                    return capabilities.SupportsPersonConfig;
                case DeviceCapability.FaceConfig:
                    return capabilities.SupportsFaceConfig;
                case DeviceCapability.FaceCapture:
                    return capabilities.SupportsFaceCapture;
                case DeviceCapability.HistoryEventQuery:
                    return capabilities.SupportsHistoryEventQuery;
                case DeviceCapability.Isapi:
                    return capabilities.SupportsIsapi;
                case DeviceCapability.Aiop:
                    return capabilities.SupportsAiop;
                default:
                    return false;
            }
        }
    }
}
