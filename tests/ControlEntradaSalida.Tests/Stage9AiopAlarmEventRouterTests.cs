using System.Collections.Generic;
using ControlDoor.CameraDoorInterlock;
using ControlDoor.Configuration;
using ControlDoor.Devices.Runtime;
using ControlDoor.Hikvision;

namespace ControlEntradaSalida.Tests
{
    public static class Stage9AiopAlarmEventRouterTests
    {
        [TestCase]
        public static void Stage9Router_AiopFromConfiguredCameraIp_IsAccepted()
        {
            var registry = new DeviceRuntimeRegistry();
            var sink = new RecordingAiopAlarmEventSink();
            var resolver = new InterlockMappingResolver(EnabledOptions("10.0.0.5", "10.0.0.10"), registry);
            var router = new AiopAlarmEventRouter(registry, sink, EnabledOptions("10.0.0.5", "10.0.0.10"), resolver);

            var result = router.Route(new AlarmEventData
            {
                Command = AiopAlarmEventRouter.CommUploadAiopVideo,
                EventType = "COMM_UPLOAD_AIOP_VIDEO",
                DeviceIpAddress = "10.0.0.5",
                RawPayload = new byte[] { 1, 2, 3 }
            });

            Assert.True(result.Accepted, result.Code);
            Assert.Equal(1, sink.Events.Count);
            Assert.Equal("10.0.0.5", sink.Events[0].CameraKey);
            Assert.Equal(AiopAlarmEventRouter.CommUploadAiopVideo, sink.Events[0].Command);
        }

        [TestCase]
        public static void Stage9Router_NonAiopCommand_IsIgnored()
        {
            var registry = new DeviceRuntimeRegistry();
            var sink = new RecordingAiopAlarmEventSink();
            var resolver = new InterlockMappingResolver(EnabledOptions("10.0.0.5", "10.0.0.10"), registry);
            var router = new AiopAlarmEventRouter(registry, sink, EnabledOptions("10.0.0.5", "10.0.0.10"), resolver);

            var result = router.Route(new AlarmEventData { Command = 0x5002, DeviceIpAddress = "10.0.0.5" });

            Assert.False(result.Accepted);
            Assert.Contains("IGNORED_NON_AIOP", result.Code);
            Assert.Equal(0, sink.Events.Count);
        }

        [TestCase]
        public static void Stage9Router_UnconfiguredCamera_IsIgnored()
        {
            var registry = new DeviceRuntimeRegistry();
            var sink = new RecordingAiopAlarmEventSink();
            var resolver = new InterlockMappingResolver(EnabledOptions("10.0.0.5", "10.0.0.10"), registry);
            var router = new AiopAlarmEventRouter(registry, sink, EnabledOptions("10.0.0.5", "10.0.0.10"), resolver);

            var result = router.Route(new AlarmEventData
            {
                Command = AiopAlarmEventRouter.CommUploadAiopVideo,
                DeviceIpAddress = "10.0.0.99"
            });

            Assert.False(result.Accepted);
            Assert.Contains("IGNORED_UNCONFIGURED_CAMERA", result.Code);
            Assert.Equal(0, sink.Events.Count);
        }

        [TestCase]
        public static void Stage9Router_Disabled_RejectsWithDisabledCode()
        {
            var registry = new DeviceRuntimeRegistry();
            var sink = new RecordingAiopAlarmEventSink();
            var disabledOptions = EnabledOptions("10.0.0.5", "10.0.0.10");
            disabledOptions.Enabled = false;
            var resolver = new InterlockMappingResolver(EnabledOptions("10.0.0.5", "10.0.0.10"), registry);
            var router = new AiopAlarmEventRouter(registry, sink, disabledOptions, resolver);

            var result = router.Route(new AlarmEventData
            {
                Command = AiopAlarmEventRouter.CommUploadAiopVideo,
                DeviceIpAddress = "10.0.0.5"
            });

            Assert.False(result.Accepted);
            Assert.Contains("DISABLED", result.Code);
        }

