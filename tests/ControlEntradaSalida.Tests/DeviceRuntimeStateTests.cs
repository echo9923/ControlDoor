using System;
using ControlDoor.Devices.Runtime;

namespace ControlEntradaSalida.Tests
{
    public static class DeviceRuntimeStateTests
    {
        [TestCase]
        public static void DeviceRuntimeState_NewEnabledDevice_StartsAsLoaded()
        {
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var state = NewState(now: now);

            var snapshot = state.ToSnapshot();

            Assert.Equal(DeviceConnectionStatus.Loaded, snapshot.Status);
            Assert.False(snapshot.IsConnected);
            Assert.Equal(now, snapshot.UpdatedAt);
            Assert.Equal(8000, snapshot.Port);
            Assert.Equal("10.0.0.8", snapshot.IpAddress);
        }

        [TestCase]
        public static void DeviceRuntimeState_DisabledDevice_StartsAsDisabled()
        {
            var state = NewState(enabled: false);

            Assert.Equal(DeviceConnectionStatus.Disabled, state.ToSnapshot().Status);
        }

        [TestCase]
        public static void DeviceRuntimeState_LoginSuccess_UpdatesHandlesAndReconnect()
        {
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var state = NewState(now: now);

            state.MarkLoginFailed(DeviceRuntimeError.Create("Login", "SDK_ERROR", "failed", now, sdkErrorCode: 7, retryable: true), now);
            state.MarkLoginSucceeded(12, "SERIAL-1", now.AddSeconds(5));
            var snapshot = state.ToSnapshot();

            Assert.Equal(DeviceConnectionStatus.Online, snapshot.Status);
            Assert.True(snapshot.IsConnected);
            Assert.Equal(12, snapshot.SdkUserId.Value);
            Assert.Equal("SERIAL-1", snapshot.SerialNumber);
            Assert.Equal(0, snapshot.Reconnect.AttemptCount);
            Assert.Equal(null, snapshot.LastError);
        }

        [TestCase]
        public static void DeviceRuntimeState_SnapshotDoesNotExposeMutableReferences()
        {
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var state = NewState(now: now);
            state.MarkCapabilities(new DeviceCapabilities { SupportsAcs = true, SupportsIsapi = true }, now);
            state.RecordError(DeviceRuntimeError.Create("Probe", "DEVICE_ERROR", "old", now), now);

            var snapshot = state.ToSnapshot();
            snapshot.Capabilities.SupportsAcs = false;
            snapshot.LastError.Message = "changed";

            var second = state.ToSnapshot();
            Assert.True(second.Capabilities.SupportsAcs);
            Assert.Equal("old", second.LastError.Message);
        }

        [TestCase]
        public static void DeviceRuntimeState_DeletingAndDeleted_ClearRuntimeHandles()
        {
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var state = NewState(now: now);
            state.MarkLoginSucceeded(21, "SERIAL", now);
            state.MarkAlarmArmed(31, now);

            state.MarkDeleting(now.AddSeconds(1));
            Assert.Equal(DeviceConnectionStatus.Disconnecting, state.ToSnapshot().Status);

            state.MarkDeleted(now.AddSeconds(2));
            var snapshot = state.ToSnapshot();
            Assert.Equal(DeviceConnectionStatus.Deleted, snapshot.Status);
            Assert.True(snapshot.IsDeleting);
            Assert.Equal(null, snapshot.SdkUserId);
            Assert.Equal(null, snapshot.AlarmHandle);
        }

        [TestCase]
        public static void DeviceRuntimeState_CoversAllConnectionStatuses()
        {
            Assert.Equal(14, Enum.GetValues(typeof(DeviceConnectionStatus)).Length);
            Assert.True(Enum.IsDefined(typeof(DeviceConnectionStatus), DeviceConnectionStatus.Unknown));
            Assert.True(Enum.IsDefined(typeof(DeviceConnectionStatus), DeviceConnectionStatus.Disabled));
            Assert.True(Enum.IsDefined(typeof(DeviceConnectionStatus), DeviceConnectionStatus.Offline));
            Assert.True(Enum.IsDefined(typeof(DeviceConnectionStatus), DeviceConnectionStatus.Connecting));
            Assert.True(Enum.IsDefined(typeof(DeviceConnectionStatus), DeviceConnectionStatus.Online));
            Assert.True(Enum.IsDefined(typeof(DeviceConnectionStatus), DeviceConnectionStatus.Degraded));
            Assert.True(Enum.IsDefined(typeof(DeviceConnectionStatus), DeviceConnectionStatus.Disconnecting));
            Assert.True(Enum.IsDefined(typeof(DeviceConnectionStatus), DeviceConnectionStatus.Faulted));
            Assert.True(Enum.IsDefined(typeof(DeviceConnectionStatus), DeviceConnectionStatus.Deleted));
            Assert.True(Enum.IsDefined(typeof(DeviceConnectionStatus), DeviceConnectionStatus.Loaded));
            Assert.True(Enum.IsDefined(typeof(DeviceConnectionStatus), DeviceConnectionStatus.InvalidConfig));
            Assert.True(Enum.IsDefined(typeof(DeviceConnectionStatus), DeviceConnectionStatus.ReconnectPending));
            Assert.True(Enum.IsDefined(typeof(DeviceConnectionStatus), DeviceConnectionStatus.Disconnected));
            Assert.True(Enum.IsDefined(typeof(DeviceConnectionStatus), DeviceConnectionStatus.Failed));
        }

        private static DeviceRuntimeState NewState(bool enabled = true, DateTime? now = null)
        {
            return new DeviceRuntimeState(new DeviceRuntimeCreationOptions
            {
                DeviceId = 8,
                DeviceName = "east gate",
                IpAddress = "10.0.0.8",
                Port = 8000,
                Username = "admin",
                Password = "secret",
                Enabled = enabled,
                CreatedAt = now
            });
        }
    }
}
