using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ControlDoor.Configuration;
using ControlDoor.Runtime.Health;
using ControlDoor.Runtime.Health.Checks;

namespace ControlEntradaSalida.Tests
{
    public static class Stage10JsonDeviceHealthAndConfigTests
    {
        [TestCase]
        public static void DeviceStoreHealthCheck_DuplicateDeviceId_FailsAsJsonInventory()
        {
            var runDirectory = TestWorkspace.Create();
            WriteDevicesJson(runDirectory,
                "{\"devices\":[" +
                "{\"deviceId\":1,\"name\":\"A\",\"types\":[\"Acs\"],\"ipAddress\":\"10.30.0.1\",\"port\":8000,\"password\":\"pw\",\"enabled\":true}," +
                "{\"deviceId\":1,\"name\":\"B\",\"types\":[\"Camera\"],\"ipAddress\":\"10.30.0.2\",\"port\":8000,\"password\":\"pw\",\"enabled\":true}" +
                "]}");
            var settings = NewValidSettings();
            settings.Devices = new DeviceStoreOptions { FilePath = "Configuration\\devices.json" };

            var result = new DeviceStoreHealthCheck().Run(NewContext(runDirectory, settings));

            Assert.Equal(HealthCheckStatus.Failed, result.Status);
            Assert.Contains("deviceId=1", result.Message);
            Assert.False(result.Message.Contains("Database"));
            Assert.False(result.Message.Contains("数据库" + "来源"));
        }

        [TestCase]
        public static void DeviceStoreHealthCheck_DuplicateIp_FailsClearly()
        {
            var runDirectory = TestWorkspace.Create();
            WriteDevicesJson(runDirectory,
                "{\"devices\":[" +
                "{\"deviceId\":1,\"name\":\"A\",\"types\":[\"Acs\"],\"ipAddress\":\"10.30.0.1\",\"port\":8000,\"password\":\"pw\",\"enabled\":true}," +
                "{\"deviceId\":2,\"name\":\"B\",\"types\":[\"Camera\"],\"ipAddress\":\"10.30.0.1\",\"port\":8000,\"password\":\"pw\",\"enabled\":true}" +
                "]}");
            var settings = NewValidSettings();
            settings.Devices = new DeviceStoreOptions { FilePath = "Configuration\\devices.json" };

            var result = new DeviceStoreHealthCheck().Run(NewContext(runDirectory, settings));

            Assert.Equal(HealthCheckStatus.Failed, result.Status);
            Assert.Contains("ipAddress=10.30.0.1", result.Message);
            Assert.Contains("重复", result.Message);
        }

        [TestCase]
        public static void DeviceStoreHealthCheck_EnabledDeviceMissingRuntimeRequiredFields_FailsClearly()
        {
            var runDirectory = TestWorkspace.Create();
            WriteDevicesJson(runDirectory,
                "{\"devices\":[" +
                "{\"deviceId\":1,\"name\":\"\",\"types\":[\"Acs\"],\"ipAddress\":\"10.30.0.1\",\"port\":8000,\"password\":\"pw\",\"enabled\":true}" +
                "]}");
            var settings = NewValidSettings();
            settings.Devices = new DeviceStoreOptions { FilePath = "Configuration\\devices.json" };

            var result = new DeviceStoreHealthCheck().Run(NewContext(runDirectory, settings));

            Assert.Equal(HealthCheckStatus.Failed, result.Status);
            Assert.Contains("devices[0].name", result.Message);
        }

        [TestCase]
        public static void DeviceStoreHealthCheck_AbsoluteFilePath_IsSupported()
        {
            var runDirectory = TestWorkspace.Create();
            var absolutePath = Path.Combine(runDirectory, "Inventory", "devices.json");
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));
            File.WriteAllText(
                absolutePath,
                "{\"devices\":[{\"deviceId\":1,\"name\":\"A\",\"types\":[\"Acs\"],\"ipAddress\":\"10.30.0.1\",\"port\":8000,\"password\":\"pw\",\"enabled\":true}]}",
                Encoding.UTF8);
            var settings = NewValidSettings();
            settings.Devices = new DeviceStoreOptions { FilePath = absolutePath };

