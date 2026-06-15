using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using ControlDoor.Configuration;

namespace ControlDoor.Runtime.Health.Checks
{
    public sealed class DeviceStoreHealthCheck : IHealthCheck
    {
        public string Name => "设备清单";

        public HealthCheckResult Run(HealthCheckContext context)
        {
            var settings = context.Settings ?? new AppSettings();
            settings.EnsureGroups();
            var devices = settings.Devices ?? new DeviceStoreOptions();
            if (devices.Items != null && devices.Items.Count > 0)
            {
                var inlineValidation = ValidateItems(devices.Items);
                if (inlineValidation != null)
                {
                    return HealthCheckResult.Failed(Name, inlineValidation);
                }

                return HealthCheckResult.Ok(Name, "使用 appsettings.json 内联设备清单，数量: " + devices.Items.Count + "。");
            }

            var filePath = Path.IsPathRooted(devices.FilePath)
                ? devices.FilePath
                : Path.Combine(context.RunDirectory, devices.FilePath ?? string.Empty);
            if (!File.Exists(filePath))
            {
                return HealthCheckResult.Failed(Name, "设备清单文件不存在: " + filePath);
            }

            try
            {
                var json = File.ReadAllText(filePath);
                var document = new JavaScriptSerializer().Deserialize<DeviceStoreDocument>(json);
                var items = document == null || document.devices == null ? new DeviceStoreItem[0] : document.devices;
                var validation = ValidateItems(items);
                if (validation != null)
                {
                    return HealthCheckResult.Failed(Name, validation);
                }

                var count = items.Length;
                return HealthCheckResult.Ok(Name, "设备清单文件可读取，设备数量: " + count + "。");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Failed(Name, "设备清单文件解析失败: " + ex.Message);
            }
        }

        private static string ValidateItems(IList<DeviceStoreItem> items)
        {
            items = items ?? new DeviceStoreItem[0];
            var seenIds = new HashSet<int>();
            var seenIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var prefix = "devices[" + i + "]";
                if (item == null)
                {
                    return prefix + " 不能为空。";
                }

                if (item.DeviceId <= 0)
                {
                    return prefix + ".deviceId 必须大于 0。";
                }

                if (!seenIds.Add(item.DeviceId))
                {
                    return prefix + ".deviceId=" + item.DeviceId + " 重复。";
                }

                if (string.IsNullOrWhiteSpace(item.IpAddress))
                {
                    return prefix + ".ipAddress 不能为空。";
                }

                if (!seenIps.Add(item.IpAddress.Trim()))
                {
                    return prefix + ".ipAddress=" + item.IpAddress.Trim() + " 重复。";
                }

                if (item.Types == null || item.Types.Count == 0)
                {
                    return prefix + ".types 不能为空（至少声明一个: Acs / FaceCapture / Camera）。";
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

                if (normalized.Count == 0)
                {
                    return prefix + ".types 不能为空（至少声明一个: Acs / FaceCapture / Camera）。";
                }
            }

            return null;
        }

        private static bool IsKnownDeviceType(string value)
        {
            return string.Equals(value, "Acs", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "FaceCapture", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Camera", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class DeviceStoreDocument
        {
            public DeviceStoreItem[] devices { get; set; }
        }
    }
}
