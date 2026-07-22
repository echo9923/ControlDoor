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
                await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
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
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await RunOnceAsync(RequestContext.Background("ScanRetryStates").RequestId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger?.Error("DeviceOperationRetry", "补偿后台单轮扫描失败，将等待下一轮恢复。", ex);
                    }

                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
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

        private async Task<DeviceOperationRetryScanResult> RunScanAsync(string requestId, CancellationToken cancellationToken)
        {
            var now = DateTime.Now;
            var stopwatch = Stopwatch.StartNew();
            var result = new DeviceOperationRetryScanResult
            {
                RequestId = string.IsNullOrWhiteSpace(requestId) ? RequestContext.Background("ScanRetryStates").RequestId : requestId,
                ScannedAt = now
            };

            try
            {
                var states = store.LoadDueStates(now, options.BatchSize);
                result.Due = states.Count;
                foreach (var state in states)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = state.StateKey;
                    if (!TryEnterInFlight(key))
                    {
                        result.InFlightSkipped++;
                        LogRetryState(result.RequestId, state, "Retry state skipped because it is already in flight.", "IN_FLIGHT");
                        continue;
                    }

                    try
                    {
                        if (!store.TryClaimDueState(state, now))
                        {
                            result.ClaimSkipped++;
                            LogRetryState(result.RequestId, state, "Retry state claim skipped.", "CLAIM_SKIPPED");
                            continue;
                        }

                        LogRetryState(result.RequestId, state, "Retry state claimed.", "CLAIMED");
                        await ProcessStateAsync(state, result, now, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        LeaveInFlight(key);
                    }
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
                    logger?.Info("DeviceOperationRetry", "补偿扫描完成。", new LogFields
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
                            ["cleanupDeleted"] = result.CleanupDeleted.ToString()
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

        private async Task ProcessStateAsync(DeviceOperationRetryState state, DeviceOperationRetryScanResult scan, DateTime now, CancellationToken cancellationToken)
        {
            if (!state.HasPending)
            {
                if (store.TryDeleteEmptyState(state))
                {
                    scan.EmptyDeleted++;
                    LogRetryState(scan.RequestId, state, "Empty retry state deleted.", "EMPTY_DELETED");
                }
                else
                {
                    LogRetryState(scan.RequestId, state, "Empty retry state delete was stale.", "STALE");
                }

                return;
            }

            var lookup = registry.TryGetByDeviceId(state.DeviceId);
            if (!lookup.Found || lookup.Snapshot == null)
            {
                if (store.MarkTerminalFailure(state, "DEVICE_NOT_FOUND", "设备运行时不存在。", now))
                {
                    scan.Terminal++;
                }

                return;
            }

            var snapshot = lookup.Snapshot;
            if (!snapshot.Enabled || snapshot.Status == DeviceConnectionStatus.Disabled)
            {
                if (store.MarkTerminalFailure(state, "DEVICE_DISABLED", "设备已停用。", now))
                {
                    scan.Terminal++;
                }

                return;
            }

            if (snapshot.Status == DeviceConnectionStatus.InvalidConfig)
            {
                if (store.MarkTerminalFailure(state, "DEVICE_CONFIG_INVALID", "设备配置非法。", now))
                {
                    scan.Terminal++;
                }

                return;
            }

            if (!snapshot.IsConnected || !snapshot.SdkUserId.HasValue || snapshot.Status == DeviceConnectionStatus.ReconnectPending || snapshot.Status == DeviceConnectionStatus.Connecting)
            {
                if (store.DeferOffline(state, "DEVICE_OFFLINE", "设备未在线，延后补偿。", now))
                {
                    scan.OfflineDeferred++;
                }

                return;
            }

            var plan = planner.Plan(state);
            if (!plan.HasSteps)
            {
                if (store.TryDeleteEmptyState(state))
                {
                    scan.EmptyDeleted++;
                    LogRetryState(scan.RequestId, state, "Retry state produced no executable steps and was deleted.", "NO_STEPS");
                }
                else
                {
                    LogRetryState(scan.RequestId, state, "Retry state no-step delete was stale.", "STALE");
                }

                return;
            }

            scan.Submitted++;
            LogRetryState(scan.RequestId, state, "Retry state submitted for execution.", "SUBMITTED", fields =>
            {
                fields.Extra["stepCount"] = plan.Steps.Count.ToString();
            });
            var execution = await coordinator.ExecuteAsync(plan, scan.RequestId, cancellationToken).ConfigureAwait(false);
            store.ApplyExecutionResult(execution, DateTime.Now);
            if (execution.IsStale)
            {
                LogRetryState(scan.RequestId, state, "Retry state execution result was stale and ignored.", "STALE");
            }
            else if (execution.AllSucceeded)
            {
                scan.Succeeded++;
                LogRetryState(scan.RequestId, state, "Retry state execution succeeded.", execution.Code ?? "OK");
            }
            else if (WillMarkTerminal(execution))
            {
                scan.Terminal++;
                LogRetryState(scan.RequestId, state, "Retry state execution reached terminal failure.", execution.Code);
            }
            else
            {
                scan.Failed++;
                LogRetryState(scan.RequestId, state, "Retry state execution failed and will retry.", execution.Code);
            }
        }

        private void LogRetryState(string requestId, DeviceOperationRetryState state, string message, string code, Action<LogFields> configure = null)
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
            fields.Extra["attemptCount"] = state.AttemptCount.ToString();
            fields.Extra["nextRetryAt"] = state.NextRetryAt.HasValue ? state.NextRetryAt.Value.ToString("yyyy-MM-dd HH:mm:ss") : string.Empty;
            fields.Extra["lastError"] = state.LastError ?? string.Empty;
            fields.Extra["permissionPending"] = state.PermissionPending.ToString();
            fields.Extra["personPending"] = state.PersonPending.ToString();
            fields.Extra["facePending"] = state.FacePending.ToString();
            fields.Extra["deletePersonPending"] = state.DeletePersonPending.ToString();
            fields.Extra["deleteFacePending"] = state.DeleteFacePending.ToString();
            configure?.Invoke(fields);
            logger.Info("DeviceOperationRetry", message, fields);
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
