using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Observability;

namespace ControlDoor.Hikvision
{
    public sealed class HikvisionSdkWrapper : IHikvisionGateway
    {
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
            return SendIsapiJsonAsync("UploadFace", "/ISAPI/Intelligent/FDLib/FaceDataRecord", IsapiMethod.Post, request.UserId, request.Face, cancellationToken);
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

                callback?.Invoke(data);
                RaiseManagedAlarm(data);
                return true;
            }
            catch
            {
                return true;
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
    }
}
