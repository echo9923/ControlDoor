using System;
using System.Collections.Generic;

namespace ControlDoor.Configuration
{
    public sealed class ConfigurationValidator
    {
        public ConfigurationValidationResult Validate(AppSettings settings)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            settings = settings ?? new AppSettings();
            settings.EnsureGroups();

            if (string.IsNullOrWhiteSpace(settings.Database.ConnectionString))
            {
                errors.Add("Database.ConnectionString 不能为空。");
            }

            settings.Service.GrpcListenPort = RangeOrDefault(
                settings.Service.GrpcListenPort,
                1,
                65535,
                5001,
                "Service.GrpcListenPort",
                warnings);

            settings.Database.CommandTimeoutSeconds = MinimumOrDefault(
                settings.Database.CommandTimeoutSeconds,
                1,
                30,
                "Database.CommandTimeoutSeconds",
                warnings);

            settings.Database.StartupRetryCount = MinimumOrDefault(
                settings.Database.StartupRetryCount,
                1,
                10,
                "Database.StartupRetryCount",
                warnings);

            settings.Database.StartupRetryIntervalSeconds = MinimumOrDefault(
                settings.Database.StartupRetryIntervalSeconds,
                1,
                60,
                "Database.StartupRetryIntervalSeconds",
                warnings);

            settings.Logging.LogDirectory = StringOrDefault(
                settings.Logging.LogDirectory,
                "logs",
                "Logging.LogDirectory",
                warnings);

            settings.Logging.RetentionDays = MinimumOrDefault(
                settings.Logging.RetentionDays,
                1,
                30,
                "Logging.RetentionDays",
                warnings);

