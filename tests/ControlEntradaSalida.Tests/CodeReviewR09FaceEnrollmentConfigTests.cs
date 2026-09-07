using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ControlDoor.Configuration;
using ControlDoor.Devices.Management;
using ControlDoor.GrpcApi;
using ControlDoor.Hikvision;
using ControlDoor.Permissions;

namespace ControlEntradaSalida.Tests
{
    // 代码复核 R09：FaceEnrollment 的图片上限、采集超时和任务保留配置必须接入实际执行。
    public static class CodeReviewR09FaceEnrollmentConfigTests
    {
        [TestCase]
        public static void PermissionSyncService_NonDefaultMaxFaceImageBytes_RejectsOversizedCapture()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice(deviceId: 1, types: new[] { DeviceType.FaceCapture });
                var service = new PermissionSyncGrpcService(
                    fixture.Registry,
                    fixture.Dispatcher,
                    fixture.Gateway,
                    fixture.RetryWriter,
                    fixture.UserWriter,
                    fixture.EnrollmentStore,
                    null,
                    null,
                    null,
                    new FaceEnrollmentOptions { MaxFaceImageBytes = 1024 });

                // mock 默认抓拍 6 字节，改成 2KB 的合法 JPEG 触发上限校验。
                fixture.Gateway.PictureBytes = new byte[2048];
                fixture.Gateway.PictureBytes[0] = 0xFF;
                fixture.Gateway.PictureBytes[1] = 0xD8;
                fixture.Gateway.PictureBytes[2046] = 0xFF;
                fixture.Gateway.PictureBytes[2047] = 0xD9;

                var frames = service.CaptureFaceStream(@"{""employee_id"":""10001""}", fixture.Context("r09-too-large")).ToList();
                var response = fixture.Response(frames[frames.Count - 1]);

                Assert.Equal("FACE_TOO_LARGE", response["code"]);
                Assert.Contains("超过 1KB", (string)response["message"]);
            }
        }

        [TestCase]
        public static void PermissionSyncService_NonDefaultMaxFaceImageBytes_RejectsOversizedUpload()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice(deviceId: 1, types: new[] { DeviceType.FaceCapture });
                var service = new PermissionSyncGrpcService(
                    fixture.Registry,
                    fixture.Dispatcher,
                    fixture.Gateway,
                    fixture.RetryWriter,
                    fixture.UserWriter,
                    fixture.EnrollmentStore,
                    null,
                    null,
                    null,
                    new FaceEnrollmentOptions { MaxFaceImageBytes = 1024 });

                var oversized = Convert.ToBase64String(new byte[2048]);
                var response = fixture.Response(service.SyncFacesToDevices(
                    @"{""device_ids"":[1],""items"":[{""employee_id"":""10001"",""face_image_base64"":""" + oversized + @"""}]}",
                    fixture.Context("r09-upload-too-large")));

                Assert.Equal("FACE_TOO_LARGE", response["code"]);
            }
        }

        [TestCase]
        public static void HikvisionSdkWrapper_CaptureTimeoutSeconds_DerivesPollingAttempts()
        {
            var sixtySeconds = new Stage3FakeNativeClient();
            var quarterSecond = new Stage3FakeNativeClient();
            var unspecified = new Stage3FakeNativeClient();
            sixtySeconds.FaceCaptureStatus = 1002;
            quarterSecond.FaceCaptureStatus = 1002;
            unspecified.FaceCaptureStatus = 1002;

            using (var wrapper60 = new HikvisionSdkWrapper(sixtySeconds, 60000))
            using (var wrapperQuarter = new HikvisionSdkWrapper(quarterSecond, 250))
            using (var wrapperDefault = new HikvisionSdkWrapper(unspecified))
            {
                TryCapture(wrapper60);
                TryCapture(wrapperQuarter);
                TryCapture(wrapperDefault);

                Assert.Equal(600, sixtySeconds.LastFaceCaptureMaxAttempts);
                Assert.Equal(3, quarterSecond.LastFaceCaptureMaxAttempts);
                Assert.Equal(100, unspecified.LastFaceCaptureMaxAttempts);
            }
        }

        [TestCase]
        public static void EnrollmentTaskStore_ConfiguredRetention_RemovesExpiredCompletedTasks()
        {
            var store = new EnrollmentTaskStore(retention: TimeSpan.FromMilliseconds(1));
            store.Start("r09-task", "10001");
            store.Succeed("r09-task", "done");
            Thread.Sleep(20);

            // 清理在访问时惰性执行：保留期过后完成任务不再可见。
            Assert.True(store.GetByTaskId("r09-task") == null, "保留期过后完成任务应被清理。");
        }

        private static void TryCapture(HikvisionSdkWrapper wrapper)
        {
            try
            {
                wrapper.CaptureFaceAsync(new CaptureRequest { UserId = 1 }).GetAwaiter().GetResult();
            }
            catch (DeviceGatewayException)
            {
                // 状态 1002（无有效人脸）仅用于让调用快速返回，轮询参数已被记录。
            }
        }
    }
}
