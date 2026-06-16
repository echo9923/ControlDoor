using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Hikvision;

namespace ControlEntradaSalida.Tests
{
    public static class Stage3GatewayTests
    {
        [TestCase]
        public static void GatewayContract_ExposesPlannedCapabilityGroups()
        {
            var methods = typeof(IHikvisionGateway).GetMethods().Select(item => item.Name).ToList();

            Assert.True(methods.Contains("LoginAsync"));
            Assert.True(methods.Contains("LogoutAsync"));
            Assert.True(methods.Contains("SetAlarmAsync"));
            Assert.True(methods.Contains("CloseAlarmAsync"));
            Assert.True(methods.Contains("GetAlarmDeploymentStatusAsync"));
            Assert.True(methods.Contains("AddPersonAsync"));
            Assert.True(methods.Contains("UpsertPersonAsync"));
            Assert.True(methods.Contains("DeletePersonAsync"));
            Assert.True(methods.Contains("ModifyPersonAsync"));
            Assert.True(methods.Contains("QueryPersonAsync"));
            Assert.True(methods.Contains("UploadFaceAsync"));
            Assert.True(methods.Contains("DeleteFaceAsync"));
            Assert.True(methods.Contains("QueryFaceAsync"));
            Assert.True(methods.Contains("SetPermissionAsync"));
            Assert.True(methods.Contains("QueryPermissionAsync"));
            Assert.True(methods.Contains("ControlGatewayAsync"));
            Assert.True(methods.Contains("CaptureFaceAsync"));
            Assert.True(methods.Contains("CapturePictureAsync"));
            Assert.True(methods.Contains("QueryEventRecordAsync"));
            Assert.True(methods.Contains("SendIsapiRequestAsync"));
            Assert.True(methods.Contains("GetDeviceCapabilitiesAsync"));
            Assert.True(methods.Contains("GetDeviceInfoAsync"));
            Assert.True(methods.Contains("GetLastErrorCode"));
            Assert.True(methods.Contains("GetErrorMessage"));
        }

        [TestCase]
        public static void GatewayContract_PublicDtos_DoNotExposeNativePointers()
        {
            var dtoTypes = typeof(LoginRequest).Assembly.GetTypes()
                .Where(type => type.IsPublic && type.Namespace == "ControlDoor.Hikvision" && !type.IsEnum && type != typeof(HikvisionSdkWrapper))
                .ToList();

            foreach (var type in dtoTypes)
            {
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    Assert.False(property.PropertyType == typeof(IntPtr), type.FullName + "." + property.Name + " exposes IntPtr.");
                    Assert.False(property.PropertyType == typeof(UIntPtr), type.FullName + "." + property.Name + " exposes UIntPtr.");
                    Assert.False(property.PropertyType == typeof(uint), type.FullName + "." + property.Name + " exposes uint.");
                }
            }
        }

        [TestCase]
        public static void SdkError_CommonCodes_ReturnChineseMessages()
        {
            Assert.Equal("成功", SdkError.FromCode(0).Message);
            Assert.Contains("用户名", SdkError.FromCode(1).Message);
            Assert.Contains("连接设备失败", SdkError.FromCode(7).Message);
            Assert.Contains("超时", SdkError.FromCode(52).Message);
            Assert.Contains("资源不存在", SdkError.FromHttpStatusCode(404).Message);
        }

        [TestCase]
        public static void DeviceGatewayException_IncludesOperationAndCode()
        {
            var ex = new DeviceGatewayException("Login", SdkError.FromCode(1));

            Assert.Contains("Login", ex.Message);
            Assert.Equal(1, ex.Error.Code);
            Assert.Equal("Login", ex.OperationName);
        }

        [TestCase]
        public static void Validator_LoginRequest_RejectsMissingAddress()
        {
            Expect<ArgumentException>(() => HikvisionGatewayValidator.RequireLoginRequest(new LoginRequest
            {
                UserName = "admin",
                Password = "12345"
            }));
        }

        [TestCase]
        public static void Validator_FaceBytes_AcceptsBase64Jpeg()
        {
            var bytes = JpegBytes();
            var face = new FaceInfo
            {
                EmployeeId = "10001",
                ImageBase64 = Convert.ToBase64String(bytes)
            };

            var resolved = HikvisionGatewayValidator.ResolveFaceBytes(face);

            Assert.Equal(bytes.Length, resolved.Length);
            Assert.True(HikvisionGatewayValidator.LooksLikeJpeg(resolved));
        }

        [TestCase]
        public static void Validator_FaceBytes_RejectsNonJpeg()
        {
            Expect<DeviceGatewayException>(() => HikvisionGatewayValidator.RequireFace(new FaceInfo
            {
                EmployeeId = "10001",
                ImageBytes = new byte[] { 1, 2, 3 }
            }, 1024));
        }

        [TestCase]
        public static void Validator_FaceBytes_RejectsOversizedImage()
        {
            Expect<DeviceGatewayException>(() => HikvisionGatewayValidator.RequireFace(new FaceInfo
            {
                EmployeeId = "10001",
                ImageBytes = JpegBytes()
            }, 2));
        }

        [TestCase]
        public static void CapabilityValidator_ReturnsMissingCapabilities()
        {
            var caps = new DeviceCapabilities
            {
                Known = true,
                SupportsAcs = true
            };

            var missing = DeviceCapabilityValidator.GetMissingCapabilities(caps, new[] { DeviceCapability.Acs, DeviceCapability.FaceConfig });

            Assert.Equal(1, missing.Count);
            Assert.Equal(DeviceCapability.FaceConfig, missing[0]);
        }

        [TestCase]
        public static void CapabilityValidator_ThrowsForUnknownCapabilities()
        {
            Expect<DeviceGatewayException>(() => DeviceCapabilityValidator.ValidateCapabilities(
                new DeviceCapabilities(),
                new[] { DeviceCapability.Isapi }));
        }

        [TestCase]
        public static void XmlParser_Capabilities_MapsRawAbilityText()
        {
            var xml = "<Capabilities><Acs>true</Acs><Face>true</Face><CaptureFace>true</CaptureFace><ISAPI>true</ISAPI><doorNum>2</doorNum><model>DS-K1T</model></Capabilities>";

            var caps = ParseCapabilities(xml);

            Assert.True(caps.Known);
            Assert.True(caps.SupportsAcs);
            Assert.True(caps.SupportsFaceConfig);
            Assert.True(caps.SupportsFaceCapture);
            Assert.True(caps.SupportsIsapi);
            Assert.Equal(2, caps.DoorCount);
            Assert.Equal("DS-K1T", caps.Model);
        }

