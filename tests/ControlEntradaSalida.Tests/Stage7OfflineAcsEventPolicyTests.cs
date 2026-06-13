using System;
using System.Linq;
using ControlDoor.Configuration;
using ControlDoor.Devices.Runtime;
using ControlDoor.FaceEvents;
using ControlDoor.Hikvision;

namespace ControlEntradaSalida.Tests
{
    public static class Stage7OfflineAcsEventPolicyTests
    {
        [TestCase]
        public static void OfflineAcsEventPolicy_CurrentEventFlagTwo_IsOfflineUpload()
        {
            var policy = new OfflineAcsEventPolicy(new FaceEventLoggingOptions());

            Assert.True(policy.IsOfflineUpload(2));
            Assert.False(policy.IsOfflineUpload(0));
        }

        [TestCase]
        public static void AcsAlarmEventRouter_OfflineCompensationDisabled_IgnoresOfflineUploadOnly()
        {
            var registry = NewRegistry();
            var sink = new RecordingRawAcsAlarmEventSink();
            var router = new AcsAlarmEventRouter(
                registry,
                sink,
                new FaceEventLoggingOptions { OfflineCompensationEnabled = false });

            var offline = router.Route(new AlarmEventData
            {
                Command = AcsAlarmEventRouter.CommAlarmAcs,
                DeviceIpAddress = "192.168.1.10",
                CurrentEventFlag = 2
            });
            var realtime = router.Route(new AlarmEventData
            {
                Command = AcsAlarmEventRouter.CommAlarmAcs,
                DeviceIpAddress = "192.168.1.10",
                CurrentEventFlag = 0
            });

            Assert.False(offline.Accepted);
            Assert.Equal("OFFLINE_COMPENSATION_DISABLED", offline.Code);
            Assert.True(realtime.Accepted);
            Assert.Equal(1, sink.Events.Count);
            Assert.Equal(AcsAlarmEventSource.Realtime, sink.Events.Single().Source);
        }

        [TestCase]
        public static void AcsAlarmEventRouter_ExcludedDevice_DoesNotEnqueue()
        {
            var registry = NewRegistry();
            var sink = new RecordingRawAcsAlarmEventSink();
            var router = new AcsAlarmEventRouter(
                registry,
                sink,
                new FaceEventLoggingOptions { ExcludedDeviceIds = { 7 } });

            var result = router.Route(new AlarmEventData
            {
                Command = AcsAlarmEventRouter.CommAlarmAcs,
                DeviceIpAddress = "192.168.1.10"
            });

            Assert.False(result.Accepted);
            Assert.Equal("EXCLUDED_DEVICE_ID", result.Code);
            Assert.Equal(0, sink.Events.Count);
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
