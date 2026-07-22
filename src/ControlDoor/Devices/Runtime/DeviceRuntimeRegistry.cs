using System;
using System.Collections.Generic;
using System.Linq;
using ControlDoor.Devices.Management;

namespace ControlDoor.Devices.Runtime
{
    public sealed class DeviceRuntimeRegistry
    {
        private readonly object gate = new object();
        private readonly Dictionary<int, DeviceRuntimeState> devices = new Dictionary<int, DeviceRuntimeState>();
        private readonly Dictionary<string, int> ipIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, int> sdkUserIdIndex = new Dictionary<int, int>();
        private readonly Dictionary<int, int> alarmHandleIndex = new Dictionary<int, int>();
        private readonly Dictionary<int, int> workerRoutes = new Dictionary<int, int>();
        private readonly Dictionary<int, DeviceQueueInfo> queueInfos = new Dictionary<int, DeviceQueueInfo>();
        private readonly int workerCount;
        private int conflictCount;
        private int deletedCount;

        public DeviceRuntimeRegistry(DeviceRuntimeRegistryOptions options = null)
        {
            options = options ?? new DeviceRuntimeRegistryOptions();
            if (options.WorkerCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options.WorkerCount), "WorkerCount must be greater than 0.");
            }

            workerCount = options.WorkerCount;
        }

        public int WorkerCount => workerCount;

        public DeviceRuntimeMutationResult Register(DeviceRuntimeCreationOptions options, DeviceIndexUpdateContext context = null)
        {
            if (options == null)
            {
                return DeviceRuntimeMutationResult.Invalid("Device options are required.");
            }

            if (options.DeviceId <= 0)
            {
                return DeviceRuntimeMutationResult.Invalid("DeviceId must be greater than 0.");
            }

            if (options.Port <= 0 || options.Port > 65535)
            {
                return DeviceRuntimeMutationResult.Invalid("Port must be in range 1-65535.");
            }

            var normalizedIp = DeviceIndexKeyNormalizer.NormalizeIpAddress(options.IpAddress);

            lock (gate)
            {
                if (devices.ContainsKey(options.DeviceId))
                {
                    conflictCount++;
                    return DeviceRuntimeMutationResult.Conflict(options.DeviceId, "DEVICE_ID_CONFLICT", "DeviceId is already registered.");
                }

                if (!string.IsNullOrEmpty(normalizedIp) && ipIndex.ContainsKey(normalizedIp))
                {
                    conflictCount++;
                    return DeviceRuntimeMutationResult.Conflict(ipIndex[normalizedIp], "IP_CONFLICT", "IP address is already registered.");
                }

                var creationOptions = CopyWithNormalizedIp(options, normalizedIp);
                var state = new DeviceRuntimeState(creationOptions);
                var workerIndex = CalculateWorkerIndex(options.DeviceId);

                devices.Add(options.DeviceId, state);
                workerRoutes.Add(options.DeviceId, workerIndex);
                if (!string.IsNullOrEmpty(normalizedIp))
                {
                    ipIndex.Add(normalizedIp, options.DeviceId);
                }

                return DeviceRuntimeMutationResult.Succeeded(state.ToSnapshot(), workerIndex, "REGISTERED", "Device runtime was registered.");
            }
        }

        public DeviceRuntimeLookupResult TryGetByDeviceId(int deviceId)
        {
            if (deviceId <= 0)
            {
                return DeviceRuntimeLookupResult.Invalid("DeviceId must be greater than 0.");
            }

            lock (gate)
            {
                return TryGetByDeviceIdLocked(deviceId);
            }
        }

        public DeviceRuntimeCreationOptions GetConnectionOptions(int deviceId)
        {
            if (deviceId <= 0)
            {
                return null;
            }

            lock (gate)
            {
                DeviceRuntimeState state;
                return devices.TryGetValue(deviceId, out state) ? state.ToConnectionOptions() : null;
            }
        }

        public DeviceRuntimeLookupResult TryGetByIpAddress(string ipAddress)
        {
            var key = DeviceIndexKeyNormalizer.NormalizeIpAddress(ipAddress);
            if (string.IsNullOrEmpty(key))
            {
                return DeviceRuntimeLookupResult.Invalid("IP address is required.");
            }

            lock (gate)
            {
                if (!ipIndex.TryGetValue(key, out var deviceId))
                {
                    return DeviceRuntimeLookupResult.NotFound("No device is registered for the IP address.");
                }

                return TryGetByDeviceIdLocked(deviceId);
            }
        }

        public DeviceRuntimeLookupResult TryGetBySdkUserId(int sdkUserId)
        {
            if (sdkUserId < 0)
            {
                return DeviceRuntimeLookupResult.Invalid("SDK user id must be non-negative.");
            }

            lock (gate)
            {
                if (!sdkUserIdIndex.TryGetValue(sdkUserId, out var deviceId))
                {
                    return DeviceRuntimeLookupResult.NotFound("No device is registered for the SDK user id.");
                }

                return TryGetByDeviceIdLocked(deviceId);
            }
        }

        public DeviceRuntimeLookupResult TryGetByAlarmHandle(int alarmHandle)
        {
            if (alarmHandle < 0)
            {
                return DeviceRuntimeLookupResult.Invalid("Alarm handle must be non-negative.");
            }

            lock (gate)
            {
                if (!alarmHandleIndex.TryGetValue(alarmHandle, out var deviceId))
                {
                    return DeviceRuntimeLookupResult.NotFound("No device is registered for the alarm handle.");
                }

                return TryGetByDeviceIdLocked(deviceId);
            }
        }

        public DeviceRuntimeLookupResult TryGetWorkerRoute(int deviceId)
        {
            if (deviceId <= 0)
            {
                return DeviceRuntimeLookupResult.Invalid("DeviceId must be greater than 0.");
            }

            lock (gate)
            {
                if (!workerRoutes.TryGetValue(deviceId, out var workerIndex))
                {
                    return DeviceRuntimeLookupResult.NotFound("No worker route exists for the device.");
                }

                var snapshot = devices.ContainsKey(deviceId) ? devices[deviceId].ToSnapshot(GetQueueInfoLocked(deviceId)) : null;
                return DeviceRuntimeLookupResult.FoundResult(snapshot, workerIndex);
            }
        }

        public DeviceRuntimeMutationResult RegisterSdkUserId(int deviceId, int sdkUserId, string serialNumber, DateTime now, DeviceIndexUpdateContext context = null, bool publishOnline = true)
        {
            if (sdkUserId < 0)
            {
                return DeviceRuntimeMutationResult.Invalid("SDK user id must be non-negative.");
            }

            lock (gate)
            {
                if (!devices.TryGetValue(deviceId, out var state))
                {
                    return DeviceRuntimeMutationResult.NotFound();
                }

                if (state.IsDeleting)
                {
                    return DeviceRuntimeMutationResult.Invalid("设备正在删除，拒绝注册新的 SDK 会话。");
                }

                if (sdkUserIdIndex.TryGetValue(sdkUserId, out var ownerDeviceId) && ownerDeviceId != deviceId)
                {
                    conflictCount++;
                    return DeviceRuntimeMutationResult.Conflict(ownerDeviceId, "SDK_USER_ID_CONFLICT", "SDK user id is already registered to another device.");
                }

                RemoveSdkUserIndexLocked(deviceId, state.SdkUserId);
                sdkUserIdIndex[sdkUserId] = deviceId;
                if (publishOnline)
                {
                    state.MarkLoginSucceeded(sdkUserId, serialNumber, now);
                }
                else
                {
                    state.MarkSdkSessionRegistered(sdkUserId, serialNumber, now);
                }

                return DeviceRuntimeMutationResult.Succeeded(state.ToSnapshot(GetQueueInfoLocked(deviceId)), GetWorkerIndexLocked(deviceId), "SDK_USER_REGISTERED", "SDK user id was registered.");
            }
        }

        public DeviceRuntimeMutationResult PromoteRegisteredSdkSession(int deviceId, DateTime now, DeviceIndexUpdateContext context = null)
        {
            lock (gate)
            {
                if (!devices.TryGetValue(deviceId, out var state))
                {
                    return DeviceRuntimeMutationResult.NotFound();
                }

                if (state.IsDeleting)
                {
                    return DeviceRuntimeMutationResult.Invalid("设备正在删除，拒绝发布新的 SDK 会话。");
                }

                state.PromoteRegisteredSdkSession(now);
                return DeviceRuntimeMutationResult.Succeeded(state.ToSnapshot(GetQueueInfoLocked(deviceId)), GetWorkerIndexLocked(deviceId), "SDK_SESSION_ONLINE", "SDK session was published online.");
            }
        }

        public DeviceRuntimeMutationResult MarkLoginFailed(int deviceId, DeviceRuntimeError error, DateTime now, bool faulted = false, DeviceIndexUpdateContext context = null)
        {
            lock (gate)
            {
                if (!devices.TryGetValue(deviceId, out var state))
                {
                    return DeviceRuntimeMutationResult.NotFound();
                }

                RemoveSdkUserIndexLocked(deviceId, state.SdkUserId);
                RemoveAlarmHandleIndexLocked(deviceId, state.AlarmHandle);
                state.MarkLoginFailed(error, now, faulted);
                return DeviceRuntimeMutationResult.Succeeded(state.ToSnapshot(GetQueueInfoLocked(deviceId)), GetWorkerIndexLocked(deviceId), "LOGIN_FAILED", "Device login failure was recorded.");
            }
        }

        public DeviceRuntimeMutationResult MarkConnecting(int deviceId, DateTime now, DeviceIndexUpdateContext context = null)
        {
            lock (gate)
            {
                if (!devices.TryGetValue(deviceId, out var state))
                {
                    return DeviceRuntimeMutationResult.NotFound();
                }

                if (state.IsDeleting)
                {
                    return DeviceRuntimeMutationResult.Invalid("设备正在删除，拒绝开始新的登录。");
                }

                state.MarkConnecting(now);
                return DeviceRuntimeMutationResult.Succeeded(state.ToSnapshot(GetQueueInfoLocked(deviceId)), GetWorkerIndexLocked(deviceId), "CONNECTING", "Device runtime is connecting.");
            }
        }

        public DeviceRuntimeMutationResult RegisterAlarmHandle(int deviceId, int alarmHandle, DateTime now, DeviceIndexUpdateContext context = null)
        {
            if (alarmHandle < 0)
            {
                return DeviceRuntimeMutationResult.Invalid("Alarm handle must be non-negative.");
            }

            lock (gate)
            {
                if (!devices.TryGetValue(deviceId, out var state))
                {
                    return DeviceRuntimeMutationResult.NotFound();
                }

                if (alarmHandleIndex.TryGetValue(alarmHandle, out var ownerDeviceId) && ownerDeviceId != deviceId)
                {
                    conflictCount++;
                    return DeviceRuntimeMutationResult.Conflict(ownerDeviceId, "ALARM_HANDLE_CONFLICT", "Alarm handle is already registered to another device.");
                }

                RemoveAlarmHandleIndexLocked(deviceId, state.AlarmHandle);
                alarmHandleIndex[alarmHandle] = deviceId;
                state.MarkAlarmArmed(alarmHandle, now);

                return DeviceRuntimeMutationResult.Succeeded(state.ToSnapshot(GetQueueInfoLocked(deviceId)), GetWorkerIndexLocked(deviceId), "ALARM_HANDLE_REGISTERED", "Alarm handle was registered.");
            }
        }

        public DeviceRuntimeMutationResult ClearAlarmHandle(int deviceId, DateTime now, DeviceIndexUpdateContext context = null)
        {
            return ClearAlarmHandle(deviceId, now, false, context);
        }

        public DeviceRuntimeMutationResult MarkAlarmManuallyDisarmed(int deviceId, DateTime now, DeviceIndexUpdateContext context = null)
        {
            return ClearAlarmHandle(deviceId, now, true, context);
        }

        public DeviceRuntimeMutationResult ClearManualAlarmDisarm(int deviceId, DateTime now, DeviceIndexUpdateContext context = null)
        {
            lock (gate)
            {
                if (!devices.TryGetValue(deviceId, out var state))
                {
                    return DeviceRuntimeMutationResult.NotFound();
                }

                state.ClearManualAlarmDisarm(now);
                return DeviceRuntimeMutationResult.Succeeded(state.ToSnapshot(GetQueueInfoLocked(deviceId)), GetWorkerIndexLocked(deviceId), "MANUAL_ALARM_DISARM_CLEARED", "Manual alarm disarm marker was cleared.");
            }
        }

        private DeviceRuntimeMutationResult ClearAlarmHandle(int deviceId, DateTime now, bool manuallyDisarmed, DeviceIndexUpdateContext context = null)
        {
            lock (gate)
            {
                if (!devices.TryGetValue(deviceId, out var state))
                {
                    return DeviceRuntimeMutationResult.NotFound();
                }

                RemoveAlarmHandleIndexLocked(deviceId, state.AlarmHandle);
                state.MarkAlarmClosed(now, manuallyDisarmed);
                return DeviceRuntimeMutationResult.Succeeded(state.ToSnapshot(GetQueueInfoLocked(deviceId)), GetWorkerIndexLocked(deviceId), "ALARM_HANDLE_CLEARED", "Alarm handle was cleared.");
            }
        }

        public DeviceRuntimeMutationResult ClearStaleAlarmHandle(int deviceId, DateTime now, DeviceIndexUpdateContext context = null)
        {
            lock (gate)
            {
                if (!devices.TryGetValue(deviceId, out var state))
                {
                    return DeviceRuntimeMutationResult.NotFound();
                }

                state.ClearStaleAlarmHandle(now);
                return DeviceRuntimeMutationResult.Succeeded(state.ToSnapshot(GetQueueInfoLocked(deviceId)), GetWorkerIndexLocked(deviceId), "STALE_ALARM_HANDLE_CLEARED", "Stale alarm handle was cleared.");
            }
        }

        public DeviceRuntimeMutationResult RecordPendingSdkLogout(int deviceId, int userId, DateTime now, DeviceIndexUpdateContext context = null)
        {
            lock (gate)
            {
                if (!devices.TryGetValue(deviceId, out var state))
                {
                    return DeviceRuntimeMutationResult.NotFound();
                }

                state.RecordPendingSdkLogout(userId, now);
                return DeviceRuntimeMutationResult.Succeeded(state.ToSnapshot(GetQueueInfoLocked(deviceId)), GetWorkerIndexLocked(deviceId), "PENDING_SDK_LOGOUT_RECORDED", "Pending SDK logout was recorded.");
            }
        }

        public DeviceRuntimeMutationResult ClearPendingSdkLogout(int deviceId, int userId, DateTime now, DeviceIndexUpdateContext context = null)
        {
            lock (gate)
            {
                if (!devices.TryGetValue(deviceId, out var state))
                {
                    return DeviceRuntimeMutationResult.NotFound();
                }

                state.ClearPendingSdkLogout(userId, now);
                return DeviceRuntimeMutationResult.Succeeded(state.ToSnapshot(GetQueueInfoLocked(deviceId)), GetWorkerIndexLocked(deviceId), "PENDING_SDK_LOGOUT_CLEARED", "Pending SDK logout was cleared.");
            }
        }

        public IReadOnlyList<int> GetPendingSdkLogouts(int deviceId)
        {
            lock (gate)
            {
                if (!devices.TryGetValue(deviceId, out var state))
                {
                    return new List<int>();
                }

                return state.GetPendingSdkLogouts();
            }
        }

        public DeviceRuntimeMutationResult MarkLoggedOut(int deviceId, DateTime now, DeviceIndexUpdateContext context = null)
        {
            lock (gate)
            {
                if (!devices.TryGetValue(deviceId, out var state))
                {
                    return DeviceRuntimeMutationResult.NotFound();
                }

                RemoveSdkUserIndexLocked(deviceId, state.SdkUserId);
                RemoveAlarmHandleIndexLocked(deviceId, state.AlarmHandle);
                state.MarkLoggedOut(now);
                return DeviceRuntimeMutationResult.Succeeded(state.ToSnapshot(GetQueueInfoLocked(deviceId)), GetWorkerIndexLocked(deviceId), "LOGGED_OUT", "Device runtime was logged out.");
            }
        }

        public DeviceRuntimeMutationResult MarkManualDisconnected(int deviceId, DeviceRuntimeError error, DateTime now, DeviceIndexUpdateContext context = null)
        {
            lock (gate)
            {
                if (!devices.TryGetValue(deviceId, out var state))
                {
                    return DeviceRuntimeMutationResult.NotFound();
                }

                RemoveSdkUserIndexLocked(deviceId, state.SdkUserId);
                RemoveAlarmHandleIndexLocked(deviceId, state.AlarmHandle);
                state.MarkManualDisconnected(error, now);
                return DeviceRuntimeMutationResult.Succeeded(state.ToSnapshot(GetQueueInfoLocked(deviceId)), GetWorkerIndexLocked(deviceId), "MANUAL_DISCONNECTED", "Device runtime was manually disconnected.");
            }
        }

        public DeviceRuntimeMutationResult MarkInvalidConfig(int deviceId, DeviceRuntimeError error, DateTime now, DeviceIndexUpdateContext context = null)
        {
            lock (gate)
            {
                if (!devices.TryGetValue(deviceId, out var state))
                {
                    return DeviceRuntimeMutationResult.NotFound();
                }

                RemoveSdkUserIndexLocked(deviceId, state.SdkUserId);
                RemoveAlarmHandleIndexLocked(deviceId, state.AlarmHandle);
                state.MarkInvalidConfig(error, now);
                return DeviceRuntimeMutationResult.Succeeded(state.ToSnapshot(GetQueueInfoLocked(deviceId)), GetWorkerIndexLocked(deviceId), "INVALID_CONFIG", "Device runtime was marked invalid.");
            }
        }

        public DeviceRuntimeMutationResult MarkReconnectPending(int deviceId, DateTime nextReconnectAt, string reason, DateTime now, DeviceIndexUpdateContext context = null)
        {
            lock (gate)
            {
                if (!devices.TryGetValue(deviceId, out var state))
                {
                    return DeviceRuntimeMutationResult.NotFound();
                }

                state.MarkReconnectPending(nextReconnectAt, reason, now);
                return DeviceRuntimeMutationResult.Succeeded(state.ToSnapshot(GetQueueInfoLocked(deviceId)), GetWorkerIndexLocked(deviceId), "RECONNECT_PENDING", "Device reconnect was scheduled.");
            }
        }

        public DeviceRuntimeMutationResult ResetReconnect(int deviceId, DateTime now, DeviceIndexUpdateContext context = null)
        {
            lock (gate)
            {
                if (!devices.TryGetValue(deviceId, out var state))
                {
                    return DeviceRuntimeMutationResult.NotFound();
                }

                state.ResetReconnect(now);
                return DeviceRuntimeMutationResult.Succeeded(state.ToSnapshot(GetQueueInfoLocked(deviceId)), GetWorkerIndexLocked(deviceId), "RECONNECT_RESET", "Device reconnect state was reset.");
            }
        }

        public DeviceRuntimeMutationResult MarkDisconnected(int deviceId, DeviceRuntimeError error, DateTime now, DeviceConnectionStatus status = DeviceConnectionStatus.Offline, DeviceIndexUpdateContext context = null)
        {
            lock (gate)
            {
                if (!devices.TryGetValue(deviceId, out var state))
                {
                    return DeviceRuntimeMutationResult.NotFound();
                }

                RemoveSdkUserIndexLocked(deviceId, state.SdkUserId);
                RemoveAlarmHandleIndexLocked(deviceId, state.AlarmHandle);
                state.MarkDisconnected(error, now, status);
                return DeviceRuntimeMutationResult.Succeeded(state.ToSnapshot(GetQueueInfoLocked(deviceId)), GetWorkerIndexLocked(deviceId), "DISCONNECTED", "Device runtime was disconnected.");
            }
        }

        public DeviceRuntimeMutationResult MarkChecked(int deviceId, DateTime now, DeviceConnectionStatus status, DeviceRuntimeError error = null, DeviceIndexUpdateContext context = null)
        {
            lock (gate)
            {
                if (!devices.TryGetValue(deviceId, out var state))
                {
                    return DeviceRuntimeMutationResult.NotFound();
                }

                state.MarkChecked(now, status, error);
                return DeviceRuntimeMutationResult.Succeeded(state.ToSnapshot(GetQueueInfoLocked(deviceId)), GetWorkerIndexLocked(deviceId), "CHECKED", "Device runtime check state was updated.");
            }
        }

        public DeviceRuntimeMutationResult RecordError(int deviceId, DeviceRuntimeError error, DateTime now, DeviceConnectionStatus? status = null, DeviceIndexUpdateContext context = null)
        {
            lock (gate)
            {
                if (!devices.TryGetValue(deviceId, out var state))
                {
                    return DeviceRuntimeMutationResult.NotFound();
                }

                state.RecordError(error, now, status);
                return DeviceRuntimeMutationResult.Succeeded(state.ToSnapshot(GetQueueInfoLocked(deviceId)), GetWorkerIndexLocked(deviceId), "ERROR_RECORDED", "Device runtime error was recorded.");
            }
        }

        public DeviceRuntimeMutationResult UpdateCapabilities(int deviceId, DeviceCapabilities capabilities, DateTime now, DeviceIndexUpdateContext context = null)
        {
            lock (gate)
            {
                if (!devices.TryGetValue(deviceId, out var state))
                {
                    return DeviceRuntimeMutationResult.NotFound();
                }

                state.MarkCapabilities(capabilities, now);
                return DeviceRuntimeMutationResult.Succeeded(state.ToSnapshot(GetQueueInfoLocked(deviceId)), GetWorkerIndexLocked(deviceId), "CAPABILITIES_UPDATED", "Device capabilities were updated.");
            }
        }

        public DeviceRuntimeMutationResult BeginDeleting(int deviceId, DateTime now, DeviceIndexUpdateContext context = null)
        {
            lock (gate)
            {
                if (!devices.TryGetValue(deviceId, out var state))
                {
                    return DeviceRuntimeMutationResult.NotFound();
                }

                if (state.IsDeleting)
                {
                    return DeviceRuntimeMutationResult.Conflict(deviceId, "DELETE_IN_PROGRESS", "设备正在删除。");
                }

                state.MarkDeleting(now);
                return DeviceRuntimeMutationResult.Succeeded(state.ToSnapshot(GetQueueInfoLocked(deviceId)), GetWorkerIndexLocked(deviceId), "DELETE_STARTED", "设备删除屏障已建立。");
            }
        }

        public DeviceRuntimeMutationResult ClearDeleting(int deviceId, DateTime now, DeviceIndexUpdateContext context = null)
        {
            lock (gate)
            {
                if (!devices.TryGetValue(deviceId, out var state))
                {
                    return DeviceRuntimeMutationResult.NotFound();
                }

                state.ClearDeleting(now);
                return DeviceRuntimeMutationResult.Succeeded(state.ToSnapshot(GetQueueInfoLocked(deviceId)), GetWorkerIndexLocked(deviceId), "DELETE_CANCELLED", "设备删除屏障已解除。");
            }
        }

        public DeviceRuntimeMutationResult RemoveDevice(int deviceId, DateTime now, DeviceIndexUpdateContext context = null)
        {
            lock (gate)
            {
                if (!devices.TryGetValue(deviceId, out var state))
                {
                    return DeviceRuntimeMutationResult.NotFound();
                }

                RemoveIpIndexLocked(deviceId, state.IpAddress);
                RemoveSdkUserIndexLocked(deviceId, state.SdkUserId);
                RemoveAlarmHandleIndexLocked(deviceId, state.AlarmHandle);
                workerRoutes.Remove(deviceId);
                queueInfos.Remove(deviceId);
                devices.Remove(deviceId);
                state.MarkDeleted(now);
                deletedCount++;

                return DeviceRuntimeMutationResult.Deleted(state.ToSnapshot());
            }
        }

        public DeviceRuntimeMutationResult UpdateQueueInfo(int deviceId, DeviceQueueInfo queueInfo)
        {
            lock (gate)
            {
                if (!devices.ContainsKey(deviceId))
                {
                    return DeviceRuntimeMutationResult.NotFound();
                }

                if (queueInfo == null)
                {
                    queueInfos.Remove(deviceId);
                }
                else
                {
                    queueInfos[deviceId] = queueInfo.Clone();
                }

                var snapshot = devices[deviceId].ToSnapshot(GetQueueInfoLocked(deviceId));
                return DeviceRuntimeMutationResult.Succeeded(snapshot, GetWorkerIndexLocked(deviceId), "QUEUE_INFO_UPDATED", "Queue info was updated.");
            }
        }

        public IReadOnlyList<DeviceRuntimeSnapshot> GetAllSnapshots()
        {
            lock (gate)
            {
                return devices
                    .OrderBy(item => item.Key)
                    .Select(item => item.Value.ToSnapshot(GetQueueInfoLocked(item.Key)))
                    .ToList();
            }
        }

        public DeviceRuntimeRegistrySnapshot GetRegistrySnapshot(DateTime? now = null)
        {
            lock (gate)
            {
                var statusCounts = devices.Values
                    .Select(item => item.ToSnapshot().Status)
                    .GroupBy(status => status)
                    .ToDictionary(group => group.Key, group => group.Count());

                return new DeviceRuntimeRegistrySnapshot(
                    devices.Count,
                    ipIndex.Count,
                    sdkUserIdIndex.Count,
                    alarmHandleIndex.Count,
                    workerRoutes.Count,
                    conflictCount,
                    deletedCount,
                    statusCounts,
                    now ?? DateTime.Now);
            }
        }

        private DeviceRuntimeLookupResult TryGetByDeviceIdLocked(int deviceId)
        {
            if (!devices.TryGetValue(deviceId, out var state))
            {
                return DeviceRuntimeLookupResult.NotFound();
            }

            if (state.Status == DeviceConnectionStatus.Deleted)
            {
                return DeviceRuntimeLookupResult.Deleted(deviceId, state.ToSnapshot(GetQueueInfoLocked(deviceId)));
            }

            return DeviceRuntimeLookupResult.FoundResult(state.ToSnapshot(GetQueueInfoLocked(deviceId)), GetWorkerIndexLocked(deviceId));
        }

        private int CalculateWorkerIndex(int deviceId)
        {
            return DeviceWorkerRouter.CalculateWorkerIndex(deviceId, workerCount);
        }

        private int GetWorkerIndexLocked(int deviceId)
        {
            return workerRoutes.ContainsKey(deviceId) ? workerRoutes[deviceId] : -1;
        }

        private DeviceQueueInfo GetQueueInfoLocked(int deviceId)
        {
            return queueInfos.TryGetValue(deviceId, out var queueInfo) ? queueInfo.Clone() : null;
        }

        private void RemoveIpIndexLocked(int deviceId, string ipAddress)
        {
            var key = DeviceIndexKeyNormalizer.NormalizeIpAddress(ipAddress);
            if (!string.IsNullOrEmpty(key) && ipIndex.TryGetValue(key, out var ownerDeviceId) && ownerDeviceId == deviceId)
            {
                ipIndex.Remove(key);
            }
        }

        private void RemoveSdkUserIndexLocked(int deviceId, int? sdkUserId)
        {
            if (sdkUserId.HasValue && sdkUserIdIndex.TryGetValue(sdkUserId.Value, out var ownerDeviceId) && ownerDeviceId == deviceId)
            {
                sdkUserIdIndex.Remove(sdkUserId.Value);
            }
        }

        private void RemoveAlarmHandleIndexLocked(int deviceId, int? alarmHandle)
        {
            if (alarmHandle.HasValue && alarmHandleIndex.TryGetValue(alarmHandle.Value, out var ownerDeviceId) && ownerDeviceId == deviceId)
            {
                alarmHandleIndex.Remove(alarmHandle.Value);
            }
        }

        private static DeviceRuntimeCreationOptions CopyWithNormalizedIp(DeviceRuntimeCreationOptions options, string normalizedIp)
        {
            return new DeviceRuntimeCreationOptions
            {
                DeviceId = options.DeviceId,
                DeviceName = options.DeviceName,
                Description = options.Description,
                IpAddress = normalizedIp,
                Port = options.Port,
                Username = options.Username,
                Password = options.Password,
                Enabled = options.Enabled,
                Types = (options.Types ?? Enumerable.Empty<DeviceType>()).ToList(),
                CreatedAt = options.CreatedAt
            };
        }
    }
}
