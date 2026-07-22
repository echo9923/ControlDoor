using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Configuration;
using ControlDoor.Devices.Workers;
using ControlDoor.Observability;
using ControlDoor.Runtime;

namespace ControlDoor.CameraDoorInterlock
{
    /// <summary>
    /// 阶段 9 总入口（task01）：消费 AIOP 报警，按摄像头独立窗口驱动门禁常闭/恢复。
    /// 扫描循环驱动窗口到期与恢复重试（镜像阶段 6 DeviceOperationRetryManager 的扫描模式）。
    /// 常闭/恢复均 fire-and-observe，失败登记重试由扫描循环重投；服务停止时对活动门目标 best-effort 恢复（每门独立时间片）。
    /// 所有时间由调用方显式传入（ProcessEvents/ExpireWindows/ProcessRestoreRetries），便于单元测试。
    /// </summary>
    public sealed class CameraDoorInterlockService : IBackgroundTask, IAiopAlarmEventSink, IDisposable
    {
        public const int DefaultQueueCapacity = 1000;
        public const int DefaultScanIntervalMs = 500;

        private readonly object gate = new object();
        private readonly CameraAlarmDoorInterlockOptions options;
        private readonly InterlockMappingResolver resolver;
        private readonly AiopVideoPayloadParser parser;
        private readonly CameraAlarmWindowManager windowManager;
        private readonly DoorTargetStateManager targetManager;
        private readonly DoorControlTaskFactory taskFactory;
        private readonly DeviceSdkDispatcher dispatcher;
        private readonly Func<DateTime> clock;
        private readonly ServiceLogger logger;
        private readonly int scanIntervalMs;
        private readonly BlockingCollection<RawAiopAlarmEvent> queue;
        private readonly BackgroundTaskStatus status;
        private readonly bool disabled;

        private CancellationTokenSource stopSource;
        private Task loopTask;
        private bool disposed;

        public CameraDoorInterlockService(
            CameraAlarmDoorInterlockOptions options,
            InterlockMappingResolver resolver,
            AiopVideoPayloadParser parser,
            CameraAlarmWindowManager windowManager,
            DoorTargetStateManager targetManager,
            DoorControlTaskFactory taskFactory,
            DeviceSdkDispatcher dispatcher,
            Func<DateTime> clock = null,
            ServiceLogger logger = null,
            int scanIntervalMs = DefaultScanIntervalMs,
            int queueCapacity = DefaultQueueCapacity)
        {
            this.options = options ?? new CameraAlarmDoorInterlockOptions();
            this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            this.parser = parser ?? new AiopVideoPayloadParser();
            this.windowManager = windowManager ?? throw new ArgumentNullException(nameof(windowManager));
            this.targetManager = targetManager ?? throw new ArgumentNullException(nameof(targetManager));
            this.taskFactory = taskFactory ?? throw new ArgumentNullException(nameof(taskFactory));
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            this.clock = clock ?? (() => DateTime.Now);
            this.logger = logger;
            this.scanIntervalMs = scanIntervalMs < 10 ? DefaultScanIntervalMs : scanIntervalMs;
            this.queue = new BlockingCollection<RawAiopAlarmEvent>(Math.Max(16, queueCapacity));
            this.status = new BackgroundTaskStatus("CameraDoorInterlockService", false);

            disabled = !this.options.Enabled || !this.resolver.HasValidMapping;
            if (disabled && this.options.Enabled)
            {
                this.logger?.Error("CameraDoorInterlock", "阶段 9 已启用但无有效摄像头→门禁映射，CameraDoorInterlockService 自禁用。", null);
            }
        }

        public string Name => "CameraDoorInterlockService";

        public bool IsCritical => false;

        public bool IsDisabled => disabled;

        public Task StartAsync(BackgroundTaskContext context)
        {
            lock (gate)
            {
                if (loopTask != null && !loopTask.IsCompleted)
                {
                    return Task.CompletedTask;
                }

                status.MarkStarted();
                if (disabled)
                {
                    logger?.Info("CameraDoorInterlock", "阶段 9 已禁用，后台扫描循环不启动。");
                    return Task.CompletedTask;
                }

                stopSource = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
                loopTask = Task.Run(() => RunLoopAsync(stopSource.Token));
            }

            return Task.CompletedTask;
        }

