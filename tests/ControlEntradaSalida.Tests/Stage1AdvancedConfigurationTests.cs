using System;
using System.IO;
using ControlDoor.Configuration;

namespace ControlEntradaSalida.Tests
{
    public static class Stage1AdvancedConfigurationTests
    {
        [TestCase]
        public static void ConfigurationLoader_DoesNotReadEnvironmentVariables()
        {
            var runDirectory = TestWorkspace.Create();
            var previous = Environment.GetEnvironmentVariable("Database__ConnectionString");
            Environment.SetEnvironmentVariable("Database__ConnectionString", "Server=from-env;Database=wrong;");
            try
            {
                TestWorkspace.WriteConfig(runDirectory, @"{
  ""Database"": { ""ConnectionString"": ""Server=from-json;Database=expected;"" }
}");

                var result = new ConfigurationLoader().Load(runDirectory);

                Assert.True(result.Success);
                Assert.Equal("Server=from-json;Database=expected;", result.Settings.Database.ConnectionString);
            }
            finally
            {
                Environment.SetEnvironmentVariable("Database__ConnectionString", previous);
            }
        }

        [TestCase]
        public static void ConfigurationLoader_DoesNotWalkParentDirectories()
        {
            var parentRunDirectory = TestWorkspace.Create();
            TestWorkspace.WriteConfig(parentRunDirectory, @"{
  ""Database"": { ""ConnectionString"": ""Server=parent;Database=wrong;"" }
}");
            var childRunDirectory = Path.Combine(parentRunDirectory, "child");
            Directory.CreateDirectory(childRunDirectory);

            var result = new ConfigurationLoader().Load(childRunDirectory);

            Assert.False(result.Success);
            Assert.Contains(Path.Combine(childRunDirectory, "Configuration", "appsettings.json"), result.ConfigPath);
        }

        [TestCase]
        public static void ConfigurationValidator_PreservesSelfUseLoggingDefaults()
        {
            var settings = new AppSettings();
            settings.Database.ConnectionString = "Server=.;Database=test;";
            settings.Logging.IncludeCredentialFields = true;
            settings.Logging.IncludeFaceImageBase64 = false;

            var result = new ConfigurationValidator().Validate(settings);

            Assert.True(result.Success);
            Assert.True(result.Settings.Logging.IncludeCredentialFields);
            Assert.False(result.Settings.Logging.IncludeFaceImageBase64);
        }

        [TestCase]
        public static void ConfigurationValidator_NormalizesFullPayloadMode()
        {
            var settings = new AppSettings();
            settings.Database.ConnectionString = "Server=.;Database=test;";
            settings.Logging.GrpcPayloadLogMode = "full";

            var result = new ConfigurationValidator().Validate(settings);

            Assert.True(result.Success);
            Assert.Equal("Full", result.Settings.Logging.GrpcPayloadLogMode);
        }

        [TestCase]
        public static void ConfigurationValidator_FillsAllOptionalGroupsWhenMissing()
        {
            var settings = new AppSettings
            {
                Service = null,
                Logging = null,
                DeviceSdkDispatcher = null,
                DeviceConnection = null,
                DeviceOperationRetry = null,
                FaceEventLogging = null,
                FaceEnrollment = null,
                CameraAlarmDoorInterlock = null,
                Database = new DatabaseOptions { ConnectionString = "Server=.;Database=test;" }
            };

            var result = new ConfigurationValidator().Validate(settings);

            Assert.True(result.Success);
            Assert.NotNull(result.Settings.Service);
            Assert.NotNull(result.Settings.Logging);
            Assert.NotNull(result.Settings.DeviceSdkDispatcher);
            Assert.NotNull(result.Settings.FaceEventLogging);
            Assert.False(result.Settings.CameraAlarmDoorInterlock.Enabled);
        }
    }
}
