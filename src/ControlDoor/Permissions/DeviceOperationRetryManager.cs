using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Configuration;
using ControlDoor.Devices.Runtime;
using ControlDoor.Observability;
using ControlDoor.Runtime;

namespace ControlDoor.Permissions
{
    public sealed class DeviceOperationRetryManager : IBackgroundTask
    {
        private readonly object gate = new object();
        private readonly DeviceOperationRetryStore store;
        private readonly DeviceRuntimeRegistry registry;
        private readonly RetryExecutionCoordinator coordinator;
        private readonly RetryCommandPlanner planner;
        private readonly DeviceOperationRetryOptions options;
        private readonly ServiceLogger logger;
        private readonly HashSet<string> inFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly BackgroundTaskStatus status = new BackgroundTaskStatus("DeviceOperationRetryManager", false);
        private CancellationTokenSource stopSource;
        private Task loopTask;
        private bool scanning;

        public DeviceOperationRetryManager(
            DeviceOperationRetryStore store,
            DeviceRuntimeRegistry registry,
            RetryExecutionCoordinator coordinator,
            DeviceOperationRetryOptions options = null,
            ServiceLogger logger = null)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            this.options = options ?? new DeviceOperationRetryOptions();
            this.logger = logger;
            planner = new RetryCommandPlanner();
        }

        public string Name => "DeviceOperationRetryManager";

        public bool IsCritical => false;

        public Task Completion
        {
            get { lock (gate) { return loopTask ?? Task.CompletedTask; } }
        }

        public Task StartAsync(BackgroundTaskContext context)
        {
            lock (gate)
            {
                if (loopTask != null && !loopTask.IsCompleted)
                {
                    return Task.CompletedTask;
                }

                stopSource = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
                status.MarkStarted();
                loopTask = Task.Run(() => RunLoopAsync(stopSource.Token));
            }

            return Task.CompletedTask;
        }

        public async Task StopAsync(BackgroundTaskContext context)
        {
            Task running;
            lock (gate)
            {
                stopSource?.Cancel();
                running = loopTask;
            }

            if (running != null)
            {
                await running.ConfigureAwait(false);
            }

            lock (gate)
            {
                status.MarkStopped();
                stopSource?.Dispose();
                stopSource = null;
                loopTask = null;
            }
        }

        public BackgroundTaskStatus GetStatus()
        {
            lock (gate)
            {
                return status.Clone();
            }
        }

        public async Task<DeviceOperationRetryScanResult> RunOnceAsync(string requestId = null, CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                if (scanning)
                {
                    return new DeviceOperationRetryScanResult
                    {
                        RequestId = requestId ?? string.Empty,
                        ScannedAt = DateTime.Now
                    };
                }

                scanning = true;
            }

            try
            {
                return await RunScanAsync(requestId, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (gate)
                {
                    scanning = false;
                }
            }
        }

