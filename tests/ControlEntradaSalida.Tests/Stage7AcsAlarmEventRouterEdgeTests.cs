using System;
using System.Linq;
using ControlDoor.Configuration;
using ControlDoor.Devices.Runtime;
using ControlDoor.FaceEvents;
using ControlDoor.Hikvision;

namespace ControlEntradaSalida.Tests
{
    public static class Stage7AcsAlarmEventRouterEdgeTests
    {
        [TestCase]
        public static void AcsAlarmEventRouter_NullAlarmData_ReturnsInvalidArgument()
        {
            var registry = NewRegistry();
            var sink = new RecordingRawAcsAlarmEventSink();
            var router = new AcsAlarmEventRouter(registry, sink);

            var result = router.Route(null);

            Assert.False(result.Accepted);
            Assert.Equal("INVALID_ARGUMENT", result.Code);
            Assert.Equal(0, sink.Events.Count);
        }

        [TestCase]
        public static void AcsAlarmEventRouter_Disabled_ReturnsDisabledAndDoesNotEnqueue()
        {
            var registry = NewRegistry();
            var sink = new RecordingRawAcsAlarmEventSink();
            var router = new AcsAlarmEventRouter(
                registry,
                sink,
                new FaceEventLoggingOptions { Enabled = false });

            var result = router.Route(NewAcsAlarm(ip: "192.168.1.10"));

            Assert.False(result.Accepted);
            Assert.Equal("DISABLED", result.Code);
            Assert.Equal(0, sink.Events.Count);
        }

        [TestCase]
        public static void AcsAlarmEventRouter_ExcludedDeviceIp_DoesNotEnqueue()
        {
            var registry = NewRegistry();
            var sink = new RecordingRawAcsAlarmEventSink();
            var router = new AcsAlarmEventRouter(
                registry,
                sink,
                new FaceEventLoggingOptions { ExcludedDeviceIps = { "192.168.1.10" } });

            var result = router.Route(NewAcsAlarm(ip: "192.168.1.10"));

            Assert.False(result.Accepted);
            Assert.Equal("EXCLUDED_DEVICE_IP", result.Code);
            Assert.Equal(0, sink.Events.Count);
        }

        [TestCase]
        public static void AcsAlarmEventRouter_UnresolvedDevice_StillEnqueuesWithZeroDeviceId()
        {
            var registry = NewRegistry();
            var sink = new RecordingRawAcsAlarmEventSink();
            var router = new AcsAlarmEventRouter(registry, sink);

            var result = router.Route(NewAcsAlarm(ip: "9.9.9.9", userId: -1, alarmHandle: -1));

            Assert.True(result.Accepted);
            Assert.Equal(0, sink.Events.Single().DeviceId);
            Assert.Equal("9.9.9.9", sink.Events.Single().DeviceIp);
        }

        [TestCase]
        public static void AcsAlarmEventRouter_ByCurrentEventStringFallback_MarksOffline()
        {
            var registry = NewRegistry();
            var sink = new RecordingRawAcsAlarmEventSink();
            var router = new AcsAlarmEventRouter(registry, sink);

            var data = NewAcsAlarm(ip: "192.168.1.10");
            data.Values["byCurrentEvent"] = "2";

            var result = router.Route(data);

            Assert.True(result.Accepted);
            Assert.Equal(AcsAlarmEventSource.OfflineUpload, sink.Events.Single().Source);
            Assert.Equal(2, sink.Events.Single().CurrentEventFlag.Value);
        }

        [TestCase]
        public static void AcsAlarmEventRouter_CommandZeroWithAcsEventType_NormalizesAndEnqueues()
        {
            var registry = NewRegistry();
            var sink = new RecordingRawAcsAlarmEventSink();
            var router = new AcsAlarmEventRouter(registry, sink);

            var data = NewAcsAlarm(ip: "192.168.1.10");
            data.Command = 0;
            data.EventType = "COMM_ALARM_ACS";

            var result = router.Route(data);

            Assert.True(result.Accepted);
            Assert.Equal(AcsAlarmEventRouter.CommAlarmAcs, sink.Events.Single().Command);
        }

