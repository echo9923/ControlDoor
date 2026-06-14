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

        // ===== 人脸验证类 minor 过滤（与 main 项目 IsFaceVerifyMinor 对齐）=====

        [TestCase]
        public static void AcsAlarmEventRouter_FaceVerifyPass_IsAccepted()
        {
            var registry = NewRegistry();
            var sink = new RecordingRawAcsAlarmEventSink();
            var router = new AcsAlarmEventRouter(registry, sink);

            var result = router.Route(NewAcsEventWithMinor("75")); // 0x4B 人脸验证通过

            Assert.True(result.Accepted);
            Assert.Equal(1, sink.Events.Count);
        }

        [TestCase]
        public static void AcsAlarmEventRouter_FaceVerifyFail_IsAccepted()
        {
            var registry = NewRegistry();
            var sink = new RecordingRawAcsAlarmEventSink();
            var router = new AcsAlarmEventRouter(registry, sink);

            var result = router.Route(NewAcsEventWithMinor("76")); // 0x4C 人脸验证失败

            Assert.True(result.Accepted);
            Assert.Equal(1, sink.Events.Count);
        }

        [TestCase]
        public static void AcsAlarmEventRouter_CombinedVerifyRange_IsAccepted()
        {
            var registry = NewRegistry();
            var sink = new RecordingRawAcsAlarmEventSink();
            var router = new AcsAlarmEventRouter(registry, sink);

            // 0x3C~0x44 组合验证范围两端都应放行
            Assert.True(router.Route(NewAcsEventWithMinor("60")).Accepted); // 0x3C
            Assert.True(router.Route(NewAcsEventWithMinor("68")).Accepted); // 0x44
            Assert.Equal(2, sink.Events.Count);
        }

        [TestCase]
        public static void AcsAlarmEventRouter_NonFaceVerifyMinor_IsRejected()
        {
            var registry = NewRegistry();
            var sink = new RecordingRawAcsAlarmEventSink();
            var router = new AcsAlarmEventRouter(registry, sink);

            // 0x01 纯刷卡、0x26 旧版刷脸成功、0x65 工号+人脸 —— 都不在人脸验证类范围，应拒绝
            Assert.False(router.Route(NewAcsEventWithMinor("1")).Accepted);   // 0x01
            Assert.False(router.Route(NewAcsEventWithMinor("38")).Accepted);  // 0x26
            Assert.False(router.Route(NewAcsEventWithMinor("101")).Accepted); // 0x65
            Assert.Equal(0, sink.Events.Count);

            // 拒绝码应为 IGNORED_NON_FACE_VERIFY
            var result = router.Route(NewAcsEventWithMinor("1"));
            Assert.False(result.Accepted);
            Assert.Equal("IGNORED_NON_FACE_VERIFY", result.Code);
        }

        [TestCase]
        public static void AcsAlarmEventRouter_MissingMinor_IsAcceptedForCompatibility()
        {
            // dwMinor 缺失时保守放行，避免格式异常的人脸事件被误丢弃（向后兼容）。
            var registry = NewRegistry();
            var sink = new RecordingRawAcsAlarmEventSink();
            var router = new AcsAlarmEventRouter(registry, sink);

            var data = new AlarmEventData
            {
                Command = AcsAlarmEventRouter.CommAlarmAcs,
                EventType = "COMM_ALARM_ACS",
                DeviceIpAddress = "192.168.1.10"
                // 故意不设 Values["dwMinor"]
            };

            var result = router.Route(data);

            Assert.True(result.Accepted);
            Assert.Equal(1, sink.Events.Count);
        }

        private static AlarmEventData NewAcsEventWithMinor(string dwMinor)
        {
            var data = new AlarmEventData
            {
                Command = AcsAlarmEventRouter.CommAlarmAcs,
                EventType = "COMM_ALARM_ACS",
                DeviceIpAddress = "192.168.1.10"
            };
            data.Values["dwMinor"] = dwMinor;
            return data;
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
