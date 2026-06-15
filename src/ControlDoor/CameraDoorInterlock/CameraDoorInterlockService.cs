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
    /// 常闭 fire-and-track，恢复 wait-and-retry；服务停止时对活动门目标 best-effort 恢复。
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
                return AiopAlarmEnqueueResult.Rejected("INVALID_ARGUMENT", "alarm event is required", 0, queue.BoundedCapacity);
            }

            if (disabled)
            {
                return AiopAlarmEnqueueResult.Rejected("DISABLED", "camera door interlock is disabled", queue.Count, queue.BoundedCapacity);
            }

            if (disposed)
            {
                return AiopAlarmEnqueueResult.Rejected("DISPOSED", "camera door interlock service is disposed", queue.Count, queue.BoundedCapacity);
            }

            if (queue.TryAdd(alarmEvent, 0))
            {
                return AiopAlarmEnqueueResult.AcceptedResult(queue.Count, queue.BoundedCapacity);
            }

            logger?.Warn("CameraDoorInterlock", "AIOP 报警队列已满，丢弃事件。", new LogFields
            {
                Extra =
                {
                    ["cameraKey"] = alarmEvent.CameraKey ?? string.Empty,
                    ["cameraIp"] = alarmEvent.CameraIp ?? string.Empty,
                    ["queueDepth"] = queue.Count.ToString()
                }
            });
            return AiopAlarmEnqueueResult.Rejected("QUEUE_FULL", "AIOP alarm queue is full", queue.Count, queue.BoundedCapacity);
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
            var payload = parser.Parse(evt.RawPayload, evt.CameraDeviceId, evt.CameraIp);
            if (!payload.ParseSucceeded)
            {
                logger?.Warn("CameraDoorInterlock", "AIOP 载荷解析失败，仍按命中摄像头触发联动。", new LogFields
                {
                    Extra =
                    {
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
                    Extra = { ["cameraKey"] = evt.CameraKey }
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
                    Extra =
                    {
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
                    Extra =
                    {
                        ["cameraKey"] = evt.CameraKey,
                        ["endsAt"] = windowResult.Window.EndsAt.ToString("O"),
                        ["triggeredCount"] = windowResult.Window.TriggeredCount.ToString()
                    }
                });
                return;
            }

            foreach (var target in targets)
            {
                var change = targetManager.OnCameraWindowOpened(evt.CameraKey, target, now);
                if (change.ShouldSubmitAlwaysClose)
                {
                    SubmitAlwaysClose(target, evt.RequestId, now);
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
                        SubmitRestore(change.Activity, "restore-" + Guid.NewGuid().ToString("N"), attempt: 0, now);
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
                SubmitRestore(activity, "restore-retry-" + Guid.NewGuid().ToString("N"), attempt, now);
                result.RetriesProcessed++;
            }

            status.Heartbeat();
            return result;
        }

        private void SubmitAlwaysClose(DoorTarget target, string requestId, DateTime now)
        {
            var task = taskFactory.CreateAlwaysClose(target.DoorDeviceId, target.DoorNo, target.TargetKey, requestId);
            var submission = dispatcher.Submit(task);
            if (!submission.Accepted)
            {
                logger?.Error("CameraDoorInterlock", "常闭任务投递被拒绝。", null, new LogFields
                {
                    Extra =
                    {
                        ["targetKey"] = target.TargetKey,
                        ["deviceId"] = target.DoorDeviceId.ToString(),
                        ["doorNo"] = target.DoorNo.ToString(),
                        ["code"] = submission.ImmediateResult == null ? string.Empty : submission.ImmediateResult.Code
                    }
                });
                return;
            }

            targetManager.MarkAlwaysCloseSubmitted(target.TargetKey, now);
            logger?.Info("CameraDoorInterlock", "常闭任务已投递。", new LogFields
            {
                Extra =
                {
                    ["targetKey"] = target.TargetKey,
                    ["deviceId"] = target.DoorDeviceId.ToString(),
                    ["doorNo"] = target.DoorNo.ToString(),
                    ["taskId"] = task.TaskId
                }
            });

            ObserveAlwaysCloseCompletion(task, target);
        }

        private void ObserveAlwaysCloseCompletion(Devices.Tasks.DeviceSdkTask task, DoorTarget target)
        {
            var targetKey = target.TargetKey;
            var deviceId = target.DoorDeviceId;
            var doorNo = target.DoorNo;
            var capturedLogger = logger;

            task.Completion.Task.ContinueWith(completed =>
            {
                try
                {
                    var result = completed.Result;
                    if (result != null && result.Success)
                    {
                        capturedLogger?.Info("CameraDoorInterlock", "常闭任务成功。", new LogFields
                        {
                            Extra =
                            {
                                ["targetKey"] = targetKey,
                                ["deviceId"] = deviceId.ToString(),
                                ["doorNo"] = doorNo.ToString(),
                                ["durationMs"] = result.DurationMilliseconds.ToString()
                            }
                        });
                    }
                    else
                    {
                        capturedLogger?.Error("CameraDoorInterlock", "常闭任务失败。", null, new LogFields
                        {
                            Extra =
                            {
                                ["targetKey"] = targetKey,
                                ["deviceId"] = deviceId.ToString(),
                                ["doorNo"] = doorNo.ToString(),
                                ["code"] = result == null ? string.Empty : result.Code,
                                ["sdkErrorCode"] = result == null || !result.SdkErrorCode.HasValue ? string.Empty : result.SdkErrorCode.Value.ToString()
                            }
                        });
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
            var task = taskFactory.CreateRestore(activity.DoorDeviceId, activity.DoorNo, activity.TargetKey, requestId, attempt);
            Devices.Tasks.DeviceTaskResult result;
            try
            {
                result = dispatcher.SubmitAndWaitAsync(task, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger?.Error("CameraDoorInterlock", "恢复任务投递异常。", ex, new LogFields
                {
                    Extra = { ["targetKey"] = activity.TargetKey, ["attempt"] = attempt.ToString() }
                });
                RecordRestoreOutcome(activity, attempt, success: false, retryable: true, now);
                return;
            }

            if (result != null && result.Success)
            {
                targetManager.MarkRestoreSucceeded(activity.TargetKey, now);
                logger?.Info("CameraDoorInterlock", "恢复任务成功。", new LogFields
                {
                    Extra =
                    {
                        ["targetKey"] = activity.TargetKey,
                        ["attempt"] = attempt.ToString(),
                        ["durationMs"] = result.DurationMilliseconds.ToString()
                    }
                });
                return;
            }

            var code = result == null ? "UNKNOWN" : result.Code;
            var retryable = result != null && result.Retryable;
            logger?.Warn("CameraDoorInterlock", "恢复任务失败。", new LogFields
            {
                Extra =
                {
                    ["targetKey"] = activity.TargetKey,
                    ["attempt"] = attempt.ToString(),
                    ["code"] = code,
                    ["sdkErrorCode"] = result == null || !result.SdkErrorCode.HasValue ? string.Empty : result.SdkErrorCode.Value.ToString(),
                    ["retryable"] = retryable.ToString()
                }
            });

            RecordRestoreOutcome(activity, attempt, success: false, retryable: retryable, now);
        }

        private void RecordRestoreOutcome(DoorTargetActivity activity, int attempt, bool success, bool retryable, DateTime now)
        {
            if (success)
            {
                targetManager.MarkRestoreSucceeded(activity.TargetKey, now);
                return;
            }

            // 可重试错误（设备离线、网络抖动、SDK 超时等）持续重试，不设最大次数。
            // 设备恢复常闭是实时安全动作，门必须尽快恢复到普通受控状态。
            if (retryable)
            {
                var nextAttempt = attempt + 1;
                var nextRetryAt = now.AddMilliseconds(Math.Max(100, options.RestoreRetryIntervalMs));
                targetManager.RecordRestoreFailure(activity.TargetKey, nextAttempt, nextRetryAt, now);
                logger?.Info("CameraDoorInterlock", "恢复失败将持续重试直至成功。", new LogFields
                {
                    Extra =
                    {
                        ["targetKey"] = activity.TargetKey,
                        ["nextAttempt"] = nextAttempt.ToString(),
                        ["nextRetryAt"] = nextRetryAt.ToString("O")
                    }
                });
            }
            else
            {
                // 不可重试错误（如非法门号等配置类错误）重试无意义，转终态需人工确认。
                targetManager.RecordRestoreFailure(activity.TargetKey, attempt, null, now);
                logger?.Error("CameraDoorInterlock", "恢复遇到不可重试错误，需人工确认门禁恢复。", null, new LogFields
                {
                    Extra =
                    {
                        ["targetKey"] = activity.TargetKey,
                        ["deviceId"] = activity.DoorDeviceId.ToString(),
                        ["doorNo"] = activity.DoorNo.ToString(),
                        ["attempt"] = attempt.ToString(),
                        ["retryable"] = retryable.ToString()
                    }
                });
            }
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

            var deadline = nowForBestEffort().Add(timeout);
            foreach (var activity in outstanding)
            {
                if (nowForBestEffort() >= deadline)
                {
                    result.Unfinished++;
                    continue;
                }

                try
                {
                    var task = taskFactory.CreateRestore(activity.DoorDeviceId, activity.DoorNo, activity.TargetKey, "restore-stop-" + Guid.NewGuid().ToString("N"), 0);
                    var taskResult = dispatcher.SubmitAndWaitAsync(task, CancellationToken.None).GetAwaiter().GetResult();
                    if (taskResult != null && taskResult.Success)
                    {
                        result.Succeeded++;
                        targetManager.MarkRestoreSucceeded(activity.TargetKey, nowForBestEffort());
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

        private DateTime nowForBestEffort()
        {
            return clock();
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
                try { stopSource.Cancel(); } catch { }
            }

            if (loopTask != null)
            {
                try { Task.WhenAny(loopTask, Task.Delay(TimeSpan.FromSeconds(2))).GetAwaiter().GetResult(); } catch { }
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
