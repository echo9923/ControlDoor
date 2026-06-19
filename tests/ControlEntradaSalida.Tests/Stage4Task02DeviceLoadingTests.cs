using System.Linq;
using ControlDoor.Devices.Runtime;

namespace ControlEntradaSalida.Tests
{
    public static class Stage4Task02DeviceLoadingTests
    {
        [TestCase]
        public static void DeviceLifecycle_LoadEnabledDevices_RegistersOnlyEnabledDevices()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord(1, "10.0.4.1", enabled: true);
                fixture.AddRecord(2, "10.0.4.2", enabled: false);

                var summary = fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);

                Assert.Equal(1, summary.LoadedCount);
                Assert.Equal(1, fixture.Registry.GetAllSnapshots().Count);
                Assert.Equal(DeviceConnectionStatus.Loaded, fixture.Registry.TryGetByDeviceId(1).Snapshot.Status);
                Assert.False(fixture.Registry.TryGetByDeviceId(2).Found);
            }
        }

        [TestCase]
        public static void DeviceLifecycle_LoadEnabledDevices_RecordsInvalidAndConflictsWithoutBlockingValid()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord(1, "10.0.4.1", enabled: true);
                fixture.AddRecord(2, "10.0.4.1", enabled: true);
                fixture.AddRecord(3, "", enabled: true);

                var summary = fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);

                Assert.Equal(1, summary.LoadedCount);
                Assert.Equal(1, summary.ConflictCount);
                Assert.Equal(1, summary.InvalidCount);
                Assert.Equal(2, fixture.Registry.GetAllSnapshots().Count);
                Assert.Equal(DeviceConnectionStatus.InvalidConfig, fixture.Registry.TryGetByDeviceId(3).Snapshot.Status);
                Assert.True(summary.Warnings.Any(item => item.Contains("注册失败") || item.Contains("配置非法")));
            }
        }
        [TestCase]
        public static void DeviceLifecycle_LoadEnabledDevices_InvalidEnabledRecord_RegistersInvalidConfigAndDoesNotLogin()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord(1, "10.0.4.1", enabled: true);
                var record = fixture.Repository.GetByDeviceId(1);
                record.Port = 0;
                fixture.Repository.Add(record);

                var summary = fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: true);
                var lookup = fixture.Registry.TryGetByDeviceId(1);

                Assert.Equal(1, summary.InvalidCount);
                Assert.True(lookup.Found);
                Assert.Equal(DeviceConnectionStatus.InvalidConfig, lookup.Snapshot.Status);
                Assert.Equal("INVALID_CONFIG", lookup.Snapshot.LastErrorCode);
                Assert.False(fixture.Gateway.Calls.Any(call => call.MethodName == "LoginAsync"));
            }
        }
    }
}
