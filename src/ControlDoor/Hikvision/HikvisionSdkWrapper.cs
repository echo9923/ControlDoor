using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Observability;

namespace ControlDoor.Hikvision
{
    public sealed class HikvisionSdkWrapper : IHikvisionGateway
    {
        private const string FaceSetupUrl = "PUT /ISAPI/Intelligent/FDLib/FDSetUp?format=json";
        private const int NetSdkConfigStatusSuccess = 1000;
        private const int NetSdkConfigStatusFinish = 1002;
        private const int NetSdkConfigStatusFailed = 1003;

        private readonly IHikvisionSdkNativeClient nativeClient;
        private readonly HikvisionIsapiClient isapiClient;
        private readonly SdkTraceLogger traceLogger;
        private bool initialized;
        private bool disposed;

        public HikvisionSdkWrapper()
            : this(new HikvisionSdkNativeClient(), new HikvisionIsapiClient(), null)
        {
        }

        internal HikvisionSdkWrapper(IHikvisionSdkNativeClient nativeClient, HikvisionIsapiClient isapiClient = null, SdkTraceLogger traceLogger = null)
        {
            this.nativeClient = nativeClient ?? throw new ArgumentNullException(nameof(nativeClient));
            this.isapiClient = isapiClient ?? new HikvisionIsapiClient();
            this.traceLogger = traceLogger;
        }

        public event EventHandler<AlarmEventData> OnAlarmEvent;

        public Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
        {
            HikvisionGatewayValidator.RequireLoginRequest(request);
            cancellationToken.ThrowIfCancellationRequested();

            return Execute("Login", request, () =>
            {
                EnsureInitialized();
                DeviceInfo deviceInfo;
                var userId = nativeClient.Login(request, out deviceInfo);
                if (userId < 0)
                {
                    ThrowLastError("Login");
                }

                return new LoginResponse
                {
                    UserId = userId,
                    DeviceInfo = deviceInfo
                };
            });
        }

        public Task LogoutAsync(LogoutRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            cancellationToken.ThrowIfCancellationRequested();

            return Execute("Logout", request, () =>
            {
                EnsureInitialized();
                nativeClient.Logout(request.UserId);
                return 0;
            });
        }

        public Task<AlarmSetupResponse> SetAlarmAsync(AlarmSetupRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            cancellationToken.ThrowIfCancellationRequested();

            return Execute("SetAlarm", request, () =>
            {
                EnsureInitialized();
                if (!nativeClient.SetMessageCallback((command, alarmer, alarmInfo, alarmInfoLength, userData) =>
                    HandleNativeAlarm(command, alarmer, alarmInfo, alarmInfoLength, userData, request.Callback)))
                {
                    ThrowLastError("SetAlarmCallback");
                }

                var handle = nativeClient.SetupAlarm(request.UserId, request.Level, request.AlarmInfoType);
                if (handle < 0)
                {
                    ThrowLastError("SetAlarm");
                }

                return new AlarmSetupResponse { AlarmHandle = handle };
            });
        }

        public Task CloseAlarmAsync(AlarmCloseRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireAlarmHandle(request.AlarmHandle);
            cancellationToken.ThrowIfCancellationRequested();

            return Execute("CloseAlarm", request, () =>
            {
                EnsureInitialized();
                if (!nativeClient.CloseAlarm(request.AlarmHandle))
                {
                    ThrowLastError("CloseAlarm");
                }

                return 0;
            });
        }

        public Task AddPersonAsync(AddPersonRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            HikvisionGatewayValidator.RequirePerson(request.Person);
            return SendIsapiJsonAsync("AddPerson", "/ISAPI/AccessControl/UserManagement/UserInfo/Record", IsapiMethod.Post, request.UserId, request.Person, cancellationToken);
        }

        public Task UpsertPersonAsync(UpsertPersonRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            HikvisionGatewayValidator.RequirePerson(request.Person);
            var body = HikvisionPersonPayloadBuilder.BuildUserInfoSetup(request.Person);
            return SendIsapiJsonAsync("UpsertPerson", "/ISAPI/AccessControl/UserInfo/SetUp?format=json", IsapiMethod.Put, request.UserId, body, cancellationToken);
        }

        public Task DeletePersonAsync(DeletePersonRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            if (string.IsNullOrWhiteSpace(request.EmployeeId) && string.IsNullOrWhiteSpace(request.CardNumber))
            {
                throw new ArgumentException("EmployeeId or CardNumber is required.");
            }

            return SendIsapiJsonAsync("DeletePerson", "/ISAPI/AccessControl/UserManagement/UserInfo/Delete", IsapiMethod.Put, request.UserId, request, cancellationToken);
        }

