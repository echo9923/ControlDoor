using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ControlDoor.Hikvision;
using ControlDoor.Observability;
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
        public static void RetryExecutionCoordinator_Submit_SeparatesWaitOutcomeFromFinalCompletion()
        {
            using (var inner = new Stage4Fixture(defaultTaskTimeoutMilliseconds: 200))
            {
                inner.AddRecord(1);
                inner.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                var login = inner.Lifecycle.SubmitLogin(1, wait: true, requestId: "stage6-wait-login");
                Assert.True(login.Success, login.Message);
                inner.Gateway.ConfigureDelay("UploadFaceAsync", TimeSpan.FromMilliseconds(500));

                var state = new DeviceOperationRetryState
                {
                    Id = 99,
                    DeviceId = 1,
                    EmployeeId = "10001",
                    FacePending = true,
                    FacePayloadJson = @"{""employee_id"":""10001"",""face_image_base64"":""" + Stage5TestData.JpegBase64() + @"""}"
                };
                var plan = new RetryCommandPlanner().Plan(state);
                var handle = new RetryExecutionCoordinator(inner.Dispatcher, inner.Gateway)
                    .Submit(plan, "stage6-wait-outcome");
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (DateTime.UtcNow < deadline && !inner.Gateway.Calls.Any(call => call.MethodName == "UploadFaceAsync"))
                {
                    Thread.Sleep(10);
                }

                Assert.True(inner.Gateway.Calls.Any(call => call.MethodName == "UploadFaceAsync"), "retry operation did not start.");
                var waitResult = handle.WaitResult.GetAwaiter().GetResult();
                var finalResult = handle.FinalResult.GetAwaiter().GetResult();

                Assert.True(waitResult.IsWaitOutcome);
                Assert.False(finalResult.IsWaitOutcome);
                Assert.True(finalResult.TaskStarted);
                Assert.Equal("TIMEOUT", finalResult.Code);
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

        [TestCase]
        public static void RetryExecutionCoordinator_DeletePersonFaceFailure_StillDeletesPersonAndWritesWarnLog()
        {
            using (var fixture = new Stage6Fixture())
            {
                fixture.AddOnlineDevice();
                fixture.Gateway.ConfigureException("DeleteFaceAsync", new InvalidOperationException("face delete failed"));
                fixture.Database.QueryRows.Add(Row(id: 2, deviceId: 1, employeeId: "10001", deletePersonPending: true));

                var result = fixture.Manager.RunOnceAsync("stage6-delete-person-face-timeout").GetAwaiter().GetResult();

                Assert.Equal(1, result.Succeeded);
                Assert.Equal(0, result.Failed);
                Assert.True(fixture.Gateway.Calls.Any(call => call.MethodName == "DeleteFaceAsync"));
                Assert.True(fixture.Gateway.Calls.Any(call => call.MethodName == "DeletePersonAsync"));
                Assert.False(fixture.Database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.ScheduleRetry"));
                Assert.True(fixture.Database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.DeleteIfCompleted"));
                var log = fixture.ReadLog();
                Assert.Contains("level=Warn", log);
                Assert.Contains("operationName=\"RetryDeleteFaceBeforePerson\"", log);
                Assert.Contains("employeeId=\"10001\"", log);
                Assert.Contains("InvalidOperationException", log);
                Assert.Contains("face delete failed", log);
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
