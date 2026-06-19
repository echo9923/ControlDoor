using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
        public static void SyncPersons_UploadFaceTimeout_QueuesFaceIntentAndKeepsPersonSync()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice();
                fixture.Gateway.ConfigureTimeout("UploadFaceAsync");

                var response = fixture.Response(fixture.Service.SyncPersons(
                    @"{""items"":[{""employee_id"":""10001"",""name"":""Test User"",""face_image_base64"":""" + Stage5TestData.JpegBase64() + @"""}]}",
                    fixture.Context("face-timeout")));

                Assert.Equal("PARTIAL_SUCCESS", response["code"]);
                Assert.Equal(1, Convert.ToInt32(response["succeeded"]));
                Assert.Equal(1, Convert.ToInt32(response["queued"]));
                Assert.Equal(0, Convert.ToInt32(response["failed"]));
                Assert.Equal(0, Convert.ToInt32(response["facesUploaded"]));
                Assert.True(fixture.UserWriter.PersonsSynced.Contains("10001"));
                Assert.True(fixture.RetryWriter.Intents.Any(intent => intent.Operation == "UploadFace"));
            }
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
