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
                // 恢复为异步投递（复核 R07），等待设备执行完成后再断言。
                SpinUntil(() => RestoreCount(fixture) >= 1, "最后一条报警后静默满 WindowSeconds 才应恢复。");
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
                // 恢复为异步投递（复核 R07/G4），等待设备执行完成后再断言指令内容。
                SpinUntil(() => RestoreRequest(fixture) != null, "恢复指令未被异步执行。");

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
                // 恢复为异步投递（复核 R07），等待两个门目标恢复完成。
                SpinUntil(() => fixture.Gateway.Calls.Count(c => CommandOf(c) == GateControlCommand.Restore) >= 2, "窗口结束后两个门都应恢复。");

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
                // 恢复为异步投递（复核 R07），等待最后一个摄像头窗口结束后的恢复完成。
                SpinUntil(() => RestoreCount(fixture) >= 1, "最后一个摄像头窗口结束才应恢复。");
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

                var text = System.IO.File.ReadAllText(logger.CurrentDiagnosticLogPath);
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
                fixture.SpinForControlGatewayCalls(1);
                fixture.Service.ExpireWindows(t0.AddSeconds(5));
                // 恢复为异步投递：以共享读取等待两条完成回调记录都落盘——常闭失败与恢复失败
                // 必须各自成行携带 sdkErrorCode=7，不能只看全文出现任一 sdkErrorCode（复核 G5）。
                SpinUntil(() =>
                {
                    var pending = ReadSharedLogText(logger.CurrentDiagnosticLogPath);
                    return FindLineWith(pending, "operationName=\"AlwaysClose\"", "sdkErrorCode=\"7\"") != null
                        && FindLineWith(pending, "operationName=\"RestoreDoor\"", "sdkErrorCode=\"7\"", "恢复任务失败") != null;
                }, "常闭/恢复失败回调日志未完整写入。");

                var text = ReadSharedLogText(logger.CurrentDiagnosticLogPath);
                var alwaysCloseLine = FindLineWith(text, "operationName=\"AlwaysClose\"", "sdkErrorCode=\"7\"");
                var restoreLine = FindLineWith(text, "operationName=\"RestoreDoor\"", "sdkErrorCode=\"7\"", "恢复任务失败");
                Assert.NotNull(alwaysCloseLine);
                Assert.NotNull(restoreLine);
                var interlockId = ExtractField(alwaysCloseLine, "interlockId");
                Assert.False(string.IsNullOrWhiteSpace(interlockId));
                Assert.Equal(interlockId, ExtractField(restoreLine, "interlockId"));
                Assert.True(CountOccurrences(text, "interlockId=\"" + interlockId + "\"") >= 4);
                Assert.Contains("deviceId=\"10\"", alwaysCloseLine);
                Assert.Contains("deviceId=\"10\"", restoreLine);
                Assert.Contains("doorNo=\"1\"", alwaysCloseLine);
                Assert.Contains("doorNo=\"1\"", restoreLine);
                Assert.Contains("sdkOperation=\"ControlGateway\"", alwaysCloseLine);
                Assert.Contains("sdkOperation=\"ControlGateway\"", restoreLine);
                Assert.Contains("retryable=\"True\"", restoreLine);
                Assert.Contains("manualActionRequired=\"False\"", restoreLine);
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
            var deadline = DateTime.UtcNow.AddSeconds(8);
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

        // 共享读取运行中的日志：独占式 File.ReadAllText 会与 RollingLogFile.Append 冲突（复核 G5）。
        // 轮转/占用瞬间按空内容返回，由外层自旋在下一轮重试。
        private static string ReadSharedLogText(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                return string.Empty;
            }

            try
            {
                using (var stream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
                using (var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (System.IO.IOException)
            {
                return string.Empty;
            }
            catch (UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }

        private static string FindLineWith(string text, params string[] tokens)
        {
            if (string.IsNullOrEmpty(text) || tokens == null || tokens.Length == 0)
            {
                return null;
            }

            foreach (var line in text.Split('\n'))
            {
                var matched = true;
                foreach (var token in tokens)
                {
                    if (!line.Contains(token))
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched)
                {
                    return line;
                }
            }

            return null;
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