        [TestCase]
        public static void XmlParser_DeviceInfo_MapsBasicFields()
        {
            var xml = "<DeviceInfo><model>DS-K1T</model><serialNumber>SN1</serialNumber><firmwareVersion>V1</firmwareVersion><deviceName>门禁</deviceName><doorCount>4</doorCount></DeviceInfo>";

            var info = ParseDeviceInfo(xml);

            Assert.Equal("DS-K1T", info.Model);
            Assert.Equal("SN1", info.SerialNumber);
            Assert.Equal("V1", info.FirmwareVersion);
            Assert.Equal("门禁", info.DeviceName);
            Assert.Equal(4, info.DoorCount);
        }

        [TestCase]
        public static void MockGateway_Login_ReturnsSessionAndRecordsCall()
        {
            var gateway = new MockHikvisionGateway();

            var login = Login(gateway);

            Assert.True(login.UserId > 0);
            Assert.Equal("Mock-Hikvision", login.DeviceInfo.Model);
            Assert.Equal("LoginAsync", gateway.Calls[0].MethodName);
        }

        [TestCase]
        public static void MockGateway_LoginWrongPassword_ThrowsUnifiedError()
        {
            var gateway = new MockHikvisionGateway();

            var ex = Expect<DeviceGatewayException>(() => gateway.LoginAsync(new LoginRequest
            {
                IpAddress = "127.0.0.1",
                UserName = "admin",
                Password = "wrong"
            }).GetAwaiter().GetResult());

            Assert.Equal(1, ex.Error.Code);
            Assert.Equal(1, gateway.GetLastErrorCode());
        }

        [TestCase]
        public static void MockGateway_Logout_RemovesSession()
        {
            var gateway = new MockHikvisionGateway();
            var login = Login(gateway);

            gateway.LogoutAsync(new LogoutRequest { UserId = login.UserId }).GetAwaiter().GetResult();

            Expect<DeviceGatewayException>(() => gateway.SetAlarmAsync(new AlarmSetupRequest { UserId = login.UserId }).GetAwaiter().GetResult());
        }

        [TestCase]
        public static void MockGateway_SetAlarmAndCloseAlarm_Work()
        {
            var gateway = new MockHikvisionGateway();
            var login = Login(gateway);

            var alarm = gateway.SetAlarmAsync(new AlarmSetupRequest { UserId = login.UserId }).GetAwaiter().GetResult();

            Assert.True(alarm.AlarmHandle >= 1000);
            gateway.CloseAlarmAsync(new AlarmCloseRequest { AlarmHandle = alarm.AlarmHandle }).GetAwaiter().GetResult();
        }

        [TestCase]
        public static void MockGateway_CloseAlarmTwice_Throws()
        {
            var gateway = new MockHikvisionGateway();
            var login = Login(gateway);
            var alarm = gateway.SetAlarmAsync(new AlarmSetupRequest { UserId = login.UserId }).GetAwaiter().GetResult();
            gateway.CloseAlarmAsync(new AlarmCloseRequest { AlarmHandle = alarm.AlarmHandle }).GetAwaiter().GetResult();

            var ex = Expect<DeviceGatewayException>(() => gateway.CloseAlarmAsync(new AlarmCloseRequest { AlarmHandle = alarm.AlarmHandle }).GetAwaiter().GetResult());

            Assert.Equal(17, ex.Error.Code);
        }

        [TestCase]
        public static void MockGateway_EmitAlarm_CopiesPayload()
        {
            var gateway = new MockHikvisionGateway();
            AlarmEventData captured = null;
            gateway.OnAlarmEvent += (sender, data) => captured = data;
            var raw = new byte[] { 1, 2, 3 };

            gateway.EmitAlarm(new AlarmEventData
            {
                Command = 0x5002,
                EventType = "COMM_ALARM_ACS",
                EmployeeId = "10001",
                RawPayload = raw
            });
            raw[0] = 9;

            Assert.NotNull(captured);
            Assert.Equal("10001", captured.EmployeeId);
            Assert.Equal(1, captured.RawPayload[0]);
        }

        [TestCase]
        public static void MockGateway_AddPersonThenQuery_ReturnsPerson()
        {
            var gateway = new MockHikvisionGateway();
            var login = Login(gateway);

            gateway.AddPersonAsync(new AddPersonRequest { UserId = login.UserId, Person = Person("10001") }).GetAwaiter().GetResult();
            var result = gateway.QueryPersonAsync(new QueryPersonRequest { UserId = login.UserId, EmployeeId = "10001" }).GetAwaiter().GetResult();

            Assert.True(result.Exists);
            Assert.Equal(1, result.TotalCount);
            Assert.Equal("张三", result.Persons[0].Name);
        }

        [TestCase]
        public static void MockGateway_AddDuplicatePerson_ThrowsDeviceError()
        {
            var gateway = new MockHikvisionGateway();
            var login = Login(gateway);
            gateway.AddPersonAsync(new AddPersonRequest { UserId = login.UserId, Person = Person("10001") }).GetAwaiter().GetResult();

            var ex = Expect<DeviceGatewayException>(() => gateway.AddPersonAsync(new AddPersonRequest { UserId = login.UserId, Person = Person("10001") }).GetAwaiter().GetResult());

            Assert.Equal(29, ex.Error.Code);
        }

        [TestCase]
        public static void MockGateway_ModifyPerson_UpdatesStoredRecord()
        {
            var gateway = new MockHikvisionGateway();
            var login = Login(gateway);
            gateway.AddPersonAsync(new AddPersonRequest { UserId = login.UserId, Person = Person("10001") }).GetAwaiter().GetResult();
            var modified = Person("10001");
            modified.Name = "李四";

            gateway.ModifyPersonAsync(new ModifyPersonRequest { UserId = login.UserId, Person = modified }).GetAwaiter().GetResult();
            var result = gateway.QueryPersonAsync(new QueryPersonRequest { UserId = login.UserId, EmployeeId = "10001" }).GetAwaiter().GetResult();

            Assert.Equal("李四", result.Persons[0].Name);
        }

