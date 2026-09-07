using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Configuration;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Tasks;
using ControlDoor.Devices.Workers;
using ControlDoor.GrpcApi;
using ControlDoor.Hikvision;
using ControlDoor.Observability;

namespace ControlEntradaSalida.Tests
{
    public static class DualLoggingTests
    {
        [TestCase]
        public static void ServiceLogger_DefaultLevels_SeparatesDailyAndDiagnosticContent()
        {
            using (var logger = NewLogger())
            {
                logger.Debug("DeviceWorker", "正常轮询");
                logger.Info("DeviceLifecycle", "设备上线", new LogFields { DeviceId = 7, RequestId = "req-route" });
                logger.Error("Host", "处理失败", new InvalidOperationException("failure-detail"));
                var daily = File.ReadAllText(logger.CurrentLogPath);
                var diagnostic = File.ReadAllText(logger.CurrentDiagnosticLogPath);
                Assert.False(daily.Contains("正常轮询"));
                Assert.Contains("设备=7", daily);
                Assert.Contains("requestId=req-route", daily);
                Assert.Contains("failure-detail", daily);
                Assert.False(daily.Contains("InvalidOperationException"));
                Assert.Contains("正常轮询", diagnostic);
                Assert.Contains("InvalidOperationException", diagnostic);
                Assert.Contains("requestId=\"req-route\"", diagnostic);
            }
        }

        [TestCase]
        public static void ServiceLogger_IndependentThresholds_DoNotFilterOtherOutput()
        {
            var options = NewOptions();
            options.MinimumLevel = LogLevel.Error;
            using (var logger = new ServiceLogger(options))
            {
                logger.Debug("Test", "diagnostic-only");
                Assert.False(File.Exists(logger.CurrentLogPath));
                Assert.Contains("diagnostic-only", File.ReadAllText(logger.CurrentDiagnosticLogPath));
            }
            options = NewOptions();
            options.DiagnosticMinimumLevel = LogLevel.Error;
            using (var logger = new ServiceLogger(options))
            {
                logger.Info("Test", "daily-only");
                Assert.False(File.Exists(logger.CurrentDiagnosticLogPath));
                Assert.Contains("daily-only", File.ReadAllText(logger.CurrentLogPath));
            }
        }

        [TestCase]
        public static void ServiceLogger_ConcurrentScopes_PreserveExplicitFieldsAndRestoreParents()
        {
            using (var logger = NewLogger())
            {
                using (logger.BeginScope(new LogFields { RequestId = "parent", DeviceId = 99 }))
                {
                    Task.WhenAll(Enumerable.Range(1, 8).Select(async index =>
                    {
                        using (logger.BeginScope(new LogFields { RequestId = "req-" + index, DeviceId = index }))
                        {
                            await Task.Yield();
                            logger.Debug("Scope", "child-" + index);
                            logger.Debug("Scope", "explicit-" + index, new LogFields { RequestId = "override-" + index });
                        }
                    })).GetAwaiter().GetResult();
                    logger.Info("Scope", "parent-restored");
                }
                logger.Info("Scope", "outside");
                var lines = File.ReadAllLines(logger.CurrentDiagnosticLogPath);
                foreach (var index in Enumerable.Range(1, 8))
                {
                    var child = lines.Single(line => line.Contains("message=\"child-" + index + "\""));
                    Assert.Contains("requestId=\"req-" + index + "\"", child);
                    Assert.Contains("deviceId=\"" + index + "\"", child);
                    Assert.Contains("requestId=\"override-" + index + "\"", lines.Single(line => line.Contains("message=\"explicit-" + index + "\"")));
                }
                Assert.Contains("requestId=\"parent\"", lines.Single(line => line.Contains("parent-restored")));
                Assert.False(lines.Single(line => line.Contains("message=\"outside\"")).Contains("requestId="));
            }
        }

