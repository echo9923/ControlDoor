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

            CancelDelayedReconnect(deviceId);
            var task = CreateDisconnectTask(deviceId, "ManualDisconnect", DeviceConnectionStatus.Disconnected, requestId);
            var result = dispatcher.SubmitAndWaitAsync(task).GetAwaiter().GetResult();
            return FromTaskResult(result);
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

            if (!force && lookup.Snapshot.IsConnected)
            {
                return DeviceOperationResult.FromSnapshot(true, "OK", "设备已在线。", lookup.Snapshot);
            }

            CancelDelayedReconnect(deviceId);
            registry.ResetReconnect(deviceId, DateTime.Now);
            var cleanup = CreateDisconnectTask(deviceId, "ReconnectCleanup", DeviceConnectionStatus.Offline, requestId);
            cleanup.Priority = force ? DeviceTaskPriority.Critical : DeviceTaskPriority.High;
            var cleanupResult = dispatcher.SubmitAndWaitAsync(cleanup).GetAwaiter().GetResult();
            if (!cleanupResult.Success && cleanupResult.Code != "OK")
            {
                logger?.Warn("DeviceLifecycle", "重连前清理失败，继续尝试登录。", new LogFields { DeviceId = deviceId, ErrorCode = cleanupResult.Code });
            }

            return SubmitLogin(deviceId, wait: true, requestId: requestId);
        }

        public DeviceOperationResult DeleteDevice(int deviceId, bool disconnectFirst, string requestId)
        {
            var lookup = registry.TryGetByDeviceId(deviceId);
            if (lookup.Found && disconnectFirst)
            {
                var cleanup = CreateDisconnectTask(deviceId, "DeleteDeviceCleanup", DeviceConnectionStatus.Offline, requestId);
                cleanup.Priority = DeviceTaskPriority.Critical;
                dispatcher.SubmitAndWaitAsync(cleanup).GetAwaiter().GetResult();
            }

            CancelDelayedReconnect(deviceId);
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

                    repository.UpdateLastUsedTime(deviceId);
                    ClearHealthFailures(deviceId);
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
                    context.Registry.MarkChecked(deviceId, DateTime.Now, DeviceConnectionStatus.Online);
                    ClearHealthFailures(deviceId);
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
                    var alarm = await gateway.SetAlarmAsync(new AlarmSetupRequest { UserId = snapshot.SdkUserId.Value }, context.CancellationToken).ConfigureAwait(false);
                    var register = context.Registry.RegisterAlarmHandle(deviceId, alarm.AlarmHandle, DateTime.Now);
                    if (!register.Success)
                    {
                        await gateway.CloseAlarmAsync(new AlarmCloseRequest { AlarmHandle = alarm.AlarmHandle }, CancellationToken.None).ConfigureAwait(false);
                        return DeviceTaskResult.FromTask(context.Task, false, register.Code, register.Message, DeviceConnectionStatus.Online, started, DateTime.Now);
                    }

                    return DeviceTaskResult.FromTask(context.Task, true, "OK", "布防成功。", DeviceConnectionStatus.Online, started, DateTime.Now);
                }
                catch (Exception ex)
                {
                    var error = ToRuntimeError("DeviceArmAlarm", ex, DateTime.Now, retryable: true);
                    context.Registry.RecordError(deviceId, error, DateTime.Now, DeviceConnectionStatus.Degraded);
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
                    try
                    {
                        await gateway.CloseAlarmAsync(new AlarmCloseRequest { AlarmHandle = snapshot.AlarmHandle.Value }, context.CancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        lastError = ToRuntimeError("DeviceCloseAlarm", ex, DateTime.Now, retryable: false);
                    }
                    finally
                    {
                        context.Registry.ClearAlarmHandle(deviceId, DateTime.Now);
                    }
                }

                if (snapshot.SdkUserId.HasValue)
                {
                    try
                    {
                        await gateway.LogoutAsync(new LogoutRequest { UserId = snapshot.SdkUserId.Value }, context.CancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        lastError = ToRuntimeError("DeviceLogout", ex, DateTime.Now, retryable: false);
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

            if (snapshot.Reconnect.AttemptCount >= Math.Max(1, options.MaxReconnectAttempts))
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
        }

        private void CancelDelayedReconnect(int deviceId)
        {
            delayedScheduler?.CancelByTaskKey("stage4:reconnect:" + deviceId, "manual operation");
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
