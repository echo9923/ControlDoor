using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Hikvision;

namespace ControlEntradaSalida.Tests
{
    public static class Stage5RetryIntentTests
    {
        [TestCase]
        public static void SyncPermissions_RetryableSdkTimeout_QueuesSyncPermissionIntent()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice();
                fixture.Gateway.ConfigureTimeout("UpsertPersonAsync");

                var response = fixture.Response(fixture.Service.SyncPermissions(
                    @"{""items"":[{""employee_id"":""10001"",""full_name"":""张三"",""permission_code"":3}]}",
                    fixture.Context("permission-timeout")));

                Assert.Equal("PARTIAL_SUCCESS", response["code"]);
                Assert.Equal(1, Convert.ToInt32(response["queued"]));
                Assert.Equal(1, fixture.RetryWriter.Intents.Count);
                Assert.Equal("SyncPermission", fixture.RetryWriter.Intents[0].Operation);
                Assert.Equal(3, fixture.RetryWriter.Intents[0].PermissionLevel.Value);
                Assert.Contains("permission_code", fixture.RetryWriter.Intents[0].PayloadJson);
                Assert.Contains("张三", fixture.RetryWriter.Intents[0].PermissionPayloadJson);
            }
        }

        [TestCase]
        public static void SyncPermissions_CallerCancelled_DoesNotQueueQueuedTask()
        {
            using (var fixture = new Stage5Fixture())
            using (var cancellation = new CancellationTokenSource())
            {
                fixture.AddOnlineDevice();
                fixture.Gateway.ConfigureDelay("UpsertPersonAsync", TimeSpan.FromSeconds(2));
                cancellation.CancelAfter(50);
                var context = fixture.Context("permission-cancelled");
                context.CancellationToken = cancellation.Token;

                var response = fixture.Response(fixture.Service.SyncPermissions(
                    @"{""items"":[{""employee_id"":""10002"",""name"":""Cancel User"",""permission_code"":4}]}",
                    context));

                Assert.Equal("FAILED", response["code"]);
                Assert.Equal(0, Convert.ToInt32(response["queued"]));
                Assert.Equal(1, Convert.ToInt32(response["failed"]));
                Assert.Equal(0, fixture.RetryWriter.Intents.Count);
                Assert.False(fixture.UserWriter.PermissionLevels.ContainsKey("10002"));
            }
        }

        [TestCase]
        public static void SyncPermissions_PreCancelled_DoesNotSubmitOrQueueIntent()
        {
            using (var fixture = new Stage5Fixture())
            using (var cancellation = new CancellationTokenSource())
            {
                fixture.AddOnlineDevice();
                cancellation.Cancel();
                var context = fixture.Context("permission-pre-cancelled");
                context.CancellationToken = cancellation.Token;

                var response = fixture.Response(fixture.Service.SyncPermissions(
                    @"{""items"":[{""employee_id"":""10005"",""name"":""Pre Cancelled"",""permission_code"":6}]}",
                    context));

                Assert.Equal("FAILED", response["code"]);
                Assert.Equal(0, Convert.ToInt32(response["queued"]));
                Assert.Equal(1, Convert.ToInt32(response["failed"]));
                Assert.Equal(0, fixture.RetryWriter.IntentCount);
                Assert.False(fixture.Gateway.Calls.Any(call => call.MethodName == "UpsertPersonAsync"));
            }
        }

        [TestCase]
        public static void SyncPermissions_PreCancelledOffline_DoesNotQueueIntent()
        {
            using (var fixture = new Stage5Fixture())
            using (var cancellation = new CancellationTokenSource())
            {
                fixture.AddOfflineDevice();
                cancellation.Cancel();
                var context = fixture.Context("permission-pre-cancelled-offline");
                context.CancellationToken = cancellation.Token;

                var response = fixture.Response(fixture.Service.SyncPermissions(
                    @"{""items"":[{""employee_id"":""10011"",""name"":""Offline Pre Cancelled"",""permission_code"":4}]}",
                    context));

                Assert.Equal("FAILED", response["code"]);
                Assert.Equal(0, Convert.ToInt32(response["queued"]));
                Assert.Equal(1, Convert.ToInt32(response["failed"]));
                Assert.Equal(0, fixture.RetryWriter.IntentCount);
                Assert.False(fixture.Gateway.Calls.Any(call => call.MethodName == "UpsertPersonAsync"));
            }
        }

        [TestCase]
        public static void SyncPersons_PreCancelledOfflineWithFace_DoesNotQueuePersonOrFaceIntent()
        {
            using (var fixture = new Stage5Fixture())
            using (var cancellation = new CancellationTokenSource())
            {
                fixture.AddOfflineDevice();
                cancellation.Cancel();
                var context = fixture.Context("persons-pre-cancelled-offline");
                context.CancellationToken = cancellation.Token;

                var response = fixture.Response(fixture.Service.SyncPersons(
                    @"{""items"":[{""employee_id"":""10012"",""name"":""Offline Person"",""face_image_base64"":""" + Stage5TestData.JpegBase64() + @"""}]}",
                    context));

                Assert.Equal("FAILED", response["code"]);
                Assert.Equal(0, Convert.ToInt32(response["queued"]));
                Assert.Equal(1, Convert.ToInt32(response["failed"]));
                Assert.Equal(0, fixture.RetryWriter.IntentCount);
                Assert.False(fixture.Gateway.Calls.Any(call => call.MethodName == "UpsertPersonAsync"));
                Assert.False(fixture.Gateway.Calls.Any(call => call.MethodName == "UploadFaceAsync"));
            }
        }

        [TestCase]
        public static void SyncPermissions_LateFinalSuccess_DoesNotQueueOrChangeResponse()
        {
            using (var fixture = new Stage5Fixture())
            using (var cancellation = new CancellationTokenSource())
            using (var entered = new ManualResetEventSlim(false))
            using (var release = new ManualResetEventSlim(false))
            {
                fixture.AddOnlineDevice();
                var completedBefore = fixture.Dispatcher.GetWorkerSnapshots().Sum(item => item.CompletedTaskCount);
                fixture.Gateway.ConfigureResult<int>("UpsertPersonAsync", request =>
                {
                    entered.Set();
                    release.Wait();
                    return 0;
                });

                var context = fixture.Context("permission-late-success");
                context.CancellationToken = cancellation.Token;
                var requestTask = Task.Run(() => fixture.Service.SyncPermissions(
                    @"{""items"":[{""employee_id"":""10006"",""name"":""Late Success"",""permission_code"":7}]}",
                    context));
                try
                {
                    Assert.True(entered.Wait(TimeSpan.FromSeconds(2)), "permission task did not reach the gateway.");
                    cancellation.Cancel();
                    Assert.True(requestTask.Wait(TimeSpan.FromSeconds(2)), "cancelled permission request did not return.");
                    var response = fixture.Response(requestTask.GetAwaiter().GetResult());

                    Assert.Equal("FAILED", response["code"]);
                    Assert.Equal(0, Convert.ToInt32(response["queued"]));
                    Assert.Equal(1, Convert.ToInt32(response["failed"]));

                    release.Set();
                    WaitUntil(
                        () => fixture.Dispatcher.GetWorkerSnapshots().Sum(item => item.CompletedTaskCount) > completedBefore,
                        "late successful permission task did not complete.");
                    Assert.Equal(0, fixture.RetryWriter.IntentCount);
                    Assert.Equal("FAILED", response["code"]);
                    Assert.Equal(0, Convert.ToInt32(response["queued"]));
                }
                finally
                {
                    release.Set();
                    requestTask.Wait(TimeSpan.FromSeconds(2));
                }
            }
        }

        [TestCase]
        public static void SyncPermissions_LateRetryableFailure_QueuesExactlyOneIntentAndDoesNotChangeResponse()
        {
            using (var fixture = new Stage5Fixture())
            using (var cancellation = new CancellationTokenSource())
            using (var entered = new ManualResetEventSlim(false))
            using (var release = new ManualResetEventSlim(false))
            {
                fixture.AddOnlineDevice();
                fixture.Gateway.ConfigureResult<int>("UpsertPersonAsync", request =>
                {
                    entered.Set();
                    release.Wait();
                    throw new DeviceGatewayException("UpsertPerson", SdkError.FromCode(408, "late timeout"));
                });

                var context = fixture.Context("permission-late-failure");
                context.CancellationToken = cancellation.Token;
                var requestTask = Task.Run(() => fixture.Service.SyncPermissions(
                    @"{""items"":[{""employee_id"":""10007"",""name"":""Late Failure"",""permission_code"":8}]}",
                    context));
                try
                {
                    Assert.True(entered.Wait(TimeSpan.FromSeconds(2)), "permission task did not reach the gateway.");
                    cancellation.Cancel();
                    Assert.True(requestTask.Wait(TimeSpan.FromSeconds(2)), "cancelled permission request did not return.");
                    var response = fixture.Response(requestTask.GetAwaiter().GetResult());

                    Assert.Equal("FAILED", response["code"]);
                    Assert.Equal(0, Convert.ToInt32(response["queued"]));
                    Assert.Equal(1, Convert.ToInt32(response["failed"]));
                    Assert.Equal(0, fixture.RetryWriter.IntentCount);

                    release.Set();
                    WaitUntil(() => fixture.RetryWriter.IntentCount == 1, "late retryable permission failure did not queue an intent.");
                    var intents = fixture.RetryWriter.Snapshot();
                    Assert.Equal(1, intents.Count);
                    Assert.Equal("SyncPermission", intents[0].Operation);
                    Assert.Equal("FAILED", response["code"]);
                    Assert.Equal(0, Convert.ToInt32(response["queued"]));
                }
                finally
                {
                    release.Set();
                    requestTask.Wait(TimeSpan.FromSeconds(2));
                }
            }
        }

        [TestCase]
        public static void SyncPersons_LateRetryableFailureWithFace_QueuesPersonAndFaceOnce()
        {
            using (var fixture = new Stage5Fixture())
            using (var cancellation = new CancellationTokenSource())
            using (var entered = new ManualResetEventSlim(false))
            using (var release = new ManualResetEventSlim(false))
            {
                fixture.AddOnlineDevice();
                fixture.Gateway.ConfigureResult<int>("UpsertPersonAsync", request =>
                {
                    entered.Set();
                    release.Wait();
                    throw new DeviceGatewayException("UpsertPerson", SdkError.FromCode(408, "late person timeout"));
                });

                var context = fixture.Context("person-late-failure-with-face");
                context.CancellationToken = cancellation.Token;
                var requestTask = Task.Run(() => fixture.Service.SyncPersons(
                    @"{""items"":[{""employee_id"":""10008"",""name"":""Late Person"",""face_image_base64"":""" + Stage5TestData.JpegBase64() + @"""}]}",
                    context));
                try
                {
                    Assert.True(entered.Wait(TimeSpan.FromSeconds(2)), "person task did not reach the gateway.");
                    cancellation.Cancel();
                    Assert.True(requestTask.Wait(TimeSpan.FromSeconds(2)), "cancelled person request did not return.");
                    var response = fixture.Response(requestTask.GetAwaiter().GetResult());

                    Assert.Equal("FAILED", response["code"]);
                    Assert.Equal(0, Convert.ToInt32(response["queued"]));
                    Assert.Equal(1, Convert.ToInt32(response["failed"]));
                    Assert.Equal(0, fixture.RetryWriter.IntentCount);
                    Assert.False(fixture.Gateway.Calls.Any(call => call.MethodName == "UploadFaceAsync"));

                    release.Set();
                    WaitUntil(() => fixture.RetryWriter.IntentCount == 2, "late person failure did not preserve both retry intents.");
                    var operations = fixture.RetryWriter.Snapshot().Select(intent => intent.Operation).ToList();
                    Assert.Equal(1, operations.Count(operation => operation == "SyncPerson"));
                    Assert.Equal(1, operations.Count(operation => operation == "UploadFace"));
                    Assert.False(fixture.Gateway.Calls.Any(call => call.MethodName == "UploadFaceAsync"));
                    Assert.Equal("FAILED", response["code"]);
                    Assert.Equal(0, Convert.ToInt32(response["queued"]));
                }
                finally
                {
                    release.Set();
                    requestTask.Wait(TimeSpan.FromSeconds(2));
                }
            }
        }

        [TestCase]
        public static void SyncPermissions_QueuedCancellation_QueuesExactlyOneIntent()
        {
            using (var fixture = new Stage5Fixture(defaultFaceCaptureDeviceId: null, dispatcherTimeoutMilliseconds: 50))
            using (var entered = new ManualResetEventSlim(false))
            using (var release = new ManualResetEventSlim(false))
            {
                fixture.AddOnlineDevice();
                fixture.Gateway.ConfigureResult<int>("UpsertPersonAsync", request =>
                {
                    entered.Set();
                    release.Wait();
                    return 0;
                });

                var firstRequest = Task.Run(() => fixture.Service.SyncPermissions(
                    @"{""items"":[{""employee_id"":""10009"",""name"":""Queue Holder"",""permission_code"":1}]}",
                    fixture.Context("permission-queue-holder")));
                try
                {
                    Assert.True(entered.Wait(TimeSpan.FromSeconds(2)), "queue holder task did not reach the gateway.");
                    Assert.True(firstRequest.Wait(TimeSpan.FromSeconds(2)), "queue holder request did not return its wait result.");

                    var response = fixture.Response(fixture.Service.SyncPermissions(
                        @"{""items"":[{""employee_id"":""10010"",""name"":""Queued Cancellation"",""permission_code"":2}]}",
                        fixture.Context("permission-queued-cancellation")));

                    Assert.Equal("PARTIAL_SUCCESS", response["code"]);
                    Assert.Equal(1, Convert.ToInt32(response["queued"]));
                    Assert.Equal(1, fixture.RetryWriter.IntentCount);
                    Assert.Equal("SyncPermission", fixture.RetryWriter.Snapshot()[0].Operation);

                    release.Set();
                    WaitUntil(() => fixture.RetryWriter.IntentCount == 1, "queued cancellation intent count changed unexpectedly.");
                    Assert.Equal(1, fixture.RetryWriter.Snapshot().Count);
                }
                finally
                {
                    release.Set();
                    firstRequest.Wait(TimeSpan.FromSeconds(2));
                }
            }
        }

        [TestCase]
        public static void SyncPermissions_DispatcherWaitTimeout_DoesNotQueueIntent()
        {
            using (var fixture = new Stage5Fixture(defaultFaceCaptureDeviceId: null, dispatcherTimeoutMilliseconds: 50))
            {
                fixture.AddOnlineDevice();
                fixture.Gateway.ConfigureDelay("UpsertPersonAsync", TimeSpan.FromSeconds(2));

                var response = fixture.Response(fixture.Service.SyncPermissions(
                    @"{""items"":[{""employee_id"":""10003"",""name"":""Timeout User"",""permission_code"":5}]}",
                    fixture.Context("permission-wait-timeout")));

                Assert.Equal("FAILED", response["code"]);
                Assert.Equal(0, Convert.ToInt32(response["queued"]));
                Assert.Equal(1, Convert.ToInt32(response["failed"]));
                Assert.Equal(0, fixture.RetryWriter.Intents.Count);
                Assert.False(fixture.UserWriter.PermissionLevels.ContainsKey("10003"));
            }
        }

        [TestCase]
        public static void SyncPersons_UploadFaceTimeout_QueuesFaceIntentAndDoesNotMarkSynced()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice();
                fixture.Gateway.ConfigureTimeout("UploadFaceAsync");

                var response = fixture.Response(fixture.Service.SyncPersons(
                    @"{""items"":[{""employee_id"":""10001"",""name"":""Test User"",""face_image_base64"":""" + Stage5TestData.JpegBase64() + @"""}]}",
                    fixture.Context("face-timeout")));

                Assert.Equal("PARTIAL_SUCCESS", response["code"]);
                Assert.Equal(0, Convert.ToInt32(response["succeeded"]));
                Assert.Equal(1, Convert.ToInt32(response["queued"]));
                Assert.Equal(0, Convert.ToInt32(response["failed"]));
                Assert.Equal(0, Convert.ToInt32(response["facesUploaded"]));
                Assert.False(fixture.UserWriter.PersonsSynced.Contains("10001"));
                Assert.True(fixture.RetryWriter.Intents.Any(intent => intent.Operation == "UploadFace"));
            }
        }

        [TestCase]
        public static void SyncPersons_PersonTimeoutWithFace_QueuesPersonAndFaceIntents()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice();
                fixture.Gateway.ConfigureTimeout("UpsertPersonAsync");

                var response = fixture.Response(fixture.Service.SyncPersons(
                    @"{""items"":[{""employee_id"":""10001"",""name"":""Test User"",""face_image_base64"":""" + Stage5TestData.JpegBase64() + @"""}]}",
                    fixture.Context("person-timeout-with-face")));

                Assert.Equal("PARTIAL_SUCCESS", response["code"]);
                Assert.Equal(1, Convert.ToInt32(response["queued"]));
                Assert.Equal(0, Convert.ToInt32(response["succeeded"]));
                Assert.Equal(0, Convert.ToInt32(response["facesUploaded"]));
                Assert.False(fixture.UserWriter.PersonsSynced.Contains("10001"));
                Assert.False(fixture.Gateway.Calls.Any(call => call.MethodName == "UploadFaceAsync"));
                Assert.True(fixture.RetryWriter.Intents.Any(intent => intent.Operation == "SyncPerson"));
                Assert.True(fixture.RetryWriter.Intents.Any(intent => intent.Operation == "UploadFace"));
            }
        }

        [TestCase]
        public static void SyncPersons_DispatcherWaitTimeout_DoesNotQueueIntent()
        {
            using (var fixture = new Stage5Fixture(defaultFaceCaptureDeviceId: null, dispatcherTimeoutMilliseconds: 50))
            {
                fixture.AddOnlineDevice();
                fixture.Gateway.ConfigureDelay("UpsertPersonAsync", TimeSpan.FromSeconds(2));

                var response = fixture.Response(fixture.Service.SyncPersons(
                    @"{""items"":[{""employee_id"":""10004"",""name"":""Wait User""}]}",
                    fixture.Context("person-wait-timeout")));

                Assert.Equal("FAILED", response["code"]);
                Assert.Equal(0, Convert.ToInt32(response["queued"]));
                Assert.Equal(1, Convert.ToInt32(response["failed"]));
                Assert.Equal(0, fixture.RetryWriter.Intents.Count);
            }
        }


        [TestCase]
        public static void SyncPersons_OmittedName_DefaultsToEmployeeId()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice();

                var response = fixture.Response(fixture.Service.SyncPersons(
                    @"{""items"":[{""employee_id"":""10009""}]}",
                    fixture.Context("person-default-name")));

                Assert.Equal("OK", response["code"]);
                Assert.Equal(1, Convert.ToInt32(response["succeeded"]));
                Assert.Equal("10009", fixture.LastUpsertPerson("10009").Name);
            }
        }

        [TestCase]
        public static void SyncPersons_PartialDeviceSuccess_DoesNotMarkPersonSynced()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice(1);
                fixture.AddOfflineDevice(2);

                var response = fixture.Response(fixture.Service.SyncPersons(
                    @"{""items"":[{""employee_id"":""10008"",""name"":""Partial""}]}",
                    fixture.Context("person-partial-device")));

                Assert.Equal("PARTIAL_SUCCESS", response["code"]);
                Assert.Equal(0, Convert.ToInt32(response["succeeded"]));
                Assert.Equal(1, Convert.ToInt32(response["queued"]));
                Assert.False(fixture.UserWriter.PersonsSynced.Contains("10008"));
            }
        }

        [TestCase]
        public static void SyncPersons_RetryWriteFailure_DoesNotReportQueued()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOfflineDevice();
                fixture.RetryWriter.FailNext = true;

                var response = fixture.Response(fixture.Service.SyncPersons(
                    @"{""items"":[{""employee_id"":""10007"",""name"":""DB Fail""}]}",
                    fixture.Context("person-retry-write-fail")));

                Assert.Equal("FAILED", response["code"]);
                Assert.Equal(0, Convert.ToInt32(response["queued"]));
                Assert.Equal(1, Convert.ToInt32(response["failed"]));
                Assert.Equal(0, fixture.RetryWriter.Intents.Count);
            }
        }
        private static void WaitUntil(Func<bool> condition, string message)
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                Thread.Sleep(10);
            }

            Assert.True(condition(), message);
        }

        [TestCase]
        public static void DeleteFaces_RetryableTimeout_QueuesDeleteFaceIntent()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice();
                fixture.Gateway.ConfigureTimeout("DeleteFaceAsync");

                var response = fixture.Response(fixture.Service.DeleteFaces(@"[""10001""]", fixture.Context("delete-face-timeout")));

                Assert.Equal("PARTIAL_SUCCESS", response["code"]);
                Assert.Equal(1, Convert.ToInt32(response["queued"]));
                Assert.Equal(1, fixture.RetryWriter.Intents.Count);
                Assert.Equal("DeleteFace", fixture.RetryWriter.Intents[0].Operation);
            }
        }

        [TestCase]
        public static void DeletePersons_DeviceUnsupported_DoesNotQueueRetryIntent()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice();
                fixture.Gateway.ConfigureException("DeletePersonAsync", new DeviceGatewayException("DeletePerson", SdkError.FromCode(23, "unsupported")));

                var response = fixture.Response(fixture.Service.DeletePersons(@"[""10001""]", fixture.Context("delete-person-unsupported")));

                Assert.Equal("FAILED", response["code"]);
                Assert.Equal(0, Convert.ToInt32(response["queued"]));
                Assert.Equal(0, fixture.RetryWriter.Intents.Count);
                var calls = fixture.Gateway.Calls.ToList();
                var faceIndex = calls.FindIndex(call => call.MethodName == "DeleteFaceAsync");
                var personIndex = calls.FindIndex(call => call.MethodName == "DeletePersonAsync");
                Assert.True(faceIndex >= 0);
                Assert.True(personIndex > faceIndex);
            }
        }

        [TestCase]
        public static void DeletePersons_DeleteFaceRetryableTimeout_StillDeletesPersonWithoutRetryIntent()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice();
                fixture.Gateway.ConfigureTimeout("DeleteFaceAsync");

                var response = fixture.Response(fixture.Service.DeletePersons(@"[""10001""]", fixture.Context("delete-person-face-timeout")));

                Assert.Equal("OK", response["code"]);
                Assert.Equal(1, Convert.ToInt32(response["succeeded"]));
                Assert.Equal(0, Convert.ToInt32(response["queued"]));
                Assert.Equal(0, fixture.RetryWriter.Intents.Count);
                Assert.True(fixture.Gateway.Calls.Any(call => call.MethodName == "DeleteFaceAsync"));
                Assert.True(fixture.Gateway.Calls.Any(call => call.MethodName == "DeletePersonAsync"));
                Assert.True(fixture.UserWriter.PersonsDeleted.Contains("10001"));
            }
        }

        [TestCase]
        public static void QueuedDetails_ContainNextRetryAtAndStableOperationFields()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOfflineDevice();

                var response = fixture.Response(fixture.Service.DeleteFaces(@"[""10001""]", fixture.Context("queued-next-retry")));
                var details = (ArrayList)response["queuedDetails"];
                var first = (Dictionary<string, object>)details[0];

                Assert.Equal("PARTIAL_SUCCESS", response["code"]);
                Assert.Equal(1, Convert.ToInt32(first["deviceId"]));
                Assert.Equal("10001", first["employeeId"]);
                Assert.Equal("DeleteFace", first["operation"]);
                Assert.True(first.ContainsKey("createdAt"));
                Assert.True(first.ContainsKey("nextRetryAt"));
                Assert.True(!string.IsNullOrWhiteSpace(Convert.ToString(first["nextRetryAt"])));
            }
        }
    }
}
