using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Tasks;
using ControlDoor.Devices.Workers;
using ControlDoor.Hikvision;
using ControlDoor.Observability;

namespace ControlDoor.Devices.Management
{
    public sealed class DeviceLifecycleService : IDisposable
    {
        private readonly object gate = new object();
        private readonly DeviceRuntimeRegistry registry;
        private readonly DeviceSdkDispatcher dispatcher;
        private readonly DelayedDeviceTaskScheduler delayedScheduler;
        private readonly IDeviceRepository repository;
        private readonly IHikvisionGateway gateway;
        private readonly DeviceLifecycleOptions options;
        private readonly ServiceLogger logger;
        private readonly Dictionary<int, int> healthFailureCounts = new Dictionary<int, int>();

        private readonly Dictionary<int, int> reArmFailureCounts = new Dictionary<int, int>();
        private readonly Dictionary<int, int> alarmProbeFailureCounts = new Dictionary<int, int>();
        private bool disposed;

        public DeviceLifecycleService(
            DeviceRuntimeRegistry registry,
            DeviceSdkDispatcher dispatcher,
            DelayedDeviceTaskScheduler delayedScheduler,
            IDeviceRepository repository,
            IHikvisionGateway gateway,
            DeviceLifecycleOptions options = null,
            ServiceLogger logger = null)
        {
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            this.delayedScheduler = delayedScheduler;
            this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
            this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
            this.options = options ?? new DeviceLifecycleOptions();
            this.logger = logger;
        }

        public DeviceRuntimeRegistry Registry => registry;

        public DeviceLoadSummary LoadEnabledDevices(bool enqueueLogin)
        {
            ThrowIfDisposed();
            var summary = new DeviceLoadSummary();
            var records = repository.LoadEnabledDevices();
            foreach (var record in records)
            {
                var validation = ValidateRecord(record);
                if (!validation.Success)
                {
                    summary.InvalidCount++;
                    summary.Warnings.Add("设备 " + record.DeviceId + " 配置非法: " + validation.Message);
                    RegisterInvalidConfigRecord(record, validation.Message);
                    continue;
                }

                var result = registry.Register(record.ToRuntimeOptions(DateTime.Now));
                if (!result.Success)
                {
                    if (result.Code == "IP_CONFLICT" || result.Code == "DEVICE_ID_CONFLICT")
                    {
                        summary.ConflictCount++;
                    }
                    else
                    {
                        summary.SkippedCount++;
                    }

                    summary.Warnings.Add("设备 " + record.DeviceId + " 注册失败: " + result.Code + " " + result.Message);
                    continue;
                }

                summary.LoadedCount++;
                summary.LoadedDevices.Add(record);
                if (enqueueLogin)
                {
                    SubmitLogin(record.DeviceId, wait: false, requestId: string.Empty);
                }
            }

            logger?.Info("DeviceLifecycle", "设备加载完成。", new LogFields
            {
                Extra =
                {
                    ["loaded"] = summary.LoadedCount.ToString(),
                    ["skipped"] = summary.SkippedCount.ToString(),
                    ["conflict"] = summary.ConflictCount.ToString(),
                    ["invalid"] = summary.InvalidCount.ToString()
                }
            });
            return summary;
        }

        private void RegisterInvalidConfigRecord(DeviceRecord record, string message)
        {
            if (record == null || record.DeviceId <= 0)
            {
                return;
            }

            var options = record.ToRuntimeOptions(DateTime.Now);
            options.DeviceName = string.IsNullOrWhiteSpace(options.DeviceName) ? "device-" + record.DeviceId : options.DeviceName;
            options.IpAddress = string.IsNullOrWhiteSpace(options.IpAddress) ? "invalid-" + record.DeviceId : options.IpAddress;
            options.Port = options.Port <= 0 || options.Port > 65535 ? 8000 : options.Port;
            options.Username = string.IsNullOrWhiteSpace(options.Username) ? "admin" : options.Username;
            options.Password = options.Password ?? string.Empty;
            options.Enabled = true;

            var result = registry.Register(options);
            if (!result.Success)
            {
                return;
            }

            registry.MarkInvalidConfig(
                record.DeviceId,
                DeviceRuntimeError.Create("ValidateDeviceConfig", "INVALID_CONFIG", message, DateTime.Now, retryable: false),
                DateTime.Now);
        }

        public IReadOnlyList<DeviceRuntimeSnapshot> GetDeviceSnapshots(bool includeDisabled)
        {
            var snapshots = registry.GetAllSnapshots();
            if (!includeDisabled)
            {
                snapshots = snapshots.Where(item => item.Enabled && item.Status != DeviceConnectionStatus.Disabled).ToList();
            }

            return snapshots;
        }

        public DeviceOperationResult RegisterDevice(DeviceRecord record, bool persist)
        {
            var validation = ValidateRecord(record);
            if (!validation.Success)
            {
                return new DeviceOperationResult
                {
                    Success = false,
                    Code = "INVALID_ARGUMENT",
                    DeviceId = record == null ? 0 : record.DeviceId,
                    Message = validation.Message
                };
            }

            if (registry.TryGetByDeviceId(record.DeviceId).Found || repository.ExistsDeviceId(record.DeviceId))
            {
                return new DeviceOperationResult
                {
                    Success = false,
                    Code = "INVALID_ARGUMENT",
                    DeviceId = record.DeviceId,
                    Message = "设备 ID 已存在。"
                };
            }

            if (registry.TryGetByIpAddress(record.IpAddress).Found)
            {
                return new DeviceOperationResult
                {
                    Success = false,
                    Code = "INVALID_ARGUMENT",
                    DeviceId = record.DeviceId,
                    Message = "设备 IP 已存在。"
                };
            }

            if (repository.ExistsIpAddress(record.IpAddress))
            {
                return new DeviceOperationResult
                {
                    Success = false,
                    Code = "INVALID_ARGUMENT",
                    DeviceId = record.DeviceId,
                    Message = "设备 IP 已存在。"
                };
            }

            if (persist)
            {
                var insert = repository.InsertDevice(record);
                if (!insert.Success)
                {
                    return new DeviceOperationResult
                    {
                        Success = false,
                        Code = insert.Code,
                        DeviceId = record.DeviceId,
                        Message = insert.Message
                    };
                }
            }

            var register = registry.Register(record.ToRuntimeOptions(DateTime.Now));
            if (!register.Success)
            {
                if (persist)
                {
                    var rollback = repository.DeleteDevice(record.DeviceId);
                    if (!rollback.Success)
                    {
                        logger?.Error("DeviceLifecycle", "设备运行时注册失败，JSON 设备清单回滚失败: " + rollback.Message, null);
                        return new DeviceOperationResult
                        {
                            Success = false,
                            Code = register.Code,
                            DeviceId = record.DeviceId,
                            Message = register.Message + "；JSON 设备清单回滚失败，请人工检查 devices.json: " + rollback.Message
                        };
                    }
                }

                return new DeviceOperationResult
                {
                    Success = false,
                    Code = register.Code,
                    DeviceId = record.DeviceId,
                    Message = register.Message
                };
            }

            return DeviceOperationResult.FromSnapshot(true, "OK", "设备已新增。", register.Snapshot);
        }

        public DeviceOperationResult SubmitLogin(int deviceId, bool wait, string requestId)
        {
            var task = CreateLoginTask(deviceId, requestId);
            if (wait)
            {
                var result = dispatcher.SubmitAndWaitAsync(task).GetAwaiter().GetResult();
                return FromTaskResult(result);
            }

            var submitted = dispatcher.Submit(task);
            return new DeviceOperationResult
            {
                Success = submitted.Accepted,
                Code = submitted.Accepted ? "OK" : submitted.ImmediateResult.Code,
                DeviceId = deviceId,
                Message = submitted.Accepted ? "登录任务已投递。" : submitted.ImmediateResult.Message,
                TaskResult = submitted.ImmediateResult,
                Snapshot = registry.TryGetByDeviceId(deviceId).Snapshot
            };
        }

        public DeviceOperationResult SubmitHealthCheck(int deviceId, bool wait, string requestId)
        {
            var task = CreateHealthCheckTask(deviceId, requestId);
            if (wait)
            {
                var result = dispatcher.SubmitAndWaitAsync(task).GetAwaiter().GetResult();
                return FromTaskResult(result);
            }

            var submitted = dispatcher.Submit(task);
            return new DeviceOperationResult
            {
                Success = submitted.Accepted,
                Code = submitted.Accepted ? "OK" : submitted.ImmediateResult.Code,
                DeviceId = deviceId,
                Message = submitted.Accepted ? "状态检测任务已投递。" : submitted.ImmediateResult.Message,
                TaskResult = submitted.ImmediateResult,
                Snapshot = registry.TryGetByDeviceId(deviceId).Snapshot
            };
        }

        public DeviceOperationResult DisconnectDevice(int deviceId, string requestId)
        {
            var lookup = registry.TryGetByDeviceId(deviceId);
            if (!lookup.Found)
            {
                return new DeviceOperationResult
                {
                    Success = false,
                    Code = "NOT_FOUND",
                    DeviceId = deviceId,
                    Message = "设备不存在。"
                };
            }

            LogManualOperationRequested(deviceId, requestId, "ManualDisconnect");
            CancelDelayedReconnect(deviceId);
            CancelDelayedReArm(deviceId);
            var task = CreateDisconnectTask(deviceId, "ManualDisconnect", DeviceConnectionStatus.Disconnected, requestId);
            var result = dispatcher.SubmitAndWaitAsync(task).GetAwaiter().GetResult();
            var operation = FromTaskResult(result);
            LogDeviceOperationResult("Manual disconnect completed.", requestId, "ManualDisconnect", operation);
            return operation;
        }

        public DeviceOperationResult ReconnectDevice(int deviceId, bool force, string requestId)
        {
            var lookup = registry.TryGetByDeviceId(deviceId);
            if (!lookup.Found)
            {
                return new DeviceOperationResult
                {
                    Success = false,
                    Code = "NOT_FOUND",
                    DeviceId = deviceId,
                    Message = "设备不存在。"
                };
            }

            LogManualOperationRequested(deviceId, requestId, "ManualReconnect");
            if (!force && lookup.Snapshot.IsConnected)
            {
                return DeviceOperationResult.FromSnapshot(true, "OK", "设备已在线。", lookup.Snapshot);
            }

            CancelDelayedReconnect(deviceId);
            CancelDelayedReArm(deviceId);
            registry.ResetReconnect(deviceId, DateTime.Now);
            registry.ClearManualAlarmDisarm(deviceId, DateTime.Now);
            var cleanup = CreateDisconnectTask(deviceId, "ReconnectCleanup", DeviceConnectionStatus.Offline, requestId);
            cleanup.Priority = force ? DeviceTaskPriority.Critical : DeviceTaskPriority.High;
            var cleanupResult = dispatcher.SubmitAndWaitAsync(cleanup).GetAwaiter().GetResult();
            if (!cleanupResult.Success && cleanupResult.Code != "OK")
            {
                logger?.Warn("DeviceLifecycle", "重连前清理失败，已停止重连以保留设备侧布防状态。", new LogFields { DeviceId = deviceId, ErrorCode = cleanupResult.Code });
                return FromTaskResult(cleanupResult);
            }

            var login = SubmitLogin(deviceId, wait: true, requestId: requestId);
            LogDeviceOperationResult("Manual reconnect completed.", requestId, "ManualReconnect", login);
            return login;
        }

        public DeviceOperationResult DisarmDeviceAlarm(int deviceId, string requestId)
        {
            var lookup = registry.TryGetByDeviceId(deviceId);
            if (!lookup.Found)
            {
                return new DeviceOperationResult
                {
                    Success = false,
                    Code = "NOT_FOUND",
                    DeviceId = deviceId,
                    Message = "设备不存在。"
                };
            }

            LogManualOperationRequested(deviceId, requestId, "ManualDisarmAlarm");
            CancelDelayedReArm(deviceId);
            if (!lookup.Snapshot.AlarmHandle.HasValue)
            {
                registry.MarkAlarmManuallyDisarmed(deviceId, DateTime.Now);
                return DeviceOperationResult.FromSnapshot(true, "OK", "设备未布防，跳过撤防。", registry.TryGetByDeviceId(deviceId).Snapshot);
            }

            var result = dispatcher.SubmitAndWaitAsync(CreateDisarmAlarmTask(deviceId, requestId, manuallyDisarmed: true)).GetAwaiter().GetResult();
            var operation = FromTaskResult(result);
            LogDeviceOperationResult("Manual disarm completed.", requestId, "ManualDisarmAlarm", operation);
            return operation;
        }

