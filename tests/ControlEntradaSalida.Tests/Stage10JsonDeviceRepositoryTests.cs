using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using ControlDoor.Configuration;
using ControlDoor.Devices.Management;

namespace ControlEntradaSalida.Tests
{
    public static class Stage10JsonDeviceRepositoryTests
    {
        [TestCase]
        public static void JsonDeviceRepository_LoadFromFile_PopulatesCacheAndParsesTypes()
        {
            var runDirectory = TestWorkspace.Create();
            WriteDevicesJson(runDirectory,
                "{\"devices\":[" +
                "{\"deviceId\":1001,\"name\":\"东门门禁\",\"types\":[\"Acs\"],\"ipAddress\":\"10.0.0.1\",\"port\":8000,\"username\":\"admin\",\"password\":\"pw1\",\"enabled\":true}," +
                "{\"deviceId\":2001,\"name\":\"前台明眸\",\"types\":[\"Acs\",\"FaceCapture\"],\"ipAddress\":\"10.0.0.2\",\"port\":8000,\"username\":\"admin\",\"password\":\"pw2\",\"enabled\":true}," +
                "{\"deviceId\":3001,\"name\":\"周界摄像头\",\"types\":[\"camera\"],\"ipAddress\":\"10.0.0.3\",\"port\":8000,\"username\":\"admin\",\"password\":\"pw3\",\"enabled\":false}" +
                "]}");

            var repo = CreateRepo(runDirectory);
            var enabled = repo.LoadEnabledDevices();
            Assert.Equal(2, enabled.Count);

            var mingmou = repo.GetByDeviceId(2001);
            Assert.NotNull(mingmou);
            Assert.True(mingmou.Types.Contains(DeviceType.Acs));
            Assert.True(mingmou.Types.Contains(DeviceType.FaceCapture));

            var all = repo.LoadAllDevices();
            Assert.Equal(3, all.Count);
            Assert.True(all.Any(item => item.DeviceId == 3001 && item.Types.Contains(DeviceType.Camera)));
        }

        [TestCase]
        public static void JsonDeviceRepository_MissingFile_FailsClearly()
        {
            var runDirectory = TestWorkspace.Create();
            try
            {
                CreateRepo(runDirectory);
                Assert.True(false, "Expected missing devices.json to fail.");
            }
            catch (FileNotFoundException ex)
            {
                Assert.Contains("设备清单文件不存在", ex.Message);
            }
        }

        [TestCase]
        public static void DeviceStoreOptions_NoLongerExposeSourceAndDefaultToJsonFile()
        {
            var settings = NewValidSettings();
            settings.Devices = new DeviceStoreOptions();

            var result = new ConfigurationValidator().Validate(settings);

            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.Equal("Configuration\\devices.json", result.Settings.Devices.FilePath);
            Assert.Equal(null, typeof(DeviceStoreOptions).GetProperty("Source"));
        }

        [TestCase]
        public static void ConfigurationValidator_DeviceStoreInlineItems_RejectsDuplicateIdsAndIps()
        {
            var settings = NewValidSettings();
            settings.Devices = new DeviceStoreOptions
            {
                Items = new List<DeviceStoreItem>
                {
                    NewItem(1, "10.0.0.1"),
                    NewItem(1, "10.0.0.1")
                }
            };

            var result = new ConfigurationValidator().Validate(settings);

            Assert.False(result.Success);
            Assert.True(result.Errors.Any(item => item.Contains("deviceId=1") && item.Contains("重复")));
            Assert.True(result.Errors.Any(item => item.Contains("ipAddress=10.0.0.1") && item.Contains("重复")));
        }

        [TestCase]
        public static void ConfigurationValidator_DeviceStoreInlineItems_RequiresAndNormalizesTypes()
        {
            var settings = NewValidSettings();
            settings.Devices = new DeviceStoreOptions
            {
                Items = new List<DeviceStoreItem>
                {
                    NewItem(1, "10.0.0.1", "acs", "bad"),
                    NewItem(2, "10.0.0.2")
                }
            };

            var result = new ConfigurationValidator().Validate(settings);

            Assert.False(result.Success);
            Assert.True(result.Warnings.Any(item => item.Contains("types 含非法值")));
            Assert.True(result.Errors.Any(item => item.Contains("Devices.Items[1].types 不能为空")));
            Assert.Equal(1, result.Settings.Devices.Items[0].Types.Count);
            Assert.Equal("Acs", result.Settings.Devices.Items[0].Types[0]);
        }

