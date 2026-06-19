using System.IO;
using System.Linq;
using ControlDoor.Configuration;
using ControlDoor.Deployment;

namespace ControlEntradaSalida.Tests
{
    public static class Stage8ServicePackageCheckerTests
    {
        [TestCase]
        public static void Stage8ServicePackageChecker_CompletePackage_Passes()
        {
            var packageRoot = CreatePackage();

            var result = new Stage8ServicePackageChecker().Check(packageRoot);

            Assert.True(result.Success, string.Join("; ", result.Items.Where(item => !item.Success).Select(item => item.Name + ":" + item.Message)));
        }

        [TestCase]
        public static void Stage8ServicePackageChecker_MissingSdkDll_Fails()
        {
            var packageRoot = CreatePackage();
            File.Delete(Path.Combine(packageRoot, "sdk", "Hikvision", "HCNetSDK.dll"));

            var result = new Stage8ServicePackageChecker().Check(packageRoot);

            Assert.False(result.Success);
            Assert.True(result.Items.Any(item => item.Name == "Hikvision SDK DLL" && !item.Success));
        }

        [TestCase]
        public static void Stage8ServicePackageChecker_MissingDevicesJson_Fails()
        {
            var packageRoot = CreatePackage();
            File.Delete(Path.Combine(packageRoot, "Configuration", "devices.json"));

            var result = new Stage8ServicePackageChecker().Check(packageRoot);

            Assert.False(result.Success);
            Assert.True(result.Items.Any(item => item.Name == "Configuration\\devices.json" && !item.Success));
        }

        [TestCase]
        public static void Stage8ServicePackageChecker_MissingCommonServiceScript_Fails()
        {
            var packageRoot = CreatePackage();
            File.Delete(Path.Combine(packageRoot, "tools", "service", "common-service.ps1"));

            var result = new Stage8ServicePackageChecker().Check(packageRoot);

            Assert.False(result.Success);
            Assert.True(result.Items.Any(item => item.Name == "tools\\service\\common-service.ps1" && !item.Success));
        }

        [TestCase]
        public static void ConfigurationLoader_Stage8AliasGroups_LoadAsRuntimeOptions()
        {
            var packageRoot = CreatePackage();

            var result = new ConfigurationLoader().Load(packageRoot);

            Assert.True(result.Success);
            Assert.Equal(6, result.Settings.DeviceSdkDispatcher.WorkerCount);
            Assert.Equal(5000, result.Settings.DeviceConnection.StatusCheckIntervalMs);
            Assert.Equal("sdk\\Hikvision", result.Settings.HikvisionSdk.DllDirectory);
        }

        private static string CreatePackage()
        {
            var packageRoot = TestWorkspace.Create();
            Directory.CreateDirectory(Path.Combine(packageRoot, "Configuration"));
            Directory.CreateDirectory(Path.Combine(packageRoot, "sdk", "Hikvision"));
            Directory.CreateDirectory(Path.Combine(packageRoot, "SqlServerTypes"));
            Directory.CreateDirectory(Path.Combine(packageRoot, "logs"));
            Directory.CreateDirectory(Path.Combine(packageRoot, "snapshots"));
            Directory.CreateDirectory(Path.Combine(packageRoot, "docs"));
            Directory.CreateDirectory(Path.Combine(packageRoot, "tools", "service"));

            File.WriteAllText(Path.Combine(packageRoot, "ControlDoor.exe"), "placeholder");
            File.WriteAllText(Path.Combine(packageRoot, "ControlDoor.exe.config"), "<configuration />");
            File.WriteAllText(Path.Combine(packageRoot, "sdk", "Hikvision", "HCNetSDK.dll"), "placeholder");
            File.WriteAllText(Path.Combine(packageRoot, "docs", "部署说明.md"), "部署说明");
            File.WriteAllText(Path.Combine(packageRoot, "docs", "运行前检查.md"), "运行前检查");
            File.WriteAllText(Path.Combine(packageRoot, "docs", "联调记录模板.md"), "联调记录模板");
            File.WriteAllText(Path.Combine(packageRoot, "Configuration", "devices.json"), @"{""devices"":[]}");
            foreach (var script in new[] { "common-service.ps1", "install-service.ps1", "start-service.ps1", "stop-service.ps1", "uninstall-service.ps1" })
            {
                File.WriteAllText(Path.Combine(packageRoot, "tools", "service", script), "placeholder");
            }
            TestWorkspace.WriteConfig(packageRoot, @"{
  ""Service"": { ""GrpcListenPort"": 5001, ""GrpcManagementApiKey"": """" },
  ""Database"": { ""ConnectionString"": ""Server=.;Database=test;"" },
  ""Logging"": { ""LogDirectory"": ""logs"", ""RetentionDays"": 30 },
  ""DeviceRuntime"": { ""WorkerCount"": 6, ""QueueCapacity"": 1000, ""DefaultTaskTimeoutMs"": 30000 },
  ""HikvisionSdk"": { ""Platform"": ""x64"", ""DllDirectory"": ""sdk\\Hikvision"", ""SdkLogDirectory"": ""logs\\sdk"" },
  ""DeviceLifecycle"": { ""StatusCheckIntervalMs"": 5000, ""LoginTimeoutMs"": 15000 },
  ""DeviceOperationRetry"": { ""ScanIntervalSeconds"": 30, ""MaxRetryAttempts"": 10 },
  ""FaceEventLogging"": { ""Enabled"": true, ""SnapshotRootDirectory"": ""snapshots"" },
  ""FaceEnrollment"": { ""MaxFaceImageBytes"": 204800 },
  ""CameraAlarmDoorInterlock"": { ""Enabled"": false, ""Mappings"": [] }
}");

            return packageRoot;
        }
    }
}
