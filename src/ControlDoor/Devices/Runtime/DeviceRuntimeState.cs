using System;
using System.Collections.Generic;
using System.Linq;
using ControlDoor.Devices.Management;

namespace ControlDoor.Devices.Runtime
{
    public sealed class DeviceRuntimeState
    {
        private readonly object gate = new object();
        private readonly int deviceId;
        private string deviceName;
        private string description;
        private string ipAddress;
        private int port;
        private string username;
        private string password;
        private bool enabled;
        private DeviceConnectionStatus status;
        private int? sdkUserId;
        private int? alarmHandle;
        private bool alarmManuallyDisarmed;
        private string serialNumber;
        private DeviceCapabilities capabilities;
        private DateTime? lastLoginAt;
        private DateTime? lastLogoutAt;
        private DateTime? lastCheckedAt;
        private DeviceRuntimeError lastError;
        private ReconnectState reconnect;
        private bool isDeleting;
        private DateTime updatedAt;
        private readonly IReadOnlyList<DeviceType> types;

        public DeviceRuntimeState(DeviceRuntimeCreationOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (options.DeviceId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options.DeviceId), "DeviceId must be greater than 0.");
            }

            if (options.Port <= 0 || options.Port > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(options.Port), "Port must be in range 1-65535.");
            }

            deviceId = options.DeviceId;
            deviceName = options.DeviceName ?? string.Empty;
            description = options.Description ?? string.Empty;
            ipAddress = options.IpAddress ?? string.Empty;
            port = options.Port;
            username = options.Username ?? string.Empty;
            password = options.Password ?? string.Empty;
            enabled = options.Enabled;
            status = enabled ? DeviceConnectionStatus.Loaded : DeviceConnectionStatus.Disabled;
            serialNumber = string.Empty;
            capabilities = DeviceCapabilities.Unknown();
            reconnect = ReconnectState.New();
            types = (options.Types ?? Enumerable.Empty<DeviceType>()).ToList().AsReadOnly();
            updatedAt = options.CreatedAt ?? DateTime.Now;
        }

        public IReadOnlyList<DeviceType> Types
        {
            get
            {
                return types;
            }
        }

        public int DeviceId => deviceId;

        public string DeviceName
        {
            get
            {
                lock (gate)
                {
                    return deviceName;
                }
            }
        }

        public string Description
        {
            get
            {
                lock (gate)
                {
                    return description;
                }
            }
        }

        public string IpAddress
        {
            get
            {
                lock (gate)
                {
                    return ipAddress;
                }
            }
        }

        public int Port
        {
            get
            {
                lock (gate)
                {
                    return port;
                }
            }
        }

        public string Username
        {
            get
            {
                lock (gate)
                {
                    return username;
                }
            }
        }

        public string Password
        {
            get
            {
                lock (gate)
                {
                    return password;
                }
            }
        }

        public bool Enabled
        {
            get
            {
                lock (gate)
                {
                    return enabled;
                }
            }
        }

        public DeviceConnectionStatus Status
        {
            get
            {
                lock (gate)
                {
                    return status;
                }
            }
        }

        public int? SdkUserId
        {
            get
            {
                lock (gate)
                {
                    return sdkUserId;
                }
            }
        }

        public int? AlarmHandle
        {
            get
            {
                lock (gate)
                {
                    return alarmHandle;
                }
            }
        }

        public bool IsDeleting
        {
            get
            {
                lock (gate)
                {
                    return isDeleting;
                }
            }
        }

        public DeviceRuntimeSnapshot ToSnapshot(DeviceQueueInfo queueInfo = null)
        {
            lock (gate)
            {
                return new DeviceRuntimeSnapshot(
                    deviceId,
                    deviceName,
                    ipAddress,
                    port,
                    enabled,
                    status,
                    isDeleting,
                    sdkUserId,
                    alarmHandle,
                    alarmManuallyDisarmed,
                    serialNumber,
                    capabilities,
                    lastLoginAt,
                    lastLogoutAt,
                    lastCheckedAt,
                    lastError,
                    reconnect,
                    updatedAt,
                    queueInfo,
                    types,
                    description);
            }
        }

        public DeviceRuntimeCreationOptions ToConnectionOptions()
        {
            lock (gate)
            {
                return new DeviceRuntimeCreationOptions
                {
                    DeviceId = deviceId,
                    DeviceName = deviceName,
                    Description = description,
                    IpAddress = ipAddress,
                    Port = port,
                    Username = username,
                    Password = password,
                    Enabled = enabled,
                    Types = types.ToList(),
                    CreatedAt = updatedAt
                };
            }
        }

        public void MarkConnecting(DateTime now)
        {
            lock (gate)
            {
                status = DeviceConnectionStatus.Connecting;
                Touch(now);
            }
        }

        public void MarkLoginSucceeded(int newSdkUserId, string newSerialNumber, DateTime now)
        {
            if (newSdkUserId < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(newSdkUserId), "SDK user id must be non-negative.");
            }

            lock (gate)
            {
                sdkUserId = newSdkUserId;
                serialNumber = newSerialNumber ?? string.Empty;
                status = DeviceConnectionStatus.Online;
                lastLoginAt = now;
                reconnect.AttemptCount = 0;
                reconnect.InCooldown = false;
                reconnect.CooldownReason = null;
                reconnect.LastSuccessAt = now;
                lastError = null;
                Touch(now);
            }
        }

        public void MarkLoginFailed(DeviceRuntimeError error, DateTime now, bool faulted = false)
        {
            lock (gate)
            {
                sdkUserId = null;
                alarmHandle = null;
                status = faulted ? DeviceConnectionStatus.Faulted : DeviceConnectionStatus.Offline;
                lastError = error == null ? null : error.Clone();
                reconnect.AttemptCount++;
                reconnect.LastAttemptAt = now;
                Touch(now);
            }
        }

        public void MarkInvalidConfig(DeviceRuntimeError error, DateTime now)
        {
            lock (gate)
            {
                enabled = false;
                sdkUserId = null;
                alarmHandle = null;
                status = DeviceConnectionStatus.InvalidConfig;
                lastError = error == null ? null : error.Clone();
                reconnect.InCooldown = true;
                reconnect.CooldownReason = "InvalidConfig";
                Touch(now);
            }
        }

        public void MarkReconnectPending(DateTime nextReconnectAt, string reason, DateTime now)
        {
            lock (gate)
            {
                status = DeviceConnectionStatus.ReconnectPending;
                reconnect.NextReconnectAt = nextReconnectAt;
                reconnect.InCooldown = true;
                reconnect.CooldownReason = reason;
                Touch(now);
            }
        }

        public void ResetReconnect(DateTime now)
        {
            lock (gate)
            {
                reconnect.AttemptCount = 0;
                reconnect.NextReconnectAt = null;
                reconnect.InCooldown = false;
                reconnect.CooldownReason = null;
                reconnect.ManualDisconnected = false;
                Touch(now);
            }
        }

        public void MarkAlarmArmed(int newAlarmHandle, DateTime now)
        {
            if (newAlarmHandle < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(newAlarmHandle), "Alarm handle must be non-negative.");
            }

            lock (gate)
            {
                alarmHandle = newAlarmHandle;
                alarmManuallyDisarmed = false;
                if (status == DeviceConnectionStatus.Online || status == DeviceConnectionStatus.Degraded)
                {
                    capabilities.SupportsAlarm = true;
                    capabilities.Known = true;
                }

                lastError = null;
                Touch(now);
            }
        }

        public void MarkAlarmClosed(DateTime now, bool manuallyDisarmed = false)
        {
            lock (gate)
            {
                alarmHandle = null;
                if (manuallyDisarmed)
                {
                    alarmManuallyDisarmed = true;
                }

                Touch(now);
            }
        }

        public void ClearManualAlarmDisarm(DateTime now)
        {
            lock (gate)
            {
                alarmManuallyDisarmed = false;
                Touch(now);
            }
        }

        public void MarkLoggedOut(DateTime now)
        {
            lock (gate)
            {
                sdkUserId = null;
                alarmHandle = null;
                status = DeviceConnectionStatus.Offline;
                lastLogoutAt = now;
                Touch(now);
            }
        }

        public void MarkManualDisconnected(DeviceRuntimeError error, DateTime now)
        {
            lock (gate)
            {
                sdkUserId = null;
                alarmHandle = null;
                alarmManuallyDisarmed = false;
                status = DeviceConnectionStatus.Disconnected;
                reconnect.ManualDisconnected = true;
                reconnect.InCooldown = true;
                reconnect.CooldownReason = "ManualDisconnected";
                lastError = error == null ? null : error.Clone();
                lastLogoutAt = now;
                Touch(now);
            }
        }

        public void MarkDisconnected(DeviceRuntimeError error, DateTime now, DeviceConnectionStatus newStatus = DeviceConnectionStatus.Offline)
        {
            if (newStatus != DeviceConnectionStatus.Offline &&
                newStatus != DeviceConnectionStatus.Disconnecting &&
                newStatus != DeviceConnectionStatus.Faulted &&
                newStatus != DeviceConnectionStatus.Disconnected &&
                newStatus != DeviceConnectionStatus.ReconnectPending &&
                newStatus != DeviceConnectionStatus.Failed)
            {
                throw new ArgumentException("Disconnected status must be Offline, Disconnecting, Faulted, Disconnected, ReconnectPending, or Failed.", nameof(newStatus));
            }

            lock (gate)
            {
                sdkUserId = null;
                alarmHandle = null;
                status = newStatus;
                lastError = error == null ? null : error.Clone();
                Touch(now);
            }
        }

        public void MarkCapabilities(DeviceCapabilities newCapabilities, DateTime now)
        {
            lock (gate)
            {
                capabilities = newCapabilities == null ? DeviceCapabilities.Unknown() : newCapabilities.Clone();
                capabilities.Known = true;
                capabilities.LastCheckedAt = now;
                lastCheckedAt = now;
                Touch(now);
            }
        }

        public void MarkChecked(DateTime now, DeviceConnectionStatus newStatus, DeviceRuntimeError error = null)
        {
            lock (gate)
            {
                status = newStatus;
                lastCheckedAt = now;
                if (error == null && (newStatus == DeviceConnectionStatus.Online || newStatus == DeviceConnectionStatus.Degraded))
                {
                    lastError = null;
                }
                else if (error != null)
                {
                    lastError = error.Clone();
                }

                Touch(now);
            }
        }

        public void MarkBusinessSuccess(DateTime now)
        {
            lock (gate)
            {
                lastError = null;
                Touch(now);
            }
        }

        public void RecordError(DeviceRuntimeError error, DateTime now, DeviceConnectionStatus? newStatus = null)
        {
            lock (gate)
            {
                lastError = error == null ? null : error.Clone();
                if (newStatus.HasValue)
                {
                    status = newStatus.Value;
                }

                Touch(now);
            }
        }

        public void SetManualDisconnected(bool manualDisconnected, string reason, DateTime now)
        {
            lock (gate)
            {
                reconnect.ManualDisconnected = manualDisconnected;
                reconnect.InCooldown = manualDisconnected;
                reconnect.CooldownReason = reason;
                Touch(now);
            }
        }

        public void MarkDeleting(DateTime now)
        {
            lock (gate)
            {
                isDeleting = true;
                status = DeviceConnectionStatus.Disconnecting;
                Touch(now);
            }
        }

        public void MarkDeleted(DateTime now)
        {
            lock (gate)
            {
                isDeleting = true;
                enabled = false;
                sdkUserId = null;
                alarmHandle = null;
                status = DeviceConnectionStatus.Deleted;
                Touch(now);
            }
        }

        private void Touch(DateTime now)
        {
            updatedAt = now;
        }
    }
}
