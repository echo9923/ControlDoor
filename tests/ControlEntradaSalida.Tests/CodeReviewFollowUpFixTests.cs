using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.CameraDoorInterlock;
using ControlDoor.Configuration;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Tasks;
using ControlDoor.FaceEvents;
using ControlDoor.Hikvision;
using ControlDoor.Permissions;
using ControlDoor.Runtime;

namespace ControlEntradaSalida.Tests
{
    // 验收复核 F01-F06 回归：到期重试不被持续新流量饿死、重试道容量背压、
    // 删除补偿走统一重连状态机、恢复状态按代次条件更新、补偿路径图片上限一致、紧凑命名幂等。
    public static class CodeReviewFollowUpFixTests
    {
        [TestCase]
        public static void F01_ContinuousHealthyFlow_DoesNotStarveRetry()
        {
            var runDirectory = TestWorkspace.Create();
            var retryDirectory = Path.Combine(runDirectory, "acs-retry");
            var processor = new FollowUpProcessor();
            var service = new FaceEventIngestionService(
                new FaceEventLoggingOptions { QueueCapacity = 500, BatchSize = 1, FlushIntervalMs = 50 },
                processor, null, retryDirectory);
            var context = new BackgroundTaskContext("f01-starve", CancellationToken.None, null);
            service.StartAsync(context).GetAwaiter().GetResult();
            try
            {
                var poison = NewRawEvent("f01-poison");
                processor.FailOnceRequestIds.Add(poison.RequestId);
                Assert.True(service.TryEnqueue(poison).Accepted);

                var stopFeeding = new ManualResetEventSlim(false);
                var feeder = Task.Run(() =>
                {
                    var index = 0;
                    while (!stopFeeding.IsSet)
                    {
                        service.TryEnqueue(NewRawEvent("f01-healthy-" + index));
                        index++;
                        Thread.Sleep(5);
                    }
                });

                // 供给期内（1.2 秒）失败事件必须完成第二次尝试：到期重试与新事件轮流使用批次预算。
                var secondAttemptWithinFlow = false;
                var deadline = DateTime.UtcNow.AddMilliseconds(1200);
                while (DateTime.UtcNow < deadline)
                {
                    if (processor.PoisonAttempts >= 2)
                    {
                        secondAttemptWithinFlow = true;
                        break;
                    }

                    Thread.Sleep(10);
                }

                stopFeeding.Set();
                feeder.GetAwaiter().GetResult();

                Assert.True(secondAttemptWithinFlow, "持续满批新流量下到期重试被饿死。");
                SpinUntil(() => processor.SucceededRequestIds.Contains(poison.RequestId) && service.Count == 0, "失败事件未在流量结束后恢复完成。");
            }
            finally
            {
                service.StopAsync(context).GetAwaiter().GetResult();
                service.Dispose();
            }
        }

        [TestCase]
        public static void F01_LaneFull_AppliesQueueFullBackpressure()
        {
            var runDirectory = TestWorkspace.Create();
            var retryDirectory = Path.Combine(runDirectory, "acs-retry");
            var processor = new FollowUpProcessor { FailAllRetryable = true };
            var service = new FaceEventIngestionService(
                new FaceEventLoggingOptions { QueueCapacity = 3, BatchSize = 1, FlushIntervalMs = 20, OverflowQueueCapacity = 1 },
                processor, null, retryDirectory);
            service.ItemRetryLimit = 100;
            var context = new BackgroundTaskContext("f01-backpressure", CancellationToken.None, null);
            service.StartAsync(context).GetAwaiter().GetResult();
            try
            {
                // 逐条入队并留出消费间隙：前三条经活动批次进入重试道，后三条因重试道满而滞留队列。
                for (var i = 0; i < 6; i++)
                {
                    Assert.True(service.TryEnqueue(NewRawEvent("f01b-" + i)).Accepted);
                    Thread.Sleep(60);
                }

                // 重试道达容量（3）后停止取件，队列积压到容量并对外形成背压（F01 核心）。
                // 复核 R1 后队列满不再直接丢弃：事件经溢出通道持久化，恢复后一并补齐
                //（OVERFLOW_FULL 最后边界在 CodeReviewR01EventOverflowPersistenceTests 中确定性覆盖）。
                SpinUntil(() => service.Count == 3, "重试道满后队列未积压到容量。");
                var overflowed = service.TryEnqueue(NewRawEvent("f01b-overflow"));
                Assert.True(overflowed.Accepted);
                Assert.Equal("QUEUE_FULL_PERSISTED", overflowed.Code);

                // 故障恢复后自动排空，队列与重试道全部清空。
                processor.FailAllRetryable = false;
                SpinUntil(() => service.Count == 0, "恢复后队列未排空。");
                SpinUntil(() => !Directory.EnumerateFiles(retryDirectory, "*.json").Any(), "恢复后重试文件未清理。");
            }
            finally
            {
                service.StopAsync(context).GetAwaiter().GetResult();
                service.Dispose();
            }
        }

