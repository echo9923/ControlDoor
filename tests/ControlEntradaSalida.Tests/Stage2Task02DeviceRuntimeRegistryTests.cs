using System;
using System.Linq;
using System.Threading.Tasks;
using ControlDoor.Devices.Runtime;

namespace ControlEntradaSalida.Tests
{
    public static class Stage2Task02DeviceRuntimeRegistryTests
    {
        [TestCase]
        public static void DeviceRuntimeRegistry_Register_AddsMainIpAndWorkerIndexes()
        {
            var registry = new DeviceRuntimeRegistry(new DeviceRuntimeRegistryOptions { WorkerCount = 4 });

            var result = registry.Register(NewOptions(5, " 192.168.1.20 "));

            Assert.True(result.Success);
            Assert.Equal("REGISTERED", result.Code);
            Assert.Equal(1, registry.GetRegistrySnapshot().DeviceCount);
            Assert.Equal(1, registry.GetRegistrySnapshot().IpIndexCount);
            Assert.Equal(1, registry.GetRegistrySnapshot().WorkerRouteIndexCount);
            Assert.Equal("192.168.1.20", registry.TryGetByDeviceId(5).Snapshot.IpAddress);
            Assert.Equal(5, registry.TryGetByIpAddress("192.168.1.20").Snapshot.DeviceId);
            Assert.Equal(result.WorkerIndex.Value, registry.TryGetWorkerRoute(5).WorkerIndex.Value);
        }

        [TestCase]
        public static void DeviceRuntimeRegistry_Register_RejectsDuplicateDeviceIdAndIp()
        {
            var registry = new DeviceRuntimeRegistry();
            registry.Register(NewOptions(1, "10.0.0.1"));

            var duplicateId = registry.Register(NewOptions(1, "10.0.0.2"));
            var duplicateIp = registry.Register(NewOptions(2, " 10.0.0.1 "));

            Assert.False(duplicateId.Success);
            Assert.Equal("DEVICE_ID_CONFLICT", duplicateId.Code);
            Assert.False(duplicateIp.Success);
            Assert.Equal("IP_CONFLICT", duplicateIp.Code);
            Assert.Equal(2, registry.GetRegistrySnapshot().ConflictCount);
            Assert.Equal(1, registry.GetAllSnapshots().Count);
        }

        [TestCase]
        public static void DeviceRuntimeRegistry_SdkUserIdIndex_UpdatesAndClears()
        {
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var registry = new DeviceRuntimeRegistry();
            registry.Register(NewOptions(1, "10.0.0.1"));

            var registered = registry.RegisterSdkUserId(1, 99, "SN001", now);
            var lookup = registry.TryGetBySdkUserId(99);
            var logout = registry.MarkLoggedOut(1, now.AddMinutes(1));

            Assert.True(registered.Success);
            Assert.True(lookup.Found);
            Assert.Equal(1, lookup.Snapshot.DeviceId);
            Assert.Equal(DeviceConnectionStatus.Online, registered.Snapshot.Status);
            Assert.True(logout.Success);
            Assert.False(registry.TryGetBySdkUserId(99).Found);
            Assert.Equal(DeviceConnectionStatus.Offline, logout.Snapshot.Status);
        }

        [TestCase]
        public static void DeviceRuntimeRegistry_SdkUserIdIndex_RejectsConflictingOwner()
        {
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var registry = new DeviceRuntimeRegistry();
            registry.Register(NewOptions(1, "10.0.0.1"));
            registry.Register(NewOptions(2, "10.0.0.2"));
            registry.RegisterSdkUserId(1, 77, "SN001", now);

            var conflict = registry.RegisterSdkUserId(2, 77, "SN002", now);

            Assert.False(conflict.Success);
            Assert.Equal("SDK_USER_ID_CONFLICT", conflict.Code);
            Assert.Equal(1, conflict.ConflictingDeviceId.Value);
            Assert.Equal(1, registry.TryGetBySdkUserId(77).Snapshot.DeviceId);
        }

