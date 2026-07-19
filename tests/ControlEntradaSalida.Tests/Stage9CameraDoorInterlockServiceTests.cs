using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using ControlDoor.CameraDoorInterlock;
using ControlDoor.Configuration;
using ControlDoor.Host;
using ControlDoor.Hikvision;
using ControlDoor.Observability;
using ControlDoor.Runtime;

namespace ControlEntradaSalida.Tests
{
    public static class Stage9CameraDoorInterlockServiceTests
    {
        [TestCase]
        public static void Stage9Service_Disabled_DoesNotSubmitDoorControl()
        {
            using (var fixture = new Stage9Fixture(enabled: false))
            {
                Assert.True(fixture.Service.IsDisabled);
                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(DateTime.Now);

                Assert.Equal(0, fixture.ControlGatewayCallCount());
            }
        }

        [TestCase]
        public static void Stage9Service_ConfiguredCameraHit_SubmitsAlwaysClose()
        {
            using (var fixture = new Stage9Fixture())
            {
                var t0 = new DateTime(2026, 1, 1, 8, 0, 0);
                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(t0);
                fixture.SpinForControlGatewayCalls(1);

                var request = AlwaysCloseRequest(fixture);
                Assert.NotNull(request);
                Assert.Equal(GateControlCommand.AlwaysClose, request.Command);
                Assert.Equal(1, request.GateIndex);
            }
        }

