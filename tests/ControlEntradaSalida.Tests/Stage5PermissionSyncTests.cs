using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using ControlDoor.Devices.Management;
using ControlDoor.Devices.Runtime;
using ControlDoor.GrpcApi;
using ControlDoor.Hikvision;
using ControlDoor.Observability;
using ControlDoor.Permissions;
using ControlDoor.Configuration;

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
                Assert.True(fixture.Gateway.Calls.Count(call => call.MethodName == "UpsertPersonAsync") >= 4);
                Assert.False(fixture.Gateway.Calls.Any(call => call.MethodName == "SetPermissionAsync"));
                Assert.True(fixture.Gateway.Calls
                    .Where(call => call.MethodName == "UpsertPersonAsync")
                    .Select(call => (UpsertPersonRequest)call.Request)
                    .All(request => request.ProvisioningMode == PersonProvisioningMode.Permission));
            }
        }

        [TestCase]
        public static void SyncPermissions_DuplicateEmployeeKeepsLastPermission()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice(description: "办公区域");

                var response = fixture.Response(fixture.Service.SyncPermissions(
                    @"{""items"":[{""employee_id"":""10001"",""permission_code"":1},{""employee_id"":""10001"",""permission_code"":9}]}",
                    fixture.Context("sp-merge")));

                Assert.Equal("OK", response["code"]);
                Assert.Equal(1, Convert.ToInt32(response["total"]));
                Assert.Equal(9, fixture.UserWriter.PermissionLevels["10001"]);
                var person = fixture.LastUpsertPerson("10001");
                Assert.False(person.Enabled);
            }
        }

        [TestCase]
        public static void SyncPermissions_LevelOneEnablesOnlyOfficeDevices()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice(deviceId: 1, description: "办公区域");
                fixture.AddOnlineDevice(deviceId: 2, description: "生产区域");
                fixture.AddOnlineDevice(deviceId: 3, description: "主入口");

                var response = fixture.Response(fixture.Service.SyncPermissions(
                    @"{""items"":[{""employee_id"":""10001"",""name"":""张三"",""permission_code"":1}]}",
                    fixture.Context("sp-area-level1")));

                Assert.Equal("OK", response["code"]);
                Assert.Equal(3, fixture.Gateway.Calls.Count(call => call.MethodName == "UpsertPersonAsync"));
                Assert.False(fixture.Gateway.Calls.Any(call => call.MethodName == "SetPermissionAsync"));
                var persons = fixture.UpsertPersons("10001").ToList();
                Assert.Equal(true, persons[0].Enabled);
                Assert.Equal(false, persons[1].Enabled);
                Assert.Equal(false, persons[2].Enabled);
            }
        }

        [TestCase]
        public static void SyncPermissions_LevelTwoEnablesOfficeProductionAndOtherDevices()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice(deviceId: 1, description: "办公区域");
                fixture.AddOnlineDevice(deviceId: 2, description: "生产区域");
                fixture.AddOnlineDevice(deviceId: 3, description: string.Empty);

                var response = fixture.Response(fixture.Service.SyncPermissions(
                    @"{""items"":[{""employee_id"":""10001"",""permission_code"":2}]}",
                    fixture.Context("sp-area-level2")));

                Assert.Equal("OK", response["code"]);
                var persons = fixture.UpsertPersons("10001").ToList();
                Assert.True(persons.All(item => item.Enabled));
            }
        }

        [TestCase]
        public static void SyncPermissions_LevelZeroAndUnknownDisableAllDevices()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice(deviceId: 1, description: "办公区域");
                fixture.AddOnlineDevice(deviceId: 2, description: "生产区域");

                var zero = fixture.Response(fixture.Service.SyncPermissions(
                    @"{""items"":[{""employee_id"":""10001"",""permission_code"":0}]}",
                    fixture.Context("sp-area-zero")));
                var unknown = fixture.Response(fixture.Service.SyncPermissions(
                    @"{""items"":[{""employee_id"":""10002"",""permission_code"":9}]}",
                    fixture.Context("sp-area-unknown")));

                Assert.Equal("OK", zero["code"]);
                Assert.Equal("OK", unknown["code"]);
                Assert.True(fixture.UpsertPersons("10001").All(item => !item.Enabled));
                Assert.True(fixture.UpsertPersons("10002").All(item => !item.Enabled));
            }
        }

        [TestCase]
        public static void SyncPermissions_OfflineDeviceWritesRetryIntent()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOfflineDevice(1);

                var response = fixture.Response(fixture.Service.SyncPermissions(@"{""items"":[{""employee_id"":""10001"",""name_alias"":""张三"",""permission_code"":7}]}", fixture.Context("sp-offline")));

                Assert.Equal("PARTIAL_SUCCESS", response["code"]);
                Assert.Equal(1, Convert.ToInt32(response["queued"]));
                Assert.Equal(1, fixture.RetryWriter.Intents.Count);
                Assert.Equal("SyncPermission", fixture.RetryWriter.Intents[0].Operation);
                Assert.Equal(7, fixture.RetryWriter.Intents[0].PermissionLevel.Value);
                Assert.Contains("张三", fixture.RetryWriter.Intents[0].PermissionPayloadJson);
            }
        }

        [TestCase]
        public static void SyncPermissions_OneOfMultipleTargetDevicesQueued_DoesNotMarkPermissionSynced()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice(1);
                fixture.AddOfflineDevice(2);

                var response = fixture.Response(fixture.Service.SyncPermissions(
                    @"{""items"":[{""employee_id"":""10001"",""permission_code"":7}]}",
                    fixture.Context("sp-partial-queued")));

                Assert.Equal("PARTIAL_SUCCESS", response["code"]);
                Assert.Equal(0, Convert.ToInt32(response["updated"]));
                Assert.Equal(1, Convert.ToInt32(response["queued"]));
                var items = (ArrayList)response["items"];
                var item = (IDictionary<string, object>)items[0];
                Assert.Equal(false, item["success"]);
                Assert.Equal(true, item["queued"]);
                Assert.False(fixture.UserWriter.PermissionLevels.ContainsKey("10001"));
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
                var request = (UpsertPersonRequest)fixture.Gateway.Calls[modifyIndex].Request;
                Assert.Equal(PersonProvisioningMode.Person, request.ProvisioningMode);
            }
        }

        [TestCase]
        public static void SyncPersons_TargetsAcsFaceRecognitionDoorDevices()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice(deviceId: 1, types: new[] { DeviceType.Acs });
                fixture.AddOnlineDevice(deviceId: 2, types: new[] { DeviceType.FaceCapture });
                var face = Convert.ToBase64String(JpegBytes());

                var response = fixture.Response(fixture.Service.SyncPersons(@"{""people"":[{""employee_id"":""10001"",""name"":""张三"",""face_image_base64"":""" + face + @"""}]}", fixture.Context("persons-acs-face")));

                Assert.Equal("OK", response["code"]);
                Assert.Equal(1, Convert.ToInt32(response["targetDevices"]));
                Assert.Equal(1, Convert.ToInt32(response["facesUploaded"]));
                Assert.Equal(1, fixture.Gateway.Calls.Count(call => call.MethodName == "UpsertPersonAsync"));
                Assert.Equal(1, fixture.Gateway.Calls.Count(call => call.MethodName == "UploadFaceAsync"));
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
                var calls = fixture.Gateway.Calls.ToList();
                var faceIndex = calls.FindIndex(call => call.MethodName == "DeleteFaceAsync");
                var personIndex = calls.FindIndex(call => call.MethodName == "DeletePersonAsync");
                Assert.True(faceIndex >= 0);
                Assert.True(personIndex > faceIndex);
                Assert.True(fixture.UserWriter.PersonsDeleted.Contains("missing"));
            }
        }

        [TestCase]
        public static void DeletePersons_OneOfMultipleTargetDevicesQueued_DoesNotMarkPersonDeleted()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice(1);
                fixture.AddOfflineDevice(2);

                var response = fixture.Response(fixture.Service.DeletePersons(
                    @"{""items"":[{""employeeId"":""10001""}]}",
                    fixture.Context("delete-person-partial-queued")));

                Assert.Equal("PARTIAL_SUCCESS", response["code"]);
                Assert.Equal(0, Convert.ToInt32(response["succeeded"]));
                Assert.Equal(1, Convert.ToInt32(response["queued"]));
                var items = (ArrayList)response["items"];
                var item = (IDictionary<string, object>)items[0];
                Assert.Equal(false, item["success"]);
                Assert.Equal(true, item["queued"]);
                Assert.False(fixture.UserWriter.PersonsDeleted.Contains("10001"));
            }
        }

        [TestCase]
        public static void DeletePersons_DeleteFaceFailure_StillDeletesPersonAndWritesWarnLog()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice();
                fixture.Gateway.ConfigureException("DeleteFaceAsync", new DeviceGatewayException("DeleteFace", SdkError.FromCode(17, "face missing")));

                var response = fixture.Response(fixture.Service.DeletePersons(@"{""items"":[{""employeeId"":""10001""}]}", fixture.Context("delete-person-face-fail")));
                var deviceErrors = (ArrayList)response["deviceErrors"];

                Assert.Equal("OK", response["code"]);
                Assert.Equal(1, Convert.ToInt32(response["succeeded"]));
                Assert.Equal(0, Convert.ToInt32(response["failed"]));
                Assert.Equal(0, Convert.ToInt32(response["queued"]));
                Assert.Equal(0, deviceErrors.Count);
                Assert.True(fixture.Gateway.Calls.Any(call => call.MethodName == "DeleteFaceAsync"));
                Assert.True(fixture.Gateway.Calls.Any(call => call.MethodName == "DeletePersonAsync"));
                Assert.True(fixture.UserWriter.PersonsDeleted.Contains("10001"));
                Assert.False(fixture.RetryWriter.Intents.Any(intent => intent.Operation == "DeletePerson"));
                var log = fixture.ReadLog();
                Assert.Contains("level=Warn", log);
                Assert.Contains("operationName=\"DeleteFaceBeforePerson\"", log);
                Assert.Contains("employeeId=\"10001\"", log);
                Assert.Contains("userId", log);
                Assert.Contains("DeviceGatewayException", log);
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
                var items = (ArrayList)response["items"];
                var item = (Dictionary<string, object>)items[0];
                var devices = (ArrayList)item["devices"];
                var device = (Dictionary<string, object>)devices[0];
                Assert.Equal(1, Convert.ToInt32(device["faceCount"]));
                Assert.Equal(true, device["exists"]);
                Assert.True(device.ContainsKey("rawResponse"));
                Assert.True(device.ContainsKey("faces"));
            }
        }

        [TestCase]
        public static void CaptureFaceStream_ReturnsFrameAndStatus()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice(types: new[] { DeviceType.FaceCapture });

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
        private readonly string runDirectory = TestWorkspace.Create();
        private readonly ServiceLogger logger;
        private readonly Stage4Fixture inner;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public Stage5Fixture()
            : this(defaultFaceCaptureDeviceId: null)
        {
        }

        public Stage5Fixture(int? defaultFaceCaptureDeviceId)
        {
            logger = new ServiceLogger(LogOptions.FromSettings(runDirectory, new LoggingOptions { LogDirectory = "logs" }));
            inner = new Stage4Fixture(logger);
            RetryWriter = new RecordingRetryWriter();
            UserWriter = new RecordingUserSyncStatusWriter();
            EnrollmentStore = new EnrollmentTaskStore();
            Service = new PermissionSyncGrpcService(inner.Registry, inner.Dispatcher, inner.Gateway, RetryWriter, UserWriter, EnrollmentStore, logger, defaultFaceCaptureDeviceId);
        }

        public PermissionSyncGrpcService Service { get; }

        public ControlDoor.Devices.Runtime.DeviceRuntimeRegistry Registry => inner.Registry;

        public RecordingRetryWriter RetryWriter { get; }

        public RecordingUserSyncStatusWriter UserWriter { get; }

        public EnrollmentTaskStore EnrollmentStore { get; }

        public ControlDoor.Hikvision.MockHikvisionGateway Gateway => inner.Gateway;

        public void AddOnlineDevice(int deviceId = 1, IEnumerable<DeviceType> types = null, string description = "测试设备")
        {
            inner.AddRecord(deviceId, ipAddress: "192.168.1." + (63 + deviceId), description: description);
            ApplyTypes(deviceId, types);
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

        public void AddOfflineDevice(int deviceId = 1, IEnumerable<DeviceType> types = null, string description = "测试设备")
        {
            inner.AddRecord(deviceId, ipAddress: "192.168.1." + (63 + deviceId), description: description);
            ApplyTypes(deviceId, types);
            inner.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
        }

        public PersonInfo LastUpsertPerson(string employeeId)
        {
            var call = Gateway.Calls.Last(item => item.MethodName == "UpsertPersonAsync" &&
                ((UpsertPersonRequest)item.Request).Person.EmployeeId == employeeId);
            return ((UpsertPersonRequest)call.Request).Person;
        }

        public IEnumerable<PersonInfo> UpsertPersons(string employeeId)
        {
            return Gateway.Calls
                .Where(item => item.MethodName == "UpsertPersonAsync")
                .Select(item => (UpsertPersonRequest)item.Request)
                .Where(item => item.Person.EmployeeId == employeeId)
                .Select(item => item.Person);
        }

        private void ApplyTypes(int deviceId, IEnumerable<DeviceType> types)
        {
            if (types == null)
            {
                return;
            }

            var record = inner.Repository.GetByDeviceId(deviceId);
            record.Types = types.ToList();
            inner.Repository.Add(record);
        }

        public GrpcRequestContext Context(string requestId)
        {
            return new GrpcRequestContext { RequestId = requestId };
        }

        public Dictionary<string, object> Response(string json)
        {
            return serializer.Deserialize<Dictionary<string, object>>(json);
        }

        public string ReadLog()
        {
            return System.IO.File.Exists(logger.CurrentLogPath)
                ? System.IO.File.ReadAllText(logger.CurrentLogPath)
                : string.Empty;
        }

        public void Dispose()
        {
            inner.Dispose();
            logger.Dispose();
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
