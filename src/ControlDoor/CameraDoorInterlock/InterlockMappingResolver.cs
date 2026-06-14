using System;
using System.Collections.Generic;
using System.Linq;
using ControlDoor.Configuration;
using ControlDoor.Devices.Runtime;
using ControlDoor.Observability;

namespace ControlDoor.CameraDoorInterlock
{
    /// <summary>
    /// 将 CameraAlarmDoorInterlock 配置解析为摄像头标识与门禁目标（task03）。
    /// 摄像头优先按 IP 识别，IP 为空时按 Id；门禁设备同样优先 IP、Id 兜底。
    /// 门禁 deviceId 在运行时按 IP 反查 DeviceRuntimeRegistry 并缓存（设备可能在阶段 9 启动后才注册）。
    /// </summary>
    public sealed class InterlockMappingResolver
    {
        private readonly object gate = new object();
        private readonly CameraAlarmDoorInterlockOptions options;
        private readonly DeviceRuntimeRegistry registry;
        private readonly ServiceLogger logger;

        private readonly Dictionary<string, string> cameraIpIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, string> cameraIdIndex = new Dictionary<int, string>();
        private readonly Dictionary<string, List<MappingEntry>> cameraMappings = new Dictionary<string, List<MappingEntry>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> doorDeviceIdByIp = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> configurationErrors = new List<string>();

        public InterlockMappingResolver(CameraAlarmDoorInterlockOptions options, DeviceRuntimeRegistry registry, ServiceLogger logger = null)
        {
            this.options = options ?? new CameraAlarmDoorInterlockOptions();
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.logger = logger;
            BuildIndexes();
        }

        public bool IsEnabled => options.Enabled;

        public bool HasValidMapping
        {
            get
            {
                lock (gate)
                {
                    return cameraMappings.Count > 0;
                }
            }
        }

        public IReadOnlyList<string> ConfigurationErrors
        {
            get
            {
                lock (gate)
                {
                    return configurationErrors.ToList();
                }
            }
        }

