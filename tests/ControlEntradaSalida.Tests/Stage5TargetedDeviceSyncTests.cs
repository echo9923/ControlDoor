using System;
using System.Linq;
using ControlDoor.Devices.Management;
using ControlDoor.Hikvision;

namespace ControlEntradaSalida.Tests
{
    public static class Stage5TargetedDeviceSyncTests
    {
        [TestCase]
        public static void SyncPersonsToDevices_OnlyCallsSelectedAcsDevice()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice(1, new[] { DeviceType.Acs });
                fixture.AddOnlineDevice(2, new[] { DeviceType.Acs });
                var selectedUserId = fixture.Registry.TryGetByDeviceId(2).Snapshot.SdkUserId.Value;

                var response = fixture.Response(fixture.Service.SyncPersonsToDevices(
                    @"{""deviceIds"":[2],""items"":[{""employee_id"":""0976"",""name"":""张三""}]}",
                    fixture.Context("target-person-selected")));

                var calls = fixture.Gateway.Calls
                    .Where(call => call.MethodName == "UpsertPersonAsync")
                    .Select(call => (UpsertPersonRequest)call.Request)
                    .ToList();
                Assert.Equal("OK", response["code"]);
                Assert.Equal(1, Convert.ToInt32(response["targetDevices"]));
                Assert.Equal(1, calls.Count);
                Assert.Equal(selectedUserId, calls[0].UserId);
                Assert.Equal("0976", calls[0].Person.EmployeeId);
                Assert.False(fixture.Gateway.Calls.Any(call => call.MethodName == "UploadFaceAsync"));
                Assert.Equal(0, fixture.UserWriter.PersonsSynced.Count);
            }
        }

        [TestCase]
        public static void SyncPersonsToDevices_DuplicateDeviceIdsExecuteOnce()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice(2, new[] { DeviceType.Acs });

                var response = fixture.Response(fixture.Service.SyncPersonsToDevices(
                    @"{""deviceIds"":[2,2],""items"":[{""employee_id"":""10001"",""name"":""王五""}]}",
                    fixture.Context("target-person-duplicate")));

                Assert.Equal("OK", response["code"]);
                Assert.Equal(1, Convert.ToInt32(response["targetDevices"]));
                Assert.Equal(1, fixture.Gateway.Calls.Count(call => call.MethodName == "UpsertPersonAsync"));
                Assert.Equal(0, fixture.UserWriter.PersonsSynced.Count);
            }
        }

        [TestCase]
        public static void SyncPersonsToDevices_OfflineSelectedDeviceQueuesOnlyThatDevice()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOfflineDevice(1, new[] { DeviceType.Acs });
                fixture.AddOfflineDevice(2, new[] { DeviceType.Acs });

                var response = fixture.Response(fixture.Service.SyncPersonsToDevices(
                    @"{""deviceIds"":[2],""items"":[{""employee_id"":""10001"",""name"":""李四""}]}",
                    fixture.Context("target-person-offline")));

                Assert.Equal("PARTIAL_SUCCESS", response["code"]);
                Assert.Equal(1, fixture.RetryWriter.Intents.Count);
                Assert.Equal(2, fixture.RetryWriter.Intents[0].DeviceId);
                Assert.Equal("10001", fixture.RetryWriter.Intents[0].EmployeeId);
                Assert.Equal("SyncPerson", fixture.RetryWriter.Intents[0].Operation);
                Assert.Equal(0, fixture.Gateway.Calls.Count(call => call.MethodName == "UpsertPersonAsync"));
                Assert.Equal(0, fixture.UserWriter.PersonsSynced.Count);
            }
        }

        [TestCase]
        public static void SyncFacesToDevices_UploadsOnlyToSelectedDeviceWithoutUpsertingPerson()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice(1, new[] { DeviceType.Acs });
                fixture.AddOnlineDevice(2, new[] { DeviceType.Acs });
                var selectedUserId = fixture.Registry.TryGetByDeviceId(2).Snapshot.SdkUserId.Value;
                var face = Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });

                var response = fixture.Response(fixture.Service.SyncFacesToDevices(
                    @"{""deviceIds"":[2],""items"":[{""employee_id"":""0976"",""face_image_base64"":""" + face + @"""}]}",
                    fixture.Context("target-face-selected")));

                var calls = fixture.Gateway.Calls
                    .Where(call => call.MethodName == "UploadFaceAsync")
                    .Select(call => (UploadFaceRequest)call.Request)
                    .ToList();
                Assert.Equal("OK", response["code"]);
                Assert.Equal(1, Convert.ToInt32(response["targetDevices"]));
                Assert.Equal(1, Convert.ToInt32(response["facesUploaded"]));
                Assert.Equal(1, calls.Count);
                Assert.Equal(selectedUserId, calls[0].UserId);
                Assert.Equal("0976", calls[0].Face.EmployeeId);
                Assert.False(fixture.Gateway.Calls.Any(call => call.MethodName == "UpsertPersonAsync"));
                Assert.Equal(0, fixture.UserWriter.PersonsSynced.Count);
            }
        }

        [TestCase]
        public static void SyncFacesToDevices_OfflineSelectedDeviceQueuesOnlyUploadFace()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOfflineDevice(1, new[] { DeviceType.Acs });
                fixture.AddOfflineDevice(2, new[] { DeviceType.Acs });
                var face = Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });

                var response = fixture.Response(fixture.Service.SyncFacesToDevices(
                    @"{""deviceIds"":[2],""items"":[{""employee_id"":""10001"",""face_image_base64"":""" + face + @"""}]}",
                    fixture.Context("target-face-offline")));

                Assert.Equal("PARTIAL_SUCCESS", response["code"]);
                Assert.Equal(1, fixture.RetryWriter.Intents.Count);
                Assert.Equal(2, fixture.RetryWriter.Intents[0].DeviceId);
                Assert.Equal("10001", fixture.RetryWriter.Intents[0].EmployeeId);
                Assert.Equal("UploadFace", fixture.RetryWriter.Intents[0].Operation);
                Assert.False(fixture.RetryWriter.Intents.Any(intent => intent.Operation == "SyncPerson"));
                Assert.Equal(0, fixture.Gateway.Calls.Count(call => call.MethodName == "UploadFaceAsync"));
                Assert.Equal(0, fixture.UserWriter.PersonsSynced.Count);
            }
        }

        [TestCase]
        public static void SyncPersonsToDevices_InvalidDeviceSelectionsRejectBeforeSideEffects()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice(1, new[] { DeviceType.Acs });
                fixture.AddOnlineDevice(2, new[] { DeviceType.FaceCapture });
                var callsBefore = CountProvisioningCalls(fixture);
                var payloads = new[]
                {
                    @"{""items"":[{""employee_id"":""10001"",""name"":""张三""}]}",
                    @"{""deviceIds"":[],""items"":[{""employee_id"":""10001"",""name"":""张三""}]}",
                    @"{""deviceIds"":[0],""items"":[{""employee_id"":""10001"",""name"":""张三""}]}",
                    @"{""deviceIds"":[""1""],""items"":[{""employee_id"":""10001"",""name"":""张三""}]}",
                    @"{""deviceIds"":[999],""items"":[{""employee_id"":""10001"",""name"":""张三""}]}",
                    @"{""deviceIds"":[2],""items"":[{""employee_id"":""10001"",""name"":""张三""}]}"
                };

                foreach (var payload in payloads)
                {
                    var response = fixture.Response(fixture.Service.SyncPersonsToDevices(
                        payload,
                        fixture.Context("target-invalid-device")));

                    Assert.Equal("INVALID_ARGUMENT", response["code"]);
                    Assert.Equal(callsBefore, CountProvisioningCalls(fixture));
                    Assert.Equal(0, fixture.RetryWriter.Intents.Count);
                }
            }
        }

        [TestCase]
        public static void SyncPersonsToDevices_FaceFieldRejectsBeforeSideEffects()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice(1, new[] { DeviceType.Acs });
                var callsBefore = CountProvisioningCalls(fixture);
                var face = Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });

                var response = fixture.Response(fixture.Service.SyncPersonsToDevices(
                    @"{""deviceIds"":[1],""items"":[{""employee_id"":""10001"",""name"":""张三"",""faceImageBase64"":""" + face + @"""}]}",
                    fixture.Context("target-person-face-rejected")));

                Assert.Equal("INVALID_ARGUMENT", response["code"]);
                Assert.Equal(callsBefore, CountProvisioningCalls(fixture));
                Assert.Equal(0, fixture.RetryWriter.Intents.Count);
            }
        }

        [TestCase]
        public static void SyncPersonsToDevices_InvalidBatchesRejectBeforeSideEffects()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice(1, new[] { DeviceType.Acs });
                var callsBefore = CountProvisioningCalls(fixture);
                var tooManyItems = string.Join(",", Enumerable.Range(1, 501)
                    .Select(index => @"{""employee_id"":""" + index + @""",""name"":""测试""}"));
                var payloads = new[]
                {
                    @"{""deviceIds"":[1],""items"":[]}",
                    @"{""deviceIds"":[1],""items"":[{""employee_id"":""10001"",""name"":""张三"",""valid_from"":""2030-01-01"",""valid_to"":""2029-01-01""}]}",
                    @"{""deviceIds"":[1],""items"":[" + tooManyItems + "]}"
                };

                foreach (var payload in payloads)
                {
                    var response = fixture.Response(fixture.Service.SyncPersonsToDevices(
                        payload,
                        fixture.Context("target-invalid-batch")));

                    Assert.True(response["code"].Equals("INVALID_ARGUMENT") || response["code"].Equals("BATCH_TOO_LARGE"));
                    Assert.Equal(callsBefore, CountProvisioningCalls(fixture));
                    Assert.Equal(0, fixture.RetryWriter.Intents.Count);
                }
            }
        }

        [TestCase]
        public static void SyncFacesToDevices_InvalidFacesRejectBeforeSideEffects()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice(1, new[] { DeviceType.Acs });
                var callsBefore = CountProvisioningCalls(fixture);
                var oversized = Convert.ToBase64String(new byte[205 * 1024]);
                var payloads = new[]
                {
                    @"{""deviceIds"":[1],""items"":[{""employee_id"":""10001""}]}",
                    @"{""deviceIds"":[1],""items"":[{""employee_id"":""10001"",""face_image_base64"":""not-base64""}]}",
                    @"{""deviceIds"":[1],""items"":[{""employee_id"":""10001"",""face_image_base64"":""" + oversized + @"""}]}"
                };

                foreach (var payload in payloads)
                {
                    var response = fixture.Response(fixture.Service.SyncFacesToDevices(
                        payload,
                        fixture.Context("target-invalid-face")));

                    Assert.True(response["code"].Equals("INVALID_ARGUMENT") || response["code"].Equals("FACE_TOO_LARGE"));
                    Assert.Equal(callsBefore, CountProvisioningCalls(fixture));
                    Assert.Equal(0, fixture.RetryWriter.Intents.Count);
                }
            }
        }

        [TestCase]
        public static void SyncPersonsToDevices_MissingNameUsesOriginalEmployeeId()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice(1, new[] { DeviceType.Acs });

                var response = fixture.Response(fixture.Service.SyncPersonsToDevices(
                    @"{""deviceIds"":[1],""items"":[{""employee_id"":""0976""}]}",
                    fixture.Context("target-person-name-fallback")));

                var request = (UpsertPersonRequest)fixture.Gateway.Calls
                    .Single(call => call.MethodName == "UpsertPersonAsync")
                    .Request;
                Assert.Equal("OK", response["code"]);
                Assert.Equal("0976", request.Person.EmployeeId);
                Assert.Equal("0976", request.Person.Name);
            }
        }

        private static int CountProvisioningCalls(Stage5Fixture fixture)
        {
            return fixture.Gateway.Calls.Count(call =>
                call.MethodName == "UpsertPersonAsync" ||
                call.MethodName == "UploadFaceAsync");
        }
    }
}
