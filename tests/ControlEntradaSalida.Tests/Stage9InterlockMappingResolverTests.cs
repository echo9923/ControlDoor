using System.Collections.Generic;
using System.Linq;
using ControlDoor.CameraDoorInterlock;
using ControlDoor.Configuration;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Management;

namespace ControlEntradaSalida.Tests
{
    public static class Stage9InterlockMappingResolverTests
    {
        [TestCase]
        public static void Stage9Resolver_DoorIp_ResolvesDeviceIdFromRegistry()
        {
            var registry = RegisterDoorDevice(new DeviceRuntimeRegistry(), 10, "10.0.0.10");
            var resolver = new InterlockMappingResolver(Options("10.0.0.5", doorIp: "10.0.0.10"), registry);

            var targets = resolver.ResolveTargets("10.0.0.5");

            Assert.Equal(1, targets.Count);
            Assert.Equal(10, targets[0].DoorDeviceId);
            Assert.Equal(1, targets[0].DoorNo);
            Assert.Equal("10:1", targets[0].TargetKey);
        }

        [TestCase]
        public static void Stage9Resolver_DoorIpTakesPrecedenceOverId()
        {
            var registry = RegisterDoorDevice(new DeviceRuntimeRegistry(), 10, "10.0.0.10");
            var options = Options("10.0.0.5", doorIp: "10.0.0.10");
            options.Mappings[0].DoorDevice.Id = 99;
            var resolver = new InterlockMappingResolver(options, registry);

            var targets = resolver.ResolveTargets("10.0.0.5");

            Assert.Equal(10, targets[0].DoorDeviceId);
        }

        [TestCase]
        public static void Stage9Resolver_DoorIdFallback_WhenIpEmpty()
        {
            var registry = new DeviceRuntimeRegistry();
            var options = new CameraAlarmDoorInterlockOptions
            {
                Enabled = true,
                Mappings = new List<CameraAlarmDoorInterlockMapping>
                {
                    new CameraAlarmDoorInterlockMapping
                    {
                        Camera = new InterlockCamera { Ip = "10.0.0.5" },
                        DoorDevice = new InterlockDoorDevice { Id = 77 },
                        DoorNos = new List<int> { 1 }
                    }
                }
            };
            var resolver = new InterlockMappingResolver(options, registry);

            var targets = resolver.ResolveTargets("10.0.0.5");

            Assert.Equal(1, targets.Count);
            Assert.Equal(77, targets[0].DoorDeviceId);
        }

        [TestCase]
        public static void Stage9Resolver_DoorIdFallback_RejectsRegisteredNonAcsDevice()
        {
            var registry = new DeviceRuntimeRegistry();
            registry.Register(new DeviceRuntimeCreationOptions
            {
                DeviceId = 77,
                DeviceName = "摄像头-77",
                IpAddress = "10.0.0.77",
                Port = 8000,
                Username = "admin",
                Password = "12345",
                Types = new List<DeviceType> { DeviceType.Camera }
            });
            var options = new CameraAlarmDoorInterlockOptions
            {
                Enabled = true,
                Mappings = new List<CameraAlarmDoorInterlockMapping>
                {
                    new CameraAlarmDoorInterlockMapping
                    {
                        Camera = new InterlockCamera { Ip = "10.0.0.5" },
                        DoorDevice = new InterlockDoorDevice { Id = 77 },
                        DoorNos = new List<int> { 1 }
                    }
                }
            };
            var resolver = new InterlockMappingResolver(options, registry);

            Assert.Equal(0, resolver.ResolveTargets("10.0.0.5").Count);
        }

        [TestCase]
        public static void Stage9Resolver_DoorIpCache_RefreshesAfterRegisteredDeviceIsReplaced()
        {
            var registry = RegisterDoorDevice(new DeviceRuntimeRegistry(), 10, "10.0.0.10");
            var resolver = new InterlockMappingResolver(Options("10.0.0.5", doorIp: "10.0.0.10"), registry);

            Assert.Equal(10, resolver.ResolveTargets("10.0.0.5")[0].DoorDeviceId);
            Assert.True(registry.RemoveDevice(10, new System.DateTime(2026, 1, 1)).Success);
            RegisterDoorDevice(registry, 11, "10.0.0.10");

            var targets = resolver.ResolveTargets("10.0.0.5");

            Assert.Equal(1, targets.Count);
            Assert.Equal(11, targets[0].DoorDeviceId);
        }

        [TestCase]
        public static void Stage9Resolver_EmptyDoorNos_DefaultsToSingleDoorOne()
        {
            var registry = RegisterDoorDevice(new DeviceRuntimeRegistry(), 10, "10.0.0.10");
            var options = Options("10.0.0.5", doorIp: "10.0.0.10");
            options.Mappings[0].DoorNos = new List<int>();
            var resolver = new InterlockMappingResolver(options, registry);

            var targets = resolver.ResolveTargets("10.0.0.5");

            Assert.Equal(1, targets.Count);
            Assert.Equal(1, targets[0].DoorNo);
        }