        [TestCase]
        public static void Stage9Service_GarbagePayload_StillTriggersAlwaysClose()
        {
            using (var fixture = new Stage9Fixture())
            {
                fixture.EmitAiopAlarm(fixture.CameraIp, rawPayload: new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
                fixture.Service.ProcessEvents(DateTime.Now);
                fixture.SpinForControlGatewayCalls(1);

                Assert.NotNull(AlwaysCloseRequest(fixture));
            }
        }

        [TestCase]
        public static void Stage9Service_NonShortSleeveType_StillTriggersAlwaysClose()
        {
            using (var fixture = new Stage9Fixture())
            {
                var buffer = BuildAiopBuffer("{\"events\":{\"alertInfo\":[{\"target\":{\"type\":3,\"modelID\":\"m\"}}]}}");
                fixture.EmitAiopAlarm(fixture.CameraIp, rawPayload: buffer);
                fixture.Service.ProcessEvents(DateTime.Now);
                fixture.SpinForControlGatewayCalls(1);

                Assert.NotNull(AlwaysCloseRequest(fixture));
            }
        }

        [TestCase]
        public static void Stage9Service_RepeatAlarmWithinWindow_DoesNotRepeatAlwaysClose()
        {
            using (var fixture = new Stage9Fixture())
            {
                var t0 = new DateTime(2026, 1, 1, 8, 0, 0);
                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(t0);
                fixture.SpinForControlGatewayCalls(1);

                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(t0.AddSeconds(2));
                fixture.SpinForControlGatewayCalls(1);

                Assert.Equal(1, fixture.Gateway.Calls.Count(c => c.MethodName == "ControlGatewayAsync" && ((GateControlRequest)c.Request).Command == GateControlCommand.AlwaysClose));
            }
        }

        [TestCase]
        public static void Stage9Service_ContinuousAlarmExtendsWindowAndRestoresAfterQuietPeriod()
        {
            using (var fixture = new Stage9Fixture())
            {
                var t0 = new DateTime(2026, 1, 1, 8, 0, 0);
                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(t0);
                fixture.SpinForControlGatewayCalls(1);

                for (var second = 1; second <= 4; second++)
                {
                    fixture.EmitAiopAlarm(fixture.CameraIp);
                    fixture.Service.ProcessEvents(t0.AddSeconds(second));
                    fixture.SpinForControlGatewayCalls(1);
                }

                Assert.Equal(1, fixture.Gateway.Calls.Count(c => CommandOf(c) == GateControlCommand.AlwaysClose));

                fixture.Service.ExpireWindows(t0.AddSeconds(5));
                Assert.Equal(0, RestoreCount(fixture), "持续报警已将窗口续期，第 5 秒不应恢复。");
                Assert.Equal(1, fixture.WindowManager.GetActive().Count);

                fixture.Service.ExpireWindows(t0.AddSeconds(9));
                fixture.SpinForControlGatewayCalls(2); // 等待异步恢复任务分配并执行（1 常闭 + 1 恢复）。
                Assert.Equal(1, RestoreCount(fixture), "最后一条报警后静默满 WindowSeconds 才应恢复。");
            }
        }

        [TestCase]
        public static void Stage9Service_WindowEnd_SubmitsRestore()
        {
            using (var fixture = new Stage9Fixture())
            {
                var t0 = new DateTime(2026, 1, 1, 8, 0, 0);
                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(t0);
                fixture.SpinForControlGatewayCalls(1);

                fixture.Service.ExpireWindows(t0.AddSeconds(5));
                fixture.SpinForControlGatewayCalls(2); // 等待异步恢复任务执行完成。

                var restore = RestoreRequest(fixture);
                Assert.NotNull(restore);
                Assert.Equal(GateControlCommand.Restore, restore.Command);
            }
        }

        [TestCase]
        public static void Stage9Service_OneCameraMultipleDoors_AllClosedAndRestored()
        {
            using (var fixture = new Stage9Fixture(doorNos: new[] { 1, 2 }))
            {
                var t0 = new DateTime(2026, 1, 1, 8, 0, 0);
                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(t0);
                fixture.SpinForControlGatewayCalls(2);

                var alwaysCloseDoorNos = fixture.Gateway.Calls
                    .Where(c => c.MethodName == "ControlGatewayAsync" && ((GateControlRequest)c.Request).Command == GateControlCommand.AlwaysClose)
                    .Select(c => ((GateControlRequest)c.Request).GateIndex)
                    .OrderBy(x => x)
                    .ToList();
                Assert.Equal(2, alwaysCloseDoorNos.Count);
                Assert.Equal(1, alwaysCloseDoorNos[0]);
                Assert.Equal(2, alwaysCloseDoorNos[1]);

                fixture.Service.ExpireWindows(t0.AddSeconds(5));
                fixture.SpinForControlGatewayCalls(4); // 等待异步恢复任务执行完成（2 常闭 + 2 恢复）。

                var restoreDoorNos = fixture.Gateway.Calls
                    .Where(c => c.MethodName == "ControlGatewayAsync" && ((GateControlRequest)c.Request).Command == GateControlCommand.Restore)
                    .Select(c => ((GateControlRequest)c.Request).GateIndex)
                    .OrderBy(x => x)
                    .ToList();
                Assert.Equal(2, restoreDoorNos.Count);
            }
        }

        [TestCase]
        public static void Stage9Service_TwoCamerasSharedDoor_RestoresOnlyAfterLastWindowEnds()
        {
            using (var fixture = new Stage9Fixture(secondCameraIp: "10.0.0.6"))
            {
                var t0 = new DateTime(2026, 1, 1, 8, 0, 0);

                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(t0);
                fixture.SpinForControlGatewayCalls(1);

                fixture.EmitAiopAlarm(fixture.SecondCameraIp);
                fixture.Service.ProcessEvents(t0.AddSeconds(2));
                fixture.SpinForControlGatewayCalls(1);

                Assert.Equal(1, fixture.Gateway.Calls.Count(c => CommandOf(c) == GateControlCommand.AlwaysClose));
                fixture.Service.ExpireWindows(t0.AddSeconds(5));
                Assert.Equal(0, RestoreCount(fixture), "第一摄像头窗口结束、仍有活动摄像头时不应恢复。");

                fixture.Service.ExpireWindows(t0.AddSeconds(7));
                fixture.SpinForControlGatewayCalls(2); // 等待异步恢复任务执行完成（1 常闭 + 1 恢复）。
                Assert.Equal(1, RestoreCount(fixture), "最后一个摄像头窗口结束才应恢复。");
            }
        }

        [TestCase]
        public static void Stage9Service_StopBestEffortRestore_RestoresActiveDoorTargets()
        {
            using (var fixture = new Stage9Fixture())
            {
                var t0 = new DateTime(2026, 1, 1, 8, 0, 0);
                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(t0);
                fixture.SpinForControlGatewayCalls(1);

                var result = fixture.Service.RestoreActiveTargetsBestEffort(TimeSpan.FromSeconds(5));

                Assert.Equal(1, result.Total);
                Assert.Equal(1, result.Succeeded);
                Assert.True(fixture.Gateway.Calls.Any(c => CommandOf(c) == GateControlCommand.Restore));
            }
        }

        [TestCase]
        public static void Stage9HostStop_RestoresActiveDoorBeforeDeviceLogout()
        {
            using (var fixture = new Stage9Fixture())
            using (var host = new ControlDoorHost(TestWorkspace.Create()))
            {
                fixture.Service.StartAsync(new BackgroundTaskContext("stage9-host-stop", CancellationToken.None, null)).GetAwaiter().GetResult();
                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.SpinForControlGatewayCalls(1);
                var backgroundHost = new BackgroundTaskHost();
                backgroundHost.Register(fixture.Service, startOrder: 36, stopOrder: 54, isCritical: false);
                backgroundHost.StartAsync().GetAwaiter().GetResult();
                SetHostField(host, "backgroundTaskHost", backgroundHost);
                SetHostField(host, "deviceLifecycle", fixture.Lifecycle);
                SetHostField(host, "deviceDispatcher", fixture.Dispatcher);
                SetHostField(host, "cameraDoorInterlockService", fixture.Service);
                SetHostField(host, "state", ServiceLifecycleState.Running);

                var stop = host.StopAsync("stage9-host-stop-test").GetAwaiter().GetResult();

                Assert.True(stop.Success);
                var calls = fixture.Gateway.Calls.ToList();
                var restoreIndex = calls.FindIndex(c => CommandOf(c) == GateControlCommand.Restore);
                var logoutIndex = calls.FindIndex(c => c.MethodName == "LogoutAsync");
                Assert.True(restoreIndex >= 0, "Host stop must restore active Stage9 door targets.");
                Assert.True(logoutIndex >= 0, "Host stop must still logout devices after restore.");
                Assert.True(restoreIndex < logoutIndex, "Restore must run before device logout so SdkUserId is still available.");
            }
        }

        [TestCase]
        public static void Stage9Service_StopBestEffortRestore_HonorsTimeoutAndReportsUnfinished()
        {
            using (var fixture = new Stage9Fixture())
            {
                var t0 = new DateTime(2026, 1, 1, 8, 0, 0);
                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(t0);
                fixture.SpinForControlGatewayCalls(1);
                fixture.Gateway.ConfigureDelay("ControlGatewayAsync", TimeSpan.FromSeconds(2));

                var startedAt = DateTime.UtcNow;
                var result = fixture.Service.RestoreActiveTargetsBestEffort(TimeSpan.FromMilliseconds(100));
                var elapsed = DateTime.UtcNow - startedAt;

                Assert.True(elapsed < TimeSpan.FromSeconds(1), "best-effort restore must return when timeout expires.");
                Assert.Equal(1, result.Total);
                Assert.Equal(1, result.Unfinished);
                Assert.Equal(0, result.Succeeded);
            }
        }

        [TestCase]
        public static void Stage9Service_RestorePriority_CriticalHigherThanAlwaysClose()
        {
            using (var fixture = new Stage9Fixture())
            {
                var t0 = new DateTime(2026, 1, 1, 8, 0, 0);
                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(t0);

                var alwaysCloseTask = fixture.TaskFactory.CreateAlwaysClose(fixture.DoorDeviceId, 1, fixture.DoorDeviceId + ":1", "req-ac");
                var restoreTask = fixture.TaskFactory.CreateRestore(fixture.DoorDeviceId, 1, fixture.DoorDeviceId + ":1", "req-rs", 0);

                Assert.True(restoreTask.Priority < alwaysCloseTask.Priority, "恢复优先级(Critical)应高于常闭(High)。");
            }
        }

        [TestCase]
        public static void Stage9Service_DisposeLoopWaitFailure_WritesWarnLog()
        {
            var runDirectory = TestWorkspace.Create();
            using (var logger = new ServiceLogger(LogOptions.FromSettings(runDirectory, new LoggingOptions { LogDirectory = "logs" })))
            using (var fixture = new Stage9Fixture(
                clock: () => { throw new InvalidOperationException("loop exploded"); },
                logger: logger))
            {
                fixture.Service.StartAsync(new ControlDoor.Runtime.BackgroundTaskContext("stage9-dispose-fault", CancellationToken.None, logger)).GetAwaiter().GetResult();
                SpinUntil(() => fixture.Service.GetStatus().LastError != null, "loop fault was not observed.");

                fixture.Service.Dispose();

                var text = System.IO.File.ReadAllText(logger.CurrentLogPath);
                Assert.Contains("level=Warn", text);
                Assert.Contains("CameraDoorInterlockDispose", text);
                Assert.Contains("loop exploded", text);
            }
        }

        [TestCase]
        public static void Stage9Service_TryEnqueueDuringDispose_DoesNotThrowFromCallbackThread()
        {
            using (var fixture = new Stage9Fixture())
            {
                fixture.Service.Dispose();

                Exception callbackException = null;
                AiopAlarmEnqueueResult result = null;
                var callback = new Thread(() =>
                {
                    try
                    {
                        result = fixture.Service.TryEnqueue(new RawAiopAlarmEvent
                        {
                            CameraKey = fixture.CameraIp,
                            CameraIp = fixture.CameraIp,
                            Command = AiopAlarmEventRouter.CommUploadAiopVideo,
                            RawPayload = new byte[0]
                        });
                    }
                    catch (Exception ex)
                    {
                        callbackException = ex;
                    }
                });

                callback.Start();
                callback.Join();

                Assert.Equal(null, callbackException);
                Assert.NotNull(result);
                Assert.False(result.Accepted);
                Assert.Equal("DISPOSED", result.Code);
            }
        }

        [TestCase]
        public static void Stage9Service_AiopInterlockLogsShareInterlockIdAndOperationalFailureFields()
        {
            var runDirectory = TestWorkspace.Create();
            using (var logger = new ServiceLogger(LogOptions.FromSettings(runDirectory, new LoggingOptions { LogDirectory = "logs" })))
            using (var fixture = new Stage9Fixture(windowSeconds: 5, restoreRetryIntervalMs: 1000, logger: logger))
            {
                fixture.Gateway.ConfigureException("ControlGatewayAsync", new DeviceGatewayException("ControlGateway", SdkError.FromCode(7, "busy")));
                var t0 = new DateTime(2026, 1, 1, 8, 0, 0);

                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(t0);
                fixture.Service.ExpireWindows(t0.AddSeconds(5));
                fixture.SpinForControlGatewayCalls(2); // 等待异步常闭和恢复任务都执行完成，日志写入。

                var text = System.IO.File.ReadAllText(logger.CurrentLogPath);
                var interlockId = ExtractField(text, "interlockId");
                Assert.False(string.IsNullOrWhiteSpace(interlockId));
                Assert.True(CountOccurrences(text, "interlockId=\"" + interlockId + "\"") >= 4);
                Assert.Contains("operationName=\"AlwaysClose\"", text);
                Assert.Contains("operationName=\"RestoreDoor\"", text);
                Assert.Contains("deviceId=\"10\"", text);
                Assert.Contains("doorNo=\"1\"", text);
                Assert.Contains("sdkOperation=\"ControlGateway\"", text);
                Assert.Contains("sdkErrorCode=\"7\"", text);
                Assert.Contains("retryable=\"True\"", text);
                Assert.Contains("manualActionRequired=\"False\"", text);
            }
        }

        [TestCase]
        public static void Stage9Service_AlwaysCloseExecutionFailure_RegistersRetryAndResubmitsOnScan()
        {
            // AIOP-02：常闭投递/执行失败必须登记重试，扫描循环按配置间隔重投，直至成功。
            using (var fixture = new Stage9Fixture(windowSeconds: 30, restoreRetryIntervalMs: 60000))
            {
                fixture.Gateway.ConfigureException("ControlGatewayAsync", new DeviceGatewayException("ControlGateway", SdkError.FromCode(7, "busy")));
                var t0 = new DateTime(2026, 1, 1, 8, 0, 0);

                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(t0);
                // 等 always-close 异步完成回调登记失败 + 排下次重试。
                Stage9Fixture.SpinFor(() =>
                    fixture.TargetManager.TryGetActivity("10:1", out var after) &&
                    after.PendingAlwaysCloseAttempt.HasValue &&
                    after.AlwaysCloseNextRetryAt.HasValue,
                    "常闭失败必须登记下次重试，而不是只打日志。");

                fixture.TargetManager.TryGetActivity("10:1", out var failed);
                var nextRetryAt = failed.AlwaysCloseNextRetryAt.Value;

                // 设备恢复在线，扫描循环到点重投应成功并清掉挂起重试。
                fixture.Gateway.ConfigureResult<GateControlResponse>("ControlGatewayAsync", new GateControlResponse { Success = true, Code = "OK", Message = "ok", GateIndex = 1, Command = GateControlCommand.AlwaysClose });
                var retry = fixture.Service.ProcessAlwaysCloseRetries(nextRetryAt);
                Assert.Equal(1, retry.RetriesProcessed, "扫描循环应按登记的下次重试时间重投常闭。");

                Stage9Fixture.SpinFor(() =>
                    fixture.TargetManager.TryGetActivity("10:1", out var after) &&
                    !after.PendingAlwaysCloseAttempt.HasValue &&
                    !after.AlwaysCloseNextRetryAt.HasValue,
                    "常闭重试成功后应清掉挂起重试状态。");
                Assert.True(fixture.Gateway.Calls.Count(c => CommandOf(c) == GateControlCommand.AlwaysClose) >= 2,
                    "常闭至少应有首次失败 + 重试成功两次投递。");
            }
        }

        [TestCase]
        public static void Stage9Service_AlwaysCloseExecutionRetryableFailure_RegistersRetryAndResubmitsOnScan()
        {
            // AIOP-02：常闭执行可重试失败必须登记重试，不能只打日志后窗口内永不重试。
            using (var fixture = new Stage9Fixture(windowSeconds: 30, restoreRetryIntervalMs: 60000))
            {
                fixture.Gateway.ConfigureException("ControlGatewayAsync", new DeviceGatewayException("ControlGateway", SdkError.FromCode(7, "busy")));
                var t0 = new DateTime(2026, 1, 1, 8, 0, 0);

                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(t0);
                // 等待 always-close 任务执行完成并回调登记失败 + 排下次重试。
                Stage9Fixture.SpinFor(() =>
                    fixture.TargetManager.TryGetActivity("10:1", out var after) &&
                    after.PendingAlwaysCloseAttempt.HasValue &&
                    after.AlwaysCloseNextRetryAt.HasValue,
                    "常闭执行失败必须登记下次重试，不能只打日志后窗口内永不重试。");
            }
        }

        [TestCase]
        public static void Stage9Service_RestoreActiveTargetsBestEffort_AllotsPerDoorTimeSliceAndCoversAllDoors()
        {
            // AIOP-04：停止恢复时第一扇门慢/超时不能把全局超时吃光，至少为每扇门提交一次 best-effort 恢复。
            using (var fixture = new Stage9Fixture(doorNos: new[] { 1, 2, 3 }))
            {
                var t0 = new DateTime(2026, 1, 1, 8, 0, 0);
                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(t0);
                fixture.SpinForControlGatewayCalls(3);

                // 第一扇门慢：模拟它在 door slice 内不返回，best-effort 应超时但继续走后续门。
                fixture.Gateway.ConfigureDelay("ControlGatewayAsync", System.TimeSpan.FromMilliseconds(800));
                var startedAt = System.DateTime.UtcNow;
                var result = fixture.Service.RestoreActiveTargetsBestEffort(System.TimeSpan.FromMilliseconds(1500));
                var elapsed = System.DateTime.UtcNow - startedAt;

                Assert.Equal(3, result.Total);
                // 至少为每扇门提交一次（投递数应覆盖全部 3 扇门，不是只有第一扇）。
                Assert.True(fixture.Gateway.Calls.Count(c => CommandOf(c) == GateControlCommand.Restore) >= 3,
                    "至少为每扇门提交一次 best-effort 恢复。");
                // 总耗时受全局超时约束（每门最多 slice，全局 1500ms）。
                Assert.True(elapsed < System.TimeSpan.FromSeconds(8), "best-effort 恢复总耗时应受全局超时约束。");
            }
        }

        [TestCase]
        public static void Stage9Service_ScanLoopSubmitRestore_DoesNotBlockOnWorkerExecution()
        {
            // AIOP-05：扫描循环 SubmitRestore 改为异步提交/完成回调，恢复执行慢不能卡住 ProcessRestoreRetries 返回。
            using (var fixture = new Stage9Fixture(windowSeconds: 5, restoreRetryIntervalMs: 1000))
            {
                // 设备执行慢（远大于扫描间隔）。
                fixture.Gateway.ConfigureDelay("ControlGatewayAsync", System.TimeSpan.FromMilliseconds(800));
                var t0 = new DateTime(2026, 1, 1, 8, 0, 0);

                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(t0);

                var startedAt = System.DateTime.UtcNow;
                var expireResult = fixture.Service.ExpireWindows(t0.AddSeconds(5));
                var elapsed = System.DateTime.UtcNow - startedAt;

                Assert.Equal(1, expireResult.RestoreSubmissions);
                // ExpireWindows 必须立即返回（异步提交），不等 worker 执行完成。
                Assert.True(elapsed < System.TimeSpan.FromMilliseconds(300),
                    "ExpireWindows 调用 SubmitRestore 不能同步等待 worker 执行完成。");
            }
        }

        private static GateControlRequest AlwaysCloseRequest(Stage9Fixture fixture)
        {
            return fixture.Gateway.Calls
                .Where(c => c.MethodName == "ControlGatewayAsync" && ((GateControlRequest)c.Request).Command == GateControlCommand.AlwaysClose)
                .Select(c => (GateControlRequest)c.Request)
                .FirstOrDefault();
        }

        private static GateControlRequest RestoreRequest(Stage9Fixture fixture)
        {
            return fixture.Gateway.Calls
                .Where(c => c.MethodName == "ControlGatewayAsync" && ((GateControlRequest)c.Request).Command == GateControlCommand.Restore)
                .Select(c => (GateControlRequest)c.Request)
                .FirstOrDefault();
        }

        private static GateControlCommand CommandOf(MockGatewayCall call)
        {
            if (call.MethodName != "ControlGatewayAsync")
            {
                return GateControlCommand.Open;
            }

            return ((GateControlRequest)call.Request).Command;
        }

        private static int RestoreCount(Stage9Fixture fixture)
        {
            return fixture.Gateway.Calls.Count(c => CommandOf(c) == GateControlCommand.Restore);
        }

        private static void SetHostField(ControlDoorHost host, string fieldName, object value)
        {
            var field = typeof(ControlDoorHost).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field.SetValue(host, value);
        }

        private static void SpinUntil(Func<bool> condition, string failureMessage)
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                System.Threading.Thread.Sleep(5);
            }

