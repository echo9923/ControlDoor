using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ControlDoor.Configuration;
using ControlDoor.Devices.Management;

namespace ControlEntradaSalida.Tests
{
    public static class Stage10JsonDeviceRepositoryEdgeTests
    {
        [TestCase]
        public static void JsonDeviceRepository_LoadAllDevices_ReturnsSortedClones()
        {
            var runDirectory = TestWorkspace.Create();
            WriteDevicesJson(runDirectory,
                "{\"devices\":[" +
                "{\"deviceId\":20,\"name\":\"B\",\"types\":[\"Camera\"],\"ipAddress\":\"10.10.0.20\",\"port\":8000,\"password\":\"pw\",\"enabled\":true}," +
                "{\"deviceId\":10,\"name\":\"A\",\"types\":[\"Acs\"],\"ipAddress\":\"10.10.0.10\",\"port\":8000,\"password\":\"pw\",\"enabled\":true}" +
                "]}");

            var repo = CreateRepo(runDirectory);
            var all = repo.LoadAllDevices();

            Assert.Equal(10, all[0].DeviceId);
            Assert.Equal(20, all[1].DeviceId);

            all[0].DeviceName = "被外部修改";
            all[0].Types.Clear();
            all[0].Types.Add(DeviceType.Camera);

            var loadedAgain = repo.GetByDeviceId(10);
            Assert.Equal("A", loadedAgain.DeviceName);
            Assert.True(loadedAgain.Types.Contains(DeviceType.Acs));
            Assert.False(loadedAgain.Types.Contains(DeviceType.Camera));
        }

        [TestCase]
        public static void JsonDeviceRepository_BackupOnWriteFalse_DoesNotCreateBackupFile()
        {
            var runDirectory = TestWorkspace.Create();
            WriteDevicesJson(runDirectory, "{\"devices\":[]}");

            var repo = CreateRepo(runDirectory, backupOnWrite: false);
            var insert = repo.InsertDevice(NewRecord(1, "10.10.0.1"));

            Assert.True(insert.Success);
            Assert.False(File.Exists(GetDevicesFilePath(runDirectory) + ".bak"));
            Assert.True(File.Exists(GetDevicesFilePath(runDirectory)));
        }

        [TestCase]
        public static void JsonDeviceRepository_DeleteDevice_BackupKeepsPreviousInventory()
        {
            var runDirectory = TestWorkspace.Create();
            WriteDevicesJson(runDirectory,
                "{\"devices\":[" +
                "{\"deviceId\":1,\"name\":\"保留\",\"types\":[\"Acs\"],\"ipAddress\":\"10.10.0.1\",\"port\":8000,\"password\":\"pw\",\"enabled\":true}," +
                "{\"deviceId\":2,\"name\":\"删除\",\"types\":[\"FaceCapture\"],\"ipAddress\":\"10.10.0.2\",\"port\":8000,\"password\":\"pw\",\"enabled\":true}" +
                "]}");

            var repo = CreateRepo(runDirectory, backupOnWrite: true);
            var delete = repo.DeleteDevice(2);

            Assert.True(delete.Success);
            var currentJson = File.ReadAllText(GetDevicesFilePath(runDirectory), Encoding.UTF8);
            var backupJson = File.ReadAllText(GetDevicesFilePath(runDirectory) + ".bak", Encoding.UTF8);
            Assert.Contains("\"deviceId\": 1", currentJson);
            Assert.False(currentJson.Contains("\"deviceId\": 2"));
            Assert.Contains("\"deviceId\":2", backupJson);
            Assert.Contains("\"ipAddress\":\"10.10.0.2\"", backupJson);
        }

        [TestCase]
        public static void JsonDeviceRepository_AbsoluteFilePath_LoadsAndPersists()
        {
            var runDirectory = TestWorkspace.Create();
            var absolutePath = Path.Combine(runDirectory, "Inventory", "doors.json");
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));
            File.WriteAllText(absolutePath, "{\"devices\":[]}", Encoding.UTF8);

            var repo = new JsonDeviceRepository(runDirectory, new DeviceStoreOptions
            {
                FilePath = absolutePath,
                BackupOnWrite = true,
                Items = new List<DeviceStoreItem>()
            });

            var insert = repo.InsertDevice(NewRecord(33, "10.10.0.33"));

            Assert.True(insert.Success);
            var persistedJson = File.ReadAllText(absolutePath, Encoding.UTF8);
            Assert.Contains("\"deviceId\": 33", persistedJson);
            Assert.True(File.Exists(absolutePath + ".bak"));
        }

        [TestCase]
        public static void JsonDeviceRepository_InlineDelete_UpdatesCacheWithoutTouchingFile()
        {
            var runDirectory = TestWorkspace.Create();
            WriteDevicesJson(runDirectory, "{\"devices\":[]}");
            var originalJson = File.ReadAllText(GetDevicesFilePath(runDirectory), Encoding.UTF8);
            var repo = new JsonDeviceRepository(runDirectory, new DeviceStoreOptions
            {
                FilePath = "Configuration\\devices.json",
                BackupOnWrite = true,
                Items = new List<DeviceStoreItem>
                {
                    new DeviceStoreItem
                    {
                        DeviceId = 7,
                        Name = "内联设备",
                        IpAddress = "10.10.0.7",
                        Port = 8000,
                        Password = "pw",
                        Enabled = true,
                        Types = new List<string> { "Acs" }
                    }
                }
            });

            var delete = repo.DeleteDevice(7);

            Assert.True(delete.Success);
            Assert.False(repo.ExistsDeviceId(7));
            Assert.Equal(originalJson, File.ReadAllText(GetDevicesFilePath(runDirectory), Encoding.UTF8));
            Assert.False(File.Exists(GetDevicesFilePath(runDirectory) + ".bak"));
        }

        private static JsonDeviceRepository CreateRepo(string runDirectory, bool backupOnWrite = true)
        {
            return new JsonDeviceRepository(runDirectory, new DeviceStoreOptions
            {
                FilePath = "Configuration\\devices.json",
                BackupOnWrite = backupOnWrite,
                Items = new List<DeviceStoreItem>()
            });
        }

        private static DeviceRecord NewRecord(int deviceId, string ipAddress)
        {
            return new DeviceRecord
            {
                DeviceId = deviceId,
                DeviceName = "设备-" + deviceId,
                IpAddress = ipAddress,
                Port = 8000,
                Username = "admin",
                Password = "pw",
                Enabled = true,
                Types = new List<DeviceType> { DeviceType.Acs }
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
