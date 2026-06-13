using System.IO;
using System.Linq;
using ControlDoor.Configuration;
using ControlDoor.Devices.Runtime;
using ControlDoor.Hikvision;
using ControlDoor.Observability;

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
        public static void DeviceLifecycle_AlarmDeployType_UsesClientDeployTypeZero()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.Options.AlarmDeployType = 0;
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);

                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                System.Threading.Thread.Sleep(100);
                var request = fixture.Gateway.Calls
                    .Where(call => call.MethodName == "SetAlarmAsync")
                    .Select(call => call.Request as AlarmSetupRequest)
                    .FirstOrDefault();

                Assert.NotNull(request);
                Assert.Equal(0, request.DeployType);
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

        [TestCase]
        public static void DeviceLifecycle_SuccessfulLoginAlarmAndDisconnect_WriteLifecycleSuccessLogs()
        {
            var runDirectory = TestWorkspace.Create();
            using (var logger = new ServiceLogger(LogOptions.FromSettings(runDirectory, new LoggingOptions { LogDirectory = "logs" })))
            using (var fixture = new Stage4Fixture(logger))
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);

                var login = fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-log-success");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue, "alarm was not armed.");
                var disconnect = fixture.Lifecycle.DisconnectDevice(1, "req-log-disconnect");

                var text = File.ReadAllText(logger.CurrentLogPath);
                Assert.True(login.Success);
                Assert.True(disconnect.Success);
                Assert.Contains("message=\"设备登录成功。\"", text);
                Assert.Contains("message=\"设备布防成功。\"", text);
                Assert.Contains("message=\"设备撤防成功。\"", text);
                Assert.Contains("message=\"设备登出成功。\"", text);
                Assert.Contains("component=\"DeviceLifecycle\"", text);
                Assert.Contains("deviceId=\"1\"", text);
                Assert.Contains("requestId=\"req-log-success\"", text);
                Assert.Contains("operationName=\"DeviceLogin\"", text);
                Assert.Contains("operationName=\"DeviceArmAlarm\"", text);
                Assert.Contains("operationName=\"ManualDisconnect\"", text);
                Assert.Contains("userId=", text);
                Assert.Contains("alarmHandle=", text);
                Assert.False(text.Contains("password"));
                Assert.False(text.Contains("12345"));
            }
        }

        private static void WaitUntil(System.Func<bool> condition, string message)
        {
            var deadline = System.DateTime.UtcNow.AddSeconds(2);
            while (System.DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                System.Threading.Thread.Sleep(20);
            }

            Assert.True(condition(), message);
        }
    }
}