        [TestCase]
        public static void JsonDeviceRepository_InsertDevice_PersistsAtomically()
        {
            var runDirectory = TestWorkspace.Create();
            WriteDevicesJson(runDirectory, "{\"devices\":[]}");

            var repo = CreateRepo(runDirectory);
            var record = new DeviceRecord
            {
                DeviceId = 1001,
                DeviceName = "新增门禁",
                IpAddress = "10.0.0.10",
                Port = 8000,
                Username = "admin",
                Password = "pw",
                Enabled = true,
                Types = new System.Collections.Generic.List<DeviceType> { DeviceType.Acs }
            };

            var insert = repo.InsertDevice(record);
            Assert.True(insert.Success);
            Assert.True(repo.ExistsDeviceId(1001));
            Assert.True(repo.ExistsIpAddress("10.0.0.10"));
            var persistedJson = File.ReadAllText(GetDevicesFilePath(runDirectory), Encoding.UTF8);
            Assert.Contains("\"deviceId\"", persistedJson);
            Assert.Contains("\"ipAddress\"", persistedJson);
            Assert.Contains("\"types\"", persistedJson);

            // 重启（新实例）后应能从磁盘读回。
            var repo2 = CreateRepo(runDirectory);
            var loaded = repo2.GetByDeviceId(1001);
            Assert.NotNull(loaded);
            Assert.Equal("新增门禁", loaded.DeviceName);
            Assert.True(loaded.Types.Contains(DeviceType.Acs));
        }

        [TestCase]
        public static void JsonDeviceRepository_InsertDuplicateDeviceId_RejectedAndRolledBack()
        {
            var runDirectory = TestWorkspace.Create();
            WriteDevicesJson(runDirectory,
                "{\"devices\":[{\"deviceId\":1001,\"name\":\"已存在\",\"types\":[\"Acs\"],\"ipAddress\":\"10.0.0.1\",\"port\":8000,\"password\":\"pw\",\"enabled\":true}]}");

            var repo = CreateRepo(runDirectory);
            var record = new DeviceRecord
            {
                DeviceId = 1001,
                DeviceName = "重复",
                IpAddress = "10.0.0.99",
                Port = 8000,
                Password = "pw",
                Enabled = true,
                Types = new System.Collections.Generic.List<DeviceType> { DeviceType.Acs }
            };

            var insert = repo.InsertDevice(record);
            Assert.False(insert.Success);
            Assert.Equal("DUPLICATE_DEVICE_ID", insert.Code);

            // 回滚：磁盘和内存都没有重复条目。
            Assert.False(repo.ExistsIpAddress("10.0.0.99"));
            Assert.Equal(1, repo.LoadAllDevices().Count);
        }

        [TestCase]
        public static void JsonDeviceRepository_InsertDuplicateIp_RejectedAndRolledBack()
        {
            var runDirectory = TestWorkspace.Create();
            WriteDevicesJson(runDirectory,
                "{\"devices\":[{\"deviceId\":1001,\"name\":\"已存在\",\"types\":[\"Acs\"],\"ipAddress\":\"10.0.0.1\",\"port\":8000,\"password\":\"pw\",\"enabled\":true}]}");

            var repo = CreateRepo(runDirectory);
            var record = new DeviceRecord
            {
                DeviceId = 1002,
                DeviceName = "IP重复",
                IpAddress = "10.0.0.1",
                Port = 8000,
                Password = "pw",
                Enabled = true,
                Types = new System.Collections.Generic.List<DeviceType> { DeviceType.Acs }
            };

            var insert = repo.InsertDevice(record);
            Assert.False(insert.Success);
            Assert.Equal("DUPLICATE_IP_ADDRESS", insert.Code);
            Assert.False(repo.ExistsDeviceId(1002));
            Assert.Equal(1, repo.LoadAllDevices().Count);
        }

        [TestCase]
        public static void JsonDeviceRepository_InvalidJson_FailsClearly()
        {
            var runDirectory = TestWorkspace.Create();
            WriteDevicesJson(runDirectory, "{bad");

            try
            {
                CreateRepo(runDirectory);
                Assert.True(false, "Expected invalid devices.json to fail.");
            }
            catch (System.InvalidOperationException ex)
            {
                Assert.Contains("设备清单加载失败", ex.Message);
            }
        }

        [TestCase]
        public static void JsonDeviceRepository_InvalidTypes_FailClearly()
        {
            var runDirectory = TestWorkspace.Create();
            WriteDevicesJson(runDirectory,
                "{\"devices\":[{\"deviceId\":1001,\"name\":\"坏类型\",\"types\":[\"1\"],\"ipAddress\":\"10.0.0.1\",\"port\":8000,\"password\":\"pw\",\"enabled\":true}]}");

            try
            {
                CreateRepo(runDirectory);
                Assert.True(false, "Expected invalid device type to fail.");
            }
            catch (System.InvalidOperationException ex)
            {
                Assert.Contains("types 含非法值", ex.Message);
            }
        }

        [TestCase]
        public static void JsonDeviceRepository_MissingTypes_FailClearly()
        {
            var runDirectory = TestWorkspace.Create();
            WriteDevicesJson(runDirectory,
                "{\"devices\":[{\"deviceId\":1001,\"name\":\"空类型\",\"types\":[],\"ipAddress\":\"10.0.0.1\",\"port\":8000,\"password\":\"pw\",\"enabled\":true}]}");

            try
            {
                CreateRepo(runDirectory);
                Assert.True(false, "Expected empty device types to fail.");
            }
            catch (System.InvalidOperationException ex)
            {
                Assert.Contains("types 不能为空", ex.Message);
            }
        }