        [TestCase]
        public static void DeviceRuntimeRegistry_AlarmHandleIndex_UpdatesAndClears()
        {
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var registry = new DeviceRuntimeRegistry();
            registry.Register(NewOptions(1, "10.0.0.1"));
            registry.RegisterSdkUserId(1, 11, "SN001", now);

            var armed = registry.RegisterAlarmHandle(1, 2001, now);
            var lookup = registry.TryGetByAlarmHandle(2001);
            var cleared = registry.ClearAlarmHandle(1, now.AddMinutes(1));

            Assert.True(armed.Success);
            Assert.True(lookup.Found);
            Assert.Equal(1, lookup.Snapshot.DeviceId);
            Assert.Equal(2001, armed.Snapshot.AlarmHandle.Value);
            Assert.True(cleared.Success);
            Assert.False(registry.TryGetByAlarmHandle(2001).Found);
            Assert.False(cleared.Snapshot.AlarmHandle.HasValue);
        }

        [TestCase]
        public static void DeviceRuntimeRegistry_RemoveDevice_ClearsAllReverseIndexes()
        {
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var registry = new DeviceRuntimeRegistry();
            registry.Register(NewOptions(1, "10.0.0.1"));
            registry.RegisterSdkUserId(1, 11, "SN001", now);
            registry.RegisterAlarmHandle(1, 22, now);

            var removed = registry.RemoveDevice(1, now.AddMinutes(1));

            Assert.True(removed.Success);
            Assert.Equal(DeviceRuntimeLookupStatus.Deleted, removed.Status);
            Assert.False(registry.TryGetByDeviceId(1).Found);
            Assert.False(registry.TryGetByIpAddress("10.0.0.1").Found);
            Assert.False(registry.TryGetBySdkUserId(11).Found);
            Assert.False(registry.TryGetByAlarmHandle(22).Found);
            Assert.Equal(0, registry.GetRegistrySnapshot().DeviceCount);
            Assert.Equal(1, registry.GetRegistrySnapshot().DeletedCount);
        }

        [TestCase]
        public static void DeviceRuntimeRegistry_GetAllSnapshots_ReturnsCopiesInDeviceOrder()
        {
            var registry = new DeviceRuntimeRegistry();
            registry.Register(NewOptions(2, "10.0.0.2"));
            registry.Register(NewOptions(1, "10.0.0.1"));
            registry.UpdateQueueInfo(1, new DeviceQueueInfo { WorkerIndex = 1, QueuedTaskCount = 3 });

            var snapshots = registry.GetAllSnapshots();
            snapshots[0].QueueInfo.QueuedTaskCount = 0;

            Assert.Equal(1, snapshots[0].DeviceId);
            Assert.Equal(2, snapshots[1].DeviceId);
            Assert.Equal(3, registry.TryGetByDeviceId(1).Snapshot.QueueInfo.QueuedTaskCount);
        }

        [TestCase]
        public static void DeviceRuntimeRegistry_ConcurrentRegister_DoesNotCreateHalfIndexes()
        {
            var registry = new DeviceRuntimeRegistry();

            Parallel.For(1, 50, i => registry.Register(NewOptions(i, "10.10.0." + i)));

            var snapshot = registry.GetRegistrySnapshot();
            Assert.Equal(49, snapshot.DeviceCount);
            Assert.Equal(49, snapshot.IpIndexCount);
            Assert.Equal(49, snapshot.WorkerRouteIndexCount);
            Assert.Equal(0, snapshot.ConflictCount);
            Assert.Equal(49, registry.GetAllSnapshots().Select(item => item.DeviceId).Distinct().Count());
        }

        private static DeviceRuntimeCreationOptions NewOptions(int deviceId, string ipAddress)
        {
            return new DeviceRuntimeCreationOptions
            {
                DeviceId = deviceId,
                DeviceName = "device-" + deviceId,
                IpAddress = ipAddress,
                Port = 8000,
                Username = "admin",
                Password = "pwd",
                Enabled = true,
                CreatedAt = new DateTime(2026, 1, 1, 8, 0, 0)
            };
        }
    }
}