        public Task ModifyPersonAsync(ModifyPersonRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            HikvisionGatewayValidator.RequirePerson(request.Person);
            return SendIsapiJsonAsync("ModifyPerson", "/ISAPI/AccessControl/UserManagement/UserInfo/Modify", IsapiMethod.Put, request.UserId, request.Person, cancellationToken);
        }

        public async Task<QueryPersonResponse> QueryPersonAsync(QueryPersonRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            var response = await SendIsapiJsonForResponseAsync("QueryPerson", "/ISAPI/AccessControl/UserManagement/UserInfo/Search", IsapiMethod.Post, request.UserId, request, cancellationToken).ConfigureAwait(false);
            return new QueryPersonResponse
            {
                Exists = response.IsSuccessStatusCode,
                RawResponse = response.Body,
                TotalCount = string.IsNullOrWhiteSpace(response.Body) ? 0 : 1
            };
        }

        public Task UploadFaceAsync(UploadFaceRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            HikvisionGatewayValidator.RequireFace(request.Face, request.MaxImageBytes);
            var pictureBytes = HikvisionGatewayValidator.ResolveFaceBytes(request.Face);
            cancellationToken.ThrowIfCancellationRequested();

            return Execute("UploadFace", request, () =>
            {
                EnsureInitialized();
                var body = BuildFaceSetupPayload(request.Face.EmployeeId);
                string responseBody;
                var status = nativeClient.UploadFaceData(request.UserId, FaceSetupUrl, body, pictureBytes, out responseBody);
                if (status < 0)
                {
                    ThrowLastError("UploadFace");
                }

                if (status == NetSdkConfigStatusSuccess || status == NetSdkConfigStatusFinish)
                {
                    EnsureIsapiBodyAccepted("UploadFace", responseBody);
                    return 0;
                }

                throw new DeviceGatewayException("UploadFace", SdkError.FromCode(
                    status == 0 ? NetSdkConfigStatusFailed : status,
                    BuildRemoteConfigErrorMessage(status, responseBody),
                    "SDK_REMOTE_CONFIG"));
            });
        }

        public Task DeleteFaceAsync(DeleteFaceRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            if (string.IsNullOrWhiteSpace(request.EmployeeId) && string.IsNullOrWhiteSpace(request.CardNumber) && string.IsNullOrWhiteSpace(request.FaceId))
            {
                throw new ArgumentException("EmployeeId, CardNumber or FaceId is required.");
            }

            return SendIsapiJsonAsync("DeleteFace", "/ISAPI/Intelligent/FDLib/FaceDataRecord/Delete", IsapiMethod.Put, request.UserId, request, cancellationToken);
        }

        public async Task<QueryFaceResponse> QueryFaceAsync(QueryFaceRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            var response = await SendIsapiJsonForResponseAsync("QueryFace", "/ISAPI/Intelligent/FDLib/FaceDataRecord/Search", IsapiMethod.Post, request.UserId, request, cancellationToken).ConfigureAwait(false);
            return new QueryFaceResponse
            {
                Exists = response.IsSuccessStatusCode,
                RawResponse = response.Body,
                TotalCount = string.IsNullOrWhiteSpace(response.Body) ? 0 : 1
            };
        }

        public Task SetPermissionAsync(SetPermissionRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            HikvisionGatewayValidator.RequirePermissions(request.Permissions);
            return SendIsapiJsonAsync("SetPermission", "/ISAPI/AccessControl/UserRight/SetUp", IsapiMethod.Put, request.UserId, request.Permissions, cancellationToken);
        }

        public async Task<QueryPermissionResponse> QueryPermissionAsync(QueryPermissionRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            var response = await SendIsapiJsonForResponseAsync("QueryPermission", "/ISAPI/AccessControl/UserRight/Search", IsapiMethod.Post, request.UserId, request, cancellationToken).ConfigureAwait(false);
            return new QueryPermissionResponse
            {
                RawResponse = response.Body,
                TotalCount = string.IsNullOrWhiteSpace(response.Body) ? 0 : 1
            };
        }

