using System.Linq;
using ControlDoor.Devices.Runtime;
using ControlDoor.Hikvision;

namespace ControlEntradaSalida.Tests
{
    public static class Stage4LifecycleOrchestrationTests
    {
        [TestCase]
        public static void DeviceLifecycle_LoginSuccess_RegistersUserAndArmsAlarmThroughDispatcher()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);

                var login = fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                System.Threading.Thread.Sleep(100);
                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;

                Assert.True(login.Success);
                Assert.Equal(DeviceConnectionStatus.Online, snapshot.Status);
                Assert.True(snapshot.SdkUserId.HasValue);
                Assert.True(snapshot.AlarmHandle.HasValue);
                Assert.True(fixture.Gateway.Calls.Any(call => call.MethodName == "LoginAsync"));
                Assert.True(fixture.Gateway.Calls.Any(call => call.MethodName == "SetAlarmAsync"));
                Assert.True(fixture.Repository.Operations.Any(item => item == "UpdateLastUsedTime:1"));
            }
        }

        [TestCase]
        public static void DeviceLifecycle_LoginFailure_MarksOfflineAndSchedulesReconnect()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord(password: "wrong");
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);

                var login = fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-fail");
                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;
                var delayed = fixture.DelayedScheduler.GetSnapshot();

                Assert.False(login.Success);
                Assert.Equal(DeviceConnectionStatus.ReconnectPending, snapshot.Status);
                Assert.Equal("SDK_ERROR", snapshot.LastErrorCode);
                Assert.Equal(1, delayed.DelayedTaskCount);
            }
        }

        [TestCase]
        public static void DeviceLifecycle_Disconnect_ClearsAlarmUserAndMarksDisconnected()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                System.Threading.Thread.Sleep(100);

                var disconnect = fixture.Lifecycle.DisconnectDevice(1, "req-disconnect");
                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;

                Assert.True(disconnect.Success);
                Assert.Equal(DeviceConnectionStatus.Disconnected, snapshot.Status);
                Assert.False(snapshot.SdkUserId.HasValue);
                Assert.False(snapshot.AlarmHandle.HasValue);
                Assert.True(fixture.Gateway.Calls.Any(call => call.MethodName == "CloseAlarmAsync"));
                Assert.True(fixture.Gateway.Calls.Any(call => call.MethodName == "LogoutAsync"));
            }
        }

        [TestCase]
        public static void DeviceLifecycle_ReconnectForce_CleansOldSessionAndLogsInAgain()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                System.Threading.Thread.Sleep(100);
                var firstUser = fixture.Registry.TryGetByDeviceId(1).Snapshot.SdkUserId.Value;

                var reconnect = fixture.Lifecycle.ReconnectDevice(1, force: true, requestId: "req-reconnect");
                System.Threading.Thread.Sleep(100);
                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;

                Assert.True(reconnect.Success);
                Assert.True(snapshot.SdkUserId.HasValue);
                Assert.False(firstUser == snapshot.SdkUserId.Value);
                Assert.Equal(DeviceConnectionStatus.Online, snapshot.Status);
                Assert.True(fixture.Gateway.Calls.Count(call => call.MethodName == "LoginAsync") >= 2);
            }
        }

        [TestCase]
        public static void DeviceLifecycle_DeleteDevice_RemovesRuntimeAndDatabaseIdempotently()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);

                var first = fixture.Lifecycle.DeleteDevice(1, disconnectFirst: true, requestId: "req-delete");
                var second = fixture.Lifecycle.DeleteDevice(1, disconnectFirst: true, requestId: "req-delete2");

                Assert.True(first.Success);
                Assert.True(second.Success);
                Assert.False(fixture.Registry.TryGetByDeviceId(1).Found);
                Assert.True(fixture.Repository.Operations.Count(item => item == "DeleteDevice:1") >= 2);
            }
        }

        [TestCase]
        public static void DeviceLifecycle_ThreeHealthFailures_MarkOfflineAndScheduleReconnect()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                System.Threading.Thread.Sleep(100);
                fixture.Gateway.ConfigureException("GetDeviceInfoAsync", new DeviceGatewayException("Info", SdkError.FromCode(52)));

                fixture.Lifecycle.SubmitHealthCheck(1, wait: true, requestId: "h1");
                fixture.Lifecycle.SubmitHealthCheck(1, wait: true, requestId: "h2");
                fixture.Lifecycle.SubmitHealthCheck(1, wait: true, requestId: "h3");
                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;

                Assert.Equal(DeviceConnectionStatus.ReconnectPending, snapshot.Status);
                Assert.True(fixture.DelayedScheduler.GetSnapshot().DelayedTaskCount >= 1);
            }
        }

        [TestCase]
        public static void DeviceLifecycle_ReconnectAttemptsExhausted_MarksFailed()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.Options.MaxReconnectAttempts = 2;
                fixture.AddRecord(password: "wrong");
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);

                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "r1");
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "r2");
                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;

                Assert.Equal(DeviceConnectionStatus.Failed, snapshot.Status);
                Assert.Equal("FAILED", snapshot.LastErrorCode);
            }
        }
    }
}