        [TestCase]
        public static void F02_DeleteCompensation_LoginExpiredInQueue_EventuallyRecovers()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.Options.LoginTimeoutMs = 80;
                fixture.Options.ReconnectBaseDelayMs = 10;
                fixture.Options.ReconnectMaxDelayMs = 20;
                fixture.Options.MaxReconnectAttempts = 0;
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "f02-login");
                SpinUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.Status == DeviceConnectionStatus.Online, "初始登录未完成。");
                var loginCount = fixture.Gateway.Calls.Count(call => call.MethodName == "LoginAsync");

                fixture.Repository.FailWrites = true;
                var failed = fixture.Lifecycle.DeleteDevice(1, disconnectFirst: true, requestId: "f02-delete");
                Assert.False(failed.Success);
                Assert.Equal("WRITE_FAILED", failed.Code);

                // 补偿进入统一重连状态机：ReconnectPending + 延迟重连任务（带完成观察），而非一次性登录任务。
                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;
                Assert.Equal(DeviceConnectionStatus.ReconnectPending, snapshot.Status);
                Assert.True(fixture.DelayedScheduler.GetSnapshot().GetSourceCount("Stage4Reconnect") >= 1, "补偿未安排延迟重连任务。");

                // 让补偿登录在设备队列中过期：先阻塞工作线程（确认已开始），再启动调度器派发重连任务。
                var f02BlockerStarted = new ManualResetEventSlim(false);
                fixture.Dispatcher.Submit(Blocker(1, 400, f02BlockerStarted));
                Assert.True(f02BlockerStarted.Wait(TimeSpan.FromSeconds(2)), "阻塞任务未开始执行。");
                var schedulerContext = new BackgroundTaskContext("f02-scheduler", CancellationToken.None, null);
                fixture.DelayedScheduler.StartAsync(schedulerContext).GetAwaiter().GetResult();

                // 过期后由完成观察重新安排，最终恢复在线。
                SpinUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.Status == DeviceConnectionStatus.Online, "补偿登录过期后设备未恢复在线。");
                SpinUntil(() => fixture.Gateway.Calls.Count(call => call.MethodName == "LoginAsync") > loginCount, "补偿链路未重新登录。");
            }
        }

        [TestCase]
        public static void H1_ManualDisconnectFails_NonForcedReconnect_RestoresBusinessUsability()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "h1-login");
                SpinUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.Status == DeviceConnectionStatus.Online, "初始登录未完成。");

                // 手动断开在撤防阶段失败：仍在线，但带手动断开标记。
                fixture.Gateway.ConfigureException("CloseAlarmAsync", new DeviceGatewayException("CloseAlarm", SdkError.FromCode(7)));
                var disconnect = fixture.Lifecycle.DisconnectDevice(1, "h1-disconnect");
                Assert.False(disconnect.Success);
                var stuck = fixture.Registry.TryGetByDeviceId(1).Snapshot;
                Assert.True(stuck.Reconnect.ManualDisconnected, "断开失败应保留手动断开意图。");
                Assert.Equal(DeviceConnectionStatus.Online, stuck.Status);

                // 故障解除后普通重连（force=false）不得因"已在线"假成功，必须清除标记并恢复可用。
                fixture.Gateway.Configure("CloseAlarmAsync", new MockGatewayBehavior());
                var reconnect = fixture.Lifecycle.ReconnectDevice(1, force: false, requestId: "h1-reconnect");
                Assert.True(reconnect.Success, reconnect.Message);
                SpinUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.Status == DeviceConnectionStatus.Online, "重连后未恢复在线。");
                var recovered = fixture.Registry.TryGetByDeviceId(1).Snapshot;
                Assert.False(recovered.Reconnect.ManualDisconnected, "重连后手动断开标记未清除。");
                Assert.True(recovered.SdkUserId.HasValue);

                // 后续业务任务实际执行，不再被 DEVICE_MANUALLY_DISCONNECTED 拒绝。
                var check = fixture.Lifecycle.SubmitHealthCheck(1, wait: true, requestId: "h1-health");
                Assert.True(check.Success, check.Message);
            }
        }

        [TestCase]
        public static void H2_DiskReplay_RespectsLaneCapacity_AndDrainsAllAfterRecovery()
        {
            var runDirectory = TestWorkspace.Create();
            var retryDirectory = Path.Combine(runDirectory, "acs-retry");
            var writer = new AcsEventRetrySpool(retryDirectory, null);
            for (var i = 0; i < 12; i++)
            {
                writer.Save(NewRawEvent("h2-disk-" + i));
            }

            var processor = new FollowUpProcessor { FailAllRetryable = true };
            var service = new FaceEventIngestionService(
                new FaceEventLoggingOptions { QueueCapacity = 2, BatchSize = 1, FlushIntervalMs = 20 },
                processor, null, retryDirectory);
            service.ItemRetryLimit = 100;
            var context = new BackgroundTaskContext("h2", CancellationToken.None, null);
            service.StartAsync(context).GetAwaiter().GetResult();
            try
            {
                // 故障期间内存准入有界：处理过的不同事件数稳定在 容量+批次 以内，磁盘不再继续加载。
                var lastSeen = -1;
                var stableSince = DateTime.UtcNow;
                var deadline = DateTime.UtcNow.AddSeconds(6);
                while (DateTime.UtcNow < deadline)
                {
                    var distinct = processor.SeenRequestIds.Count;
                    if (distinct != lastSeen)
                    {
                        lastSeen = distinct;
                        stableSince = DateTime.UtcNow;
                    }
                    else if (distinct >= 1 && DateTime.UtcNow - stableSince > TimeSpan.FromMilliseconds(600))
                    {
                        break;
                    }

                    Thread.Sleep(50);
                }

                Assert.True(processor.SeenRequestIds.Count <= 2 + 1, "磁盘回放突破容量约束，内存内不同事件数: " + processor.SeenRequestIds.Count);

                // 故障解除后全部磁盘事件最终排空，文件清理完毕。
                processor.FailAllRetryable = false;
                SpinUntil(() => processor.SucceededRequestIds.Count >= 12, "恢复后未排空全部磁盘事件。");
                SpinUntil(() => !Directory.EnumerateFiles(retryDirectory, "*.json").Any(), "排空后重试文件未清理。");
            }
            finally
            {
                service.StopAsync(context).GetAwaiter().GetResult();
                service.Dispose();
            }
        }

        [TestCase]
        public static void H3_DueRetriesAndNewEvents_AlternateFairly()
        {
            var runDirectory = TestWorkspace.Create();
            var retryDirectory = Path.Combine(runDirectory, "acs-retry");
            var processor = new FollowUpProcessor { FailAllRetryable = true };
            var service = new FaceEventIngestionService(
                new FaceEventLoggingOptions { QueueCapacity = 20, BatchSize = 1, FlushIntervalMs = 50 },
                processor, null, retryDirectory);
            var context = new BackgroundTaskContext("h3", CancellationToken.None, null);
            service.StartAsync(context).GetAwaiter().GetResult();
            try
            {
                // 六条事件全部失败进入重试道。
                for (var i = 0; i < 6; i++)
                {
                    Assert.True(service.TryEnqueue(NewRawEvent("h3-r" + i)).Accepted);
                }

                SpinUntil(() => service.Count == 0 && Enumerable.Count(processor.SeenRequestIds, id => id.StartsWith("h3-r")) >= 6, "六条事件未全部进入重试道。");

                // 阻塞消费者等待全部重试到期，再入队新事件，形成"到期重试 + 新事件"争用。
                var blocker = NewRawEvent("h3-blocker");
                processor.BlockingRequestIds.Add(blocker.RequestId);
                Assert.True(service.TryEnqueue(blocker).Accepted);
                SpinUntil(() => processor.BlockEntered.IsSet, "阻塞事件未开始处理。");
                Assert.True(service.TryEnqueue(NewRawEvent("h3-fresh")).Accepted);
                Thread.Sleep(320);

                processor.FailAllRetryable = false;
                processor.RecordOrder = true;
                processor.ReleaseBlock();
                SpinUntil(() => processor.SucceededRequestIds.Contains("h3-fresh")
                    && Enumerable.Count(processor.SucceededRequestIds, id => id.StartsWith("h3-r")) >= 6, "重试与新事件未全部完成。");

                // 新事件必须在少量重试之后获得处理机会，不得被连续六轮重试阻塞。
                var order = processor.RecordedOrder;
                var freshIndex = order.IndexOf("h3-fresh");
                Assert.True(freshIndex >= 0, "新事件未被处理。");
                var retriesBeforeFresh = order.Take(freshIndex).Count(id => id.StartsWith("h3-r"));
                Assert.True(retriesBeforeFresh <= 2, "新事件被到期重试连续阻塞，前置重试 " + retriesBeforeFresh + " 条。");
            }
            finally
            {
                processor.ReleaseBlock();
                service.StopAsync(context).GetAwaiter().GetResult();
                service.Dispose();
            }
        }

        [TestCase]
        public static void G1_RetryRound_DoesNotDropNewEvents()
        {
            var runDirectory = TestWorkspace.Create();
            var retryDirectory = Path.Combine(runDirectory, "acs-retry");
            var processor = new FollowUpProcessor();
            var service = new FaceEventIngestionService(
                new FaceEventLoggingOptions { QueueCapacity = 20, BatchSize = 1, FlushIntervalMs = 50 },
                processor, null, retryDirectory);
            var context = new BackgroundTaskContext("g1", CancellationToken.None, null);
            service.StartAsync(context).GetAwaiter().GetResult();
            try
            {
                var eventA = NewRawEvent("g1-a");
                processor.FailOnceRequestIds.Add(eventA.RequestId);
                Assert.True(service.TryEnqueue(eventA).Accepted);
                SpinUntil(() => processor.PoisonAttempts >= 1, "事件 A 未完成首次处理。");

                var eventB = NewRawEvent("g1-b");
                processor.BlockingRequestIds.Add(eventB.RequestId);
                Assert.True(service.TryEnqueue(eventB).Accepted);
                SpinUntil(() => processor.BlockEntered.IsSet, "事件 B 未进入阻塞处理。");
                var eventC = NewRawEvent("g1-c");
                Assert.True(service.TryEnqueue(eventC).Accepted);

                // 确保 A 的重试已到期后释放 B：下一轮为重试独占轮，活动批次正持有 C（修复前会被误删）。
                Thread.Sleep(300);
                processor.ReleaseBlock();
                SpinUntil(() => processor.SucceededRequestIds.Contains(eventA.RequestId), "事件 A 重试未成功。");

                service.StopAsync(context).GetAwaiter().GetResult();
                Assert.True(processor.SucceededRequestIds.Contains(eventA.RequestId), "事件 A 未成功。");
                Assert.True(processor.SucceededRequestIds.Contains(eventB.RequestId), "事件 B 未成功。");
                Assert.True(processor.SucceededRequestIds.Contains(eventC.RequestId), "事件 C 被重试轮误删。");
                Assert.Equal(0, service.Count);
                Assert.False(Directory.EnumerateFiles(retryDirectory, "*.json").Any(), "不应残留重试/死信文件。");
            }
            finally
            {
                service.Dispose();
            }
        }

        [TestCase]
        public static void G2_DeleteCleanupCloseAlarmFails_KeepsLiveSessionWithoutReconnect()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "g2-login");
                SpinUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.Status == DeviceConnectionStatus.Online, "初始登录未完成。");
                var originalSession = fixture.Registry.TryGetByDeviceId(1).Snapshot.SdkUserId.Value;
                var loginCount = fixture.Gateway.Calls.Count(call => call.MethodName == "LoginAsync");

                fixture.Gateway.ConfigureException("CloseAlarmAsync", new DeviceGatewayException("CloseAlarm", SdkError.FromCode(17)));
                var failed = fixture.Lifecycle.DeleteDevice(1, disconnectFirst: true, requestId: "g2-delete");

                Assert.False(failed.Success);
                Thread.Sleep(200);
                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;
                Assert.Equal(DeviceConnectionStatus.Online, snapshot.Status);
                Assert.Equal(originalSession, snapshot.SdkUserId.Value);
                Assert.True(snapshot.AlarmHandle.HasValue, "撤防失败的报警句柄应保留。");
                Assert.Equal(loginCount, fixture.Gateway.Calls.Count(call => call.MethodName == "LoginAsync"), "存活会话不得被重连覆盖。");
                Assert.Equal(0, fixture.DelayedScheduler.GetSnapshot().GetSourceCount("Stage4Reconnect"), "会话存活时不应安排重连。");
            }
        }

        [TestCase]
        public static void G2_DeleteCleanupLogoutFails_HandsSessionToStaleCleanupAndRecovers()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.Options.ReconnectBaseDelayMs = 10;
                fixture.Options.ReconnectMaxDelayMs = 20;
                fixture.Options.MaxReconnectAttempts = 0;
                var schedulerContext = new BackgroundTaskContext("g2-scheduler", CancellationToken.None, null);
                fixture.DelayedScheduler.StartAsync(schedulerContext).GetAwaiter().GetResult();
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "g2-login");
                SpinUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.Status == DeviceConnectionStatus.Online, "初始登录未完成。");
                var originalSession = fixture.Registry.TryGetByDeviceId(1).Snapshot.SdkUserId.Value;
                var loginCount = fixture.Gateway.Calls.Count(call => call.MethodName == "LoginAsync");

                fixture.Gateway.ConfigureException("LogoutAsync", new DeviceGatewayException("Logout", SdkError.FromCode(7)));
                var failed = fixture.Lifecycle.DeleteDevice(1, disconnectFirst: true, requestId: "g2-delete-logout");
                Assert.False(failed.Success);

                var handedOff = fixture.Registry.TryGetByDeviceId(1).Snapshot;
                Assert.True(handedOff.StaleSdkUserId.HasValue, "登出失败的旧会话应移交 StaleSdkUserId。");

                fixture.Gateway.Configure("LogoutAsync", new MockGatewayBehavior());
                SpinUntil(() => fixture.Gateway.Calls.Count(call => call.MethodName == "LoginAsync") > loginCount, "补偿重连未执行。");
                SpinUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.Status == DeviceConnectionStatus.Online, "补偿重连后未恢复在线。");
                Assert.True(fixture.Gateway.Calls.Any(call => call.MethodName == "LogoutAsync" && ((LogoutRequest)call.Request).UserId == originalSession),
                    "旧会话应被统一清理登出，而不是被覆盖遗忘。");
            }
        }

        [TestCase]
        public static void G3_SameGenerationCompletion_IsAtomicPerTask()
        {
            var manager = new DoorTargetStateManager();
            var target = new DoorTarget { DoorDeviceId = 1, DoorNo = 1, TargetKey = "1:1" };
            var now = new DateTime(2026, 1, 1, 8, 0, 0);

            manager.OnCameraWindowOpened("cam-a", target, now);
            manager.OnCameraWindowClosed("cam-a", "1:1", now.AddSeconds(5));
            manager.MarkRestoreSubmitted("1:1", generation: 1, taskId: "t1", attempt: 0, now: now.AddSeconds(5));
            manager.RecordRestoreFailure("1:1", generation: 1, taskId: "t1", attempt: 1, nextRetryAt: now.AddSeconds(6), now: now.AddSeconds(5));
            Assert.True(manager.TryGetActivity("1:1", out var afterFirst));
            Assert.False(afterFirst.RestoreInFlight);

            // 旧到期记录仍在时提交新任务 t2：在途标记保护其不被重复调度。
            manager.MarkRestoreSubmitted("1:1", generation: 1, taskId: "t2", attempt: 1, now: now.AddSeconds(6));
            Assert.True(manager.TryGetActivity("1:1", out var second));
            Assert.True(second.RestoreInFlight);
            Assert.Equal(0, manager.GetDueRestoreRetries(now.AddYears(1)).Count, "在途任务不应出现在到期扫描。");

            // 旧任务 t1 的迟到结果不得影响 t2 的在途与重试安排。
            Assert.False(manager.MarkRestoreSucceeded("1:1", generation: 1, taskId: "t1", now: now.AddSeconds(7)));
            Assert.False(manager.RecordRestoreFailure("1:1", generation: 1, taskId: "t1", attempt: 9, nextRetryAt: null, now: now.AddSeconds(7)));
            Assert.False(manager.ClearRestoreInFlight("1:1", generation: 1, taskId: "t1"));
            Assert.True(manager.TryGetActivity("1:1", out var afterStale));
            Assert.True(afterStale.RestoreInFlight, "旧任务结果清除了新任务在途标记。");
            Assert.Equal(1, afterStale.PendingRestoreAttempt);
            Assert.True(afterStale.RestoreNextRetryAt.HasValue);

            // t2 自身完成正常生效。
            Assert.True(manager.MarkRestoreSucceeded("1:1", generation: 1, taskId: "t2", now: now.AddSeconds(8)));
            Assert.False(manager.TryGetActivity("1:1", out var cleared));
        }

        [TestCase]
        public static void F03_StaleGenerationCompletion_DoesNotAffectNewWindow()
        {
            var manager = new DoorTargetStateManager();
            var target = new DoorTarget { DoorDeviceId = 1, DoorNo = 1, TargetKey = "1:1" };
            var now = new DateTime(2026, 1, 1, 8, 0, 0);

            manager.OnCameraWindowOpened("cam-a", target, now);
            manager.OnCameraWindowClosed("cam-a", "1:1", now.AddSeconds(5));
            manager.MarkRestoreSubmitted("1:1", generation: 1, taskId: "t1", attempt: 0, now: now.AddSeconds(5));

            // 新窗口开启：代次递增、在途清除，随后新恢复任务在途。
            manager.OnCameraWindowOpened("cam-a", target, now.AddSeconds(6));
            manager.OnCameraWindowClosed("cam-a", "1:1", now.AddSeconds(11));
            manager.MarkRestoreSubmitted("1:1", generation: 2, taskId: "t2", attempt: 1, now: now.AddSeconds(11));
            Assert.True(manager.TryGetActivity("1:1", out var current));
            Assert.True(current.RestoreInFlight);

            // 旧代次的迟到完成：成功、失败、清除在途均不得影响新窗口状态。
            manager.MarkRestoreSucceeded("1:1", generation: 1, taskId: "t1", now: now.AddSeconds(12));
            manager.RecordRestoreFailure("1:1", generation: 1, taskId: "t1", attempt: 5, nextRetryAt: now.AddSeconds(1), now: now.AddSeconds(12));
            manager.ClearRestoreInFlight("1:1", generation: 1, taskId: "t1");

            Assert.True(manager.TryGetActivity("1:1", out var afterStale));
            Assert.True(afterStale.RestoreInFlight, "旧代次结果清除了新任务在途标记。");
            Assert.Equal(1, afterStale.PendingRestoreAttempt);
            Assert.False(afterStale.RestoreTerminalFailed);
            Assert.Equal(0, manager.GetDueRestoreRetries(now.AddYears(1)).Count, "在途任务不应出现在到期重试扫描。");

            // 新代次的正常完成仍生效。
            manager.MarkRestoreSucceeded("1:1", generation: 2, taskId: "t2", now: now.AddSeconds(13));
            Assert.False(manager.TryGetActivity("1:1", out var cleared));
        }

        [TestCase]
        public static void F04_RetryCoordinator_UsesConfiguredFaceImageLimit()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice();
                var payload = @"{""employee_id"":""10001"",""face_image_base64"":""" + BigJpegBase64(250 * 1024) + @"""}";
                var state = DeviceOperationRetryState.FromRow(Row(id: 1, deviceId: 1, employeeId: "10001", facePending: true, facePayload: payload, attemptCount: 1));
                var plan = new RetryCommandPlanner().Plan(state);

                // 默认 200KB：同一载荷被判为不可重试的 INVALID_PAYLOAD。
                var legacy = new RetryExecutionCoordinator(fixture.Dispatcher, fixture.Gateway);
                var legacyResult = legacy.ExecuteAsync(plan, "f04-legacy", CancellationToken.None).GetAwaiter().GetResult();
                Assert.False(legacyResult.Retryable);
                Assert.Equal("INVALID_PAYLOAD", legacyResult.Code);

                // 配置 300KB：解析与 SDK 上传使用同一上限，在线可接受的载荷补偿同样接受。
                var coordinator = new RetryExecutionCoordinator(fixture.Dispatcher, fixture.Gateway, maxFaceImageBytes: 300 * 1024);
                var result = coordinator.ExecuteAsync(plan, "f04-configured", CancellationToken.None).GetAwaiter().GetResult();

                Assert.True(result.SucceededOperations.Contains(RetryOperation.Face), "配置上限下补偿未执行人脸下发: " + result.Code + " " + result.Message);
            }
        }

        [TestCase]
        public static void F05_CompactNaming_ReusesExistingSnapshot()
        {
            var runDirectory = TestWorkspace.Create();
            var root = Path.Combine(runDirectory, new string('x', Math.Max(1, 180 - runDirectory.Length)));
            var storage = new SnapshotStorage(runDirectory, new FaceEventLoggingOptions { SnapshotRootDirectory = root });

            string firstPath = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var result = storage.Save(NewLongEmployeeEvent());
                Assert.True(result.Saved, "保存失败: " + result.ErrorMessage);
                if (firstPath == null)
                {
                    firstPath = result.SnapshotPath;
                    Assert.True(firstPath.Length <= 255, "测试应触发紧凑命名分支。");
                }
                else
                {
                    Assert.Equal(firstPath, result.SnapshotPath);
                }
            }

            Assert.Equal(1, Directory.EnumerateFiles(root, "*.jpg", SearchOption.AllDirectories).Count());
        }

        private static RawAcsAlarmEvent NewRawEvent(string requestId)
        {
            return new RawAcsAlarmEvent
            {
                ReceivedAt = new DateTime(2026, 9, 7, 10, 0, 0),
                Command = AcsAlarmEventRouter.CommAlarmAcs,
                DeviceId = 7,
                DeviceIp = "192.168.1.10",
                Source = AcsAlarmEventSource.Realtime,
                RequestId = requestId
            };
        }

        private static AcsFaceEvent NewLongEmployeeEvent()
        {
            return new AcsFaceEvent
            {
                EventId = 990101,
                EmployeeId = new string('e', 64),
                EventTime = new DateTime(2026, 9, 7, 12, 30, 15, 123),
                DeviceId = 7,
                PictureBytes = new byte[] { 0xFF, 0xD8, 0x01, 0x02, 0x03, 0xFF, 0xD9 }
            };
        }

        private static string BigJpegBase64(int size)
        {
            var bytes = new byte[size];
            bytes[0] = 0xFF;
            bytes[1] = 0xD8;
            for (var i = 2; i < size - 2; i++)
            {
                bytes[i] = 0xAB;
            }

            bytes[size - 2] = 0xFF;
            bytes[size - 1] = 0xD9;
            return Convert.ToBase64String(bytes);
        }

        private static DeviceSdkTask Blocker(int deviceId, int milliseconds, ManualResetEventSlim started = null)
        {
            return new DeviceSdkTask(deviceId, DeviceTaskType.HealthCheck, "F02Blocker", context => Task.Run(() =>
            {
                started?.Set();
                Thread.Sleep(milliseconds);
                var now = DateTime.Now;
                return DeviceTaskResult.FromTask(context.Task, true, "OK", "ok", DeviceConnectionStatus.Online, now, now);
            }));
        }

        private static IReadOnlyDictionary<string, object> Row(
            long id,
            int deviceId,
            string employeeId,
            bool facePending = false,
            string facePayload = null,
            int attemptCount = 0)
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = id,
                ["device_id"] = deviceId,
                ["employee_id"] = employeeId,
                ["permission_level"] = null,
                ["permission_payload"] = null,
                ["permission_pending"] = false,
                ["permission_sync_completion_blocked"] = false,
                ["person_payload"] = null,
                ["person_pending"] = false,
                ["face_payload"] = facePayload,
                ["face_pending"] = facePending,
                ["delete_person_pending"] = false,
                ["delete_face_pending"] = false,
                ["intent_version"] = Guid.NewGuid(),
                ["attempt_count"] = attemptCount,
                ["next_retry_at"] = new DateTime(2026, 1, 1),
                ["last_error"] = null,
                ["last_attempt_at"] = null,
                ["exhausted_at"] = null,
                ["created_at"] = new DateTime(2026, 1, 1),
                ["updated_at"] = new DateTime(2026, 1, 1)
            };
        }

        private static void SpinUntil(Func<bool> condition, string message)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                Thread.Sleep(10);
            }

            Assert.True(condition(), message);
        }

        private sealed class FollowUpProcessor : IAcsFaceEventProcessor
        {
            public readonly HashSet<string> FailOnceRequestIds = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> SucceededRequestIds = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> BlockingRequestIds = new HashSet<string>(StringComparer.Ordinal);
            public readonly ManualResetEventSlim BlockGate = new ManualResetEventSlim(false);
            public readonly ManualResetEventSlim BlockEntered = new ManualResetEventSlim(false);
            public readonly HashSet<string> SeenRequestIds = new HashSet<string>(StringComparer.Ordinal);
            public readonly List<string> RecordedOrder = new List<string>();
            public bool RecordOrder;
            public bool FailAllRetryable;
            public int PoisonAttempts;

            public void ReleaseBlock()
            {
                BlockGate.Set();
            }

            public FaceEventProcessResult Process(RawAcsAlarmEvent rawEvent)
            {
                return Decide(rawEvent);
            }

            public IReadOnlyList<FaceEventBatchItemResult> ProcessBatch(IReadOnlyList<RawAcsAlarmEvent> rawEvents)
            {
                var results = new List<FaceEventBatchItemResult>(rawEvents.Count);
                foreach (var rawEvent in rawEvents)
                {
                    var outcome = Decide(rawEvent);
                    results.Add(outcome.Success
                        ? FaceEventBatchItemResult.Ok(0, outcome.Code, outcome.Message)
                        : FaceEventBatchItemResult.Failed(0, outcome.Code, outcome.Message));
                }

                return results;
            }

            private FaceEventProcessResult Decide(RawAcsAlarmEvent rawEvent)
            {
                SeenRequestIds.Add(rawEvent.RequestId);
                if (RecordOrder)
                {
                    RecordedOrder.Add(rawEvent.RequestId);
                }

                if (BlockingRequestIds.Contains(rawEvent.RequestId))
                {
                    BlockEntered.Set();
                    BlockGate.Wait();
                    SucceededRequestIds.Add(rawEvent.RequestId);
                    return FaceEventProcessResult.Ok("INSERTED", "ok");
                }

                var poisonOnce = FailOnceRequestIds.Contains(rawEvent.RequestId);
                if (poisonOnce)
                {
                    PoisonAttempts++;
                    if (PoisonAttempts == 1)
                    {
                        return FaceEventProcessResult.Failed("RETRYABLE_FAILURE", "transient once");
                    }

                    SucceededRequestIds.Add(rawEvent.RequestId);
                    return FaceEventProcessResult.Ok("INSERTED", "ok");
                }

                if (FailAllRetryable)
                {
                    return FaceEventProcessResult.Failed("RETRYABLE_FAILURE", "transient");
                }

                SucceededRequestIds.Add(rawEvent.RequestId);
                return FaceEventProcessResult.Ok("INSERTED", "ok");
            }
        }
    }
}
