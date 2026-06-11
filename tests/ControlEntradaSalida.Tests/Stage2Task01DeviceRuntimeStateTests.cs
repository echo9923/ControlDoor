using System;
using System.Linq;
using ControlDoor.Devices.Runtime;

namespace ControlEntradaSalida.Tests
{
    public static class Stage2Task01DeviceRuntimeStateTests
    {
        [TestCase]
        public static void DeviceRuntimeState_InitialStatus_ReflectsEnabledFlag()
        {
            var now = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
            var enabled = NewState(1, "front", "192.168.1.10", true, now);
            var disabled = NewState(2, "side", "192.168.1.11", false, now);

            Assert.Equal(DeviceConnectionStatus.Unknown, enabled.ToSnapshot().Status);
            Assert.Equal(DeviceConnectionStatus.Disabled, disabled.ToSnapshot().Status);
            Assert.Equal(now, enabled.ToSnapshot().UpdatedAt);
        }

        [TestCase]
        public static void DeviceRuntimeState_StatusEnum_CoversStage2States()
        {
            var names = Enum.GetNames(typeof(DeviceConnectionStatus)).ToList();

            Assert.True(names.Contains("Unknown"));
            Assert.True(names.Contains("Disabled"));
            Assert.True(names.Contains("Offline"));
            Assert.True(names.Contains("Connecting"));
            Assert.True(names.Contains("Online"));
            Assert.True(names.Contains("Degraded"));
            Assert.True(names.Contains("Disconnecting"));
            Assert.True(names.Contains("Faulted"));
            Assert.True(names.Contains("Deleted"));
        }

        [TestCase]
        public static void DeviceRuntimeState_Snapshot_DoesNotExposeMutableRuntimeObjects()
        {
            var state = NewState(3, "front", "10.0.0.5", true, DateTime.UtcNow);
            state.MarkCapabilities(new DeviceCapabilities { Known = true, SupportsAcs = true }, DateTime.UtcNow);

            var snapshot = state.ToSnapshot();
            snapshot.Capabilities.SupportsAcs = false;

            Assert.True(state.ToSnapshot().Capabilities.SupportsAcs);
            Assert.Equal("10.0.0.5", snapshot.IpAddress);
        }

        [TestCase]
        public static void DeviceRuntimeState_LoginLogoutAndError_UpdateSnapshotFields()
        {
            var state = NewState(4, "front", "10.0.0.8", true, DateTime.UtcNow);
            var loginAt = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
            var errorAt = loginAt.AddMinutes(1);
            var logoutAt = loginAt.AddMinutes(2);

            state.MarkLoginSucceeded(99, "SN001", loginAt);
            var online = state.ToSnapshot();
            Assert.True(online.IsConnected);
            Assert.Equal(99, online.SdkUserId.Value);
            Assert.Equal("SN001", online.SerialNumber);
            Assert.Equal(loginAt, online.LastLoginAt.Value);

            state.RecordError(DeviceRuntimeError.Create("SetupAlarm", "SDK_ERROR", "alarm failed", errorAt, "req-1", 17, true), errorAt, DeviceConnectionStatus.Degraded);
            var degraded = state.ToSnapshot();
            Assert.True(degraded.IsConnected);
            Assert.Equal(DeviceConnectionStatus.Degraded, degraded.Status);
            Assert.Equal("SDK_ERROR", degraded.LastErrorCode);
            Assert.Equal("alarm failed", degraded.LastErrorMessage);

            state.MarkLoggedOut(logoutAt);
            var offline = state.ToSnapshot();
            Assert.False(offline.IsConnected);
            Assert.Equal(DeviceConnectionStatus.Offline, offline.Status);
            Assert.False(offline.SdkUserId.HasValue);
            Assert.False(offline.AlarmHandle.HasValue);
            Assert.Equal(logoutAt, offline.LastLogoutAt.Value);
        }

        [TestCase]
        public static void DeviceRuntimeState_ReconnectAndQueueInfo_AreCopiedToSnapshot()
        {
            var state = NewState(5, "front", "10.0.0.9", true, DateTime.UtcNow);
            var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
            state.MarkLoginFailed(DeviceRuntimeError.Create("Login", "SDK_ERROR", "failed", now, sdkErrorCode: 7, retryable: true), now);
            state.MarkLoginFailed(DeviceRuntimeError.Create("Login", "SDK_ERROR", "failed", now.AddSeconds(1), sdkErrorCode: 7, retryable: true), now.AddSeconds(1));
            state.MarkLoginFailed(DeviceRuntimeError.Create("Login", "SDK_ERROR", "failed", now.AddSeconds(2), sdkErrorCode: 7, retryable: true), now.AddSeconds(2));
            state.SetManualDisconnected(true, "network", now.AddSeconds(3));

            var snapshot = state.ToSnapshot(new DeviceQueueInfo { WorkerIndex = 2, QueuedTaskCount = 7, CurrentTaskId = "task-1" });
            snapshot.Reconnect.AttemptCount = 0;
            snapshot.QueueInfo.QueuedTaskCount = 0;

            var second = state.ToSnapshot(new DeviceQueueInfo { WorkerIndex = 2, QueuedTaskCount = 7, CurrentTaskId = "task-1" });
            Assert.Equal(3, second.Reconnect.AttemptCount);
            Assert.True(second.Reconnect.InCooldown);
            Assert.Equal("network", second.Reconnect.CooldownReason);
            Assert.Equal(7, second.QueueInfo.QueuedTaskCount);
            Assert.Equal(2, second.QueueInfo.WorkerIndex);
        }

        private static DeviceRuntimeState NewState(int deviceId, string name, string ipAddress, bool enabled, DateTime now)
        {
            return new DeviceRuntimeState(new DeviceRuntimeCreationOptions
            {
                DeviceId = deviceId,
                DeviceName = name,
                IpAddress = ipAddress,
                Port = 8000,
                Username = "admin",
                Password = "pwd",
                Enabled = enabled,
                CreatedAt = now
            });
        }
    }
}
