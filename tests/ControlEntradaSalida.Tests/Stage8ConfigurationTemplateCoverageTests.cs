using System;
using System.IO;
using System.Text;
using ControlDoor.Configuration;

namespace ControlEntradaSalida.Tests
{
    public static class Stage8ConfigurationTemplateCoverageTests
    {
        [TestCase]
        public static void Stage8ConfigurationTemplate_RepositoryTemplate_LoadsWithEveryRequiredGroup()
        {
            var runDirectory = CopyRepositoryTemplateToRunDirectory();

            var result = new ConfigurationLoader().Load(runDirectory);

            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.NotNull(result.Settings.Service);
            Assert.NotNull(result.Settings.Database);
            Assert.NotNull(result.Settings.Logging);
            Assert.NotNull(result.Settings.DeviceSdkDispatcher);
            Assert.NotNull(result.Settings.DeviceConnection);
            Assert.NotNull(result.Settings.HikvisionSdk);
            Assert.NotNull(result.Settings.DeviceOperationRetry);
            Assert.NotNull(result.Settings.FaceEventLogging);
            Assert.NotNull(result.Settings.FaceEnrollment);
            Assert.NotNull(result.Settings.CameraAlarmDoorInterlock);
            Assert.Equal(5001, result.Settings.Service.GrpcListenPort);
            Assert.Equal("logs", result.Settings.Logging.LogDirectory);
            Assert.Equal("Summary", result.Settings.Logging.GrpcPayloadLogMode);
            Assert.Equal("sdk\\Hikvision", result.Settings.HikvisionSdk.DllDirectory);
            Assert.Equal("snapshots", result.Settings.FaceEventLogging.SnapshotRootDirectory);
            Assert.False(result.Settings.CameraAlarmDoorInterlock.Enabled);
            Assert.Equal("Configuration\\devices.json", result.Settings.Devices.FilePath);
        }

        [TestCase]
        public static void Stage8ConfigurationTemplate_TemplateUsesStage8GroupNames_NotRuntimeInternalAliases()
        {
            var template = ReadRepositoryTemplate();

            Assert.Contains("\"DeviceRuntime\"", template);
            Assert.Contains("\"DeviceLifecycle\"", template);
            Assert.Contains("\"Devices\"", template);
            Assert.Contains("\"FilePath\"", template);
            Assert.False(template.Contains("\"DeviceSdkDispatcher\""));
            Assert.False(template.Contains("\"DeviceConnection\""));
            Assert.False(template.Contains("\"Source\""));
            Assert.False(template.Contains("Source=" + "Database"));
        }

        [TestCase]
        public static void Stage8ConfigurationTemplate_CommentStripper_PreservesCommentTokensInsideStrings()
        {
            var runDirectory = TestWorkspace.Create();
            TestWorkspace.WriteConfig(runDirectory, @"{
  // top-level comment
  ""Service"": { ""GrpcListenPort"": 5001 },
  ""Database"": {
    ""ConnectionString"": ""Server=tcp://127.0.0.1;Database=door;Password=abc//def/*literal*/ghi;""
  },
  ""Logging"": { ""LogDirectory"": ""logs"" },
  ""DeviceRuntime"": { ""WorkerCount"": 4, ""QueueCapacity"": 1000, ""DefaultTaskTimeoutMs"": 30000 },
  ""HikvisionSdk"": { ""DllDirectory"": ""sdk\\Hikvision"", ""SdkLogDirectory"": ""logs\\sdk"" },
  ""DeviceLifecycle"": { ""StatusCheckIntervalMs"": 30000, ""LoginTimeoutMs"": 15000 },
  ""DeviceOperationRetry"": { ""ScanIntervalSeconds"": 30, ""MaxRetryAttempts"": 10 },
  ""FaceEventLogging"": { ""SnapshotRootDirectory"": ""snapshots"" },
  ""FaceEnrollment"": { ""MaxFaceImageBytes"": 204800 },
  ""CameraAlarmDoorInterlock"": { ""Enabled"": false, ""Mappings"": [] },
  ""Devices"": { ""FilePath"": ""Configuration\\devices.json"", ""Items"": [] }
}");

            var result = new ConfigurationLoader().Load(runDirectory);

            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.Contains("tcp://127.0.0.1", result.Settings.Database.ConnectionString);
            Assert.Contains("abc//def/*literal*/ghi", result.Settings.Database.ConnectionString);
        }

        [TestCase]
        public static void Stage8ConfigurationTemplate_DoesNotContainObviousLiveSecretValues()
        {
            var template = ReadRepositoryTemplate();
            var lower = template.ToLowerInvariant();

            Assert.Contains("change_me", lower);
            Assert.False(lower.Contains("password=123456"));
            Assert.False(lower.Contains("password=admin"));
            Assert.False(lower.Contains("grpcmanagementapikey\": \"secret"));
            Assert.False(lower.Contains("grpcmanagementapikey\": \"prod"));
        }

        private static string CopyRepositoryTemplateToRunDirectory()
        {
            var runDirectory = TestWorkspace.Create();
            var configurationDirectory = Path.Combine(runDirectory, "Configuration");
            Directory.CreateDirectory(configurationDirectory);
            File.Copy(RepositoryTemplatePath(), Path.Combine(configurationDirectory, "appsettings.json"));
            return runDirectory;
        }

        private static string ReadRepositoryTemplate()
        {
            return File.ReadAllText(RepositoryTemplatePath(), Encoding.UTF8);
        }

        private static string RepositoryTemplatePath()
        {
            return Path.Combine("src", "ControlDoor", "Configuration", "appsettings.json");
        }
    }
}
