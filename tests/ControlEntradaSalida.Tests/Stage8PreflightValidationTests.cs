using System.IO;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using ControlDoor.Configuration;
using ControlDoor.Database;
using ControlDoor.Observability;
using ControlDoor.Runtime.Health;
using ControlDoor.Runtime.Health.Checks;

namespace ControlEntradaSalida.Tests
{
    public static class Stage8PreflightValidationTests
    {
        [TestCase]
        public static void DllPresenceHealthCheck_RequiredMissingSdk_Fails()
        {
            var check = new DllPresenceHealthCheck("Hikvision SDK DLL", required: true, "sdk\\Hikvision\\HCNetSDK.dll");

            var result = check.Run(NewContext(TestWorkspace.Create()));

            Assert.Equal(HealthCheckStatus.Failed, result.Status);
            Assert.True(result.BlocksStartup);
        }

        [TestCase]
        public static void HealthCheckService_CreateStage8_UsesStrictRuntimeChecks()
        {
            var runDirectory = TestWorkspace.Create();
            Directory.CreateDirectory(Path.Combine(runDirectory, "Configuration"));
            File.WriteAllText(Path.Combine(runDirectory, "Configuration", "appsettings.json"), "{}");
            var settings = NewSettings();
            var database = new RecordingDatabaseClient();

            var summary = HealthCheckService
                .CreateStage8(runDirectory, settings, database)
                .Run(new HealthCheckContext(runDirectory, settings, null, CancellationToken.None));

            Assert.False(summary.Success);
            Assert.True(summary.Results.Any(item => item.Name == "Hikvision SDK DLL" && item.Status == HealthCheckStatus.Failed));
            Assert.True(summary.Results.Any(item => item.Name == "SqlServerTypes DLL" && item.Status == HealthCheckStatus.Failed));
        }

        [TestCase]
        public static void DeviceStoreHealthCheck_ValidJsonDeviceList_IsOk()
        {
            var runDirectory = TestWorkspace.Create();
            Directory.CreateDirectory(Path.Combine(runDirectory, "Configuration"));
            File.WriteAllText(
                Path.Combine(runDirectory, "Configuration", "devices.json"),
                @"{""devices"":[{""deviceId"":1,""name"":""门禁"",""types"":[""Acs""],""ipAddress"":""10.0.0.1"",""port"":8000,""password"":""pw"",""enabled"":true}]}");
            var settings = NewSettings();
            settings.Devices = new DeviceStoreOptions { FilePath = "Configuration\\devices.json" };

            var result = new DeviceStoreHealthCheck().Run(new HealthCheckContext(runDirectory, settings, null, CancellationToken.None));

            Assert.Equal(HealthCheckStatus.OK, result.Status);
        }

        [TestCase]
        public static void DeviceStoreHealthCheck_MissingJsonDeviceList_Fails()
        {
            var runDirectory = TestWorkspace.Create();
            var settings = NewSettings();
            settings.Devices = new DeviceStoreOptions { FilePath = "Configuration\\devices.json" };

            var result = new DeviceStoreHealthCheck().Run(new HealthCheckContext(runDirectory, settings, null, CancellationToken.None));

            Assert.Equal(HealthCheckStatus.Failed, result.Status);
            Assert.Contains("设备清单文件不存在", result.Message);
        }

        [TestCase]
        public static void DeviceStoreHealthCheck_InlineItems_IsOkAndDoesNotWarnForDatabaseSource()
        {
            var runDirectory = TestWorkspace.Create();
            var settings = NewSettings();
            settings.Devices = new DeviceStoreOptions
            {
                FilePath = "Configuration\\devices.json",
                Items = new List<DeviceStoreItem>
                {
                    new DeviceStoreItem
                    {
                        DeviceId = 1,
                        Name = "内联门禁",
                        IpAddress = "10.0.0.1",
                        Port = 8000,
                        Password = "pw",
                        Enabled = true,
                        Types = new List<string> { "Acs" }
                    }
                }
            };

            var result = new DeviceStoreHealthCheck().Run(new HealthCheckContext(runDirectory, settings, null, CancellationToken.None));

            Assert.Equal(HealthCheckStatus.OK, result.Status);
            Assert.False(result.Message.Contains("Database"));
            Assert.False(result.Message.Contains("数据库"));
        }

