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
        private int? staleAlarmHandle;
        // 补偿登出失败后保留的待清理 SDK 会话：下次成功登录时再次尝试登出，避免设备端会话泄漏且无记录。
        private readonly List<int> pendingSdkLogoutUserIds = new List<int>();
        private bool alarmManuallyDisarmed;
        private string serialNumber;
        private DeviceCapabilities capabilities;
        private DateTime? lastLoginAt;
        private DateTime? lastLogoutAt;
        private DateTime? lastCheckedAt;
        private DeviceRuntimeError lastError;
        private ReconnectState reconnect;
        private bool isDeleting;
        private string deletingLeaseId;
        private DeviceRuntimeSnapshot deletingCheckpoint;
        private long runtimeVersion;
        private DateTime updatedAt;
        private readonly IReadOnlyList<DeviceType> types;
        private readonly List<int> pendingAlarmHandles = new List<int>();
        private readonly HashSet<int> cleanedDuringDeleteSdkUserIds = new HashSet<int>();
        private readonly HashSet<int> cleanedDuringDeleteAlarmHandles = new HashSet<int>();

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

        public int? StaleAlarmHandle
        {
            get
            {
                lock (gate)
                {
                    return staleAlarmHandle;
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

        public long RuntimeVersion
        {
            get
            {
                lock (gate)
                {
                    return runtimeVersion;
                }
            }
        }

        public string DeletingLeaseId
        {
            get
            {
                lock (gate)
                {
                    return deletingLeaseId;
                }
            }
        }

        public DeviceRuntimeSnapshot ToSnapshot(DeviceQueueInfo queueInfo = null)
        {
            lock (gate)
            {
                return ToSnapshotLocked(queueInfo);
            }
        }

        private DeviceRuntimeSnapshot ToSnapshotLocked(DeviceQueueInfo queueInfo)
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
                staleAlarmHandle,
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
                description,
                pendingSdkLogoutUserIds,
                pendingAlarmHandles,
                runtimeVersion,
                deletingLeaseId);
        }

        public bool Matches(long? expectedRuntimeVersion = null, int? expectedSdkUserId = null)
        {
            lock (gate)
            {
                return (!expectedRuntimeVersion.HasValue || runtimeVersion == expectedRuntimeVersion.Value) &&
                    (!expectedSdkUserId.HasValue || sdkUserId == expectedSdkUserId.Value);
            }
        }

        public bool IsDeleteLeaseValid(string leaseId)
        {
            lock (gate)
            {
                return isDeleting && !string.IsNullOrWhiteSpace(leaseId) &&
                    string.Equals(deletingLeaseId, leaseId, StringComparison.Ordinal);
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

        public bool MarkConnecting(DateTime now, long? expectedRuntimeVersion = null)
        {
            lock (gate)
            {
                if (isDeleting || (expectedRuntimeVersion.HasValue && runtimeVersion != expectedRuntimeVersion.Value))
                {
                    return false;
                }

                status = DeviceConnectionStatus.Connecting;
                Touch(now);
                return true;
            }
        }

        public bool MarkLoginSucceeded(int newSdkUserId, string newSerialNumber, DateTime now)
        {
            if (newSdkUserId < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(newSdkUserId), "SDK user id must be non-negative.");
            }

            lock (gate)
            {
                if (isDeleting)
                {
                    return false;
                }

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
                return true;
            }
        }

        public bool MarkSdkSessionRegistered(int newSdkUserId, string newSerialNumber, DateTime now)
        {
            if (newSdkUserId < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(newSdkUserId), "SDK user id must be non-negative.");
            }

            lock (gate)
            {
                if (isDeleting)
                {
                    return false;
                }

                sdkUserId = newSdkUserId;
                serialNumber = newSerialNumber ?? string.Empty;
                status = DeviceConnectionStatus.Connecting;
                lastError = null;
                Touch(now);
                return true;
            }
        }

        public bool PromoteRegisteredSdkSession(DateTime now, int? expectedSdkUserId = null, long? expectedRuntimeVersion = null)
        {
            lock (gate)
            {
                if (isDeleting || !sdkUserId.HasValue ||
                    (expectedSdkUserId.HasValue && sdkUserId != expectedSdkUserId.Value) ||
                    (expectedRuntimeVersion.HasValue && runtimeVersion != expectedRuntimeVersion.Value))
                {
                    return false;
                }

                status = DeviceConnectionStatus.Online;
                lastLoginAt = now;
                reconnect.AttemptCount = 0;
                reconnect.InCooldown = false;
                reconnect.CooldownReason = null;
                reconnect.LastSuccessAt = now;
                lastError = null;
                Touch(now);
                return true;
            }
        }

        public bool MarkLoginFailed(DeviceRuntimeError error, DateTime now, bool faulted = false, int? expectedSdkUserId = null, long? expectedRuntimeVersion = null)
        {
            lock (gate)
            {
                if (isDeleting ||
                    (expectedSdkUserId.HasValue && sdkUserId != expectedSdkUserId.Value) ||
                    (expectedRuntimeVersion.HasValue && runtimeVersion != expectedRuntimeVersion.Value))
                {
                    return false;
                }

                if (alarmHandle.HasValue)
                {
                    RememberAlarmHandleForCleanupLocked(alarmHandle.Value);
                }

                sdkUserId = null;
                alarmHandle = null;
                status = faulted ? DeviceConnectionStatus.Faulted : DeviceConnectionStatus.Offline;
                lastError = error == null ? null : error.Clone();
                reconnect.AttemptCount++;
                reconnect.LastAttemptAt = now;
                Touch(now);
                return true;
            }
        }

        public bool MarkInvalidConfig(DeviceRuntimeError error, DateTime now, int? expectedSdkUserId = null, long? expectedRuntimeVersion = null, bool allowDeleting = false)
        {
            lock (gate)
            {
                if ((!allowDeleting && isDeleting) ||
                    (expectedSdkUserId.HasValue && sdkUserId != expectedSdkUserId.Value) ||
                    (expectedRuntimeVersion.HasValue && runtimeVersion != expectedRuntimeVersion.Value))
                {
                    return false;
                }

                enabled = false;
                if (alarmHandle.HasValue)
                {
                    RememberAlarmHandleForCleanupLocked(alarmHandle.Value);
                }

                sdkUserId = null;
                alarmHandle = null;
                status = DeviceConnectionStatus.InvalidConfig;
                lastError = error == null ? null : error.Clone();
                reconnect.InCooldown = true;
                reconnect.CooldownReason = "InvalidConfig";
                Touch(now);
                return true;
            }
        }

        public bool MarkReconnectPending(DateTime nextReconnectAt, string reason, DateTime now, long? expectedRuntimeVersion = null)
        {
            lock (gate)
            {
                if (isDeleting || (expectedRuntimeVersion.HasValue && runtimeVersion != expectedRuntimeVersion.Value))
                {
                    return false;
                }

                status = DeviceConnectionStatus.ReconnectPending;
                reconnect.NextReconnectAt = nextReconnectAt;
                reconnect.InCooldown = true;
                reconnect.CooldownReason = reason;
                Touch(now);
                return true;
            }
        }

        public bool ResetReconnect(DateTime now, long? expectedRuntimeVersion = null)
        {
            lock (gate)
            {
                if (isDeleting || (expectedRuntimeVersion.HasValue && runtimeVersion != expectedRuntimeVersion.Value))
                {
                    return false;
                }

                reconnect.AttemptCount = 0;
                reconnect.NextReconnectAt = null;
                reconnect.InCooldown = false;
                reconnect.CooldownReason = null;
                reconnect.ManualDisconnected = false;
                Touch(now);
                return true;
            }
        }

        public bool MarkAlarmArmed(int newAlarmHandle, DateTime now, int? expectedSdkUserId = null, long? expectedRuntimeVersion = null)
        {
            if (newAlarmHandle < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(newAlarmHandle), "Alarm handle must be non-negative.");
            }

            lock (gate)
            {
                if (isDeleting || (expectedSdkUserId.HasValue && sdkUserId != expectedSdkUserId.Value) ||
                    (expectedRuntimeVersion.HasValue && runtimeVersion != expectedRuntimeVersion.Value))
                {
                    return false;
                }

                alarmHandle = newAlarmHandle;
                pendingAlarmHandles.Remove(newAlarmHandle);
                if (staleAlarmHandle == newAlarmHandle)
                {
                    staleAlarmHandle = pendingAlarmHandles.Count == 0 ? (int?)null : pendingAlarmHandles[0];
                }

                alarmManuallyDisarmed = false;
                if (status == DeviceConnectionStatus.Online || status == DeviceConnectionStatus.Degraded)
                {
                    capabilities.SupportsAlarm = true;
                    capabilities.Known = true;
                }

                lastError = null;
                Touch(now);
                return true;
            }
        }

        public bool MarkAlarmClosed(
            DateTime now,
            bool manuallyDisarmed = false,
            int? expectedAlarmHandle = null,
            bool allowDeleting = false,
            int? expectedSdkUserId = null,
            long? expectedRuntimeVersion = null)
        {
            lock (gate)
            {
                if ((isDeleting && !allowDeleting) ||
                    (expectedAlarmHandle.HasValue && alarmHandle != expectedAlarmHandle.Value) ||
                    (expectedSdkUserId.HasValue && sdkUserId != expectedSdkUserId.Value) ||
                    (expectedRuntimeVersion.HasValue && runtimeVersion != expectedRuntimeVersion.Value))
                {
                    return false;
                }

                if (alarmHandle.HasValue)
                {
                    RememberAlarmHandleForCleanupLocked(alarmHandle.Value);
                    pendingAlarmHandles.Remove(alarmHandle.Value);
                    if (staleAlarmHandle == alarmHandle.Value)
                    {
                        staleAlarmHandle = pendingAlarmHandles.Count == 0 ? (int?)null : pendingAlarmHandles[0];
                    }
                }

                alarmHandle = null;
                if (manuallyDisarmed)
                {
                    alarmManuallyDisarmed = true;
                }

                Touch(now);
                return true;
            }
        }

        public bool ClearStaleAlarmHandle(DateTime now)
        {
            return ClearStaleAlarmHandle(null, now, false, null, null);
        }

        public bool ClearStaleAlarmHandle(int? handle, DateTime now, bool allowDeleting = false, int? expectedSdkUserId = null, long? expectedRuntimeVersion = null)
        {
            lock (gate)
            {
                if ((!allowDeleting && isDeleting) ||
                    (expectedSdkUserId.HasValue && sdkUserId != expectedSdkUserId.Value) ||
                    (expectedRuntimeVersion.HasValue && runtimeVersion != expectedRuntimeVersion.Value))
                {
                    return false;
                }

                var target = handle;
                if (!target.HasValue)
                {
                    target = staleAlarmHandle.HasValue
                        ? staleAlarmHandle
                        : (pendingAlarmHandles.Count == 0 ? (int?)null : pendingAlarmHandles[0]);
                }

                if (!target.HasValue)
                {
                    return false;
                }

                var changed = pendingAlarmHandles.Remove(target.Value);
                if (staleAlarmHandle == target.Value)
                {
                    staleAlarmHandle = pendingAlarmHandles.Count == 0 ? (int?)null : pendingAlarmHandles[0];
                    changed = true;
                }

                if (changed)
                {
                    Touch(now);
                }

                return changed;
            }
        }

        public IReadOnlyList<int> GetPendingAlarmHandles()
        {
            lock (gate)
            {
                return pendingAlarmHandles.ToList();
            }
        }

        public bool RecordPendingAlarmHandle(int handle, DateTime now, bool allowDeleting = false, int? expectedSdkUserId = null, long? expectedRuntimeVersion = null)
        {
            lock (gate)
            {
                if (handle < 0 || (!allowDeleting && isDeleting) ||
                    (expectedSdkUserId.HasValue && sdkUserId != expectedSdkUserId.Value) ||
                    (expectedRuntimeVersion.HasValue && runtimeVersion != expectedRuntimeVersion.Value))
                {
                    return false;
                }

                RememberAlarmHandleForCleanupLocked(handle);
                Touch(now);
                return true;
            }
        }

        public bool ClearPendingAlarmHandle(int handle, DateTime now, bool allowDeleting = false, int? expectedSdkUserId = null, long? expectedRuntimeVersion = null)
        {
            return ClearStaleAlarmHandle(handle, now, allowDeleting, expectedSdkUserId, expectedRuntimeVersion);
        }

        public bool RecordCleanedDuringDeleteAlarmHandle(
            int handle,
            DateTime now,
            int? expectedSdkUserId = null,
            long? expectedRuntimeVersion = null,
            string expectedDeletingLeaseId = null)
        {
            lock (gate)
            {
                if (!isDeleting || string.IsNullOrWhiteSpace(expectedDeletingLeaseId) ||
                    !string.Equals(deletingLeaseId, expectedDeletingLeaseId, StringComparison.Ordinal) ||
                    (expectedSdkUserId.HasValue && sdkUserId != expectedSdkUserId.Value) ||
                    (expectedRuntimeVersion.HasValue && runtimeVersion != expectedRuntimeVersion.Value))
                {
                    return false;
                }

                var changed = cleanedDuringDeleteAlarmHandles.Add(handle);
                changed = pendingAlarmHandles.Remove(handle) || changed;
                if (alarmHandle == handle)
                {
                    alarmHandle = null;
                    changed = true;
                }

                if (staleAlarmHandle == handle)
                {
                    staleAlarmHandle = pendingAlarmHandles.Count == 0 ? (int?)null : pendingAlarmHandles[0];
                    changed = true;
                }

                if (changed)
                {
                    Touch(now);
                }

                return true;
            }
        }

        public bool RecordCleanedDuringDeleteSdkUser(
            int userId,
            DateTime now,
            int? expectedSdkUserId = null,
            long? expectedRuntimeVersion = null,
            string expectedDeletingLeaseId = null)
        {
            lock (gate)
            {
                if (!isDeleting || string.IsNullOrWhiteSpace(expectedDeletingLeaseId) ||
                    !string.Equals(deletingLeaseId, expectedDeletingLeaseId, StringComparison.Ordinal) ||
                    (expectedSdkUserId.HasValue && sdkUserId != expectedSdkUserId.Value) ||
                    (expectedRuntimeVersion.HasValue && runtimeVersion != expectedRuntimeVersion.Value))
                {
                    return false;
                }

                var changed = cleanedDuringDeleteSdkUserIds.Add(userId);
                changed = pendingSdkLogoutUserIds.Remove(userId) || changed;
                if (sdkUserId == userId)
                {
                    sdkUserId = null;
                    changed = true;
                }

                if (changed)
                {
                    Touch(now);
                }

                return true;
            }
        }

        public bool RecordPendingSdkLogout(int userId, DateTime now, bool allowDeleting = false, int? expectedSdkUserId = null, long? expectedRuntimeVersion = null)
        {
            lock (gate)
            {
                if (userId < 0 || (!allowDeleting && isDeleting) ||
                    (expectedSdkUserId.HasValue && sdkUserId != expectedSdkUserId.Value) ||
                    (expectedRuntimeVersion.HasValue && runtimeVersion != expectedRuntimeVersion.Value))
                {
                    return false;
                }

                // UserId >= 0 是合法 SDK 会话（0 是合法句柄）；负数才是无效输入，忽略以免污染待清理列表。
                if (!pendingSdkLogoutUserIds.Contains(userId))
                {
                    pendingSdkLogoutUserIds.Add(userId);
                    Touch(now);
                }

                return true;
            }
        }

        public bool ClearPendingSdkLogout(int userId, DateTime now, bool allowDeleting = false, int? expectedSdkUserId = null, long? expectedRuntimeVersion = null)
        {
            lock (gate)
            {
                if ((!allowDeleting && isDeleting) ||
                    (expectedSdkUserId.HasValue && sdkUserId != expectedSdkUserId.Value) ||
                    (expectedRuntimeVersion.HasValue && runtimeVersion != expectedRuntimeVersion.Value))
                {
                    return false;
                }

                if (pendingSdkLogoutUserIds.Remove(userId))
                {
                    Touch(now);
                    return true;
                }

                return false;
            }
        }

        public IReadOnlyList<int> GetPendingSdkLogouts()
        {
            lock (gate)
            {
                return pendingSdkLogoutUserIds.ToList();
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

        public bool MarkLoggedOut(DateTime now, int? expectedSdkUserId = null, bool allowDeleting = false, long? expectedRuntimeVersion = null, string expectedDeletingLeaseId = null, bool preserveCurrentAlarmHandle = false)
        {
            lock (gate)
            {
                if ((isDeleting && !allowDeleting) ||
                    (allowDeleting && (!isDeleting || string.IsNullOrWhiteSpace(expectedDeletingLeaseId) || !string.Equals(deletingLeaseId, expectedDeletingLeaseId, StringComparison.Ordinal))) ||
                    (expectedSdkUserId.HasValue && sdkUserId != expectedSdkUserId.Value) ||
                    (expectedRuntimeVersion.HasValue && runtimeVersion != expectedRuntimeVersion.Value))
                {
                    return false;
                }

                if (alarmHandle.HasValue && !preserveCurrentAlarmHandle)
                {
                    RememberAlarmHandleForCleanupLocked(alarmHandle.Value);
                    alarmHandle = null;
                }

                sdkUserId = null;
                status = isDeleting ? DeviceConnectionStatus.Disconnecting : DeviceConnectionStatus.Offline;
                lastLogoutAt = now;
                Touch(now);
                return true;
            }
        }

        public bool MarkManualDisconnected(DeviceRuntimeError error, DateTime now, bool allowDeleting = false, string expectedDeletingLeaseId = null, bool preserveCurrentAlarmHandle = false)
        {
            lock (gate)
            {
                if ((!allowDeleting && isDeleting) ||
                    (allowDeleting && (!isDeleting || string.IsNullOrWhiteSpace(expectedDeletingLeaseId) || !string.Equals(deletingLeaseId, expectedDeletingLeaseId, StringComparison.Ordinal))))
                {
                    return false;
                }

                if (alarmHandle.HasValue && !preserveCurrentAlarmHandle)
                {
                    RememberAlarmHandleForCleanupLocked(alarmHandle.Value);
                    alarmHandle = null;
                }

                sdkUserId = null;
                // 保留当前/旧布防句柄：断开任务在关闭成功时已显式清理；此处仍有值说明旧布防关闭失败，需留给后续人工重连撤防。
                alarmManuallyDisarmed = false;
                status = isDeleting ? DeviceConnectionStatus.Disconnecting : DeviceConnectionStatus.Disconnected;
                reconnect.ManualDisconnected = !isDeleting;
                reconnect.InCooldown = !isDeleting;
                reconnect.CooldownReason = isDeleting ? null : "ManualDisconnected";
                lastError = error == null ? null : error.Clone();
                lastLogoutAt = now;
                Touch(now);
                return true;
            }
        }

        public bool MarkDisconnected(DeviceRuntimeError error, DateTime now, DeviceConnectionStatus newStatus = DeviceConnectionStatus.Offline, bool allowDeleting = false, int? expectedSdkUserId = null, long? expectedRuntimeVersion = null, string expectedDeletingLeaseId = null, bool preserveCurrentAlarmHandle = false)
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
                if ((!allowDeleting && isDeleting) ||
                    (allowDeleting && (!isDeleting || string.IsNullOrWhiteSpace(expectedDeletingLeaseId) || !string.Equals(deletingLeaseId, expectedDeletingLeaseId, StringComparison.Ordinal))) ||
                    (expectedSdkUserId.HasValue && sdkUserId != expectedSdkUserId.Value) ||
                    (expectedRuntimeVersion.HasValue && runtimeVersion != expectedRuntimeVersion.Value))
                {
                    return false;
                }

                if (alarmHandle.HasValue && !preserveCurrentAlarmHandle)
                {
                    RememberAlarmHandleForCleanupLocked(alarmHandle.Value);
                    alarmHandle = null;
                }

                sdkUserId = null;
                status = isDeleting ? DeviceConnectionStatus.Disconnecting : newStatus;
                lastError = error == null ? null : error.Clone();
                Touch(now);
                return true;
            }
        }

        public bool MarkCapabilities(DeviceCapabilities newCapabilities, DateTime now, long? expectedRuntimeVersion = null)
        {
            lock (gate)
            {
                if (isDeleting || (expectedRuntimeVersion.HasValue && runtimeVersion != expectedRuntimeVersion.Value))
                {
                    return false;
                }

                capabilities = newCapabilities == null ? DeviceCapabilities.Unknown() : newCapabilities.Clone();
                capabilities.Known = true;
                capabilities.LastCheckedAt = now;
                lastCheckedAt = now;
                Touch(now);
                return true;
            }
        }

        public bool MarkChecked(DateTime now, DeviceConnectionStatus newStatus, DeviceRuntimeError error = null, int? expectedSdkUserId = null, long? expectedRuntimeVersion = null)
        {
            lock (gate)
            {
                if (isDeleting ||
                    (expectedSdkUserId.HasValue && sdkUserId != expectedSdkUserId.Value) ||
                    (expectedRuntimeVersion.HasValue && runtimeVersion != expectedRuntimeVersion.Value))
                {
                    return false;
                }

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
                return true;
            }
        }

        public bool MarkBusinessSuccess(DateTime now, long? expectedRuntimeVersion = null)
        {
            lock (gate)
            {
                if (isDeleting || (expectedRuntimeVersion.HasValue && runtimeVersion != expectedRuntimeVersion.Value))
                {
                    return false;
                }

                lastError = null;
                Touch(now);
                return true;
            }
        }

        public bool RecordError(DeviceRuntimeError error, DateTime now, DeviceConnectionStatus? newStatus = null, int? expectedSdkUserId = null, long? expectedRuntimeVersion = null, bool allowDeleting = false)
        {
            lock (gate)
            {
                if ((!allowDeleting && isDeleting) ||
                    (expectedSdkUserId.HasValue && sdkUserId != expectedSdkUserId.Value) ||
                    (expectedRuntimeVersion.HasValue && runtimeVersion != expectedRuntimeVersion.Value))
                {
                    return false;
                }

                lastError = error == null ? null : error.Clone();
                if (newStatus.HasValue)
                {
                    status = newStatus.Value;
                }

                Touch(now);
                return true;
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

        public DeviceRuntimeSnapshot MarkDeleting(DateTime now, string leaseId)
        {
            lock (gate)
            {
                if (isDeleting)
                {
                    return deletingCheckpoint;
                }

                deletingCheckpoint = ToSnapshotLocked(null);
                cleanedDuringDeleteSdkUserIds.Clear();
                cleanedDuringDeleteAlarmHandles.Clear();
                deletingLeaseId = leaseId;
                isDeleting = true;
                status = DeviceConnectionStatus.Disconnecting;
                Touch(now);
                return deletingCheckpoint;
            }
        }

        public void MarkDeleting(DateTime now)
        {
            MarkDeleting(now, Guid.NewGuid().ToString("N"));
        }

        public bool ClearDeleting(DateTime now, string leaseId)
        {
            lock (gate)
            {
                if (!isDeleting || string.IsNullOrWhiteSpace(leaseId) ||
                    !string.Equals(deletingLeaseId, leaseId, StringComparison.Ordinal))
                {
                    return false;
                }

                var checkpoint = deletingCheckpoint;
                var cleanupError = lastError == null ? null : lastError.Clone();
                var cleanupSdkUserIds = pendingSdkLogoutUserIds.ToList();
                var cleanupAlarmHandles = pendingAlarmHandles.ToList();
                var cleanupStaleAlarmHandle = staleAlarmHandle;
                MergeCleanupCluesLocked(checkpoint);
                cleanupSdkUserIds = cleanupSdkUserIds
                    .Concat(pendingSdkLogoutUserIds)
                    .Distinct()
                    .ToList();
                cleanupAlarmHandles = cleanupAlarmHandles
                    .Concat(pendingAlarmHandles)
                    .Distinct()
                    .ToList();
                var cleanedSdkUserIds = new HashSet<int>(cleanedDuringDeleteSdkUserIds);
                var cleanedAlarmHandles = new HashSet<int>(cleanedDuringDeleteAlarmHandles);
                if (checkpoint != null)
                {
                    enabled = checkpoint.Enabled;
                    status = checkpoint.Status;
                    sdkUserId = checkpoint.SdkUserId.HasValue && !cleanedSdkUserIds.Contains(checkpoint.SdkUserId.Value)
                        ? checkpoint.SdkUserId
                        : (int?)null;
                    alarmHandle = checkpoint.AlarmHandle.HasValue && !cleanedAlarmHandles.Contains(checkpoint.AlarmHandle.Value)
                        ? checkpoint.AlarmHandle
                        : (int?)null;
                    staleAlarmHandle = checkpoint.StaleAlarmHandle.HasValue && !cleanedAlarmHandles.Contains(checkpoint.StaleAlarmHandle.Value)
                        ? checkpoint.StaleAlarmHandle
                        : (int?)null;
                    alarmManuallyDisarmed = checkpoint.AlarmManuallyDisarmed;
                    serialNumber = checkpoint.SerialNumber;
                    capabilities = checkpoint.Capabilities == null ? DeviceCapabilities.Unknown() : checkpoint.Capabilities.Clone();
                    lastLoginAt = checkpoint.LastLoginAt;
                    lastLogoutAt = checkpoint.LastLogoutAt;
                    lastCheckedAt = checkpoint.LastCheckedAt;
                    lastError = checkpoint.LastError == null ? null : checkpoint.LastError.Clone();
                    reconnect = checkpoint.Reconnect == null ? ReconnectState.New() : checkpoint.Reconnect.Clone();
                    pendingSdkLogoutUserIds.Clear();
                    foreach (var userId in checkpoint.PendingSdkLogoutUserIds ?? Enumerable.Empty<int>())
                    {
                        if (!cleanedSdkUserIds.Contains(userId) && !pendingSdkLogoutUserIds.Contains(userId))
                        {
                            pendingSdkLogoutUserIds.Add(userId);
                        }
                    }

                    pendingAlarmHandles.Clear();
                    foreach (var handle in checkpoint.PendingAlarmHandles ?? Enumerable.Empty<int>())
                    {
                        if (!cleanedAlarmHandles.Contains(handle) && !pendingAlarmHandles.Contains(handle))
                        {
                            pendingAlarmHandles.Add(handle);
                        }
                    }

                    if (checkpoint.SdkUserId.HasValue && cleanedSdkUserIds.Contains(checkpoint.SdkUserId.Value) &&
                        (status == DeviceConnectionStatus.Online || status == DeviceConnectionStatus.Degraded ||
                         status == DeviceConnectionStatus.Connecting || status == DeviceConnectionStatus.Disconnecting))
                    {
                        // The device-side session was already logged out during delete. Never
                        // resurrect the checkpoint as an online runtime after repository rollback.
                        status = DeviceConnectionStatus.Offline;
                    }

                    if (staleAlarmHandle.HasValue && pendingAlarmHandles.Contains(staleAlarmHandle.Value) == false)
                    {
                        staleAlarmHandle = pendingAlarmHandles.Count == 0 ? staleAlarmHandle : pendingAlarmHandles[0];
                    }
                }

                foreach (var userId in cleanupSdkUserIds)
                {
                    if (!cleanedSdkUserIds.Contains(userId) && !pendingSdkLogoutUserIds.Contains(userId))
                    {
                        pendingSdkLogoutUserIds.Add(userId);
                    }
                }

                foreach (var handle in cleanupAlarmHandles)
                {
                    if (!cleanedAlarmHandles.Contains(handle) && !pendingAlarmHandles.Contains(handle))
                    {
                        pendingAlarmHandles.Add(handle);
                    }
                }

                if (cleanupStaleAlarmHandle.HasValue && !cleanedAlarmHandles.Contains(cleanupStaleAlarmHandle.Value) &&
                    !staleAlarmHandle.HasValue)
                {
                    staleAlarmHandle = cleanupStaleAlarmHandle;
                }

                // A failed cleanup is an observable runtime failure. Do not let rollback
                // erase the close/logout error that caused the delete to be rejected.
                if (cleanupError != null)
                {
                    lastError = cleanupError;
                }

                isDeleting = false;
                deletingLeaseId = null;
                deletingCheckpoint = null;
                cleanedDuringDeleteSdkUserIds.Clear();
                cleanedDuringDeleteAlarmHandles.Clear();
                Touch(now);
                return true;
            }
        }

        public void ClearDeleting(DateTime now)
        {
            ClearDeleting(now, string.Empty);
        }

        public void MarkDeleted(DateTime now)
        {
            lock (gate)
            {
                isDeleting = true;
                enabled = false;
                sdkUserId = null;
                alarmHandle = null;
                staleAlarmHandle = null;
                pendingAlarmHandles.Clear();
                pendingSdkLogoutUserIds.Clear();
                status = DeviceConnectionStatus.Deleted;
                deletingLeaseId = null;
                deletingCheckpoint = null;
                Touch(now);
            }
        }

        private void MergeCleanupCluesLocked(DeviceRuntimeSnapshot checkpoint)
        {
            if (sdkUserId.HasValue && (checkpoint == null || !checkpoint.SdkUserId.HasValue || checkpoint.SdkUserId.Value != sdkUserId.Value))
            {
                if (!pendingSdkLogoutUserIds.Contains(sdkUserId.Value))
                {
                    pendingSdkLogoutUserIds.Add(sdkUserId.Value);
                }
            }

            if (alarmHandle.HasValue && (checkpoint == null || !checkpoint.AlarmHandle.HasValue || checkpoint.AlarmHandle.Value != alarmHandle.Value))
            {
                RememberAlarmHandleForCleanupLocked(alarmHandle.Value);
            }
        }

        private void RememberAlarmHandleForCleanupLocked(int handle)
        {
            if (handle < 0)
            {
                return;
            }

            if (!pendingAlarmHandles.Contains(handle))
            {
                pendingAlarmHandles.Add(handle);
            }

            if (!staleAlarmHandle.HasValue)
            {
                staleAlarmHandle = handle;
            }
        }

        private void Touch(DateTime now)
        {
            updatedAt = now;
            runtimeVersion++;
        }
    }
}
