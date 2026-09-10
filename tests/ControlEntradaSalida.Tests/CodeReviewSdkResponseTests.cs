using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using ControlDoor.GrpcApi;
using ControlDoor.Hikvision;
using ControlDoor.Permissions;

namespace ControlEntradaSalida.Tests
{
    public static class CodeReviewSdkResponseTests
    {
        [TestCase]
        public static void PermissionSync_XmlFailure_DoesNotCompleteIntent()
        {
            AssertOnlineOutcome(XmlStatus(4, "Invalid Operation", "notSupport"), false, false);
        }

        [TestCase]
        public static void PermissionSync_XmlSuccess_CompletesIntent()
        {
            AssertOnlineOutcome(XmlStatus(1, "OK", "ok"), true, false);
        }

        [TestCase]
        public static void PermissionSync_JsonDeviceBusy_SchedulesRetry()
        {
            AssertOnlineOutcome("{\"statusCode\":2,\"statusString\":\"Device Busy\",\"subStatusCode\":\"deviceBusy\"}", false, true);
        }

        [TestCase]
        public static void PermissionSync_XmlDeviceBusy_SchedulesRetry()
        {
            AssertOnlineOutcome(XmlStatus(2, "Device Busy", "deviceBusy"), false, true);
        }

        [TestCase]
        public static void PermissionSync_MalformedResponse_PreservesIntentForRetry()
        {
            foreach (var body in new[] { "<ResponseStatus><statusCode>4", "{\"statusCode\":", "not a device response", "null" })
            {
                AssertOnlineOutcome(body, false, true);
            }
        }

        [TestCase]
        public static void PermissionSync_JsonSuccess_CompletesIntent()
        {
            AssertOnlineOutcome("{\"statusCode\":1,\"statusString\":\"OK\"}", true, false);
        }

