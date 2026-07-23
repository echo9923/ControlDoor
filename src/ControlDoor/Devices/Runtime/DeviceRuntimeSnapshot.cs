using System;
using System.Collections.Generic;
using System.Linq;
using ControlDoor.Devices.Management;

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
            int? staleAlarmHandle,
            bool alarmManuallyDisarmed,
            string serialNumber,
            DeviceCapabilities capabilities,
            DateTime? lastLoginAt,
            DateTime? lastLogoutAt,
            DateTime? lastCheckedAt,
            DeviceRuntimeError lastError,
            ReconnectState reconnect,
            DateTime updatedAt,
            DeviceQueueInfo queueInfo,
            IEnumerable<DeviceType> types = null,
            string description = null,
            IEnumerable<int> pendingSdkLogoutUserIds = null,
            IEnumerable<int> pendingAlarmHandles = null,
            long runtimeVersion = 0,
            string deletingLeaseId = null)
        {
            DeviceId = deviceId;
            DeviceName = deviceName ?? string.Empty;
            Description = description ?? string.Empty;
            IpAddress = ipAddress ?? string.Empty;
            Port = port;
            Enabled = enabled;
            Status = status;
            IsDeleting = isDeleting;
            SdkUserId = sdkUserId;
            AlarmHandle = alarmHandle;
            StaleAlarmHandle = staleAlarmHandle;
            AlarmManuallyDisarmed = alarmManuallyDisarmed;
            SerialNumber = serialNumber ?? string.Empty;
            Capabilities = capabilities == null ? DeviceCapabilities.Unknown() : capabilities.Clone();
            LastLoginAt = lastLoginAt;
            LastLogoutAt = lastLogoutAt;
            LastCheckedAt = lastCheckedAt;
            LastError = lastError == null ? null : lastError.Clone();
            Reconnect = reconnect == null ? ReconnectState.New() : reconnect.Clone();
            UpdatedAt = updatedAt;
            QueueInfo = queueInfo == null ? null : queueInfo.Clone();
            Types = (types ?? Enumerable.Empty<DeviceType>()).ToList().AsReadOnly();
            PendingSdkLogoutUserIds = (pendingSdkLogoutUserIds ?? Enumerable.Empty<int>()).Distinct().ToList().AsReadOnly();
            PendingAlarmHandles = (pendingAlarmHandles ?? Enumerable.Empty<int>()).Distinct().ToList().AsReadOnly();
            RuntimeVersion = runtimeVersion;
            DeletingLeaseId = deletingLeaseId;
        }

        public int DeviceId { get; private set; }

        public string DeviceName { get; private set; }

        public string Description { get; private set; }

        public string IpAddress { get; private set; }

        public int Port { get; private set; }

        public bool Enabled { get; private set; }

        public DeviceConnectionStatus Status { get; private set; }

        public string StatusMessage => Status.ToString();

        public bool IsConnected => Status == DeviceConnectionStatus.Online || Status == DeviceConnectionStatus.Degraded;

        public bool IsDeleting { get; private set; }

        public int? SdkUserId { get; private set; }

        public int? AlarmHandle { get; private set; }

        public int? StaleAlarmHandle { get; private set; }

        public bool AlarmManuallyDisarmed { get; private set; }

        public string SerialNumber { get; private set; }

        public DeviceCapabilities Capabilities { get; private set; }

        public DateTime? LastLoginAt { get; private set; }

        public DateTime? LastLogoutAt { get; private set; }

        public DateTime? LastCheckedAt { get; private set; }

        public DeviceRuntimeError LastError { get; private set; }

        public string LastErrorCode => LastError == null ? null : LastError.Code;

        public string LastErrorMessage => LastError == null ? null : LastError.Message;

        public ReconnectState Reconnect { get; private set; }

        public DateTime UpdatedAt { get; private set; }

        public DeviceQueueInfo QueueInfo { get; private set; }

        // 声明态设备类型，来自设备清单；不可变快照，供消费方按角色筛选。
        public IReadOnlyList<DeviceType> Types { get; private set; }

        public IReadOnlyList<int> PendingSdkLogoutUserIds { get; private set; }

        public IReadOnlyList<int> PendingAlarmHandles { get; private set; }

        public long RuntimeVersion { get; private set; }

        public string DeletingLeaseId { get; private set; }
    }
}
