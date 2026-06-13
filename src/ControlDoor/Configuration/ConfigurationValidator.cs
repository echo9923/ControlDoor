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

            settings.CameraAlarmDoorInterlock.Mappings = settings.CameraAlarmDoorInterlock.Mappings ?? new List<CameraAlarmDoorInterlockMapping>();

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
    }
}