        [TestCase]
        public static void PermissionSync_SdkCodeTwo_RemainsNonRetryable()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice();
                fixture.Gateway.ConfigureException("UpsertPersonAsync", new DeviceGatewayException("UpsertPerson", SdkError.FromCode(2)));
                var response = fixture.Response(fixture.Service.SyncPersonsToDevices(PersonRequest));
                Assert.Equal("FAILED", response["code"]);
                Assert.Equal(0, Convert.ToInt32(response["queued"]));
            }
        }

        [TestCase]
        public static void PermissionSync_FacePollingTimeout_SchedulesRetry()
        {
            AssertOnlineOutcome(null, false, true, true, new TimeoutException("face upload wait timed out"));
        }

        [TestCase]
        public static void PermissionSync_FaceXmlFailure_DoesNotCompleteIntent()
        {
            AssertOnlineOutcome(XmlStatus(4, "Invalid Operation", "notSupport"), false, false, true);
        }

        [TestCase]
        public static void PermissionSync_FaceRemoteDeviceBusy_SchedulesRetry()
        {
            AssertOnlineOutcome("{\"statusCode\":2,\"statusString\":\"Device Busy\",\"subStatusCode\":\"deviceBusy\"}", false, true, true, faceUploadStatus: 1003);
        }

        [TestCase]
        public static void SdkWrapper_FaceUpload_ForwardsWorkerCancellationToken()
        {
            var native = new Stage3FakeNativeClient();
            using (var source = new CancellationTokenSource())
            using (var gateway = new HikvisionSdkWrapper(native))
            {
                gateway.UploadFaceAsync(new UploadFaceRequest
                {
                    UserId = 1,
                    Face = new FaceInfo { EmployeeId = "E1", ImageBytes = Stage3TestReflection.JpegBytes() }
                }, source.Token).GetAwaiter().GetResult();
                Assert.Equal(source.Token, native.LastFaceUploadCancellationToken);
            }
        }

        [TestCase]
        public static void RetryCoordinator_DeviceBusyThenSuccess_RetriesAndCompletesIntent()
        {
            using (var fixture = new Stage5Fixture())
            using (var gateway = new HikvisionSdkWrapper(new Stage3FakeNativeClient { StdXmlOutput = XmlStatus(2, "Device Busy", "deviceBusy") }))
            {
                fixture.AddOnlineDevice();
                var state = new DeviceOperationRetryState
                {
                    Id = 1, IntentVersion = Guid.NewGuid(), DeviceId = 1, EmployeeId = "E1", PersonPending = true,
                    PersonPayloadJson = "{\"employee_id\":\"E1\",\"name\":\"Review Person\"}"
                };
                var database = new RecordingDatabaseClient { RowsAffected = 1 };
                var store = new DeviceOperationRetryStore(database);
                var plan = new RetryCommandPlan(state, new[] { new RetryOperationStep(RetryOperation.Person) });
                var failed = new RetryExecutionCoordinator(fixture.Dispatcher, gateway).ExecuteAsync(plan, "busy-retry", CancellationToken.None).GetAwaiter().GetResult();
                Assert.False(failed.AllSucceeded);
                Assert.True(failed.Retryable);
                store.ApplyExecutionResult(failed, DateTime.Now);
                Assert.True(database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.ScheduleRetry"));
                Assert.False(database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.MarkTerminalFailure"));
                Assert.False(database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.DeleteIfCompleted"));

                using (var recoveredGateway = new HikvisionSdkWrapper(new Stage3FakeNativeClient { StdXmlOutput = XmlStatus(1, "OK", "ok") }))
                {
                    var succeeded = new RetryExecutionCoordinator(fixture.Dispatcher, recoveredGateway).ExecuteAsync(plan, "busy-recovered", CancellationToken.None).GetAwaiter().GetResult();
                    Assert.True(succeeded.AllSucceeded);
                    store.ApplyExecutionResult(succeeded, DateTime.Now);
                    Assert.True(database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.MarkOperationSuccess"));
                    Assert.True(database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.DeleteIfCompleted"));
                }
            }
        }

        [TestCase]
        public static void IsapiClient_TransportFailure_RemainsRetryableWithoutConfusingBusinessCodeSeven()
        {
            var handler = new Stage3RecordingHttpHandler((request, token) => throw new HttpRequestException("network down"));
            using (var client = new HikvisionIsapiClient(() => handler))
            {
                var failure = Stage3TestReflection.Expect<DeviceGatewayException>(() => client.SendAsync(new IsapiRequest
                {
                    BaseAddress = "http://192.0.2.1", Path = "/ISAPI/System/status"
                }).GetAwaiter().GetResult());
                Assert.True(failure.Error.Retryable);
                Assert.False(SdkError.FromCode(7, "Reboot Required", "ISAPI").Retryable);
            }
        }

        [TestCase]
        public static void SdkWrapper_QueryFaceMultipartFailure_RejectsBusinessError()
        {
            var body = "--boundary\r\nContent-Type: application/json\r\n\r\n"
                + "{\"statusCode\":2,\"statusString\":\"Device Busy\",\"subStatusCode\":\"deviceBusy\"}\r\n--boundary--";
            using (var gateway = new HikvisionSdkWrapper(new Stage3FakeNativeClient { StdXmlOutput = body }))
            {
                var failure = Stage3TestReflection.Expect<DeviceGatewayException>(() => gateway.QueryFaceAsync(new QueryFaceRequest
                {
                    UserId = 1, EmployeeId = "E1"
                }).GetAwaiter().GetResult());
                Assert.Equal(2, failure.Error.Code);
                Assert.True(failure.Error.Retryable);
            }
        }

        [TestCase]
        public static void SdkWrapper_QueryFaceXmlErrorContainingJson_DoesNotMistakeMessageForSuccess()
        {
            var body = XmlStatus(4, "Invalid Operation {\"statusCode\":1}", "notSupport");
            using (var gateway = new HikvisionSdkWrapper(new Stage3FakeNativeClient { StdXmlOutput = body }))
            {
                var failure = Stage3TestReflection.Expect<DeviceGatewayException>(() => gateway.QueryFaceAsync(new QueryFaceRequest
                {
                    UserId = 1, EmployeeId = "E1"
                }).GetAwaiter().GetResult());
                Assert.Equal(4, failure.Error.Code);
                Assert.False(failure.Error.Retryable);
            }
        }

        private const string PersonRequest = "{\"deviceIds\":[1],\"people\":[{\"employeeId\":\"E1\",\"name\":\"Review Person\"}]}";

        private static string XmlStatus(int code, string status, string subStatus)
        {
            return "<?xml version=\"1.0\"?><ResponseStatus xmlns=\"http://www.isapi.org/ver20/XMLSchema\"><statusCode>" + code
                + "</statusCode><statusString>" + status + "</statusString><subStatusCode>" + subStatus + "</subStatusCode></ResponseStatus>";
        }

        private static void AssertOnlineOutcome(string body, bool success, bool retryable, bool face = false, Exception uploadException = null, int faceUploadStatus = 1000)
        {
            using (var fixture = new Stage5Fixture())
            using (var gateway = new HikvisionSdkWrapper(new Stage3FakeNativeClient
            {
                StdXmlOutput = body, FaceUploadResponse = body, FaceUploadException = uploadException, FaceUploadStatus = faceUploadStatus
            }))
            {
                fixture.AddOnlineDevice();
                var database = new RecordingDatabaseClient { RowsAffected = 1 };
                database.QueryRows.Add(new Dictionary<string, object>
                {
                    ["id"] = 1L, ["device_id"] = 1, ["employee_id"] = "E1",
                    ["intent_version"] = Guid.NewGuid(), ["person_pending"] = !face, ["face_pending"] = face,
                    ["person_payload"] = "{\"employee_id\":\"E1\",\"name\":\"Review Person\"}"
                });
                var store = new DeviceOperationRetryStore(database);
                var service = new PermissionSyncGrpcService(fixture.Registry, fixture.Dispatcher, gateway, store);
                var response = fixture.Response(face
                    ? service.SyncFacesToDevices("{\"deviceIds\":[1],\"people\":[{\"employeeId\":\"E1\",\"faceImageBase64\":\"" + Stage5TestData.JpegBase64() + "\"}]}")
                    : service.SyncPersonsToDevices(PersonRequest));

                Assert.Equal(success ? "OK" : retryable ? "PARTIAL_SUCCESS" : "FAILED", response["code"], body);
                Assert.Equal(retryable ? 1 : 0, Convert.ToInt32(response["queued"]), body);
                Assert.Equal(success, database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.MarkOperationSuccess"), body);
                Assert.Equal(success, database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.DeleteIfCompleted"), body);
                Assert.Equal(retryable, database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.ScheduleRetry"), body);
                Assert.Equal(!success && !retryable, database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.MarkTerminalFailure"), body);
            }
        }
    }
}
