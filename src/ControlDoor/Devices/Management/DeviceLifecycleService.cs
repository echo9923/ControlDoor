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
        private volatile bool stopping;

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
            if (stopping) return new DeviceOperationResult { DeviceId = deviceId, Code = "SERVICE_STOPPING", Message = "服务正在停止。" };
            var task = CreateLoginTask(deviceId, requestId);
            // Initial login can be rejected before its delegate gets a chance to arrange recovery.
            task.Completion.Task.ContinueWith(completed =>
            {
                OnReconnectDispatchCompleted(deviceId, completed.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? completed.Result : null);
            }, CancellationToken.None, System.Threading.Tasks.TaskContinuationOptions.None, System.Threading.Tasks.TaskScheduler.Default);
            if (wait)
            {
                var result = dispatcher.SubmitAndWaitAsync(task).GetAwaiter().GetResult();
                if (result.Code == "TIMEOUT" && task.ExecutionState == DeviceTaskExecutionState.Cancelled && !task.StartedAt.HasValue)
                {
                    // SubmitAndWait cancels expired queued work instead of letting the worker expire it.
                    result.ExpiredBeforeExecution = true;
                    OnReconnectDispatchCompleted(deviceId, result);
                }
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
            registry.SetManualDisconnected(deviceId, true, DateTime.Now);
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
            // 手动断开失败的设备可能仍在线但带手动断开标记：此时业务任务会被守卫拒绝，
            // "已在线"快速返回对调用方是假成功——必须走完整恢复流程清除标记（复核 H1）。
            if (!force && lookup.Snapshot.IsConnected && !lookup.Snapshot.Reconnect.ManualDisconnected)
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
            var lookup = registry.TryGetByDeviceId(deviceId);
            if (lookup.Found && !disconnectFirst && (lookup.Snapshot.SdkUserId.HasValue || lookup.Snapshot.StaleSdkUserId.HasValue || lookup.Snapshot.Status == DeviceConnectionStatus.Connecting))
            {
                return DeviceOperationResult.FromSnapshot(false, "DEVICE_BUSY", "设备仍有会话，请先断开或启用删除前清理。", lookup.Snapshot);
            }

            // 记录删除前的连接意图：配置写失败已登出的设备需要补偿，避免一次失败的删除让它永久离线。
            var wasManualDisconnected = lookup.Found && lookup.Snapshot.Reconnect.ManualDisconnected;
            var wasAutoOnline = lookup.Found && !wasManualDisconnected &&
                (lookup.Snapshot.Status == DeviceConnectionStatus.Online ||
                 lookup.Snapshot.Status == DeviceConnectionStatus.Degraded ||
                 lookup.Snapshot.Status == DeviceConnectionStatus.Connecting ||
                 lookup.Snapshot.Status == DeviceConnectionStatus.ReconnectPending);
            if (lookup.Found && !registry.SetDeleting(deviceId, true, DateTime.Now).Success)
            {
                return DeviceOperationResult.FromSnapshot(false, "DEVICE_DELETING", "设备正在删除。", lookup.Snapshot);
            }
            CancelDelayedReconnect(deviceId);
            CancelDelayedReArm(deviceId);
            try
            {
                if (lookup.Found)
                {
                    var cleanup = CreateDisconnectTask(deviceId, "DeleteDeviceCleanup", DeviceConnectionStatus.Offline, requestId);
                    cleanup.Priority = DeviceTaskPriority.Critical;
                    var cleanupResult = dispatcher.SubmitAndWaitAsync(cleanup).GetAwaiter().GetResult();
                    if (!cleanupResult.Success && cleanupResult.Code != "OK")
                    {
                        registry.SetDeleting(deviceId, false, DateTime.Now);
                        CompensateFailedDelete(deviceId, wasAutoOnline, wasManualDisconnected, requestId);
                        return FromTaskResult(cleanupResult);
                    }
                }

                CancelDelayedReconnect(deviceId);
                CancelDelayedReArm(deviceId);
                var delete = repository.DeleteDevice(deviceId);
                if (!delete.Success)
                {
                    if (lookup.Found) registry.SetDeleting(deviceId, false, DateTime.Now);
                    CompensateFailedDelete(deviceId, wasAutoOnline, wasManualDisconnected, requestId);
                    return new DeviceOperationResult
                    {
                        Success = false,
                        Code = delete.Code,
                        DeviceId = deviceId,
                        Message = delete.Message
                    };
                }

                if (lookup.Found)
                {
                    registry.RemoveDevice(deviceId, DateTime.Now);
                }

                return new DeviceOperationResult
                {
                    Success = true,
                    Code = "OK",
                    DeviceId = deviceId,
                    Message = "设备已删除。"
                };
            }
            catch
            {
                if (lookup.Found) registry.SetDeleting(deviceId, false, DateTime.Now);
                CompensateFailedDelete(deviceId, wasAutoOnline, wasManualDisconnected, requestId);
                throw;
            }
        }

        // 删除失败补偿按清理后的真实资源状态选择动作（复核 G2）：
        // - SDK 会话仍存活（清理在登出前失败）：不得改状态强制重登覆盖会话；CancelDeleting 已按会话
        //   恢复 Online，撤防已成功的情形按需重新布防，会话继续由既有清理流程持有。
        // - 会话已释放（完全登出后写盘失败）：走统一重连状态机（ReconnectPending + 带完成观察的延迟任务），
        //   补偿任务入队被拒或执行前过期都会被重新安排，健康检查自愈兜底（复核 F02）。
        // 原本手动断开的设备恢复手动标志。需在 SetDeleting(false) 之后调用，否则守卫会拒绝调度。
        private void CompensateFailedDelete(int deviceId, bool wasAutoOnline, bool wasManualDisconnected, string requestId)
        {
            if (wasAutoOnline)
            {
                var snapshot = registry.TryGetByDeviceId(deviceId).Snapshot;
                if (snapshot != null && snapshot.SdkUserId.HasValue)
                {
                    if (options.AlarmEnabled && !snapshot.AlarmHandle.HasValue && !snapshot.AlarmManuallyDisarmed)
                    {
                        SubmitArmAlarm(deviceId, wait: false, requestId: requestId);
                    }

                    logger?.Warn("DeviceLifecycle", "设备删除失败，SDK 会话仍存活，保持在线并按需重新布防。", new LogFields
                    {
                        DeviceId = deviceId,
                        RequestId = requestId,
                        OperationName = "DeleteDeviceCompensation"
                    });
                    return;
                }

                EnsureReconnectScheduled(deviceId, "delete failure compensation");
                logger?.Warn("DeviceLifecycle", "设备删除失败，已安排延迟重连以恢复连接。", new LogFields
                {
                    DeviceId = deviceId,
                    RequestId = requestId,
                    OperationName = "DeleteDeviceCompensation"
                });
            }
            else if (wasManualDisconnected)
            {
                registry.SetManualDisconnected(deviceId, true, DateTime.Now);
                logger?.Warn("DeviceLifecycle", "设备删除失败，已恢复手动断开语义。", new LogFields
                {
                    DeviceId = deviceId,
                    RequestId = requestId,
                    OperationName = "DeleteDeviceCompensation"
                });
            }
        }

        public void BeginStopping()
        {
            stopping = true;
            dispatcher.BeginShutdown();
        }

        public void StopAllDevicesBestEffort()
        {
            StopAllDevicesAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public async System.Threading.Tasks.Task StopAllDevicesAsync(CancellationToken cancellationToken)
        {
            BeginStopping();
            var snapshots = registry.GetAllSnapshots();
            foreach (var snapshot in snapshots)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CancelDelayedReconnect(snapshot.DeviceId);
                    CancelDelayedReArm(snapshot.DeviceId);
                    DeviceTaskResult completed;
                    do
                    {
                        var task = CreateDisconnectTask(snapshot.DeviceId, "ServiceStopCleanup", DeviceConnectionStatus.Offline, string.Empty);
                        task.Priority = DeviceTaskPriority.Critical;
                        task.AllowDuringShutdown = true;
                        task.IgnoreDeadline = true;
                        dispatcher.Submit(task);
                        completed = await task.Completion.Task.ConfigureAwait(false);
                        if (completed.Code == "QUEUE_FULL")
                        {
                            await System.Threading.Tasks.Task.Delay(50, cancellationToken).ConfigureAwait(false);
                        }
                    } while (completed.Code == "QUEUE_FULL");
                    if (!completed.Success) throw new InvalidOperationException(completed.Message);
                }
                catch (Exception ex)
                {
                    logger?.Warn("DeviceLifecycle", "服务停止清理设备失败: " + ex.Message, new LogFields { DeviceId = snapshot.DeviceId });
                }
            }
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
                if (stopping)
                {
                    return DeviceTaskResult.Rejected(context.Task, "SERVICE_STOPPING", "服务正在停止。");
                }
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

                context.Registry.MarkConnecting(deviceId, DateTime.Now);

                try
                {
                    var staleCleanup = await CloseStaleSessionAsync(context, snapshot, started).ConfigureAwait(false);
                    if (staleCleanup != null)
                    {
                        context.Registry.MarkLoginFailed(deviceId, DeviceRuntimeError.Create(
                            "DeviceCloseStaleSession", staleCleanup.Code, staleCleanup.Message, DateTime.Now, retryable: true), DateTime.Now);
                        ScheduleReconnect(deviceId, staleCleanup.Message);
                        return staleCleanup;
                    }

                    var login = await gateway.LoginAsync(new LoginRequest
                    {
                        IpAddress = connection.IpAddress,
                        Port = connection.Port,
                        UserName = connection.Username,
                        Password = connection.Password,
                        TimeoutMilliseconds = options.LoginTimeoutMs
                    }, context.CancellationToken).ConfigureAwait(false);
                    var serial = login.DeviceInfo == null ? string.Empty : login.DeviceInfo.SerialNumber;
                    var register = context.Registry.RegisterSdkUserId(deviceId, login.UserId, serial, DateTime.Now);
                    if (!register.Success)
                    {
                        await gateway.LogoutAsync(new LogoutRequest { UserId = login.UserId }, CancellationToken.None).ConfigureAwait(false);
                        return DeviceTaskResult.FromTask(context.Task, false, register.Code, register.Message, DeviceConnectionStatus.Offline, started, DateTime.Now);
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
                    context.Registry.MarkLoginFailed(deviceId, error, DateTime.Now);
                    ScheduleReconnect(deviceId, error.Message);
                    LogLifecycleFailure(context, "设备登录失败，已安排重连。", started, error);
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

        private async System.Threading.Tasks.Task<DeviceTaskResult> CloseStaleSessionAsync(DeviceTaskContext context, DeviceRuntimeSnapshot snapshot, DateTime started)
        {
            if (snapshot == null || (!snapshot.StaleAlarmHandle.HasValue && !snapshot.StaleSdkUserId.HasValue))
            {
                return null;
            }

            var deviceId = context.Task.DeviceId;
            try
            {
                if (snapshot.StaleAlarmHandle.HasValue)
                {
                    try
                    {
                        await gateway.CloseAlarmAsync(new AlarmCloseRequest { AlarmHandle = snapshot.StaleAlarmHandle.Value }, context.CancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ToRuntimeError("DeviceCloseStaleAlarm", ex, DateTime.Now, true).SdkErrorCode == 17)
                    {
                        // The SDK no longer owns this alarm handle.
                    }
                    context.Registry.ClearStaleAlarmHandle(deviceId, DateTime.Now);
                }

                if (snapshot.StaleSdkUserId.HasValue)
                {
                    try
                    {
                        await gateway.LogoutAsync(new LogoutRequest { UserId = snapshot.StaleSdkUserId.Value }, context.CancellationToken).ConfigureAwait(false);
                    }
                    catch (DeviceGatewayException ex) when (ex.Error.Code == 47)
                    {
                        // NET_DVR_USERNOTEXIST: the previous SDK session is already gone.
                    }
                    context.Registry.MarkLoggedOut(deviceId, DateTime.Now);
                }
                return null;
            }
            catch (Exception ex)
            {
                var error = ToRuntimeError("DeviceCloseStaleSession", ex, DateTime.Now, retryable: true);
                context.Registry.MarkDisconnected(deviceId, error, DateTime.Now, DeviceConnectionStatus.Offline);
                LogLifecycleFailure(context, "旧 SDK 会话清理失败。", started, error);
                var result = DeviceTaskResult.FromTask(context.Task, false, error.Code, error.Message, DeviceConnectionStatus.Offline, started, DateTime.Now);
                result.SdkErrorCode = error.SdkErrorCode;
                result.Retryable = true;
                return result;
            }
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

                if (!snapshot.SdkUserId.HasValue || !snapshot.Enabled || snapshot.Reconnect.ManualDisconnected || snapshot.Status == DeviceConnectionStatus.Disconnected)
                {
                    return DeviceTaskResult.FromTask(context.Task, true, "OK", "设备未在线，跳过状态检测。", snapshot.Status, started, DateTime.Now);
                }

                try
                {
                    await gateway.GetDeviceInfoAsync(new DeviceInfoRequest { UserId = snapshot.SdkUserId.Value }, context.CancellationToken).ConfigureAwait(false);
                    var checkedAt = DateTime.Now;
                    context.Registry.MarkChecked(deviceId, checkedAt, DeviceConnectionStatus.Online);
                    ClearHealthFailures(deviceId);
                    if (logger?.ClearRepeatedWarnings("DeviceLifecycle", deviceId, "DeviceHealthCheck") == true)
                    {
                        logger.Info("DeviceLifecycle", "设备状态检测已恢复。", new LogFields { DeviceId = deviceId });
                    }
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
                        if (snapshot.Status != DeviceConnectionStatus.Offline)
                        {
                            logger?.Warn("DeviceLifecycle", "设备已离线，将自动重连。", new LogFields { DeviceId = deviceId, ErrorCode = error.Code });
                        }
                        ScheduleReconnect(deviceId, error.Message);
                    }
                    else
                    {
                        context.Registry.RecordError(deviceId, error, DateTime.Now, DeviceConnectionStatus.Degraded);
                    }

                    LogLifecycleFailure(context, "设备状态检测失败。", started, error, fields =>
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
            task.RequestId = requestId ?? string.Empty;
            return task;
        }

        private async System.Threading.Tasks.Task ProbeAlarmDeploymentAsync(DeviceTaskContext context, DeviceRuntimeSnapshot snapshot, DateTime started)
        {
            var deviceId = context.Task.DeviceId;
            // 本地缺失句柄自愈不依赖 AlarmStatusProbeEnabled（K1）：设备在线却没有布防句柄
            // 意味着 ACS 事件订阅缺失，健康检查必须补排重布防；该开关只控制设备侧主动探测。
            if (!ShouldCheckLocalAlarmHandle(snapshot))
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

                // 设备侧主动探测仍受 AlarmStatusProbeEnabled 开关控制。
                if (!ShouldProbeAlarmDeployment(snapshot))
                {
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
                    LogAlarmProbe(context, "设备布防状态未知，保留当前布防句柄。", status, started);
                    return;
                }

                if (status.IsDeployed)
                {
                    ClearAlarmProbeFailures(deviceId);
                    if (logger?.ClearRepeatedWarnings("DeviceLifecycle", deviceId, "AlarmStatusProbe") == true)
                    {
                        logger.Info("DeviceLifecycle", "设备布防状态检测已恢复。", new LogFields { DeviceId = deviceId, OperationName = "AlarmStatusProbe" });
                    }
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
                LogAlarmProbe(context, "设备侧未布防，保留当前布防句柄。", status, started, fields =>
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
                fields.OperationName = "AlarmStatusProbe";
                logger?.WarnRepeated("DeviceLifecycle", "设备布防状态探测失败，保留当前布防句柄。", fields);
            }
        }

        // 本地布防句柄自愈守卫（K1）：设备在线且启用 ACS 报警即检查本地句柄，
        // 与 AlarmStatusProbeEnabled 无关——该开关只控制向设备侧主动探测布防状态。
        private bool ShouldCheckLocalAlarmHandle(DeviceRuntimeSnapshot snapshot)
        {
            if (!options.AlarmEnabled || snapshot == null)
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

        private bool ShouldProbeAlarmDeployment(DeviceRuntimeSnapshot snapshot)
        {
            return options.AlarmStatusProbeEnabled && ShouldCheckLocalAlarmHandle(snapshot);
        }

        internal DeviceOperationResult SubmitArmAlarm(int deviceId, bool wait, string requestId)
        {
            var task = CreateArmAlarmTask(deviceId, requestId);
            if (wait)
            {
                return FromTaskResult(dispatcher.SubmitAndWaitAsync(task).GetAwaiter().GetResult());
            }

            // 异步布防的完成结果必须观察：投递被拒或排队过期时布防委托从未运行，
            // 委托内部的重试安排不会发生，不观察则设备在线却永久缺失布防句柄（K1）。
            task.Completion.Task.ContinueWith(completed =>
            {
                OnArmAlarmDispatchCompleted(deviceId, completed.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? completed.Result : null);
            });
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
                if (stopping || snapshot?.AlarmManuallyDisarmed == true)
                {
                    return DeviceTaskResult.FromTask(context.Task, true, "SUPERSEDED", "布防已取消。", snapshot == null ? DeviceConnectionStatus.Unknown : snapshot.Status, started, DateTime.Now);
                }
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
                    var register = context.Registry.RegisterAlarmHandle(deviceId, alarm.AlarmHandle, DateTime.Now);
                    if (!register.Success)
                    {
                        await gateway.CloseAlarmAsync(new AlarmCloseRequest { AlarmHandle = alarm.AlarmHandle }, CancellationToken.None).ConfigureAwait(false);
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
                    context.Registry.RecordError(deviceId, error, DateTime.Now, DeviceConnectionStatus.Degraded);
                    // 布防失败后按指数退避无限重试，直到成功或设备被手动断开/删除/服务停止。
                    if (options.AlarmEnabled)
                    {
                        ScheduleReArm(deviceId, error.Message);
                    }
                    LogLifecycleFailure(context, "设备布防失败。", started, error);
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
                    if (manuallyDisarmed)
                    {
                        context.Registry.MarkAlarmManuallyDisarmed(deviceId, DateTime.Now);
                    }
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
                    if (manuallyDisarmed)
                    {
                        context.Registry.MarkAlarmManuallyDisarmed(deviceId, DateTime.Now);
                    }
                    else
                    {
                        context.Registry.ClearAlarmHandle(deviceId, DateTime.Now);
                    }

                    LogLifecycleSuccess(context, "设备撤防成功。", started, fields =>
                    {
                        fields.Extra["alarmHandle"] = snapshot.AlarmHandle.Value.ToString();
                    });
                }
                catch (Exception ex)
                {
                    lastError = ToRuntimeError("DeviceCloseAlarm", ex, DateTime.Now, retryable: false);
                    context.Registry.RecordError(deviceId, lastError, DateTime.Now, snapshot.Status);
                    LogLifecycleFailure(context, "设备撤防失败。", started, lastError);
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
            task.AllowWhenDeleting = true;
            task.RequestId = requestId ?? string.Empty;
            return task;
        }

        private DeviceSdkTask CreateDisconnectTask(int deviceId, string operationName, DeviceConnectionStatus finalStatus, string requestId)
        {
            var task = new DeviceSdkTask(deviceId, DeviceTaskType.Logout, operationName, async context =>
            {
                var started = DateTime.Now;
                var snapshot = context.SnapshotBeforeExecution;
                if (snapshot == null)
                {
                    return DeviceTaskResult.FromTask(context.Task, true, "OK", "设备不存在，清理视为成功。", DeviceConnectionStatus.Deleted, started, DateTime.Now);
                }

                DeviceRuntimeError lastError = null;
                var staleCleanup = await CloseStaleSessionAsync(context, snapshot, started).ConfigureAwait(false);
                if (staleCleanup != null)
                {
                    return staleCleanup;
                }
                if (snapshot.AlarmHandle.HasValue)
                {
                    var alarmClosed = false;
                    try
                    {
                        await gateway.CloseAlarmAsync(new AlarmCloseRequest { AlarmHandle = snapshot.AlarmHandle.Value }, context.CancellationToken).ConfigureAwait(false);
                        alarmClosed = true;
                        LogLifecycleSuccess(context, "设备撤防成功。", started, fields =>
                        {
                            fields.Extra["alarmHandle"] = snapshot.AlarmHandle.Value.ToString();
                        });
                    }
                    catch (Exception ex)
                    {
                        lastError = ToRuntimeError("DeviceCloseAlarm", ex, DateTime.Now, retryable: false);
                        LogLifecycleFailure(context, "设备断开前撤防失败。", started, lastError);
                    }

                    if (alarmClosed)
                    {
                        context.Registry.ClearAlarmHandle(deviceId, DateTime.Now);
                    }
                    else
                    {
                        context.Registry.RecordError(deviceId, lastError, DateTime.Now, snapshot.Status);
                        return DeviceTaskResult.FromTask(context.Task, false, lastError.Code, lastError.Message, snapshot.Status, started, DateTime.Now);
                    }
                }

                if (snapshot.SdkUserId.HasValue)
                {
                    try
                    {
                        await gateway.LogoutAsync(new LogoutRequest { UserId = snapshot.SdkUserId.Value }, context.CancellationToken).ConfigureAwait(false);
                        context.Registry.MarkLoggedOut(deviceId, DateTime.Now);
                        LogLifecycleSuccess(context, "设备登出成功。", started, fields =>
                        {
                            fields.Extra["userId"] = snapshot.SdkUserId.Value.ToString();
                            fields.Extra["finalStatus"] = finalStatus.ToString();
                        });
                    }
                    catch (Exception ex)
                    {
                        lastError = ToRuntimeError("DeviceLogout", ex, DateTime.Now, retryable: false);
                        LogLifecycleFailure(context, "设备登出失败。", started, lastError);
                    }
                }

                if (finalStatus == DeviceConnectionStatus.Disconnected)
                {
                    context.Registry.MarkManualDisconnected(deviceId, lastError, DateTime.Now);
                }
                else
                {
                    context.Registry.MarkDisconnected(deviceId, lastError, DateTime.Now, finalStatus);
                }

                var success = lastError == null;
                return DeviceTaskResult.FromTask(context.Task, success, success ? "OK" : lastError.Code, success ? "登出清理成功。" : lastError.Message, finalStatus, started, DateTime.Now);
            });
            task.Priority = DeviceTaskPriority.Critical;
            task.TimeoutMilliseconds = options.LogoutTimeoutMs;
            task.AllowWhenDeleting = true;
            task.AllowWhenManualDisconnected = true;
            task.RequestId = requestId ?? string.Empty;
            return task;
        }

        private void ScheduleReconnect(int deviceId, string reason)
        {
            if (stopping) return;
            var snapshot = registry.TryGetByDeviceId(deviceId).Snapshot;
            if (snapshot == null ||
                snapshot.IsDeleting ||
                snapshot.Status == DeviceConnectionStatus.Disconnected ||
                snapshot.Status == DeviceConnectionStatus.InvalidConfig ||
                snapshot.Status == DeviceConnectionStatus.Disabled ||
                snapshot.Reconnect.ManualDisconnected)
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
                    DeviceConnectionStatus.Failed);
                return;
            }

            var policy = RetryBackoffPolicy.Exponential(
                TimeSpan.FromMilliseconds(options.ReconnectBaseDelayMs),
                TimeSpan.FromMilliseconds(options.ReconnectMaxDelayMs));
            var delay = policy.CalculateDelay(snapshot.Reconnect.AttemptCount);
            var dueAt = DateTime.Now.Add(delay);
            registry.MarkReconnectPending(deviceId, dueAt, reason, DateTime.Now);
            var delayedTask = new DelayedDeviceTask(
                deviceId,
                DeviceTaskType.Login,
                DeviceTaskPriority.High,
                dueAt,
                "stage4:reconnect:" + deviceId,
                "Stage4Reconnect",
                () => CreateLoginTask(deviceId, string.Empty),
                DateTime.Now);
            delayedTask.CompletionObserver = (task, result) => OnReconnectDispatchCompleted(deviceId, result);
            var scheduleResult = delayedScheduler?.Schedule(delayedTask);
            if (scheduleResult != null && !scheduleResult.Accepted)
            {
                // 调度被拒绝时状态已是 ReconnectPending，由健康检查的待重连自愈兜底重新安排。
                logger?.Error("DeviceLifecycle", "延迟重连调度失败，等待健康检查自愈。", null, new LogFields
                {
                    DeviceId = deviceId,
                    OperationName = "ScheduleReconnect",
                    ErrorCode = scheduleResult.Status.ToString()
                });
            }

            LogDelayedDeviceTaskScheduled("设备重连已调度。", deviceId, "Stage4Reconnect", snapshot.Status, snapshot.Reconnect.AttemptCount, delay, dueAt, reason);
        }

        // 初次登录和重连共用完成观察；登录委托未执行时补排，执行后的失败由委托处理。
        private void OnReconnectDispatchCompleted(int deviceId, DeviceTaskResult result)
        {
            if (stopping || disposed || result == null || result.Success ||
                (!result.ExpiredBeforeExecution && result.Code != "QUEUE_FULL"))
            {
                return;
            }

            var snapshot = registry.TryGetByDeviceId(deviceId).Snapshot;
            if (snapshot == null || snapshot.IsConnected || snapshot.Status == DeviceConnectionStatus.Connecting)
            {
                return;
            }

            ScheduleReconnect(deviceId, "login task rejected before execution: " + result.Code);
        }

        // 布防任务被设备队列接受后的完成观察（K1）：排队过期或入队被拒意味着布防委托从未运行，
        // 其内部的失败重试安排（CreateArmAlarmTask 的 catch）不会发生，这里补一次调度。
        // 其余失败路径由布防委托自己处理；重复调度由 stage4:rearm 延迟任务同键合并去重。
        private void OnArmAlarmDispatchCompleted(int deviceId, DeviceTaskResult result)
        {
            if (stopping || result == null || result.Success)
            {
                return;
            }

            if (!result.ExpiredBeforeExecution && result.Code != "QUEUE_FULL")
            {
                return;
            }

            ScheduleReArm(deviceId, "arm task rejected before execution: " + result.Code);
        }

        // 健康检查/删除补偿自愈入口：为停留在 ReconnectPending 或已登出的自动在线设备幂等补排重连。
        public void EnsureReconnectScheduled(int deviceId, string reason = "health check reconnect self-heal")
        {
            ScheduleReconnect(deviceId, reason);
        }

        private void CancelDelayedReconnect(int deviceId)
        {
            delayedScheduler?.CancelByTaskKey("stage4:reconnect:" + deviceId, "manual operation");
            logger?.Debug("DeviceLifecycle", "延迟重连已取消。", new LogFields
            {
                DeviceId = deviceId,
                OperationName = "CancelReconnect"
            });
        }

        // 布防失败后无限重试，直到成功或设备被手动断开/删除/服务停止。门控与重连一致。
        private void ScheduleReArm(int deviceId, string reason)
        {
            if (stopping) return;
            var snapshot = registry.TryGetByDeviceId(deviceId).Snapshot;
            if (snapshot == null ||
                snapshot.IsDeleting || snapshot.AlarmManuallyDisarmed ||
                snapshot.Status == DeviceConnectionStatus.Disconnected ||
                snapshot.Status == DeviceConnectionStatus.InvalidConfig ||
                snapshot.Status == DeviceConnectionStatus.Disabled ||
                snapshot.Reconnect.ManualDisconnected)
            {
                return;
            }

            var attempt = IncrementReArmFailure(deviceId);
            var policy = RetryBackoffPolicy.Exponential(
                TimeSpan.FromMilliseconds(options.ReArmBaseDelayMs),
                TimeSpan.FromMilliseconds(options.ReArmMaxDelayMs));
            var delay = policy.CalculateDelay(attempt);
            var dueAt = DateTime.Now.Add(delay);
            var delayedTask = new DelayedDeviceTask(
                deviceId,
                DeviceTaskType.SetupAlarm,
                DeviceTaskPriority.Normal,
                dueAt,
                "stage4:rearm:" + deviceId,
                "Stage4ReArm",
                () => CreateArmAlarmTask(deviceId, string.Empty),
                DateTime.Now);
            // 延迟重布防派发被拒或再次排队过期同样意味着布防委托从未运行（K1），
            // 补一次调度形成闭环；退避计数随每次调度递增，不会形成紧密循环。
            delayedTask.CompletionObserver = (task, result) => OnArmAlarmDispatchCompleted(deviceId, result);
            delayedScheduler?.Schedule(delayedTask);
            LogDelayedDeviceTaskScheduled("设备重新布防已调度。", deviceId, "Stage4ReArm", snapshot.Status, attempt, delay, dueAt, reason);
        }

        private void CancelDelayedReArm(int deviceId)
        {
            ClearReArmFailures(deviceId);
            delayedScheduler?.CancelByTaskKey("stage4:rearm:" + deviceId, "manual operation");
            logger?.Debug("DeviceLifecycle", "延迟布防已取消。", new LogFields
            {
                DeviceId = deviceId,
                OperationName = "CancelReArm"
            });
        }

        private void LogManualOperationRequested(int deviceId, string requestId, string operationName)
        {
            logger?.Debug("DeviceLifecycle", "收到手动设备操作请求。", new LogFields
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
            if (error.Retryable && (context.Task.TaskType == DeviceTaskType.Login || context.Task.TaskType == DeviceTaskType.HealthCheck || context.Task.TaskType == DeviceTaskType.SetupAlarm))
            {
                logger.WarnRepeated("DeviceLifecycle", message, fields);
            }
            else
            {
                logger.Error("DeviceLifecycle", message, fields: fields);
            }
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
            logger.Debug("DeviceLifecycle", message, fields);
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
            logger.Debug("DeviceLifecycle", message, fields);
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
            logger.ClearRepeatedWarnings("DeviceLifecycle", context.Task.DeviceId, context.Task.OperationName);
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

            fields.ErrorCode = status == null || !status.Known ? "ALARM_STATUS_UNKNOWN" : "ALARM_NOT_DEPLOYED";
            configure?.Invoke(fields);
            logger.WarnRepeated("DeviceLifecycle", message, fields);
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