        [TestCase]
        public static void JsonDeviceRepository_DeleteDevice_RemovesAndPersists()
        {
            var runDirectory = TestWorkspace.Create();
            WriteDevicesJson(runDirectory,
                "{\"devices\":[" +
                "{\"deviceId\":1001,\"name\":\"A\",\"types\":[\"Acs\"],\"ipAddress\":\"10.0.0.1\",\"port\":8000,\"password\":\"pw\",\"enabled\":true}," +
                "{\"deviceId\":1002,\"name\":\"B\",\"types\":[\"Camera\"],\"ipAddress\":\"10.0.0.2\",\"port\":8000,\"password\":\"pw\",\"enabled\":true}" +
                "]}");

            var repo = CreateRepo(runDirectory);
            var delete = repo.DeleteDevice(1001);
            Assert.True(delete.Success);
            Assert.False(repo.ExistsDeviceId(1001));
            Assert.Equal(1, repo.LoadAllDevices().Count);

            var repo2 = CreateRepo(runDirectory);
            Assert.False(repo2.ExistsDeviceId(1001));
            Assert.True(repo2.ExistsDeviceId(1002));
        }

        [TestCase]
        public static void JsonDeviceRepository_DeleteUnknownDevice_SucceedsWithZeroRows()
        {
            var runDirectory = TestWorkspace.Create();
            WriteDevicesJson(runDirectory, "{\"devices\":[]}");

            var repo = CreateRepo(runDirectory);
            var delete = repo.DeleteDevice(9999);
            Assert.True(delete.Success);
        }

        [TestCase]
        public static void JsonDeviceRepository_BackupOnWrite_CreatesBakFile()
        {
            var runDirectory = TestWorkspace.Create();
            WriteDevicesJson(runDirectory, "{\"devices\":[]}");

            var repo = CreateRepo(runDirectory, backupOnWrite: true);
            repo.InsertDevice(new DeviceRecord
            {
                DeviceId = 1,
                DeviceName = "n",
                IpAddress = "1.1.1.1",
                Port = 8000,
                Password = "pw",
                Enabled = true,
                Types = new System.Collections.Generic.List<DeviceType> { DeviceType.Acs }
            });

            var devicesPath = GetDevicesFilePath(runDirectory);
            Assert.True(File.Exists(devicesPath + ".bak"));
        }

        [TestCase]
        public static void JsonDeviceRepository_InlineItems_SkipsFileIo()
        {
            var options = new DeviceStoreOptions
            {
                FilePath = "Configuration\\devices.json",
                BackupOnWrite = true,
                Items = new System.Collections.Generic.List<DeviceStoreItem>
                {
                    new DeviceStoreItem
                    {
                        DeviceId = 1,
                        Name = "内联设备",
                        IpAddress = "10.0.0.1",
                        Port = 8000,
                        Password = "pw",
                        Enabled = true,
                        Types = new System.Collections.Generic.List<string> { "Acs" }
                    }
                }
            };

            var repo = new JsonDeviceRepository(TestWorkspace.Create(), options);
            Assert.Equal(1, repo.LoadAllDevices().Count);
            Assert.True(repo.ExistsDeviceId(1));

            // 内联模式下 InsertDevice 只更新运行期缓存，不写入 FilePath。
            var insert = repo.InsertDevice(new DeviceRecord
            {
                DeviceId = 2,
                DeviceName = "运行时新增",
                IpAddress = "10.0.0.2",
                Port = 8000,
                Password = "pw",
                Enabled = true,
                Types = new System.Collections.Generic.List<DeviceType> { DeviceType.Camera }
            });
            Assert.True(insert.Success);
            Assert.Equal(2, repo.LoadAllDevices().Count);
        }

        private static JsonDeviceRepository CreateRepo(string runDirectory, bool backupOnWrite = true)
        {
            var options = new DeviceStoreOptions
            {
                FilePath = "Configuration\\devices.json",
                BackupOnWrite = backupOnWrite,
                Items = new System.Collections.Generic.List<DeviceStoreItem>()
            };
            return new JsonDeviceRepository(runDirectory, options);
        }

        private static AppSettings NewValidSettings()
        {
            return new AppSettings
            {
                Database = new DatabaseOptions { ConnectionString = "Server=.;Database=test;" }
            };
        }

        private static DeviceStoreItem NewItem(int deviceId, string ipAddress, params string[] types)
        {
            return new DeviceStoreItem
            {
                DeviceId = deviceId,
                Name = "设备-" + deviceId,
                IpAddress = ipAddress,
                Port = 8000,
                Username = "admin",
                Password = "pw",
                Enabled = true,
                Types = types == null ? new List<string>() : types.ToList()
            };
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
