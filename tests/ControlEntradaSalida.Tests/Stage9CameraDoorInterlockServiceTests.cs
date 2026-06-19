using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ControlDoor.CameraDoorInterlock;
using ControlDoor.Configuration;
using ControlDoor.Hikvision;
using ControlDoor.Observability;

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