        public DeviceOperationResult RearmDeviceAlarm(int deviceId, bool force, string requestId)
        {
            var lookup = registry.TryGetByDeviceId(deviceId);
            if (!lookup.Found)
            {
                return new DeviceOperationResult
                {
                    Success = false,
                    Code = "NOT_FOUND",
                    DeviceId = deviceId,
                    Message = "设备不存在。"
                };
            }

            LogManualOperationRequested(deviceId, requestId, "ManualRearmAlarm");
            if (!lookup.Snapshot.IsConnected || !lookup.Snapshot.SdkUserId.HasValue)
            {
                return DeviceOperationResult.FromSnapshot(false, "DEVICE_ERROR", "设备未在线，不能重新布防。", lookup.Snapshot);
            }

            if (!force && lookup.Snapshot.AlarmHandle.HasValue)
            {
                return DeviceOperationResult.FromSnapshot(true, "OK", "设备已布防。", lookup.Snapshot);
            }

            CancelDelayedReArm(deviceId);
            registry.ClearManualAlarmDisarm(deviceId, DateTime.Now);
            if (lookup.Snapshot.AlarmHandle.HasValue)
            {
                var disarm = dispatcher.SubmitAndWaitAsync(CreateDisarmAlarmTask(deviceId, requestId, manuallyDisarmed: false)).GetAwaiter().GetResult();
                if (!disarm.Success)
                {
                    return FromTaskResult(disarm);
                }
            }

            var arm = SubmitArmAlarm(deviceId, wait: true, requestId: requestId);
            LogDeviceOperationResult("Manual rearm completed.", requestId, "ManualRearmAlarm", arm);
            return arm;
        }

        public DeviceOperationResult DeleteDevice(int deviceId, bool disconnectFirst, string requestId)
        {
            var initialLookup = registry.TryGetByDeviceId(deviceId);
            if (!initialLookup.Found)
            {
                var missingDelete = repository.DeleteDevice(deviceId);
                return new DeviceOperationResult
                {
                    Success = missingDelete.Success,
                    Code = missingDelete.Success ? "OK" : missingDelete.Code,
                    DeviceId = deviceId,
                    Message = missingDelete.Success ? "设备已删除。" : missingDelete.Message
                };
            }

            var barrier = registry.BeginDeleting(deviceId, DateTime.Now);
            if (!barrier.Success)
            {
                return new DeviceOperationResult
                {
                    Success = false,
                    Code = barrier.Code,
                    DeviceId = deviceId,
                    Message = barrier.Message,
                    Snapshot = barrier.Snapshot
                };
            }

            var leaseId = barrier.DeleteLeaseId;
            var delayedTasks = delayedScheduler == null
                ? new List<DelayedDeviceTask>()
                : delayedScheduler.TakeByDevice(deviceId).ToList();
            var lookup = registry.TryGetByDeviceId(deviceId);
            var snapshot = lookup.Snapshot;
            var checkpoint = barrier.DeleteCheckpoint ?? snapshot;
            var hasActiveAlarm = checkpoint != null &&
                (checkpoint.AlarmHandle.HasValue ||
                 checkpoint.StaleAlarmHandle.HasValue ||
                 (checkpoint.PendingAlarmHandles != null && checkpoint.PendingAlarmHandles.Count > 0));
            if (lookup.Found && !disconnectFirst &&
                (hasActiveAlarm ||
                 (checkpoint != null &&
                  (checkpoint.IsConnected ||
                   checkpoint.Status == DeviceConnectionStatus.Connecting ||
                   checkpoint.SdkUserId.HasValue))))
            {
                var restored = registry.ClearDeleting(deviceId, DateTime.Now, null, leaseId);
                delayedScheduler?.RestoreTasks(delayedTasks);
                return new DeviceOperationResult
                {
                    Success = false,
                    Code = "DEVICE_CONNECTED",
                    DeviceId = deviceId,
                    Message = "设备仍在线或存在待清理布防句柄，disconnectFirst=false 时拒绝删除。",
                    Snapshot = restored.Snapshot ?? registry.TryGetByDeviceId(deviceId).Snapshot
                };
            }

            try
            {
                if (lookup.Found && disconnectFirst)
                {
                    var cleanup = CreateDisconnectTask(deviceId, "DeleteDeviceCleanup", DeviceConnectionStatus.Offline, requestId, leaseId);
                    cleanup.Priority = DeviceTaskPriority.Critical;
                    var cleanupResult = dispatcher.SubmitAndWaitAsync(cleanup).GetAwaiter().GetResult();
                    if (!cleanupResult.Success && cleanupResult.Code != "OK")
                    {
                        var restored = registry.ClearDeleting(deviceId, DateTime.Now, null, leaseId);
                        delayedScheduler?.RestoreTasks(delayedTasks);
                        return new DeviceOperationResult
                        {
                            Success = false,
                            Code = cleanupResult.Code,
                            DeviceId = deviceId,
                            Message = cleanupResult.Message,
                            TaskResult = cleanupResult,
                            Snapshot = restored.Snapshot ?? registry.TryGetByDeviceId(deviceId).Snapshot
                        };
                    }
                }

                CancelDelayedReconnect(deviceId);
                CancelDelayedReArm(deviceId);
                // 删除前 best-effort 排空历史遗留的待清理 SDK 会话（补偿登出失败留下的）。
                // 仍有 pending 时拒绝删除，避免 RemoveDevice 丢失最后的会话清理记录。
                if (!DrainPendingSdkLogoutsForDevice(deviceId, leaseId))
                {
                    var restored = registry.ClearDeleting(deviceId, DateTime.Now, null, leaseId);
                    delayedScheduler?.RestoreTasks(delayedTasks);
                    return new DeviceOperationResult
                    {
                        Success = false,
                        Code = "PENDING_LOGOUT",
                        DeviceId = deviceId,
                        Message = "仍有 SDK 会话未成功登出，已保留 pending 清理记录。",
                        Snapshot = restored.Snapshot ?? registry.TryGetByDeviceId(deviceId).Snapshot
                    };
                }

                var delete = repository.DeleteDevice(deviceId);
                if (!delete.Success)
                {
                    var restored = registry.ClearDeleting(deviceId, DateTime.Now, null, leaseId);
                    delayedScheduler?.RestoreTasks(delayedTasks);
                    return new DeviceOperationResult
                    {
                        Success = false,
                        Code = delete.Code,
                        DeviceId = deviceId,
                        Message = delete.Message,
                        Snapshot = restored.Snapshot ?? registry.TryGetByDeviceId(deviceId).Snapshot
                    };
                }

                var removed = registry.RemoveDevice(deviceId, DateTime.Now, null, leaseId);
                if (!removed.Success && removed.Code != "DEVICE_NOT_FOUND")
                {
                    var restored = registry.ClearDeleting(deviceId, DateTime.Now, null, leaseId);
                    delayedScheduler?.RestoreTasks(delayedTasks);
                    return new DeviceOperationResult
                    {
                        Success = false,
                        Code = removed.Code,
                        DeviceId = deviceId,
                        Message = removed.Message,
                        Snapshot = restored.Snapshot ?? registry.TryGetByDeviceId(deviceId).Snapshot
                    };
                }

                delayedScheduler?.DiscardTasks(delayedTasks, "device deleted");
                return new DeviceOperationResult
                {
                    Success = true,
                    Code = "OK",
                    DeviceId = deviceId,
                    Message = "设备已删除。"
                };
            }
            catch (Exception ex)
            {
                var restored = registry.ClearDeleting(deviceId, DateTime.Now, null, leaseId);
                delayedScheduler?.RestoreTasks(delayedTasks);
                return new DeviceOperationResult
                {
                    Success = false,
                    Code = "INTERNAL_ERROR",
                    DeviceId = deviceId,
                    Message = ex.Message,
                    Snapshot = restored.Snapshot ?? registry.TryGetByDeviceId(deviceId).Snapshot
                };
            }
        }

        // 删除/断开前同步排空历史遗留的待清理 SDK 会话；与 DrainPendingSdkLogoutsAsync（登录路径）保持一致的语义：
        // 登出成功或返回 17（设备端已无此会话）即清除；其余失败保留 pending，避免静默丢弃待清理记录。
        // 每次 SDK 登出都通过同设备 worker，避免删除线程绕过 SDK 串行化约束。
        private bool DrainPendingSdkLogoutsForDevice(int deviceId, string deletingLeaseId)
        {
            var pending = registry.GetPendingSdkLogouts(deviceId);
            if (pending.Count == 0)
            {
                return true;
            }

            foreach (var userId in pending)
            {
                var task = CreatePendingLogoutTask(deviceId, userId, "DeleteDevicePendingLogout", deletingLeaseId);
                var result = dispatcher.SubmitAndWaitAsync(task).GetAwaiter().GetResult();
                if (result.Success || result.SdkErrorCode == 17)
                {
                    if (!string.IsNullOrWhiteSpace(deletingLeaseId))
                    {
                        var current = registry.TryGetByDeviceId(deviceId).Snapshot;
                        if (current == null || !registry.RecordCleanedDuringDeleteSdkUser(
                            deviceId,
                            userId,
                            DateTime.Now,
                            current.SdkUserId,
                            current.RuntimeVersion,
                            deletingLeaseId).Success)
                        {
                            logger?.Warn("DeviceLifecycle", "删除前排空 SDK 会话成功，但运行时清理记录失败。", new LogFields
                            {
                                DeviceId = deviceId,
                                OperationName = "DeviceLogout",
                                ErrorCode = "DELETE_CLEANUP_STATE_REJECTED"
                            });
                        }
                    }
                    else
                    {
                        registry.ClearPendingSdkLogout(deviceId, userId, DateTime.Now, null, false, null, null, null);
                    }

                    continue;
                }

                logger?.Warn("DeviceLifecycle", "Drain of pending SDK logout " + userId + " failed before delete; record retained.", new LogFields
                {
                    DeviceId = deviceId,
                    OperationName = "DeviceLogout",
                    ErrorCode = result.Code
                });
            }

            return registry.GetPendingSdkLogouts(deviceId).Count == 0;
        }

        public void StopAllDevicesBestEffort()
        {
            var snapshots = registry.GetAllSnapshots()
                .Where(item => item.IsDeleting ||
                    item.SdkUserId.HasValue ||
                    item.AlarmHandle.HasValue ||
                    item.StaleAlarmHandle.HasValue ||
                    (item.PendingAlarmHandles != null && item.PendingAlarmHandles.Count > 0) ||
                    (item.PendingSdkLogoutUserIds != null && item.PendingSdkLogoutUserIds.Count > 0) ||
                    item.IsConnected)
                .ToList();
            foreach (var snapshot in snapshots)
            {
                try
                {
                    CancelDelayedReArm(snapshot.DeviceId);
                    var task = CreateDisconnectTask(
                        snapshot.DeviceId,
                        "ServiceStopCleanup",
                        DeviceConnectionStatus.Offline,
                        string.Empty,
                        snapshot.IsDeleting ? snapshot.DeletingLeaseId : null);
                    task.Priority = DeviceTaskPriority.Critical;
                    dispatcher.SubmitAndWaitAsync(task).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    logger?.Warn("DeviceLifecycle", "服务停止清理设备失败: " + ex.Message, new LogFields { DeviceId = snapshot.DeviceId });
                }
            }
        }

        // 服务停止前取消指定设备的延迟 reconnect/rearm 任务，确保 delayedScheduler 即便有残存任务也不会触发重新登录/布防。
        // 与单设备路径的 CancelDelayedReconnect/CancelDelayedReArm 等价，集中暴露给 Host 停止流程调用。
        public void CancelDelayedDeviceTasksForDevice(int deviceId)
        {
            CancelDelayedReconnect(deviceId);
            CancelDelayedReArm(deviceId);
        }

        public void Dispose()
        {
            disposed = true;
        }