        [TestCase]
        public static void DeviceStoreHealthCheck_InvalidJsonDeviceType_Fails()
        {
            var runDirectory = TestWorkspace.Create();
            Directory.CreateDirectory(Path.Combine(runDirectory, "Configuration"));
            File.WriteAllText(
                Path.Combine(runDirectory, "Configuration", "devices.json"),
                @"{""devices"":[{""deviceId"":1,""name"":""门禁"",""types"":[""1""],""ipAddress"":""10.0.0.1"",""port"":8000,""password"":""pw"",""enabled"":true}]}");
            var settings = NewSettings();
            settings.Devices = new DeviceStoreOptions { FilePath = "Configuration\\devices.json" };

            var result = new DeviceStoreHealthCheck().Run(new HealthCheckContext(runDirectory, settings, null, CancellationToken.None));

            Assert.Equal(HealthCheckStatus.Failed, result.Status);
            Assert.Contains("types 含非法值", result.Message);
        }

        [TestCase]
        public static void HealthCheckService_Run_WritesDetailWithoutDuplicateMessageField()
        {
            var runDirectory = TestWorkspace.Create();
            Directory.CreateDirectory(Path.Combine(runDirectory, "Configuration"));
            File.WriteAllText(Path.Combine(runDirectory, "Configuration", "appsettings.json"), "{}");
            var settings = NewSettings();
            using (var logger = new ServiceLogger(LogOptions.FromSettings(runDirectory, settings.Logging)))
            {
                var service = new HealthCheckService(new IHealthCheck[] { new ConfigurationFileHealthCheck() });

                service.Run(new HealthCheckContext(runDirectory, settings, logger, CancellationToken.None));

                var text = File.ReadAllText(logger.CurrentLogPath);
                Assert.Equal(1, CountOccurrences(text, "message="));
                Assert.Contains("detail=", text);
            }
        }

        [TestCase]
        public static void HealthCheckService_Run_SlowCheckWritesWarnWithThreshold()
        {
            var runDirectory = TestWorkspace.Create();
            var settings = NewSettings();
            settings.Logging.SlowOperationThresholdMs = 1;
            using (var logger = new ServiceLogger(LogOptions.FromSettings(runDirectory, settings.Logging)))
            {
                var service = new HealthCheckService(new IHealthCheck[] { new SlowHealthCheck() });

                service.Run(new HealthCheckContext(runDirectory, settings, logger, CancellationToken.None));

                var text = File.ReadAllText(logger.CurrentLogPath);
                Assert.Contains("level=Warn", text);
                Assert.Contains("thresholdMs=\"1\"", text);
                Assert.Contains("detail=", text);
            }
        }

        [TestCase]
        public static void DatabaseHealthCheckItem_ConnectionFailure_StopsAfterConnectionTest()
        {
            var database = new RecordingDatabaseClient
            {
                FailOperationName = "ConnectionTest",
                FailSqlErrorNumber = 18456
            };
            var check = new DatabaseHealthCheckItem(database);

            var result = check.Run(NewContext(TestWorkspace.Create()));

            Assert.Equal(HealthCheckStatus.Failed, result.Status);
            Assert.Equal(1, database.Commands.Count);
            Assert.Equal("ConnectionTest", database.Commands[0].OperationName);
            Assert.Contains("18456", result.Message);
        }

        [TestCase]
        public static void ConfigurationValidator_HikvisionSdkOptions_NormalizeDefaults()
        {
            var settings = NewSettings();
            settings.HikvisionSdk.Platform = "arm64";
            settings.HikvisionSdk.DllDirectory = "";
            settings.HikvisionSdk.SdkLogDirectory = "";

            var result = new ConfigurationValidator().Validate(settings);

            Assert.True(result.Success);
            Assert.Equal("x64", result.Settings.HikvisionSdk.Platform);
            Assert.Equal("sdk\\Hikvision", result.Settings.HikvisionSdk.DllDirectory);
            Assert.Equal("logs\\sdk", result.Settings.HikvisionSdk.SdkLogDirectory);
            Assert.True(result.Warnings.Count >= 3);
        }

        private static HealthCheckContext NewContext(string runDirectory)
        {
            return new HealthCheckContext(runDirectory, NewSettings(), null, CancellationToken.None);
        }

        private static AppSettings NewSettings()
        {
            return new AppSettings
            {
                Database = new DatabaseOptions { ConnectionString = "Server=.;Database=test;" },
                Logging = new LoggingOptions { LogDirectory = "logs" },
                FaceEventLogging = new FaceEventLoggingOptions { SnapshotRootDirectory = "snapshots", Enabled = true },
                HikvisionSdk = new HikvisionSdkOptions { DllDirectory = "sdk\\Hikvision", SdkLogDirectory = "logs\\sdk" }
            };
        }

        private static int CountOccurrences(string text, string value)
        {
            var count = 0;
            var index = 0;
            while ((index = text.IndexOf(value, index, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
        }

        private sealed class SlowHealthCheck : IHealthCheck
        {
            public string Name => "SlowHealthCheck";

            public HealthCheckResult Run(HealthCheckContext context)
            {
                Thread.Sleep(5);
                return HealthCheckResult.Ok(Name, "slow ok");
            }
        }
    }
}
