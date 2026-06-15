using System;
using System.Linq;
using ControlDoor.Devices.Management;
using ControlDoor.Hikvision;

namespace ControlEntradaSalida.Tests
{
    public static class Stage5EnrollmentTests
    {
        [TestCase]
        public static void CaptureFaceStream_MissingEmployee_ReturnsInvalidArgumentFrame()
        {
            using (var fixture = new Stage5Fixture())
            {
                var frames = fixture.Service.CaptureFaceStream(@"{}", fixture.Context("capture-missing-employee")).ToList();
                var frame = fixture.Response(frames[0]);

                Assert.Equal(1, frames.Count);
                Assert.Equal(false, frame["success"]);
                Assert.Equal("INVALID_ARGUMENT", frame["code"]);
            }
        }

        [TestCase]
        public static void CaptureFaceStream_GatewayFailure_RecordsFailedEnrollmentStatus()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice(types: new[] { DeviceType.FaceCapture });
                fixture.Gateway.ConfigureException("CaptureFaceAsync", new DeviceGatewayException("CaptureFace", SdkError.FromCode(7, "session lost")));

                var frame = fixture.Response(fixture.Service.CaptureFaceStream(@"{""employee_id"":""10001""}", fixture.Context("capture-failure")).First());
                var status = fixture.Response(fixture.Service.GetEnrollmentStatus(
                    @"{""taskId"":""" + Convert.ToString(frame["taskId"]) + @"""}",
                    fixture.Context("capture-failure-status")));

                Assert.Equal(false, frame["success"]);
                Assert.Equal("SDK_ERROR", frame["code"]);
                Assert.Equal("OK", status["code"]);
                Assert.Equal("Failed", status["status"]);
                Assert.Equal("SDK_ERROR", status["errorCode"]);
            }
        }

        [TestCase]
        public static void GetEnrollmentStatus_WithTaskId_ReturnsSucceededTask()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice(types: new[] { DeviceType.FaceCapture });

                var frame = fixture.Response(fixture.Service.CaptureFaceStream(@"{""employee_id"":""10001""}", fixture.Context("capture-status-task")).First());
                var status = fixture.Response(fixture.Service.GetEnrollmentStatus(
                    @"{""task_id"":""" + Convert.ToString(frame["taskId"]) + @"""}",
                    fixture.Context("capture-status-task-query")));

                Assert.Equal("OK", frame["code"]);
                Assert.Equal("OK", status["code"]);
                Assert.Equal(Convert.ToString(frame["taskId"]), Convert.ToString(status["taskId"]));
                Assert.Equal("10001", status["employeeId"]);
                Assert.Equal("Succeeded", status["status"]);
            }
        }

        [TestCase]
        public static void CaptureFaceStream_ImageTooLarge_RecordsFailedEnrollmentStatus()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice(types: new[] { DeviceType.FaceCapture });
                fixture.Gateway.ConfigureResult("CaptureFaceAsync", new FaceCaptureResult
                {
                    ImageBytes = new byte[205 * 1024],
                    ContentType = "image/jpeg",
                    FaceDetected = true,
                    QualityScore = 91
                });

                var frame = fixture.Response(fixture.Service.CaptureFaceStream(@"{""employee_id"":""10001""}", fixture.Context("capture-large")).First());
                var status = fixture.Response(fixture.Service.GetEnrollmentStatus(
                    @"{""employee_id"":""10001""}",
                    fixture.Context("capture-large-status")));

                Assert.Equal(false, frame["success"]);
                Assert.Equal("FACE_TOO_LARGE", frame["code"]);
                Assert.Equal("OK", status["code"]);
                Assert.Equal("Failed", status["status"]);
                Assert.Equal("FACE_TOO_LARGE", status["errorCode"]);
            }
        }
    }
}