        [TestCase]
        public static void Stage9Router_UserIdFallback_IdentifiesCameraWhenIpMissing()
        {
            var registry = new DeviceRuntimeRegistry();
            registry.Register(new DeviceRuntimeCreationOptions
            {
                DeviceId = 51,
                DeviceName = "摄像头",
                IpAddress = "10.0.0.5",
                Port = 8000,
                Username = "admin",
                Password = "12345",
                Enabled = true,
                CreatedAt = new System.DateTime(2026, 1, 1)
            });
            registry.RegisterSdkUserId(51, 42, "CAM-SN-51", new System.DateTime(2026, 1, 1));

            var sink = new RecordingAiopAlarmEventSink();
            var resolver = new InterlockMappingResolver(EnabledOptions("10.0.0.5", "10.0.0.10"), registry);
            var router = new AiopAlarmEventRouter(registry, sink, EnabledOptions("10.0.0.5", "10.0.0.10"), resolver);

            var result = router.Route(new AlarmEventData
            {
                Command = AiopAlarmEventRouter.CommUploadAiopVideo,
                DeviceIpAddress = "",
                UserId = 42
            });

            Assert.True(result.Accepted, result.Code);
            Assert.Equal(1, sink.Events.Count);
            Assert.Equal("10.0.0.5", sink.Events[0].CameraKey);
            Assert.Equal(51, sink.Events[0].CameraDeviceId);
        }

        [TestCase]
        public static void Stage9Router_AttachedToGateway_RoutesEmittedAlarms()
        {
            var registry = new DeviceRuntimeRegistry();
            var sink = new RecordingAiopAlarmEventSink();
            var resolver = new InterlockMappingResolver(EnabledOptions("10.0.0.5", "10.0.0.10"), registry);
            var router = new AiopAlarmEventRouter(registry, sink, EnabledOptions("10.0.0.5", "10.0.0.10"), resolver);
            var gateway = new MockHikvisionGateway();
            router.Attach(gateway);

            gateway.EmitAlarm(new AlarmEventData
            {
                Command = AiopAlarmEventRouter.CommUploadAiopVideo,
                EventType = "COMM_UPLOAD_AIOP_VIDEO",
                DeviceIpAddress = "10.0.0.5"
            });

            Assert.Equal(1, sink.Events.Count);
            router.Dispose();
            gateway.Dispose();
        }

        [TestCase]
        public static void Stage9Router_SinkRejected_ReturnsRejectionAndDoesNotThrow()
        {
            var registry = new DeviceRuntimeRegistry();
            var sink = new RecordingAiopAlarmEventSink { Accept = false };
            var resolver = new InterlockMappingResolver(EnabledOptions("10.0.0.5", "10.0.0.10"), registry);
            var router = new AiopAlarmEventRouter(registry, sink, EnabledOptions("10.0.0.5", "10.0.0.10"), resolver);

            var result = router.Route(new AlarmEventData
            {
                Command = AiopAlarmEventRouter.CommUploadAiopVideo,
                DeviceIpAddress = "10.0.0.5"
            });

            Assert.False(result.Accepted);
            Assert.Equal(1, sink.Events.Count);
        }

        private static CameraAlarmDoorInterlockOptions EnabledOptions(string cameraIp, string doorIp)
        {
            return new CameraAlarmDoorInterlockOptions
            {
                Enabled = true,
                WindowSeconds = 5,
                Mappings = new List<CameraAlarmDoorInterlockMapping>
                {
                    new CameraAlarmDoorInterlockMapping
                    {
                        Camera = new InterlockCamera { Ip = cameraIp },
                        DoorDevice = new InterlockDoorDevice { Ip = doorIp },
                        DoorNos = new List<int> { 1 }
                    }
                }
            };
        }
    }

    internal sealed class RecordingAiopAlarmEventSink : IAiopAlarmEventSink
    {
        private readonly object gate = new object();
        private readonly List<RawAiopAlarmEvent> events = new List<RawAiopAlarmEvent>();

        public bool Accept { get; set; } = true;

        public IReadOnlyList<RawAiopAlarmEvent> Events
        {
            get
            {
                lock (gate)
                {
                    return events;
                }
            }
        }

        public AiopAlarmEnqueueResult TryEnqueue(RawAiopAlarmEvent alarmEvent)
        {
            lock (gate)
            {
                events.Add(alarmEvent);
                if (Accept)
                {
                    return AiopAlarmEnqueueResult.AcceptedResult(events.Count, 1000);
                }

                return AiopAlarmEnqueueResult.Rejected("QUEUE_FULL", "queue full", events.Count, 1000);
            }
        }
    }
}
