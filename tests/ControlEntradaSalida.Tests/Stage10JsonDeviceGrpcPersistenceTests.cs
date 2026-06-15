using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using ControlDoor.Configuration;
using ControlDoor.Devices.Management;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Workers;
using ControlDoor.GrpcApi;
using ControlDoor.Hikvision;

namespace ControlEntradaSalida.Tests
{
    public static class Stage10JsonDeviceGrpcPersistenceTests
    {
        [TestCase]
        public static void AccessControlGrpcService_AddDevice_InvalidTypes_DoesNotRewriteJson()
        {
            using (var fixture = JsonBackedFixture.Create("{\"devices\":[]}"))
            {
                var originalJson = File.ReadAllText(fixture.DevicesFilePath, Encoding.UTF8);
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.AddDevice(
                    @"{""deviceId"":101,""deviceName"":""错误类型"",""ipAddress"":""10.20.0.101"",""password"":""pw"",""types"":[""bad""],""connectNow"":false}",
                    new GrpcRequestContext { RequestId = "req-add-invalid-json" }));

                Assert.Equal(false, response["success"]);
                Assert.Equal("INVALID_ARGUMENT", response["code"]);
                Assert.False(fixture.Registry.TryGetByDeviceId(101).Found);
                Assert.Equal(originalJson, File.ReadAllText(fixture.DevicesFilePath, Encoding.UTF8));
                Assert.False(File.Exists(fixture.DevicesFilePath + ".bak"));
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_AddDevice_DuplicateIpInJson_DoesNotRewriteJson()
        {
            using (var fixture = JsonBackedFixture.Create(
                "{\"devices\":[{\"deviceId\":100,\"name\":\"已存在\",\"types\":[\"Acs\"],\"ipAddress\":\"10.20.0.100\",\"port\":8000,\"username\":\"admin\",\"password\":\"pw\",\"enabled\":true}]}"))
            {
                var originalJson = File.ReadAllText(fixture.DevicesFilePath, Encoding.UTF8);
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.AddDevice(
                    @"{""deviceId"":101,""deviceName"":""重复IP"",""ipAddress"":""10.20.0.100"",""password"":""pw"",""types"":[""Acs""],""connectNow"":false}",
                    new GrpcRequestContext { RequestId = "req-add-dup-ip-json" }));

                Assert.Equal(false, response["success"]);
                Assert.Equal("INVALID_ARGUMENT", response["code"]);
                Assert.Contains("设备 IP 已存在", (string)response["message"]);
                Assert.False(fixture.Repository.ExistsDeviceId(101));
                Assert.Equal(originalJson, File.ReadAllText(fixture.DevicesFilePath, Encoding.UTF8));
                Assert.False(File.Exists(fixture.DevicesFilePath + ".bak"));
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_DeleteUnknownDevice_DoesNotRewriteJsonOrCreateBackup()
        {
            using (var fixture = JsonBackedFixture.Create(
                "{\"devices\":[{\"deviceId\":100,\"name\":\"保留\",\"types\":[\"Acs\"],\"ipAddress\":\"10.20.0.100\",\"port\":8000,\"username\":\"admin\",\"password\":\"pw\",\"enabled\":true}]}"))
            {
                var originalJson = File.ReadAllText(fixture.DevicesFilePath, Encoding.UTF8);
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.DeleteDevice(
                    @"{""deviceId"":999,""disconnectFirst"":false}",
                    new GrpcRequestContext { RequestId = "req-delete-missing-json" }));

                Assert.Equal(true, response["success"]);
                Assert.Equal("OK", response["code"]);
                Assert.True(fixture.Repository.ExistsDeviceId(100));
                Assert.Equal(originalJson, File.ReadAllText(fixture.DevicesFilePath, Encoding.UTF8));
                Assert.False(File.Exists(fixture.DevicesFilePath + ".bak"));
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_GetDeviceStatus_ByIpReturnsDisabledJsonOnlyDevice()
        {
            using (var fixture = JsonBackedFixture.Create(
                "{\"devices\":[{\"deviceId\":300,\"name\":\"禁用摄像头\",\"types\":[\"Camera\"],\"ipAddress\":\"10.20.0.300\",\"port\":8000,\"username\":\"admin\",\"password\":\"pw\",\"enabled\":false,\"remark\":\"生产区域\"}]}"))
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.GetDeviceStatus(
                    @"{""includeDisabled"":true,""ipAddress"":""10.20.0.300""}",
                    new GrpcRequestContext { RequestId = "req-status-disabled-json" }));

                Assert.Equal(true, response["success"]);
                var devices = (ArrayList)response["devices"];
                Assert.Equal(1, devices.Count);
                var device = (Dictionary<string, object>)devices[0];
                Assert.Equal(300, device["deviceId"]);
                Assert.Equal(false, device["enabled"]);
                Assert.Equal("Disabled", device["status"]);
                Assert.Equal("生产区域", device["description"]);
                Assert.True(device.ContainsKey("types"));
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_AddDevice_ConnectNowFalsePersistsBeforeRuntimeLookup()
        {
            using (var fixture = JsonBackedFixture.Create("{\"devices\":[]}"))
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.AddDevice(
                    @"{""deviceId"":120,""deviceName"":""新增门禁"",""ipAddress"":""10.20.0.120"",""password"":""pw"",""types"":[""Acs"",""FaceCapture""],""description"":""办公区域"",""connectNow"":false}",
                    new GrpcRequestContext { RequestId = "req-add-json-success" }));

                Assert.Equal(true, response["success"]);
                Assert.True(fixture.Repository.ExistsDeviceId(120));
                Assert.True(fixture.Registry.TryGetByDeviceId(120).Found);
                var snapshot = fixture.Registry.TryGetByDeviceId(120).Snapshot;
                Assert.Equal("办公区域", snapshot.Description);
                var persistedJson = File.ReadAllText(fixture.DevicesFilePath, Encoding.UTF8);
                Assert.Contains("\"deviceId\": 120", persistedJson);
                Assert.Contains("\"FaceCapture\"", persistedJson);
                Assert.Contains("\"remark\": \"办公区域\"", persistedJson);
            }
        }

        private static Dictionary<string, object> Deserialize(string json)
        {
            return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
        }

        private sealed class JsonBackedFixture : System.IDisposable
        {
            private JsonBackedFixture(
                string runDirectory,
                JsonDeviceRepository repository,
                DeviceRuntimeRegistry registry,
                DeviceSdkDispatcher dispatcher,
                DelayedDeviceTaskScheduler delayedScheduler,
                MockHikvisionGateway gateway,
                DeviceLifecycleService lifecycle)
            {
                RunDirectory = runDirectory;
                Repository = repository;
                Registry = registry;
                Dispatcher = dispatcher;
                DelayedScheduler = delayedScheduler;
                Gateway = gateway;
                Lifecycle = lifecycle;
                DevicesFilePath = Path.Combine(runDirectory, "Configuration", "devices.json");
            }

            public string RunDirectory { get; }

            public string DevicesFilePath { get; }

            public JsonDeviceRepository Repository { get; }

            public DeviceRuntimeRegistry Registry { get; }

            public DeviceSdkDispatcher Dispatcher { get; }

            public DelayedDeviceTaskScheduler DelayedScheduler { get; }

            public MockHikvisionGateway Gateway { get; }

            public DeviceLifecycleService Lifecycle { get; }

            public static JsonBackedFixture Create(string devicesJson)
            {
                var runDirectory = TestWorkspace.Create();
                var configurationDirectory = Path.Combine(runDirectory, "Configuration");
                Directory.CreateDirectory(configurationDirectory);
                File.WriteAllText(Path.Combine(configurationDirectory, "devices.json"), devicesJson, Encoding.UTF8);

                var repository = new JsonDeviceRepository(
                    runDirectory,
                    new DeviceStoreOptions
                    {
                        FilePath = "Configuration\\devices.json",
                        BackupOnWrite = true,
                        Items = new List<DeviceStoreItem>()
                    });
                var registry = new DeviceRuntimeRegistry(new DeviceRuntimeRegistryOptions { WorkerCount = 2 });
                var dispatcher = new DeviceSdkDispatcher(registry, workerCount: 2, queueCapacityPerWorker: 50, defaultTaskTimeoutMilliseconds: 5000);
                var delayedScheduler = new DelayedDeviceTaskScheduler(dispatcher, new DelayedDeviceTaskSchedulerOptions { WakeupMaxSleepMilliseconds = 10 });
                var gateway = new MockHikvisionGateway();
                var lifecycle = new DeviceLifecycleService(
                    registry,
                    dispatcher,
                    delayedScheduler,
                    repository,
                    gateway,
                    new DeviceLifecycleOptions
                    {
                        LoginTimeoutMs = 5000,
                        LogoutTimeoutMs = 5000,
                        HealthCheckIntervalMs = 1000,
                        HealthCheckTimeoutMs = 5000,
                        ReconnectBaseDelayMs = 10,
                        ReconnectMaxDelayMs = 100,
                        MaxReconnectAttempts = 3,
                        FailureThreshold = 3,
                        AlarmEnabled = true
                    });

                return new JsonBackedFixture(runDirectory, repository, registry, dispatcher, delayedScheduler, gateway, lifecycle);
            }

            public void Dispose()
            {
                Dispatcher.StopAsync(System.TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                Gateway.Dispose();
                Lifecycle.Dispose();
            }
        }
    }
}