        public Task<GateControlResponse> ControlGatewayAsync(GateControlRequest request, CancellationToken cancellationToken = default)
        {
            HikvisionGatewayValidator.RequireGateControl(request);
            cancellationToken.ThrowIfCancellationRequested();

            return Execute("ControlGateway", request, () =>
            {
                EnsureInitialized();
                var ok = nativeClient.ControlGateway(request.UserId, request.GateIndex, request.Command);
                if (!ok)
                {
                    ThrowLastError("ControlGateway");
                }

                return new GateControlResponse
                {
                    Success = true,
                    Code = "OK",
                    Message = "成功",
                    GateIndex = request.GateIndex,
                    Command = request.Command
                };
            });
        }

        public Task<CaptureResponse> CapturePictureAsync(CaptureRequest request, CancellationToken cancellationToken = default)
        {
            HikvisionGatewayValidator.RequireCapture(request);
            cancellationToken.ThrowIfCancellationRequested();

            return Execute("CapturePicture", request, () =>
            {
                EnsureInitialized();
                var filePath = Path.Combine(Path.GetTempPath(), "ControlDoor-Capture-" + Guid.NewGuid().ToString("N") + ".jpg");
                try
                {
                    var ok = nativeClient.CaptureJpegPicture(request.UserId, request.Channel, request.PictureQuality, filePath);
                    if (!ok)
                    {
                        ThrowLastError("CapturePicture");
                    }

                    return new CaptureResponse
                    {
                        ImageBytes = File.Exists(filePath) ? File.ReadAllBytes(filePath) : new byte[0],
                        ContentType = "image/jpeg",
                        CapturedAt = DateTime.Now
                    };
                }
                finally
                {
                    TryDelete(filePath);
                }
            });
        }

        public async Task<FaceCaptureResult> CaptureFaceAsync(CaptureRequest request, CancellationToken cancellationToken = default)
        {
            var picture = await CapturePictureAsync(request, cancellationToken).ConfigureAwait(false);
            return new FaceCaptureResult
            {
                ImageBytes = picture.ImageBytes,
                ContentType = picture.ContentType,
                CapturedAt = picture.CapturedAt,
                FaceDetected = picture.ImageBytes != null && picture.ImageBytes.Length > 0,
                QualityScore = picture.ImageBytes != null && picture.ImageBytes.Length > 0 ? 80 : 0
            };
        }

        public async Task<EventQueryResponse> QueryEventRecordAsync(EventQueryRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            HikvisionGatewayValidator.RequireDateRange(request.BeginTime, request.EndTime);
            var response = await SendIsapiJsonForResponseAsync("QueryEventRecord", "/ISAPI/AccessControl/AcsEvent", IsapiMethod.Post, request.UserId, request, cancellationToken).ConfigureAwait(false);
            return new EventQueryResponse
            {
                RawResponse = response.Body,
                TotalCount = string.IsNullOrWhiteSpace(response.Body) ? 0 : 1,
                HasMore = false
            };
        }

        public Task<IsapiResponse> SendIsapiRequestAsync(IsapiRequest request, CancellationToken cancellationToken = default)
        {
            HikvisionGatewayValidator.RequireIsapiRequest(request);
            if (string.IsNullOrWhiteSpace(request.BaseAddress))
            {
                return Execute("STDXMLConfig", request, () =>
                {
                    EnsureInitialized();
                    string output;
                    var ok = nativeClient.StandardXmlConfig(request.UserId, BuildSdkRequestUrl(request), request.Body, out output);
                    if (!ok)
                    {
                        ThrowLastError("STDXMLConfig");
                    }

                    return new IsapiResponse
                    {
                        StatusCode = 200,
                        Body = output,
                        ContentType = request.ContentType
                    };
                });
            }

            return isapiClient.SendAsync(request, cancellationToken);
        }

        public async Task<DeviceCapabilities> GetDeviceCapabilitiesAsync(DeviceCapabilitiesRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            var response = await SendIsapiRequestAsync(new IsapiRequest
            {
                UserId = request.UserId,
                Method = IsapiMethod.Get,
                Path = request.IsapiPath
            }, cancellationToken).ConfigureAwait(false);
            var capabilities = HikvisionXmlParser.ParseCapabilities(response.Body);
            capabilities.SupportsIsapi = capabilities.SupportsIsapi || response.IsSuccessStatusCode;
            return capabilities;
        }

        public async Task<DeviceInfo> GetDeviceInfoAsync(DeviceInfoRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            var response = await SendIsapiRequestAsync(new IsapiRequest
            {
                UserId = request.UserId,
                Method = IsapiMethod.Get,
                Path = request.IsapiPath
            }, cancellationToken).ConfigureAwait(false);
            return HikvisionXmlParser.ParseDeviceInfo(response.Body);
        }

