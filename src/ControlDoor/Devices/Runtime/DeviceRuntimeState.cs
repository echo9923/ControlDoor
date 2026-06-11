using System;

namespace ControlDoor.Devices.Runtime
{
    public sealed class DeviceRuntimeState
    {
        private readonly object gate = new object();
        private readonly int deviceId;
        private string deviceName;
        private string ipAddress;
        private int port;
        private string username;
        private string password;
        private bool enabled;
        private DeviceConnectionStatus status;
        private int? sdkUserId;
        private int? alarmHandle;
        private string serialNumber;
        private DeviceCapabilities capabilities;
        private DateTime? lastLoginAt;
        private DateTime? lastLogoutAt;
        private DateTime? lastCheckedAt;
        private DateTime? lastUsedAt;
        private DeviceRuntimeError lastError;
        private ReconnectState reconnect;
        private bool isDeleting;
        private DateTime updatedAt;

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
            ipAddress = options.IpAddress ?? string.Empty;
            port = options.Port;
            username = options.Username ?? string.Empty;
            password = options.Password ?? string.Empty;
            enabled = options.Enabled;
            status = enabled ? DeviceConnectionStatus.Unknown : DeviceConnectionStatus.Disabled;
            serialNumber = string.Empty;
            capabilities = DeviceCapabilities.Unknown();
            reconnect = ReconnectState.New();
            updatedAt = options.CreatedAt ?? DateTime.Now;
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
                    serialNumber,
                    capabilities,
                    lastLoginAt,
                    lastLogoutAt,
                    lastCheckedAt,
                    lastUsedAt,
                    lastError,
                    reconnect,
                    updatedAt,
                    queueInfo);
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

        public void MarkAlarmArmed(int newAlarmHandle, DateTime now)
        {
            if (newAlarmHandle < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(newAlarmHandle), "Alarm handle must be non-negative.");
            }

            lock (gate)
            {
                alarmHandle = newAlarmHandle;
                if (status == DeviceConnectionStatus.Online || status == DeviceConnectionStatus.Degraded)
                {
                    capabilities.SupportsAlarm = true;
                    capabilities.Known = true;
                }

                lastError = null;
                Touch(now);
            }
        }

        public void MarkAlarmClosed(DateTime now)
        {
            lock (gate)
            {
                alarmHandle = null;
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

        public void MarkDisconnected(DeviceRuntimeError error, DateTime now, DeviceConnectionStatus newStatus = DeviceConnectionStatus.Offline)
        {
            if (newStatus != DeviceConnectionStatus.Offline &&
                newStatus != DeviceConnectionStatus.Disconnecting &&
                newStatus != DeviceConnectionStatus.Faulted)
            {
                throw new ArgumentException("Disconnected status must be Offline, Disconnecting, or Faulted.", nameof(newStatus));
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
                lastError = error == null ? lastError : error.Clone();
                Touch(now);
            }
        }

        public void MarkBusinessSuccess(DateTime now)
        {
            lock (gate)
            {
                lastUsedAt = now;
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
