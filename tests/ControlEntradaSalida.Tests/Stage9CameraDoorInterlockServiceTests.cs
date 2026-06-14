using System;
using System.Collections.Generic;
using System.Linq;
using ControlDoor.CameraDoorInterlock;
using ControlDoor.Configuration;
using ControlDoor.Hikvision;

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