        [TestCase]
        public static void DeviceWorker_RequestContext_CarriesRequestAndTaskIntoSdkTrace()
        {
            using (var logger = NewLogger())
            {
                var registry = new DeviceRuntimeRegistry(new DeviceRuntimeRegistryOptions { WorkerCount = 1 });
                using (var dispatcher = new DeviceSdkDispatcher(registry, new ControlDoor.Devices.Workers.DeviceSdkDispatcherOptions { WorkerCount = 1, QueueCapacityPerWorker = 10 }, logger))
                {
                    registry.Register(new DeviceRuntimeCreationOptions { DeviceId = 7, DeviceName = "测试设备", IpAddress = "127.0.0.1", Enabled = true });
                    var task = new DeviceSdkTask(7, DeviceTaskType.HealthCheck, "ScopeProbe", async context =>
                    {
                        Assert.Equal("req-worker", context.RequestContext.RequestId);
                        await Task.Yield();
                        new SdkTraceLogger(context.Logger, true).Trace("Probe", null, true, 1);
                        return DeviceTaskResult.FromTask(context.Task, true, "OK", "ok", DeviceConnectionStatus.Online, DateTime.Now, DateTime.Now);
                    }) { RequestId = "req-worker", CorrelationId = "trace-worker", RequiresOnline = false };
                    var result = dispatcher.SubmitAndWaitAsync(task, CancellationToken.None).GetAwaiter().GetResult();
                    Assert.True(result.Success);
                    var line = File.ReadAllLines(logger.CurrentDiagnosticLogPath).Single(value => value.Contains("component=\"SdkTrace\""));
                    Assert.Contains("requestId=\"req-worker\"", line);
                    Assert.Contains("traceId=\"trace-worker\"", line);
                    Assert.Contains("deviceId=\"7\"", line);
                    Assert.Contains("taskId=\"" + task.TaskId + "\"", line);
                }
            }
        }

        [TestCase]
        public static void ServiceLogger_RepeatedWarnings_SummarizesAndResetsOnRecoveryOrCodeChange()
        {
            var now = new DateTime(2026, 9, 6, 12, 0, 0);
            using (var logger = new ServiceLogger(NewOptions(), () => now))
            {
                var fields = new LogFields { DeviceId = 7, OperationName = "Login", ErrorCode = "7" };
                logger.WarnRepeated("SdkTrace", "login-failed", fields);
                logger.WarnRepeated("SdkTrace", "login-failed", fields);
                logger.WarnRepeated("SdkTrace", "login-failed", fields);
                Assert.Equal(1, File.ReadAllLines(logger.CurrentLogPath).Length);
                Assert.Equal(3, File.ReadAllLines(logger.CurrentDiagnosticLogPath).Length);
                now = now.AddSeconds(60);
                logger.Maintain();
                Assert.Contains("重复次数=2", File.ReadAllText(logger.CurrentLogPath));
                fields.ErrorCode = "8";
                logger.WarnRepeated("SdkTrace", "login-changed", fields);
                Assert.Contains("login-changed", File.ReadAllText(logger.CurrentLogPath));
                Assert.True(logger.ClearRepeatedWarnings("SdkTrace", 7, "Login"));
                logger.Info("SdkTrace", "recovered", fields);
                logger.WarnRepeated("SdkTrace", "failed-again", fields);
                Assert.Contains("failed-again", File.ReadAllText(logger.CurrentLogPath));
                logger.Error("Test", "terminal-one");
                logger.Error("Test", "terminal-two");
                Assert.Contains("terminal-two", File.ReadAllText(logger.CurrentLogPath));
            }
        }

        [TestCase]
        public static void PayloadFormatter_DefaultPolicy_RedactsBeforeTruncatingAndHandlesLargeFaces()
        {
            var formatter = new PayloadLogFormatter();
            var options = new LogOptions { MaxPayloadChars = 128 };
            var payload = "{\"password\":\"credential-original\",\"items\":[{\"faceImageBase64\":\"" + new string('A', 2200000) + "\",\"value\":\"" + new string('z', 400) + "\"}]}";
            var result = formatter.Format(payload, options);
            Assert.False(result.Contains("credential-original"));
            Assert.Contains("base64Length=2200000", result);
            Assert.Contains("payloadTruncated originalLength=", result);
            Assert.False(result.Contains("AAAA"));
            var invalid = formatter.Format("{\"password\":\"secret-invalid", options);
            Assert.Contains("reason=", invalid);
            Assert.False(invalid.Contains("secret-invalid"));
            var alias = formatter.Format("{\"face_image\":\"alias-image-original\"}", new LogOptions());
            Assert.Contains("base64Length=20", alias);
            Assert.False(alias.Contains("alias-image-original"));
        }

        [TestCase]
        public static void GrpcCallLogger_QueryAndSync_SeparatesProcessAndSummarizesPending()
        {
            using (var logger = NewLogger())
            {
                var calls = new GrpcCallLogger(logger, null);
                var context = new GrpcRequestContext { RequestId = "req-grpc" };
                calls.ExecuteUnary("Test", "GetDeviceStatus", "{\"password\":\"do-not-log\"}", context,
                    (request, ctx) => "{\"success\":true,\"code\":\"OK\"}");
                Assert.False(File.Exists(logger.CurrentLogPath));
                calls.ExecuteUnary("Test", "SyncPersons", "{}", context,
                    (request, ctx) => "{\"success\":true,\"code\":\"PARTIAL_SUCCESS\",\"total\":3,\"succeeded\":2,\"failed\":0,\"queued\":1}");
                var daily = File.ReadAllText(logger.CurrentLogPath);
                Assert.Contains("[WARN]", daily);
                Assert.Contains("总数=3", daily);
                Assert.Contains("待补偿=1", daily);
                Assert.Contains("统计口径=", daily);
                Assert.False(daily.Contains("接口报文"));
                Assert.False(File.ReadAllText(logger.CurrentDiagnosticLogPath).Contains("do-not-log"));
            }
        }

