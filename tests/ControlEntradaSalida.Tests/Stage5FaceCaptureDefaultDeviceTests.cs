using System;
using System.Linq;
using ControlDoor.Devices.Management;
using ControlDoor.Hikvision;

namespace ControlEntradaSalida.Tests
{
    // 验证 CaptureFaceStream 的设备选择语义：
    //   1. 配置了 defaultFaceCaptureDeviceId 时，固定使用该设备（即便存在其他在线 FaceCapture 设备）。
    //   2. 默认设备离线时严格失败，错误码 DEVICE_ERROR，且不回退到其他在线设备。
    //   3. 未配置时维持"按 FaceCapture 类型取第一个在线设备"的旧行为。
    public static class Stage5FaceCaptureDefaultDeviceTests
    {
        private const string EmployeeId = "10001";

        [TestCase]
        public static void CaptureFace_DefaultDeviceConfigured_UsesOnlyThatDevice()
        {
            using (var fixture = new Stage5Fixture(defaultFaceCaptureDeviceId: 2))
            {
                // 两台在线 FaceCapture 设备，deviceId 升序下默认逻辑会取 1，但这里配置默认为 2。
                fixture.AddOnlineDevice(deviceId: 1, types: new[] { DeviceType.FaceCapture });
                fixture.AddOnlineDevice(deviceId: 2, types: new[] { DeviceType.FaceCapture });
                var expectedUserId = fixture.Registry.GetAllSnapshots().First(item => item.DeviceId == 2).SdkUserId;

                var frame = fixture.Response(fixture.Service.CaptureFaceStream(RequestJson(), fixture.Context("capture-default")).First());

                Assert.Equal("OK", frame["code"]);
                var captureCall = fixture.Gateway.Calls.Last(item => item.MethodName == "CaptureFaceAsync");
                Assert.Equal(expectedUserId.Value, ((CaptureRequest)captureCall.Request).UserId);
            }
        }

        [TestCase]
        public static void CaptureFace_DefaultDeviceOffline_StrictFailureNoFallback()
        {
            using (var fixture = new Stage5Fixture(defaultFaceCaptureDeviceId: 2))
            {
                // 默认设备 2 离线，另一台 1 在线 —— 必须严格失败，绝不回退到 1。
                fixture.AddOnlineDevice(deviceId: 1, types: new[] { DeviceType.FaceCapture });
                fixture.AddOfflineDevice(deviceId: 2, types: new[] { DeviceType.FaceCapture });

                var frame = fixture.Response(fixture.Service.CaptureFaceStream(RequestJson(), fixture.Context("capture-offline-default")).First());

                Assert.Equal(false, frame["success"]);
                Assert.Equal("DEVICE_ERROR", frame["code"]);
                Assert.False(fixture.Gateway.Calls.Any(item => item.MethodName == "CaptureFaceAsync"),
                    "默认设备离线时不应调用任何采集设备。");
            }
        }

        [TestCase]
        public static void CaptureFace_DefaultDeviceNotConfigured_FallsBackToLegacy()
        {
            using (var fixture = new Stage5Fixture())
            {
                // 未配置默认设备：维持旧行为，取 deviceId 最小的在线 FaceCapture 设备（=1）。
                fixture.AddOnlineDevice(deviceId: 1, types: new[] { DeviceType.FaceCapture });
                fixture.AddOnlineDevice(deviceId: 2, types: new[] { DeviceType.FaceCapture });
                var expectedUserId = fixture.Registry.GetAllSnapshots().First(item => item.DeviceId == 1).SdkUserId;

                var frame = fixture.Response(fixture.Service.CaptureFaceStream(RequestJson(), fixture.Context("capture-legacy")).First());

                Assert.Equal("OK", frame["code"]);
                var captureCall = fixture.Gateway.Calls.Last(item => item.MethodName == "CaptureFaceAsync");
                Assert.Equal(expectedUserId.Value, ((CaptureRequest)captureCall.Request).UserId);
            }
        }

        private static string RequestJson()
        {
            return @"{""employee_id"":""" + EmployeeId + @"""}";
        }
    }
}
