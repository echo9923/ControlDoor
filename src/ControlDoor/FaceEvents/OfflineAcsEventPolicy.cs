using System;
using System.Linq;
using ControlDoor.Configuration;
using ControlDoor.Devices.Runtime;

namespace ControlDoor.FaceEvents
{
    public sealed class OfflineAcsEventPolicy
    {
        private readonly FaceEventLoggingOptions options;

        public OfflineAcsEventPolicy(FaceEventLoggingOptions options)
        {
            this.options = options ?? new FaceEventLoggingOptions();
        }

        public bool IsOfflineUpload(int? currentEventFlag)
        {
            return currentEventFlag == 2;
        }

        public bool ShouldIgnore(DeviceRuntimeSnapshot snapshot, string deviceIp, int? currentEventFlag, out string reason)
        {
            reason = null;
            if (snapshot != null && options.ExcludedDeviceIds != null && options.ExcludedDeviceIds.Contains(snapshot.DeviceId))
            {
                reason = "EXCLUDED_DEVICE_ID";
                return true;
            }

            if (!string.IsNullOrWhiteSpace(deviceIp) &&
                options.ExcludedDeviceIps != null &&
                options.ExcludedDeviceIps.Any(item => string.Equals(item, deviceIp, StringComparison.OrdinalIgnoreCase)))
            {
                reason = "EXCLUDED_DEVICE_IP";
                return true;
            }

            if (IsOfflineUpload(currentEventFlag) && !options.OfflineCompensationEnabled)
            {
                reason = "OFFLINE_COMPENSATION_DISABLED";
                return true;
            }

            return false;
        }
    }
}
