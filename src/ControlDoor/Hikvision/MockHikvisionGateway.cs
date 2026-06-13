using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ControlDoor.Hikvision
{
    public sealed class MockHikvisionGateway : IHikvisionGateway
    {
        private readonly object gate = new object();
        private readonly IDictionary<string, MockGatewayBehavior> behaviors = new Dictionary<string, MockGatewayBehavior>(StringComparer.OrdinalIgnoreCase);
        private readonly IDictionary<int, LoginResponse> sessions = new Dictionary<int, LoginResponse>();
        private readonly IDictionary<int, AlarmSetupRequest> alarms = new Dictionary<int, AlarmSetupRequest>();
        private readonly IDictionary<string, PersonInfo> persons = new Dictionary<string, PersonInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly IDictionary<string, FaceInfo> faces = new Dictionary<string, FaceInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly IDictionary<string, PermissionInfo> permissions = new Dictionary<string, PermissionInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly IList<MockGatewayCall> calls = new List<MockGatewayCall>();
        private int nextUserId = 1;
        private int nextAlarmHandle = 1000;
        private int lastErrorCode;
        private bool disposed;

        public MockHikvisionGateway()
        {
            Capabilities = new DeviceCapabilities
            {
                Known = true,
                SupportsAcs = true,
                SupportsAlarm = true,
                SupportsPersonConfig = true,
                SupportsFaceConfig = true,
                SupportsFaceCapture = true,
                SupportsHistoryEventQuery = true,
                SupportsIsapi = true,
                SupportsAiop = true,
                DoorCount = 4,
                ChannelCount = 1,
                Model = "Mock-Hikvision",
                FirmwareVersion = "1.0.0",
                LastCheckedAt = DateTime.Now
            };
            DeviceInfo = new DeviceInfo
            {
                Model = "Mock-Hikvision",
                SerialNumber = "MOCK0001",
                FirmwareVersion = "1.0.0",
                DeviceName = "Mock Device",
                DoorCount = 4,
                ChannelCount = 1,
                IpAddress = "127.0.0.1"
            };
            PictureBytes = new byte[] { 0xFF, 0xD8, 0x01, 0x02, 0xFF, 0xD9 };
        }

        public event EventHandler<AlarmEventData> OnAlarmEvent;

        public DeviceCapabilities Capabilities { get; set; }

        public DeviceInfo DeviceInfo { get; set; }

        public byte[] PictureBytes { get; set; }

        public IReadOnlyList<MockGatewayCall> Calls
        {
            get
            {
                lock (gate)
                {
                    return calls.ToList();
                }
            }
        }

        public void ConfigureResult<T>(string methodName, T result)
        {
            Configure(methodName, new MockGatewayBehavior { ResultFactory = _ => result });
        }

        public void ConfigureResult<T>(string methodName, Func<object, T> resultFactory)
        {
            Configure(methodName, new MockGatewayBehavior { ResultFactory = request => resultFactory(request) });
        }

        public void ConfigureException(string methodName, Exception exception)
        {
            Configure(methodName, new MockGatewayBehavior { Exception = exception });
        }

        public void ConfigureDelay(string methodName, TimeSpan delay)
        {
            Configure(methodName, new MockGatewayBehavior { Delay = delay });
        }

        public void ConfigureTimeout(string methodName)
        {
            Configure(methodName, new MockGatewayBehavior { Timeout = true });
        }

        public void Configure(string methodName, MockGatewayBehavior behavior)
        {
            if (string.IsNullOrWhiteSpace(methodName))
            {
                throw new ArgumentException("Method name is required.", nameof(methodName));
            }

            if (behavior == null)
            {
                throw new ArgumentNullException(nameof(behavior));
            }

            lock (gate)
            {
                behaviors[methodName] = behavior;
            }
        }

        public Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
        {
            HikvisionGatewayValidator.RequireLoginRequest(request);
            return ExecuteAsync("LoginAsync", request, cancellationToken, () =>
            {
                if (request.Password == "wrong")
                {
                    lastErrorCode = 1;
                    throw new DeviceGatewayException("Login", SdkError.FromCode(1));
                }

                var response = new LoginResponse
                {
                    UserId = nextUserId++,
                    DeviceInfo = CloneDeviceInfo(DeviceInfo)
                };
                sessions[response.UserId] = response;
                return response;
            });
        }

        public Task LogoutAsync(LogoutRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            return ExecuteAsync("LogoutAsync", request, cancellationToken, () =>
            {
                sessions.Remove(request.UserId);
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
            return ExecuteAsync("SetAlarmAsync", request, cancellationToken, () =>
            {
                RequireSession(request.UserId);
                var handle = nextAlarmHandle++;
                alarms[handle] = request;
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
            return ExecuteAsync("CloseAlarmAsync", request, cancellationToken, () =>
            {
                if (!alarms.Remove(request.AlarmHandle))
                {
                    lastErrorCode = 17;
                    throw new DeviceGatewayException("CloseAlarm", SdkError.FromCode(17, "报警句柄不存在"));
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
            return ExecuteAsync("AddPersonAsync", request, cancellationToken, () =>
            {
                RequireSession(request.UserId);
                if (persons.ContainsKey(request.Person.EmployeeId))
                {
                    lastErrorCode = 29;
                    throw new DeviceGatewayException("AddPerson", SdkError.FromCode(29, "人员已存在"));
                }

                persons[request.Person.EmployeeId] = ClonePerson(request.Person);
                return 0;
            });
        }

        public Task UpsertPersonAsync(UpsertPersonRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            HikvisionGatewayValidator.RequirePerson(request.Person);
            return ExecuteAsync("UpsertPersonAsync", request, cancellationToken, () =>
            {
                RequireSession(request.UserId);
                persons[request.Person.EmployeeId] = ClonePerson(request.Person);
                return 0;
            });
        }

        public Task DeletePersonAsync(DeletePersonRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            return ExecuteAsync("DeletePersonAsync", request, cancellationToken, () =>
            {
                RequireSession(request.UserId);
                var key = ResolvePersonKey(request.EmployeeId, request.CardNumber);
                if (key != null)
                {
                    persons.Remove(key);
                    faces.Remove(key);
                    permissions.Remove(key);
                }

                return 0;
            });
        }

        public Task ModifyPersonAsync(ModifyPersonRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            HikvisionGatewayValidator.RequirePerson(request.Person);
            return ExecuteAsync("ModifyPersonAsync", request, cancellationToken, () =>
            {
                RequireSession(request.UserId);
                persons[request.Person.EmployeeId] = ClonePerson(request.Person);
                return 0;
            });
        }

        public Task<QueryPersonResponse> QueryPersonAsync(QueryPersonRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            return ExecuteAsync("QueryPersonAsync", request, cancellationToken, () =>
            {
                RequireSession(request.UserId);
                var items = persons.Values
                    .Where(item => Matches(item.EmployeeId, request.EmployeeId) && Matches(item.CardNumber, request.CardNumber))
                    .Select(ClonePerson)
                    .ToList();
                var response = new QueryPersonResponse
                {
                    Exists = items.Count > 0,
                    TotalCount = items.Count,
                    RawResponse = HikvisionGatewayJson.Serialize(items)
                };
                foreach (var item in items)
                {
                    response.Persons.Add(item);
                }

                return response;
            });
        }

        public Task UploadFaceAsync(UploadFaceRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            HikvisionGatewayValidator.RequireFace(request.Face, request.MaxImageBytes);
            return ExecuteAsync("UploadFaceAsync", request, cancellationToken, () =>
            {
                RequireSession(request.UserId);
                faces[request.Face.EmployeeId] = CloneFace(request.Face);
                return 0;
            });
        }

        public Task DeleteFaceAsync(DeleteFaceRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            return ExecuteAsync("DeleteFaceAsync", request, cancellationToken, () =>
            {
                RequireSession(request.UserId);
                var key = ResolvePersonKey(request.EmployeeId, request.CardNumber);
                if (key != null)
                {
                    faces.Remove(key);
                }

                return 0;
            });
        }

        public Task<QueryFaceResponse> QueryFaceAsync(QueryFaceRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            return ExecuteAsync("QueryFaceAsync", request, cancellationToken, () =>
            {
                RequireSession(request.UserId);
                var items = faces.Values
                    .Where(item => Matches(item.EmployeeId, request.EmployeeId) && Matches(item.CardNumber, request.CardNumber))
                    .Select(CloneFace)
                    .ToList();
                var response = new QueryFaceResponse
                {
                    Exists = items.Count > 0,
                    TotalCount = items.Count,
                    RawResponse = HikvisionGatewayJson.Serialize(items)
                };
                foreach (var item in items)
                {
                    response.Faces.Add(item);
                }

                return response;
            });
        }

        public Task SetPermissionAsync(SetPermissionRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            HikvisionGatewayValidator.RequirePermissions(request.Permissions);
            return ExecuteAsync("SetPermissionAsync", request, cancellationToken, () =>
            {
                RequireSession(request.UserId);
                foreach (var permission in request.Permissions)
                {
                    permissions[permission.EmployeeId] = ClonePermission(permission);
                }

                return 0;
            });
        }

        public Task<QueryPermissionResponse> QueryPermissionAsync(QueryPermissionRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            return ExecuteAsync("QueryPermissionAsync", request, cancellationToken, () =>
            {
                RequireSession(request.UserId);
                var items = permissions.Values
                    .Where(item => Matches(item.EmployeeId, request.EmployeeId) && Matches(item.PermissionCode, request.PermissionCode))
                    .Select(ClonePermission)
                    .ToList();
                var response = new QueryPermissionResponse
                {
                    TotalCount = items.Count,
                    RawResponse = HikvisionGatewayJson.Serialize(items)
                };
                foreach (var item in items)
                {
                    response.Permissions.Add(item);
                }

                return response;
            });
        }

        public Task<GateControlResponse> ControlGatewayAsync(GateControlRequest request, CancellationToken cancellationToken = default)
        {
            HikvisionGatewayValidator.RequireGateControl(request);
            return ExecuteAsync("ControlGatewayAsync", request, cancellationToken, () =>
            {
                RequireSession(request.UserId);
                if (request.GateIndex > Math.Max(1, Capabilities.DoorCount))
                {
                    lastErrorCode = 4;
                    throw new DeviceGatewayException("ControlGateway", SdkError.FromCode(4, "门号非法"));
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
            return ExecuteAsync("CapturePictureAsync", request, cancellationToken, () =>
            {
                RequireSession(request.UserId);
                return new CaptureResponse
                {
                    ImageBytes = CopyBytes(PictureBytes),
                    ContentType = "image/jpeg",
                    CapturedAt = DateTime.Now
                };
            });
        }

        public Task<FaceCaptureResult> CaptureFaceAsync(CaptureRequest request, CancellationToken cancellationToken = default)
        {
            HikvisionGatewayValidator.RequireCapture(request);
            return ExecuteAsync("CaptureFaceAsync", request, cancellationToken, () =>
            {
                RequireSession(request.UserId);
                return new FaceCaptureResult
                {
                    ImageBytes = CopyBytes(PictureBytes),
                    ContentType = "image/jpeg",
                    CapturedAt = DateTime.Now,
                    FaceDetected = true,
                    QualityScore = 88
                };
            });
        }

        public Task<EventQueryResponse> QueryEventRecordAsync(EventQueryRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            HikvisionGatewayValidator.RequireDateRange(request.BeginTime, request.EndTime);
            return ExecuteAsync("QueryEventRecordAsync", request, cancellationToken, () =>
            {
                RequireSession(request.UserId);
                return new EventQueryResponse
                {
                    TotalCount = 0,
                    HasMore = false,
                    RawResponse = "[]"
                };
            });
        }

        public Task<IsapiResponse> SendIsapiRequestAsync(IsapiRequest request, CancellationToken cancellationToken = default)
        {
            HikvisionGatewayValidator.RequireIsapiRequest(request);
            return ExecuteAsync("SendIsapiRequestAsync", request, cancellationToken, () =>
            {
                return new IsapiResponse
                {
                    StatusCode = 200,
                    Body = "{}",
                    ContentType = request.ContentType
                };
            });
        }

        public Task<DeviceCapabilities> GetDeviceCapabilitiesAsync(DeviceCapabilitiesRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            return ExecuteAsync("GetDeviceCapabilitiesAsync", request, cancellationToken, () => CloneCapabilities(Capabilities));
        }

        public Task<DeviceInfo> GetDeviceInfoAsync(DeviceInfoRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HikvisionGatewayValidator.RequireUserId(request.UserId);
            return ExecuteAsync("GetDeviceInfoAsync", request, cancellationToken, () => CloneDeviceInfo(DeviceInfo));
        }

        public int GetLastErrorCode()
        {
            return lastErrorCode;
        }

        public string GetErrorMessage(int errorCode)
        {
            return SdkError.GetDefaultMessage(errorCode);
        }

        public void EmitAlarm(AlarmEventData data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            var copied = new AlarmEventData
            {
                AlarmHandle = data.AlarmHandle,
                UserId = data.UserId,
                Command = data.Command,
                EventType = data.EventType,
                DeviceIpAddress = data.DeviceIpAddress,
                DeviceSerialNumber = data.DeviceSerialNumber,
                EmployeeId = data.EmployeeId,
                CardNumber = data.CardNumber,
                DoorIndex = data.DoorIndex,
                Direction = data.Direction,
                Success = data.Success,
                EventTime = data.EventTime,
                RawPayload = CopyBytes(data.RawPayload),
                PictureBytes = CopyBytes(data.PictureBytes),
                RawPayloadSummary = data.RawPayloadSummary
            };
            copied.CurrentEventFlag = data.CurrentEventFlag;
            foreach (var value in data.Values)
            {
                copied.Values[value.Key] = value.Value;
            }

            var handler = OnAlarmEvent;
            if (handler != null)
            {
                handler(this, copied);
            }
        }

        public void Dispose()
        {
            disposed = true;
            lock (gate)
            {
                sessions.Clear();
                alarms.Clear();
            }
        }

        private async Task<T> ExecuteAsync<T>(string methodName, object request, CancellationToken cancellationToken, Func<T> fallback)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            MockGatewayBehavior behavior;
            lock (gate)
            {
                calls.Add(new MockGatewayCall
                {
                    MethodName = methodName,
                    Request = request,
                    CalledAt = DateTime.Now
                });
                behaviors.TryGetValue(methodName, out behavior);
            }

            if (behavior != null)
            {
                await behavior.ApplyAsync(cancellationToken).ConfigureAwait(false);
                return behavior.Resolve(request, fallback);
            }

            return fallback();
        }

        private void RequireSession(int userId)
        {
            if (!sessions.ContainsKey(userId))
            {
                lastErrorCode = 7;
                throw new DeviceGatewayException("MockGateway", SdkError.FromCode(7, "设备未登录或会话不存在"));
            }
        }

        private string ResolvePersonKey(string employeeId, string cardNumber)
        {
            if (!string.IsNullOrWhiteSpace(employeeId))
            {
                return employeeId;
            }

            return persons.Values.FirstOrDefault(item => string.Equals(item.CardNumber, cardNumber, StringComparison.OrdinalIgnoreCase))?.EmployeeId;
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(MockHikvisionGateway));
            }
        }

        private static bool Matches(string value, string expected)
        {
            return string.IsNullOrWhiteSpace(expected) || string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static DeviceInfo CloneDeviceInfo(DeviceInfo source)
        {
            if (source == null)
            {
                return null;
            }

            return new DeviceInfo
            {
                Model = source.Model,
                SerialNumber = source.SerialNumber,
                FirmwareVersion = source.FirmwareVersion,
                DeviceName = source.DeviceName,
                DoorCount = source.DoorCount,
                ChannelCount = source.ChannelCount,
                MacAddress = source.MacAddress,
                IpAddress = source.IpAddress
            };
        }

        private static DeviceCapabilities CloneCapabilities(DeviceCapabilities source)
        {
            if (source == null)
            {
                return null;
            }

            return new DeviceCapabilities
            {
                Known = source.Known,
                SupportsAcs = source.SupportsAcs,
                SupportsAlarm = source.SupportsAlarm,
                SupportsPersonConfig = source.SupportsPersonConfig,
                SupportsFaceConfig = source.SupportsFaceConfig,
                SupportsFaceCapture = source.SupportsFaceCapture,
                SupportsHistoryEventQuery = source.SupportsHistoryEventQuery,
                SupportsIsapi = source.SupportsIsapi,
                SupportsAiop = source.SupportsAiop,
                DoorCount = source.DoorCount,
                ChannelCount = source.ChannelCount,
                Model = source.Model,
                FirmwareVersion = source.FirmwareVersion,
                RawCapabilities = source.RawCapabilities,
                LastCheckedAt = source.LastCheckedAt
            };
        }

        private static PersonInfo ClonePerson(PersonInfo source)
        {
            var result = new PersonInfo
            {
                EmployeeId = source.EmployeeId,
                Name = source.Name,
                CardNumber = source.CardNumber,
                Department = source.Department,
                ValidFrom = source.ValidFrom,
                ValidTo = source.ValidTo,
                Enabled = source.Enabled
            };
            foreach (var item in source.Metadata)
            {
                result.Metadata[item.Key] = item.Value;
            }

            return result;
        }

        private static FaceInfo CloneFace(FaceInfo source)
        {
            return new FaceInfo
            {
                EmployeeId = source.EmployeeId,
                CardNumber = source.CardNumber,
                FaceId = source.FaceId,
                ImageBytes = CopyBytes(HikvisionGatewayValidator.ResolveFaceBytes(source)),
                ImageBase64 = source.ImageBase64,
                ImageFormat = source.ImageFormat,
                QualityScore = source.QualityScore
            };
        }

        private static PermissionInfo ClonePermission(PermissionInfo source)
        {
            var result = new PermissionInfo
            {
                EmployeeId = source.EmployeeId,
                PermissionCode = source.PermissionCode,
                ValidFrom = source.ValidFrom,
                ValidTo = source.ValidTo,
                Enabled = source.Enabled,
                ScheduleTemplate = source.ScheduleTemplate
            };
            foreach (var doorIndex in source.DoorIndexes)
            {
                result.DoorIndexes.Add(doorIndex);
            }

            return result;
        }

        private static byte[] CopyBytes(byte[] source)
        {
            if (source == null)
            {
                return new byte[0];
            }

            var copy = new byte[source.Length];
            Buffer.BlockCopy(source, 0, copy, 0, source.Length);
            return copy;
        }
    }
}