        [TestCase]
        public static void AcsAlarmEventRouter_ThrowingSink_ReturnsRouteErrorWithoutThrowing()
        {
            var registry = NewRegistry();
            var router = new AcsAlarmEventRouter(registry, new ThrowingSink());

            var result = router.Route(NewAcsAlarm(ip: "192.168.1.10"));

            Assert.False(result.Accepted);
            Assert.Equal("ROUTE_ERROR", result.Code);
        }

        [TestCase]
        public static void AcsAlarmEventRouter_Dispose_StopsRoutingEvents()
        {
            var registry = NewRegistry();
            var sink = new RecordingRawAcsAlarmEventSink();
            var gateway = new MockHikvisionGateway();
            var router = new AcsAlarmEventRouter(registry, sink);
            router.Attach(gateway);

            gateway.EmitAlarm(NewAcsAlarm(ip: "192.168.1.10"));
            Assert.Equal(1, sink.Events.Count);

            router.Dispose();

            gateway.EmitAlarm(NewAcsAlarm(ip: "192.168.1.10"));
            Assert.Equal(1, sink.Events.Count);
        }

        [TestCase]
        public static void AcsAlarmEventRouter_Reattach_DetachesPreviousGateway()
        {
            var registry = NewRegistry();
            var sink = new RecordingRawAcsAlarmEventSink();
            var gatewayA = new MockHikvisionGateway();
            var gatewayB = new MockHikvisionGateway();
            var router = new AcsAlarmEventRouter(registry, sink);

            router.Attach(gatewayA);
            gatewayA.EmitAlarm(NewAcsAlarm(ip: "192.168.1.10"));
            Assert.Equal(1, sink.Events.Count);

            router.Attach(gatewayB);
            gatewayA.EmitAlarm(NewAcsAlarm(ip: "192.168.1.10"));
            Assert.Equal(1, sink.Events.Count);

            gatewayB.EmitAlarm(NewAcsAlarm(ip: "192.168.1.10"));
            Assert.Equal(2, sink.Events.Count);
        }

        [TestCase]
        public static void AcsAlarmEventRouter_DefensivelyCopiesPictureBytes()
        {
            var registry = NewRegistry();
            var sink = new RecordingRawAcsAlarmEventSink();
            var router = new AcsAlarmEventRouter(registry, sink);

            var data = NewAcsAlarm(ip: "192.168.1.10");
            data.PictureBytes = new byte[] { 1, 2, 3 };

            router.Route(data);

            data.PictureBytes[0] = 9;

            var enqueued = sink.Events.Single().PictureBytes;
            Assert.True(enqueued.SequenceEqual(new byte[] { 1, 2, 3 }));
        }

        private static AlarmEventData NewAcsAlarm(string ip, int userId = -1, int alarmHandle = -1)
        {
            return new AlarmEventData
            {
                Command = AcsAlarmEventRouter.CommAlarmAcs,
                EventType = "COMM_ALARM_ACS",
                DeviceIpAddress = ip,
                UserId = userId,
                AlarmHandle = alarmHandle
            };
        }

        private static DeviceRuntimeRegistry NewRegistry()
        {
            var registry = new DeviceRuntimeRegistry(new DeviceRuntimeRegistryOptions { WorkerCount = 2 });
            registry.Register(new DeviceRuntimeCreationOptions
            {
                DeviceId = 7,
                DeviceName = "Front Gate",
                IpAddress = "192.168.1.10",
                Port = 8000,
                Username = "admin",
                Password = "pwd",
                Enabled = true,
                CreatedAt = DateTime.Now
            });
            return registry;
        }

        private sealed class ThrowingSink : IRawAcsAlarmEventSink
        {
            public FaceEventEnqueueResult TryEnqueue(RawAcsAlarmEvent alarmEvent)
            {
                throw new InvalidOperationException("sink exploded");
            }
        }
    }
}
