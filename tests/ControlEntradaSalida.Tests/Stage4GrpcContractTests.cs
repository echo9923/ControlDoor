using System.Linq;
using System.Web.Script.Serialization;
using ControlDoor.GrpcApi;

namespace ControlEntradaSalida.Tests
{
    public static class Stage4GrpcContractTests
    {
        [TestCase]
        public static void AccessControlGrpcService_MethodFullNames_MatchContract()
        {
            using (var fixture = new Stage4Fixture())
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                Assert.True(service.MethodFullNames.Contains(AccessControlGrpcService.GetDeviceStatusFullName));
                Assert.True(service.MethodFullNames.Contains(AccessControlGrpcService.AddDeviceFullName));
                Assert.True(service.MethodFullNames.Contains(AccessControlGrpcService.DeleteDeviceFullName));
                Assert.True(service.MethodFullNames.Contains(AccessControlGrpcService.DisconnectDeviceFullName));
                Assert.True(service.MethodFullNames.Contains(AccessControlGrpcService.ReconnectDeviceFullName));
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_GetDeviceStatus_ReturnsUnifiedDeviceFields()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.GetDeviceStatus("{}", new GrpcRequestContext { RequestId = "req-status" }));

                Assert.Equal(true, response["success"]);
                Assert.Equal("OK", response["code"]);
                Assert.True(response.ContainsKey("requestId"));
                Assert.True(response.ContainsKey("devices"));
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_GetDeviceStatus_IncludeDisabledControlsDatabaseOnlyDevices()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord(1, "10.0.4.1", enabled: true);
                fixture.AddRecord(2, "10.0.4.2", enabled: false);
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var included = Deserialize(service.GetDeviceStatus(@"{""includeDisabled"":true}", new GrpcRequestContext { RequestId = "req-disabled-1" }));
                var filtered = Deserialize(service.GetDeviceStatus(@"{""includeDisabled"":false}", new GrpcRequestContext { RequestId = "req-disabled-2" }));

                var includedDevices = (System.Collections.ICollection)included["devices"];
                var filteredDevices = (System.Collections.ICollection)filtered["devices"];
                Assert.Equal(2, includedDevices.Count);
                Assert.Equal(1, filteredDevices.Count);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_GetDeviceStatus_MissingDeviceReturnsNotFound()
        {
            using (var fixture = new Stage4Fixture())
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.GetDeviceStatus(@"{""deviceId"":77}", new GrpcRequestContext { RequestId = "req-missing" }));

                Assert.Equal(false, response["success"]);
                Assert.Equal("NOT_FOUND", response["code"]);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_AddDevice_AcceptsAliasesAndDefaults()
        {
            using (var fixture = new Stage4Fixture())
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.AddDevice(@"{""device_id"":9,""device_name"":""北门"",""ip_address"":""10.0.4.9"",""password"":""12345"",""connectNow"":false}", new GrpcRequestContext { RequestId = "req-add" }));

                Assert.Equal(true, response["success"]);
                Assert.Equal("OK", response["code"]);
                Assert.True(fixture.Registry.TryGetByDeviceId(9).Found);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_ApiKey_RejectsInvalidMetadata()
        {
            using (var fixture = new Stage4Fixture())
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository, "secret");

                var response = Deserialize(service.GetDeviceStatus("{}", new GrpcRequestContext { RequestId = "req-auth" }));

                Assert.Equal(false, response["success"]);
                Assert.Equal("UNAUTHENTICATED", response["code"]);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_DeleteDevice_IsIdempotentSuccess()
        {
            using (var fixture = new Stage4Fixture())
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.DeleteDevice(@"{""deviceId"":99}", new GrpcRequestContext { RequestId = "req-delete" }));

                Assert.Equal(true, response["success"]);
                Assert.Equal("OK", response["code"]);
                Assert.Equal(true, response["deleted"]);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_ReconnectMissingDevice_ReturnsNotFound()
        {
            using (var fixture = new Stage4Fixture())
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.ReconnectDevice(@"{""device_id"":99}", new GrpcRequestContext { RequestId = "req-reconnect" }));

                Assert.Equal(false, response["success"]);
                Assert.Equal("NOT_FOUND", response["code"]);
            }
        }

        private static System.Collections.Generic.Dictionary<string, object> Deserialize(string json)
        {
            return new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string, object>>(json);
        }
    }
}