        public async Task StopAsync(BackgroundTaskContext context)
        {
            Task running;
            CancellationTokenSource source;
            lock (gate)
            {
                source = stopSource;
                running = loopTask;
            }

            if (source != null)
            {
                source.Cancel();
            }

            if (running != null)
            {
                await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            }

            if (!disabled)
            {
                try
                {
                    RestoreActiveTargetsBestEffort(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    logger?.Error("CameraDoorInterlock", "停止恢复活动门目标失败。", ex);
                }
            }

            lock (gate)
            {
                status.MarkStopped();
                source?.Dispose();
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

        public AiopAlarmEnqueueResult TryEnqueue(RawAiopAlarmEvent alarmEvent)
        {
            if (alarmEvent == null)
            {
                return AiopAlarmEnqueueResult.Rejected("INVALID_ARGUMENT", "alarm event is required", 0, SafeQueueCapacity());
            }

            if (disabled)
            {
                return AiopAlarmEnqueueResult.Rejected("DISABLED", "camera door interlock is disabled", SafeQueueDepth(), SafeQueueCapacity());
            }

            if (disposed)
            {
                return AiopAlarmEnqueueResult.Rejected("DISPOSED", "camera door interlock service is disposed", 0, 0);
            }

            lock (gate)
            {
                if (disposed)
                {
                    return AiopAlarmEnqueueResult.Rejected("DISPOSED", "camera door interlock service is disposed", 0, 0);
                }

                try
                {
                    if (queue.TryAdd(alarmEvent, 0))
                    {
                        return AiopAlarmEnqueueResult.AcceptedResult(queue.Count, queue.BoundedCapacity);
                    }
                }
                catch (ObjectDisposedException)
                {
                    return AiopAlarmEnqueueResult.Rejected("DISPOSED", "camera door interlock service is disposed", 0, 0);
                }
            }

            logger?.Warn("CameraDoorInterlock", "AIOP 报警队列已满，丢弃事件。", new LogFields
            {
                Extra =
                {
                    ["cameraKey"] = alarmEvent.CameraKey ?? string.Empty,
                    ["cameraIp"] = alarmEvent.CameraIp ?? string.Empty,
                    ["queueDepth"] = SafeQueueDepth().ToString()
                }
            });
            return AiopAlarmEnqueueResult.Rejected("QUEUE_FULL", "AIOP alarm queue is full", SafeQueueDepth(), SafeQueueCapacity());
        }

        /// <summary>消费队列中的 AIOP 报警：解析载荷、开窗、对新增活动门目标投递常闭任务。</summary>
        public InterlockProcessResult ProcessEvents(DateTime now)
        {
            var result = new InterlockProcessResult();
            if (disabled)
            {
                return result;
            }

            var batch = new List<RawAiopAlarmEvent>();
            while (queue.TryTake(out var evt))
            {
                batch.Add(evt);
            }

            result.EventsProcessed = batch.Count;
            foreach (var evt in batch)
            {
                try
                {
                    ProcessOneEvent(evt, now, result);
                }
                catch (Exception ex)
                {
                    logger?.Error("CameraDoorInterlock", "处理 AIOP 报警事件失败。", ex, new LogFields
                    {
                        Extra = { ["cameraKey"] = evt.CameraKey ?? string.Empty }
                    });
                }
            }

            status.Heartbeat();
            return result;
        }

        private void ProcessOneEvent(RawAiopAlarmEvent evt, DateTime now, InterlockProcessResult result)
        {
            var interlockId = NormalizeInterlockId(evt.InterlockId);
            var payload = parser.Parse(evt.RawPayload, evt.CameraDeviceId, evt.CameraIp);
            if (!payload.ParseSucceeded)
            {
                logger?.Warn("CameraDoorInterlock", "AIOP 载荷解析失败，仍按命中摄像头触发联动。", new LogFields
                {
                    RequestId = interlockId,
                    OperationName = "AiopInterlockProcess",
                    Extra =
                    {
                        ["interlockId"] = interlockId,
                        ["cameraKey"] = evt.CameraKey,
                        ["cameraIp"] = evt.CameraIp,
                        ["parseError"] = payload.ParseError
                    }
                });
            }

            var targets = resolver.ResolveTargets(evt.CameraKey);
            if (targets.Count == 0)
            {
                logger?.Warn("CameraDoorInterlock", "AIOP 报警未解析到有效门禁目标，跳过。", new LogFields
                {
                    RequestId = interlockId,
                    OperationName = "AiopInterlockProcess",
                    Extra = { ["interlockId"] = interlockId, ["cameraKey"] = evt.CameraKey }
                });
                return;
            }

            var targetKeys = new List<string>(targets.Count);
            foreach (var target in targets)
            {
                targetKeys.Add(target.TargetKey);
            }

            var windowResult = windowManager.OpenOrRecord(evt.CameraKey, targetKeys, now);
            result.WindowsTouched++;
            if (windowResult.OpenedNew)
            {
                logger?.Info("CameraDoorInterlock", "摄像头报警窗口开启。", new LogFields
                {
                    RequestId = interlockId,
                    OperationName = "OpenInterlockWindow",
                    Extra =
                    {
                        ["interlockId"] = interlockId,
                        ["cameraKey"] = evt.CameraKey,
                        ["startedAt"] = windowResult.Window.StartedAt.ToString("O"),
                        ["endsAt"] = windowResult.Window.EndsAt.ToString("O"),
                        ["targetCount"] = targetKeys.Count.ToString()
                    }
                });
            }
            else
            {
                logger?.Info("CameraDoorInterlock", "摄像头窗口内重复报警，窗口已续期。", new LogFields
                {
                    RequestId = interlockId,
                    OperationName = "ExtendInterlockWindow",
                    Extra =
                    {
                        ["interlockId"] = interlockId,
                        ["cameraKey"] = evt.CameraKey,
                        ["endsAt"] = windowResult.Window.EndsAt.ToString("O"),
                        ["triggeredCount"] = windowResult.Window.TriggeredCount.ToString()
                    }
                });
                return;
            }

            foreach (var target in targets)
            {
                var change = targetManager.OnCameraWindowOpened(evt.CameraKey, target, now, interlockId);
                if (change.ShouldSubmitAlwaysClose && change.Activity != null)
                {
                    SubmitAlwaysClose(target, interlockId, now, change.Activity.ActivityGeneration, change.Activity.AlwaysCloseOperationToken);
                    result.AlwaysCloseSubmissions++;
                }
            }
        }

        /// <summary>到期窗口移除，门目标活动集合清空时投递恢复任务（首次恢复 attempt=0）。</summary>
        public InterlockExpireResult ExpireWindows(DateTime now)
        {
            var result = new InterlockExpireResult();
            if (disabled)
            {
                return result;
            }

            var expired = windowManager.ExpireDue(now);
            result.WindowsExpired = expired.Count;
            foreach (var window in expired)
            {
                logger?.Info("CameraDoorInterlock", "摄像头报警窗口结束。", new LogFields
                {
                    Extra =
                    {
                        ["cameraKey"] = window.CameraKey,
                        ["targetKeys"] = string.Join(",", window.TargetKeys)
                    }
                });

                foreach (var targetKey in window.TargetKeys)
                {
                    var change = targetManager.OnCameraWindowClosed(window.CameraKey, targetKey, now);
                    if (change.ShouldSubmitRestore && change.Activity != null)
                    {
                        SubmitRestore(change.Activity, RestoreRequestId(change.Activity), attempt: 0, now);
                        result.RestoreSubmissions++;
                    }
                    else if (change.Activity != null && change.Activity.IsActive)
                    {
                        logger?.Info("CameraDoorInterlock", "门目标仍有活动摄像头，等待恢复。", new LogFields
                        {
                            Extra =
                            {
                                ["targetKey"] = targetKey,
                                ["remainingCameras"] = change.Activity.ActiveCameraKeys.Count.ToString()
                            }
                        });
                    }
                }
            }

            status.Heartbeat();
            return result;
        }

        /// <summary>处理到期的恢复重试（task05：恢复失败按配置次数和间隔延迟重投，不在 worker 等待）。</summary>
        public InterlockRetryResult ProcessRestoreRetries(DateTime now)
        {
            var result = new InterlockRetryResult();
            if (disabled)
            {
                return result;
            }

            var due = targetManager.GetDueRestoreRetries(now);
            foreach (var activity in due)
            {
                var attempt = activity.PendingRestoreAttempt ?? 0;
                SubmitRestore(activity, RestoreRequestId(activity), attempt, now);
                result.RetriesProcessed++;
            }

            status.Heartbeat();
            return result;
        }

        /// <summary>处理到期的常闭重试（AIOP-02：常闭投递/执行失败必须登记重试，不能只打日志后窗口内永不重试）。</summary>
        public InterlockRetryResult ProcessAlwaysCloseRetries(DateTime now)
        {
            var result = new InterlockRetryResult();
            if (disabled)
            {
                return result;
            }

            var due = targetManager.GetDueAlwaysCloseRetries(now);
            foreach (var activity in due)
            {
                if (!activity.IsActive)
                {
                    // 窗口已结束/活动门目标已移除：无意义重投，清状态。
                    targetManager.RecordAlwaysCloseFailure(activity.TargetKey, attempt: 0, nextRetryAt: null, now);
                    continue;
                }

                var attempt = activity.PendingAlwaysCloseAttempt ?? 0;
                var target = new DoorTarget
                {
                    DoorDeviceId = activity.DoorDeviceId,
                    DoorNo = activity.DoorNo,
                    TargetKey = activity.TargetKey
                };
                SubmitAlwaysClose(target, RestoreRequestId(activity), attempt + 1, now, activity.ActivityGeneration, activity.AlwaysCloseOperationToken);
                result.RetriesProcessed++;
            }

            return result;
        }

        private void SubmitAlwaysClose(DoorTarget target, string requestId, DateTime now, long generation, string operationToken)
        {
            SubmitAlwaysClose(target, requestId, attempt: 0, now, generation, operationToken);
        }

        private void SubmitAlwaysClose(DoorTarget target, string requestId, int attempt, DateTime now, long generation, string operationToken)
        {
            var interlockId = NormalizeInterlockId(requestId);
            var task = taskFactory.CreateAlwaysClose(target.DoorDeviceId, target.DoorNo, target.TargetKey, requestId);
            var submission = dispatcher.Submit(task);
            if (!submission.Accepted)
            {
                var immediate = submission.ImmediateResult;
                var retryable = immediate != null && immediate.Retryable;
                logger?.Error("CameraDoorInterlock", "常闭任务投递被拒绝。", null, new LogFields
                {
                    RequestId = interlockId,
                    DeviceId = target.DoorDeviceId,
                    OperationName = "AlwaysClose",
                    ErrorCode = immediate == null ? string.Empty : immediate.Code,
                    Extra =
                    {
                        ["interlockId"] = interlockId,
                        ["targetKey"] = target.TargetKey,
                        ["doorNo"] = target.DoorNo.ToString(),
                        ["attempt"] = attempt.ToString(),
                        ["code"] = immediate == null ? string.Empty : immediate.Code,
                        ["sdkOperation"] = "ControlGateway",
                        ["sdkErrorCode"] = immediate == null || !immediate.SdkErrorCode.HasValue ? string.Empty : immediate.SdkErrorCode.Value.ToString(),
                        ["retryable"] = retryable.ToString(),
                        ["manualActionRequired"] = "False"
                    }
                });
                RecordAlwaysCloseOutcome(target, generation, operationToken, attempt, success: false, retryable: retryable, now: now);
                return;
            }

            targetManager.MarkAlwaysCloseSubmitted(target.TargetKey, generation, operationToken, now);
            logger?.Info("CameraDoorInterlock", "常闭任务已投递。", new LogFields
            {
                RequestId = interlockId,
                DeviceId = target.DoorDeviceId,
                OperationName = "AlwaysClose",
                Extra =
                {
                    ["interlockId"] = interlockId,
                    ["targetKey"] = target.TargetKey,
                    ["doorNo"] = target.DoorNo.ToString(),
                    ["attempt"] = attempt.ToString(),
                    ["taskId"] = task.TaskId,
                    ["sdkOperation"] = "ControlGateway"
                }
            });

            ObserveAlwaysCloseCompletion(task, target, interlockId, attempt, generation, operationToken, now);
        }

        private void ObserveAlwaysCloseCompletion(Devices.Tasks.DeviceSdkTask task, DoorTarget target, string interlockId, int attempt, long generation, string operationToken, DateTime submittedAt)
        {
            var targetKey = target.TargetKey;
            var deviceId = target.DoorDeviceId;
            var doorNo = target.DoorNo;
            var capturedLogger = logger;
            var capturedTargetManager = targetManager;

            task.Completion.Task.ContinueWith(completed =>
            {
                try
                {
                    var result = completed.Result;
                    if (result != null && result.Success)
                    {
                        capturedTargetManager?.MarkAlwaysCloseSubmitted(targetKey, generation, operationToken, submittedAt);
                        if (capturedLogger != null && capturedLogger.IsSlowOperation(result.DurationMilliseconds))
                        {
                            capturedLogger.Warn("CameraDoorInterlock", "常闭任务执行较慢。", new LogFields
                            {
                                RequestId = interlockId,
                                DeviceId = deviceId,
                                OperationName = "AlwaysClose",
                                ElapsedMs = result.DurationMilliseconds,
                                Extra =
                                {
                                    ["interlockId"] = interlockId,
                                    ["targetKey"] = targetKey,
                                    ["doorNo"] = doorNo.ToString(),
                                    ["sdkOperation"] = "ControlGateway",
                                    ["thresholdMs"] = capturedLogger.SlowOperationThresholdMs.ToString(),
                                    ["manualActionRequired"] = "False"
                                }
                            });
                        }

                        capturedLogger?.Info("CameraDoorInterlock", "常闭任务成功。", new LogFields
                        {
                            RequestId = interlockId,
                            DeviceId = deviceId,
                            OperationName = "AlwaysClose",
                            ElapsedMs = result.DurationMilliseconds,
                            Extra =
                            {
                                ["interlockId"] = interlockId,
                                ["targetKey"] = targetKey,
                                ["doorNo"] = doorNo.ToString(),
                                ["durationMs"] = result.DurationMilliseconds.ToString(),
                                ["sdkOperation"] = "ControlGateway",
                                ["retryable"] = result.Retryable.ToString(),
                                ["manualActionRequired"] = "False"
                            }
                        });
                    }
                    else
                    {
                        var retryable = result != null && result.Retryable;
                        capturedLogger?.Error("CameraDoorInterlock", "常闭任务失败。", null, new LogFields
                        {
                            RequestId = interlockId,
                            DeviceId = deviceId,
                            OperationName = "AlwaysClose",
                            ErrorCode = result == null ? "UNKNOWN" : result.Code,
                            ElapsedMs = result == null ? (long?)null : result.DurationMilliseconds,
                            Extra =
                            {
                                ["interlockId"] = interlockId,
                                ["targetKey"] = targetKey,
                                ["doorNo"] = doorNo.ToString(),
                                ["attempt"] = attempt.ToString(),
                                ["code"] = result == null ? string.Empty : result.Code,
                                ["sdkOperation"] = "ControlGateway",
                                ["sdkErrorCode"] = result == null || !result.SdkErrorCode.HasValue ? string.Empty : result.SdkErrorCode.Value.ToString(),
                                ["retryable"] = retryable.ToString(),
                                ["manualActionRequired"] = "False"
                            }
                        });

                        // 常闭执行失败必须登记重试，不能只打日志（AIOP-02）。
                        capturedTargetManager?.RecordAlwaysCloseFailure(targetKey, generation, operationToken, attempt, retryable ? (DateTime?)submittedAt.AddMilliseconds(CalculateAlwaysCloseRetryDelayMilliseconds(attempt + 1)) : (DateTime?)null, submittedAt);
                        if (retryable)
                        {
                            capturedLogger?.Info("CameraDoorInterlock", "常闭失败将持续重试直至成功。", new LogFields
                            {
                                RequestId = interlockId,
                                DeviceId = deviceId,
                                OperationName = "AlwaysClose",
                                Extra =
                                {
                                    ["interlockId"] = interlockId,
                                    ["targetKey"] = targetKey,
                                    ["doorNo"] = doorNo.ToString(),
                                    ["nextAttempt"] = (attempt + 1).ToString(),
                                    ["sdkOperation"] = "ControlGateway",
                                    ["retryable"] = "True",
                                    ["manualActionRequired"] = "False"
                                }
                            });
                        }
                    }
                }
                catch (Exception)
                {
                    // 常闭观察回调不得影响主流程。
                }
            }, TaskScheduler.Default);
        }

        private void SubmitRestore(DoorTargetActivity activity, string requestId, int attempt, DateTime now)
        {
            var generation = activity.ActivityGeneration;
            var operationToken = activity.RestoreOperationToken;
            var interlockId = NormalizeInterlockId(string.IsNullOrWhiteSpace(activity.InterlockId) ? requestId : activity.InterlockId);
            if (!targetManager.RecordRestoreSubmitted(activity.TargetKey, generation, operationToken, attempt, now))
            {
                return;
            }

            var task = taskFactory.CreateRestore(activity.DoorDeviceId, activity.DoorNo, activity.TargetKey, requestId, attempt);
            var submission = dispatcher.Submit(task);
            if (!submission.Accepted)
            {
                // 投递被拒/同步终态：立即按 immediate result 登记结果，不阻塞扫描循环（AIOP-05）。
                var immediate = submission.ImmediateResult;
                var success = immediate != null && immediate.Success;
                var retryable = immediate != null && immediate.Retryable;
                logger?.Warn("CameraDoorInterlock", "恢复任务投递被拒绝。", new LogFields
                {
                    RequestId = interlockId,
                    DeviceId = activity.DoorDeviceId,
                    OperationName = "RestoreDoor",
                    ErrorCode = immediate == null ? "UNKNOWN" : immediate.Code,
                    Extra =
                    {
                        ["interlockId"] = interlockId,
                        ["targetKey"] = activity.TargetKey,
                        ["doorNo"] = activity.DoorNo.ToString(),
                        ["attempt"] = attempt.ToString(),
                        ["code"] = immediate == null ? string.Empty : immediate.Code,
                        ["sdkOperation"] = "ControlGateway",
                        ["sdkErrorCode"] = immediate == null || !immediate.SdkErrorCode.HasValue ? string.Empty : immediate.SdkErrorCode.Value.ToString(),
                        ["retryable"] = retryable.ToString(),
                        ["manualActionRequired"] = (!retryable).ToString()
                    }
                });
                RecordRestoreOutcome(activity, attempt, success, retryable, now);
                return;
            }

            logger?.Info("CameraDoorInterlock", "恢复任务已投递。", new LogFields
            {
                RequestId = interlockId,
                DeviceId = activity.DoorDeviceId,
                OperationName = "RestoreDoor",
                Extra =
                {
                    ["interlockId"] = interlockId,
                    ["targetKey"] = activity.TargetKey,
                    ["doorNo"] = activity.DoorNo.ToString(),
                    ["attempt"] = attempt.ToString(),
                    ["taskId"] = task.TaskId,
                    ["sdkOperation"] = "ControlGateway",
                    ["manualActionRequired"] = "False"
                }
            });

            ObserveRestoreCompletion(task, activity, interlockId, attempt, generation, operationToken, now);
        }

        private void ObserveRestoreCompletion(Devices.Tasks.DeviceSdkTask task, DoorTargetActivity activity, string interlockId, int attempt, long generation, string operationToken, DateTime submittedAt)
        {
            var targetKey = activity.TargetKey;
            var deviceId = activity.DoorDeviceId;
            var doorNo = activity.DoorNo;
            var capturedLogger = logger;
            task.Completion.Task.ContinueWith(completed =>
            {
                try
                {
                    var result = completed.Result;
                    var success = result != null && result.Success;
                    var retryable = result != null && result.Retryable;
                    if (success)
                    {
                        if (capturedLogger != null && capturedLogger.IsSlowOperation(result.DurationMilliseconds))
                        {
                            capturedLogger.Warn("CameraDoorInterlock", "恢复任务执行较慢。", new LogFields
                            {
                                RequestId = interlockId,
                                DeviceId = deviceId,
                                OperationName = "RestoreDoor",
                                ElapsedMs = result.DurationMilliseconds,
                                Extra =
                                {
                                    ["interlockId"] = interlockId,
                                    ["targetKey"] = targetKey,
                                    ["doorNo"] = doorNo.ToString(),
                                    ["attempt"] = attempt.ToString(),
                                    ["sdkOperation"] = "ControlGateway",
                                    ["thresholdMs"] = capturedLogger.SlowOperationThresholdMs.ToString(),
                                    ["manualActionRequired"] = "False"
                                }
                            });
                        }

                        capturedLogger?.Info("CameraDoorInterlock", "恢复任务成功。", new LogFields
                        {
                            RequestId = interlockId,
                            DeviceId = deviceId,
                            OperationName = "RestoreDoor",
                            ElapsedMs = result.DurationMilliseconds,
                            Extra =
                            {
                                ["interlockId"] = interlockId,
                                ["targetKey"] = targetKey,
                                ["doorNo"] = doorNo.ToString(),
                                ["attempt"] = attempt.ToString(),
                                ["durationMs"] = result.DurationMilliseconds.ToString(),
                                ["sdkOperation"] = "ControlGateway",
                                ["manualActionRequired"] = "False"
                            }
                        });
                    }
                    else
                    {
                        capturedLogger?.Warn("CameraDoorInterlock", "恢复任务失败。", new LogFields
                        {
                            RequestId = interlockId,
                            DeviceId = deviceId,
                            OperationName = "RestoreDoor",
                            ErrorCode = result == null ? "UNKNOWN" : result.Code,
                            ElapsedMs = result == null ? (long?)null : result.DurationMilliseconds,
                            Extra =
                            {
                                ["interlockId"] = interlockId,
                                ["targetKey"] = targetKey,
                                ["doorNo"] = doorNo.ToString(),
                                ["attempt"] = attempt.ToString(),
                                ["code"] = result == null ? string.Empty : result.Code,
                                ["sdkOperation"] = "ControlGateway",
                                ["sdkErrorCode"] = result == null || !result.SdkErrorCode.HasValue ? string.Empty : result.SdkErrorCode.Value.ToString(),
                                ["retryable"] = retryable.ToString(),
                                ["manualActionRequired"] = (!retryable).ToString()
                            }
                        });
                    }

                    RecordRestoreOutcomeWith(targetKey, deviceId, doorNo, interlockId, attempt, generation, operationToken, success, retryable, submittedAt);
                }
                catch (Exception)
                {
                    // 恢复观察回调不得影响主流程。
                }
            }, TaskScheduler.Default);
        }

        private void RecordRestoreOutcome(DoorTargetActivity activity, int attempt, bool success, bool retryable, DateTime now)
        {
            var interlockId = NormalizeInterlockId(activity.InterlockId);
            RecordRestoreOutcomeWith(activity.TargetKey, activity.DoorDeviceId, activity.DoorNo, interlockId, attempt, activity.ActivityGeneration, activity.RestoreOperationToken, success, retryable, now);
        }

        private void RecordRestoreOutcomeWith(string targetKey, int deviceId, int doorNo, string interlockId, int attempt, long generation, string operationToken, bool success, bool retryable, DateTime now)
        {
            if (success)
            {
                targetManager.MarkRestoreSucceeded(targetKey, generation, operationToken, now);
                return;
            }

            if (retryable)
            {
                var nextAttempt = attempt + 1;
                var nextRetryAt = now.AddMilliseconds(CalculateRestoreRetryDelayMilliseconds(nextAttempt));
                targetManager.RecordRestoreFailure(targetKey, generation, operationToken, nextAttempt, nextRetryAt, now);
                logger?.Info("CameraDoorInterlock", "恢复失败将持续重试直至成功。", new LogFields
                {
                    RequestId = interlockId,
                    DeviceId = deviceId,
                    OperationName = "RestoreDoor",
                    Extra =
                    {
                        ["interlockId"] = interlockId,
                        ["targetKey"] = targetKey,
                        ["doorNo"] = doorNo.ToString(),
                        ["nextAttempt"] = nextAttempt.ToString(),
                        ["nextRetryAt"] = nextRetryAt.ToString("O"),
                        ["sdkOperation"] = "ControlGateway",
                        ["retryable"] = "True",
                        ["manualActionRequired"] = "False"
                    }
                });
            }
            else
            {
                targetManager.RecordRestoreFailure(targetKey, generation, operationToken, attempt, null, now);
                logger?.Error("CameraDoorInterlock", "恢复遇到不可重试错误，需人工确认门禁恢复。", null, new LogFields
                {
                    RequestId = interlockId,
                    DeviceId = deviceId,
                    OperationName = "RestoreDoor",
                    Extra =
                    {
                        ["interlockId"] = interlockId,
                        ["targetKey"] = targetKey,
                        ["doorNo"] = doorNo.ToString(),
                        ["attempt"] = attempt.ToString(),
                        ["sdkOperation"] = "ControlGateway",
                        ["retryable"] = retryable.ToString(),
                        ["manualActionRequired"] = "True"
                    }
                });
            }
        }

        private int CalculateRestoreRetryDelayMilliseconds(int nextAttempt)
        {
            var initial = Math.Max(100, options.RestoreRetryIntervalMs);
            var exponent = Math.Max(0, Math.Min(6, nextAttempt - 1));
            var delay = (long)initial * (1 << exponent);
            return (int)Math.Min(delay, 60000);
        }

        /// <summary>
        /// 常闭失败重试登记（AIOP-02）。与 Restore 失败登记风格一致：可重试错误持续重试无最大次数限制，
        /// 不可重试错误转终态（nextRetryAt = null）。注意：常闭失败终态只是停止本周期内重投，
        /// 窗口期内一旦窗口续期重新进入活动（OnCameraWindowOpened），状态机仍会清掉挂起重试后重新发起首次常闭。
        /// </summary>
        private void RecordAlwaysCloseOutcome(DoorTarget target, long generation, string operationToken, int attempt, bool success, bool retryable, DateTime now)
        {
            if (success)
            {
                targetManager.MarkAlwaysCloseSubmitted(target.TargetKey, generation, operationToken, now);
                return;
            }

            if (retryable)
            {
                var nextAttempt = attempt + 1;
                var nextRetryAt = now.AddMilliseconds(CalculateAlwaysCloseRetryDelayMilliseconds(nextAttempt));
                targetManager.RecordAlwaysCloseFailure(target.TargetKey, generation, operationToken, nextAttempt, nextRetryAt, now);
                logger?.Info("CameraDoorInterlock", "常闭失败将持续重试直至成功。", new LogFields
                {
                    DeviceId = target.DoorDeviceId,
                    OperationName = "AlwaysClose",
                    Extra =
                    {
                        ["targetKey"] = target.TargetKey,
                        ["doorNo"] = target.DoorNo.ToString(),
                        ["nextAttempt"] = nextAttempt.ToString(),
                        ["nextRetryAt"] = nextRetryAt.ToString("O"),
                        ["sdkOperation"] = "ControlGateway",
                        ["retryable"] = "True",
                        ["manualActionRequired"] = "False"
                    }
                });
            }
            else
            {
                targetManager.RecordAlwaysCloseFailure(target.TargetKey, generation, operationToken, attempt, nextRetryAt: null, now);
            }
        }

        private int CalculateAlwaysCloseRetryDelayMilliseconds(int nextAttempt)
        {
            var initial = Math.Max(1000, options.AlwaysCloseRetryIntervalMs);
            var exponent = Math.Max(0, Math.Min(6, nextAttempt - 1));
            var delay = (long)initial * (1 << exponent);
            return (int)Math.Min(delay, 60000);
        }

        /// <summary>服务停止时对活动门目标 best-effort 恢复（task05）。</summary>
        public RestoreActiveTargetsBestEffortResult RestoreActiveTargetsBestEffort(TimeSpan timeout)
        {
            var result = new RestoreActiveTargetsBestEffortResult();
            if (disabled)
            {
                return result;
            }

            var outstanding = targetManager.GetOutstandingTargets();
            result.Total = outstanding.Count;
            if (outstanding.Count == 0)
            {
                logger?.Info("CameraDoorInterlock", "停止恢复：无活动门目标。");
                return result;
            }

            // 每扇门至少预留独立的时间片（AIOP-04）：避免第一扇门耗尽全局超时而后续门被静默跳过。
            // perDoorBudget = max(200ms, min(timeout/N, 2000ms))，与剩余全局额度取较小者。
            var perDoorBudget = ComputePerDoorRestoreBudget(timeout, outstanding.Count);
            var deadlineUtc = DateTime.UtcNow.Add(timeout <= TimeSpan.Zero ? TimeSpan.Zero : timeout);
            foreach (var activity in outstanding)
            {
                var remaining = deadlineUtc - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    result.Unfinished++;
                    continue;
                }

                // 单门至少 best-effort 提交一次：用 min(remaining, perDoorBudget)，让后续门仍有时间。
                var doorSlice = remaining < perDoorBudget ? remaining : perDoorBudget;

                try
                {
                    var task = taskFactory.CreateRestore(activity.DoorDeviceId, activity.DoorNo, activity.TargetKey, "restore-stop-" + Guid.NewGuid().ToString("N"), 0);
                    var taskResult = SubmitBestEffortRestore(task, doorSlice);
                    if (IsBestEffortRestoreUnfinished(taskResult))
                    {
                        result.Unfinished++;
                    }
                    else if (taskResult != null && taskResult.Success)
                    {
                        result.Succeeded++;
                        targetManager.MarkRestoreSucceeded(
                            activity.TargetKey,
                            activity.ActivityGeneration,
                            activity.RestoreOperationToken,
                            nowForBestEffort());
                    }
                    else
                    {
                        result.Failed++;
                        logger?.Error("CameraDoorInterlock", "停止恢复失败，需人工处理。", null, new LogFields
                        {
                            Extra =
                            {
                                ["targetKey"] = activity.TargetKey,
                                ["deviceId"] = activity.DoorDeviceId.ToString(),
                                ["doorNo"] = activity.DoorNo.ToString(),
                                ["code"] = taskResult == null ? string.Empty : taskResult.Code
                            }
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    result.Unfinished++;
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    logger?.Error("CameraDoorInterlock", "停止恢复异常，需人工处理。", ex, new LogFields
                    {
                        Extra =
                        {
                            ["targetKey"] = activity.TargetKey,
                            ["deviceId"] = activity.DoorDeviceId.ToString(),
                            ["doorNo"] = activity.DoorNo.ToString()
                        }
                    });
                }
            }

            logger?.Info("CameraDoorInterlock", "停止恢复完成。", new LogFields
            {
                Extra =
                {
                    ["total"] = result.Total.ToString(),
                    ["succeeded"] = result.Succeeded.ToString(),
                    ["failed"] = result.Failed.ToString(),
                    ["unfinished"] = result.Unfinished.ToString()
                }
            });

            return result;
        }

        private Devices.Tasks.DeviceTaskResult SubmitBestEffortRestore(Devices.Tasks.DeviceSdkTask task, TimeSpan remaining)
        {
            var waitMilliseconds = ToPositiveMilliseconds(remaining);
            if (waitMilliseconds <= 0)
            {
                return Devices.Tasks.DeviceTaskResult.Timeout(task, "Stop restore wait timed out before task submission.");
            }

            task.TimeoutMilliseconds = waitMilliseconds;
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.CancelAfter(waitMilliseconds);
                return dispatcher.SubmitAndWaitAsync(task, cancellation.Token).GetAwaiter().GetResult();
            }
        }

        private static bool IsBestEffortRestoreUnfinished(Devices.Tasks.DeviceTaskResult result)
        {
            return result == null ||
                string.Equals(result.Code, "CANCELLED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(result.Code, "TIMEOUT", StringComparison.OrdinalIgnoreCase);
        }

        private static int ToPositiveMilliseconds(TimeSpan remaining)
        {
            if (remaining <= TimeSpan.Zero)
            {
                return 0;
            }

            if (remaining.TotalMilliseconds >= int.MaxValue)
            {
                return int.MaxValue;
            }

            return Math.Max(1, (int)Math.Ceiling(remaining.TotalMilliseconds));
        }

        /// <summary>
        /// 计算停止恢复时单门的独立时间预算（AIOP-04）。
        /// perDoorBudget = max(200ms, min(timeout/N, 2000ms))，保证每扇门至少有 200ms 的 best-effort 提交窗口，
        /// 单门不得超过 2 秒以防把后续门饿死。当 timeout/N 小于 200ms 时也至少给 200ms。
        /// </summary>
        private static TimeSpan ComputePerDoorRestoreBudget(TimeSpan timeout, int doorCount)
        {
            if (doorCount <= 0)
            {
                return TimeSpan.Zero;
            }

            if (timeout <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            var slice = TimeSpan.FromTicks(timeout.Ticks / doorCount);
            var cap = TimeSpan.FromMilliseconds(2000);
            var floor = TimeSpan.FromMilliseconds(200);
            if (slice > cap)
            {
                return cap;
            }

            if (slice < floor)
            {
                return floor;
            }

            return slice;
        }

        private DateTime nowForBestEffort()
        {
            return clock();
        }

        private int SafeQueueCapacity()
        {
            try
            {
                return queue.BoundedCapacity;
            }
            catch (ObjectDisposedException)
            {
                return 0;
            }
        }

        private int SafeQueueDepth()
        {
            try
            {
                return queue.Count;
            }
            catch (ObjectDisposedException)
            {
                return 0;
            }
        }

        private static string RestoreRequestId(DoorTargetActivity activity)
        {
            if (activity != null && !string.IsNullOrWhiteSpace(activity.InterlockId))
            {
                return activity.InterlockId;
            }

            return "restore-" + Guid.NewGuid().ToString("N");
        }

        private static string NormalizeInterlockId(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value.Trim();
        }

        private async Task RunLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var now = clock();
                    ProcessEvents(now);
                    ExpireWindows(now);
                    ProcessRestoreRetries(now);
                    ProcessAlwaysCloseRetries(now);
                    await Task.Delay(scanIntervalMs, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                status.MarkStopped();
            }
            catch (Exception ex)
            {
                status.MarkFailed(ex);
                logger?.Error("CameraDoorInterlock", "阶段 9 后台扫描循环异常。", ex);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
            }

            if (stopSource != null)
            {
                try
                {
                    stopSource.Cancel();
                }
                catch (Exception ex)
                {
                    logger?.Warn("CameraDoorInterlockDispose", "停止阶段 9 后台扫描取消令牌失败，已忽略并继续清理。", new LogFields
                    {
                        OperationName = "DisposeCancel",
                        Exception = ex.GetType().Name + ": " + ex.Message,
                        ErrorCode = ex.GetType().Name
                    });
                }
            }

            if (loopTask != null)
            {
                try
                {
                    Task.WhenAny(loopTask, Task.Delay(TimeSpan.FromSeconds(2))).GetAwaiter().GetResult();
                    if (loopTask.IsFaulted && loopTask.Exception != null)
                    {
                        logger?.Warn("CameraDoorInterlockDispose", "阶段 9 后台扫描循环已异常结束。", new LogFields
                        {
                            OperationName = "DisposeWaitLoop",
                            Exception = loopTask.Exception.GetBaseException().GetType().Name + ": " + loopTask.Exception.GetBaseException().Message,
                            ErrorCode = loopTask.Exception.GetBaseException().GetType().Name
                        });
                    }
                    else if (loopTask.IsCompleted && !string.IsNullOrWhiteSpace(status.LastError))
                    {
                        logger?.Warn("CameraDoorInterlockDispose", "阶段 9 后台扫描循环已带错误状态结束。", new LogFields
                        {
                            OperationName = "DisposeWaitLoop",
                            Exception = status.LastError,
                            ErrorCode = "LoopFailed"
                        });
                    }
                }
                catch (Exception ex)
                {
                    logger?.Warn("CameraDoorInterlockDispose", "等待阶段 9 后台扫描循环停止失败，已忽略并继续清理。", new LogFields
                    {
                        OperationName = "DisposeWaitLoop",
                        Exception = ex.GetType().Name + ": " + ex.Message,
                        ErrorCode = ex.GetType().Name
                    });
                }
            }

            queue.CompleteAdding();
            queue.Dispose();
            stopSource?.Dispose();
        }
    }

    public sealed class InterlockProcessResult
    {
        public int EventsProcessed { get; set; }

        public int WindowsTouched { get; set; }

        public int AlwaysCloseSubmissions { get; set; }
    }

    public sealed class InterlockExpireResult
    {
        public int WindowsExpired { get; set; }

        public int RestoreSubmissions { get; set; }
    }

    public sealed class InterlockRetryResult
    {
        public int RetriesProcessed { get; set; }
    }

    public sealed class RestoreActiveTargetsBestEffortResult
    {
        public int Total { get; set; }

        public int Succeeded { get; set; }

        public int Failed { get; set; }

        public int Unfinished { get; set; }
    }
}