        public int GetLastErrorCode()
        {
            return nativeClient.GetLastError();
        }

        public string GetErrorMessage(int errorCode)
        {
            var message = nativeClient.GetErrorMessage(errorCode);
            return string.IsNullOrWhiteSpace(message) ? SdkError.GetDefaultMessage(errorCode) : message;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (initialized)
            {
                nativeClient.Cleanup();
                initialized = false;
            }
        }

        internal void RaiseManagedAlarmForTest(AlarmEventData data)
        {
            RaiseManagedAlarm(data);
        }

        private Task SendIsapiJsonAsync(string operationName, string path, IsapiMethod method, int userId, object body, CancellationToken cancellationToken)
        {
            return SendIsapiJsonForResponseAsync(operationName, path, method, userId, body, cancellationToken);
        }

        private async Task<IsapiResponse> SendIsapiJsonForResponseAsync(string operationName, string path, IsapiMethod method, int userId, object body, CancellationToken cancellationToken)
        {
            var response = await SendIsapiRequestAsync(new IsapiRequest
            {
                UserId = userId,
                Method = method,
                Path = path,
                Body = HikvisionGatewayJson.Serialize(body),
                ContentType = "application/json"
            }, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new DeviceGatewayException(operationName, SdkError.FromHttpStatusCode(response.StatusCode, response.Body));
            }

            EnsureIsapiBodyAccepted(operationName, response.Body);
            return response;
        }