            if (!string.Equals(settings.Logging.GrpcPayloadLogMode, "Summary", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(settings.Logging.GrpcPayloadLogMode, "Full", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("Logging.GrpcPayloadLogMode 非法，已回退为 Summary。");
                settings.Logging.GrpcPayloadLogMode = "Summary";
            }
            else if (string.Equals(settings.Logging.GrpcPayloadLogMode, "Full", StringComparison.OrdinalIgnoreCase))
            {
                settings.Logging.GrpcPayloadLogMode = "Full";
            }
            else
            {
                settings.Logging.GrpcPayloadLogMode = "Summary";
            }

            settings.DeviceSdkDispatcher.WorkerCount = MinimumOrDefault(
                settings.DeviceSdkDispatcher.WorkerCount,
                1,
                4,
                "DeviceSdkDispatcher.WorkerCount",
                warnings);

            settings.DeviceSdkDispatcher.QueueCapacity = MinimumOrDefault(
                settings.DeviceSdkDispatcher.QueueCapacity,
                100,
                1000,
                "DeviceSdkDispatcher.QueueCapacity",
                warnings);

            settings.DeviceSdkDispatcher.DefaultTaskTimeoutMs = MinimumOrDefault(
                settings.DeviceSdkDispatcher.DefaultTaskTimeoutMs,
                1000,
                30000,
                "DeviceSdkDispatcher.DefaultTaskTimeoutMs",
                warnings);

            settings.DeviceConnection.StatusCheckIntervalMs = MinimumOrDefault(
                settings.DeviceConnection.StatusCheckIntervalMs,
                5000,
                30000,
                "DeviceConnection.StatusCheckIntervalMs",
                warnings);

            settings.DeviceConnection.LoginTimeoutMs = MinimumOrDefault(
                settings.DeviceConnection.LoginTimeoutMs,
                1000,
                15000,
                "DeviceConnection.LoginTimeoutMs",
                warnings);

            settings.DeviceConnection.LogoutTimeoutMs = MinimumOrDefault(
                settings.DeviceConnection.LogoutTimeoutMs,
                1000,
                10000,
                "DeviceConnection.LogoutTimeoutMs",
                warnings);

            if (!string.Equals(settings.HikvisionSdk.Platform, "x86", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(settings.HikvisionSdk.Platform, "x64", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(settings.HikvisionSdk.Platform, "AnyCPU", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("HikvisionSdk.Platform 非法，已回退为 x64。");
                settings.HikvisionSdk.Platform = "x64";
            }
            else if (string.Equals(settings.HikvisionSdk.Platform, "x86", StringComparison.OrdinalIgnoreCase))
            {
                settings.HikvisionSdk.Platform = "x86";
            }
            else if (string.Equals(settings.HikvisionSdk.Platform, "AnyCPU", StringComparison.OrdinalIgnoreCase))
            {
                settings.HikvisionSdk.Platform = "AnyCPU";
            }
            else
            {
                settings.HikvisionSdk.Platform = "x64";
            }

            settings.HikvisionSdk.DllDirectory = StringOrDefault(
                settings.HikvisionSdk.DllDirectory,
                "sdk\\Hikvision",
                "HikvisionSdk.DllDirectory",
                warnings);

            settings.HikvisionSdk.SdkLogDirectory = StringOrDefault(
                settings.HikvisionSdk.SdkLogDirectory,
                "logs\\sdk",
                "HikvisionSdk.SdkLogDirectory",
                warnings);

            settings.DeviceOperationRetry.ScanIntervalSeconds = MinimumOrDefault(
                settings.DeviceOperationRetry.ScanIntervalSeconds,
                5,
                30,
                "DeviceOperationRetry.ScanIntervalSeconds",
                warnings);

            settings.DeviceOperationRetry.InitialRetryDelaySeconds = MinimumOrDefault(
                settings.DeviceOperationRetry.InitialRetryDelaySeconds,
                1,
                60,
                "DeviceOperationRetry.InitialRetryDelaySeconds",
                warnings);

            settings.DeviceOperationRetry.MaxRetryDelaySeconds = MinimumOrDefault(
                settings.DeviceOperationRetry.MaxRetryDelaySeconds,
                1,
                3600,
                "DeviceOperationRetry.MaxRetryDelaySeconds",
                warnings);

            if (settings.DeviceOperationRetry.MaxRetryDelaySeconds < settings.DeviceOperationRetry.InitialRetryDelaySeconds)
            {
                warnings.Add("DeviceOperationRetry.MaxRetryDelaySeconds 小于 InitialRetryDelaySeconds，已回退为 3600。");
                settings.DeviceOperationRetry.MaxRetryDelaySeconds = 3600;
            }

            settings.DeviceOperationRetry.MaxRetryAttempts = MinimumOrDefault(
                settings.DeviceOperationRetry.MaxRetryAttempts,
                1,
                10,
                "DeviceOperationRetry.MaxRetryAttempts",
                warnings);

            settings.DeviceOperationRetry.FailureRetentionDays = MinimumOrDefault(
                settings.DeviceOperationRetry.FailureRetentionDays,
                1,
                settings.DeviceOperationRetry.TerminalRetentionDays > 0 ? settings.DeviceOperationRetry.TerminalRetentionDays : 7,
                "DeviceOperationRetry.FailureRetentionDays",
                warnings);

            settings.DeviceOperationRetry.BatchSize = MinimumOrDefault(
                settings.DeviceOperationRetry.BatchSize,
                1,
                100,
                "DeviceOperationRetry.BatchSize",
                warnings);

            settings.DeviceOperationRetry.TerminalRetentionDays = MinimumOrDefault(
                settings.DeviceOperationRetry.TerminalRetentionDays,
                1,
                settings.DeviceOperationRetry.FailureRetentionDays,
                "DeviceOperationRetry.TerminalRetentionDays",
                warnings);

            settings.FaceEventLogging.SnapshotRootDirectory = StringOrDefault(
                settings.FaceEventLogging.SnapshotRootDirectory,
                "snapshots",
                "FaceEventLogging.SnapshotRootDirectory",
                warnings);

            settings.FaceEventLogging.ExcludedDeviceIds = settings.FaceEventLogging.ExcludedDeviceIds ?? new List<int>();
            settings.FaceEventLogging.ExcludedDeviceIps = settings.FaceEventLogging.ExcludedDeviceIps ?? new List<string>();
            if (settings.FaceEventLogging.AlarmDeployType != 0)
            {
                warnings.Add("FaceEventLogging.AlarmDeployType 非法，阶段 7 已回退为 0。");
                settings.FaceEventLogging.AlarmDeployType = 0;
            }

            settings.FaceEventLogging.QueueCapacity = MinimumOrDefault(
                settings.FaceEventLogging.QueueCapacity,
                1,
                2000,
                "FaceEventLogging.QueueCapacity",
                warnings);

            settings.FaceEnrollment.MaxFaceImageBytes = MinimumOrDefault(
                settings.FaceEnrollment.MaxFaceImageBytes,
                1,
                204800,
                "FaceEnrollment.MaxFaceImageBytes",
                warnings);

            settings.FaceEnrollment.CaptureTimeoutSeconds = MinimumOrDefault(
                settings.FaceEnrollment.CaptureTimeoutSeconds,
                1,
                60,
                "FaceEnrollment.CaptureTimeoutSeconds",
                warnings);

            settings.FaceEnrollment.TaskRetentionMinutes = MinimumOrDefault(
                settings.FaceEnrollment.TaskRetentionMinutes,
                1,
                30,
                "FaceEnrollment.TaskRetentionMinutes",
                warnings);

            ValidateCameraAlarmDoorInterlock(settings, warnings);

            return new ConfigurationValidationResult(settings, errors, warnings);
        }

        private static int RangeOrDefault(int value, int minimum, int maximum, int defaultValue, string name, ICollection<string> warnings)
        {
            if (value < minimum || value > maximum)
            {
                warnings.Add(name + " 非法，已回退为 " + defaultValue + "。");
                return defaultValue;
            }

            return value;
        }

        private static int MinimumOrDefault(int value, int minimum, int defaultValue, string name, ICollection<string> warnings)
        {
            if (value < minimum)
            {
                warnings.Add(name + " 非法，已回退为 " + defaultValue + "。");
                return defaultValue;
            }

            return value;
        }

        private static string StringOrDefault(string value, string defaultValue, string name, ICollection<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                warnings.Add(name + " 为空，已回退为 " + defaultValue + "。");
                return defaultValue;
            }

            return value.Trim();
        }

        private static void ValidateCameraAlarmDoorInterlock(AppSettings settings, ICollection<string> warnings)
        {
            var section = settings.CameraAlarmDoorInterlock;
            section.Mappings = section.Mappings ?? new List<CameraAlarmDoorInterlockMapping>();

            section.WindowSeconds = MinimumOrDefault(section.WindowSeconds, 1, 5, "CameraAlarmDoorInterlock.WindowSeconds", warnings);
            section.RestoreRetryIntervalMs = MinimumOrDefault(section.RestoreRetryIntervalMs, 100, 1000, "CameraAlarmDoorInterlock.RestoreRetryIntervalMs", warnings);
            section.DoorControlSdkLockTimeoutMs = MinimumOrDefault(section.DoorControlSdkLockTimeoutMs, 1000, 5000, "CameraAlarmDoorInterlock.DoorControlSdkLockTimeoutMs", warnings);

            for (var i = 0; i < section.Mappings.Count; i++)
            {
                var mapping = section.Mappings[i];
                if (mapping == null)
                {
                    continue;
                }

                if (mapping.Camera == null)
                {
                    mapping.Camera = new InterlockCamera();
                }

                if (mapping.DoorDevice == null)
                {
                    mapping.DoorDevice = new InterlockDoorDevice();
                }

                mapping.DoorNos = mapping.DoorNos ?? new List<int>();

                if (!mapping.Enabled)
                {
                    continue;
                }

                var cameraHasIp = !string.IsNullOrWhiteSpace(mapping.Camera.Ip);
                var cameraHasId = mapping.Camera.Id > 0;
                if (!cameraHasIp && !cameraHasId)
                {
                    warnings.Add("CameraAlarmDoorInterlock.Mappings[" + i + "].Camera 缺少有效 IP 或 Id，该映射将被忽略。");
                    mapping.Enabled = false;
                    continue;
                }

                var doorHasIp = !string.IsNullOrWhiteSpace(mapping.DoorDevice.Ip);
                var doorHasId = mapping.DoorDevice.Id > 0;
                if (!doorHasIp && !doorHasId)
                {
                    warnings.Add("CameraAlarmDoorInterlock.Mappings[" + i + "].DoorDevice 缺少有效 IP 或 Id，该映射将被忽略。");
                    mapping.Enabled = false;
                    continue;
                }

                if (mapping.Camera.Ip != null)
                {
                    mapping.Camera.Ip = mapping.Camera.Ip.Trim();
                }

                if (mapping.DoorDevice.Ip != null)
                {
                    mapping.DoorDevice.Ip = mapping.DoorDevice.Ip.Trim();
                }

                if (mapping.DoorNos.Count == 0)
                {
                    mapping.DoorNos = new List<int> { 1 };
                    continue;
                }

                var validDoorNos = new List<int>();
                foreach (var doorNo in mapping.DoorNos)
                {
                    if (doorNo > 0)
                    {
                        validDoorNos.Add(doorNo);
                    }
                    else
                    {
                        warnings.Add("CameraAlarmDoorInterlock.Mappings[" + i + "].DoorNos 含非正整数门号 " + doorNo + "，已剔除。");
                    }
                }

                mapping.DoorNos = validDoorNos.Count > 0 ? validDoorNos : new List<int> { 1 };
            }

            if (section.Enabled)
            {
                var hasValidMapping = false;
                foreach (var mapping in section.Mappings)
                {
                    if (mapping != null && mapping.Enabled && mapping.DoorNos.Count > 0)
                    {
                        hasValidMapping = true;
                        break;
                    }
                }

                if (!hasValidMapping)
                {
                    warnings.Add("CameraAlarmDoorInterlock.Enabled=true 但无有效映射，阶段 9 模块将自禁用。");
                }
            }
        }
    }
}
