using System.IO;
using ControlDoor.Configuration;
using ControlDoor.GrpcApi;
using ControlDoor.Observability;

namespace ControlEntradaSalida.Tests
{
    public static class ObservabilityEnhancementTests
    {
        [TestCase]
        public static void AccessControlGrpcService_GetDeviceStatus_WritesFullRequestAndResponsePayload()
        {
            var runDirectory = TestWorkspace.Create();
            var options = FullPayloadOptions(runDirectory);

            using (var logger = new ServiceLogger(options))
            using (var fixture = new Stage4Fixture(logger))
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository, logger: logger, logOptions: options);

                var response = service.GetDeviceStatus(@"{""password"":""secret-for-log"",""includeDisabled"":true}", new GrpcRequestContext { RequestId = "req-grpc-log" });

                var text = File.ReadAllText(logger.CurrentLogPath);
                Assert.Contains(@"""success"":true", response);
                Assert.Contains("component=\"GrpcApi\"", text);
                Assert.Contains("message=\"gRPC request started.\"", text);
                Assert.Contains("message=\"gRPC payload.\"", text);
                Assert.Contains("operationName=\"GetDeviceStatus\"", text);
                Assert.Contains("requestId=\"req-grpc-log\"", text);
                Assert.Contains("direction=\"request\"", text);
                Assert.Contains("direction=\"response\"", text);
                Assert.Contains("secret-for-log", text);
                Assert.Contains("\\\"success\\\":true", text);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_Unauthenticated_WritesBusinessFailure()
        {
            var runDirectory = TestWorkspace.Create();
            var options = FullPayloadOptions(runDirectory);

            using (var logger = new ServiceLogger(options))
            using (var fixture = new Stage4Fixture(logger))
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository, "expected-key", logger, options);

                service.GetDeviceStatus("{}", new GrpcRequestContext { RequestId = "req-grpc-auth" });

                var text = File.ReadAllText(logger.CurrentLogPath);
                Assert.Contains("message=\"gRPC request business failure.\"", text);
                Assert.Contains("errorCode=\"UNAUTHENTICATED\"", text);
                Assert.Contains("operationName=\"GetDeviceStatus\"", text);
            }
        }

        [TestCase]
        public static void PermissionSyncGrpcService_OfflineDevice_WritesRetryIntentLog()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOfflineDevice();

                fixture.Service.SyncPermissions(@"{""items"":[{""employee_id"":""10001"",""permission_code"":7}]}", fixture.Context("req-retry-log"));

                var text = fixture.ReadLog();
                Assert.Contains("component=\"DeviceOperationRetry\"", text);
                Assert.Contains("message=\"Retry intent queued from gRPC.\"", text);
                Assert.Contains("deviceId=\"1\"", text);
                Assert.Contains("employeeId=\"10001\"", text);
                Assert.Contains("operationName=\"SyncPermission\"", text);
            }
        }

        private static LogOptions FullPayloadOptions(string runDirectory)
        {
            return LogOptions.FromSettings(runDirectory, new LoggingOptions
            {
                LogDirectory = "logs",
                EnableGrpcPayloadLogging = true,
                GrpcPayloadLogMode = "Full",
                IncludeCredentialFields = true,
                IncludeFaceImageBase64 = true
            });
        }
    }
}
