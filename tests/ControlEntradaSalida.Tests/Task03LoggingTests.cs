using System;
using System.Collections.Generic;
using System.IO;
using ControlDoor.Configuration;
using ControlDoor.Observability;

namespace ControlEntradaSalida.Tests
{
    public static class Task03LoggingTests
    {
        [TestCase]
        public static void ServiceLogger_CreatesLogDirectoryAndWritesLifecycleFields()
        {
            var runDirectory = TestWorkspace.Create();
            var options = LogOptions.FromSettings(runDirectory, new LoggingOptions { LogDirectory = "logs" });

            using (var logger = new ServiceLogger(options))
            {
                logger.Info("Host", "启动完成", new LogFields { RequestId = "req-1", TraceId = "trace-1", OperationName = "Start" });
                var text = File.ReadAllText(logger.CurrentLogPath);

                Assert.Contains("component=\"Host\"", text);
                Assert.Contains("message=\"启动完成\"", text);
                Assert.Contains("requestId=\"req-1\"", text);
                Assert.Contains("operationName=\"Start\"", text);
            }
        }

        [TestCase]
        public static void ServiceLogger_MinimumLevelInfo_FiltersDebug()
        {
            var runDirectory = TestWorkspace.Create();
            var options = new LogOptions
            {
                LogDirectory = Path.Combine(runDirectory, "logs"),
                MinimumLevel = LogLevel.Info
            };

            using (var logger = new ServiceLogger(options))
            {
                logger.Debug("Database", "数据库只读命令执行成功。", new LogFields { OperationName = "ConnectionTest" });
                logger.Info("Host", "启动完成");

                var text = File.ReadAllText(logger.CurrentLogPath);
                Assert.False(text.Contains("数据库只读命令执行成功。"));
                Assert.Contains("启动完成", text);
            }
        }

        [TestCase]
        public static void ServiceLogger_MinimumLevelDebug_WritesDebug()
        {
            var runDirectory = TestWorkspace.Create();
            var options = new LogOptions
            {
                LogDirectory = Path.Combine(runDirectory, "logs"),
                MinimumLevel = LogLevel.Debug
            };

            using (var logger = new ServiceLogger(options))
            {
                logger.Debug("Database", "数据库只读命令执行成功。", new LogFields { OperationName = "ConnectionTest" });

                var text = File.ReadAllText(logger.CurrentLogPath);
                Assert.Contains("level=Debug", text);
                Assert.Contains("数据库只读命令执行成功。", text);
            }
        }

        [TestCase]
        public static void ServiceLogger_ExtraReservedMessage_WritesSingleMessageField()
        {
            var runDirectory = TestWorkspace.Create();
            var options = LogOptions.FromSettings(runDirectory, new LoggingOptions { LogDirectory = "logs" });

            using (var logger = new ServiceLogger(options))
            {
                logger.Info("HealthCheck", "健康检查完成。", new LogFields
                {
                    Extra =
                    {
                        ["message"] = "配置文件可读取并解析。"
                    }
                });

                var text = File.ReadAllText(logger.CurrentLogPath);
                Assert.Equal(1, CountOccurrences(text, " message="));
                Assert.Contains("extra_message=\"配置文件可读取并解析。\"", text);
            }
        }

        [TestCase]
        public static void ServiceLogger_Error_WritesFullExceptionToString()
        {
            var runDirectory = TestWorkspace.Create();
            var options = LogOptions.FromSettings(runDirectory, new LoggingOptions { LogDirectory = "logs" });

            using (var logger = new ServiceLogger(options))
            {
                var exception = BuildNestedException();

                logger.Error("Host", "服务启动失败", exception);

                var text = File.ReadAllText(logger.CurrentLogPath);
                Assert.Contains("InvalidOperationException", text);
                Assert.Contains("outer failure", text);
                Assert.Contains("ApplicationException", text);
                Assert.Contains("inner failure", text);
                Assert.Contains("BuildNestedException", text);
            }
        }

        [TestCase]
        public static void ServiceLogger_Escape_ReplacesStandaloneCarriageReturnAndLineFeed()
        {
            var runDirectory = TestWorkspace.Create();
            var options = LogOptions.FromSettings(runDirectory, new LoggingOptions { LogDirectory = "logs" });

            using (var logger = new ServiceLogger(options))
            {
                logger.Info("Host", "first\rsecond\nthird\r\nfourth");

                var lines = File.ReadAllLines(logger.CurrentLogPath);
                Assert.Equal(1, lines.Length);
                Assert.Contains("message=\"first second third fourth\"", lines[0]);
            }
        }

