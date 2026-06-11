using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Hikvision;

namespace ControlEntradaSalida.Tests
{
    public static class Stage3ContractBoundaryTests
    {
        [TestCase]
        public static void GatewayContract_AllAsyncMethodsAcceptCancellationToken()
        {
            var asyncMethods = typeof(IHikvisionGateway)
                .GetMethods()
                .Where(method => method.Name.EndsWith("Async", StringComparison.Ordinal))
                .ToList();

            Assert.True(asyncMethods.Count >= 18);
            foreach (var method in asyncMethods)
            {
                var parameters = method.GetParameters();
                Assert.True(parameters.Length >= 2, method.Name + " should expose request and cancellation token.");
                Assert.Equal(typeof(CancellationToken), parameters[parameters.Length - 1].ParameterType, method.Name + " should end with CancellationToken.");
            }
        }

        [TestCase]
        public static void GatewayContract_AllAsyncMethodsReturnTask()
        {
            foreach (var method in typeof(IHikvisionGateway).GetMethods().Where(item => item.Name.EndsWith("Async", StringComparison.Ordinal)))
            {
                Assert.True(typeof(Task).IsAssignableFrom(method.ReturnType), method.Name + " should return Task.");
            }
        }

        [TestCase]
        public static void GatewayContract_OnlyGatewayExposesAlarmEvent()
        {
            var alarmEvent = typeof(IHikvisionGateway).GetEvent("OnAlarmEvent");

            Assert.NotNull(alarmEvent);
            Assert.Equal(typeof(EventHandler<AlarmEventData>), alarmEvent.EventHandlerType);
        }

        [TestCase]
        public static void PublicDtos_AllHaveParameterlessConstructorsForSerialization()
        {
            var dtoTypes = typeof(LoginRequest).Assembly.GetTypes()
                .Where(type => type.IsPublic &&
                    type.Namespace == "ControlDoor.Hikvision" &&
                    type.IsClass &&
                    !type.IsAbstract &&
                    !typeof(Delegate).IsAssignableFrom(type) &&
                    type != typeof(HikvisionSdkWrapper) &&
                    type != typeof(HikvisionIsapiClient) &&
                    type != typeof(MockHikvisionGateway) &&
                    type != typeof(DeviceGatewayException))
                .ToList();

            foreach (var type in dtoTypes)
            {
                Assert.NotNull(type.GetConstructor(Type.EmptyTypes), type.FullName + " should have a parameterless constructor.");
            }
        }

        [TestCase]
        public static void PublicDtos_NoPublicFieldsAreExposed()
        {
            var dtoTypes = typeof(LoginRequest).Assembly.GetTypes()
                .Where(type => type.IsPublic && type.Namespace == "ControlDoor.Hikvision" && type.IsClass)
                .ToList();

            foreach (var type in dtoTypes)
            {
                var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
                Assert.Equal(0, fields.Length, type.FullName + " should expose properties instead of public fields.");
            }
        }

        [TestCase]
        public static void DefaultRequests_UseSafeTimeoutsAndPaging()
        {
            Assert.Equal(8000, new LoginRequest().Port);
            Assert.Equal(30000, new LoginRequest().TimeoutMilliseconds);
            Assert.Equal(50, new QueryPersonRequest().PageSize);
            Assert.Equal(50, new QueryFaceRequest().PageSize);
            Assert.Equal(50, new QueryPermissionRequest().PageSize);
            Assert.Equal(100, new EventQueryRequest().PageSize);
            Assert.Equal(30000, new IsapiRequest().TimeoutMilliseconds);
        }

        [TestCase]
        public static void DefaultDtos_InitializeMutableCollections()
        {
            Assert.NotNull(new AlarmEventData().Values);
            Assert.NotNull(new PersonInfo().Metadata);
            Assert.NotNull(new SetPermissionRequest().Permissions);
            Assert.NotNull(new PermissionInfo().DoorIndexes);
            Assert.NotNull(new EventQueryRequest().EventTypes);
            Assert.NotNull(new IsapiRequest().Headers);
            Assert.NotNull(new QueryPersonResponse().Persons);
            Assert.NotNull(new QueryFaceResponse().Faces);
            Assert.NotNull(new QueryPermissionResponse().Permissions);
            Assert.NotNull(new EventQueryResponse().Records);
            Assert.NotNull(new IsapiResponse().Headers);
        }