        private DeviceSdkTask CreateLoginTask(int deviceId, string requestId)
        {
            var task = new DeviceSdkTask(deviceId, DeviceTaskType.Login, "DeviceLogin", async context =>
            {
                var started = DateTime.Now;
                var snapshot = context.SnapshotBeforeExecution;
                if (snapshot == null)
                {
                    return DeviceTaskResult.FromTask(context.Task, false, "NOT_FOUND", "设备不存在。", DeviceConnectionStatus.Unknown, started, DateTime.Now);
                }

                if (snapshot.Status == DeviceConnectionStatus.InvalidConfig || snapshot.Status == DeviceConnectionStatus.Disabled)
                {
                    return DeviceTaskResult.FromTask(context.Task, false, "INVALID_ARGUMENT", "设备配置无效或已停用。", snapshot.Status, started, DateTime.Now);
                }

                if (snapshot.IsConnected && snapshot.SdkUserId.HasValue)
                {
                    return DeviceTaskResult.FromTask(context.Task, true, "OK", "设备已在线，跳过重复登录。", snapshot.Status, started, DateTime.Now);
                }

                var connection = context.Registry.GetConnectionOptions(deviceId);
                if (connection == null)
                {
                    return DeviceTaskResult.FromTask(context.Task, false, "NOT_FOUND", "设备不存在。", DeviceConnectionStatus.Unknown, started, DateTime.Now);
                }

                var connecting = context.Registry.MarkConnecting(deviceId, DateTime.Now);
                if (!connecting.Success)
                {
                    return DeviceTaskResult.FromTask(context.Task, false, connecting.Code, connecting.Message, DeviceConnectionStatus.Disconnecting, started, DateTime.Now);
                }

                var connectingRuntimeVersion = connecting.Snapshot == null ? (long?)null : connecting.Snapshot.RuntimeVersion;
                int? loggedInUserId = null;
                int? registeredSdkUserId = null;
                long? registeredRuntimeVersion = connectingRuntimeVersion;
                var sessionRegistered = false;

                try
                {
                    var login = await gateway.LoginAsync(new LoginRequest
                    {
                        IpAddress = connection.IpAddress,
                        Port = connection.Port,
                        UserName = connection.Username,
                        Password = connection.Password,
                        TimeoutMilliseconds = options.LoginTimeoutMs
                    }, context.CancellationToken).ConfigureAwait(false);
                    var serial = login.DeviceInfo == null ? string.Empty : login.DeviceInfo.SerialNumber;
                    loggedInUserId = login.UserId;
                    var register = context.Registry.RegisterSdkUserId(
                        deviceId,
                        login.UserId,
                        serial,
                        DateTime.Now,
                        context: null,
                        publishOnline: false,
                        expectedSdkUserId: null,
                        expectedRuntimeVersion: connectingRuntimeVersion);
                    sessionRegistered = register.Success;
                    if (!register.Success)
                    {
                        // Best-effort：注册失败时登出新会话，避免泄露；登出失败不遮蔽原始错误。
                        try
                        {
                            await gateway.LogoutAsync(new LogoutRequest { UserId = login.UserId }, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception logoutEx)
                        {
                            var current = context.Registry.TryGetByDeviceId(deviceId).Snapshot;
                            var allowDeleting = current != null &&
                                current.IsDeleting &&
                                !string.IsNullOrWhiteSpace(current.DeletingLeaseId);
                            RecordCompensationLogoutFailure(
                                context,
                                deviceId,
                                login.UserId,
                                logoutEx,
                                expectedSdkUserId: current == null ? (int?)null : current.SdkUserId,
                                expectedRuntimeVersion: current == null ? connectingRuntimeVersion : current.RuntimeVersion,
                                allowDeleting: allowDeleting,
                                expectedDeletingLeaseId: allowDeleting ? current.DeletingLeaseId : null);
                        }

                        var conflictError = DeviceRuntimeError.Create(
                            "DeviceLogin",
                            register.Code,
                            register.Message,
                            DateTime.Now,
                            sdkErrorCode: null,
                            retryable: true);
                        var conflictFailed = context.Registry.MarkLoginFailed(
                            deviceId,
                            conflictError,
                            DateTime.Now,
                            faulted: false,
                            context: null,
                            expectedSdkUserId: null,
                            expectedRuntimeVersion: connectingRuntimeVersion);
                        if (conflictFailed.Success)
                        {
                            ScheduleReconnect(
                                deviceId,
                                conflictError.Message,
                                conflictFailed.Snapshot == null ? (long?)null : conflictFailed.Snapshot.RuntimeVersion);
                        }

                        return DeviceTaskResult.FromTask(context.Task, false, register.Code, register.Message, DeviceConnectionStatus.Offline, started, DateTime.Now);
                    }

                    registeredSdkUserId = login.UserId;
                    registeredRuntimeVersion = register.Snapshot == null
                        ? connectingRuntimeVersion
                        : register.Snapshot.RuntimeVersion;
                    if (IsDeviceDeleting(deviceId))
                    {
                        return await AbortLoginAfterDeleteAsync(context, deviceId, login.UserId, started).ConfigureAwait(false);
                    }

                    // 设备此刻可达，先 best-effort 排空历史遗留的待清理 SDK 会话（补偿登出失败留下的）。
                    var drained = await DrainPendingSdkLogoutsAsync(
                        context,
                        deviceId,
                        registeredSdkUserId,
                        registeredRuntimeVersion).ConfigureAwait(false);
                    if (drained == null)
                    {
                        if (IsDeviceDeleting(deviceId))
                        {
                            return await AbortLoginAfterDeleteAsync(context, deviceId, login.UserId, started).ConfigureAwait(false);
                        }

                        await LogoutSupersededLoginSessionAsync(
                            context,
                            deviceId,
                            login.UserId,
                            registeredSdkUserId,
                            registeredRuntimeVersion).ConfigureAwait(false);
                        return DeviceTaskResult.FromTask(context.Task, false, "INVALID_ARGUMENT", "设备运行时已发生变化，忽略旧登录回调。", DeviceConnectionStatus.Offline, started, DateTime.Now);
                    }

                    registeredRuntimeVersion = drained.RuntimeVersion;
                    if (drained.IsDeleting)
                    {
                        return await AbortLoginAfterDeleteAsync(context, deviceId, login.UserId, started).ConfigureAwait(false);
                    }

                    var staleCleanup = await CloseStaleAlarmBeforeRearmAsync(context, drained, started).ConfigureAwait(false);
                    if (staleCleanup != null)
                    {
                        return staleCleanup;
                    }

                    var currentAfterStaleCleanup = context.Registry.TryGetByDeviceId(deviceId).Snapshot;
                    if (currentAfterStaleCleanup == null ||
                        currentAfterStaleCleanup.IsDeleting ||
                        currentAfterStaleCleanup.SdkUserId != registeredSdkUserId)
                    {
                        if (currentAfterStaleCleanup != null && currentAfterStaleCleanup.IsDeleting)
                        {
                            return await AbortLoginAfterDeleteAsync(context, deviceId, login.UserId, started).ConfigureAwait(false);
                        }

                        await LogoutSupersededLoginSessionAsync(
                            context,
                            deviceId,
                            login.UserId,
                            registeredSdkUserId,
                            registeredRuntimeVersion).ConfigureAwait(false);
                        return DeviceTaskResult.FromTask(context.Task, false, "INVALID_ARGUMENT", "设备运行时已发生变化，忽略旧登录回调。", DeviceConnectionStatus.Offline, started, DateTime.Now);
                    }

                    registeredRuntimeVersion = currentAfterStaleCleanup.RuntimeVersion;
                    var publish = context.Registry.PromoteRegisteredSdkSession(
                        deviceId,
                        DateTime.Now,
                        context: null,
                        expectedSdkUserId: registeredSdkUserId,
                        expectedRuntimeVersion: registeredRuntimeVersion);
                    if (!publish.Success)
                    {
                        if (IsDeviceDeleting(deviceId))
                        {
                            return await AbortLoginAfterDeleteAsync(context, deviceId, login.UserId, started).ConfigureAwait(false);
                        }

                        await LogoutSupersededLoginSessionAsync(
                            context,
                            deviceId,
                            login.UserId,
                            registeredSdkUserId,
                            registeredRuntimeVersion).ConfigureAwait(false);
                        return DeviceTaskResult.FromTask(context.Task, false, publish.Code, publish.Message, DeviceConnectionStatus.Offline, started, DateTime.Now);
                    }

                    ClearHealthFailures(deviceId);
                    LogLifecycleSuccess(context, "设备登录成功。", started, fields =>
                    {
                        fields.Extra["userId"] = login.UserId.ToString();
                        fields.Extra["serialNumber"] = serial;
                        fields.Extra["ipAddress"] = connection.IpAddress;
                        fields.Extra["port"] = connection.Port.ToString();
                        fields.Extra["alarmEnabled"] = options.AlarmEnabled.ToString();
                    });

                    if (options.AlarmEnabled)
                    {
                        SubmitArmAlarm(deviceId, wait: false, requestId: requestId);
                    }

                    return DeviceTaskResult.FromTask(context.Task, true, "OK", "登录成功。", DeviceConnectionStatus.Online, started, DateTime.Now);
                }
                catch (Exception ex)
                {
                    var error = ToRuntimeError("DeviceLogin", ex, DateTime.Now, retryable: true);
                    if (loggedInUserId.HasValue && (!sessionRegistered || registeredSdkUserId == loggedInUserId.Value))
                    {
                        if (!sessionRegistered || CanCleanupRegisteredLoginSession(deviceId, loggedInUserId.Value, registeredRuntimeVersion))
                        {
                            await LogoutSupersededLoginSessionAsync(
                                context,
                                deviceId,
                                loggedInUserId.Value,
                                sessionRegistered ? registeredSdkUserId : null,
                                sessionRegistered ? registeredRuntimeVersion : connectingRuntimeVersion).ConfigureAwait(false);
                        }
                    }

                    var expectedUserId = sessionRegistered ? registeredSdkUserId : null;
                    var expectedVersion = sessionRegistered ? registeredRuntimeVersion : connectingRuntimeVersion;
                    var failed = context.Registry.MarkLoginFailed(
                        deviceId,
                        error,
                        DateTime.Now,
                        faulted: false,
                        context: null,
                        expectedSdkUserId: expectedUserId,
                        expectedRuntimeVersion: expectedVersion);
                    if (failed.Success)
                    {
                        ScheduleReconnect(
                            deviceId,
                            error.Message,
                            failed.Snapshot == null ? (long?)null : failed.Snapshot.RuntimeVersion);
                        LogLifecycleFailure(context, "Device login failed and reconnect was scheduled.", started, error);
                    }
                    else
                    {
                        LogLifecycleFailure(context, "Device login failed; stale runtime callback was ignored.", started, error);
                    }

                    var result = DeviceTaskResult.FromTask(context.Task, false, error.Code, error.Message, DeviceConnectionStatus.Offline, started, DateTime.Now);
                    result.SdkErrorCode = error.SdkErrorCode;
                    result.Retryable = true;
                    return result;
                }
            });
            task.Priority = DeviceTaskPriority.High;
            task.TimeoutMilliseconds = options.LoginTimeoutMs;
            task.RequestId = requestId ?? string.Empty;
            return task;
        }

        private bool IsDeviceDeleting(int deviceId)
        {
            var snapshot = registry.TryGetByDeviceId(deviceId).Snapshot;
            return snapshot != null && snapshot.IsDeleting;
        }

        private async System.Threading.Tasks.Task<DeviceTaskResult> AbortLoginAfterDeleteAsync(DeviceTaskContext context, int deviceId, int userId, DateTime started)
        {
            var deleting = context.Registry.TryGetByDeviceId(deviceId).Snapshot;
            var leaseId = deleting != null && deleting.IsDeleting ? deleting.DeletingLeaseId : null;
            var canMutate = deleting != null &&
                deleting.IsDeleting &&
                !string.IsNullOrWhiteSpace(leaseId) &&
                deleting.SdkUserId == userId;
            var logoutGone = false;

            try
            {
                await gateway.LogoutAsync(new LogoutRequest { UserId = userId }, CancellationToken.None).ConfigureAwait(false);
                logoutGone = true;
            }
            catch (Exception ex)
            {
                var error = ToRuntimeError("DeviceLogout", ex, DateTime.Now, retryable: false);
                logoutGone = error.SdkErrorCode == 17;
                if (!logoutGone && canMutate)
                {
                    var current = context.Registry.TryGetByDeviceId(deviceId).Snapshot;
                    if (current != null &&
                        current.IsDeleting &&
                        string.Equals(current.DeletingLeaseId, leaseId, StringComparison.Ordinal) &&
                        current.SdkUserId == userId)
                    {
                        RecordCompensationLogoutFailure(
                            context,
                            deviceId,
                            userId,
                            ex,
                            expectedSdkUserId: userId,
                            expectedRuntimeVersion: current.RuntimeVersion,
                            allowDeleting: true,
                            expectedDeletingLeaseId: leaseId);
                    }
                }
            }

            if (logoutGone && canMutate)
            {
                var current = context.Registry.TryGetByDeviceId(deviceId).Snapshot;
                if (current != null &&
                    current.IsDeleting &&
                    string.Equals(current.DeletingLeaseId, leaseId, StringComparison.Ordinal) &&
                    current.SdkUserId == userId)
                {
                    var cleaned = context.Registry.RecordCleanedDuringDeleteSdkUser(
                        deviceId,
                        userId,
                        DateTime.Now,
                        expectedSdkUserId: userId,
                        expectedRuntimeVersion: current.RuntimeVersion,
                        expectedDeletingLeaseId: leaseId);
                    if (!cleaned.Success)
                    {
                        logger?.Warn("DeviceLifecycle", "删除流程终止登录后，SDK 会话已登出但运行时清理记录失败。", new LogFields
                        {
                            DeviceId = deviceId,
                            OperationName = "DeviceLogout",
                            ErrorCode = "DELETE_CLEANUP_STATE_REJECTED"
                        });
                    }
                }
            }

            return DeviceTaskResult.FromTask(context.Task, false, "DEVICE_DELETING", "设备已进入删除流程，已终止本次登录。", DeviceConnectionStatus.Disconnecting, started, DateTime.Now);
        }

        private bool CanCleanupRegisteredLoginSession(int deviceId, int userId, long? expectedRuntimeVersion)
        {
            var snapshot = registry.TryGetByDeviceId(deviceId).Snapshot;
            return snapshot != null &&
                !snapshot.IsDeleting &&
                snapshot.SdkUserId == userId &&
                (!expectedRuntimeVersion.HasValue || snapshot.RuntimeVersion == expectedRuntimeVersion.Value);
        }

        private async System.Threading.Tasks.Task<DeviceRuntimeMutationResult> LogoutSupersededLoginSessionAsync(
            DeviceTaskContext context,
            int deviceId,
            int userId,
            int? expectedSdkUserId,
            long? expectedRuntimeVersion)
        {
            try
            {
                await gateway.LogoutAsync(new LogoutRequest { UserId = userId }, CancellationToken.None).ConfigureAwait(false);
                return null;
            }
            catch (Exception logoutEx)
            {
                return RecordCompensationLogoutFailure(
                    context,
                    deviceId,
                    userId,
                    logoutEx,
                    expectedSdkUserId,
                    expectedRuntimeVersion);
            }
        }

        // 补偿登出失败：区分 17（设备端已无此会话）与真实失败；真实失败时保留待清理会话，供下次登录重试，避免泄漏设备端 SDK 会话。
        private DeviceRuntimeMutationResult RecordCompensationLogoutFailure(
            DeviceTaskContext context,
            int deviceId,
            int userId,
            Exception ex,
            int? expectedSdkUserId = null,
            long? expectedRuntimeVersion = null,
            bool allowDeleting = false,
            string expectedDeletingLeaseId = null)
        {
            var error = ToRuntimeError("DeviceLogout", ex, DateTime.Now, retryable: false);
            if (error.SdkErrorCode == 17)
            {
                logger?.Info("DeviceLifecycle", "Compensation logout reported the SDK session was already gone; no pending cleanup recorded.", new LogFields
                {
                    DeviceId = deviceId,
                    OperationName = "DeviceLogout",
                    ErrorCode = error.Code
                });
                return null;
            }

            if (allowDeleting && string.IsNullOrWhiteSpace(expectedDeletingLeaseId))
            {
                return null;
            }

            var recorded = context.Registry.RecordPendingSdkLogout(
                deviceId,
                userId,
                DateTime.Now,
                context: null,
                allowDeleting: allowDeleting,
                expectedSdkUserId: expectedSdkUserId,
                expectedRuntimeVersion: expectedRuntimeVersion,
                expectedDeletingLeaseId: expectedDeletingLeaseId);
            logger?.Warn("DeviceLifecycle", "Compensation logout failed; SDK session " + userId + " retained for retry on next login.", new LogFields
            {
                DeviceId = deviceId,
                OperationName = "DeviceLogout",
                ErrorCode = error.Code
            });
            return recorded;
        }

        // 登录成功且设备可达后，best-effort 排空历史遗留的待清理 SDK 会话；成功或 17 即清除，其余保留至下次。
        private async System.Threading.Tasks.Task<DeviceRuntimeSnapshot> DrainPendingSdkLogoutsAsync(
            DeviceTaskContext context,
            int deviceId,
            int? expectedSdkUserId,
            long? expectedRuntimeVersion)
        {
            var expectedVersion = expectedRuntimeVersion;
            var pending = context.Registry.GetPendingSdkLogouts(deviceId);
            foreach (var userId in pending)
            {
                var before = context.Registry.TryGetByDeviceId(deviceId).Snapshot;
                if (before == null ||
                    before.IsDeleting ||
                    (expectedSdkUserId.HasValue && before.SdkUserId != expectedSdkUserId.Value) ||
                    (expectedVersion.HasValue && before.RuntimeVersion != expectedVersion.Value))
                {
                    return null;
                }

                try
                {
                    await gateway.LogoutAsync(new LogoutRequest { UserId = userId }, context.CancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var error = ToRuntimeError("DeviceLogout", ex, DateTime.Now, retryable: false);
                    if (error.SdkErrorCode != 17)
                    {
                        logger?.Warn("DeviceLifecycle", "Drain of pending SDK logout " + userId + " failed; retained for the next login attempt.", new LogFields
                        {
                            DeviceId = deviceId,
                            OperationName = "DeviceLogout",
                            ErrorCode = error.Code
                        });
                        continue;
                    }
                }

                var cleared = context.Registry.ClearPendingSdkLogout(
                    deviceId,
                    userId,
                    DateTime.Now,
                    context: null,
                    allowDeleting: false,
                    expectedSdkUserId: expectedSdkUserId,
                    expectedRuntimeVersion: expectedVersion);
                var after = context.Registry.TryGetByDeviceId(deviceId).Snapshot;
                if (!cleared.Success)
                {
                    if (after == null ||
                        after.IsDeleting ||
                        (expectedSdkUserId.HasValue && after.SdkUserId != expectedSdkUserId.Value) ||
                        (expectedVersion.HasValue && after.RuntimeVersion != expectedVersion.Value))
                    {
                        return null;
                    }

                    continue;
                }

                expectedVersion = cleared.Snapshot == null ? after == null ? expectedVersion : after.RuntimeVersion : cleared.Snapshot.RuntimeVersion;
            }

            var current = context.Registry.TryGetByDeviceId(deviceId).Snapshot;
            if (current == null ||
                current.IsDeleting ||
                (expectedSdkUserId.HasValue && current.SdkUserId != expectedSdkUserId.Value) ||
                (expectedVersion.HasValue && current.RuntimeVersion != expectedVersion.Value))
            {
                return null;
            }

            return current;
        }

        private async System.Threading.Tasks.Task<DeviceTaskResult> CloseStaleAlarmBeforeRearmAsync(DeviceTaskContext context, DeviceRuntimeSnapshot snapshot, DateTime started)
        {
            if (context == null || context.Task == null || snapshot == null || !snapshot.StaleAlarmHandle.HasValue)
            {
                return null;
            }

            var deviceId = context.Task.DeviceId;
            var staleAlarmHandle = snapshot.StaleAlarmHandle.Value;
            var allowDeleting = snapshot.IsDeleting && !string.IsNullOrWhiteSpace(snapshot.DeletingLeaseId);
            var expectedUserId = snapshot.SdkUserId;
            var expectedRuntimeVersion = snapshot.RuntimeVersion;
            var expectedDeletingLeaseId = allowDeleting ? snapshot.DeletingLeaseId : null;
            try
            {
                await gateway.CloseAlarmAsync(new AlarmCloseRequest { AlarmHandle = staleAlarmHandle }, context.CancellationToken).ConfigureAwait(false);
                var cleared = context.Registry.ClearStaleAlarmHandle(
                    deviceId,
                    staleAlarmHandle,
                    DateTime.Now,
                    context: null,
                    allowDeleting: allowDeleting,
                    expectedSdkUserId: expectedUserId,
                    expectedRuntimeVersion: expectedRuntimeVersion,
                    expectedDeletingLeaseId: expectedDeletingLeaseId);
                if (!cleared.Success)
                {
                    return await HandleStaleAlarmMutationRejectedAsync(context, snapshot, started, staleAlarmHandle).ConfigureAwait(false);
                }

                LogLifecycleSuccess(context, "Stale alarm handle closed before rearm.", started, fields =>
                {
                    fields.Extra["staleAlarmHandle"] = staleAlarmHandle.ToString();
                });
                return null;
            }
            catch (Exception ex)
            {
                var error = ToRuntimeError("DeviceCloseStaleAlarm", ex, DateTime.Now, retryable: true);
                if (error.SdkErrorCode == 17)
                {
                    var cleared = context.Registry.ClearStaleAlarmHandle(
                        deviceId,
                        staleAlarmHandle,
                        DateTime.Now,
                        context: null,
                        allowDeleting: allowDeleting,
                        expectedSdkUserId: expectedUserId,
                        expectedRuntimeVersion: expectedRuntimeVersion,
                        expectedDeletingLeaseId: expectedDeletingLeaseId);
                    if (!cleared.Success)
                    {
                        return await HandleStaleAlarmMutationRejectedAsync(context, snapshot, started, staleAlarmHandle).ConfigureAwait(false);
                    }

                    logger?.Warn("DeviceLifecycle", "Stale alarm handle was already unavailable; continuing rearm.", new LogFields
                    {
                        DeviceId = deviceId,
                        OperationName = context.Task.OperationName,
                        RequestId = string.IsNullOrWhiteSpace(context.Task.RequestId) ? context.RequestContext?.RequestId : context.Task.RequestId,
                        TraceId = context.RequestContext?.TraceId,
                        ErrorCode = error.Code
                    });
                    return null;
                }

                // Best-effort：旧布防关闭失败导致本次重连回滚前，先登出新登录会话，避免重复重连累积 SDK 会话。
                if (snapshot.SdkUserId.HasValue)
                {
                    var compensation = await LogoutSupersededLoginSessionAsync(
                        context,
                        deviceId,
                        snapshot.SdkUserId.Value,
                        expectedUserId,
                        expectedRuntimeVersion).ConfigureAwait(false);
                    if (compensation != null && compensation.Success)
                    {
                        expectedRuntimeVersion = compensation.Snapshot == null
                            ? expectedRuntimeVersion
                            : compensation.Snapshot.RuntimeVersion;
                    }
                }

                var currentBeforeDisconnect = context.Registry.TryGetByDeviceId(deviceId).Snapshot;
                if (currentBeforeDisconnect == null ||
                    currentBeforeDisconnect.IsDeleting != allowDeleting ||
                    currentBeforeDisconnect.SdkUserId != expectedUserId ||
                    (allowDeleting && !string.Equals(currentBeforeDisconnect.DeletingLeaseId, expectedDeletingLeaseId, StringComparison.Ordinal)))
                {
                    return await HandleStaleAlarmMutationRejectedAsync(context, snapshot, started, staleAlarmHandle).ConfigureAwait(false);
                }

                expectedRuntimeVersion = currentBeforeDisconnect.RuntimeVersion;
                var disconnected = context.Registry.MarkDisconnected(
                    deviceId,
                    error,
                    DateTime.Now,
                    DeviceConnectionStatus.Offline,
                    context: null,
                    allowDeleting: allowDeleting,
                    expectedSdkUserId: expectedUserId,
                    expectedRuntimeVersion: expectedRuntimeVersion,
                    expectedDeletingLeaseId: expectedDeletingLeaseId);
                if (disconnected.Success)
                {
                    ScheduleReconnect(
                        deviceId,
                        error.Message,
                        disconnected.Snapshot == null ? (long?)null : disconnected.Snapshot.RuntimeVersion);
                }

                LogLifecycleFailure(context, disconnected.Success
                    ? "Stale alarm handle close failed before rearm."
                    : "Stale alarm handle close failed; stale runtime callback was ignored.", started, error, fields =>
                {
                    fields.Extra["staleAlarmHandle"] = staleAlarmHandle.ToString();
                });
                var result = DeviceTaskResult.FromTask(context.Task, false, error.Code, error.Message, DeviceConnectionStatus.Offline, started, DateTime.Now);
                result.SdkErrorCode = error.SdkErrorCode;
                result.Retryable = true;
                return result;
            }
        }

        private async System.Threading.Tasks.Task<DeviceTaskResult> HandleStaleAlarmMutationRejectedAsync(
            DeviceTaskContext context,
            DeviceRuntimeSnapshot snapshot,
            DateTime started,
            int staleAlarmHandle)
        {
            if (snapshot.SdkUserId.HasValue)
            {
                await LogoutSupersededLoginSessionAsync(
                    context,
                    context.Task.DeviceId,
                    snapshot.SdkUserId.Value,
                    snapshot.SdkUserId,
                    snapshot.RuntimeVersion).ConfigureAwait(false);
            }

            var current = context.Registry.TryGetByDeviceId(context.Task.DeviceId).Snapshot;
            var deleting = current != null && current.IsDeleting;
            var code = deleting ? "DEVICE_DELETING" : "INVALID_ARGUMENT";
            var message = deleting
                ? "设备已进入删除流程，忽略旧布防回调。"
                : "设备运行时已发生变化，忽略旧布防回调。";
            var result = DeviceTaskResult.FromTask(
                context.Task,
                false,
                code,
                message,
                deleting ? DeviceConnectionStatus.Disconnecting : DeviceConnectionStatus.Offline,
                started,
                DateTime.Now);
            LogLifecycleFailure(context, "Stale alarm handle cleanup was rejected by the runtime fence.", started, DeviceRuntimeError.Create(
                "DeviceCloseStaleAlarm",
                code,
                message,
                DateTime.Now,
                retryable: true), fields =>
            {
                fields.Extra["staleAlarmHandle"] = staleAlarmHandle.ToString();
            });
            return result;
        }

        private DeviceSdkTask CreateHealthCheckTask(int deviceId, string requestId)
        {
            var task = new DeviceSdkTask(deviceId, DeviceTaskType.HealthCheck, "DeviceHealthCheck", async context =>
            {
                var started = DateTime.Now;
                var snapshot = context.SnapshotBeforeExecution;
                if (snapshot == null)
                {
                    return DeviceTaskResult.FromTask(context.Task, false, "NOT_FOUND", "设备不存在。", DeviceConnectionStatus.Unknown, started, DateTime.Now);
                }

                if (!snapshot.SdkUserId.HasValue || !snapshot.Enabled || snapshot.Status == DeviceConnectionStatus.Disconnected)
                {
                    return DeviceTaskResult.FromTask(context.Task, true, "OK", "设备未在线，跳过状态检测。", snapshot.Status, started, DateTime.Now);
                }

                try
                {
                    await gateway.GetDeviceInfoAsync(new DeviceInfoRequest { UserId = snapshot.SdkUserId.Value }, context.CancellationToken).ConfigureAwait(false);
                    var checkedAt = DateTime.Now;
                    context.Registry.MarkChecked(deviceId, checkedAt, DeviceConnectionStatus.Online);
                    ClearHealthFailures(deviceId);
                    await ProbeAlarmDeploymentAsync(context, snapshot, started).ConfigureAwait(false);
                    return DeviceTaskResult.FromTask(context.Task, true, "OK", "状态检测成功。", DeviceConnectionStatus.Online, started, DateTime.Now);
                }
                catch (Exception ex)
                {
                    var error = ToRuntimeError("DeviceHealthCheck", ex, DateTime.Now, retryable: true);
                    var count = IncrementHealthFailure(deviceId);
                    if (count >= options.FailureThreshold)
                    {
                        context.Registry.MarkDisconnected(deviceId, error, DateTime.Now, DeviceConnectionStatus.Offline);
                        ScheduleReconnect(deviceId, error.Message);
                    }
                    else
                    {
                        context.Registry.RecordError(deviceId, error, DateTime.Now, DeviceConnectionStatus.Degraded);
                    }

                    LogLifecycleFailure(context, "Device health check failed.", started, error, fields =>
                    {
                        fields.Extra["failureCount"] = count.ToString();
                        fields.Extra["failureThreshold"] = options.FailureThreshold.ToString();
                        fields.Extra["markedOffline"] = (count >= options.FailureThreshold).ToString();
                    });
                    var result = DeviceTaskResult.FromTask(context.Task, false, error.Code, error.Message, DeviceConnectionStatus.Offline, started, DateTime.Now);
                    result.SdkErrorCode = error.SdkErrorCode;
                    result.Retryable = true;
                    return result;
                }
            });
            task.Priority = DeviceTaskPriority.Low;
            task.TimeoutMilliseconds = options.HealthCheckTimeoutMs;
            // 健康检查任务在设备未在线时会自行跳过且不调用 SDK，允许在手动断开状态下执行，保持“跳过即成功”语义。
            task.AllowWhenManualDisconnected = true;
            task.RequestId = requestId ?? string.Empty;
            return task;
        }

        private async System.Threading.Tasks.Task ProbeAlarmDeploymentAsync(DeviceTaskContext context, DeviceRuntimeSnapshot snapshot, DateTime started)
        {
            var deviceId = context.Task.DeviceId;
            if (!ShouldProbeAlarmDeployment(snapshot))
            {
                ClearAlarmProbeFailures(deviceId);
                return;
            }

            try
            {
                if (!snapshot.AlarmHandle.HasValue)
                {
                    if (snapshot.AlarmManuallyDisarmed)
                    {
                        ClearAlarmProbeFailures(deviceId);
                        return;
                    }

                    var missingHandleError = DeviceRuntimeError.Create(
                        "AlarmStatusProbe",
                        "ALARM_HANDLE_MISSING",
                        "Local alarm handle is missing for an online ACS device.",
                        DateTime.Now,
                        retryable: true);
                    context.Registry.RecordError(deviceId, missingHandleError, DateTime.Now, DeviceConnectionStatus.Degraded);
                    ScheduleReArm(deviceId, missingHandleError.Message);
                    ClearAlarmProbeFailures(deviceId);
                    return;
                }

                var status = await gateway.GetAlarmDeploymentStatusAsync(new AlarmDeploymentStatusRequest
                {
                    UserId = snapshot.SdkUserId.Value,
                    Channel = -1,
                    AlarmInputIndex = 0
                }, context.CancellationToken).ConfigureAwait(false);

                if (status == null || !status.Known)
                {
                    ClearAlarmProbeFailures(deviceId);
                    LogAlarmProbe(context, "Alarm deployment status is unknown; keeping current AlarmHandle.", status, started);
                    return;
                }

                if (status.IsDeployed)
                {
                    ClearAlarmProbeFailures(deviceId);
                    return;
                }

                var count = IncrementAlarmProbeFailure(deviceId);
                var threshold = Math.Max(1, options.AlarmStatusProbeFailureThreshold);
                var notDeployedError = DeviceRuntimeError.Create(
                    "AlarmStatusProbe",
                    "ALARM_NOT_DEPLOYED",
                    "Device-side alarm deployment status is disarmed.",
                    DateTime.Now,
                    retryable: true);
                context.Registry.RecordError(deviceId, notDeployedError, DateTime.Now, DeviceConnectionStatus.Degraded);
                LogAlarmProbe(context, "Alarm input deployment status is disarmed; keeping current AlarmHandle.", status, started, fields =>
                {
                    fields.Extra["probeFailureCount"] = count.ToString();
                    fields.Extra["probeFailureThreshold"] = threshold.ToString();
                });
            }
            catch (Exception ex)
            {
                var probeError = ToRuntimeError("AlarmStatusProbe", ex, DateTime.Now, retryable: true);
                context.Registry.RecordError(deviceId, probeError, DateTime.Now, DeviceConnectionStatus.Degraded);
                var fields = new LogFields
                {
                    DeviceId = deviceId,
                    OperationName = context.Task.OperationName,
                    ErrorCode = probeError.Code
                };
                fields.Extra["errorMessage"] = probeError.Message;
                logger?.Warn("DeviceLifecycle", "Alarm deployment status probe failed; keeping current AlarmHandle.", fields);
            }
        }

        private bool ShouldProbeAlarmDeployment(DeviceRuntimeSnapshot snapshot)
        {
            if (!options.AlarmEnabled || !options.AlarmStatusProbeEnabled || snapshot == null)
            {
                return false;
            }

            return snapshot.Enabled
                && !snapshot.IsDeleting
                && snapshot.IsConnected
                && snapshot.SdkUserId.HasValue
                && snapshot.Types != null
                && snapshot.Types.Contains(DeviceType.Acs);
        }

        private DeviceOperationResult SubmitArmAlarm(int deviceId, bool wait, string requestId)
        {
            var task = CreateArmAlarmTask(deviceId, requestId);
            if (wait)
            {
                return FromTaskResult(dispatcher.SubmitAndWaitAsync(task).GetAwaiter().GetResult());
            }

            var submitted = dispatcher.Submit(task);
            return new DeviceOperationResult
            {
                Success = submitted.Accepted,
                Code = submitted.Accepted ? "OK" : submitted.ImmediateResult.Code,
                DeviceId = deviceId,
                Message = submitted.Accepted ? "布防任务已投递。" : submitted.ImmediateResult.Message,
                Snapshot = registry.TryGetByDeviceId(deviceId).Snapshot
            };
        }

        private DeviceSdkTask CreateArmAlarmTask(int deviceId, string requestId)
        {
            var task = new DeviceSdkTask(deviceId, DeviceTaskType.SetupAlarm, "DeviceArmAlarm", async context =>
            {
                var started = DateTime.Now;
                var snapshot = context.SnapshotBeforeExecution;
                if (snapshot == null || !snapshot.SdkUserId.HasValue || !snapshot.IsConnected)
                {
                    return DeviceTaskResult.FromTask(context.Task, false, "DEVICE_ERROR", "设备未在线，不能布防。", DeviceConnectionStatus.Offline, started, DateTime.Now);
                }

                if (snapshot.AlarmHandle.HasValue)
                {
                    return DeviceTaskResult.FromTask(context.Task, true, "OK", "设备已布防。", snapshot.Status, started, DateTime.Now);
                }

                try
                {
                    var alarm = await gateway.SetAlarmAsync(new AlarmSetupRequest
                    {
                        UserId = snapshot.SdkUserId.Value,
                        DeployType = options.AlarmDeployType
                    }, context.CancellationToken).ConfigureAwait(false);
                    var register = context.Registry.RegisterAlarmHandle(
                        deviceId,
                        alarm.AlarmHandle,
                        DateTime.Now,
                        context: null,
                        expectedSdkUserId: snapshot.SdkUserId,
                        expectedRuntimeVersion: snapshot.RuntimeVersion);
                    if (!register.Success)
                    {
                        try
                        {
                            await gateway.CloseAlarmAsync(new AlarmCloseRequest { AlarmHandle = alarm.AlarmHandle }, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception closeEx)
                        {
                            var closeError = ToRuntimeError("DeviceCloseAlarm", closeEx, DateTime.Now, retryable: false);
                            var current = context.Registry.TryGetByDeviceId(deviceId).Snapshot;
                            if (current != null &&
                                current.IsDeleting &&
                                !string.IsNullOrWhiteSpace(current.DeletingLeaseId))
                            {
                                context.Registry.RecordPendingAlarmHandle(
                                    deviceId,
                                    alarm.AlarmHandle,
                                    DateTime.Now,
                                    context: null,
                                    allowDeleting: true,
                                    expectedSdkUserId: current.SdkUserId,
                                    expectedRuntimeVersion: current.RuntimeVersion,
                                    expectedDeletingLeaseId: current.DeletingLeaseId);
                            }

                            var failedClose = DeviceTaskResult.FromTask(context.Task, false, closeError.Code, closeError.Message, DeviceConnectionStatus.Degraded, started, DateTime.Now);
                            failedClose.SdkErrorCode = closeError.SdkErrorCode;
                            return failedClose;
                        }

                        return DeviceTaskResult.FromTask(context.Task, false, register.Code, register.Message, DeviceConnectionStatus.Online, started, DateTime.Now);
                    }

                    LogLifecycleSuccess(context, "设备布防成功。", started, fields =>
                    {
                        fields.Extra["userId"] = snapshot.SdkUserId.Value.ToString();
                        fields.Extra["alarmHandle"] = alarm.AlarmHandle.ToString();
                        fields.Extra["alarmDeployType"] = options.AlarmDeployType.ToString();
                    });

                    ClearReArmFailures(deviceId);
                    return DeviceTaskResult.FromTask(context.Task, true, "OK", "布防成功。", DeviceConnectionStatus.Online, started, DateTime.Now);
                }
                catch (Exception ex)
                {
                    var error = ToRuntimeError("DeviceArmAlarm", ex, DateTime.Now, retryable: true);
                    var recorded = context.Registry.RecordError(
                        deviceId,
                        error,
                        DateTime.Now,
                        DeviceConnectionStatus.Degraded,
                        context: null,
                        allowDeleting: false,
                        expectedSdkUserId: snapshot.SdkUserId,
                        expectedRuntimeVersion: snapshot.RuntimeVersion);
                    // 布防失败后按指数退避无限重试，直到成功或设备被手动断开/删除/服务停止。
                    if (options.AlarmEnabled && recorded.Success)
                    {
                        ScheduleReArm(
                            deviceId,
                            error.Message,
                            recorded.Snapshot == null ? (long?)null : recorded.Snapshot.RuntimeVersion);
                    }
                    LogLifecycleFailure(context, recorded.Success
                        ? "Device alarm deployment failed."
                        : "Device alarm deployment failed; stale runtime callback was ignored.", started, error);
                    var result = DeviceTaskResult.FromTask(context.Task, false, error.Code, error.Message, DeviceConnectionStatus.Degraded, started, DateTime.Now);
                    result.SdkErrorCode = error.SdkErrorCode;
                    result.Retryable = true;
                    return result;
                }
            });
            task.Priority = DeviceTaskPriority.Normal;
            task.RequestId = requestId ?? string.Empty;
            return task;
        }

        private DeviceSdkTask CreateDisarmAlarmTask(int deviceId, string requestId, bool manuallyDisarmed = true)
        {
            var task = new DeviceSdkTask(deviceId, DeviceTaskType.CloseAlarm, "DeviceDisarmAlarm", async context =>
            {
                var started = DateTime.Now;
                var snapshot = context.SnapshotBeforeExecution;
                if (snapshot == null)
                {
                    return DeviceTaskResult.FromTask(context.Task, false, "NOT_FOUND", "设备不存在。", DeviceConnectionStatus.Unknown, started, DateTime.Now);
                }

                if (!snapshot.AlarmHandle.HasValue)
                {
                    if (snapshot.AlarmManuallyDisarmed)
                    {
                        return DeviceTaskResult.FromTask(context.Task, true, "OK", "设备已手动撤防，跳过自动布防。", snapshot.Status, started, DateTime.Now);
                    }

                    return DeviceTaskResult.FromTask(context.Task, true, "OK", "设备未布防，跳过撤防。", snapshot.Status, started, DateTime.Now);
                }

                DeviceRuntimeError lastError = null;
                try
                {
                    await gateway.CloseAlarmAsync(new AlarmCloseRequest { AlarmHandle = snapshot.AlarmHandle.Value }, context.CancellationToken).ConfigureAwait(false);
                    var cleared = context.Registry.ClearAlarmHandle(
                        deviceId,
                        DateTime.Now,
                        manuallyDisarmed: manuallyDisarmed,
                        context: null,
                        expectedAlarmHandle: snapshot.AlarmHandle,
                        allowDeleting: false,
                        expectedSdkUserId: snapshot.SdkUserId,
                        expectedRuntimeVersion: snapshot.RuntimeVersion);
                    if (!cleared.Success)
                    {
                        lastError = DeviceRuntimeError.Create(
                            "DeviceCloseAlarm",
                            "INVALID_ARGUMENT",
                            "设备运行时已发生变化，忽略旧撤防回调。",
                            DateTime.Now,
                            retryable: false);
                    }
                    else
                    {
                        LogLifecycleSuccess(context, "设备撤防成功。", started, fields =>
                        {
                            fields.Extra["alarmHandle"] = snapshot.AlarmHandle.Value.ToString();
                        });
                    }
                }
                catch (Exception ex)
                {
                    lastError = ToRuntimeError("DeviceCloseAlarm", ex, DateTime.Now, retryable: false);
                    context.Registry.RecordError(
                        deviceId,
                        lastError,
                        DateTime.Now,
                        snapshot.Status,
                        context: null,
                        allowDeleting: false,
                        expectedSdkUserId: snapshot.SdkUserId,
                        expectedRuntimeVersion: snapshot.RuntimeVersion);
                    LogLifecycleFailure(context, "Device alarm close failed.", started, lastError);
                }

                var success = lastError == null;
                var result = DeviceTaskResult.FromTask(context.Task, success, success ? "OK" : lastError.Code, success ? "撤防成功。" : lastError.Message, snapshot.Status, started, DateTime.Now);
                if (lastError != null)
                {
                    result.SdkErrorCode = lastError.SdkErrorCode;
                }

                return result;
            });
            task.Priority = DeviceTaskPriority.Critical;
            task.TimeoutMilliseconds = options.LogoutTimeoutMs;
            task.AllowWhenDeleting = false;
            task.RequestId = requestId ?? string.Empty;
            return task;
        }

        private DeviceSdkTask CreatePendingLogoutTask(int deviceId, int userId, string requestId, string deletingLeaseId = null)
        {
            var task = new DeviceSdkTask(deviceId, DeviceTaskType.Logout, "PendingSdkLogout", async context =>
            {
                var started = DateTime.Now;
                var deleteCleanup = !string.IsNullOrWhiteSpace(deletingLeaseId);
                if (deleteCleanup &&
                    (context.SnapshotBeforeExecution == null ||
                     !context.SnapshotBeforeExecution.IsDeleting ||
                     !string.Equals(context.SnapshotBeforeExecution.DeletingLeaseId, deletingLeaseId, StringComparison.Ordinal)))
                {
                    return DeviceTaskResult.FromTask(context.Task, false, "DEVICE_DELETING", "设备删除 lease 已失效，忽略待清理登出任务。", DeviceConnectionStatus.Disconnecting, started, DateTime.Now);
                }

                try
                {
                    await gateway.LogoutAsync(new LogoutRequest { UserId = userId }, context.CancellationToken).ConfigureAwait(false);
                    return DeviceTaskResult.FromTask(context.Task, true, "OK", "待清理 SDK 会话登出成功。", DeviceConnectionStatus.Offline, started, DateTime.Now);
                }
                catch (Exception ex)
                {
                    var error = ToRuntimeError("DeviceLogout", ex, DateTime.Now, retryable: false);
                    var result = DeviceTaskResult.FromTask(context.Task, false, error.Code, error.Message, DeviceConnectionStatus.Offline, started, DateTime.Now);
                    result.SdkErrorCode = error.SdkErrorCode;
                    return result;
                }
            });
            task.Priority = DeviceTaskPriority.Critical;
            task.TimeoutMilliseconds = options.LogoutTimeoutMs;
            var pendingLogoutLeaseValid = !string.IsNullOrWhiteSpace(deletingLeaseId) &&
                registry.TryGetByDeviceId(deviceId).Snapshot != null &&
                registry.TryGetByDeviceId(deviceId).Snapshot.IsDeleting &&
                string.Equals(registry.TryGetByDeviceId(deviceId).Snapshot.DeletingLeaseId, deletingLeaseId, StringComparison.Ordinal);
            task.AllowWhenDeleting = pendingLogoutLeaseValid;
            task.AllowWhenManualDisconnected = true;
            task.RequestId = requestId ?? string.Empty;
            return task;
        }

        private DeviceSdkTask CreateDisconnectTask(int deviceId, string operationName, DeviceConnectionStatus finalStatus, string requestId, string deletingLeaseId = null)
        {
            var task = new DeviceSdkTask(deviceId, DeviceTaskType.Logout, operationName, async context =>
            {
                var started = DateTime.Now;
                var snapshot = context.SnapshotBeforeExecution;
                if (snapshot == null)
                {
                    return DeviceTaskResult.FromTask(context.Task, true, "OK", "设备不存在，清理视为成功。", DeviceConnectionStatus.Deleted, started, DateTime.Now);
                }

                var deleteCleanup = !string.IsNullOrWhiteSpace(deletingLeaseId);
                if (deleteCleanup &&
                    (!snapshot.IsDeleting || !string.Equals(snapshot.DeletingLeaseId, deletingLeaseId, StringComparison.Ordinal)))
                {
                    return DeviceTaskResult.FromTask(context.Task, false, "DEVICE_DELETING", "设备删除 lease 已失效，忽略旧清理任务。", DeviceConnectionStatus.Disconnecting, started, DateTime.Now);
                }

                var allowDeleting = deleteCleanup;
                var expectedDeletingLeaseId = allowDeleting ? deletingLeaseId : null;
                DeviceRuntimeError lastError = null;
                var mutationRejected = false;
                var currentAlarmCleanupFailed = false;
                var alarmHandles = new List<int>();
                var seenAlarmHandles = new HashSet<int>();
                Action<int?> addAlarmHandle = handle =>
                {
                    if (handle.HasValue && seenAlarmHandles.Add(handle.Value))
                    {
                        alarmHandles.Add(handle.Value);
                    }
                };
                addAlarmHandle(snapshot.AlarmHandle);
                addAlarmHandle(snapshot.StaleAlarmHandle);
                foreach (var handle in snapshot.PendingAlarmHandles ?? new List<int>())
                {
                    addAlarmHandle(handle);
                }

                foreach (var alarmHandle in alarmHandles)
                {
                    var before = context.Registry.TryGetByDeviceId(deviceId).Snapshot;
                    if (before == null)
                    {
                        mutationRejected = true;
                        lastError = DeviceRuntimeError.Create("DeviceCloseAlarm", "DEVICE_NOT_FOUND", "设备运行时不存在，忽略旧撤防回调。", DateTime.Now, retryable: false);
                        break;
                    }

                    var expectedSdkUserId = before.SdkUserId;
                    var expectedRuntimeVersion = before.RuntimeVersion;
                    var isCurrentAlarm = before.AlarmHandle == alarmHandle;
                    var closeSucceeded = false;
                    DeviceRuntimeError closeError = null;
                    try
                    {
                        await gateway.CloseAlarmAsync(new AlarmCloseRequest { AlarmHandle = alarmHandle }, context.CancellationToken).ConfigureAwait(false);
                        closeSucceeded = true;
                    }
                    catch (Exception ex)
                    {
                        closeError = ToRuntimeError("DeviceCloseAlarm", ex, DateTime.Now, retryable: false);
                        closeSucceeded = allowDeleting && closeError.SdkErrorCode == 17;
                    }

                    if (!closeSucceeded)
                    {
                        if (isCurrentAlarm)
                        {
                            currentAlarmCleanupFailed = true;
                        }

                        lastError = closeError;
                        var recorded = context.Registry.RecordError(
                            deviceId,
                            closeError,
                            DateTime.Now,
                            before.Status,
                            context: null,
                            allowDeleting: allowDeleting,
                            expectedSdkUserId: expectedSdkUserId,
                            expectedRuntimeVersion: expectedRuntimeVersion,
                            expectedDeletingLeaseId: expectedDeletingLeaseId);
                        if (!recorded.Success)
                        {
                            mutationRejected = true;
                        }

                        LogLifecycleFailure(context, "Device alarm close failed during disconnect.", started, closeError, fields =>
                        {
                            fields.Extra["alarmHandle"] = alarmHandle.ToString();
                        });
                        continue;
                    }

                    DeviceRuntimeMutationResult cleared;
                    if (allowDeleting)
                    {
                        cleared = context.Registry.RecordCleanedDuringDeleteAlarmHandle(
                            deviceId,
                            alarmHandle,
                            DateTime.Now,
                            expectedSdkUserId,
                            expectedRuntimeVersion,
                            expectedDeletingLeaseId);
                    }
                    else if (isCurrentAlarm)
                    {
                        cleared = context.Registry.ClearAlarmHandle(
                            deviceId,
                            DateTime.Now,
                            manuallyDisarmed: false,
                            context: null,
                            expectedAlarmHandle: alarmHandle,
                            allowDeleting: false,
                            expectedSdkUserId: expectedSdkUserId,
                            expectedRuntimeVersion: expectedRuntimeVersion);
                    }
                    else
                    {
                        cleared = context.Registry.ClearStaleAlarmHandle(
                            deviceId,
                            alarmHandle,
                            DateTime.Now,
                            context: null,
                            allowDeleting: false,
                            expectedSdkUserId: expectedSdkUserId,
                            expectedRuntimeVersion: expectedRuntimeVersion);
                    }

                    if (!cleared.Success)
                    {
                        mutationRejected = true;
                        if (lastError == null)
                        {
                            lastError = DeviceRuntimeError.Create(
                                "DeviceCloseAlarm",
                                "INVALID_ARGUMENT",
                                "设备运行时已发生变化，忽略旧撤防回调。",
                                DateTime.Now,
                                retryable: false);
                        }
                        continue;
                    }

                    LogLifecycleSuccess(context, closeError == null ? "设备撤防成功。" : "设备已不存在，撤防视为成功。", started, fields =>
                    {
                        fields.Extra["alarmHandle"] = alarmHandle.ToString();
                    });
                }

                var sdkUsers = new List<int>();
                var seenSdkUsers = new HashSet<int>();
                Action<int?> addSdkUser = userId =>
                {
                    if (userId.HasValue && seenSdkUsers.Add(userId.Value))
                    {
                        sdkUsers.Add(userId.Value);
                    }
                };
                addSdkUser(snapshot.SdkUserId);
                foreach (var userId in snapshot.PendingSdkLogoutUserIds ?? new List<int>())
                {
                    addSdkUser(userId);
                }

                foreach (var userId in sdkUsers)
                {
                    var before = context.Registry.TryGetByDeviceId(deviceId).Snapshot;
                    if (before == null)
                    {
                        mutationRejected = true;
                        lastError = DeviceRuntimeError.Create("DeviceLogout", "DEVICE_NOT_FOUND", "设备运行时不存在，忽略旧登出回调。", DateTime.Now, retryable: false);
                        break;
                    }

                    var isCurrentUser = before.SdkUserId == userId;
                    var expectedSdkUserId = isCurrentUser ? (int?)userId : null;
                    var expectedRuntimeVersion = before.RuntimeVersion;
                    var logoutSucceeded = false;
                    DeviceRuntimeError logoutError = null;
                    try
                    {
                        await gateway.LogoutAsync(new LogoutRequest { UserId = userId }, context.CancellationToken).ConfigureAwait(false);
                        logoutSucceeded = true;
                    }
                    catch (Exception ex)
                    {
                        logoutError = ToRuntimeError("DeviceLogout", ex, DateTime.Now, retryable: false);
                        logoutSucceeded = logoutError.SdkErrorCode == 17;
                    }

                    if (!logoutSucceeded)
                    {
                        lastError = logoutError;
                        var recorded = context.Registry.RecordPendingSdkLogout(
                            deviceId,
                            userId,
                            DateTime.Now,
                            context: null,
                            allowDeleting: allowDeleting,
                            expectedSdkUserId: expectedSdkUserId,
                            expectedRuntimeVersion: expectedRuntimeVersion,
                            expectedDeletingLeaseId: expectedDeletingLeaseId);
                        if (!recorded.Success)
                        {
                            mutationRejected = true;
                        }

                        LogLifecycleFailure(context, "Device logout failed.", started, logoutError, fields =>
                        {
                            fields.Extra["userId"] = userId.ToString();
                        });
                        continue;
                    }

                    DeviceRuntimeMutationResult cleared;
                    if (allowDeleting)
                    {
                        cleared = context.Registry.RecordCleanedDuringDeleteSdkUser(
                            deviceId,
                            userId,
                            DateTime.Now,
                            expectedSdkUserId,
                            expectedRuntimeVersion,
                            expectedDeletingLeaseId);
                    }
                    else if (isCurrentUser)
                    {
                        cleared = context.Registry.MarkLoggedOut(
                            deviceId,
                            DateTime.Now,
                            context: null,
                            expectedSdkUserId: expectedSdkUserId,
                            allowDeleting: false,
                            expectedDeletingLeaseId: null,
                            expectedRuntimeVersion: expectedRuntimeVersion,
                            preserveCurrentAlarmHandle: currentAlarmCleanupFailed);
                    }
                    else
                    {
                        cleared = context.Registry.ClearPendingSdkLogout(
                            deviceId,
                            userId,
                            DateTime.Now,
                            context: null,
                            allowDeleting: false,
                            expectedSdkUserId: null,
                            expectedRuntimeVersion: expectedRuntimeVersion);
                    }

                    if (!cleared.Success)
                    {
                        mutationRejected = true;
                        if (lastError == null)
                        {
                            lastError = DeviceRuntimeError.Create(
                                "DeviceLogout",
                                "INVALID_ARGUMENT",
                                "设备运行时已发生变化，忽略旧登出回调。",
                                DateTime.Now,
                                retryable: false);
                        }
                        continue;
                    }

                    LogLifecycleSuccess(context, logoutError == null ? "设备登出成功。" : "设备会话已不存在，登出视为成功。", started, fields =>
                    {
                        fields.Extra["userId"] = userId.ToString();
                    });
                }

                var current = context.Registry.TryGetByDeviceId(deviceId).Snapshot;
                var stateMatches = current != null;
                if (stateMatches && allowDeleting)
                {
                    stateMatches = current.IsDeleting &&
                        string.Equals(current.DeletingLeaseId, expectedDeletingLeaseId, StringComparison.Ordinal);
                }
                else if (stateMatches)
                {
                    stateMatches = !current.IsDeleting;
                }

                if (!stateMatches)
                {
                    mutationRejected = true;
                }
                else if (!mutationRejected)
                {
                    DeviceRuntimeMutationResult finalMutation;
                    if (finalStatus == DeviceConnectionStatus.Disconnected)
                    {
                        finalMutation = context.Registry.MarkManualDisconnected(
                            deviceId,
                            lastError,
                            DateTime.Now,
                            context: null,
                            allowDeleting: allowDeleting,
                            expectedDeletingLeaseId: expectedDeletingLeaseId,
                            preserveCurrentAlarmHandle: currentAlarmCleanupFailed);
                    }
                    else
                    {
                        finalMutation = context.Registry.MarkDisconnected(
                            deviceId,
                            lastError,
                            DateTime.Now,
                            finalStatus,
                            context: null,
                            allowDeleting: allowDeleting,
                            expectedSdkUserId: current.SdkUserId,
                            expectedRuntimeVersion: current.RuntimeVersion,
                            expectedDeletingLeaseId: expectedDeletingLeaseId,
                            preserveCurrentAlarmHandle: currentAlarmCleanupFailed);
                    }

                    if (!finalMutation.Success)
                    {
                        mutationRejected = true;
                    }
                }

                if (mutationRejected && lastError == null)
                {
                    lastError = DeviceRuntimeError.Create(
                        "DeviceDisconnect",
                        "INVALID_ARGUMENT",
                        "设备运行时已发生变化，忽略旧断开回调。",
                        DateTime.Now,
                        retryable: false);
                }

                var success = lastError == null && !mutationRejected;
                var result = DeviceTaskResult.FromTask(
                    context.Task,
                    success,
                    success ? "OK" : lastError.Code,
                    success ? "登出清理成功。" : lastError.Message,
                    finalStatus,
                    started,
                    DateTime.Now);
                if (lastError != null)
                {
                    result.SdkErrorCode = lastError.SdkErrorCode;
                }

                return result;
            });
            task.Priority = DeviceTaskPriority.Critical;
            task.TimeoutMilliseconds = options.LogoutTimeoutMs;
            task.AllowWhenDeleting = !string.IsNullOrWhiteSpace(deletingLeaseId);
            task.AllowWhenManualDisconnected = true;
            task.RequestId = requestId ?? string.Empty;
            return task;
        }


        private void ScheduleReconnect(int deviceId, string reason, long? expectedRuntimeVersion = null)
        {
            var snapshot = registry.TryGetByDeviceId(deviceId).Snapshot;
            if (snapshot == null ||
                snapshot.IsDeleting ||
                snapshot.Status == DeviceConnectionStatus.Disconnected ||
                snapshot.Status == DeviceConnectionStatus.InvalidConfig ||
                snapshot.Status == DeviceConnectionStatus.Disabled ||
                snapshot.Reconnect.ManualDisconnected ||
                (expectedRuntimeVersion.HasValue && snapshot.RuntimeVersion != expectedRuntimeVersion.Value))
            {
                return;
            }

            // 仅当显式配置为正数时才作为最大重连次数刹车；0/负数 = 无限重试直到成功。
            if (options.MaxReconnectAttempts > 0
                && snapshot.Reconnect.AttemptCount >= options.MaxReconnectAttempts)
            {
                registry.MarkDisconnected(
                    deviceId,
                    DeviceRuntimeError.Create("Reconnect", "FAILED", "重连次数已耗尽。", DateTime.Now, retryable: false),
                    DateTime.Now,
                    DeviceConnectionStatus.Failed,
                    context: null,
                    allowDeleting: false,
                    expectedSdkUserId: snapshot.SdkUserId,
                    expectedRuntimeVersion: snapshot.RuntimeVersion);
                return;
            }

            var policy = RetryBackoffPolicy.Exponential(
                TimeSpan.FromMilliseconds(options.ReconnectBaseDelayMs),
                TimeSpan.FromMilliseconds(options.ReconnectMaxDelayMs));
            var delay = policy.CalculateDelay(snapshot.Reconnect.AttemptCount);
            var dueAt = DateTime.Now.Add(delay);
            var pending = registry.MarkReconnectPending(deviceId, dueAt, reason, DateTime.Now, context: null, expectedRuntimeVersion: snapshot.RuntimeVersion);
            if (!pending.Success)
            {
                return;
            }

            var scheduled = delayedScheduler?.Schedule(new DelayedDeviceTask(
                deviceId,
                DeviceTaskType.Login,
                DeviceTaskPriority.High,
                dueAt,
                "stage4:reconnect:" + deviceId,
                "Stage4Reconnect",
                () => CreateLoginTask(deviceId, string.Empty),
                DateTime.Now));
            if (scheduled == null || !scheduled.Accepted)
            {
                // 调度失败时设备不能停在 ReconnectPending：没有任何任务会再触发重连，标记 Faulted 让运维可见。
                logger?.Warn("DeviceLifecycle", "Device reconnect schedule failed; marking device faulted.", new LogFields
                {
                    DeviceId = deviceId,
                    OperationName = "Stage4Reconnect",
                    ErrorCode = scheduled == null ? "SCHEDULER_UNAVAILABLE" : scheduled.Code
                });
                registry.MarkDisconnected(
                    deviceId,
                    DeviceRuntimeError.Create("Reconnect", "RECONNECT_SCHEDULE_FAILED", "重连任务调度失败。", DateTime.Now, retryable: false),
                    DateTime.Now,
                    DeviceConnectionStatus.Faulted,
                    context: null,
                    allowDeleting: false,
                    expectedSdkUserId: snapshot.SdkUserId,
                    expectedRuntimeVersion: pending.Snapshot == null ? (long?)null : pending.Snapshot.RuntimeVersion);
                return;
            }

            LogDelayedDeviceTaskScheduled("Device reconnect scheduled.", deviceId, "Stage4Reconnect", snapshot.Status, snapshot.Reconnect.AttemptCount, delay, dueAt, reason);
        }

        private void CancelDelayedReconnect(int deviceId)
        {
            delayedScheduler?.CancelByTaskKey("stage4:reconnect:" + deviceId, "manual operation");
            logger?.Info("DeviceLifecycle", "Delayed reconnect cancelled.", new LogFields
            {
                DeviceId = deviceId,
                OperationName = "CancelReconnect"
            });
        }

        // 布防失败后无限重试，直到成功或设备被手动断开/删除/服务停止。门控与重连一致。
        private void ScheduleReArm(int deviceId, string reason, long? expectedRuntimeVersion = null)
        {
            var snapshot = registry.TryGetByDeviceId(deviceId).Snapshot;
            if (snapshot == null ||
                snapshot.IsDeleting ||
                snapshot.Status == DeviceConnectionStatus.Disconnected ||
                snapshot.Status == DeviceConnectionStatus.InvalidConfig ||
                snapshot.Status == DeviceConnectionStatus.Disabled ||
                snapshot.Reconnect.ManualDisconnected ||
                (expectedRuntimeVersion.HasValue && snapshot.RuntimeVersion != expectedRuntimeVersion.Value))
            {
                return;
            }

            var attempt = IncrementReArmFailure(deviceId);
            var baseMs = Math.Max(1, options.ReArmBaseDelayMs);
            var maxMs = Math.Max(baseMs, options.ReArmMaxDelayMs);
            var policy = RetryBackoffPolicy.Exponential(
                TimeSpan.FromMilliseconds(baseMs),
                TimeSpan.FromMilliseconds(maxMs));
            var delay = policy.CalculateDelay(attempt);
            var dueAt = DateTime.Now.Add(delay);
            var scheduled = delayedScheduler?.Schedule(new DelayedDeviceTask(
                deviceId,
                DeviceTaskType.SetupAlarm,
                DeviceTaskPriority.Normal,
                dueAt,
                "stage4:rearm:" + deviceId,
                "Stage4ReArm",
                () => CreateArmAlarmTask(deviceId, string.Empty),
                DateTime.Now));
            if (scheduled == null || !scheduled.Accepted)
            {
                // 布防重试调度失败：保持当前状态，由健康探针或人工重新布防再次发现。
                logger?.Warn("DeviceLifecycle", "Device alarm redeploy schedule failed.", new LogFields
                {
                    DeviceId = deviceId,
                    OperationName = "Stage4ReArm",
                    ErrorCode = scheduled == null ? "SCHEDULER_UNAVAILABLE" : scheduled.Code
                });
                return;
            }

            LogDelayedDeviceTaskScheduled("Device alarm redeploy scheduled.", deviceId, "Stage4ReArm", snapshot.Status, attempt, delay, dueAt, reason);
        }

        private void CancelDelayedReArm(int deviceId)
        {
            ClearReArmFailures(deviceId);
            delayedScheduler?.CancelByTaskKey("stage4:rearm:" + deviceId, "manual operation");
            logger?.Info("DeviceLifecycle", "Delayed alarm redeploy cancelled.", new LogFields
            {
                DeviceId = deviceId,
                OperationName = "CancelReArm"
            });
        }

        private void LogManualOperationRequested(int deviceId, string requestId, string operationName)
        {
            logger?.Info("DeviceLifecycle", "Manual device operation requested.", new LogFields
            {
                RequestId = requestId,
                DeviceId = deviceId,
                OperationName = operationName
            });
        }

        private void LogDeviceOperationResult(string message, string requestId, string operationName, DeviceOperationResult result)
        {
            if (logger == null || result == null)
            {
                return;
            }

            var fields = new LogFields
            {
                RequestId = requestId,
                DeviceId = result.DeviceId,
                OperationName = operationName,
                ErrorCode = result.Code
            };
            fields.Extra["success"] = result.Success.ToString();
            fields.Extra["status"] = result.Snapshot == null ? string.Empty : result.Snapshot.Status.ToString();
            fields.Extra["message"] = result.Message ?? string.Empty;
            if (result.Success)
            {
                logger.Info("DeviceLifecycle", message, fields);
            }
            else
            {
                logger.Warn("DeviceLifecycle", message, fields);
            }
        }

        private void LogLifecycleFailure(DeviceTaskContext context, string message, DateTime startedAt, DeviceRuntimeError error, Action<LogFields> configure = null)
        {
            if (logger == null || context == null || context.Task == null || error == null)
            {
                return;
            }

            var fields = new LogFields
            {
                DeviceId = context.Task.DeviceId,
                OperationName = context.Task.OperationName,
                RequestId = string.IsNullOrWhiteSpace(context.Task.RequestId) ? context.RequestContext?.RequestId : context.Task.RequestId,
                TraceId = context.RequestContext?.TraceId,
                ElapsedMs = Math.Max(0, (long)(DateTime.Now - startedAt).TotalMilliseconds),
                ErrorCode = error.Code
            };
            fields.Extra["errorMessage"] = error.Message ?? string.Empty;
            fields.Extra["sdkErrorCode"] = error.SdkErrorCode.HasValue ? error.SdkErrorCode.Value.ToString() : string.Empty;
            fields.Extra["retryable"] = error.Retryable.ToString();
            configure?.Invoke(fields);
            logger.Warn("DeviceLifecycle", message, fields);
        }

        private void LogLifecycleSkip(DeviceTaskContext context, string message, DateTime startedAt, string status)
        {
            if (logger == null || context == null || context.Task == null)
            {
                return;
            }

            var fields = new LogFields
            {
                DeviceId = context.Task.DeviceId,
                OperationName = context.Task.OperationName,
                RequestId = string.IsNullOrWhiteSpace(context.Task.RequestId) ? context.RequestContext?.RequestId : context.Task.RequestId,
                TraceId = context.RequestContext?.TraceId,
                ElapsedMs = Math.Max(0, (long)(DateTime.Now - startedAt).TotalMilliseconds)
            };
            fields.Extra["status"] = status ?? string.Empty;
            logger.Info("DeviceLifecycle", message, fields);
        }

        private void LogDelayedDeviceTaskScheduled(string message, int deviceId, string operationName, DeviceConnectionStatus status, int attemptCount, TimeSpan delay, DateTime dueAt, string reason)
        {
            if (logger == null)
            {
                return;
            }

            var fields = new LogFields
            {
                DeviceId = deviceId,
                OperationName = operationName
            };
            fields.Extra["status"] = status.ToString();
            fields.Extra["attemptCount"] = attemptCount.ToString();
            fields.Extra["delayMs"] = ((long)delay.TotalMilliseconds).ToString();
            fields.Extra["dueAt"] = dueAt.ToString("yyyy-MM-dd HH:mm:ss");
            fields.Extra["reason"] = reason ?? string.Empty;
            logger.Warn("DeviceLifecycle", message, fields);
        }

        private void LogLifecycleSuccess(DeviceTaskContext context, string message, DateTime startedAt, Action<LogFields> configure = null)
        {
            if (logger == null || context == null || context.Task == null)
            {
                return;
            }

            var now = DateTime.Now;
            var fields = new LogFields
            {
                DeviceId = context.Task.DeviceId,
                OperationName = context.Task.OperationName,
                RequestId = string.IsNullOrWhiteSpace(context.Task.RequestId) ? context.RequestContext?.RequestId : context.Task.RequestId,
                TraceId = context.RequestContext?.TraceId,
                ElapsedMs = Math.Max(0, (long)(now - startedAt).TotalMilliseconds)
            };
            fields.Extra["taskId"] = context.Task.TaskId;
            fields.Extra["taskType"] = context.Task.TaskType.ToString();
            configure?.Invoke(fields);
            logger.Info("DeviceLifecycle", message, fields);
        }

        private void LogAlarmProbe(DeviceTaskContext context, string message, AlarmDeploymentStatus status, DateTime startedAt, Action<LogFields> configure = null)
        {
            if (logger == null || context == null || context.Task == null)
            {
                return;
            }

            var now = DateTime.Now;
            var fields = new LogFields
            {
                DeviceId = context.Task.DeviceId,
                OperationName = "AlarmStatusProbe",
                RequestId = string.IsNullOrWhiteSpace(context.Task.RequestId) ? context.RequestContext?.RequestId : context.Task.RequestId,
                TraceId = context.RequestContext?.TraceId,
                ElapsedMs = Math.Max(0, (long)(now - startedAt).TotalMilliseconds)
            };
            if (status != null)
            {
                fields.Extra["known"] = status.Known.ToString();
                fields.Extra["isDeployed"] = status.IsDeployed.ToString();
                fields.Extra["rawSetupAlarmStatus"] = status.RawSetupAlarmStatus.ToString();
                fields.Extra["rawSummary"] = status.RawSummary ?? string.Empty;
            }

            configure?.Invoke(fields);
            logger.Warn("DeviceLifecycle", message, fields);
        }

        private DeviceOperationResult FromTaskResult(DeviceTaskResult result)
        {
            return new DeviceOperationResult
            {
                Success = result.Success,
                Code = result.Code,
                Message = result.Message,
                DeviceId = result.DeviceId,
                TaskResult = result,
                Snapshot = registry.TryGetByDeviceId(result.DeviceId).Snapshot
            };
        }

        private static ValidationResult ValidateRecord(DeviceRecord record)
        {
            if (record == null)
            {
                return ValidationResult.Failed("设备记录不能为空。");
            }

            if (record.DeviceId <= 0)
            {
                return ValidationResult.Failed("deviceId 必须大于 0。");
            }

            if (string.IsNullOrWhiteSpace(record.DeviceName))
            {
                return ValidationResult.Failed("deviceName 不能为空。");
            }

            if (string.IsNullOrWhiteSpace(record.IpAddress))
            {
                return ValidationResult.Failed("ipAddress 不能为空。");
            }

            if (record.Port <= 0 || record.Port > 65535)
            {
                return ValidationResult.Failed("port 必须在 1-65535 范围内。");
            }

            if (record.Types == null || record.Types.Count == 0)
            {
                return ValidationResult.Failed("types 不能为空。");
            }

            if (string.IsNullOrWhiteSpace(record.Password))
            {
                return ValidationResult.Failed("password 不能为空。");
            }

            record.IpAddress = record.IpAddress.Trim();
            record.Username = string.IsNullOrWhiteSpace(record.Username) ? "admin" : record.Username.Trim();
            record.DeviceName = record.DeviceName.Trim();
            return ValidationResult.Ok();
        }

        private int IncrementHealthFailure(int deviceId)
        {
            lock (gate)
            {
                int count;
                healthFailureCounts.TryGetValue(deviceId, out count);
                count++;
                healthFailureCounts[deviceId] = count;
                return count;
            }
        }

        private void ClearHealthFailures(int deviceId)
        {
            lock (gate)
            {
                healthFailureCounts.Remove(deviceId);
            }
        }

        private int IncrementReArmFailure(int deviceId)
        {
            lock (gate)
            {
                int count;
                reArmFailureCounts.TryGetValue(deviceId, out count);
                count++;
                reArmFailureCounts[deviceId] = count;
                return count;
            }
        }

        private void ClearReArmFailures(int deviceId)
        {
            lock (gate)
            {
                reArmFailureCounts.Remove(deviceId);
            }
        }

        private int IncrementAlarmProbeFailure(int deviceId)
        {
            lock (gate)
            {
                int count;
                alarmProbeFailureCounts.TryGetValue(deviceId, out count);
                count++;
                alarmProbeFailureCounts[deviceId] = count;
                return count;
            }
        }

        private void ClearAlarmProbeFailures(int deviceId)
        {
            lock (gate)
            {
                alarmProbeFailureCounts.Remove(deviceId);
            }
        }

        private static DeviceRuntimeError ToRuntimeError(string operationName, Exception ex, DateTime now, bool retryable)
        {
            var gatewayEx = ex as DeviceGatewayException;
            if (gatewayEx != null)
            {
                return DeviceRuntimeError.Create(operationName, "SDK_ERROR", gatewayEx.Error.Message, now, sdkErrorCode: gatewayEx.Error.Code, retryable: retryable);
            }

            if (ex is TimeoutException || ex is OperationCanceledException)
            {
                return DeviceRuntimeError.Create(operationName, "TIMEOUT", ex.Message, now, retryable: true);
            }

            return DeviceRuntimeError.Create(operationName, "DEVICE_ERROR", ex == null ? "设备操作失败。" : ex.Message, now, retryable: retryable);
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(DeviceLifecycleService));
            }
        }

        private sealed class ValidationResult
        {
            public bool Success { get; set; }

            public string Message { get; set; }

            public static ValidationResult Ok()
            {
                return new ValidationResult { Success = true };
            }

            public static ValidationResult Failed(string message)
            {
                return new ValidationResult { Success = false, Message = message };
            }
        }
    }
}
