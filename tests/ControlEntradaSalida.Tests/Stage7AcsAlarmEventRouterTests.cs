using System;
using System.Linq;
using ControlDoor.Configuration;
using ControlDoor.Devices.Runtime;
using ControlDoor.FaceEvents;
using ControlDoor.Hikvision;

namespace ControlEntradaSalida.Tests
{
    public static class Stage7AcsAlarmEventRouterTests
    {
        [TestCase]
        public static void AcsAlarmEventRouter_AcsCommand_EnqueuesWithIpLookup()
        {
            var registry = NewRegistry();
            var queue = new FaceEventIngestionService(new FaceEventLoggingOptions { QueueCapacity = 10 });
            var router = new AcsAlarmEventRouter(registry, queue);

            var result = router.Route(new AlarmEventData
            {
                Command = AcsAlarmEventRouter.CommAlarmAcs,
                EventType = "COMM_ALARM_ACS",
                DeviceIpAddress = "192.168.1.10",
                RawPayload = new byte[] { 1, 2, 3 },
                PictureBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }
            });

            Assert.True(result.Accepted);
            Assert.Equal(1, queue.Count);
        }

        [TestCase]
        public static void AcsAlarmEventRouter_NonAcsCommand_IsIgnored()
        {
            var registry = NewRegistry();
            var queue = new FaceEventIngestionService(new FaceEventLoggingOptions { QueueCapacity = 10 });
            var router = new AcsAlarmEventRouter(registry, queue);

            var result = router.Route(new AlarmEventData
            {
                Command = 0x4021,
                EventType = "COMM_UPLOAD_AIOP_VIDEO",
                DeviceIpAddress = "192.168.1.10"
            });

            Assert.False(result.Accepted);
            Assert.Equal("IGNORED_NON_ACS", result.Code);
            Assert.Equal(0, queue.Count);
        }

        [TestCase]
        public static void AcsAlarmEventRouter_UserIdLookup_WorksWhenIpMissing()
        {
            var registry = NewRegistry();
            registry.RegisterSdkUserId(7, 88, "SN-7", DateTime.Now);
            var sink = new RecordingRawAcsAlarmEventSink();
            var router = new AcsAlarmEventRouter(registry, sink);

            var result = router.Route(new AlarmEventData
            {
                Command = AcsAlarmEventRouter.CommAlarmAcs,
                UserId = 88
            });

            Assert.True(result.Accepted);
            Assert.Equal(7, sink.Events.Single().DeviceId);
        }

        [TestCase]
        public static void AcsAlarmEventRouter_AlarmHandleLookup_WorksWhenIpAndUserMissing()
        {
            var registry = NewRegistry();
            registry.RegisterAlarmHandle(7, 900, DateTime.Now);
            var sink = new RecordingRawAcsAlarmEventSink();
            var router = new AcsAlarmEventRouter(registry, sink);

            var result = router.Route(new AlarmEventData
            {
                Command = AcsAlarmEventRouter.CommAlarmAcs,
                UserId = -1,
                AlarmHandle = 900
            });

            Assert.True(result.Accepted);
            Assert.Equal(7, sink.Events.Single().DeviceId);
        }

        [TestCase]
        public static void AcsAlarmEventRouter_QueueFull_ReturnsRejectedWithoutThrowing()
        {
            var registry = NewRegistry();
            var queue = new FaceEventIngestionService(new FaceEventLoggingOptions { QueueCapacity = 1 });
            var router = new AcsAlarmEventRouter(registry, queue);

            var first = router.Route(new AlarmEventData { Command = AcsAlarmEventRouter.CommAlarmAcs, DeviceIpAddress = "192.168.1.10" });
            var second = router.Route(new AlarmEventData { Command = AcsAlarmEventRouter.CommAlarmAcs, DeviceIpAddress = "192.168.1.10" });

            Assert.True(first.Accepted);
            Assert.False(second.Accepted);
            Assert.Equal("QUEUE_FULL", second.Code);
        }

        [TestCase]
        public static void AcsAlarmEventRouter_CurrentEventFlagTwo_MarksOfflineUpload()
        {
            var registry = NewRegistry();
            var sink = new RecordingRawAcsAlarmEventSink();
            var router = new AcsAlarmEventRouter(registry, sink);

            var data = new AlarmEventData
            {
                Command = AcsAlarmEventRouter.CommAlarmAcs,
                DeviceIpAddress = "192.168.1.10",
                CurrentEventFlag = 2
            };

            var result = router.Route(data);

            Assert.True(result.Accepted);
            Assert.Equal(AcsAlarmEventSource.OfflineUpload, sink.Events.Single().Source);
            Assert.Equal(2, sink.Events.Single().CurrentEventFlag.Value);
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
    }
}