            var result = new DeviceStoreHealthCheck().Run(NewContext(runDirectory, settings));

            Assert.Equal(HealthCheckStatus.OK, result.Status);
            Assert.Contains("设备数量: 1", result.Message);
        }

        [TestCase]
        public static void DeviceStoreHealthCheck_InlineInvalidTypes_FailsBeforeMissingFileCheck()
        {
            var runDirectory = TestWorkspace.Create();
            var settings = NewValidSettings();
            settings.Devices = new DeviceStoreOptions
            {
                FilePath = "Configuration\\missing-devices.json",
                Items = new List<DeviceStoreItem>
                {
                    new DeviceStoreItem
                    {
                        DeviceId = 1,
                        Name = "内联坏类型",
                        IpAddress = "10.30.0.1",
                        Port = 8000,
                        Password = "pw",
                        Enabled = true,
                        Types = new List<string> { "bad" }
                    }
                }
            };

            var result = new DeviceStoreHealthCheck().Run(NewContext(runDirectory, settings));

            Assert.Equal(HealthCheckStatus.Failed, result.Status);
            Assert.Contains("types 含非法值", result.Message);
            Assert.False(result.Message.Contains("设备清单文件不存在"));
        }

        [TestCase]
        public static void ConfigurationValidator_DeviceStoreInlineItems_NormalizesPortNullItemAndTypes()
        {
            var settings = NewValidSettings();
            settings.Devices = new DeviceStoreOptions
            {
                FilePath = "",
                Items = new List<DeviceStoreItem>
                {
                    null,
                    new DeviceStoreItem
                    {
                        DeviceId = 1,
                        Name = "  门禁  ",
                        IpAddress = " 10.30.0.1 ",
                        Port = 90000,
                        Username = " ",
                        Password = "pw",
                        Enabled = true,
                        Types = new List<string> { "acs", "ACS", "camera", "bad", " " }
                    }
                }
            };

            var result = new ConfigurationValidator().Validate(settings);

            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.Equal("Configuration\\devices.json", result.Settings.Devices.FilePath);
            Assert.True(result.Warnings.Any(item => item.Contains("Devices.Items[0] 为空")));
            Assert.True(result.Warnings.Any(item => item.Contains("port 非法")));
            Assert.True(result.Warnings.Any(item => item.Contains("types 含非法值")));
            var item = result.Settings.Devices.Items[1];
            Assert.Equal("门禁", item.Name);
            Assert.Equal("10.30.0.1", item.IpAddress);
            Assert.Equal(8000, item.Port);
            Assert.Equal("admin", item.Username);
            Assert.Equal(2, item.Types.Count);
            Assert.True(item.Types.Contains("Acs"));
            Assert.True(item.Types.Contains("Camera"));
        }

        [TestCase]
        public static void ConfigurationTemplate_DevicesSection_IsJsonOnly()
        {
            var template = File.ReadAllText(Path.Combine("src", "ControlDoor", "Configuration", "appsettings.json"), Encoding.UTF8);

            Assert.Contains("JSON device inventory", template);
            Assert.Contains("\"FilePath\": \"Configuration\\\\devices.json\"", template);
            Assert.Contains("\"BackupOnWrite\": true", template);
            Assert.False(template.Contains("\"Source\""));
            Assert.False(template.Contains("Source=" + "Database"));
            Assert.False(template.Contains("dbo." + "devices"));
        }

        private static AppSettings NewValidSettings()
        {
            return new AppSettings
            {
                Database = new DatabaseOptions { ConnectionString = "Server=.;Database=test;" }
            };
        }

        private static HealthCheckContext NewContext(string runDirectory, AppSettings settings)
        {
            return new HealthCheckContext(runDirectory, settings, null, CancellationToken.None);
        }

        private static string GetDevicesFilePath(string runDirectory)
        {
            return Path.Combine(runDirectory, "Configuration", "devices.json");
        }

        private static void WriteDevicesJson(string runDirectory, string json)
        {
            var configDirectory = Path.Combine(runDirectory, "Configuration");
            Directory.CreateDirectory(configDirectory);
            File.WriteAllText(GetDevicesFilePath(runDirectory), json, Encoding.UTF8);
        }
    }
}