        [TestCase]
        public static void MockGateway_DeletePerson_RemovesPersonFaceAndPermission()
        {
            var gateway = new MockHikvisionGateway();
            var login = Login(gateway);
            gateway.AddPersonAsync(new AddPersonRequest { UserId = login.UserId, Person = Person("10001") }).GetAwaiter().GetResult();
            gateway.UploadFaceAsync(new UploadFaceRequest { UserId = login.UserId, Face = Face("10001") }).GetAwaiter().GetResult();
            gateway.SetPermissionAsync(PermissionRequest(login.UserId, "10001")).GetAwaiter().GetResult();

            gateway.DeletePersonAsync(new DeletePersonRequest { UserId = login.UserId, EmployeeId = "10001" }).GetAwaiter().GetResult();

            Assert.Equal(0, gateway.QueryPersonAsync(new QueryPersonRequest { UserId = login.UserId, EmployeeId = "10001" }).GetAwaiter().GetResult().TotalCount);
            Assert.Equal(0, gateway.QueryFaceAsync(new QueryFaceRequest { UserId = login.UserId, EmployeeId = "10001" }).GetAwaiter().GetResult().TotalCount);
            Assert.Equal(0, gateway.QueryPermissionAsync(new QueryPermissionRequest { UserId = login.UserId, EmployeeId = "10001" }).GetAwaiter().GetResult().TotalCount);
        }

        [TestCase]
        public static void MockGateway_UploadFaceThenQuery_ReturnsFace()
        {
            var gateway = new MockHikvisionGateway();
            var login = Login(gateway);

            gateway.UploadFaceAsync(new UploadFaceRequest { UserId = login.UserId, Face = Face("10001") }).GetAwaiter().GetResult();
            var result = gateway.QueryFaceAsync(new QueryFaceRequest { UserId = login.UserId, EmployeeId = "10001" }).GetAwaiter().GetResult();

            Assert.True(result.Exists);
            Assert.Equal(1, result.TotalCount);
            Assert.True(result.Faces[0].ImageBytes.Length > 0);
        }

        [TestCase]
        public static void MockGateway_DeleteFace_RemovesOnlyFace()
        {
            var gateway = new MockHikvisionGateway();
            var login = Login(gateway);
            gateway.AddPersonAsync(new AddPersonRequest { UserId = login.UserId, Person = Person("10001") }).GetAwaiter().GetResult();
            gateway.UploadFaceAsync(new UploadFaceRequest { UserId = login.UserId, Face = Face("10001") }).GetAwaiter().GetResult();

            gateway.DeleteFaceAsync(new DeleteFaceRequest { UserId = login.UserId, EmployeeId = "10001" }).GetAwaiter().GetResult();

            Assert.Equal(0, gateway.QueryFaceAsync(new QueryFaceRequest { UserId = login.UserId, EmployeeId = "10001" }).GetAwaiter().GetResult().TotalCount);
            Assert.Equal(1, gateway.QueryPersonAsync(new QueryPersonRequest { UserId = login.UserId, EmployeeId = "10001" }).GetAwaiter().GetResult().TotalCount);
        }

        [TestCase]
        public static void MockGateway_SetPermissionThenQuery_ReturnsPermission()
        {
            var gateway = new MockHikvisionGateway();
            var login = Login(gateway);

            gateway.SetPermissionAsync(PermissionRequest(login.UserId, "10001")).GetAwaiter().GetResult();
            var result = gateway.QueryPermissionAsync(new QueryPermissionRequest { UserId = login.UserId, EmployeeId = "10001" }).GetAwaiter().GetResult();

            Assert.Equal(1, result.TotalCount);
            Assert.Equal("P1", result.Permissions[0].PermissionCode);
            Assert.Equal(1, result.Permissions[0].DoorIndexes[0]);
        }

        [TestCase]
        public static void MockGateway_SetPermission_RejectsEmptyDoorIndexes()
        {
            var gateway = new MockHikvisionGateway();
            var login = Login(gateway);
            var request = new SetPermissionRequest { UserId = login.UserId };
            request.Permissions.Add(new PermissionInfo { EmployeeId = "10001", PermissionCode = "P1" });

            Expect<ArgumentException>(() => gateway.SetPermissionAsync(request).GetAwaiter().GetResult());
        }

        [TestCase]
        public static void MockGateway_ControlGateway_SupportsOpenRestoreAlwaysClose()
        {
            var gateway = new MockHikvisionGateway();
            var login = Login(gateway);

            Assert.True(gateway.ControlGatewayAsync(new GateControlRequest { UserId = login.UserId, GateIndex = 1, Command = GateControlCommand.Open }).GetAwaiter().GetResult().Success);
            Assert.True(gateway.ControlGatewayAsync(new GateControlRequest { UserId = login.UserId, GateIndex = 1, Command = GateControlCommand.Restore }).GetAwaiter().GetResult().Success);
            Assert.True(gateway.ControlGatewayAsync(new GateControlRequest { UserId = login.UserId, GateIndex = 1, Command = GateControlCommand.AlwaysClose }).GetAwaiter().GetResult().Success);
        }

        [TestCase]
        public static void MockGateway_ControlGateway_InvalidDoorThrows()
        {
            var gateway = new MockHikvisionGateway();
            gateway.Capabilities.DoorCount = 1;
            var login = Login(gateway);

            var ex = Expect<DeviceGatewayException>(() => gateway.ControlGatewayAsync(new GateControlRequest { UserId = login.UserId, GateIndex = 2, Command = GateControlCommand.Open }).GetAwaiter().GetResult());

            Assert.Equal(4, ex.Error.Code);
        }

        [TestCase]
        public static void MockGateway_CapturePicture_ReturnsJpegCopy()
        {
            var gateway = new MockHikvisionGateway();
            var login = Login(gateway);

            var capture = gateway.CapturePictureAsync(new CaptureRequest { UserId = login.UserId }).GetAwaiter().GetResult();
            gateway.PictureBytes[0] = 0;

            Assert.Equal("image/jpeg", capture.ContentType);
            Assert.Equal(0xFF, capture.ImageBytes[0]);
        }

        [TestCase]
        public static void MockGateway_CaptureFace_ReturnsQuality()
        {
            var gateway = new MockHikvisionGateway();
            var login = Login(gateway);

            var capture = gateway.CaptureFaceAsync(new CaptureRequest { UserId = login.UserId }).GetAwaiter().GetResult();

            Assert.True(capture.FaceDetected);
            Assert.True(capture.QualityScore > 0);
            Assert.True(capture.ImageBytes.Length > 0);
        }

