using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
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
            Assert.NotNull(result.Settings.CameraAlarmDoorInterlock);
            Assert.Equal(5001, result.Settings.Service.GrpcListenPort);
            Assert.Equal(@"D:\ControlDoorData\logs", result.Settings.Logging.LogDirectory);
            Assert.Equal("sdk\\Hikvision", result.Settings.HikvisionSdk.DllDirectory);
            Assert.Equal(@"D:\ControlDoorData\logs\sdk", result.Settings.HikvisionSdk.SdkLogDirectory);
            Assert.Equal(@"D:\ControlDoorData\snapshots", result.Settings.FaceEventLogging.SnapshotRootDirectory);
            Assert.True(result.Settings.CameraAlarmDoorInterlock.Enabled);
            Assert.Equal(28, result.Settings.Devices.DefaultFaceCaptureDeviceId);
            Assert.Equal("Configuration\\devices.json", result.Settings.Devices.FilePath);
            AssertRetryDefaults(result.Settings.DeviceOperationRetry);
        }

        [TestCase]
        public static void Stage8ConfigurationTemplate_FieldInventory_ContainsConfiguredDevicesAndInterlockMappings()
        {
            var runDirectory = CopyRepositoryTemplateToRunDirectory();
            var result = new ConfigurationLoader().Load(runDirectory);
            var devices = LoadRepositoryDevices();

            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.Equal(28, devices.Count);
            Assert.True(devices.Select(item => item.deviceId).OrderBy(item => item).SequenceEqual(Enumerable.Range(1, 28)));
            Assert.Equal(28, devices.Select(item => item.ipAddress).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(24, devices.Count(item => item.types.Contains("Acs")));
            Assert.Equal(3, devices.Count(item => item.types.Contains("Camera")));
            Assert.Equal(1, devices.Count(item => item.types.Contains("FaceCapture")));
            foreach (var enabled in devices.Where(item => item.enabled))
            {
                Assert.False(string.IsNullOrWhiteSpace(enabled.name));
                Assert.False(string.IsNullOrWhiteSpace(enabled.ipAddress));
                Assert.False(string.IsNullOrWhiteSpace(enabled.password));
                Assert.True(enabled.types != null && enabled.types.Count > 0);
            }

            var defaultCapture = devices.Single(item => item.deviceId == 28);
            Assert.Equal("西门门卫采集仪", defaultCapture.name);
            Assert.True(defaultCapture.types.Contains("FaceCapture"));
            Assert.Equal(defaultCapture.deviceId, result.Settings.Devices.DefaultFaceCaptureDeviceId);

            var section = result.Settings.CameraAlarmDoorInterlock;
            Assert.True(section.Enabled);
            Assert.Equal(10, section.Mappings.Count);
            AssertMapping(section.Mappings, devices, cameraId: 25, doorIds: new[] { 19, 20, 21, 22 }, area: "检修路", enabled: false);
            AssertMapping(section.Mappings, devices, cameraId: 26, doorIds: new[] { 15, 16, 17, 18 }, area: "平安路", enabled: false);
            AssertMapping(section.Mappings, devices, cameraId: 27, doorIds: new[] { 13 }, area: "物资超市", enabled: false);
            AssertMapping(section.Mappings, devices, cameraId: 27, doorIds: new[] { 14 }, area: "物资超市", enabled: true);
            Assert.Equal(1, section.Mappings.Count(mapping => mapping.Enabled));
        }

        [TestCase]
        public static void Stage8ConfigurationTemplate_DeployTemplate_UsesSameFieldInventoryDefaults()
        {
            var runDirectory = CopyTemplateToRunDirectory(RepositoryDeployTemplatePath());

            var result = new ConfigurationLoader().Load(runDirectory);

            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.Equal(@"D:\ControlDoorData\logs", result.Settings.Logging.LogDirectory);
            Assert.Equal(@"D:\ControlDoorData\logs\sdk", result.Settings.HikvisionSdk.SdkLogDirectory);
            Assert.Equal(@"D:\ControlDoorData\snapshots", result.Settings.FaceEventLogging.SnapshotRootDirectory);
            Assert.True(result.Settings.CameraAlarmDoorInterlock.Enabled);
            Assert.Equal(10, result.Settings.CameraAlarmDoorInterlock.Mappings.Count);
            Assert.Equal(28, result.Settings.Devices.DefaultFaceCaptureDeviceId);
            Assert.Equal("Configuration\\devices.json", result.Settings.Devices.FilePath);
            AssertRetryDefaults(result.Settings.DeviceOperationRetry);
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
            return CopyTemplateToRunDirectory(RepositoryTemplatePath());
        }

        private static string CopyTemplateToRunDirectory(string templatePath)
        {
            var runDirectory = TestWorkspace.Create();
            var configurationDirectory = Path.Combine(runDirectory, "Configuration");
            Directory.CreateDirectory(configurationDirectory);
            File.Copy(templatePath, Path.Combine(configurationDirectory, "appsettings.json"));
            File.Copy(RepositoryDevicesPath(), Path.Combine(configurationDirectory, "devices.json"));
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

        private static string RepositoryDeployTemplatePath()
        {
            return Path.Combine("src", "ControlDoor", "Configuration", "appsettings.deploy.json");
        }

        private static string RepositoryDevicesPath()
        {
            return Path.Combine("src", "ControlDoor", "Configuration", "devices.json");
        }

        private static List<DeviceFixture> LoadRepositoryDevices()
        {
            var json = File.ReadAllText(RepositoryDevicesPath(), Encoding.UTF8);
            var document = new JavaScriptSerializer().Deserialize<DeviceDocumentFixture>(json);
            return document.devices ?? new List<DeviceFixture>();
        }

        private static void AssertRetryDefaults(DeviceOperationRetryOptions options)
        {
            Assert.NotNull(options);
            Assert.Equal(30, options.ScanIntervalSeconds);
            Assert.Equal(30, options.InitialRetryDelaySeconds);
            Assert.Equal(300, options.MaxRetryDelaySeconds);
            Assert.Equal(4200, options.MaxRetryAttempts);
            Assert.Equal(14, options.FailureRetentionDays);
            Assert.Equal(100, options.BatchSize);
            Assert.Equal(14, options.TerminalRetentionDays);
        }

        private static void AssertMapping(
            IList<CameraAlarmDoorInterlockMapping> mappings,
            IList<DeviceFixture> devices,
            int cameraId,
            int[] doorIds,
            string area,
            bool enabled)
        {
            var camera = devices.Single(item => item.deviceId == cameraId);
            Assert.True(camera.types.Contains("Camera"));
            foreach (var doorId in doorIds)
            {
                var door = devices.Single(item => item.deviceId == doorId);
                Assert.True(door.types.Contains("Acs"));
                Assert.True(mappings.Any(mapping =>
                    mapping.Enabled == enabled &&
                    mapping.Camera.Id == cameraId &&
                    string.Equals(mapping.Camera.Ip, camera.ipAddress, StringComparison.OrdinalIgnoreCase) &&
                    mapping.DoorDevice.Id == doorId &&
                    string.Equals(mapping.DoorDevice.Ip, door.ipAddress, StringComparison.OrdinalIgnoreCase) &&
                    mapping.DoorNos.Count == 1 &&
                    mapping.DoorNos[0] == 1 &&
                    mapping.Remark.Contains(area)));
            }
        }

        private sealed class DeviceDocumentFixture
        {
            public List<DeviceFixture> devices { get; set; }
        }

        private sealed class DeviceFixture
        {
            public int deviceId { get; set; }

            public string name { get; set; }

            public List<string> types { get; set; }

            public string ipAddress { get; set; }

            public string password { get; set; }

            public bool enabled { get; set; }
        }
    }
}
