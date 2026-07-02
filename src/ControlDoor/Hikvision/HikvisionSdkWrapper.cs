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
        private const string FaceDeleteUrl = "/ISAPI/Intelligent/FDLib/FDSearch/Delete?format=json&FDID=1&faceLibType=blackFD";
        private const string FaceSearchUrl = "/ISAPI/Intelligent/FDLib/FDSearch?format=json";
        private const string DefaultFaceLibType = "blackFD";
        private const string DefaultFaceLibId = "1";
        private const int NetSdkConfigStatusSuccess = 1000;
        private const int NetSdkConfigStatusFinish = 1002;
        private const int NetSdkConfigStatusFailed = 1003;

        private readonly IHikvisionSdkNativeClient nativeClient;
        private readonly HikvisionIsapiClient isapiClient;
        private readonly SdkTraceLogger traceLogger;
        private readonly object initializationGate = new object();
        private HikvisionAlarmNativeCallback alarmCallback;
        private volatile bool initialized;
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
                var handle = nativeClient.SetupAlarm(request.UserId, request.Level, request.AlarmInfoType, request.DeployType);
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

        public Task<AlarmDeploymentStatus> GetAlarmDeploymentStatusAsync(AlarmDeploymentStatusRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            if (request.AlarmInputIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(request.AlarmInputIndex), "AlarmInputIndex must be greater than or equal to 0.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Execute("GetAlarmDeploymentStatus", request, () =>
            {
                EnsureInitialized();
                AcsWorkStatus workStatus;
                if (!nativeClient.GetAcsWorkStatus(request.UserId, request.Channel, out workStatus))
                {
                    ThrowLastError("GetAlarmDeploymentStatus");
                }

                var values = workStatus == null ? null : workStatus.SetupAlarmStatus;
                if (values == null || request.AlarmInputIndex >= values.Length)
                {
                    return new AlarmDeploymentStatus
                    {
                        Known = false,
                        IsDeployed = false,
                        RawSummary = "Alarm input index is outside ACS work status range."
                    };
                }

                var raw = values[request.AlarmInputIndex];
                return new AlarmDeploymentStatus
                {
                    Known = raw == 0 || raw == 1,
                    IsDeployed = raw == 1,
                    RawSetupAlarmStatus = raw,
                    RawSummary = "bySetupAlarmStatus[" + request.AlarmInputIndex + "]=" + raw
                };
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
            var body = request.ProvisioningMode == PersonProvisioningMode.Permission
                ? HikvisionPersonPayloadBuilder.BuildPermissionUserInfoSetup(request.Person)
                : HikvisionPersonPayloadBuilder.BuildUserInfoSetup(request.Person);
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

            return SendIsapiJsonAsync("DeletePerson", "/ISAPI/AccessControl/UserInfo/Delete?format=json", IsapiMethod.Put, request.UserId, BuildPersonDeletePayload(request), cancellationToken);
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

            return SendIsapiJsonAsync("DeleteFace", FaceDeleteUrl, IsapiMethod.Put, request.UserId, BuildFaceDeletePayload(request), cancellationToken);
        }

        public async Task<QueryFaceResponse> QueryFaceAsync(QueryFaceRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            var response = await SendIsapiJsonForResponseAsync("QueryFace", FaceSearchUrl, IsapiMethod.Post, request.UserId, BuildFaceSearchPayload(request), cancellationToken).ConfigureAwait(false);
            return ParseFaceSearchResponse(response.Body, request.EmployeeId);
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

        public Task<FaceCaptureResult> CaptureFaceAsync(CaptureRequest request, CancellationToken cancellationToken = default)
        {
            HikvisionGatewayValidator.RequireCapture(request);
            cancellationToken.ThrowIfCancellationRequested();

            return Execute("CaptureFace", request, () =>
            {
                EnsureInitialized();
                // 100 次 × 100ms = 10s 采集超时，与明眸设备采集窗口一致。
                const int maxAttempts = 100;
                const int waitIntervalMs = 100;

                byte[] faceImage;
                byte faceQuality;
                int errorCode;
                var status = nativeClient.CaptureFace(
                    request.UserId, maxAttempts, waitIntervalMs,
                    cancellationToken,
                    out faceImage, out faceQuality, out errorCode);

                if (status == NetSdkConfigStatusSuccess)
                {
                    if (faceImage == null || faceImage.Length == 0)
                    {
                        throw new DeviceGatewayException("CaptureFace",
                            SdkError.FromCode(NetSdkConfigStatusFailed, "采集完成但未检测到有效人脸。", "FACE_CAPTURE_NO_FACE"));
                    }

                    return new FaceCaptureResult
                    {
                        ImageBytes = faceImage,
                        ContentType = "image/jpeg",
                        CapturedAt = DateTime.Now,
                        FaceDetected = true,
                        QualityScore = faceQuality
                    };
                }

                if (status == NetSdkConfigStatusFailed)
                {
                    ThrowLastError("CaptureFace");
                }

                if (status == NetSdkConfigStatusFinish)
                {
                    throw new DeviceGatewayException("CaptureFace",
                        SdkError.FromCode(NetSdkConfigStatusFailed, "人脸采集完成但未检测到有效人脸。", "FACE_CAPTURE_NO_FACE"));
                }

                // NEED_WAIT 跑满循环视为超时（设备未在窗口内采到人脸）。
                throw new DeviceGatewayException("CaptureFace",
                    SdkError.FromCode(NetSdkConfigStatusFailed, "人脸采集超时，请确保人脸正对设备摄像头。", "FACE_CAPTURE_TIMEOUT"));
            });
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

            isapiClient?.Dispose();
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

            lock (initializationGate)
            {
                if (initialized)
                {
                    return;
                }

                if (!nativeClient.Init())
                {
                    ThrowLastError("NET_DVR_Init");
                }

                alarmCallback = HandleNativeAlarm;
                if (!nativeClient.SetMessageCallback(alarmCallback))
                {
                    alarmCallback = null;
                    ThrowLastError("SetAlarmCallback");
                }

                initialized = true;
            }
        }

        private void ThrowLastError(string operationName)
        {
            var code = nativeClient.GetLastError();
            throw new DeviceGatewayException(operationName, SdkError.FromCode(code, GetErrorMessage(code)));
        }

        private bool HandleNativeAlarm(int command, IntPtr alarmer, IntPtr alarmInfo, int alarmInfoLength, IntPtr userData)
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

                RaiseManagedAlarm(data);
                return true;
            }
            catch (Exception ex)
            {
                traceLogger?.Trace("NativeAlarmCallback", null, false, 0, null, ex.Message);
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

            var baseSize = Marshal.SizeOf(typeof(NET_DVR_ACS_ALARM_INFO));
            if (alarmInfoLength < baseSize)
            {
                return;
            }

            var acs = (NET_DVR_ACS_ALARM_INFO)Marshal.PtrToStructure(alarmInfo, typeof(NET_DVR_ACS_ALARM_INFO));
            ApplyAcsAlarmInfo(data, acs.dwMajor, acs.dwMinor, acs.struTime, acs.struAcsEventInfo, acs.dwPicDataLen, acs.pPicData, alarmInfoLength);
            CopyAcsEventInfoExtend(alarmInfo, alarmInfoLength, data);
        }

        private static void ApplyAcsAlarmInfo(
            AlarmEventData data,
            int major,
            int minor,
            NET_DVR_TIME time,
            NET_DVR_ACS_EVENT_INFO eventInfo,
            int pictureDataLength,
            IntPtr pictureData,
            int alarmInfoLength)
        {
            data.EventTime = ToDateTime(time) ?? data.EventTime;
            data.PictureBytes = CopyPointerBytes(pictureData, pictureDataLength);
            data.RawPayloadSummary = "length=" + Math.Max(0, alarmInfoLength) +
                "; major=" + major +
                "; minor=" + minor +
                "; pic=" + (data.PictureBytes == null ? 0 : data.PictureBytes.Length);
            data.CardNumber = GetAnsiString(eventInfo.byCardNo);
            if (eventInfo.dwEmployeeNo > 0)
            {
                data.EmployeeId = eventInfo.dwEmployeeNo.ToString();
            }

            data.DoorIndex = eventInfo.dwDoorNo;
            data.Success = IsSuccessMinor(minor);
            data.Values["dwMajor"] = major.ToString();
            data.Values["dwMinor"] = minor.ToString();
            data.Values["dwSerialNo"] = eventInfo.dwSerialNo.ToString();
            data.Values["dwDoorNo"] = eventInfo.dwDoorNo.ToString();
            data.Values["dwCardReaderNo"] = eventInfo.dwCardReaderNo.ToString();
            data.Values["dwEmployeeNo"] = eventInfo.dwEmployeeNo.ToString();
            data.Values["dwPicDataLen"] = pictureDataLength.ToString();
        }

        private static void CopyAcsEventInfoExtend(IntPtr alarmInfo, int alarmInfoLength, AlarmEventData data)
        {
            var extendLayoutSize = Marshal.SizeOf(typeof(NET_DVR_ACS_ALARM_INFO_WITH_EXTEND));
            if (alarmInfoLength < extendLayoutSize)
            {
                return;
            }

            var acs = (NET_DVR_ACS_ALARM_INFO_WITH_EXTEND)Marshal.PtrToStructure(
                alarmInfo,
                typeof(NET_DVR_ACS_ALARM_INFO_WITH_EXTEND));
            if (acs.byAcsEventInfoExtend != 1 || acs.pAcsEventInfoExtend == IntPtr.Zero)
            {
                return;
            }

            try
            {
                var extend = (NET_DVR_ACS_EVENT_INFO_EXTEND)Marshal.PtrToStructure(
                    acs.pAcsEventInfoExtend,
                    typeof(NET_DVR_ACS_EVENT_INFO_EXTEND));
                data.CurrentEventFlag = extend.byCurrentEvent;
                data.Values["byCurrentEvent"] = extend.byCurrentEvent.ToString();
                data.RawPayloadSummary = data.RawPayloadSummary + "; byCurrentEvent=" + extend.byCurrentEvent;
            }
            catch
            {
                // Native callback memory is device-owned. Keep the base ACS event if the optional extension cannot be read.
            }
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
                faceLibType = DefaultFaceLibType,
                FDID = DefaultFaceLibId,
                FPID = employeeId
            });
        }

        private static object BuildFaceSearchPayload(QueryFaceRequest request)
        {
            return new
            {
                searchResultPosition = 0,
                maxResults = 1,
                faceLibType = DefaultFaceLibType,
                FDID = DefaultFaceLibId,
                FPID = request.EmployeeId
            };
        }

        private static object BuildFaceDeletePayload(DeleteFaceRequest request)
        {
            var value = FirstNonEmpty(request.EmployeeId, request.FaceId, request.CardNumber);
            return new
            {
                FPID = new[]
                {
                    new { value }
                }
            };
        }

        private static object BuildPersonDeletePayload(DeletePersonRequest request)
        {
            var employeeNo = FirstNonEmpty(request.EmployeeId, request.CardNumber);
            return new
            {
                UserInfoDelCond = new
                {
                    EmployeeNoList = new[]
                    {
                        new { employeeNo }
                    }
                }
            };
        }

        private static QueryFaceResponse ParseFaceSearchResponse(string responseBody, string employeeId)
        {
            var json = ExtractJsonFromMultipart(responseBody);
            var response = new QueryFaceResponse
            {
                RawResponse = responseBody
            };

            Dictionary<string, object> values;
            try
            {
                values = HikvisionGatewayJson.Deserialize<Dictionary<string, object>>(json);
            }
            catch
            {
                return response;
            }

            if (values == null)
            {
                return response;
            }

            var records = ReadObjectArray(values, "MatchList", "FaceDataRecord");
            foreach (var record in records)
            {
                var faceBase64 = FirstNonEmpty(
                    TryGetString(record, "facePicBinary"),
                    TryGetString(record, "FacePicBinary"),
                    TryGetString(record, "facePic"),
                    TryGetString(record, "FacePic"),
                    TryGetString(record, "modelData"));
                response.Faces.Add(new FaceInfo
                {
                    EmployeeId = FirstNonEmpty(TryGetString(record, "FPID"), TryGetString(record, "employeeNo"), employeeId),
                    FaceId = TryGetString(record, "faceID"),
                    ImageBase64 = faceBase64,
                    ImageFormat = "jpg"
                });
            }

            var totalMatches = ReadInt(values, "totalMatches");
            var numOfMatches = ReadInt(values, "numOfMatches");
            response.TotalCount = Math.Max(Math.Max(totalMatches, numOfMatches), response.Faces.Count);
            response.Exists = response.TotalCount > 0;
            return response;
        }

        private static string ExtractJsonFromMultipart(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return response;
            }

            var trimmed = response.TrimStart();
            var jsonStart = response.IndexOf("\r\n\r\n{", StringComparison.Ordinal);
            if (jsonStart < 0)
            {
                jsonStart = response.IndexOf("\n\n{", StringComparison.Ordinal);
            }

            if (jsonStart < 0 && (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal)))
            {
                jsonStart = response.IndexOf(trimmed[0]);
            }

            if (jsonStart < 0)
            {
                return response;
            }

            jsonStart = response.IndexOf('{', jsonStart);
            if (jsonStart < 0)
            {
                return response;
            }

            var jsonEnd = FindJsonObjectEnd(response, jsonStart);
            if (jsonEnd > jsonStart)
            {
                return response.Substring(jsonStart, jsonEnd - jsonStart + 1);
            }

            var lastBrace = response.LastIndexOf('}');
            return lastBrace > jsonStart ? response.Substring(jsonStart, lastBrace - jsonStart + 1) : response;
        }

        private static int FindJsonObjectEnd(string value, int start)
        {
            if (string.IsNullOrEmpty(value) || start < 0 || start >= value.Length)
            {
                return -1;
            }

            var opening = value[start];
            var closing = opening == '{' ? '}' : opening == '[' ? ']' : '\0';
            if (closing == '\0')
            {
                return -1;
            }

            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var index = start; index < value.Length; index++)
            {
                var current = value[index];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }

                    if (current == '\\')
                    {
                        escaped = true;
                        continue;
                    }

                    if (current == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (current == '"')
                {
                    inString = true;
                    continue;
                }

                if (current == opening)
                {
                    depth++;
                    continue;
                }

                if (current == closing)
                {
                    depth--;
                    if (depth == 0)
                    {
                        return index;
                    }
                }
            }

            return -1;
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

            if (values == null)
            {
                return;
            }

            if (!values.ContainsKey("statusCode"))
            {
                if (values.ContainsKey("statusString") || values.ContainsKey("subStatusCode"))
                {
                    if (IsIsapiBodyOk(values))
                    {
                        return;
                    }

                    throw new DeviceGatewayException(operationName, SdkError.FromCode(500, BuildIsapiErrorMessage(values, body), "ISAPI"));
                }

                return;
            }

            var statusCode = Convert.ToString(values["statusCode"]);
            if (IsIsapiBodyOk(values))
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

        private static bool IsIsapiBodyOk(IDictionary<string, object> values)
        {
            var statusCode = TryGetString(values, "statusCode");
            if (string.Equals(statusCode, "1", StringComparison.Ordinal))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(statusCode))
            {
                return false;
            }

            var statusString = TryGetString(values, "statusString");
            var subStatusCode = TryGetString(values, "subStatusCode");
            var statusOk = string.Equals(statusString, "OK", StringComparison.OrdinalIgnoreCase);
            var subOk = string.IsNullOrWhiteSpace(subStatusCode) || string.Equals(subStatusCode, "ok", StringComparison.OrdinalIgnoreCase);
            return statusOk && subOk;
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

        private static int ReadInt(IDictionary<string, object> values, string key)
        {
            object value;
            if (values == null || !values.TryGetValue(key, out value) || value == null)
            {
                return 0;
            }

            int parsed;
            return int.TryParse(Convert.ToString(value), out parsed) ? parsed : 0;
        }

        private static IEnumerable<IDictionary<string, object>> ReadObjectArray(IDictionary<string, object> values, params string[] keys)
        {
            foreach (var key in keys)
            {
                object raw;
                if (values == null || !values.TryGetValue(key, out raw) || raw == null)
                {
                    continue;
                }

                var direct = raw as IDictionary<string, object>;
                if (direct != null)
                {
                    yield return direct;
                    continue;
                }

                var items = raw as System.Collections.IEnumerable;
                if (items == null || raw is string)
                {
                    continue;
                }

                foreach (var item in items)
                {
                    var record = item as IDictionary<string, object>;
                    if (record != null)
                    {
                        yield return record;
                    }
                }
            }
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
            {
                return null;
            }

            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
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

        // Success 语义对齐 main：人脸验证失败(0x4C)记 failure，其余人脸验证类事件记 success。
        // 非 face-verify 类事件已在 AcsAlarmEventRouter 过滤，不会走到这里。
        private static bool IsSuccessMinor(int minor)
        {
            return minor != MinorFaceVerifyFail;
        }

        private const int MinorFaceVerifyPass = 0x4B;
        private const int MinorFaceVerifyFail = 0x4C;

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

        [StructLayout(LayoutKind.Sequential)]
        private struct NET_DVR_ACS_ALARM_INFO_WITH_EXTEND
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

            public ushort wInductiveEventType;

            public byte byPicTransType;

            public byte byRes1;

            public int dwIOTChannelNo;

            public IntPtr pAcsEventInfoExtend;

            public byte byAcsEventInfoExtend;

            public byte byTimeType;

            public byte byRes2;

            public byte byAcsEventInfoExtendV20;

            public IntPtr pAcsEventInfoExtendV20;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public byte[] byRes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NET_DVR_ACS_EVENT_INFO_EXTEND
        {
            public int dwFrontSerialNo;

            public byte byUserType;

            public byte byCurrentVerifyMode;

            public byte byCurrentEvent;

            public byte byPurePwdVerifyEnable;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] byEmployeeNo;
        }
    }
}
