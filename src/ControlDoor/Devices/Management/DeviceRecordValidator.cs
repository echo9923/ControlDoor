using System;
using System.Collections.Generic;
using ControlDoor.Configuration;

namespace ControlDoor.Devices.Management
{
    internal static class DeviceRecordValidator
    {
        public static DeviceRecordValidationResult Validate(DeviceRecord record)
        {
            if (record == null)
            {
                return DeviceRecordValidationResult.Failed("device record is required.");
            }

            if (record.DeviceId <= 0)
            {
                return DeviceRecordValidationResult.Failed("deviceId must be greater than 0.");
            }

            if (string.IsNullOrWhiteSpace(record.DeviceName))
            {
                return DeviceRecordValidationResult.Failed("deviceName must not be empty.");
            }

            if (string.IsNullOrWhiteSpace(record.IpAddress))
            {
                return DeviceRecordValidationResult.Failed("ipAddress must not be empty.");
            }

            if (record.Port <= 0 || record.Port > 65535)
            {
                return DeviceRecordValidationResult.Failed("port must be in range 1-65535.");
            }

            if (record.Types == null || record.Types.Count == 0)
            {
                return DeviceRecordValidationResult.Failed("types must not be empty.");
            }

            if (string.IsNullOrWhiteSpace(record.Password))
            {
                return DeviceRecordValidationResult.Failed("password must not be empty.");
            }

            record.IpAddress = record.IpAddress.Trim();
            record.Username = string.IsNullOrWhiteSpace(record.Username) ? "admin" : record.Username.Trim();
            record.DeviceName = record.DeviceName.Trim();
            return DeviceRecordValidationResult.Ok();
        }

        public static string ValidateStoreItem(DeviceStoreItem item, string prefix)
        {
            if (item == null)
            {
                return prefix + " 不能为空。";
            }

            if (item.DeviceId <= 0)
            {
                return prefix + ".deviceId 必须大于 0。";
            }

            if (string.IsNullOrWhiteSpace(item.Name))
            {
                return prefix + ".name 不能为空。";
            }

            if (string.IsNullOrWhiteSpace(item.IpAddress))
            {
                return prefix + ".ipAddress 不能为空。";
            }

            if (item.Port <= 0 || item.Port > 65535)
            {
                return prefix + ".port 必须在 1-65535 范围内。";
            }

            if (item.Types == null || item.Types.Count == 0)
            {
                return prefix + ".types 不能为空（至少声明一个: Acs / FaceCapture / Camera）。";
            }

            if (string.IsNullOrWhiteSpace(item.Password))
            {
                return prefix + ".password 不能为空。";
            }

            var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in item.Types)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var value = raw.Trim();
                if (!IsKnownDeviceType(value))
                {
                    return prefix + ".types 含非法值 \"" + raw + "\"（合法值: Acs / FaceCapture / Camera）。";
                }

                normalized.Add(value);
            }

            return normalized.Count == 0 ? prefix + ".types 不能为空（至少声明一个: Acs / FaceCapture / Camera）。" : null;
        }

        private static bool IsKnownDeviceType(string value)
        {
            return string.Equals(value, "Acs", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "FaceCapture", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Camera", StringComparison.OrdinalIgnoreCase);
        }

        internal sealed class DeviceRecordValidationResult
        {
            public bool Success { get; set; }

            public string Message { get; set; }

            public static DeviceRecordValidationResult Ok()
            {
                return new DeviceRecordValidationResult { Success = true };
            }

            public static DeviceRecordValidationResult Failed(string message)
            {
                return new DeviceRecordValidationResult { Success = false, Message = message };
            }
        }
    }
}
