using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ControlDoor.Hikvision;
using ControlDoor.Permissions;

namespace ControlEntradaSalida.Tests
{
    public static class Stage6RetryExecutionCoordinatorTests
    {
        [TestCase]
        public static void RetryExecutionCoordinator_NullPlan_Throws()
        {
            using (var inner = new Stage4Fixture())
            {
                var coordinator = new RetryExecutionCoordinator(inner.Dispatcher, inner.Gateway);

                Stage3TestReflection.Expect<ArgumentNullException>(() =>
                    coordinator.ExecuteAsync(null, "stage6-null-plan", CancellationToken.None).GetAwaiter().GetResult());
            }
        }

        [TestCase]
        public static void RetryExecutionCoordinator_UnsupportedDevice_MarksNonRetryableTerminal()
        {
            using (var fixture = new Stage6Fixture())
            {
                fixture.AddOnlineDevice();
                fixture.Gateway.ConfigureException(
                    "UploadFaceAsync",
                    new DeviceGatewayException("UploadFace", SdkError.FromCode(23, "设备不支持该功能")));
                fixture.Database.QueryRows.Add(Row(
                    id: 1,
                    deviceId: 1,
                    employeeId: "10001",
                    facePending: true,
                    facePayload: @"{""employee_id"":""10001"",""face_image_base64"":""" + Stage5TestData.JpegBase64() + @"""}"));

                var result = fixture.Manager.RunOnceAsync("stage6-unsupported").GetAwaiter().GetResult();

                Assert.Equal(1, result.Terminal);
                Assert.Equal(0, result.Failed);
                Assert.True(fixture.Gateway.Calls.Any(call => call.MethodName == "UploadFaceAsync"));
                Assert.True(fixture.Database.Commands.Any(item =>
                    item.OperationName == "DeviceOperationRetryStore.MarkTerminalFailure" &&
                    item.CommandText.Contains("DEVICE_UNSUPPORTED")));
            }
        }

        private static IReadOnlyDictionary<string, object> Row(
            long id,
            int deviceId,
            string employeeId,
            int? permissionLevel = null,
            bool permissionPending = false,
            bool personPending = false,
            string personPayload = null,
            bool facePending = false,
            string facePayload = null,
            bool deletePersonPending = false,
            bool deleteFacePending = false,
            int attemptCount = 0)
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = id,
                ["device_id"] = deviceId,
                ["employee_id"] = employeeId,
                ["permission_level"] = permissionLevel,
                ["permission_pending"] = permissionPending,
                ["permission_sync_completion_blocked"] = permissionPending,
                ["person_payload"] = personPayload,
                ["person_pending"] = personPending,
                ["face_payload"] = facePayload,
                ["face_pending"] = facePending,
                ["delete_person_pending"] = deletePersonPending,
                ["delete_face_pending"] = deleteFacePending,
                ["attempt_count"] = attemptCount,
                ["next_retry_at"] = new DateTime(2026, 1, 1),
                ["last_error"] = null,
                ["last_attempt_at"] = null,
                ["exhausted_at"] = null,
                ["created_at"] = new DateTime(2026, 1, 1),
                ["updated_at"] = new DateTime(2026, 1, 1)
            };
        }
    }
}