        [TestCase]
        public static void GrpcCallLogger_DisabledPayload_OmitsPlaceholderAndRecordsFailure()
        {
            var options = NewOptions();
            options.EnableGrpcPayloadLogging = false;
            using (var logger = new ServiceLogger(options))
            {
                var calls = new GrpcCallLogger(logger, null);
                calls.ExecuteUnary("Test", "SyncPersons", "{}", new GrpcRequestContext { RequestId = "req-failed" },
                    (request, ctx) => "{\"success\":false,\"code\":\"FAILED\"}");
                logger.LogPayload("Test", null, "{}");
                var diagnostic = File.ReadAllText(logger.CurrentDiagnosticLogPath);
                Assert.False(diagnostic.Contains("payload="));
                Assert.False(diagnostic.Contains("接口报文"));
                Assert.Contains("[ERROR]", File.ReadAllText(logger.CurrentLogPath));
            }
        }

        [TestCase]
        public static void SdkTraceLogger_Enabled_ReportsSuccessFailureSlowAndProductionConstructor()
        {
            using (var logger = NewLogger())
            {
                var trace = new SdkTraceLogger(logger, true);
                using (var gateway = new HikvisionSdkWrapper(trace))
                {
                    var field = typeof(HikvisionSdkWrapper).GetField("traceLogger", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    Assert.True(ReferenceEquals(trace, field.GetValue(gateway)));
                }
                trace.Trace("Probe", 7, true, 1);
                Assert.False(File.Exists(logger.CurrentLogPath));
                trace.Trace("Probe", 7, false, 2, 17, "denied");
                trace.Trace("Probe", 7, true, 1000);
                new SdkTraceLogger(logger, false).Trace("Disabled", 7, false, 1);
                var diagnostic = File.ReadAllText(logger.CurrentDiagnosticLogPath);
                Assert.Contains("success=\"True\"", diagnostic);
                Assert.Contains("success=\"False\"", diagnostic);
                Assert.Contains("errorCode=\"17\"", diagnostic);
                Assert.False(diagnostic.Contains("Disabled"));
                Assert.Contains("耗时较长", File.ReadAllText(logger.CurrentLogPath));
            }
        }

        [TestCase]
        public static void RollingLogFile_RollsByBytesAndDate_ResumesSequenceAndEnforcesTotalSize()
        {
            var directory = TestWorkspace.Create();
            var now = new DateTime(2026, 9, 6, 23, 59, 59);
            var file = new RollingLogFile(directory, "ControlDoor-", 30, 256, 600);
            file.Initialize(now);
            foreach (var index in Enumerable.Range(0, 8))
            {
                file.Append(now, index + new string('x', 180));
            }
            Assert.Contains("-007.log", file.CurrentPath(now));
            Assert.True(new DirectoryInfo(directory).GetFiles("*.log").Sum(item => item.Length) <= 600);
            var resumed = new RollingLogFile(directory, "ControlDoor-", 30, 256, 600);
            resumed.Initialize(now);
            resumed.Append(now, new string('x', 180));
            Assert.Contains("-008.log", resumed.CurrentPath(now));
            now = now.AddSeconds(2);
            resumed.Append(now, "next-day");
            Assert.Contains("20260907.log", resumed.CurrentPath(now));
            resumed.Append(now, new string('中', 1000));
            Assert.True(new DirectoryInfo(directory).GetFiles("*.log").All(item => item.Length <= 256));
            Assert.Contains("recordTruncated", File.ReadAllText(resumed.CurrentPath(now)));
        }

        [TestCase]
        public static void RollingLogFile_ContinuousCleanup_DeletesOnlyOwnedExpiredFiles()
        {
            var directory = TestWorkspace.Create();
            var now = new DateTime(2026, 9, 6, 12, 0, 0);
            var file = new RollingLogFile(directory, "ControlDoor-", 7, 1024, 10000);
            file.Initialize(now);
            var expired = Path.Combine(directory, "ControlDoor-20260801.log");
            File.WriteAllText(expired, "old");
            var unrelated = Path.Combine(directory, "ControlDoor-notes.log");
            File.WriteAllText(unrelated, "keep");
            var invalidDate = Path.Combine(directory, "ControlDoor-20269999.log");
            File.WriteAllText(invalidDate, "keep");
            file.Append(now.AddSeconds(61), "cleanup-trigger");
            Assert.False(File.Exists(expired));
            Assert.True(File.Exists(unrelated));
            Assert.True(File.Exists(invalidDate));
        }

        [TestCase]
        public static void ServiceLogger_OutputFailure_DoesNotBlockOtherOutputOrRecovery()
        {
            var options = NewOptions();
            Directory.CreateDirectory(options.LogDirectory);
            var diagnosticDirectory = Path.Combine(options.LogDirectory, "diagnostic");
            File.WriteAllText(diagnosticDirectory, "block-directory");
            using (var logger = new ServiceLogger(options))
            {
                logger.Info("Test", "daily-survives");
                Assert.Contains("daily-survives", File.ReadAllText(logger.CurrentLogPath));
                File.Delete(diagnosticDirectory);
                logger.Info("Test", "diagnostic-recovers");
                Assert.Contains("diagnostic-recovers", File.ReadAllText(logger.CurrentDiagnosticLogPath));
                using (File.Open(logger.CurrentLogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    logger.Info("Test", "diagnostic-survives");
                }
                Assert.Contains("diagnostic-survives", File.ReadAllText(logger.CurrentDiagnosticLogPath));
            }
        }

        [TestCase]
        public static void LogOptions_InvalidNewValues_UsesDocumentedDefaults()
        {
            var settings = new AppSettings();
            settings.Logging.DiagnosticMinimumLevel = "999";
            settings.Logging.DiagnosticRetentionDays = 0;
            settings.Logging.MaxFileSizeMB = 0;
            settings.Logging.MaxTotalSizeMB = 0;
            settings.Logging.DiagnosticMaxTotalSizeMB = 0;
            settings.Logging.MaxPayloadChars = 0;
            var validated = new ConfigurationValidator().Validate(settings, TestWorkspace.Create()).Settings.Logging;
            var options = LogOptions.FromSettings(TestWorkspace.Create(), validated);
            Assert.Equal(LogLevel.Debug, options.DiagnosticMinimumLevel);
            Assert.Equal(7, options.DiagnosticRetentionDays);
            Assert.Equal(20, options.MaxFileSizeMB);
            Assert.Equal(512, options.MaxTotalSizeMB);
            Assert.Equal(2048, options.DiagnosticMaxTotalSizeMB);
            Assert.Equal(16384, options.MaxPayloadChars);
            Assert.True(options.EnableGrpcPayloadLogging);
            Assert.False(options.IncludeCredentialFields);
            Assert.False(options.IncludeFaceImageBase64);
        }

        [TestCase]
        public static void GrpcCallLogger_StreamingAndException_RecordsFramesAndFullStack()
        {
            using (var logger = NewLogger())
            {
                var calls = new GrpcCallLogger(logger, null);
                var context = new GrpcRequestContext { RequestId = "req-stream" };
                calls.ExecuteStreaming("Test", "CaptureFaceStream", "{}", context, (request, ctx) => new[]
                {
                    "{\"success\":true,\"code\":\"OK\",\"face_image_base64\":\"image-original\"}"
                });
                var diagnostic = File.ReadAllText(logger.CurrentDiagnosticLogPath);
                Assert.Contains("direction=\"response\"", diagnostic);
                Assert.Contains("frameCount=\"1\"", diagnostic);
                Assert.False(diagnostic.Contains("image-original"));
                try
                {
                    calls.ExecuteUnary("Test", "SyncPersons", "{}", context, (request, ctx) => throw new InvalidOperationException("stack-marker"));
                    throw new Exception("Expected handler failure.");
                }
                catch (InvalidOperationException ex)
                {
                    Assert.Equal("stack-marker", ex.Message);
                }
                Assert.Contains("InvalidOperationException", File.ReadAllText(logger.CurrentDiagnosticLogPath));
                Assert.False(File.ReadAllText(logger.CurrentLogPath).Contains("InvalidOperationException"));
                Assert.Contains("stack-marker", File.ReadAllText(logger.CurrentLogPath));
            }
        }

        [TestCase]
        public static void RollingLogFile_IdleMaintenance_CleansExpiredCurrentDayAfterRollover()
        {
            var directory = TestWorkspace.Create();
            var now = new DateTime(2026, 9, 6);
            var file = new RollingLogFile(directory, "ControlDoor-", 1, 1024, 4096);
            file.Initialize(now);
            file.Append(now, "old-active-file");
            var oldPath = file.CurrentPath(now);
            file.Maintain(now.AddDays(1));
            Assert.False(File.Exists(oldPath));
        }

        private static LogOptions NewOptions() => new LogOptions { LogDirectory = Path.Combine(TestWorkspace.Create(), "logs") };

        private static ServiceLogger NewLogger() => new ServiceLogger(NewOptions());
    }
}
