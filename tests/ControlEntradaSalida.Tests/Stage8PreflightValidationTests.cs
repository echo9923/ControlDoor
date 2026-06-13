using System.IO;
using System.Linq;
using System.Threading;
using ControlDoor.Configuration;
using ControlDoor.Database;
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
    }
}
