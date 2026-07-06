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
            var lookup = registry.TryGetByDeviceId(deviceId);
            if (lookup.Found && disconnectFirst)
            {
                var cleanup = CreateDisconnectTask(deviceId, "DeleteDeviceCleanup", DeviceConnectionStatus.Offline, requestId);
                cleanup.Priority = DeviceTaskPriority.Critical;
                var cleanupResult = dispatcher.SubmitAndWaitAsync(cleanup).GetAwaiter().GetResult();
                if (!cleanupResult.Success && cleanupResult.Code != "OK")
                {
                    return FromTaskResult(cleanupResult);
                }
            }

            CancelDelayedReconnect(deviceId);
            CancelDelayedReArm(deviceId);
            var delete = repository.DeleteDevice(deviceId);
            if (!delete.Success)
            {
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

        public void StopAllDevicesBestEffort()
        {
            var snapshots = registry.GetAllSnapshots()
                .Where(item => item.SdkUserId.HasValue || item.AlarmHandle.HasValue || item.IsConnected)
                .ToList();
            foreach (var snapshot in snapshots)
            {
                try
                {
                    CancelDelayedReArm(snapshot.DeviceId);
                    var task = CreateDisconnectTask(snapshot.DeviceId, "ServiceStopCleanup", DeviceConnectionStatus.Offline, string.Empty);
                    task.Priority = DeviceTaskPriority.Critical;
                    dispatcher.SubmitAndWaitAsync(task).GetAwaiter().GetResult();
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

                    var staleCleanup = await CloseStaleAlarmBeforeRearmAsync(context, register.Snapshot, started).ConfigureAwait(false);
                    if (staleCleanup != null)
                    {
                        return staleCleanup;
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
                    LogLifecycleFailure(context, "Device login failed and reconnect was scheduled.", started, error);
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

        private async System.Threading.Tasks.Task<DeviceTaskResult> CloseStaleAlarmBeforeRearmAsync(DeviceTaskContext context, DeviceRuntimeSnapshot snapshot, DateTime started)
        {
            if (context == null || context.Task == null || snapshot == null || !snapshot.StaleAlarmHandle.HasValue)
            {
                return null;
            }

            var deviceId = context.Task.DeviceId;
            var staleAlarmHandle = snapshot.StaleAlarmHandle.Value;
            try
            {
                await gateway.CloseAlarmAsync(new AlarmCloseRequest { AlarmHandle = staleAlarmHandle }, context.CancellationToken).ConfigureAwait(false);
                context.Registry.ClearStaleAlarmHandle(deviceId, DateTime.Now);
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
                    context.Registry.ClearStaleAlarmHandle(deviceId, DateTime.Now);
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

                context.Registry.MarkDisconnected(deviceId, error, DateTime.Now, DeviceConnectionStatus.Offline);
                ScheduleReconnect(deviceId, error.Message);
                LogLifecycleFailure(context, "Stale alarm handle close failed before rearm.", started, error, fields =>
                {
                    fields.Extra["staleAlarmHandle"] = staleAlarmHandle.ToString();
                });
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
                    LogLifecycleFailure(context, "Device alarm deployment failed.", started, error);
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
                        LogLifecycleFailure(context, "Device alarm close failed during disconnect.", started, lastError);
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
                        LogLifecycleSuccess(context, "设备登出成功。", started, fields =>
                        {
                            fields.Extra["userId"] = snapshot.SdkUserId.Value.ToString();
                            fields.Extra["finalStatus"] = finalStatus.ToString();
                        });
                    }
                    catch (Exception ex)
                    {
                        lastError = ToRuntimeError("DeviceLogout", ex, DateTime.Now, retryable: false);
                        LogLifecycleFailure(context, "Device logout failed.", started, lastError);
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
            var snapshot = registry.TryGetByDeviceId(deviceId).Snapshot;
            if (snapshot == null ||
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
            delayedScheduler?.Schedule(new DelayedDeviceTask(
                deviceId,
                DeviceTaskType.Login,
                DeviceTaskPriority.High,
                dueAt,
                "stage4:reconnect:" + deviceId,
                "Stage4Reconnect",
                () => CreateLoginTask(deviceId, string.Empty),
                DateTime.Now));
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
        private void ScheduleReArm(int deviceId, string reason)
        {
            var snapshot = registry.TryGetByDeviceId(deviceId).Snapshot;
            if (snapshot == null ||
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
            delayedScheduler?.Schedule(new DelayedDeviceTask(
                deviceId,
                DeviceTaskType.SetupAlarm,
                DeviceTaskPriority.Normal,
                dueAt,
                "stage4:rearm:" + deviceId,
                "Stage4ReArm",
                () => CreateArmAlarmTask(deviceId, string.Empty),
                DateTime.Now));
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