        /// <summary>
        /// 按 task02 优先级识别来源摄像头：IP 命中 → UserId 反查设备 IP 命中 → 序列号命中。
        /// </summary>
        public bool TryIdentifyCamera(string ip, int? userId, int? alarmHandle, string serial, out string cameraKey)
        {
            cameraKey = null;

            var normalizedIp = NormalizeIp(ip);
            if (!string.IsNullOrEmpty(normalizedIp))
            {
                lock (gate)
                {
                    if (cameraIpIndex.TryGetValue(normalizedIp, out var key))
                    {
                        cameraKey = key;
                        return true;
                    }
                }
            }

            if (registry != null)
            {
                DeviceRuntimeSnapshot snapshot = null;
                if (userId.HasValue && userId.Value >= 0)
                {
                    var lookup = registry.TryGetBySdkUserId(userId.Value);
                    if (lookup.Found)
                    {
                        snapshot = lookup.Snapshot;
                    }
                }

                if (snapshot == null && alarmHandle.HasValue && alarmHandle.Value >= 0)
                {
                    var lookup = registry.TryGetByAlarmHandle(alarmHandle.Value);
                    if (lookup.Found)
                    {
                        snapshot = lookup.Snapshot;
                    }
                }

                if (snapshot != null)
                {
                    var snapshotIp = NormalizeIp(snapshot.IpAddress);
                    if (!string.IsNullOrEmpty(snapshotIp))
                    {
                        lock (gate)
                        {
                            if (cameraIpIndex.TryGetValue(snapshotIp, out var key))
                            {
                                cameraKey = key;
                                return true;
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(serial) && !string.IsNullOrWhiteSpace(snapshot.SerialNumber) &&
                        string.Equals(serial.Trim(), snapshot.SerialNumber.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        lock (gate)
                        {
                            if (cameraIdIndex.TryGetValue(snapshot.DeviceId, out var key))
                            {
                                cameraKey = key;
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 解析摄像头对应的全部门禁目标（已解析 deviceId）。未注册的门禁设备目标会被跳过并记录。
        /// </summary>
        public IReadOnlyList<DoorTarget> ResolveTargets(string cameraKey)
        {
            var targets = new List<DoorTarget>();
            if (string.IsNullOrEmpty(cameraKey))
            {
                return targets;
            }

            List<MappingEntry> entries;
            lock (gate)
            {
                if (!cameraMappings.TryGetValue(cameraKey, out entries) || entries == null)
                {
                    return targets;
                }

                entries = entries.ToList();
            }

            foreach (var entry in entries)
            {
                var doorDeviceId = ResolveDoorDeviceId(entry);
                if (doorDeviceId <= 0)
                {
                    logger?.Warn("CameraDoorInterlock", "门禁目标无法解析设备 ID，已跳过。", new LogFields
                    {
                        Extra =
                        {
                            ["cameraKey"] = cameraKey,
                            ["doorIp"] = entry.DoorIp ?? string.Empty,
                            ["doorId"] = entry.DoorId.ToString()
                        }
                    });
                    continue;
                }

                foreach (var doorNo in entry.DoorNos)
                {
                    targets.Add(new DoorTarget
                    {
                        DoorDeviceId = doorDeviceId,
                        DoorDeviceIp = entry.DoorIp ?? string.Empty,
                        DoorNo = doorNo,
                        TargetKey = doorDeviceId + ":" + doorNo,
                        MappingId = entry.MappingId
                    });
                }
            }

            return targets;
        }

        private int ResolveDoorDeviceId(MappingEntry entry)
        {
            if (entry.DoorId > 0 && string.IsNullOrEmpty(entry.DoorIp))
            {
                return entry.DoorId;
            }

            var doorIp = NormalizeIp(entry.DoorIp);
            if (string.IsNullOrEmpty(doorIp))
            {
                return entry.DoorId > 0 ? entry.DoorId : 0;
            }

            lock (gate)
            {
                if (doorDeviceIdByIp.TryGetValue(doorIp, out var cached))
                {
                    return cached;
                }
            }

            var lookup = registry.TryGetByIpAddress(doorIp);
            if (lookup.Found && lookup.Snapshot != null)
            {
                lock (gate)
                {
                    doorDeviceIdByIp[doorIp] = lookup.Snapshot.DeviceId;
                }

                return lookup.Snapshot.DeviceId;
            }

            return 0;
        }

        private void BuildIndexes()
        {
            if (options.Mappings == null)
            {
                return;
            }

            for (var i = 0; i < options.Mappings.Count; i++)
            {
                var mapping = options.Mappings[i];
                if (mapping == null || !mapping.Enabled)
                {
                    continue;
                }

                var cameraIp = NormalizeIp(mapping.Camera == null ? null : mapping.Camera.Ip);
                var cameraId = mapping.Camera == null ? 0 : mapping.Camera.Id;
                var cameraKey = !string.IsNullOrEmpty(cameraIp) ? cameraIp : (cameraId > 0 ? "id:" + cameraId : null);
                if (cameraKey == null)
                {
                    configurationErrors.Add("CameraAlarmDoorInterlock.Mappings[" + i + "] 摄像头缺少有效 IP 或 Id。");
                    continue;
                }

                var doorIp = NormalizeIp(mapping.DoorDevice == null ? null : mapping.DoorDevice.Ip);
                var doorId = mapping.DoorDevice == null ? 0 : mapping.DoorDevice.Id;
                if (string.IsNullOrEmpty(doorIp) && doorId <= 0)
                {
                    configurationErrors.Add("CameraAlarmDoorInterlock.Mappings[" + i + "] 门禁设备缺少有效 IP 或 Id。");
                    continue;
                }

                var doorNos = NormalizeDoorNos(mapping.DoorNos);
                if (doorNos.Count == 0)
                {
                    configurationErrors.Add("CameraAlarmDoorInterlock.Mappings[" + i + "] 门号为空。");
                    continue;
                }

                var entry = new MappingEntry
                {
                    CameraKey = cameraKey,
                    DoorIp = doorIp,
                    DoorId = doorId,
                    DoorNos = doorNos,
                    MappingId = "mapping[" + i + "]"
                };

                if (!string.IsNullOrEmpty(cameraIp))
                {
                    cameraIpIndex[cameraIp] = cameraKey;
                }

                if (cameraId > 0)
                {
                    cameraIdIndex[cameraId] = cameraKey;
                }

                List<MappingEntry> list;
                lock (gate)
                {
                    if (!cameraMappings.TryGetValue(cameraKey, out list))
                    {
                        list = new List<MappingEntry>();
                        cameraMappings[cameraKey] = list;
                    }

                    list.Add(entry);
                }
            }

            if (options.Enabled && cameraMappings.Count == 0)
            {
                logger?.Error("CameraDoorInterlock", "阶段 9 已启用但无有效摄像头→门禁映射，模块将自禁用。", null);
            }
        }

        private static List<int> NormalizeDoorNos(List<int> doorNos)
        {
            if (doorNos == null || doorNos.Count == 0)
            {
                return new List<int> { 1 };
            }

            var result = new List<int>();
            foreach (var doorNo in doorNos)
            {
                if (doorNo > 0 && !result.Contains(doorNo))
                {
                    result.Add(doorNo);
                }
            }

            return result.Count == 0 ? new List<int> { 1 } : result;
        }

        private static string NormalizeIp(string ip)
        {
            return string.IsNullOrWhiteSpace(ip) ? string.Empty : ip.Trim();
        }

        private sealed class MappingEntry
        {
            public string CameraKey { get; set; }

            public string DoorIp { get; set; }

            public int DoorId { get; set; }

            public List<int> DoorNos { get; set; }

            public string MappingId { get; set; }
        }
    }
}
