using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Hikvision;

namespace ControlEntradaSalida.Tests
{
    public static class Stage3IsapiAndSdkWrapperAdvancedTests
    {
        [TestCase]
        public static void IsapiClient_Put_SendsBody()
        {
            var handler = new Stage3RecordingHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
            var client = new HikvisionIsapiClient(() => handler);

            client.SendAsync(new IsapiRequest
            {
                BaseAddress = "http://device",
                Path = "/ISAPI/AccessControl/UserRight/SetUp",
                Method = IsapiMethod.Put,
                Body = "{\"ok\":true}"
            }).GetAwaiter().GetResult();

            Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
            Assert.Equal("{\"ok\":true}", handler.RequestBodies[0]);
        }

        [TestCase]
        public static void IsapiClient_Get_DoesNotSendBody()
        {
            var handler = new Stage3RecordingHttpHandler(new HttpResponseMessage(HttpStatusCode.OK));
            var client = new HikvisionIsapiClient(() => handler);

            client.SendAsync(new IsapiRequest
            {
                BaseAddress = "http://device",
                Path = "/ISAPI/System/deviceInfo",
                Method = IsapiMethod.Get,
                Body = "ignored"
            }).GetAwaiter().GetResult();

            Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
            Assert.Equal(string.Empty, handler.RequestBodies[0]);
        }

        [TestCase]
        public static void IsapiClient_CustomHeaders_AreForwarded()
        {
            var handler = new Stage3RecordingHttpHandler(new HttpResponseMessage(HttpStatusCode.OK));
            var client = new HikvisionIsapiClient(() => handler);
            var request = new IsapiRequest
            {
                BaseAddress = "http://device",
                Path = "/ISAPI/System/status"
            };
            request.Headers["X-Request-Id"] = "req-1";

            client.SendAsync(request).GetAwaiter().GetResult();

            Assert.True(handler.Requests[0].Headers.Contains("X-Request-Id"));
        }

        [TestCase]
        public static void IsapiClient_NormalizesBaseAddressWithoutScheme()
        {
            var handler = new Stage3RecordingHttpHandler(new HttpResponseMessage(HttpStatusCode.OK));
            var client = new HikvisionIsapiClient(() => handler);

            client.SendAsync(new IsapiRequest
            {
                BaseAddress = "192.168.1.64",
                Path = "ISAPI/System/status"
            }).GetAwaiter().GetResult();

            Assert.Equal("http://192.168.1.64/ISAPI/System/status", handler.Requests[0].RequestUri.ToString());
        }

        [TestCase]
        public static void IsapiClient_401WithoutCredentials_Returns401()
        {
            var handler = new Stage3RecordingHttpHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("auth required")
            });
            var client = new HikvisionIsapiClient(() => handler);

            var response = client.SendAsync(new IsapiRequest
            {
                BaseAddress = "http://device",
                Path = "/ISAPI/System/status"
            }).GetAwaiter().GetResult();