        [TestCase]
        public static void Stage9Resolver_MultipleDoorNos_ResolvesMultipleTargets()
        {
            var registry = RegisterDoorDevice(new DeviceRuntimeRegistry(), 10, "10.0.0.10");
            var options = Options("10.0.0.5", doorIp: "10.0.0.10");
            options.Mappings[0].DoorNos = new List<int> { 1, 2 };
            var resolver = new InterlockMappingResolver(options, registry);

            var targets = resolver.ResolveTargets("10.0.0.5");

            Assert.Equal(2, targets.Count);
            Assert.True(targets.Any(t => t.TargetKey == "10:1"));
            Assert.True(targets.Any(t => t.TargetKey == "10:2"));
        }

        [TestCase]
        public static void Stage9Resolver_TwoCamerasSharedDoor_ProduceSameTargetKey()
        {
            var registry = RegisterDoorDevice(new DeviceRuntimeRegistry(), 10, "10.0.0.10");
            var options = new CameraAlarmDoorInterlockOptions
            {
                Enabled = true,
                Mappings = new List<CameraAlarmDoorInterlockMapping>
                {
                    new CameraAlarmDoorInterlockMapping
                    {
                        Camera = new InterlockCamera { Ip = "10.0.0.5" },
                        DoorDevice = new InterlockDoorDevice { Ip = "10.0.0.10" },
                        DoorNos = new List<int> { 1 }
                    },
                    new CameraAlarmDoorInterlockMapping
                    {
                        Camera = new InterlockCamera { Ip = "10.0.0.6" },
                        DoorDevice = new InterlockDoorDevice { Ip = "10.0.0.10" },
                        DoorNos = new List<int> { 1 }
                    }
                }
            };
            var resolver = new InterlockMappingResolver(options, registry);

            var targetsA = resolver.ResolveTargets("10.0.0.5");
            var targetsB = resolver.ResolveTargets("10.0.0.6");

            Assert.Equal("10:1", targetsA[0].TargetKey);
            Assert.Equal("10:1", targetsB[0].TargetKey);
        }

        [TestCase]
        public static void Stage9Resolver_DisabledMapping_IsSkipped()
        {
            var registry = new DeviceRuntimeRegistry();
            var options = Options("10.0.0.5", doorIp: "10.0.0.10");
            options.Mappings[0].Enabled = false;
            var resolver = new InterlockMappingResolver(options, registry);

            Assert.False(resolver.HasValidMapping);
            Assert.Equal(0, resolver.ResolveTargets("10.0.0.5").Count);
            Assert.False(resolver.TryIdentifyCamera("10.0.0.5", null, null, null, out var key));
        }

        [TestCase]
        public static void Stage9Resolver_InvalidMapping_RecordsConfigurationError()
        {
            var registry = new DeviceRuntimeRegistry();
            var options = new CameraAlarmDoorInterlockOptions
            {
                Enabled = true,
                Mappings = new List<CameraAlarmDoorInterlockMapping>
                {
                    new CameraAlarmDoorInterlockMapping
                    {
                        Camera = new InterlockCamera(),
                        DoorDevice = new InterlockDoorDevice { Ip = "10.0.0.10" },
                        DoorNos = new List<int> { 1 }
                    }
                }
            };
            var resolver = new InterlockMappingResolver(options, registry);

            Assert.True(resolver.ConfigurationErrors.Count > 0);
            Assert.False(resolver.HasValidMapping);
        }

        [TestCase]
        public static void Stage9Resolver_TryIdentifyCamera_MatchesConfiguredIp()
        {
            var registry = new DeviceRuntimeRegistry();
            var resolver = new InterlockMappingResolver(Options("10.0.0.5", doorIp: "10.0.0.10"), registry);

            Assert.True(resolver.TryIdentifyCamera("10.0.0.5", null, null, null, out var key));
            Assert.Equal("10.0.0.5", key);
            Assert.False(resolver.TryIdentifyCamera("10.0.0.99", null, null, null, out key));
        }

        private static DeviceRuntimeRegistry RegisterDoorDevice(DeviceRuntimeRegistry registry, int deviceId, string ip)
        {
            registry.Register(new DeviceRuntimeCreationOptions
            {
                DeviceId = deviceId,
                DeviceName = "门禁-" + deviceId,
                IpAddress = ip,
                Port = 8000,
                Username = "admin",
                Password = "12345",
                Enabled = true,
                CreatedAt = new System.DateTime(2026, 1, 1)
            });
            return registry;
        }

        private static CameraAlarmDoorInterlockOptions Options(string cameraIp, string doorIp)
        {
            return new CameraAlarmDoorInterlockOptions
            {
                Enabled = true,
                WindowSeconds = 5,
                Mappings = new List<CameraAlarmDoorInterlockMapping>
                {
                    new CameraAlarmDoorInterlockMapping
                    {
                        Camera = new InterlockCamera { Ip = cameraIp },
                        DoorDevice = new InterlockDoorDevice { Ip = doorIp },
                        DoorNos = new List<int> { 1 }
                    }
                }
            };
        }
    }
}