        [TestCase]
        public static void MockGateway_QueryEventRecord_ReturnsEmptyResponse()
        {
            var gateway = new MockHikvisionGateway();
            var login = Login(gateway);

            var result = gateway.QueryEventRecordAsync(new EventQueryRequest
            {
                UserId = login.UserId,
                BeginTime = DateTime.Now.AddHours(-1),
                EndTime = DateTime.Now
            }).GetAwaiter().GetResult();

            Assert.Equal(0, result.TotalCount);
            Assert.False(result.HasMore);
        }

        [TestCase]
        public static void MockGateway_QueryEventRecord_RejectsInvalidRange()
        {
            var gateway = new MockHikvisionGateway();
            var login = Login(gateway);

            Expect<ArgumentException>(() => gateway.QueryEventRecordAsync(new EventQueryRequest
            {
                UserId = login.UserId,
                BeginTime = DateTime.Now,
                EndTime = DateTime.Now.AddHours(-1)
            }).GetAwaiter().GetResult());
        }

        [TestCase]
        public static void MockGateway_SendIsapiRequest_ReturnsDefaultJson()
        {
            var gateway = new MockHikvisionGateway();

            var response = gateway.SendIsapiRequestAsync(new IsapiRequest { Path = "/ISAPI/System/deviceInfo" }).GetAwaiter().GetResult();

            Assert.Equal(200, response.StatusCode);
            Assert.Equal("{}", response.Body);
        }

        [TestCase]
        public static void MockGateway_GetCapabilities_ReturnsClone()
        {
            var gateway = new MockHikvisionGateway();
            var login = Login(gateway);

            var caps = gateway.GetDeviceCapabilitiesAsync(new DeviceCapabilitiesRequest { UserId = login.UserId }).GetAwaiter().GetResult();
            caps.SupportsAcs = false;

            Assert.True(gateway.Capabilities.SupportsAcs);
        }

        [TestCase]
        public static void MockGateway_GetDeviceInfo_ReturnsClone()
        {
            var gateway = new MockHikvisionGateway();
            var login = Login(gateway);

            var info = gateway.GetDeviceInfoAsync(new DeviceInfoRequest { UserId = login.UserId }).GetAwaiter().GetResult();
            info.Model = "changed";

            Assert.Equal("Mock-Hikvision", gateway.DeviceInfo.Model);
        }

        [TestCase]
        public static void MockGateway_GetAlarmDeploymentStatus_ReturnsClone()
        {
            var gateway = new MockHikvisionGateway
            {
                AlarmDeploymentStatus = new AlarmDeploymentStatus
                {
                    Known = true,
                    IsDeployed = false,
                    RawSetupAlarmStatus = 0,
                    RawSummary = "not deployed"
                }
            };
            var login = Login(gateway);

            var status = gateway.GetAlarmDeploymentStatusAsync(new AlarmDeploymentStatusRequest { UserId = login.UserId }).GetAwaiter().GetResult();
            gateway.AlarmDeploymentStatus.IsDeployed = true;

            Assert.True(status.Known);
            Assert.False(status.IsDeployed);
            Assert.Equal((byte)0, status.RawSetupAlarmStatus);
        }

        [TestCase]
        public static void MockGateway_ConfiguredResult_OverridesDefault()
        {
            var gateway = new MockHikvisionGateway();
            gateway.ConfigureResult("SendIsapiRequestAsync", new IsapiResponse { StatusCode = 404, Body = "missing" });

            var response = gateway.SendIsapiRequestAsync(new IsapiRequest { Path = "/x" }).GetAwaiter().GetResult();

            Assert.Equal(404, response.StatusCode);
            Assert.Equal("missing", response.Body);
        }

        [TestCase]
        public static void MockGateway_ConfiguredException_Throws()
        {
            var gateway = new MockHikvisionGateway();
            gateway.ConfigureException("SendIsapiRequestAsync", new DeviceGatewayException("x", SdkError.FromCode(23)));

            var ex = Expect<DeviceGatewayException>(() => gateway.SendIsapiRequestAsync(new IsapiRequest { Path = "/x" }).GetAwaiter().GetResult());

            Assert.Equal(23, ex.Error.Code);
        }

        [TestCase]
        public static void MockGateway_ConfiguredTimeout_Throws408()
        {
            var gateway = new MockHikvisionGateway();
            gateway.ConfigureTimeout("SendIsapiRequestAsync");

            var ex = Expect<DeviceGatewayException>(() => gateway.SendIsapiRequestAsync(new IsapiRequest { Path = "/x" }).GetAwaiter().GetResult());

            Assert.Equal(408, ex.Error.Code);
        }

        [TestCase]
        public static void MockGateway_ConfiguredDelay_HonorsCancellation()
        {
            var gateway = new MockHikvisionGateway();
            gateway.ConfigureDelay("SendIsapiRequestAsync", TimeSpan.FromSeconds(2));
            var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Expect<OperationCanceledException>(() => gateway.SendIsapiRequestAsync(new IsapiRequest { Path = "/x" }, cancellation.Token).GetAwaiter().GetResult());
        }

        [TestCase]
        public static void MockGateway_Dispose_BlocksFurtherCalls()
        {
            var gateway = new MockHikvisionGateway();
            gateway.Dispose();

            Expect<ObjectDisposedException>(() => gateway.SendIsapiRequestAsync(new IsapiRequest { Path = "/x" }).GetAwaiter().GetResult());
        }

        [TestCase]
        public static void MockGateway_ConcurrentLogin_GeneratesUniqueUserIds()
        {
            var gateway = new MockHikvisionGateway();
            var tasks = Enumerable.Range(0, 10)
                .Select(_ => gateway.LoginAsync(new LoginRequest { IpAddress = "127.0.0.1", UserName = "admin", Password = "12345" }))
                .ToArray();

            Task.WaitAll(tasks);

            Assert.Equal(10, tasks.Select(item => item.Result.UserId).Distinct().Count());
        }

        [TestCase]
        public static void MockGateway_CallsHistory_RecordsRequestObjects()
        {
            var gateway = new MockHikvisionGateway();
            var request = new IsapiRequest { Path = "/ISAPI/System/status" };

            gateway.SendIsapiRequestAsync(request).GetAwaiter().GetResult();

            Assert.Equal("SendIsapiRequestAsync", gateway.Calls.Last().MethodName);
            Assert.True(object.ReferenceEquals(request, gateway.Calls.Last().Request));
        }