            Assert.Equal(401, response.StatusCode);
            Assert.Equal("auth required", response.Body);
            Assert.Equal(1, handler.Requests.Count);
        }

        [TestCase]
        public static void IsapiClient_HandlerHttpFailure_MapsToDeviceGatewayException()
        {
            var handler = new Stage3RecordingHttpHandler((request, token) => throw new HttpRequestException("network down"));
            var client = new HikvisionIsapiClient(() => handler);

            var ex = Stage3TestReflection.Expect<DeviceGatewayException>(() => client.SendAsync(new IsapiRequest
            {
                BaseAddress = "http://device",
                Path = "/ISAPI/System/status"
            }).GetAwaiter().GetResult());

            Assert.Equal(7, ex.Error.Code);
            Assert.Contains("network down", ex.Error.Message);
        }

        [TestCase]
        public static void IsapiClient_ReusesHttpClientAcrossRequests()
        {
            int handlerCreateCount = 0;
            var handler = new Stage3RecordingHttpHandler(new HttpResponseMessage(HttpStatusCode.OK));
            var client = new HikvisionIsapiClient(() =>
            {
                handlerCreateCount++;
                return handler;
            });

            for (int i = 0; i < 3; i++)
            {
                client.SendAsync(new IsapiRequest
                {
                    BaseAddress = "http://device",
                    Path = "/ISAPI/System/status"
                }).GetAwaiter().GetResult();
            }

            Assert.Equal(3, handler.Requests.Count);
            Assert.Equal(1, handlerCreateCount);
        }

        [TestCase]
        public static void IsapiClient_Dispose_PreventsFurtherUse()
        {
            var handler = new Stage3RecordingHttpHandler(new HttpResponseMessage(HttpStatusCode.OK));
            var client = new HikvisionIsapiClient(() => handler);
            client.Dispose();

            var ex = Stage3TestReflection.Expect<ObjectDisposedException>(() => client.SendAsync(new IsapiRequest
            {
                BaseAddress = "http://device",
                Path = "/ISAPI/System/status"
            }).GetAwaiter().GetResult());

            Assert.Equal(typeof(HikvisionIsapiClient).Name, ex.ObjectName);
            Assert.Equal(0, handler.Requests.Count);
        }

        [TestCase]
        public static void IsapiClient_DigestRetry_CachesDigestClientPerDeviceAndAccount()
        {
            // 验证 Digest client 按 (设备, 账号) 缓存：
            // - 匿名 client 全局单例（首次请求时创建一次）
            // - 同一 (设备, 账号) 的 Digest client 只创建一次
            // - 换设备时才创建新的 Digest client
            // mock 对每次 SendAsync 的首次请求（匿名路径）返回 401，触发 Digest 重试；重试返回 200。
            int factoryCallCount = 0;
            int requestSeq = 0;
            var handler = new Stage3RecordingHttpHandler((request, token) =>
            {
                // 每次完整的 SendAsync 会触发 2 次 handler 调用（匿名 401 + 重试 200）。
                // 用自增计数器区分：奇数=匿名首次（401），偶数=重试（200）。
                requestSeq++;
                var status = requestSeq % 2 == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK;
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent("{}")
                });
            });
            var client = new HikvisionIsapiClient(() =>
            {
                factoryCallCount++;
                return handler;
            });

            // 首次请求（device-a）：匿名 client 新建（factory=1）→401→Digest client 新建（factory=2）
            client.SendAsync(new IsapiRequest
            {
                BaseAddress = "http://device-a",
                Path = "/ISAPI/System/status",
                UserName = "admin",
                Password = "secret"
            }).GetAwaiter().GetResult();
            Assert.Equal(2, factoryCallCount);

            // 第二次请求（同一 device-a/账号）：匿名单例命中缓存 + Digest client 命中缓存，factory 不变
            client.SendAsync(new IsapiRequest
            {
                BaseAddress = "http://device-a",
                Path = "/ISAPI/System/status",
                UserName = "admin",
                Password = "secret"
            }).GetAwaiter().GetResult();
            Assert.Equal(2, factoryCallCount);

            // 第三次请求（device-b）：匿名单例命中缓存 + Digest client 新建（factory=3）
            client.SendAsync(new IsapiRequest
            {
                BaseAddress = "http://device-b",
                Path = "/ISAPI/System/status",
                UserName = "admin",
                Password = "secret"
            }).GetAwaiter().GetResult();
            Assert.Equal(3, factoryCallCount);
        }

        [TestCase]
        public static void SdkWrapper_InitFailure_ThrowsUnifiedError()
        {
            var native = new Stage3FakeNativeClient { InitResult = false, LastError = 3 };
            var gateway = new HikvisionSdkWrapper(native);

            var ex = Stage3TestReflection.Expect<DeviceGatewayException>(() => gateway.LoginAsync(new LoginRequest
            {
                IpAddress = "127.0.0.1",
                UserName = "admin",
                Password = "12345"
            }).GetAwaiter().GetResult());

            Assert.Equal(3, ex.Error.Code);
        }

        [TestCase]
        public static void SdkWrapper_InitCallbackFailure_ThrowsBeforeLoginCompletes()
        {
            var native = new Stage3FakeNativeClient { SetCallbackResult = false, LastError = 23 };
            var gateway = new HikvisionSdkWrapper(native);

            var ex = Stage3TestReflection.Expect<DeviceGatewayException>(() => Login(gateway));

            Assert.Equal(23, ex.Error.Code);
            Assert.Equal(1, native.CallbackRegisterCount);
            Assert.Equal(0, native.SetupAlarmCallCount);
        }

        [TestCase]
        public static void SdkWrapper_SetAlarmSetupFailure_ThrowsLastError()
        {
            var native = new Stage3FakeNativeClient { AlarmHandle = -1, LastError = 52 };
            var gateway = new HikvisionSdkWrapper(native);
            var login = Login(gateway);

            var ex = Stage3TestReflection.Expect<DeviceGatewayException>(() => gateway.SetAlarmAsync(new AlarmSetupRequest { UserId = login.UserId }).GetAwaiter().GetResult());

            Assert.Equal(52, ex.Error.Code);
        }

        [TestCase]
        public static void SdkWrapper_ControlGatewayFailure_ThrowsLastError()
        {
            var native = new Stage3FakeNativeClient { ControlGatewayResult = false, LastError = 4 };
            var gateway = new HikvisionSdkWrapper(native);
            var login = Login(gateway);

            var ex = Stage3TestReflection.Expect<DeviceGatewayException>(() => gateway.ControlGatewayAsync(new GateControlRequest
            {
                UserId = login.UserId,
                GateIndex = 1,
                Command = GateControlCommand.Open
            }).GetAwaiter().GetResult());

            Assert.Equal(4, ex.Error.Code);
        }

        [TestCase]
        public static void SdkWrapper_CaptureFailure_ThrowsLastError()
        {
            var native = new Stage3FakeNativeClient { CaptureResult = false, LastError = 52 };
            var gateway = new HikvisionSdkWrapper(native);
            var login = Login(gateway);

            var ex = Stage3TestReflection.Expect<DeviceGatewayException>(() => gateway.CapturePictureAsync(new CaptureRequest { UserId = login.UserId }).GetAwaiter().GetResult());

            Assert.Equal(52, ex.Error.Code);
        }

        [TestCase]
        public static void SdkWrapper_StdXmlFailure_ThrowsLastError()
        {
            var native = new Stage3FakeNativeClient { StdXmlResult = false, LastError = 23 };
            var gateway = new HikvisionSdkWrapper(native);
            var login = Login(gateway);

            var ex = Stage3TestReflection.Expect<DeviceGatewayException>(() => gateway.SendIsapiRequestAsync(new IsapiRequest
            {
                UserId = login.UserId,
                Path = "/ISAPI/System/capabilities"
            }).GetAwaiter().GetResult());

            Assert.Equal(23, ex.Error.Code);
        }

        [TestCase]
        public static void SdkWrapper_RemotePersonMethods_UseStdXmlPaths()
        {
            var native = new Stage3FakeNativeClient { StdXmlOutput = "{}" };
            var gateway = new HikvisionSdkWrapper(native);
            var login = Login(gateway);

            gateway.AddPersonAsync(new AddPersonRequest { UserId = login.UserId, Person = Person("10001") }).GetAwaiter().GetResult();

            Assert.Contains("UserInfo/Record", native.LastStdXmlUrl);
            Assert.Contains("POST", native.LastStdXmlUrl);
            Assert.Contains("10001", native.LastStdXmlInput);
        }

        [TestCase]
        public static void SdkWrapper_UpsertPerson_UsesCompatibleUserInfoSetupPayload()
        {
            var native = new Stage3FakeNativeClient { StdXmlOutput = @"{""statusCode"":1}" };
            var gateway = new HikvisionSdkWrapper(native);
            var login = Login(gateway);

            gateway.UpsertPersonAsync(new UpsertPersonRequest { UserId = login.UserId, Person = Person("10001") }).GetAwaiter().GetResult();

            Assert.Contains("PUT", native.LastStdXmlUrl);
            Assert.Contains("/ISAPI/AccessControl/UserInfo/SetUp?format=json", native.LastStdXmlUrl);
            Assert.Contains(@"""UserInfo""", native.LastStdXmlInput);
            Assert.Contains(@"""employeeNo"":""10001""", native.LastStdXmlInput);
            Assert.Contains(@"""doorRight"":""1""", native.LastStdXmlInput);
            Assert.Contains(@"""RightPlan""", native.LastStdXmlInput);
            Assert.Contains(@"""userVerifyMode"":""face""", native.LastStdXmlInput);
        }

        [TestCase]
        public static void SdkWrapper_UpsertPermission_UsesPermissionOnlyUserInfoPayload()
        {
            var native = new Stage3FakeNativeClient { StdXmlOutput = @"{""statusCode"":1}" };
            var gateway = new HikvisionSdkWrapper(native);
            var login = Login(gateway);

            gateway.UpsertPersonAsync(new UpsertPersonRequest
            {
                UserId = login.UserId,
                Person = Person("10001"),
                ProvisioningMode = PersonProvisioningMode.Permission
            }).GetAwaiter().GetResult();

            Assert.Contains("PUT", native.LastStdXmlUrl);
            Assert.Contains("/ISAPI/AccessControl/UserInfo/SetUp?format=json", native.LastStdXmlUrl);
            Assert.Contains(@"""UserInfo""", native.LastStdXmlInput);
            Assert.Contains(@"""employeeNo"":""10001""", native.LastStdXmlInput);
            Assert.Contains(@"""doorRight"":""1""", native.LastStdXmlInput);
            Assert.Contains(@"""RightPlan""", native.LastStdXmlInput);
            Assert.False(native.LastStdXmlInput.Contains(@"""userVerifyMode"""));
            Assert.False(native.LastStdXmlInput.Contains(@"""gender"""));
        }

        [TestCase]
        public static void SdkWrapper_DeleteFace_UsesStdXmlDeletePath()
        {
            var native = new Stage3FakeNativeClient { StdXmlOutput = "{}" };
            var gateway = new HikvisionSdkWrapper(native);
            var login = Login(gateway);

            gateway.DeleteFaceAsync(new DeleteFaceRequest { UserId = login.UserId, EmployeeId = "10001" }).GetAwaiter().GetResult();

            Assert.Contains("/ISAPI/Intelligent/FDLib/FDSearch/Delete?format=json&FDID=1&faceLibType=blackFD", native.LastStdXmlUrl);
            Assert.Contains("PUT", native.LastStdXmlUrl);
            Assert.Contains(@"""FPID"":[{""value"":""10001""}]", native.LastStdXmlInput);
        }

        [TestCase]
        public static void SdkWrapper_DeletePerson_UsesCompatibleUserInfoDeletePayload()
        {
            var native = new Stage3FakeNativeClient { StdXmlOutput = @"{""statusCode"":1}" };
            var gateway = new HikvisionSdkWrapper(native);
            var login = Login(gateway);

            gateway.DeletePersonAsync(new DeletePersonRequest { UserId = login.UserId, EmployeeId = "10001" }).GetAwaiter().GetResult();

            Assert.Contains("PUT", native.LastStdXmlUrl);
            Assert.Contains("/ISAPI/AccessControl/UserInfo/Delete?format=json", native.LastStdXmlUrl);
            Assert.Contains(@"""UserInfoDelCond"":{""EmployeeNoList"":[{""employeeNo"":""10001""}]}", native.LastStdXmlInput);
            Assert.False(native.LastStdXmlUrl.Contains("UserManagement"));
            Assert.False(native.LastStdXmlInput.Contains(@"""UserId"""));
            Assert.False(native.LastStdXmlInput.Contains(@"""EmployeeId"""));
        }

        [TestCase]
        public static void SdkWrapper_QueryFace_UsesFdSearchAndParsesFaceRecords()
        {
            var native = new Stage3FakeNativeClient
            {
                StdXmlOutput = @"{""statusCode"":1,""numOfMatches"":1,""MatchList"":[{""FPID"":""10001"",""facePicBinary"":""/9j/2Q==""}]}"
            };
            var gateway = new HikvisionSdkWrapper(native);
            var login = Login(gateway);

            var response = gateway.QueryFaceAsync(new QueryFaceRequest { UserId = login.UserId, EmployeeId = "10001" }).GetAwaiter().GetResult();

            Assert.Contains("POST", native.LastStdXmlUrl);
            Assert.Contains("/ISAPI/Intelligent/FDLib/FDSearch?format=json", native.LastStdXmlUrl);
            Assert.Contains(@"""faceLibType"":""blackFD""", native.LastStdXmlInput);
            Assert.Contains(@"""FDID"":""1""", native.LastStdXmlInput);
            Assert.Contains(@"""FPID"":""10001""", native.LastStdXmlInput);
            Assert.True(response.Exists);
            Assert.Equal(1, response.TotalCount);
            Assert.Equal(1, response.Faces.Count);
            Assert.Equal("/9j/2Q==", response.Faces[0].ImageBase64);
        }

        [TestCase]
        public static void SdkWrapper_QueryFace_EmptyAcceptedResponseDoesNotReportExistingFace()
        {
            var native = new Stage3FakeNativeClient
            {
                StdXmlOutput = @"{""statusCode"":1,""numOfMatches"":0,""MatchList"":[]}"
            };
            var gateway = new HikvisionSdkWrapper(native);
            var login = Login(gateway);

            var response = gateway.QueryFaceAsync(new QueryFaceRequest { UserId = login.UserId, EmployeeId = "10001" }).GetAwaiter().GetResult();

            Assert.False(response.Exists);
            Assert.Equal(0, response.TotalCount);
            Assert.Equal(0, response.Faces.Count);
        }

        [TestCase]
        public static void SdkWrapper_QueryFace_ParsesMultipartFaceDataRecordAndAlternateImageFields()
        {
            var native = new Stage3FakeNativeClient
            {
                StdXmlOutput = "--boundary\r\nContent-Type: application/json\r\n\r\n" +
                    @"{""statusString"":""OK"",""subStatusCode"":""ok"",""totalMatches"":1,""FaceDataRecord"":[{""employeeNo"":""10002"",""faceID"":""face-1"",""FacePic"":""BASE64PIC==""}]}" +
                    "\r\n--boundary--"
            };
            var gateway = new HikvisionSdkWrapper(native);
            var login = Login(gateway);

            var response = gateway.QueryFaceAsync(new QueryFaceRequest { UserId = login.UserId, EmployeeId = "10002" }).GetAwaiter().GetResult();

            Assert.True(response.Exists);
            Assert.Equal(1, response.TotalCount);
            Assert.Equal(1, response.Faces.Count);
            Assert.Equal("10002", response.Faces[0].EmployeeId);
            Assert.Equal("face-1", response.Faces[0].FaceId);
            Assert.Equal("BASE64PIC==", response.Faces[0].ImageBase64);
        }

        [TestCase]
        public static void SdkWrapper_QueryFace_ParsesMultipartMatchListBeforeImagePart()
        {
            var native = new Stage3FakeNativeClient
            {
                StdXmlOutput = "Content-Type:multipart/form-data;boundary=MIME_boundary\r\n" +
                    "--MIME_boundary\r\n" +
                    "Content-Type: application/json\r\n" +
                    "Content-Length:180\r\n\r\n" +
                    @"{""statusCode"":1,""statusString"":""OK"",""subStatusCode"":""ok"",""numOfMatches"":1,""totalMatches"":1,""MatchList"":[{""FPID"":""10001"",""modelData"":""MODEL==""}]}" +
                    "\r\n--MIME_boundary\r\n" +
                    "Content-Type:image/jpeg\r\n" +
                    "Content-Length:16\r\n\r\n" +
                    "\xff\xd8binary}jpeg\r\n" +
                    "--MIME_boundary--\r\n"
            };
            var gateway = new HikvisionSdkWrapper(native);
            var login = Login(gateway);

            var response = gateway.QueryFaceAsync(new QueryFaceRequest { UserId = login.UserId, EmployeeId = "10001" }).GetAwaiter().GetResult();

            Assert.True(response.Exists);
            Assert.Equal(1, response.TotalCount);
            Assert.Equal(1, response.Faces.Count);
            Assert.Equal("10001", response.Faces[0].EmployeeId);
            Assert.Equal("MODEL==", response.Faces[0].ImageBase64);
        }

        [TestCase]
        public static void SdkWrapper_UploadFace_UsesCompatibleFaceRemoteConfigPayload()
        {
            var native = new Stage3FakeNativeClient
            {
                FaceUploadStatus = 1000,
                FaceUploadResponse = @"{""statusCode"":1}"
            };
            var gateway = new HikvisionSdkWrapper(native);
            var login = Login(gateway);
            var face = Face("10001");

            gateway.UploadFaceAsync(new UploadFaceRequest { UserId = login.UserId, Face = face }).GetAwaiter().GetResult();

            Assert.Equal(login.UserId, native.LastFaceUploadUserId);
            Assert.Equal("PUT /ISAPI/Intelligent/FDLib/FDSetUp?format=json", native.LastFaceUploadUrl);
            Assert.Contains(@"""faceLibType"":""blackFD""", native.LastFaceUploadJson);
            Assert.Contains(@"""FDID"":""1""", native.LastFaceUploadJson);
            Assert.Contains(@"""FPID"":""10001""", native.LastFaceUploadJson);
            Assert.Equal(Stage3TestReflection.JpegBytes().Length, native.LastFaceUploadPictureBytes.Length);
            Assert.Equal(0xFF, native.LastFaceUploadPictureBytes[0]);
            Assert.Equal(0xD9, native.LastFaceUploadPictureBytes[native.LastFaceUploadPictureBytes.Length - 1]);
            Assert.Equal(0, native.StdXmlCallCount);
        }

        [TestCase]
        public static void SdkWrapper_UploadFace_StartRemoteConfigFailure_ThrowsLastError()
        {
            var native = new Stage3FakeNativeClient
            {
                FaceUploadStatus = -1,
                LastError = 23
            };
            var gateway = new HikvisionSdkWrapper(native);
            var login = Login(gateway);

            var ex = Stage3TestReflection.Expect<DeviceGatewayException>(() => gateway.UploadFaceAsync(new UploadFaceRequest
            {
                UserId = login.UserId,
                Face = Face("10001")
            }).GetAwaiter().GetResult());

            Assert.Equal(23, ex.Error.Code);
        }

        [TestCase]
        public static void SdkWrapper_UploadFace_RemoteConfigFailure_ThrowsResponseMessage()
        {
            var native = new Stage3FakeNativeClient
            {
                FaceUploadStatus = 1003,
                FaceUploadResponse = @"{""statusCode"":2,""statusString"":""failed"",""subStatusCode"":""badFace"",""errorMsg"":""invalid picture""}"
            };
            var gateway = new HikvisionSdkWrapper(native);
            var login = Login(gateway);

            var ex = Stage3TestReflection.Expect<DeviceGatewayException>(() => gateway.UploadFaceAsync(new UploadFaceRequest
            {
                UserId = login.UserId,
                Face = Face("10001")
            }).GetAwaiter().GetResult());

            Assert.Equal(1003, ex.Error.Code);
            Assert.Contains("badFace", ex.Error.Message);
            Assert.Contains("invalid picture", ex.Error.Message);
        }

        [TestCase]
        public static void SdkWrapper_QueryEventRecord_UsesAcsEventPath()
        {
            var native = new Stage3FakeNativeClient { StdXmlOutput = "{\"records\":[]}" };
            var gateway = new HikvisionSdkWrapper(native);
            var login = Login(gateway);

            var result = gateway.QueryEventRecordAsync(new EventQueryRequest
            {
                UserId = login.UserId,
                BeginTime = DateTime.Now.AddMinutes(-10),
                EndTime = DateTime.Now
            }).GetAwaiter().GetResult();

            Assert.Contains("AcsEvent", native.LastStdXmlUrl);
            Assert.Equal(1, result.TotalCount);
        }

        [TestCase]
        public static void SdkWrapper_GetLastErrorMessage_UsesNativeMessageFallback()
        {
            var native = new Stage3FakeNativeClient { LastError = 777, ErrorMessage = "native-message" };
            var gateway = new HikvisionSdkWrapper(native);

            Assert.Equal(777, gateway.GetLastErrorCode());
            Assert.Equal("native-message", gateway.GetErrorMessage(777));
        }

        private static LoginResponse Login(HikvisionSdkWrapper gateway)
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
                Name = "Test",
                CardNumber = "C" + employeeId
            };
        }

        private static FaceInfo Face(string employeeId)
        {
            return new FaceInfo
            {
                EmployeeId = employeeId,
                CardNumber = "C" + employeeId,
                ImageBytes = Stage3TestReflection.JpegBytes()
            };
        }
    }

    internal sealed class Stage3RecordingHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler;

        public Stage3RecordingHttpHandler(HttpResponseMessage response)
            : this((request, token) => Task.FromResult(response))
        {
        }

        public Stage3RecordingHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
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

    internal sealed class Stage3FakeNativeClient : IHikvisionSdkNativeClient
    {
        private HikvisionAlarmNativeCallback callback;

        public bool InitResult { get; set; } = true;

        public bool LogoutResult { get; set; } = true;

        public bool SetCallbackResult { get; set; } = true;

        public bool CloseAlarmResult { get; set; } = true;

        public bool ControlGatewayResult { get; set; } = true;

        public bool CaptureResult { get; set; } = true;

        public bool StdXmlResult { get; set; } = true;

        public int LoginUserId { get; set; } = 10;

        public int AlarmHandle { get; set; } = 77;

        public int LastError { get; set; }

        public string ErrorMessage { get; set; }

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

        public int SetupAlarmCallCount { get; private set; }

        public int CallbackRegisterCount { get; private set; }

        public HikvisionAlarmNativeCallback RegisteredCallback
        {
            get { return callback; }
        }

        public int LastAlarmDeployType { get; private set; }

        public int CleanupCallCount { get; private set; }

        public int StdXmlCallCount { get; private set; }

        public string LastStdXmlUrl { get; private set; }

        public string LastStdXmlInput { get; private set; }

        public string LastCapturePath { get; private set; }

        public int LastFaceUploadUserId { get; private set; }

        public string LastFaceUploadUrl { get; private set; }

        public string LastFaceUploadJson { get; private set; }

        public byte[] LastFaceUploadPictureBytes { get; private set; }

        public bool Init()
        {
            return InitResult;
        }

        public bool Cleanup()
        {
            CleanupCallCount++;
            return true;
        }

        public int Login(LoginRequest request, out DeviceInfo deviceInfo)
        {
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
            return LogoutResult;
        }

        public bool SetMessageCallback(HikvisionAlarmNativeCallback callback)
        {
            CallbackRegisterCount++;
            if (!SetCallbackResult)
            {
                return false;
            }

            this.callback = callback;
            return true;
        }

        public int SetupAlarm(int userId, int level, int alarmInfoType, int deployType)
        {
            SetupAlarmCallCount++;
            LastAlarmDeployType = deployType;
            return AlarmHandle;
        }

        public bool CloseAlarm(int alarmHandle)
        {
            return CloseAlarmResult;
        }

        public bool GetAcsWorkStatus(int userId, int channel, out AcsWorkStatus status)
        {
            status = new AcsWorkStatus
            {
                SetupAlarmStatus = new byte[] { 1 }
            };
            return true;
        }

        public bool ControlGateway(int userId, int gateIndex, GateControlCommand command)
        {
            return ControlGatewayResult;
        }

        public bool CaptureJpegPicture(int userId, int channel, int pictureQuality, string filePath)
        {
            LastCapturePath = filePath;
            File.WriteAllBytes(filePath, Stage3TestReflection.JpegBytes());
            return CaptureResult;
        }

        public bool StandardXmlConfig(int userId, string requestUrl, string inputXml, out string outputXml)
        {
            StdXmlCallCount++;
            LastStdXmlUrl = requestUrl;
            LastStdXmlInput = inputXml;
            outputXml = StdXmlOutput;
            return StdXmlResult;
        }

        public int UploadFaceData(int userId, string requestUrl, string jsonPayload, byte[] pictureBytes, out string responseBody)
        {
            LastFaceUploadUserId = userId;
            LastFaceUploadUrl = requestUrl;
            LastFaceUploadJson = jsonPayload;
            LastFaceUploadPictureBytes = pictureBytes == null ? null : (byte[])pictureBytes.Clone();
            responseBody = FaceUploadResponse;
            return FaceUploadStatus;
        }

        public int CaptureFace(int userId, int maxAttempts, int waitIntervalMs, CancellationToken cancellationToken, out byte[] faceImage, out byte faceQuality, out int errorCode)
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
            return ErrorMessage ?? SdkError.GetDefaultMessage(errorCode);
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
