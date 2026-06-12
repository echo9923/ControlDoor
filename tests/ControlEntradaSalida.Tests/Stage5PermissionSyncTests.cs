using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using ControlDoor.Devices.Runtime;
using ControlDoor.GrpcApi;
using ControlDoor.Hikvision;
using ControlDoor.Permissions;

namespace ControlEntradaSalida.Tests
{
    public static class Stage5PermissionSyncTests
    {
        [TestCase]
        public static void PermissionSyncGrpcService_MethodFullNames_MatchContract()
        {
            using (var fixture = new Stage5Fixture())
            {
                Assert.True(fixture.Service.MethodFullNames.Contains(PermissionSyncGrpcService.SyncPermissionsFullName));
                Assert.True(fixture.Service.MethodFullNames.Contains(PermissionSyncGrpcService.SyncPersonsFullName));
                Assert.True(fixture.Service.MethodFullNames.Contains(PermissionSyncGrpcService.DeleteFacesFullName));
                Assert.True(fixture.Service.MethodFullNames.Contains(PermissionSyncGrpcService.DeletePersonsFullName));
                Assert.True(fixture.Service.MethodFullNames.Contains(PermissionSyncGrpcService.GetFacesFullName));
                Assert.True(fixture.Service.MethodFullNames.Contains(PermissionSyncGrpcService.CaptureFaceStreamFullName));
                Assert.True(fixture.Service.MethodFullNames.Contains(PermissionSyncGrpcService.GetEnrollmentStatusFullName));
            }
        }

        [TestCase]
        public static void SyncPermissions_SupportsArrayItemsRecordsAndSingleObject()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice();

                var array = fixture.Response(fixture.Service.SyncPermissions(@"[{""employee_id"":""10001"",""permission_code"":1}]", fixture.Context("sp-array")));
                var items = fixture.Response(fixture.Service.SyncPermissions(@"{""items"":[{""employee_id"":""10002"",""permission_code"":2}]}", fixture.Context("sp-items")));
                var records = fixture.Response(fixture.Service.SyncPermissions(@"{""records"":[{""employee_id"":""10003"",""permission_code"":3}]}", fixture.Context("sp-records")));
                var single = fixture.Response(fixture.Service.SyncPermissions(@"{""employee_id"":""10004"",""permission_code"":4}", fixture.Context("sp-single")));

