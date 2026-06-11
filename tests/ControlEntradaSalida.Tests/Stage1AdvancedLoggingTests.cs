using System.Collections.Generic;
using System.IO;
using ControlDoor.Configuration;
using ControlDoor.Observability;

namespace ControlEntradaSalida.Tests
{
    public static class Stage1AdvancedLoggingTests
    {
        [TestCase]
        public static void LogOptions_FromSettings_ResolvesRelativeDirectoryUnderRunDirectory()
        {
            var runDirectory = TestWorkspace.Create();
            var options = LogOptions.FromSettings(runDirectory, new LoggingOptions { LogDirectory = "logs\\stage1" });

            Assert.Equal(Path.Combine(runDirectory, "logs\\stage1"), options.LogDirectory);
        }

        [TestCase]
        public static void LogOptions_FromSettings_KeepsAbsoluteDirectory()
        {
            var absoluteDirectory = Path.Combine(TestWorkspace.Create(), "absolute-logs");
            var options = LogOptions.FromSettings(TestWorkspace.Create(), new LoggingOptions { LogDirectory = absoluteDirectory });

            Assert.Equal(absoluteDirectory, options.LogDirectory);
        }

        [TestCase]
        public static void RequestContext_FromMetadata_FallsBackToGeneratedIds()
        {
            var context = RequestContext.FromMetadata(new Dictionary<string, string>(), "SyncPersons");

            Assert.True(context.RequestId.StartsWith("req-"));
            Assert.Equal(context.RequestId, context.TraceId);
            Assert.Equal("SyncPersons", context.MethodName);
        }

        [TestCase]
        public static void RequestContext_Background_UsesBgPrefix()
        {
            var context = RequestContext.Background("RetryScanner");

            Assert.True(context.RequestId.StartsWith("bg-"));
            Assert.Equal("RetryScanner", context.MethodName);
            Assert.Equal("background", context.Source);
        }

        [TestCase]
        public static void PayloadLogFormatter_FullKeepsCredentialWhenConfigured()
        {
            var formatter = new PayloadLogFormatter();

            var result = formatter.Format(@"{""password"":""secret"",""items"":[{""apiKey"":""k1""}]}", new LogOptions
            {
                EnableGrpcPayloadLogging = true,
                GrpcPayloadLogMode = "Full",
                IncludeCredentialFields = true
            });

            Assert.Contains(@"""password"":""secret""", result);
            Assert.Contains(@"""apiKey"":""k1""", result);
        }

        [TestCase]
        public static void PayloadLogFormatter_FullFiltersNestedCredentialAndFaceImage()
        {
            var formatter = new PayloadLogFormatter();

            var result = formatter.Format(@"{""people"":[{""password"":""nested-secret"",""faceImageBase64"":""abcdefghi""}]}", new LogOptions
            {
                EnableGrpcPayloadLogging = true,
                GrpcPayloadLogMode = "Full",
                IncludeCredentialFields = false,
                IncludeFaceImageBase64 = false
            });

            Assert.False(result.Contains("nested-secret"));
            Assert.False(result.Contains("abcdefghi"));
            Assert.Contains(@"""password"":""***""", result);
            Assert.Contains("base64Length=9", result);
        }

        [TestCase]
        public static void ServiceLogger_LogPayload_WritesRequestContextFields()
        {
            var runDirectory = TestWorkspace.Create();
            var options = new LogOptions
            {
                LogDirectory = Path.Combine(runDirectory, "logs"),
                EnableGrpcPayloadLogging = true,
                GrpcPayloadLogMode = "Summary"
            };

            using (var logger = new ServiceLogger(options))
            {
                logger.LogPayload("GrpcApi", new RequestContext("req-123", "trace-456", "GetDeviceStatus"), @"{""deviceIds"":[1,2]}");
                var text = File.ReadAllText(logger.CurrentLogPath);

                Assert.Contains("requestId=\"req-123\"", text);
                Assert.Contains("traceId=\"trace-456\"", text);
                Assert.Contains("operationName=\"GetDeviceStatus\"", text);
                Assert.Contains("deviceIdsCount=2", text);
            }
        }
    }
}
