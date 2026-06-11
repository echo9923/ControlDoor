using System;

namespace ControlDoor.Devices.Runtime
{
    public sealed class DeviceRuntimeSnapshot
    {
        public DeviceRuntimeSnapshot(
            int deviceId,
            string deviceName,
            string ipAddress,
            int port,
            bool enabled,
            DeviceConnectionStatus status,
            bool isDeleting,
            int? sdkUserId,
            int? alarmHandle,
            string serialNumber,
            DeviceCapabilities capabilities,
            DateTime? lastLoginAt,
            DateTime? lastLogoutAt,
            DateTime? lastCheckedAt,
            DateTime? lastUsedAt,
            DeviceRuntimeError lastError,
            ReconnectState reconnect,
            DateTime updatedAt,
            DeviceQueueInfo queueInfo)
        {
            DeviceId = deviceId;
            DeviceName = deviceName ?? string.Empty;
            IpAddress = ipAddress ?? string.Empty;
            Port = port;
            Enabled = enabled;
            Status = status;
            IsDeleting = isDeleting;
            SdkUserId = sdkUserId;
            AlarmHandle = alarmHandle;
            SerialNumber = serialNumber ?? string.Empty;
            Capabilities = capabilities == null ? DeviceCapabilities.Unknown() : capabilities.Clone();
            LastLoginAt = lastLoginAt;
            LastLogoutAt = lastLogoutAt;
            LastCheckedAt = lastCheckedAt;
            LastUsedAt = lastUsedAt;
            LastError = lastError == null ? null : lastError.Clone();
            Reconnect = reconnect == null ? ReconnectState.New() : reconnect.Clone();
            UpdatedAt = updatedAt;
            QueueInfo = queueInfo == null ? null : queueInfo.Clone();
        }

        public int DeviceId { get; private set; }

        public string DeviceName { get; private set; }

        public string IpAddress { get; private set; }

        public int Port { get; private set; }

        public bool Enabled { get; private set; }

        public DeviceConnectionStatus Status { get; private set; }

        public string StatusMessage => Status.ToString();

        public bool IsConnected => Status == DeviceConnectionStatus.Online || Status == DeviceConnectionStatus.Degraded;

        public bool IsDeleting { get; private set; }

        public int? SdkUserId { get; private set; }

        public int? AlarmHandle { get; private set; }

        public string SerialNumber { get; private set; }

        public DeviceCapabilities Capabilities { get; private set; }

        public DateTime? LastLoginAt { get; private set; }

        public DateTime? LastLogoutAt { get; private set; }

        public DateTime? LastCheckedAt { get; private set; }

        public DateTime? LastUsedAt { get; private set; }

        public DeviceRuntimeError LastError { get; private set; }

        public string LastErrorCode => LastError == null ? null : LastError.Code;

        public string LastErrorMessage => LastError == null ? null : LastError.Message;

        public ReconnectState Reconnect { get; private set; }

        public DateTime UpdatedAt { get; private set; }

        public DeviceQueueInfo QueueInfo { get; private set; }
    }
}