                Assert.Equal("OK", array["code"]);
                Assert.Equal("OK", items["code"]);
                Assert.Equal("OK", records["code"]);
                Assert.Equal("OK", single["code"]);
                Assert.True(fixture.Gateway.Calls.Count(call => call.MethodName == "SetPermissionAsync") >= 4);
            }
        }

        [TestCase]
        public static void SyncPermissions_DuplicateEmployeeKeepsLastPermission()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice();

                var response = fixture.Response(fixture.Service.SyncPermissions(
                    @"{""items"":[{""employee_id"":""10001"",""permission_code"":1},{""employee_id"":""10001"",""permission_code"":9}]}",
                    fixture.Context("sp-merge")));

                Assert.Equal("OK", response["code"]);
                Assert.Equal(1, Convert.ToInt32(response["total"]));
                Assert.Equal(9, fixture.UserWriter.PermissionLevels["10001"]);
            }
        }

        [TestCase]
        public static void SyncPermissions_OfflineDeviceWritesRetryIntent()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOfflineDevice(1);

                var response = fixture.Response(fixture.Service.SyncPermissions(@"{""items"":[{""employee_id"":""10001"",""permission_code"":7}]}", fixture.Context("sp-offline")));

                Assert.Equal("PARTIAL_SUCCESS", response["code"]);
                Assert.Equal(1, Convert.ToInt32(response["queued"]));
                Assert.Equal(1, fixture.RetryWriter.Intents.Count);
                Assert.Equal("SyncPermission", fixture.RetryWriter.Intents[0].Operation);
                Assert.Equal(7, fixture.RetryWriter.Intents[0].PermissionLevel.Value);
            }
        }

        [TestCase]
        public static void SyncPermissions_BatchTooLargeReturnsCode()
        {
            using (var fixture = new Stage5Fixture())
            {
                var items = string.Join(",", Enumerable.Range(1, 501).Select(index => @"{""employee_id"":""E" + index + @""",""permission_code"":1}"));
                var response = fixture.Response(fixture.Service.SyncPermissions(@"[" + items + @"]", fixture.Context("sp-too-large")));

                Assert.Equal(false, response["success"]);
                Assert.Equal("BATCH_TOO_LARGE", response["code"]);
            }
        }

        [TestCase]
        public static void SyncPersons_UploadsPersonBeforeFace()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice();
                var face = Convert.ToBase64String(JpegBytes());

                var response = fixture.Response(fixture.Service.SyncPersons(@"{""people"":[{""employee_id"":""10001"",""name"":""张三"",""face_image_base64"":""" + face + @"""}]}", fixture.Context("persons-face")));

                Assert.Equal("OK", response["code"]);
                Assert.Equal(1, Convert.ToInt32(response["facesUploaded"]));
                var modifyIndex = fixture.Gateway.Calls.ToList().FindIndex(call => call.MethodName == "UpsertPersonAsync");
                var faceIndex = fixture.Gateway.Calls.ToList().FindIndex(call => call.MethodName == "UploadFaceAsync");
                Assert.True(modifyIndex >= 0);
                Assert.True(faceIndex > modifyIndex);
            }
        }

        [TestCase]
        public static void SyncPersons_SupportsDataUriAndAliases()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice();
                var face = "data:image/jpeg;base64," + Convert.ToBase64String(JpegBytes());

                var response = fixture.Response(fixture.Service.SyncPersons(
                    @"{""data"":[{""employeeNo"":""10001"",""fullName"":""王五"",""active"":true,""validFrom"":""2026-01-01T00:00:00"",""validTo"":""2030-01-01T00:00:00"",""faceImageBase64"":""" + face + @""",""faceImageFormat"":""jpg""}]}",
                    fixture.Context("persons-alias")));

                Assert.Equal("OK", response["code"]);
                Assert.Equal(1, Convert.ToInt32(response["facesUploaded"]));
            }
        }

        [TestCase]
        public static void SyncPersons_WithoutFaceOnlySyncsPerson()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice();

                var response = fixture.Response(fixture.Service.SyncPersons(@"{""items"":[{""employeeId"":""10001"",""fullName"":""李四""}]}", fixture.Context("persons-only")));

                Assert.Equal("OK", response["code"]);
                Assert.Equal(1, Convert.ToInt32(response["succeeded"]));
                Assert.Equal(0, Convert.ToInt32(response["facesUploaded"]));
                Assert.True(fixture.Gateway.Calls.Any(call => call.MethodName == "UpsertPersonAsync"));
                Assert.False(fixture.Gateway.Calls.Any(call => call.MethodName == "UploadFaceAsync"));
            }
        }

        [TestCase]
        public static void SyncPersons_PersonFailureSkipsFaceForThatDevice()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice();
                fixture.Gateway.ConfigureException("UpsertPersonAsync", new InvalidOperationException("人员失败"));
                var face = Convert.ToBase64String(new byte[] { 1, 2, 3 });

                var response = fixture.Response(fixture.Service.SyncPersons(@"{""records"":[{""employee_no"":""10001"",""face_image_base64"":""" + face + @"""}]}", fixture.Context("persons-fail")));

                Assert.Equal("FAILED", response["code"]);
                Assert.False(fixture.Gateway.Calls.Any(call => call.MethodName == "UploadFaceAsync"));
            }
        }

        [TestCase]
        public static void SyncPersons_FaceTooLargeReturnsCode()
        {
            using (var fixture = new Stage5Fixture())
            {
                var face = Convert.ToBase64String(new byte[205 * 1024]);

                var response = fixture.Response(fixture.Service.SyncPersons(@"{""data"":[{""employee_id"":""10001"",""face_image_base64"":""" + face + @"""}]}", fixture.Context("face-large")));

                Assert.Equal(false, response["success"]);
                Assert.Equal("FACE_TOO_LARGE", response["code"]);
            }
        }

        [TestCase]
        public static void SyncPersons_InvalidFaceBase64ReturnsInvalidArgument()
        {
            using (var fixture = new Stage5Fixture())
            {
                var response = fixture.Response(fixture.Service.SyncPersons(@"{""items"":[{""employee_id"":""10001"",""face_image_base64"":""not-base64""}]}", fixture.Context("face-invalid")));

                Assert.Equal(false, response["success"]);
                Assert.Equal("INVALID_ARGUMENT", response["code"]);
            }
        }

        [TestCase]
        public static void SyncPersons_EmptyRequestReturnsInvalidArgument()
        {
            using (var fixture = new Stage5Fixture())
            {
                var response = fixture.Response(fixture.Service.SyncPersons(@"{}", fixture.Context("persons-empty")));

                Assert.Equal(false, response["success"]);
                Assert.Equal("INVALID_ARGUMENT", response["code"]);
            }
        }

        [TestCase]
        public static void SyncPersons_OfflinePersonWithFaceWritesBothRetryIntents()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOfflineDevice();

                var response = fixture.Response(fixture.Service.SyncPersons(@"{""people"":[{""employee_id"":""10001"",""name"":""赵六"",""face_image_base64"":""" + Convert.ToBase64String(JpegBytes()) + @"""}]}", fixture.Context("persons-offline")));

                Assert.Equal("PARTIAL_SUCCESS", response["code"]);
                Assert.Equal(1, Convert.ToInt32(response["queued"]));
                Assert.True(fixture.RetryWriter.Intents.Any(intent => intent.Operation == "SyncPerson"));
                Assert.True(fixture.RetryWriter.Intents.Any(intent => intent.Operation == "UploadFace"));
            }
        }

        [TestCase]
        public static void SyncPersons_RetryableSdkTimeoutQueuesIntent()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice();
                fixture.Gateway.ConfigureTimeout("UpsertPersonAsync");

                var response = fixture.Response(fixture.Service.SyncPersons(@"{""items"":[{""employee_id"":""10001"",""name"":""钱七""}]}", fixture.Context("persons-timeout")));

                Assert.Equal("PARTIAL_SUCCESS", response["code"]);
                Assert.True(fixture.RetryWriter.Intents.Any(intent => intent.Operation == "SyncPerson"));
            }
        }

        [TestCase]
        public static void SyncPersons_DeviceUnsupportedFailsWithoutRetryIntent()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice();
                fixture.Gateway.ConfigureException("UpsertPersonAsync", new DeviceGatewayException("UpsertPerson", SdkError.FromCode(23, "设备不支持该功能")));

                var response = fixture.Response(fixture.Service.SyncPersons(@"{""items"":[{""employee_id"":""10001"",""name"":""孙八""}]}", fixture.Context("persons-unsupported")));

                Assert.Equal("FAILED", response["code"]);
                Assert.Equal(0, fixture.RetryWriter.Intents.Count);
            }
        }

        [TestCase]
        public static void DeleteFaces_AcceptsStringArrayAndQueuesOffline()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOfflineDevice();

                var response = fixture.Response(fixture.Service.DeleteFaces(@"[""10001"",""10002""]", fixture.Context("delete-face")));

                Assert.Equal("PARTIAL_SUCCESS", response["code"]);
                Assert.Equal(2, Convert.ToInt32(response["queued"]));
                Assert.True(fixture.RetryWriter.Intents.All(intent => intent.Operation == "DeleteFace"));
            }
        }

        [TestCase]
        public static void QueuedDetails_ContainTraceableRetryFields()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOfflineDevice();

                var response = fixture.Response(fixture.Service.DeletePersons(@"[""10001""]", fixture.Context("queued-details")));
                var queuedDetails = (ArrayList)response["queuedDetails"];
                var first = (Dictionary<string, object>)queuedDetails[0];

                Assert.Equal("PARTIAL_SUCCESS", response["code"]);
                Assert.Equal(1, Convert.ToInt32(first["deviceId"]));
                Assert.Equal("10001", first["employeeId"]);
                Assert.Equal("DeletePerson", first["operation"]);
                Assert.True(first.ContainsKey("createdAt"));
            }
        }

        [TestCase]
        public static void DeletePersons_OnlineDeviceIsIdempotentSuccess()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice();

                var response = fixture.Response(fixture.Service.DeletePersons(@"{""items"":[{""employeeId"":""missing""}]}", fixture.Context("delete-person")));

                Assert.Equal("OK", response["code"]);
                Assert.Equal(1, Convert.ToInt32(response["succeeded"]));
                Assert.True(fixture.Gateway.Calls.Any(call => call.MethodName == "DeletePersonAsync"));
            }
        }

        [TestCase]
        public static void GetFaces_ReturnsEmployeeAndDeviceDetails()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice();
                fixture.Service.SyncPersons(@"{""items"":[{""employee_id"":""10001"",""name"":""张三"",""face_image_base64"":""" + Convert.ToBase64String(JpegBytes()) + @"""}]}", fixture.Context("seed-face"));

                var response = fixture.Response(fixture.Service.GetFaces(@"{""records"":[{""employee_no"":""10001""}]}", fixture.Context("get-face")));

                Assert.Equal("OK", response["code"]);
                Assert.Equal(1, Convert.ToInt32(response["succeeded"]));
                Assert.Equal(1, Convert.ToInt32(response["targetDevices"]));
            }
        }

        [TestCase]
        public static void CaptureFaceStream_ReturnsFrameAndStatus()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice();

                var frames = fixture.Service.CaptureFaceStream(@"{""employee_id"":""10001""}", fixture.Context("capture")).ToList();
                var frame = fixture.Response(frames[0]);
                var status = fixture.Response(fixture.Service.GetEnrollmentStatus(@"{""employee_id"":""10001""}", fixture.Context("capture-status")));

                Assert.Equal(1, frames.Count);
                Assert.Equal("OK", frame["code"]);
                Assert.Equal(1, Convert.ToInt32(frame["frameIndex"]));
                Assert.True(!string.IsNullOrWhiteSpace(Convert.ToString(frame["faceImageBase64"])));
                Assert.Equal("Succeeded", status["status"]);
            }
        }

        [TestCase]
        public static void CaptureFaceStream_NoOnlineDeviceReturnsFailureFrame()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOfflineDevice();

                var frame = fixture.Response(fixture.Service.CaptureFaceStream(@"{""employee_id"":""10001""}", fixture.Context("capture-offline")).First());

                Assert.Equal(false, frame["success"]);
                Assert.Equal("DEVICE_ERROR", frame["code"]);
            }
        }

        [TestCase]
        public static void GetEnrollmentStatus_MissingTaskReturnsNotFound()
        {
            using (var fixture = new Stage5Fixture())
            {
                var response = fixture.Response(fixture.Service.GetEnrollmentStatus(@"{""taskId"":""missing""}", fixture.Context("missing-task")));

                Assert.Equal(false, response["success"]);
                Assert.Equal("NOT_FOUND", response["code"]);
            }
        }

        [TestCase]
        public static void InvalidJson_ReturnsInvalidArgument()
        {
            using (var fixture = new Stage5Fixture())
            {
                var response = fixture.Response(fixture.Service.GetFaces("{bad", fixture.Context("bad-json")));

                Assert.Equal(false, response["success"]);
                Assert.Equal("INVALID_ARGUMENT", response["code"]);
            }
        }

        private static byte[] JpegBytes()
        {
            return new byte[] { 0xFF, 0xD8, 0x01, 0x02, 0xFF, 0xD9 };
        }
    }

    internal sealed class Stage5Fixture : IDisposable
    {
        private readonly Stage4Fixture inner = new Stage4Fixture();
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public Stage5Fixture()
        {
            RetryWriter = new RecordingRetryWriter();
            UserWriter = new RecordingUserSyncStatusWriter();
            EnrollmentStore = new EnrollmentTaskStore();
            Service = new PermissionSyncGrpcService(inner.Registry, inner.Dispatcher, inner.Gateway, RetryWriter, UserWriter, EnrollmentStore);
        }

        public PermissionSyncGrpcService Service { get; }

        public RecordingRetryWriter RetryWriter { get; }

        public RecordingUserSyncStatusWriter UserWriter { get; }

        public EnrollmentTaskStore EnrollmentStore { get; }

        public ControlDoor.Hikvision.MockHikvisionGateway Gateway => inner.Gateway;

        public void AddOnlineDevice(int deviceId = 1)
        {
            inner.AddRecord(deviceId);
            inner.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
            var login = inner.Lifecycle.SubmitLogin(deviceId, wait: true, requestId: "stage5-login");
            Assert.True(login.Success, login.Message);
            inner.Registry.UpdateCapabilities(deviceId, new ControlDoor.Devices.Runtime.DeviceCapabilities
            {
                Known = true,
                SupportsFaceCapture = true,
                SupportsFaceConfig = true,
                SupportsPersonConfig = true,
                SupportsAcs = true
            }, DateTime.Now);
        }

        public void AddOfflineDevice(int deviceId = 1)
        {
            inner.AddRecord(deviceId);
            inner.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
        }

        public GrpcRequestContext Context(string requestId)
        {
            return new GrpcRequestContext { RequestId = requestId };
        }

        public Dictionary<string, object> Response(string json)
        {
            return serializer.Deserialize<Dictionary<string, object>>(json);
        }

        public void Dispose()
        {
            inner.Dispose();
        }

    }

    internal sealed class RecordingRetryWriter : IDeviceOperationRetryWriter
    {
        public IList<DeviceOperationRetryIntent> Intents { get; } = new List<DeviceOperationRetryIntent>();

        public DeviceOperationRetryWriteResult UpsertIntent(DeviceOperationRetryIntent intent)
        {
            Intents.Add(intent);
            return DeviceOperationRetryWriteResult.Ok(intent);
        }
    }

    internal sealed class RecordingUserSyncStatusWriter : IUserSyncStatusWriter
    {
        public IDictionary<string, int> PermissionLevels { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public IList<string> PersonsSynced { get; } = new List<string>();

        public IList<string> PersonsDeleted { get; } = new List<string>();

        public void MarkPermissionSynced(string employeeId, int permissionLevel)
        {
            PermissionLevels[employeeId] = permissionLevel;
        }

        public void MarkPersonSynced(string employeeId)
        {
            PersonsSynced.Add(employeeId);
        }

        public void MarkPersonDeleted(string employeeId)
        {
            PersonsDeleted.Add(employeeId);
        }
    }
}