        private async Task RunLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                var delay = TimeSpan.FromSeconds(Math.Max(1, options.ScanIntervalSeconds));
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, Math.Max(1, options.ScanIntervalSeconds))), cancellationToken).ConfigureAwait(false);
                var lastMaintenanceAt = DateTime.Now;
                while (!cancellationToken.IsCancellationRequested)
                {
                    DeviceOperationRetryScanResult scan = null;
                    try
                    {
                        scan = await RunOnceAsync(RequestContext.Background("ScanRetryStates").RequestId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger?.Error("DeviceOperationRetry", "补偿后台单轮扫描失败，将等待下一轮恢复。", ex);
                    }

                    // 维护轮（K3）：低频按 id 游标巡检，对设备已从运行时移除/停用/配置非法的
                    // 补偿状态做终态清理；只读摘要，离线设备的记录不做任何写。
                    if ((DateTime.Now - lastMaintenanceAt).TotalSeconds >= Math.Max(30, options.MaintenanceIntervalSeconds))
                    {
                        lastMaintenanceAt = DateTime.Now;
                        try
                        {
                            RunMaintenanceScan(RequestContext.Background("MaintainRetryStates").RequestId);
                        }
                        catch (Exception ex)
                        {
                            logger?.Error("DeviceOperationRetry", "补偿维护巡检失败，将等待下一轮恢复。", ex);
                        }
                    }

                    await Task.Delay(GetScanDelay(options, scan != null && scan.Due >= Math.Max(1, options.BatchSize)), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                status.MarkStopped();
            }
            catch (Exception ex)
            {
                status.MarkFailed(ex);
                logger?.Error("DeviceOperationRetry", "补偿后台扫描循环异常。", ex);
            }
        }

        // K3：上轮读满一批说明仍有到期积压，用短间隔连续扫描（每轮仍执行真实领取与执行，有天然预算）；
        // 空闲或未读满时维持常规间隔。
        internal static TimeSpan GetScanDelay(DeviceOperationRetryOptions options, bool hasBacklog)
        {
            if (options == null)
            {
                return TimeSpan.FromSeconds(30);
            }

            return TimeSpan.FromSeconds(hasBacklog
                ? Math.Max(1, options.BacklogScanIntervalSeconds)
                : Math.Max(1, options.ScanIntervalSeconds));
        }

        private async Task<DeviceOperationRetryScanResult> RunScanAsync(string requestId, CancellationToken cancellationToken)
        {
            var now = DateTime.Now;
            var stopwatch = Stopwatch.StartNew();
            var result = new DeviceOperationRetryScanResult
            {
                RequestId = string.IsNullOrWhiteSpace(requestId) ? RequestContext.Background("ScanRetryStates").RequestId : requestId,
                ScannedAt = now
            };
            using var scanScope = logger?.BeginScope(new LogFields { RequestId = result.RequestId, TraceId = result.RequestId, OperationName = "ScanRetryStates" });

            try
            {
                // K3/L3：先按运行时可执行设备过滤读轻量摘要（不含 payload 大列），数据库侧按设备
                // 分区配额（每台最多 perDeviceQuota 条）避免单设备积压挤占其他在线设备的候选名额，
                // 内存中再按设备公平选取后取回完整载荷。
                var onlineDeviceIds = GetOnlineRetryDeviceIds();
                IReadOnlyList<DeviceOperationRetryState> states;
                if (onlineDeviceIds.Count == 0)
                {
                    states = new List<DeviceOperationRetryState>();
                }
                else
                {
                    var summaries = store.LoadDueSummaries(now, GetPerDeviceSummaryQuota(options.BatchSize), onlineDeviceIds);
                    var selectedIds = SelectFairlyAcrossDevices(summaries, Math.Max(1, options.BatchSize)).Select(state => state.Id).ToList();
                    states = store.LoadStatesByIds(selectedIds);
                }

                result.Due = states.Count;
                var lanes = states.GroupBy(state => registry.TryGetWorkerRoute(state.DeviceId).WorkerIndex ?? -1);
                var scans = await Task.WhenAll(lanes.Select(lane => Task.Run(async () =>
                {
                    var laneResult = new DeviceOperationRetryScanResult { RequestId = result.RequestId, ScannedAt = now };
                    foreach (var state in lane)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var key = state.StateKey;
                        if (!TryEnterInFlight(key))
                        {
                            laneResult.InFlightSkipped++;
                            LogRetryState(result.RequestId, state, "Retry state skipped because it is already in flight.", "IN_FLIGHT");
                            continue;
                        }

                        try
                        {
                            var claimedAt = DateTime.Now;
                            if (!store.TryClaimDueState(state, claimedAt))
                            {
                                laneResult.ClaimSkipped++;
                                LogRetryState(result.RequestId, state, "Retry state claim skipped.", "CLAIM_SKIPPED");
                                continue;
                            }

                            LogRetryState(result.RequestId, state, "Retry state claimed.", "CLAIMED");
                            await ProcessStateAsync(state, laneResult, claimedAt, cancellationToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            LeaveInFlight(key);
                        }
                    }
                    return laneResult;
                }))).ConfigureAwait(false);
                foreach (var scan in scans)
                {
                    result.InFlightSkipped += scan.InFlightSkipped;
                    result.ClaimSkipped += scan.ClaimSkipped;
                    result.Submitted += scan.Submitted;
                    result.OfflineDeferred += scan.OfflineDeferred;
                    result.Terminal += scan.Terminal;
                    result.EmptyDeleted += scan.EmptyDeleted;
                    result.Succeeded += scan.Succeeded;
                    result.Failed += scan.Failed;
                }

                result.CleanupDeleted = store.CleanupExpiredFailures(DateTime.Now, options.BatchSize);
            }
            catch (Exception ex)
            {
                logger?.Error("DeviceOperationRetry", "补偿扫描失败。", ex, new LogFields { RequestId = result.RequestId });
                throw;
            }
            finally
            {
                stopwatch.Stop();
                result.ElapsedMs = stopwatch.ElapsedMilliseconds;
                status.Heartbeat();
                if (HasObservableRetryScanWork(result))
                {
                    var level = result.Terminal > 0 ? LogLevel.Error : result.Failed > 0 ? LogLevel.Warn
                        : result.Submitted > 0 || result.Succeeded > 0 ? LogLevel.Info : LogLevel.Debug;
                    logger?.Write(level, "DeviceOperationRetry", "补偿扫描完成。", new LogFields
                    {
                        RequestId = result.RequestId,
                        OperationName = "ScanRetryStates",
                        ElapsedMs = result.ElapsedMs,
                        Extra =
                        {
                            ["due"] = result.Due.ToString(),
                            ["submitted"] = result.Submitted.ToString(),
                            ["offlineDeferred"] = result.OfflineDeferred.ToString(),
                            ["inFlightSkipped"] = result.InFlightSkipped.ToString(),
                            ["claimSkipped"] = result.ClaimSkipped.ToString(),
                            ["succeeded"] = result.Succeeded.ToString(),
                            ["failed"] = result.Failed.ToString(),
                            ["terminal"] = result.Terminal.ToString(),
                            ["emptyDeleted"] = result.EmptyDeleted.ToString(),
                            ["cleanupDeleted"] = result.CleanupDeleted.ToString(),
                            ["countBasis"] = "本轮设备与员工补偿状态条数"
                        }
                    });
                }
            }

            return result;
        }

        private static bool HasObservableRetryScanWork(DeviceOperationRetryScanResult result)
        {
            if (result == null)
            {
                return false;
            }

            return result.Due != 0 ||
                result.Submitted != 0 ||
                result.OfflineDeferred != 0 ||
                result.InFlightSkipped != 0 ||
                result.ClaimSkipped != 0 ||
                result.Succeeded != 0 ||
                result.Failed != 0 ||
                result.Terminal != 0 ||
                result.EmptyDeleted != 0 ||
                result.CleanupDeleted != 0;
        }

        // 摘要每设备配额（复核 L3）：默认 BatchSize/10（下限 2），即默认每台在线设备最多贡献 10 条候选；
        // 配额在数据库分区排名中施加（全局截断之前），保证任何在线设备的到期记录都能进入候选集合。
        internal static int GetPerDeviceSummaryQuota(int batchSize)
        {
            return Math.Max(2, Math.Max(1, batchSize) / 10);
        }

        // 与 ProcessStateAsync 的离线判定保持一致：未连接、无会话、等待重连或连接中的设备都不可执行。
        private List<int> GetOnlineRetryDeviceIds()
        {
            return registry.GetAllSnapshots()
                .Where(snapshot => snapshot != null
                    && snapshot.Enabled
                    && !snapshot.IsDeleting
                    && snapshot.IsConnected
                    && snapshot.SdkUserId.HasValue
                    && snapshot.Status != DeviceConnectionStatus.ReconnectPending
                    && snapshot.Status != DeviceConnectionStatus.Connecting)
                .Select(snapshot => snapshot.DeviceId)
                .ToList();
        }

        // 按设备轮转交错选取（K3）：单个设备的大量到期记录不会连续占满整批，
        // 多设备在每轮扫描中都能获得配额；每设备内部保持 SQL 返回的到期先后顺序。
        internal static IEnumerable<DeviceOperationRetryState> SelectFairlyAcrossDevices(IReadOnlyList<DeviceOperationRetryState> summaries, int batchSize)
        {
            if (summaries == null || summaries.Count == 0 || batchSize <= 0)
            {
                return new List<DeviceOperationRetryState>();
            }

            var queues = summaries
                .GroupBy(state => state.DeviceId)
                .Select(group => new Queue<DeviceOperationRetryState>(group))
                .ToList();
            var selected = new List<DeviceOperationRetryState>(Math.Min(batchSize, summaries.Count));
            var anyRemaining = true;
            while (selected.Count < batchSize && anyRemaining)
            {
                anyRemaining = false;
                foreach (var queue in queues)
                {
                    if (selected.Count >= batchSize)
                    {
                        break;
                    }

                    if (queue.Count == 0)
                    {
                        continue;
                    }

                    selected.Add(queue.Dequeue());
                    anyRemaining = true;
                }
            }

            return selected;
        }

        // 维护轮（K3）：按 id 游标低频巡检到期摘要，只对设备已从运行时移除、已停用或配置非法的
        // 补偿状态标记终态；离线设备记录不做任何写（主扫描的在线过滤已排除，等待设备恢复在线）。
        // 游标保证全表可覆盖，不会被长期离线设备的旧到期记录挡住；不足一批时回卷到表头。
        private long maintenanceCursorId;

        internal void RunMaintenanceScan(string requestId)
        {
            var now = DateTime.Now;
            var summaries = store.LoadMaintenanceSummaries(now, maintenanceCursorId, options.BatchSize);
            maintenanceCursorId = summaries.Count >= Math.Max(1, options.BatchSize)
                ? summaries[summaries.Count - 1].Id
                : 0;
            var terminal = 0;
            var emptyDeleted = 0;
            foreach (var state in summaries)
            {
                var lookup = registry.TryGetByDeviceId(state.DeviceId);
                if (!lookup.Found || lookup.Snapshot == null)
                {
                    store.MarkTerminalFailure(state, "DEVICE_NOT_FOUND", "设备运行时不存在。", now);
                    terminal++;
                    LogRetryState(requestId, state, "设备已移除，补偿状态转终态。", "DEVICE_NOT_FOUND", level: LogLevel.Warn);
                    continue;
                }

                var snapshot = lookup.Snapshot;
                if (!snapshot.Enabled || snapshot.Status == DeviceConnectionStatus.Disabled)
                {
                    store.MarkTerminalFailure(state, "DEVICE_DISABLED", "设备已停用。", now);
                    terminal++;
                    LogRetryState(requestId, state, "设备已停用，补偿状态转终态。", "DEVICE_DISABLED", level: LogLevel.Warn);
                    continue;
                }

                if (snapshot.Status == DeviceConnectionStatus.InvalidConfig)
                {
                    store.MarkTerminalFailure(state, "DEVICE_CONFIG_INVALID", "设备配置非法。", now);
                    terminal++;
                    LogRetryState(requestId, state, "设备配置非法，补偿状态转终态。", "DEVICE_CONFIG_INVALID", level: LogLevel.Warn);
                    continue;
                }

                if (!state.HasPending)
                {
                    store.DeleteEmptyState(state);
                    emptyDeleted++;
                }
            }

            if (terminal > 0 || emptyDeleted > 0)
            {
                var fields = new LogFields
                {
                    RequestId = requestId,
                    OperationName = "MaintainRetryStates"
                };
                fields.Extra["terminal"] = terminal.ToString();
                fields.Extra["emptyDeleted"] = emptyDeleted.ToString();
                fields.Extra["examined"] = summaries.Count.ToString();
                logger?.Info("DeviceOperationRetry", "补偿维护巡检完成。", fields);
            }
        }

        private async Task ProcessStateAsync(DeviceOperationRetryState state, DeviceOperationRetryScanResult scan, DateTime now, CancellationToken cancellationToken)
        {
            if (!state.HasPending)
            {
                store.DeleteEmptyState(state);
                scan.EmptyDeleted++;
                LogRetryState(scan.RequestId, state, "Empty retry state deleted.", "EMPTY_DELETED");
                return;
            }

            var lookup = registry.TryGetByDeviceId(state.DeviceId);
            if (!lookup.Found || lookup.Snapshot == null)
            {
                store.MarkTerminalFailure(state, "DEVICE_NOT_FOUND", "设备运行时不存在。", now);
                scan.Terminal++;
                return;
            }

            var snapshot = lookup.Snapshot;
            if (!snapshot.Enabled || snapshot.Status == DeviceConnectionStatus.Disabled)
            {
                store.MarkTerminalFailure(state, "DEVICE_DISABLED", "设备已停用。", now);
                scan.Terminal++;
                return;
            }

            if (snapshot.Status == DeviceConnectionStatus.InvalidConfig)
            {
                store.MarkTerminalFailure(state, "DEVICE_CONFIG_INVALID", "设备配置非法。", now);
                scan.Terminal++;
                return;
            }

            if (!snapshot.IsConnected || !snapshot.SdkUserId.HasValue || snapshot.Status == DeviceConnectionStatus.ReconnectPending || snapshot.Status == DeviceConnectionStatus.Connecting)
            {
                store.DeferOffline(state, "DEVICE_OFFLINE", "设备未在线，延后补偿。", now);
                scan.OfflineDeferred++;
                return;
            }

            var plan = planner.Plan(state);
            if (!plan.HasSteps)
            {
                store.DeleteEmptyState(state);
                scan.EmptyDeleted++;
                LogRetryState(scan.RequestId, state, "Retry state produced no executable steps and was deleted.", "NO_STEPS");
                return;
            }

            scan.Submitted++;
            LogRetryState(scan.RequestId, state, "Retry state submitted for execution.", "SUBMITTED", fields =>
            {
                fields.Extra["stepCount"] = plan.Steps.Count.ToString();
            });
            var execution = await coordinator.ExecuteAsync(plan, scan.RequestId, cancellationToken).ConfigureAwait(false);
            store.ApplyExecutionResult(execution, DateTime.Now);
            if (execution.AllSucceeded)
            {
                scan.Succeeded++;
                LogRetryState(scan.RequestId, state, "Retry state execution succeeded.", execution.Code ?? "OK");
            }
            else if (WillMarkTerminal(execution))
            {
                scan.Terminal++;
                LogRetryState(scan.RequestId, state, "补偿已终止，需要人工处理。", execution.Code, level: LogLevel.Error);
            }
            else
            {
                scan.Failed++;
                LogRetryState(scan.RequestId, state, "补偿执行失败，将继续重试。", execution.Code, level: LogLevel.Warn);
            }
        }

        private void LogRetryState(string requestId, DeviceOperationRetryState state, string message, string code, Action<LogFields> configure = null, LogLevel level = LogLevel.Debug)
        {
            if (logger == null || state == null)
            {
                return;
            }

            var fields = new LogFields
            {
                RequestId = requestId,
                DeviceId = state.DeviceId,
                EmployeeId = state.EmployeeId,
                OperationName = "ScanRetryStates",
                ErrorCode = code
            };
            fields.Extra["stateId"] = state.Id.ToString();
            fields.Extra["intentVersion"] = state.IntentVersion.ToString();
            fields.Extra["attemptCount"] = state.AttemptCount.ToString();
            fields.Extra["nextRetryAt"] = state.NextRetryAt.HasValue ? state.NextRetryAt.Value.ToString("yyyy-MM-dd HH:mm:ss") : string.Empty;
            fields.Extra["lastError"] = state.LastError ?? string.Empty;
            fields.Extra["permissionPending"] = state.PermissionPending.ToString();
            fields.Extra["personPending"] = state.PersonPending.ToString();
            fields.Extra["facePending"] = state.FacePending.ToString();
            fields.Extra["deletePersonPending"] = state.DeletePersonPending.ToString();
            fields.Extra["deleteFacePending"] = state.DeleteFacePending.ToString();
            configure?.Invoke(fields);
            logger.Write(level, "DeviceOperationRetry", message, fields);
        }

        private bool WillMarkTerminal(RetryExecutionResult execution)
        {
            if (execution == null || execution.State == null)
            {
                return false;
            }

            var nextAttempt = Math.Max(0, execution.State.AttemptCount) + 1;
            if (nextAttempt >= Math.Max(1, options.MaxRetryAttempts))
            {
                return true;
            }

            if (execution.Retryable)
            {
                return false;
            }

            switch ((execution.Code ?? string.Empty).Trim())
            {
                case "DEVICE_NOT_FOUND":
                case "DEVICE_DISABLED":
                case "DEVICE_CONFIG_INVALID":
                case "DEVICE_UNSUPPORTED":
                case "INVALID_PAYLOAD":
                case "SDK_CONFIGURATION_ERROR":
                    return true;
                default:
                    return false;
            }
        }

        private bool TryEnterInFlight(string key)
        {
            lock (gate)
            {
                return inFlight.Add(key ?? string.Empty);
            }
        }

        private void LeaveInFlight(string key)
        {
            lock (gate)
            {
                inFlight.Remove(key ?? string.Empty);
            }
        }
    }
}