        [TestCase]
        public static void IsapiClient_Get_SendsExpectedMethodAndPath()
        {
            var handler = new RecordingHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<ok/>")
            });
            var client = new HikvisionIsapiClient(() => handler);

            var response = client.SendAsync(new IsapiRequest
            {
                BaseAddress = "http://device",
                Path = "/ISAPI/System/deviceInfo",
                Method = IsapiMethod.Get
            }).GetAwaiter().GetResult();

            Assert.Equal(200, response.StatusCode);
            Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
            Assert.Contains("ISAPI/System/deviceInfo", handler.Requests[0].RequestUri.ToString());
        }

        [TestCase]
        public static void IsapiClient_Post_SendsBodyAndContentType()
        {
            var handler = new RecordingHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}")
            });
            var client = new HikvisionIsapiClient(() => handler);

            client.SendAsync(new IsapiRequest
            {
                BaseAddress = "http://device",
                Path = "/ISAPI/AccessControl/User",
                Method = IsapiMethod.Post,
                Body = "{\"id\":1}",
                ContentType = "application/json"
            }).GetAwaiter().GetResult();

            Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
            Assert.Equal("{\"id\":1}", handler.RequestBodies[0]);
        }

        [TestCase]
        public static void IsapiClient_Delete_SupportsDeleteMethod()
        {
            var handler = new RecordingHttpHandler(new HttpResponseMessage(HttpStatusCode.NoContent));
            var client = new HikvisionIsapiClient(() => handler);

            var response = client.SendAsync(new IsapiRequest
            {
                BaseAddress = "device",
                Path = "/ISAPI/item/1",
                Method = IsapiMethod.Delete
            }).GetAwaiter().GetResult();

            Assert.Equal(204, response.StatusCode);
            Assert.Equal(HttpMethod.Delete, handler.Requests[0].Method);
        }

        [TestCase]
        public static void IsapiClient_404_ReturnsResponseWithoutThrowing()
        {
            var handler = new RecordingHttpHandler(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("missing")
            });
            var client = new HikvisionIsapiClient(() => handler);

            var response = client.SendAsync(new IsapiRequest
            {
                BaseAddress = "http://device",
                Path = "/missing"
            }).GetAwaiter().GetResult();

            Assert.Equal(404, response.StatusCode);
            Assert.Equal("missing", response.Body);
            Assert.False(response.IsSuccessStatusCode);
        }

        [TestCase]
        public static void IsapiClient_Timeout_ThrowsGatewayException()
        {
            var handler = new RecordingHttpHandler(async (request, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
            var client = new HikvisionIsapiClient(() => handler);

            var ex = Expect<DeviceGatewayException>(() => client.SendAsync(new IsapiRequest
            {
                BaseAddress = "http://device",
                Path = "/slow",
                TimeoutMilliseconds = 10
            }).GetAwaiter().GetResult());

            Assert.Equal(408, ex.Error.Code);
        }

        [TestCase]
        public static void SdkWrapper_Login_UsesNativeClient()
        {
            var native = new FakeNativeClient();
            var gateway = new HikvisionSdkWrapper(native);

            var login = gateway.LoginAsync(new LoginRequest { IpAddress = "127.0.0.1", UserName = "admin", Password = "12345" }).GetAwaiter().GetResult();

            Assert.Equal(10, login.UserId);
            Assert.True(native.InitCalled);
            Assert.Equal(1, native.LoginCallCount);
        }

        [TestCase]
        public static void SdkWrapper_LoginFailure_ThrowsLastError()
        {
            var native = new FakeNativeClient { LoginUserId = -1, LastError = 1 };
            var gateway = new HikvisionSdkWrapper(native);

            var ex = Expect<DeviceGatewayException>(() => gateway.LoginAsync(new LoginRequest { IpAddress = "127.0.0.1", UserName = "admin", Password = "12345" }).GetAwaiter().GetResult());

            Assert.Equal(1, ex.Error.Code);
        }

        [TestCase]
        public static void SdkWrapper_LogoutFailure_DoesNotThrow()
        {
            var native = new FakeNativeClient { LogoutResult = false };
            var gateway = new HikvisionSdkWrapper(native);
            var login = gateway.LoginAsync(new LoginRequest { IpAddress = "127.0.0.1", UserName = "admin", Password = "12345" }).GetAwaiter().GetResult();

            gateway.LogoutAsync(new LogoutRequest { UserId = login.UserId }).GetAwaiter().GetResult();

            Assert.Equal(1, native.LogoutCallCount);
        }

        [TestCase]
        public static void SdkWrapper_SetAlarm_RegistersCallbackAndReturnsHandle()
        {
            var native = new FakeNativeClient();
            var gateway = new HikvisionSdkWrapper(native);
            var login = gateway.LoginAsync(new LoginRequest { IpAddress = "127.0.0.1", UserName = "admin", Password = "12345" }).GetAwaiter().GetResult();

            var alarm = gateway.SetAlarmAsync(new AlarmSetupRequest { UserId = login.UserId, DeployType = 0 }).GetAwaiter().GetResult();

            Assert.Equal(77, alarm.AlarmHandle);
            Assert.True(native.CallbackRegistered);
            Assert.Equal(0, native.LastAlarmDeployType);
        }

        [TestCase]
        public static void SdkWrapper_NativeAlarm_CopiesPayloadAndRaisesEvent()
        {
            var native = new FakeNativeClient();
            var gateway = new HikvisionSdkWrapper(native);
            AlarmEventData eventData = null;
            gateway.OnAlarmEvent += (sender, data) => eventData = data;
            var login = gateway.LoginAsync(new LoginRequest { IpAddress = "127.0.0.1", UserName = "admin", Password = "12345" }).GetAwaiter().GetResult();
            gateway.SetAlarmAsync(new AlarmSetupRequest { UserId = login.UserId }).GetAwaiter().GetResult();

            native.EmitAlarm(0x5002, new byte[] { 7, 8 });

            Assert.NotNull(eventData);
            Assert.Equal("COMM_ALARM_ACS", eventData.EventType);
            Assert.Equal(2, eventData.RawPayload.Length);
        }

        [TestCase]
        public static void SdkWrapper_CloseAlarmFailure_ThrowsUnifiedError()
        {
            var native = new FakeNativeClient { CloseAlarmResult = false, LastError = 17 };
            var gateway = new HikvisionSdkWrapper(native);
            gateway.LoginAsync(new LoginRequest { IpAddress = "127.0.0.1", UserName = "admin", Password = "12345" }).GetAwaiter().GetResult();

            var ex = Expect<DeviceGatewayException>(() => gateway.CloseAlarmAsync(new AlarmCloseRequest { AlarmHandle = 1 }).GetAwaiter().GetResult());

            Assert.Equal(17, ex.Error.Code);
        }

        [TestCase]
        public static void SdkWrapper_ControlGateway_DelegatesToNative()
        {
            var native = new FakeNativeClient();
            var gateway = new HikvisionSdkWrapper(native);
            var login = gateway.LoginAsync(new LoginRequest { IpAddress = "127.0.0.1", UserName = "admin", Password = "12345" }).GetAwaiter().GetResult();

            var result = gateway.ControlGatewayAsync(new GateControlRequest { UserId = login.UserId, GateIndex = 1, Command = GateControlCommand.AlwaysClose }).GetAwaiter().GetResult();

            Assert.True(result.Success);
            Assert.Equal(GateControlCommand.AlwaysClose, native.LastGateCommand);
        }

        [TestCase]
        public static void SdkWrapper_CapturePicture_ReturnsFileBytesAndDeletesTemp()
        {
            var native = new FakeNativeClient();
            var gateway = new HikvisionSdkWrapper(native);
            var login = gateway.LoginAsync(new LoginRequest { IpAddress = "127.0.0.1", UserName = "admin", Password = "12345" }).GetAwaiter().GetResult();

            var result = gateway.CapturePictureAsync(new CaptureRequest { UserId = login.UserId }).GetAwaiter().GetResult();

            Assert.True(result.ImageBytes.Length > 0);
            Assert.False(File.Exists(native.LastCapturePath));
        }

        [TestCase]
        public static void SdkWrapper_SendIsapiWithoutBaseAddress_UsesStdXml()
        {
            var native = new FakeNativeClient { StdXmlOutput = "<DeviceInfo><model>DS-K1T</model></DeviceInfo>" };
            var gateway = new HikvisionSdkWrapper(native);
            var login = gateway.LoginAsync(new LoginRequest { IpAddress = "127.0.0.1", UserName = "admin", Password = "12345" }).GetAwaiter().GetResult();

            var response = gateway.SendIsapiRequestAsync(new IsapiRequest { UserId = login.UserId, Path = "/ISAPI/System/deviceInfo" }).GetAwaiter().GetResult();

            Assert.Equal(200, response.StatusCode);
            Assert.Contains("DS-K1T", response.Body);
            Assert.Contains("/ISAPI/System/deviceInfo", native.LastStdXmlUrl);
        }

        [TestCase]
        public static void SdkWrapper_GetDeviceCapabilities_ParsesStdXml()
        {
            var native = new FakeNativeClient { StdXmlOutput = "<Capabilities><Acs>true</Acs><Face>true</Face><ISAPI>true</ISAPI><doorNum>2</doorNum></Capabilities>" };
            var gateway = new HikvisionSdkWrapper(native);
            var login = gateway.LoginAsync(new LoginRequest { IpAddress = "127.0.0.1", UserName = "admin", Password = "12345" }).GetAwaiter().GetResult();

            var caps = gateway.GetDeviceCapabilitiesAsync(new DeviceCapabilitiesRequest { UserId = login.UserId }).GetAwaiter().GetResult();

            Assert.True(caps.SupportsAcs);
            Assert.True(caps.SupportsFaceConfig);
            Assert.True(caps.SupportsIsapi);
            Assert.Equal(2, caps.DoorCount);
        }

        [TestCase]
        public static void SdkWrapper_GetDeviceInfo_ParsesStdXml()
        {
            var native = new FakeNativeClient { StdXmlOutput = "<DeviceInfo><model>DS-K1T</model><serialNumber>SN1</serialNumber></DeviceInfo>" };
            var gateway = new HikvisionSdkWrapper(native);
            var login = gateway.LoginAsync(new LoginRequest { IpAddress = "127.0.0.1", UserName = "admin", Password = "12345" }).GetAwaiter().GetResult();

            var info = gateway.GetDeviceInfoAsync(new DeviceInfoRequest { UserId = login.UserId }).GetAwaiter().GetResult();

            Assert.Equal("DS-K1T", info.Model);
            Assert.Equal("SN1", info.SerialNumber);
        }

        [TestCase]
        public static void SdkWrapper_GetAlarmDeploymentStatus_MapsAcsWorkStatus()
        {
            var native = new FakeNativeClient { SetupAlarmStatus = new byte[] { 0 } };
            var gateway = new HikvisionSdkWrapper(native);
            var login = gateway.LoginAsync(new LoginRequest { IpAddress = "127.0.0.1", UserName = "admin", Password = "12345" }).GetAwaiter().GetResult();

            var status = gateway.GetAlarmDeploymentStatusAsync(new AlarmDeploymentStatusRequest { UserId = login.UserId }).GetAwaiter().GetResult();

            Assert.True(status.Known);
            Assert.False(status.IsDeployed);
            Assert.Equal((byte)0, status.RawSetupAlarmStatus);
            Assert.Equal(login.UserId, native.LastAcsWorkStatusUserId);
            Assert.Equal(-1, native.LastAcsWorkStatusChannel);
        }

        [TestCase]
        public static void SdkWrapper_GetAlarmDeploymentStatusFailure_ThrowsLastError()
        {
            var native = new FakeNativeClient { AcsWorkStatusResult = false, LastError = 52 };
            var gateway = new HikvisionSdkWrapper(native);
            var login = gateway.LoginAsync(new LoginRequest { IpAddress = "127.0.0.1", UserName = "admin", Password = "12345" }).GetAwaiter().GetResult();

            var ex = Expect<DeviceGatewayException>(() => gateway.GetAlarmDeploymentStatusAsync(
                new AlarmDeploymentStatusRequest { UserId = login.UserId }).GetAwaiter().GetResult());

            Assert.Equal(52, ex.Error.Code);
        }

        [TestCase]
        public static void SdkWrapper_Dispose_CallsCleanupOnce()
        {
            var native = new FakeNativeClient();
            var gateway = new HikvisionSdkWrapper(native);
            gateway.LoginAsync(new LoginRequest { IpAddress = "127.0.0.1", UserName = "admin", Password = "12345" }).GetAwaiter().GetResult();

            gateway.Dispose();
            gateway.Dispose();

            Assert.Equal(1, native.CleanupCallCount);
        }

        [TestCase]
        public static void SdkWrapper_CaptureFaceSuccess_ReturnsDeviceImageAndQuality()
        {
            var image = new byte[] { 0xFF, 0xD8, 0x10, 0x20, 0xFF, 0xD9 };
            var native = new FakeNativeClient
            {
                FaceCaptureStatus = 1000,
                FaceCaptureImage = image,
                FaceCaptureQuality = 95
            };
            var gateway = new HikvisionSdkWrapper(native);
            var login = gateway.LoginAsync(new LoginRequest { IpAddress = "127.0.0.1", UserName = "admin", Password = "12345" }).GetAwaiter().GetResult();

            var capture = gateway.CaptureFaceAsync(new CaptureRequest { UserId = login.UserId }).GetAwaiter().GetResult();

            Assert.Equal(login.UserId, native.LastFaceCaptureUserId);
            Assert.Equal(100, native.LastFaceCaptureMaxAttempts);
            Assert.Equal(100, native.LastFaceCaptureWaitIntervalMs);
            Assert.True(capture.FaceDetected);
            Assert.Equal(image.Length, capture.ImageBytes.Length);
            Assert.True(SequenceEqual(image, capture.ImageBytes), "采集图片字节应与设备返回一致。");
            Assert.Equal(95, capture.QualityScore);
            Assert.Equal("image/jpeg", capture.ContentType);
        }

        [TestCase]
        public static void SdkWrapper_CaptureFacePollingTimeout_ThrowsFaceCaptureTimeout()
        {
            var native = new FakeNativeClient
            {
                FaceCaptureStatus = 1001,
                FaceCaptureImage = null
            };
            var gateway = new HikvisionSdkWrapper(native);
            var login = gateway.LoginAsync(new LoginRequest { IpAddress = "127.0.0.1", UserName = "admin", Password = "12345" }).GetAwaiter().GetResult();

            var ex = Expect<DeviceGatewayException>(() => gateway.CaptureFaceAsync(new CaptureRequest { UserId = login.UserId }).GetAwaiter().GetResult());

            Assert.Equal("FACE_CAPTURE_TIMEOUT", ex.Error.Source);
        }

        [TestCase]
        public static void SdkWrapper_CaptureFaceFailed_ThrowsLastError()
        {
            var native = new FakeNativeClient
            {
                FaceCaptureStatus = 1003,
                FaceCaptureImage = null,
                LastError = 7
            };
            var gateway = new HikvisionSdkWrapper(native);
            var login = gateway.LoginAsync(new LoginRequest { IpAddress = "127.0.0.1", UserName = "admin", Password = "12345" }).GetAwaiter().GetResult();

            var ex = Expect<DeviceGatewayException>(() => gateway.CaptureFaceAsync(new CaptureRequest { UserId = login.UserId }).GetAwaiter().GetResult());

            Assert.Equal(7, ex.Error.Code);
        }

        [TestCase]
        public static void GatewayJson_SerializesDtoForIsapiBodies()
        {
            var json = Serialize(new PersonInfo { EmployeeId = "10001", Name = "张三" });

            Assert.Contains("10001", json);
            Assert.Contains("张三", json);
        }

        private static LoginResponse Login(MockHikvisionGateway gateway)
        {
            return gateway.LoginAsync(new LoginRequest
            {
                IpAddress = "127.0.0.1",
                UserName = "admin",
                Password = "12345"
            }).GetAwaiter().GetResult();
        }

        private static PersonInfo Person(string employeeId)
        {
            return new PersonInfo
            {
                EmployeeId = employeeId,
                Name = "张三",
                CardNumber = "C" + employeeId,
                Department = "测试部"
            };
        }

        private static FaceInfo Face(string employeeId)
        {
            return new FaceInfo
            {
                EmployeeId = employeeId,
                CardNumber = "C" + employeeId,
                FaceId = "F" + employeeId,
                ImageBytes = JpegBytes(),
                QualityScore = 90
            };
        }

        private static SetPermissionRequest PermissionRequest(int userId, string employeeId)
        {
            var request = new SetPermissionRequest { UserId = userId };
            var permission = new PermissionInfo
            {
                EmployeeId = employeeId,
                PermissionCode = "P1"
            };
            permission.DoorIndexes.Add(1);
            request.Permissions.Add(permission);
            return request;
        }

        private static byte[] JpegBytes()
        {
            return new byte[] { 0xFF, 0xD8, 0x10, 0x20, 0xFF, 0xD9 };
        }

        private static bool SequenceEqual(byte[] expected, byte[] actual)
        {
            if (expected == null && actual == null)
            {
                return true;
            }

            if (expected == null || actual == null || expected.Length != actual.Length)
            {
                return false;
            }

            for (var index = 0; index < expected.Length; index++)
            {
                if (expected[index] != actual[index])
                {
                    return false;
                }
            }

            return true;
        }

        private static T Expect<T>(Action action)
            where T : Exception
        {
            try
            {
                action();
            }
            catch (T ex)
            {
                return ex;
            }

            throw new InvalidOperationException("Expected exception: " + typeof(T).Name);
        }

        private static DeviceCapabilities ParseCapabilities(string raw)
        {
            var type = typeof(HikvisionSdkWrapper).Assembly.GetType("ControlDoor.Hikvision.HikvisionXmlParser");
            var method = type.GetMethod("ParseCapabilities", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return (DeviceCapabilities)method.Invoke(null, new object[] { raw });
        }

        private static DeviceInfo ParseDeviceInfo(string raw)
        {
            var type = typeof(HikvisionSdkWrapper).Assembly.GetType("ControlDoor.Hikvision.HikvisionXmlParser");
            var method = type.GetMethod("ParseDeviceInfo", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return (DeviceInfo)method.Invoke(null, new object[] { raw });
        }

        private static string Serialize(object value)
        {
            var type = typeof(HikvisionSdkWrapper).Assembly.GetType("ControlDoor.Hikvision.HikvisionGatewayJson");
            var method = type.GetMethod("Serialize", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return (string)method.Invoke(null, new[] { value });
        }

        private sealed class RecordingHttpHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler;

            public RecordingHttpHandler(HttpResponseMessage response)
                : this((request, token) => Task.FromResult(response))
            {
            }

            public RecordingHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            {
                this.handler = handler;
            }

            public IList<HttpRequestMessage> Requests { get; } = new List<HttpRequestMessage>();

            public IList<string> RequestBodies { get; } = new List<string>();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request);
                RequestBodies.Add(request.Content == null ? string.Empty : request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                return handler(request, cancellationToken);
            }
        }

        private sealed class FakeNativeClient : IHikvisionSdkNativeClient
        {
            private HikvisionAlarmNativeCallback callback;

            public bool InitResult { get; set; } = true;

            public bool LogoutResult { get; set; } = true;

            public bool CloseAlarmResult { get; set; } = true;

            public bool ControlGatewayResult { get; set; } = true;

            public bool CaptureResult { get; set; } = true;

            public bool StdXmlResult { get; set; } = true;

            public int LoginUserId { get; set; } = 10;

            public int LastError { get; set; }

            public string StdXmlOutput { get; set; } = "{}";

            public int FaceUploadStatus { get; set; } = 1000;

            public string FaceUploadResponse { get; set; } = @"{""statusCode"":1}";

            public int FaceCaptureStatus { get; set; } = 1000;

            public byte[] FaceCaptureImage { get; set; } = new byte[] { 0xFF, 0xD8, 0x01, 0x02, 0xFF, 0xD9 };

            public byte FaceCaptureQuality { get; set; } = 95;

            public int FaceCaptureErrorCode { get; set; }

            public int LastFaceCaptureUserId { get; private set; }

            public int LastFaceCaptureMaxAttempts { get; private set; }

            public int LastFaceCaptureWaitIntervalMs { get; private set; }

            public bool InitCalled { get; private set; }

            public bool CallbackRegistered { get; private set; }

            public int LoginCallCount { get; private set; }

            public int LogoutCallCount { get; private set; }

            public int CleanupCallCount { get; private set; }

            public GateControlCommand LastGateCommand { get; private set; }

            public string LastCapturePath { get; private set; }

            public string LastStdXmlUrl { get; private set; }

            public string LastFaceUploadUrl { get; private set; }

            public string LastFaceUploadJson { get; private set; }

            public byte[] LastFaceUploadPictureBytes { get; private set; }

            public bool AcsWorkStatusResult { get; set; } = true;

            public byte[] SetupAlarmStatus { get; set; } = new byte[] { 1 };

            public int LastAcsWorkStatusUserId { get; private set; }

            public int LastAcsWorkStatusChannel { get; private set; }

            public bool Init()
            {
                InitCalled = true;
                return InitResult;
            }

            public bool Cleanup()
            {
                CleanupCallCount++;
                return true;
            }

            public int Login(LoginRequest request, out DeviceInfo deviceInfo)
            {
                LoginCallCount++;
                deviceInfo = new DeviceInfo
                {
                    Model = "Fake",
                    SerialNumber = "FAKE1",
                    IpAddress = request.IpAddress
                };
                return LoginUserId;
            }

            public bool Logout(int userId)
            {
                LogoutCallCount++;
                return LogoutResult;
            }

            public bool SetMessageCallback(HikvisionAlarmNativeCallback callback)
            {
                this.callback = callback;
                CallbackRegistered = true;
                return true;
            }

            public int LastAlarmDeployType { get; private set; }

            public int SetupAlarm(int userId, int level, int alarmInfoType, int deployType)
            {
                LastAlarmDeployType = deployType;
                return 77;
            }

            public bool CloseAlarm(int alarmHandle)
            {
                return CloseAlarmResult;
            }

            public bool GetAcsWorkStatus(int userId, int channel, out AcsWorkStatus status)
            {
                LastAcsWorkStatusUserId = userId;
                LastAcsWorkStatusChannel = channel;
                status = new AcsWorkStatus
                {
                    SetupAlarmStatus = SetupAlarmStatus == null ? null : (byte[])SetupAlarmStatus.Clone()
                };
                return AcsWorkStatusResult;
            }

            public bool ControlGateway(int userId, int gateIndex, GateControlCommand command)
            {
                LastGateCommand = command;
                return ControlGatewayResult;
            }

            public bool CaptureJpegPicture(int userId, int channel, int pictureQuality, string filePath)
            {
                LastCapturePath = filePath;
                File.WriteAllBytes(filePath, JpegBytes());
                return CaptureResult;
            }

            public bool StandardXmlConfig(int userId, string requestUrl, string inputXml, out string outputXml)
            {
                LastStdXmlUrl = requestUrl;
                outputXml = StdXmlOutput;
                return StdXmlResult;
            }

            public int UploadFaceData(int userId, string requestUrl, string jsonPayload, byte[] pictureBytes, out string responseBody)
            {
                LastFaceUploadUrl = requestUrl;
                LastFaceUploadJson = jsonPayload;
                LastFaceUploadPictureBytes = pictureBytes == null ? null : (byte[])pictureBytes.Clone();
                responseBody = FaceUploadResponse;
                return FaceUploadStatus;
            }

            public int CaptureFace(int userId, int maxAttempts, int waitIntervalMs, out byte[] faceImage, out byte faceQuality, out int errorCode)
            {
                LastFaceCaptureUserId = userId;
                LastFaceCaptureMaxAttempts = maxAttempts;
                LastFaceCaptureWaitIntervalMs = waitIntervalMs;
                faceImage = FaceCaptureStatus == 1000 && FaceCaptureImage != null
                    ? (byte[])FaceCaptureImage.Clone()
                    : null;
                faceQuality = faceImage == null ? (byte)0 : FaceCaptureQuality;
                errorCode = FaceCaptureErrorCode;
                return FaceCaptureStatus;
            }

            public int GetLastError()
            {
                return LastError;
            }

            public string GetErrorMessage(int errorCode)
            {
                return SdkError.GetDefaultMessage(errorCode);
            }

            public void EmitAlarm(int command, byte[] payload)
            {
                var handle = System.Runtime.InteropServices.GCHandle.Alloc(payload, System.Runtime.InteropServices.GCHandleType.Pinned);
                try
                {
                    callback(command, IntPtr.Zero, handle.AddrOfPinnedObject(), payload.Length, IntPtr.Zero);
                }
                finally
                {
                    handle.Free();
                }
            }
        }
    }
}
