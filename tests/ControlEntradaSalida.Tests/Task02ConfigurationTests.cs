using System.IO;
using System.Text;
using ControlDoor.Configuration;

namespace ControlEntradaSalida.Tests
{
    public static class Task02ConfigurationTests
    {
        [TestCase]
        public static void ConfigurationLoader_LoadsFixedRuntimeJsonPath()
        {
            var runDirectory = TestWorkspace.Create();
            TestWorkspace.WriteConfig(runDirectory, @"{
  ""Database"": { ""ConnectionString"": ""Server=.;Database=test;Trusted_Connection=True;"" }
}");

            var result = new ConfigurationLoader().Load(runDirectory);

            Assert.True(result.Success);
            Assert.Equal(Path.Combine(runDirectory, "Configuration", "appsettings.json"), result.ConfigPath);
            Assert.Equal(5001, result.Settings.Service.GrpcListenPort);
            Assert.Equal("logs", result.Settings.Logging.LogDirectory);
            Assert.Equal(4, result.Settings.DeviceSdkDispatcher.WorkerCount);
        }

        [TestCase]
        public static void ConfigurationLoader_MissingFile_ReturnsClearError()
        {
            var runDirectory = TestWorkspace.Create();

            var result = new ConfigurationLoader().Load(runDirectory);

            Assert.False(result.Success);
            Assert.Contains("配置文件不存在", result.Errors[0]);
        }

        [TestCase]
        public static void ConfigurationLoader_MissingConnectionString_Fails()
        {
            var runDirectory = TestWorkspace.Create();
            TestWorkspace.WriteConfig(runDirectory, @"{
  ""Service"": { ""GrpcListenPort"": 5001 },
  ""Database"": { ""ConnectionString"": """" }
}");

            var result = new ConfigurationLoader().Load(runDirectory);

            Assert.False(result.Success);
            Assert.Contains("Database.ConnectionString", result.Errors[0]);
        }

        [TestCase]
        public static void ConfigurationLoader_InvalidOptionalValues_FallBackWithWarnings()
        {
            var runDirectory = TestWorkspace.Create();
            TestWorkspace.WriteConfig(runDirectory, @"{
  ""Service"": { ""GrpcListenPort"": 70000 },
  ""Database"": { ""ConnectionString"": ""Server=.;Database=test;"", ""CommandTimeoutSeconds"": 0 },
  ""Logging"": { ""LogDirectory"": """", ""RetentionDays"": 0, ""GrpcPayloadLogMode"": ""Verbose"" },
  ""DeviceSdkDispatcher"": { ""WorkerCount"": 0, ""QueueCapacity"": 10, ""DefaultTaskTimeoutMs"": 100 },
  ""DeviceConnection"": { ""StatusCheckIntervalMs"": 100 },
  ""DeviceOperationRetry"": { ""ScanIntervalSeconds"": 1, ""MaxRetryAttempts"": 0 },
  ""FaceEnrollment"": { ""MaxFaceImageBytes"": 0 }
}");

            var result = new ConfigurationLoader().Load(runDirectory);

            Assert.True(result.Success);
            Assert.Equal(5001, result.Settings.Service.GrpcListenPort);
            Assert.Equal(30, result.Settings.Database.CommandTimeoutSeconds);
            Assert.Equal("logs", result.Settings.Logging.LogDirectory);
            Assert.Equal("Summary", result.Settings.Logging.GrpcPayloadLogMode);
            Assert.Equal(4, result.Settings.DeviceSdkDispatcher.WorkerCount);
            Assert.True(result.Warnings.Count >= 8, "Expected multiple fallback warnings.");
        }

        [TestCase]
        public static void ConfigurationLoader_InvalidJson_Fails()
        {
            var runDirectory = TestWorkspace.Create();
            var configDirectory = Path.Combine(runDirectory, "Configuration");
            Directory.CreateDirectory(configDirectory);
            File.WriteAllText(Path.Combine(configDirectory, "appsettings.json"), "{ invalid json", Encoding.UTF8);

            var result = new ConfigurationLoader().Load(runDirectory);

            Assert.False(result.Success);
            Assert.Contains("JSON", result.Errors[0]);
        }
    }
}
