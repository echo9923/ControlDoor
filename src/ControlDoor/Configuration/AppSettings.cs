using System;
using System.Collections.Generic;

namespace ControlDoor.Configuration
{
    public sealed class AppSettings
    {
        public ServiceOptions Service { get; set; } = new ServiceOptions();

        public DatabaseOptions Database { get; set; } = new DatabaseOptions();

        public LoggingOptions Logging { get; set; } = new LoggingOptions();

        public DeviceSdkDispatcherOptions DeviceSdkDispatcher { get; set; } = new DeviceSdkDispatcherOptions();

        public DeviceConnectionOptions DeviceConnection { get; set; } = new DeviceConnectionOptions();

        public DeviceOperationRetryOptions DeviceOperationRetry { get; set; } = new DeviceOperationRetryOptions();

        public FaceEventLoggingOptions FaceEventLogging { get; set; } = new FaceEventLoggingOptions();

        public FaceEnrollmentOptions FaceEnrollment { get; set; } = new FaceEnrollmentOptions();

        public CameraAlarmDoorInterlockOptions CameraAlarmDoorInterlock { get; set; } = new CameraAlarmDoorInterlockOptions();

        public void EnsureGroups()
        {
            Service = Service ?? new ServiceOptions();
            Database = Database ?? new DatabaseOptions();
            Logging = Logging ?? new LoggingOptions();
            DeviceSdkDispatcher = DeviceSdkDispatcher ?? new DeviceSdkDispatcherOptions();
            DeviceConnection = DeviceConnection ?? new DeviceConnectionOptions();
            DeviceOperationRetry = DeviceOperationRetry ?? new DeviceOperationRetryOptions();
            FaceEventLogging = FaceEventLogging ?? new FaceEventLoggingOptions();
            FaceEnrollment = FaceEnrollment ?? new FaceEnrollmentOptions();
            CameraAlarmDoorInterlock = CameraAlarmDoorInterlock ?? new CameraAlarmDoorInterlockOptions();
        }
    }

    public sealed class ServiceOptions
    {
        public int GrpcListenPort { get; set; } = 5001;

        public string GrpcManagementApiKey { get; set; } = string.Empty;
    }

    public sealed class DatabaseOptions
    {
        public string ConnectionString { get; set; } = string.Empty;

        public int CommandTimeoutSeconds { get; set; } = 30;

        public int StartupRetryCount { get; set; } = 10;

        public int StartupRetryIntervalSeconds { get; set; } = 60;
    }

    public sealed class LoggingOptions
    {
        public string LogDirectory { get; set; } = "logs";

        public int RetentionDays { get; set; } = 30;

        public bool EnableGrpcPayloadLogging { get; set; }

        public string GrpcPayloadLogMode { get; set; } = "Summary";

        public bool IncludeCredentialFields { get; set; } = true;

        public bool IncludeFaceImageBase64 { get; set; }

        public bool EnableSdkTrace { get; set; } = true;
    }

    public sealed class DeviceSdkDispatcherOptions
    {
        public int WorkerCount { get; set; } = 4;

        public int QueueCapacity { get; set; } = 1000;

        public int DefaultTaskTimeoutMs { get; set; } = 30000;

        public bool HighPriorityQueueEnabled { get; set; } = true;
    }

    public sealed class DeviceConnectionOptions
    {
        public int StatusCheckIntervalMs { get; set; } = 30000;

        public int LoginTimeoutMs { get; set; } = 15000;

        public int LogoutTimeoutMs { get; set; } = 10000;

        public int ReconnectBaseDelayMs { get; set; } = 5000;

        public int ReconnectMaxDelayMs { get; set; } = 300000;
    }

    public sealed class DeviceOperationRetryOptions
    {
        public int ScanIntervalSeconds { get; set; } = 30;

        public int InitialRetryDelaySeconds { get; set; } = 60;

        public int MaxRetryDelaySeconds { get; set; } = 3600;

        public int MaxRetryAttempts { get; set; } = 10;

        public int FailureRetentionDays { get; set; } = 7;

        public int BatchSize { get; set; } = 100;

        public bool RetryImmediatelyOnNewIntent { get; set; } = true;

        public int TerminalRetentionDays { get; set; } = 30;
    }

    public sealed class FaceEventLoggingOptions
    {
        public bool Enabled { get; set; } = true;

        public string SnapshotRootDirectory { get; set; } = "snapshots";

        public List<int> ExcludedDeviceIds { get; set; } = new List<int>();

        public bool EnableHistoryCompensation { get; set; } = true;
    }

    public sealed class FaceEnrollmentOptions
    {
        public int MaxFaceImageBytes { get; set; } = 204800;

        public int CaptureTimeoutSeconds { get; set; } = 60;

        public int TaskRetentionMinutes { get; set; } = 30;
    }

    public sealed class CameraAlarmDoorInterlockOptions
    {
        public bool Enabled { get; set; }

        public List<CameraAlarmDoorInterlockMapping> Mappings { get; set; } = new List<CameraAlarmDoorInterlockMapping>();
    }

    public sealed class CameraAlarmDoorInterlockMapping
    {
        public string CameraIpAddress { get; set; } = string.Empty;

        public int DoorDeviceId { get; set; }

        public int DoorNo { get; set; } = 1;

        public int CloseSeconds { get; set; } = 5;
    }
}