        [TestCase]
        public static void ServiceLogger_RemovesExpiredLogFiles()
        {
            var runDirectory = TestWorkspace.Create();
            var logDirectory = Path.Combine(runDirectory, "logs");
            Directory.CreateDirectory(logDirectory);
            var oldLog = Path.Combine(logDirectory, "ControlDoor-20000101.log");
            File.WriteAllText(oldLog, "old");
            File.SetLastWriteTime(oldLog, DateTime.Now.AddDays(-5));

            using (new ServiceLogger(new LogOptions { LogDirectory = logDirectory, RetentionDays = 1 }))
            {
            }

            Assert.False(File.Exists(oldLog));
        }

        [TestCase]
        public static void RequestContext_FromMetadata_PrefersRequestId()
        {
            var context = RequestContext.FromMetadata(new Dictionary<string, string>
            {
                ["x-trace-id"] = "trace-1",
                ["x-request-id"] = "req-1"
            }, "GetDeviceStatus");

            Assert.Equal("req-1", context.RequestId);
            Assert.Equal("trace-1", context.TraceId);
            Assert.Equal("GetDeviceStatus", context.MethodName);
        }

        [TestCase]
        public static void PayloadLogFormatter_WhenDisabled_DoesNotWritePayload()
        {
            var formatter = new PayloadLogFormatter();

            var result = formatter.Format(@"{""password"":""secret""}", new LogOptions { EnableGrpcPayloadLogging = false });

            Assert.Equal("payload=disabled", result);
        }

        [TestCase]
        public static void PayloadLogFormatter_Summary_ReportsKeysAndArrayCounts()
        {
            var formatter = new PayloadLogFormatter();

            var result = formatter.Format(@"{""items"":[{""id"":1},{""id"":2}],""deviceId"":1}", new LogOptions
            {
                EnableGrpcPayloadLogging = true,
                GrpcPayloadLogMode = "Summary"
            });

            Assert.Contains("deviceId", result);
            Assert.Contains("itemsCount=2", result);
        }

        [TestCase]
        public static void PayloadLogFormatter_Full_UsesConfiguredFieldFiltering()
        {
            var formatter = new PayloadLogFormatter();

            var result = formatter.Format(@"{""password"":""secret"",""face_image_base64"":""abcdef""}", new LogOptions
            {
                EnableGrpcPayloadLogging = true,
                GrpcPayloadLogMode = "Full",
                IncludeCredentialFields = false,
                IncludeFaceImageBase64 = false
            });

            Assert.Contains(@"""password"":""***""", result);
            Assert.Contains("base64Length=6", result);
            Assert.False(result.Contains("abcdef"));
        }

        [TestCase]
        public static void PayloadLogFormatter_Full_RedactsCommonCredentialFieldsByDefault()
        {
            var formatter = new PayloadLogFormatter();

            var result = formatter.Format(
                @"{""password"":""p1"",""apiKey"":""a1"",""secret"":""s1"",""token"":""t1"",""accessToken"":""at1"",""refreshToken"":""rt1"",""connectionString"":""cs1"",""credential"":""c1""}",
                new LogOptions
                {
                    EnableGrpcPayloadLogging = true,
                    GrpcPayloadLogMode = "Full"
                });

            Assert.Contains(@"""password"":""***""", result);
            Assert.Contains(@"""apiKey"":""***""", result);
            Assert.Contains(@"""secret"":""***""", result);
            Assert.Contains(@"""token"":""***""", result);
            Assert.Contains(@"""accessToken"":""***""", result);
            Assert.Contains(@"""refreshToken"":""***""", result);
            Assert.Contains(@"""connectionString"":""***""", result);
            Assert.Contains(@"""credential"":""***""", result);
            Assert.False(result.Contains("p1"));
            Assert.False(result.Contains("cs1"));
        }

        [TestCase]
        public static void LogOptions_FromSettings_DisablesCredentialFieldsByDefault()
        {
            var options = LogOptions.FromSettings(TestWorkspace.Create(), new LoggingOptions { LogDirectory = "logs" });

            Assert.False(options.IncludeCredentialFields);
        }

        private static Exception BuildNestedException()
        {
            try
            {
                try
                {
                    throw new ApplicationException("inner failure");
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("outer failure", ex);
                }
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private static int CountOccurrences(string text, string value)
        {
            var count = 0;
            var index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
        }
    }
}