        [TestCase]
        public static void IsapiResponse_StatusCodeRange_DeterminesSuccess()
        {
            Assert.True(new IsapiResponse { StatusCode = 200 }.IsSuccessStatusCode);
            Assert.True(new IsapiResponse { StatusCode = 204 }.IsSuccessStatusCode);
            Assert.False(new IsapiResponse { StatusCode = 199 }.IsSuccessStatusCode);
            Assert.False(new IsapiResponse { StatusCode = 300 }.IsSuccessStatusCode);
            Assert.False(new IsapiResponse { StatusCode = 500 }.IsSuccessStatusCode);
        }

        [TestCase]
        public static void GateControlCommand_ContainsOnlySupportedStage3Commands()
        {
            Assert.True(Enum.IsDefined(typeof(GateControlCommand), GateControlCommand.Open));
            Assert.True(Enum.IsDefined(typeof(GateControlCommand), GateControlCommand.Restore));
            Assert.True(Enum.IsDefined(typeof(GateControlCommand), GateControlCommand.AlwaysClose));
            Assert.Equal(3, Enum.GetValues(typeof(GateControlCommand)).Length);
        }

        [TestCase]
        public static void IsapiMethod_ContainsAllHttpVerbsUsedByStage3()
        {
            Assert.True(Enum.IsDefined(typeof(IsapiMethod), IsapiMethod.Get));
            Assert.True(Enum.IsDefined(typeof(IsapiMethod), IsapiMethod.Post));
            Assert.True(Enum.IsDefined(typeof(IsapiMethod), IsapiMethod.Put));
            Assert.True(Enum.IsDefined(typeof(IsapiMethod), IsapiMethod.Delete));
            Assert.Equal(4, Enum.GetValues(typeof(IsapiMethod)).Length);
        }

        [TestCase]
        public static void AlarmEventData_CopiesDictionaryValuesWithoutNativeTypes()
        {
            var data = new AlarmEventData
            {
                Command = 0x5002,
                EventType = "COMM_ALARM_ACS",
                EmployeeId = "10001",
                EventTime = DateTime.Now,
                RawPayload = new byte[] { 1, 2, 3 }
            };
            data.Values["serialNo"] = "SN1";

            Assert.Equal("SN1", data.Values["serialNo"]);
            Assert.Equal(3, data.RawPayload.Length);
        }

        [TestCase]
        public static void GatewayJson_RoundTripsSimpleRequestDto()
        {
            var request = new LoginRequest
            {
                IpAddress = "192.168.1.10",
                Port = 8000,
                UserName = "admin",
                Password = "12345"
            };

            var json = Stage3TestReflection.Serialize(request);
            var copy = Stage3TestReflection.Deserialize<LoginRequest>(json);

            Assert.Equal(request.IpAddress, copy.IpAddress);
            Assert.Equal(request.Port, copy.Port);
            Assert.Equal(request.UserName, copy.UserName);
            Assert.Equal(request.Password, copy.Password);
        }

        [TestCase]
        public static void CapabilityValidator_AllCapabilitiesPassWhenKnownAndEnabled()
        {
            var caps = new DeviceCapabilities
            {
                Known = true,
                SupportsAcs = true,
                SupportsAlarm = true,
                SupportsPersonConfig = true,
                SupportsFaceConfig = true,
                SupportsFaceCapture = true,
                SupportsHistoryEventQuery = true,
                SupportsIsapi = true,
                SupportsAiop = true
            };

            DeviceCapabilityValidator.ValidateCapabilities(caps, Enum.GetValues(typeof(DeviceCapability)).Cast<DeviceCapability>());
        }

        [TestCase]
        public static void Validator_IsapiRequestRejectsUnsupportedMethodValue()
        {
            Stage3TestReflection.Expect<ArgumentOutOfRangeException>(() => HikvisionGatewayValidator.RequireIsapiRequest(new IsapiRequest
            {
                Path = "/ISAPI/System/deviceInfo",
                Method = (IsapiMethod)999
            }));
        }

        [TestCase]
        public static void Validator_GateControlRejectsUnsupportedCommandValue()
        {
            Stage3TestReflection.Expect<ArgumentOutOfRangeException>(() => HikvisionGatewayValidator.RequireGateControl(new GateControlRequest
            {
                UserId = 1,
                GateIndex = 1,
                Command = (GateControlCommand)999
            }));
        }

        [TestCase]
        public static void Validator_PermissionsRejectsMissingPermissionCode()
        {
            var permission = new PermissionInfo { EmployeeId = "10001" };
            permission.DoorIndexes.Add(1);

            Stage3TestReflection.Expect<ArgumentException>(() => HikvisionGatewayValidator.RequirePermissions(new[] { permission }));
        }
    }
}