        private Task<T> Execute<T>(string operationName, object request, Func<T> action)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var result = action();
                watch.Stop();
                traceLogger?.Trace(operationName, null, true, watch.ElapsedMilliseconds, null, null);
                return Task.FromResult(result);
            }
            catch (DeviceGatewayException ex)
            {
                watch.Stop();
                traceLogger?.Trace(operationName, null, false, watch.ElapsedMilliseconds, ex.Error.Code, ex.Message);
                throw;
            }
            catch (Exception ex) when (!(ex is ArgumentException) && !(ex is OperationCanceledException))
            {
                watch.Stop();
                var error = SdkError.FromException(ex);
                traceLogger?.Trace(operationName, null, false, watch.ElapsedMilliseconds, error.Code, error.Message);
                throw new DeviceGatewayException(operationName, error, ex);
            }
        }

        private void EnsureInitialized()
        {
            if (initialized)
            {
                return;
            }

            if (!nativeClient.Init())
            {
                ThrowLastError("NET_DVR_Init");
            }

            initialized = true;
        }

        private void ThrowLastError(string operationName)
        {
            var code = nativeClient.GetLastError();
            throw new DeviceGatewayException(operationName, SdkError.FromCode(code, GetErrorMessage(code)));
        }

        private bool HandleNativeAlarm(int command, IntPtr alarmer, IntPtr alarmInfo, int alarmInfoLength, IntPtr userData, AlarmCallbackDelegate callback)
        {
            try
            {
                var payload = new byte[Math.Max(0, alarmInfoLength)];
                if (alarmInfo != IntPtr.Zero && payload.Length > 0)
                {
                    System.Runtime.InteropServices.Marshal.Copy(alarmInfo, payload, 0, payload.Length);
                }

                var data = new AlarmEventData
                {
                    Command = command,
                    EventType = MapAlarmCommand(command),
                    RawPayload = payload,
                    RawPayloadSummary = "length=" + payload.Length,
                    EventTime = DateTime.Now
                };
                CopyAlarmer(alarmer, data);
                CopyAcsAlarmInfo(command, alarmInfo, alarmInfoLength, data);

                callback?.Invoke(data);
                RaiseManagedAlarm(data);
                return true;
            }
            catch
            {
                return true;
            }
        }

        private static void CopyAlarmer(IntPtr alarmer, AlarmEventData data)
        {
            if (alarmer == IntPtr.Zero || data == null)
            {
                return;
            }

            var source = (NET_DVR_ALARMER)Marshal.PtrToStructure(alarmer, typeof(NET_DVR_ALARMER));
            if (source.byUserIDValid != 0)
            {
                data.UserId = source.lUserID;
            }

            if (source.byDeviceIPValid != 0)
            {
                data.DeviceIpAddress = GetAnsiString(source.sDeviceIP);
            }

            if (source.bySerialValid != 0)
            {
                data.DeviceSerialNumber = GetAnsiString(source.sSerialNumber);
            }

            if (!string.IsNullOrWhiteSpace(data.DeviceIpAddress))
            {
                data.Values["deviceIp"] = data.DeviceIpAddress;
            }

            if (!string.IsNullOrWhiteSpace(data.DeviceSerialNumber))
            {
                data.Values["serialNumber"] = data.DeviceSerialNumber;
            }
        }

        private static void CopyAcsAlarmInfo(int command, IntPtr alarmInfo, int alarmInfoLength, AlarmEventData data)
        {
            if (command != 0x5002 || alarmInfo == IntPtr.Zero || data == null)
            {
                return;
            }

            var minimumSize = Marshal.SizeOf(typeof(NET_DVR_ACS_ALARM_INFO));
            if (alarmInfoLength < minimumSize)
            {
                return;
            }

            var acs = (NET_DVR_ACS_ALARM_INFO)Marshal.PtrToStructure(alarmInfo, typeof(NET_DVR_ACS_ALARM_INFO));
            data.EventTime = ToDateTime(acs.struTime) ?? data.EventTime;
            data.PictureBytes = CopyPointerBytes(acs.pPicData, acs.dwPicDataLen);
            data.RawPayloadSummary = "length=" + Math.Max(0, alarmInfoLength) +
                "; major=" + acs.dwMajor +
                "; minor=" + acs.dwMinor +
                "; pic=" + (data.PictureBytes == null ? 0 : data.PictureBytes.Length);
            data.CardNumber = GetAnsiString(acs.struAcsEventInfo.byCardNo);
            if (acs.struAcsEventInfo.dwEmployeeNo > 0)
            {
                data.EmployeeId = acs.struAcsEventInfo.dwEmployeeNo.ToString();
            }

            data.DoorIndex = acs.struAcsEventInfo.dwDoorNo;
            data.Success = IsSuccessMinor(acs.dwMinor);
            data.Values["dwMajor"] = acs.dwMajor.ToString();
            data.Values["dwMinor"] = acs.dwMinor.ToString();
            data.Values["dwSerialNo"] = acs.struAcsEventInfo.dwSerialNo.ToString();
            data.Values["dwDoorNo"] = acs.struAcsEventInfo.dwDoorNo.ToString();
            data.Values["dwCardReaderNo"] = acs.struAcsEventInfo.dwCardReaderNo.ToString();
            data.Values["dwEmployeeNo"] = acs.struAcsEventInfo.dwEmployeeNo.ToString();
            data.Values["dwPicDataLen"] = acs.dwPicDataLen.ToString();
        }

        private void RaiseManagedAlarm(AlarmEventData data)
        {
            var handler = OnAlarmEvent;
            if (handler != null && data != null)
            {
                handler(this, data);
            }
        }

        private static string MapAlarmCommand(int command)
        {
            switch (command)
            {
                case 0x5002:
                    return "COMM_ALARM_ACS";
                case 0x4010:
                    return "COMM_ALARM_FACE";
                case 0x4021:
                    return "COMM_UPLOAD_AIOP_VIDEO";
                default:
                    return "SDK_COMMAND_" + command;
            }
        }

        private static string BuildSdkRequestUrl(IsapiRequest request)
        {
            var method = request.Method.ToString().ToUpperInvariant();
            return method + " " + request.Path;
        }

        private static string BuildFaceSetupPayload(string employeeId)
        {
            return HikvisionGatewayJson.Serialize(new
            {
                faceLibType = "blackFD",
                FDID = "1",
                FPID = employeeId
            });
        }

        private static void EnsureIsapiBodyAccepted(string operationName, string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return;
            }

            Dictionary<string, object> values;
            try
            {
                values = HikvisionGatewayJson.Deserialize<Dictionary<string, object>>(body);
            }
            catch
            {
                return;
            }

            if (values == null || !values.ContainsKey("statusCode"))
            {
                return;
            }

            var statusCode = Convert.ToString(values["statusCode"]);
            if (string.Equals(statusCode, "1", StringComparison.Ordinal))
            {
                return;
            }

            int numericCode;
            if (!int.TryParse(statusCode, out numericCode))
            {
                numericCode = 500;
            }

            throw new DeviceGatewayException(operationName, SdkError.FromCode(numericCode, BuildIsapiErrorMessage(values, body), "ISAPI"));
        }

        private static string BuildIsapiErrorMessage(IDictionary<string, object> values, string fallback)
        {
            var parts = new[]
            {
                TryGetString(values, "statusString"),
                TryGetString(values, "subStatusCode"),
                TryGetString(values, "errorMsg"),
                TryGetString(values, "errorCode")
            }.Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();

            return parts.Length == 0 ? fallback : string.Join(" / ", parts);
        }

        private static string BuildRemoteConfigErrorMessage(int status, string body)
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                Dictionary<string, object> values;
                try
                {
                    values = HikvisionGatewayJson.Deserialize<Dictionary<string, object>>(body);
                    var message = BuildIsapiErrorMessage(values, body);
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        return message;
                    }
                }
                catch
                {
                    return body;
                }
            }

            return "RemoteConfig status " + status;
        }

        private static string TryGetString(IDictionary<string, object> values, string key)
        {
            object value;
            return values != null && values.TryGetValue(key, out value) ? Convert.ToString(value) : null;
        }

        private static void TryDelete(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch
            {
            }
        }

        private static DateTime? ToDateTime(NET_DVR_TIME value)
        {
            try
            {
                if (value.dwYear < 1970 || value.dwMonth < 1 || value.dwMonth > 12 || value.dwDay < 1 || value.dwDay > 31)
                {
                    return null;
                }

                return new DateTime(value.dwYear, value.dwMonth, value.dwDay, value.dwHour, value.dwMinute, value.dwSecond);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsSuccessMinor(int minor)
        {
            return minor == 0x01 ||
                minor == 0x26 ||
                minor == 0x2E ||
                minor == 0x3C ||
                minor == 0x4B ||
                minor == 0x4D ||
                minor == 0x65;
        }

        private static byte[] CopyPointerBytes(IntPtr pointer, int length)
        {
            if (pointer == IntPtr.Zero || length <= 0)
            {
                return new byte[0];
            }

            var bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            return bytes;
        }

        private static string GetAnsiString(byte[] value)
        {
            if (value == null || value.Length == 0)
            {
                return string.Empty;
            }

            var length = Array.IndexOf(value, (byte)0);
            if (length < 0)
            {
                length = value.Length;
            }

            return length <= 0 ? string.Empty : System.Text.Encoding.Default.GetString(value, 0, length).Trim();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NET_DVR_ALARMER
        {
            public byte byUserIDValid;

            public byte bySerialValid;

            public byte byVersionValid;

            public byte byDeviceNameValid;

            public byte byMacAddrValid;

            public byte byLinkPortValid;

            public byte byDeviceIPValid;

            public byte bySocketIPValid;

            public int lUserID;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)]
            public byte[] sSerialNumber;

            public int dwDeviceVersion;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] sDeviceName;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            public byte[] byMacAddr;

            public ushort wLinkPort;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
            public byte[] sDeviceIP;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
            public byte[] sSocketIP;

            public byte byIpProtocol;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 11)]
            public byte[] byRes2;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NET_DVR_TIME
        {
            public int dwYear;

            public int dwMonth;

            public int dwDay;

            public int dwHour;

            public int dwMinute;

            public int dwSecond;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NET_DVR_IPADDR
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] sIpV4;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
            public byte[] sIpV6;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NET_DVR_ACS_EVENT_INFO
        {
            public int dwSize;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] byCardNo;

            public byte byCardType;

            public byte byWhiteListNo;

            public byte byReportChannel;

            public byte byCardReaderKind;

            public int dwCardReaderNo;

            public int dwDoorNo;

            public int dwVerifyNo;

            public int dwAlarmInNo;

            public int dwAlarmOutNo;

            public int dwCaseSensorNo;

            public int dwRs485No;

            public int dwMultiCardGroupNo;

            public ushort wAccessChannel;

            public byte byDeviceNo;

            public byte byDistractControlNo;

            public int dwEmployeeNo;

            public ushort wLocalControllerID;

            public byte byInternetAccess;

            public byte byType;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            public byte[] byMACAddr;

            public byte bySwipeCardType;

            public byte byRes2;

            public int dwSerialNo;

            public byte byChannelControllerID;

            public byte byChannelControllerLampID;

            public byte byChannelControllerIRAdaptorID;

            public byte byChannelControllerIREmitterID;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public byte[] byRes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NET_DVR_ACS_ALARM_INFO
        {
            public int dwSize;

            public int dwMajor;

            public int dwMinor;

            public NET_DVR_TIME struTime;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] sNetUser;

            public NET_DVR_IPADDR struRemoteHostAddr;

            public NET_DVR_ACS_EVENT_INFO struAcsEventInfo;

            public int dwPicDataLen;

            public IntPtr pPicData;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)]
            public byte[] byRes;
        }
    }
}