            Assert.True(false, failureMessage);
        }

        private static string ExtractField(string text, string field)
        {
            var prefix = field + "=\"";
            var start = text.IndexOf(prefix, StringComparison.Ordinal);
            if (start < 0)
            {
                return string.Empty;
            }

            start += prefix.Length;
            var end = text.IndexOf("\"", start, StringComparison.Ordinal);
            return end < 0 ? string.Empty : text.Substring(start, end - start);
        }

        private static int CountOccurrences(string text, string value)
        {
            var count = 0;
            var index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
        }

        private static byte[] BuildAiopBuffer(string json)
        {
            const int headerLen = 376;
            var jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);
            var image = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
            var header = new byte[headerLen];
            WriteUInt(header, 0, headerLen);
            WriteUInt(header, 88, (uint)jsonBytes.Length);
            WriteUInt(header, 92, (uint)image.Length);

            var buffer = new byte[headerLen + jsonBytes.Length + image.Length];
            Buffer.BlockCopy(header, 0, buffer, 0, headerLen);
            Buffer.BlockCopy(jsonBytes, 0, buffer, headerLen, jsonBytes.Length);
            Buffer.BlockCopy(image, 0, buffer, headerLen + jsonBytes.Length, image.Length);
            return buffer;
        }

        private static void WriteUInt(byte[] buffer, int offset, uint value)
        {
            Buffer.BlockCopy(BitConverter.GetBytes(value), 0, buffer, offset, 4);
        }
    }
}
