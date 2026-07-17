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
        public static void DeviceLifecycle_Disconnect_CloseAlarmFails_PreservesAlarmHandle()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue, "alarm was not armed.");
                var originalHandle = fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.Value;
                fixture.Gateway.ConfigureException("CloseAlarmAsync", new DeviceGatewayException("CloseAlarm", SdkError.FromCode(17, "close failed")));

                var disconnect = fixture.Lifecycle.DisconnectDevice(1, "req-disconnect-fail");
                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;

                Assert.False(disconnect.Success);
                Assert.Equal(originalHandle, snapshot.AlarmHandle.Value);
                Assert.Equal("SDK_ERROR", snapshot.LastErrorCode);
            }
        }

        [TestCase]
        public static void DeviceLifecycle_DeleteDeviceDisconnectFirst_CloseAlarmFails_PreservesAlarmHandle()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue, "alarm was not armed.");
                var originalHandle = fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.Value;
                fixture.Gateway.ConfigureException("CloseAlarmAsync", new DeviceGatewayException("CloseAlarm", SdkError.FromCode(17, "close failed")));

                var deleted = fixture.Lifecycle.DeleteDevice(1, disconnectFirst: true, requestId: "req-delete-fail");
                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;

                Assert.False(deleted.Success);
                Assert.Equal(originalHandle, snapshot.AlarmHandle.Value);
                Assert.Equal("SDK_ERROR", snapshot.LastErrorCode);
            }
        }

        [TestCase]
        public static void DeviceLifecycle_ForceReconnect_CloseAlarmFails_PreservesAlarmHandleAndSkipsLogin()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue, "alarm was not armed.");
                var originalHandle = fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.Value;
                var loginCount = fixture.Gateway.Calls.Count(call => call.MethodName == "LoginAsync");
                fixture.Gateway.ConfigureException("CloseAlarmAsync", new DeviceGatewayException("CloseAlarm", SdkError.FromCode(17, "close failed")));

                var reconnect = fixture.Lifecycle.ReconnectDevice(1, force: true, requestId: "req-reconnect-fail");
                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;

                Assert.False(reconnect.Success);
                Assert.Equal(originalHandle, snapshot.AlarmHandle.Value);
                Assert.Equal(loginCount, fixture.Gateway.Calls.Count(call => call.MethodName == "LoginAsync"));
            }
        }

        [TestCase]
        public static void DeviceLifecycle_DisarmDeviceAlarm_ClearsAlarmWithoutLogout()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue, "alarm was not armed.");
                var userId = fixture.Registry.TryGetByDeviceId(1).Snapshot.SdkUserId.Value;

                var disarm = fixture.Lifecycle.DisarmDeviceAlarm(1, "req-disarm");
                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;

                Assert.True(disarm.Success);
                Assert.Equal(DeviceConnectionStatus.Online, snapshot.Status);
                Assert.Equal(userId, snapshot.SdkUserId.Value);
                Assert.False(snapshot.AlarmHandle.HasValue);
                Assert.True(snapshot.AlarmManuallyDisarmed);
                Assert.True(fixture.Gateway.Calls.Any(call => call.MethodName == "CloseAlarmAsync"));
                Assert.Equal(0, fixture.Gateway.Calls.Count(call => call.MethodName == "LogoutAsync"));
            }
        }

        [TestCase]
        public static void DeviceLifecycle_DisarmDeviceAlarm_CloseAlarmFails_PreservesAlarmHandleAndManualFlag()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue, "alarm was not armed.");
                var originalHandle = fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.Value;
                fixture.Gateway.ConfigureException("CloseAlarmAsync", new DeviceGatewayException("CloseAlarm", SdkError.FromCode(17, "close failed")));

                var disarm = fixture.Lifecycle.DisarmDeviceAlarm(1, "req-disarm-fail");
                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;

                Assert.False(disarm.Success);
                Assert.Equal("SDK_ERROR", disarm.Code);
                Assert.Equal(originalHandle, snapshot.AlarmHandle.Value);
                Assert.False(snapshot.AlarmManuallyDisarmed);
                Assert.Equal("SDK_ERROR", snapshot.LastErrorCode);
            }
        }

        [TestCase]
        public static void DeviceLifecycle_DisarmDeviceAlarm_IsIdempotentWhenAlreadyDisarmed()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue, "alarm was not armed.");
                fixture.Lifecycle.DisarmDeviceAlarm(1, "req-disarm-1");
                var closeCount = fixture.Gateway.Calls.Count(call => call.MethodName == "CloseAlarmAsync");

                var disarm = fixture.Lifecycle.DisarmDeviceAlarm(1, "req-disarm-2");
                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;

                Assert.True(disarm.Success);
                Assert.Equal(DeviceConnectionStatus.Online, snapshot.Status);
                Assert.False(snapshot.AlarmHandle.HasValue);
                Assert.True(snapshot.AlarmManuallyDisarmed);
                Assert.Equal(closeCount, fixture.Gateway.Calls.Count(call => call.MethodName == "CloseAlarmAsync"));
            }
        }

        [TestCase]
        public static void DeviceLifecycle_RearmDeviceAlarmForce_ClosesOldAlarmAndSetsNewAlarm()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue, "alarm was not armed.");
                var oldHandle = fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.Value;

                var rearm = fixture.Lifecycle.RearmDeviceAlarm(1, force: true, requestId: "req-rearm");
                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;
                var closeIndex = FindFirstCallIndex(fixture, "CloseAlarmAsync");
                var lastSetIndex = FindLastCallIndex(fixture, "SetAlarmAsync");

                Assert.True(rearm.Success);
                Assert.True(snapshot.AlarmHandle.HasValue);
                Assert.False(snapshot.AlarmHandle.Value == oldHandle);
                Assert.Equal(DeviceConnectionStatus.Online, snapshot.Status);
                Assert.True(closeIndex >= 0);
                Assert.True(lastSetIndex > closeIndex);
            }
        }

        [TestCase]
        public static void DeviceLifecycle_RearmDeviceAlarmForce_CloseOldAlarmFails_DoesNotSetNewAlarm()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue, "alarm was not armed.");
                var originalHandle = fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.Value;
                var setCount = fixture.Gateway.Calls.Count(call => call.MethodName == "SetAlarmAsync");
                fixture.Gateway.ConfigureException("CloseAlarmAsync", new DeviceGatewayException("CloseAlarm", SdkError.FromCode(17, "close failed")));

                var rearm = fixture.Lifecycle.RearmDeviceAlarm(1, force: true, requestId: "req-rearm-fail");
                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;

                Assert.False(rearm.Success);
                Assert.Equal("SDK_ERROR", rearm.Code);
                Assert.Equal(originalHandle, snapshot.AlarmHandle.Value);
                Assert.False(snapshot.AlarmManuallyDisarmed);
                Assert.Equal(setCount, fixture.Gateway.Calls.Count(call => call.MethodName == "SetAlarmAsync"));
            }
        }

        [TestCase]
        public static void DeviceLifecycle_RearmDeviceAlarmWithoutForce_KeepsExistingAlarm()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue, "alarm was not armed.");
                var oldHandle = fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.Value;
                var setCount = fixture.Gateway.Calls.Count(call => call.MethodName == "SetAlarmAsync");

                var rearm = fixture.Lifecycle.RearmDeviceAlarm(1, force: false, requestId: "req-rearm");
                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;

                Assert.True(rearm.Success);
                Assert.Equal(oldHandle, snapshot.AlarmHandle.Value);
                Assert.Equal(setCount, fixture.Gateway.Calls.Count(call => call.MethodName == "SetAlarmAsync"));
                Assert.Equal(0, fixture.Gateway.Calls.Count(call => call.MethodName == "CloseAlarmAsync"));
            }
        }

        [TestCase]
        public static void DeviceLifecycle_RearmDeviceAlarmOffline_ReturnsDeviceErrorWithoutLogin()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);

                var rearm = fixture.Lifecycle.RearmDeviceAlarm(1, force: true, requestId: "req-rearm-offline");
                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;

                Assert.False(rearm.Success);
                Assert.Equal("DEVICE_ERROR", rearm.Code);
                Assert.Equal(DeviceConnectionStatus.Loaded, snapshot.Status);
                Assert.Equal(0, fixture.Gateway.Calls.Count(call => call.MethodName == "LoginAsync"));
                Assert.Equal(0, fixture.Gateway.Calls.Count(call => call.MethodName == "SetAlarmAsync"));
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
        public static void DeviceLifecycle_DeleteDevice_RemovesRuntimeAndRepositoryIdempotently()
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
        public static void DeviceLifecycle_RegisterDevice_RuntimeIpConflict_DoesNotPersistRepository()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord(1, "10.0.4.1", enabled: true);
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);

                var result = fixture.Lifecycle.RegisterDevice(NewRecord(2, "10.0.4.1"), persist: true);

                Assert.False(result.Success);
                Assert.Equal("INVALID_ARGUMENT", result.Code);
                Assert.False(fixture.Repository.ExistsDeviceId(2));
                Assert.False(fixture.Repository.Operations.Any(item => item == "InsertDevice:2"));
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
        public static void DeviceLifecycle_ReconnectAfterPassiveOffline_ClosesStaleAlarmBeforeRearm()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.Options.ReconnectBaseDelayMs = 10;
                fixture.Options.ReconnectMaxDelayMs = 10;
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);

                fixture.DelayedScheduler.Start();
                var setAlarmCount = 0;
                var staleAlarmClosed = false;
                fixture.Gateway.ConfigureResult("SetAlarmAsync", request =>
                {
                    setAlarmCount++;
                    if (setAlarmCount == 1)
                    {
                        return new AlarmSetupResponse { AlarmHandle = 900 };
                    }

                    if (!staleAlarmClosed)
                    {
                        throw new DeviceGatewayException("SetAlarm", SdkError.FromCode(1924, "Deploy exceed max"));
                    }

                    return new AlarmSetupResponse { AlarmHandle = 901 };
                });
                fixture.Gateway.ConfigureResult("CloseAlarmAsync", request =>
                {
                    var close = request as AlarmCloseRequest;
                    if (close != null && close.AlarmHandle == 900)
                    {
                        staleAlarmClosed = true;
                    }

                    return 0;
                });

                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle == 900, "initial alarm was not armed.");
                fixture.Gateway.ConfigureException("GetDeviceInfoAsync", new DeviceGatewayException("Info", SdkError.FromCode(7)));

                fixture.Lifecycle.SubmitHealthCheck(1, wait: true, requestId: "h1");
                fixture.Lifecycle.SubmitHealthCheck(1, wait: true, requestId: "h2");
                fixture.Lifecycle.SubmitHealthCheck(1, wait: true, requestId: "h3");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.Status == DeviceConnectionStatus.ReconnectPending, "reconnect was not scheduled.");
                fixture.Gateway.ConfigureResult("GetDeviceInfoAsync", fixture.Gateway.DeviceInfo);
                WaitUntil(() => fixture.Gateway.Calls.Count(call => call.MethodName == "LoginAsync") >= 2, "reconnect login was not attempted.");
                WaitUntil(() => fixture.Gateway.Calls.Count(call => call.MethodName == "SetAlarmAsync") >= 2, "rearm was not attempted.");

                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;

                Assert.True(staleAlarmClosed);
                Assert.True(snapshot.AlarmHandle.HasValue);
                Assert.Equal(901, snapshot.AlarmHandle.Value);
                Assert.Equal(DeviceConnectionStatus.Online, snapshot.Status);
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

        // 默认 MaxReconnectAttempts=0 表示无限重试：连续登录失败后设备仍处于重连等待，不进入 Failed 终态。
        [TestCase]
        public static void DeviceLifecycle_ReconnectUnlimited_NeverMarksFailed()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.Options.MaxReconnectAttempts = 0;
                fixture.AddRecord(password: "wrong");
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);

                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "r1");
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "r2");
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "r3");
                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;

                Assert.True(snapshot.Status != DeviceConnectionStatus.Failed, "默认无限重连不应进入 Failed 终态。");
                Assert.True(fixture.DelayedScheduler.GetSnapshot().GetSourceCount("Stage4Reconnect") >= 1);
            }
        }

        // 布防失败后按指数退避无限重试：登录成功但 SetAlarmAsync 抛异常时，应投递 stage4:rearm 延迟任务。
        [TestCase]
        public static void DeviceLifecycle_ArmFailure_SchedulesReArmRetry()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.Options.ReArmBaseDelayMs = 10000;
                fixture.Options.ReArmMaxDelayMs = 60000;
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                // 登录成功，但布防阶段抛异常，触发 ReArm 重试调度。
                fixture.Gateway.ConfigureException("SetAlarmAsync", new DeviceGatewayException("Arm", SdkError.FromCode(7)));
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                WaitUntil(() => fixture.Gateway.Calls.Any(call => call.MethodName == "SetAlarmAsync"), "alarm was not attempted.");
                WaitUntil(() => fixture.DelayedScheduler.GetSnapshot().GetSourceCount("Stage4ReArm") >= 1, "rearm was not scheduled.");

                Assert.True(fixture.DelayedScheduler.GetSnapshot().GetSourceCount("Stage4ReArm") >= 1);
                Assert.True(fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue == false);
            }
        }

        [TestCase]
        public static void DeviceLifecycle_HealthCheckAlarmProbeArmed_DoesNotScheduleReArm()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.Options.ReArmBaseDelayMs = 10000;
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue, "alarm was not armed.");
                var before = fixture.DelayedScheduler.GetSnapshot().GetSourceCount("Stage4ReArm");

                var check = fixture.Lifecycle.SubmitHealthCheck(1, wait: true, requestId: "req-health");

                Assert.True(check.Success);
                Assert.True(fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue);
                Assert.Equal(before, fixture.DelayedScheduler.GetSnapshot().GetSourceCount("Stage4ReArm"));
                Assert.True(fixture.Gateway.Calls.Any(call => call.MethodName == "GetAlarmDeploymentStatusAsync"));
            }
        }

        [TestCase]
        public static void DeviceLifecycle_HealthCheckAlarmProbeDisarmed_KeepsHandleAndDoesNotScheduleReArm()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.Options.ReArmBaseDelayMs = 10000;
                fixture.Options.AlarmStatusProbeFailureThreshold = 2;
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue, "alarm was not armed.");
                fixture.Gateway.AlarmDeploymentStatus = new AlarmDeploymentStatus
                {
                    Known = true,
                    IsDeployed = false,
                    RawSetupAlarmStatus = 0,
                    RawSummary = "not deployed"
                };
                var before = fixture.DelayedScheduler.GetSnapshot().GetSourceCount("Stage4ReArm");

                fixture.Lifecycle.SubmitHealthCheck(1, wait: true, requestId: "probe-1");
                Assert.True(fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue);
                Assert.Equal(DeviceConnectionStatus.Degraded, fixture.Registry.TryGetByDeviceId(1).Snapshot.Status);

                fixture.Lifecycle.SubmitHealthCheck(1, wait: true, requestId: "probe-2");
                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;

                Assert.True(snapshot.AlarmHandle.HasValue);
                Assert.Equal(DeviceConnectionStatus.Degraded, snapshot.Status);
                Assert.Equal(before, fixture.DelayedScheduler.GetSnapshot().GetSourceCount("Stage4ReArm"));
            }
        }

        [TestCase]
        public static void DeviceLifecycle_HealthCheckAlarmProbeFailure_KeepsAlarmHandle()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.Options.ReArmBaseDelayMs = 10000;
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue, "alarm was not armed.");
                var before = fixture.DelayedScheduler.GetSnapshot().GetSourceCount("Stage4ReArm");
                fixture.Gateway.ConfigureException("GetAlarmDeploymentStatusAsync", new DeviceGatewayException("Probe", SdkError.FromCode(52)));

                fixture.Lifecycle.SubmitHealthCheck(1, wait: true, requestId: "probe-fail");
                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;

                Assert.True(snapshot.AlarmHandle.HasValue);
                Assert.Equal(DeviceConnectionStatus.Degraded, snapshot.Status);
                Assert.Equal(before, fixture.DelayedScheduler.GetSnapshot().GetSourceCount("Stage4ReArm"));
            }
        }

        [TestCase]
        public static void DeviceLifecycle_ManualDisarm_PreventsAutomaticHealthProbeReArm()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.Options.ReArmBaseDelayMs = 10000;
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue, "alarm was not armed.");

                var disarm = fixture.Lifecycle.DisarmDeviceAlarm(1, "req-disarm");
                var before = fixture.DelayedScheduler.GetSnapshot().GetSourceCount("Stage4ReArm");
                var check = fixture.Lifecycle.SubmitHealthCheck(1, wait: true, requestId: "req-health");
                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;

                Assert.True(disarm.Success);
                Assert.True(check.Success);
                Assert.True(snapshot.IsConnected);
                Assert.False(snapshot.AlarmHandle.HasValue);
                Assert.True(snapshot.AlarmManuallyDisarmed);
                Assert.Equal(before, fixture.DelayedScheduler.GetSnapshot().GetSourceCount("Stage4ReArm"));
            }
        }

        [TestCase]
        public static void DeviceLifecycle_RearmAfterManualDisarm_ClearsManualMarkerAndArms()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue, "alarm was not armed.");
                fixture.Lifecycle.DisarmDeviceAlarm(1, "req-disarm");

                var rearm = fixture.Lifecycle.RearmDeviceAlarm(1, force: false, requestId: "req-rearm");
                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;

                Assert.True(rearm.Success);
                Assert.True(snapshot.AlarmHandle.HasValue);
                Assert.False(snapshot.AlarmManuallyDisarmed);
            }
        }

        [TestCase]
        public static void DeviceLifecycle_ManualDisconnect_PreventsAutomaticHealthReconnect()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.Options.FailureThreshold = 1;
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.SdkUserId.HasValue, "device was not logged in.");

                var disconnect = fixture.Lifecycle.DisconnectDevice(1, "req-disconnect");
                var before = fixture.DelayedScheduler.GetSnapshot().GetSourceCount("Stage4Reconnect");
                var check = fixture.Lifecycle.SubmitHealthCheck(1, wait: true, requestId: "req-health-after-disconnect");
                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;

                Assert.True(disconnect.Success);
                Assert.True(check.Success);
                Assert.Equal(DeviceConnectionStatus.Disconnected, snapshot.Status);
                Assert.True(snapshot.Reconnect.ManualDisconnected);
                Assert.Equal(before, fixture.DelayedScheduler.GetSnapshot().GetSourceCount("Stage4Reconnect"));
            }
        }

        // 断开设备时应取消挂起的布防重试：ReArm 投递后再 Disconnect，CancelledDelayedTaskCount 增加。
        [TestCase]
        public static void DeviceLifecycle_Disconnect_CancelsPendingReArm()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.Options.ReArmBaseDelayMs = 10000;
                fixture.Options.ReArmMaxDelayMs = 60000;
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Gateway.ConfigureException("SetAlarmAsync", new DeviceGatewayException("Arm", SdkError.FromCode(7)));
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                WaitUntil(() => fixture.DelayedScheduler.GetSnapshot().GetSourceCount("Stage4ReArm") >= 1, "rearm was not scheduled.");

                var beforeCancel = fixture.DelayedScheduler.GetSnapshot().CancelledDelayedTaskCount;
                fixture.Lifecycle.DisconnectDevice(1, "req-disconnect");
                var snapshot = fixture.DelayedScheduler.GetSnapshot();

                Assert.Equal(DeviceConnectionStatus.Disconnected, fixture.Registry.TryGetByDeviceId(1).Snapshot.Status);
                Assert.True(snapshot.CancelledDelayedTaskCount > beforeCancel);
            }
        }

        [TestCase]
        public static void DeviceLifecycle_DisarmDeviceAlarm_CancelsPendingReArm()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.Options.ReArmBaseDelayMs = 10000;
                fixture.Options.ReArmMaxDelayMs = 60000;
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Gateway.ConfigureException("SetAlarmAsync", new DeviceGatewayException("Arm", SdkError.FromCode(7)));
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                WaitUntil(() => fixture.DelayedScheduler.GetSnapshot().GetSourceCount("Stage4ReArm") >= 1, "rearm was not scheduled.");

                var beforeCancel = fixture.DelayedScheduler.GetSnapshot().CancelledDelayedTaskCount;
                var disarm = fixture.Lifecycle.DisarmDeviceAlarm(1, "req-disarm");
                var snapshot = fixture.DelayedScheduler.GetSnapshot();

                Assert.True(disarm.Success);
                Assert.Equal(DeviceConnectionStatus.Degraded, fixture.Registry.TryGetByDeviceId(1).Snapshot.Status);
                Assert.True(snapshot.CancelledDelayedTaskCount > beforeCancel);
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

        // P1 回归：被动离线后重连登录成功，但关闭旧布防失败时，必须登出新登录会话并保留旧布防句柄。
        [TestCase]
        public static void DeviceLifecycle_ReconnectStaleCloseFailure_LogsOutNewSessionAndKeepsStaleHandle()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.Options.ReconnectBaseDelayMs = 10;
                fixture.Options.ReconnectMaxDelayMs = 10;
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.DelayedScheduler.Start();
                fixture.Gateway.ConfigureResult("SetAlarmAsync", request => new AlarmSetupResponse { AlarmHandle = 900 });
                fixture.Gateway.ConfigureResult("CloseAlarmAsync", request =>
                {
                    var close = request as AlarmCloseRequest;
                    if (close != null && close.AlarmHandle == 900)
                    {
                        throw new DeviceGatewayException("CloseAlarm", SdkError.FromCode(7));
                    }

                    return 0;
                });

                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle == 900, "initial alarm was not armed.");
                fixture.Gateway.ConfigureException("GetDeviceInfoAsync", new DeviceGatewayException("Info", SdkError.FromCode(7)));

                fixture.Lifecycle.SubmitHealthCheck(1, wait: true, requestId: "h1");
                fixture.Lifecycle.SubmitHealthCheck(1, wait: true, requestId: "h2");
                fixture.Lifecycle.SubmitHealthCheck(1, wait: true, requestId: "h3");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.Status == DeviceConnectionStatus.ReconnectPending, "reconnect was not scheduled.");
                WaitUntil(() => fixture.Gateway.Calls.Count(call => call.MethodName == "LoginAsync") >= 2, "reconnect login was not attempted.");
                WaitUntil(
                    () => fixture.Gateway.Calls.Any(call => call.MethodName == "LogoutAsync" && (call.Request as LogoutRequest) != null && ((LogoutRequest)call.Request).UserId >= 2),
                    "new login session was not logged out after stale close failure.");

                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;
                Assert.Equal(900, snapshot.StaleAlarmHandle.Value);

                // 恢复旧布防关闭后，下一轮重连应完成布防并清理旧句柄。
                fixture.Gateway.ConfigureResult("CloseAlarmAsync", request => 0);
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.Status == DeviceConnectionStatus.Online, "device did not recover after stale close succeeded.");
                var recovered = fixture.Registry.TryGetByDeviceId(1).Snapshot;
                Assert.False(recovered.StaleAlarmHandle.HasValue);
                Assert.True(recovered.AlarmHandle.HasValue);
            }
        }

        // P1 回归：被动离线后手动断开，旧布防关闭失败时 StaleAlarmHandle 必须保留，人工重连时再次撤防。
        [TestCase]
        public static void DeviceLifecycle_ManualDisconnectAfterPassiveOffline_KeepsStaleAlarmHandleWhenCloseFails()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Gateway.ConfigureResult("SetAlarmAsync", request => new AlarmSetupResponse { AlarmHandle = 900 });
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle == 900, "initial alarm was not armed.");
                fixture.Gateway.ConfigureException("GetDeviceInfoAsync", new DeviceGatewayException("Info", SdkError.FromCode(7)));

                fixture.Lifecycle.SubmitHealthCheck(1, wait: true, requestId: "h1");
                fixture.Lifecycle.SubmitHealthCheck(1, wait: true, requestId: "h2");
                fixture.Lifecycle.SubmitHealthCheck(1, wait: true, requestId: "h3");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.StaleAlarmHandle == 900, "stale alarm handle was not recorded.");
                fixture.Gateway.ConfigureResult("GetDeviceInfoAsync", fixture.Gateway.DeviceInfo);
                fixture.Gateway.ConfigureException("CloseAlarmAsync", new DeviceGatewayException("CloseAlarm", SdkError.FromCode(7)));

                var disconnect = fixture.Lifecycle.DisconnectDevice(1, "req-disconnect");
                var disconnected = fixture.Registry.TryGetByDeviceId(1).Snapshot;

                // 旧布防关闭失败（非 17）视为断开失败：接口不得谎报成功，但句柄必须保留以备人工重连撤防。
                Assert.False(disconnect.Success);
                Assert.Equal(DeviceConnectionStatus.Disconnected, disconnected.Status);
                Assert.Equal(900, disconnected.StaleAlarmHandle.Value);
                Assert.Equal("SDK_ERROR", disconnected.LastErrorCode);

                fixture.Gateway.ConfigureResult("CloseAlarmAsync", request => 0);
                var reconnect = fixture.Lifecycle.ReconnectDevice(1, force: false, requestId: "req-reconnect");
                var recovered = fixture.Registry.TryGetByDeviceId(1).Snapshot;

                Assert.True(reconnect.Success);
                Assert.Equal(DeviceConnectionStatus.Online, recovered.Status);
                Assert.False(recovered.StaleAlarmHandle.HasValue);
                Assert.True(fixture.Gateway.Calls.Any(call => call.MethodName == "CloseAlarmAsync" && (call.Request as AlarmCloseRequest) != null && ((AlarmCloseRequest)call.Request).AlarmHandle == 900));
            }
        }

        // P1 回归：被动离线后删除设备，旧布防关闭失败（非 17）时不得删除运行时记录，保留 StaleAlarmHandle 作为恢复线索。
        [TestCase]
        public static void DeviceLifecycle_DeleteDeviceAfterPassiveOffline_CloseStaleFails_PreservesRuntimeRecord()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Gateway.ConfigureResult("SetAlarmAsync", request => new AlarmSetupResponse { AlarmHandle = 900 });
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle == 900, "initial alarm was not armed.");
                fixture.Gateway.ConfigureException("GetDeviceInfoAsync", new DeviceGatewayException("Info", SdkError.FromCode(7)));

                fixture.Lifecycle.SubmitHealthCheck(1, wait: true, requestId: "h1");
                fixture.Lifecycle.SubmitHealthCheck(1, wait: true, requestId: "h2");
                fixture.Lifecycle.SubmitHealthCheck(1, wait: true, requestId: "h3");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.StaleAlarmHandle == 900, "stale alarm handle was not recorded.");

                // 被动离线：alarmHandle 已转为 staleAlarmHandle（命中断开任务路径 B，而非路径 A）。
                Assert.False(fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue);
                fixture.Gateway.ConfigureException("CloseAlarmAsync", new DeviceGatewayException("CloseAlarm", SdkError.FromCode(7)));

                var deleted = fixture.Lifecycle.DeleteDevice(1, disconnectFirst: true, requestId: "req-delete-fail");
                var lookup = fixture.Registry.TryGetByDeviceId(1);

                Assert.False(deleted.Success);
                Assert.True(lookup.Found);
                Assert.Equal(900, lookup.Snapshot.StaleAlarmHandle.Value);
            }
        }

        // P1 回归：待清理 SDK 会话的登记/清除/查询，以及去重与无效 UserId（<=0）忽略。
        [TestCase]
        public static void DeviceRuntimeRegistry_PendingSdkLogout_TracksAndClears()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);

                Assert.Equal(0, fixture.Registry.GetPendingSdkLogouts(1).Count);
                fixture.Registry.RecordPendingSdkLogout(1, 42, System.DateTime.Now);
                fixture.Registry.RecordPendingSdkLogout(1, 42, System.DateTime.Now); // 去重
                fixture.Registry.RecordPendingSdkLogout(1, 0, System.DateTime.Now);  // 无效忽略
                fixture.Registry.RecordPendingSdkLogout(1, -1, System.DateTime.Now); // 无效忽略
                fixture.Registry.RecordPendingSdkLogout(1, 43, System.DateTime.Now);

                var pending = fixture.Registry.GetPendingSdkLogouts(1);
                Assert.Equal(2, pending.Count);
                Assert.True(pending.Contains(42));
                Assert.True(pending.Contains(43));

                fixture.Registry.ClearPendingSdkLogout(1, 42, System.DateTime.Now);
                var afterClear = fixture.Registry.GetPendingSdkLogouts(1);
                Assert.False(afterClear.Contains(42));
                Assert.True(afterClear.Contains(43));
                Assert.Equal(1, afterClear.Count);
            }
        }

        // P1 回归：登录成功且设备可达后排空待清理会话；登出成功或返回 17（设备端已无此会话）均清除。
        [TestCase]
        public static void DeviceLifecycle_DrainsPendingSdkLogoutOnLogin()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                // userId 50 登出成功即清除；userId 51 登出返回 17（会话已不存在）也清除。
                fixture.Gateway.ConfigureResult("LogoutAsync", request =>
                {
                    var logout = request as LogoutRequest;
                    if (logout != null && logout.UserId == 51)
                    {
                        throw new DeviceGatewayException("Logout", SdkError.FromCode(17));
                    }

                    return 0;
                });

                fixture.Registry.RecordPendingSdkLogout(1, 50, System.DateTime.Now);
                fixture.Registry.RecordPendingSdkLogout(1, 51, System.DateTime.Now);
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");

                Assert.Equal(0, fixture.Registry.GetPendingSdkLogouts(1).Count);
                Assert.True(fixture.Gateway.Calls.Any(call => call.MethodName == "LogoutAsync" && (call.Request as LogoutRequest) != null && ((LogoutRequest)call.Request).UserId == 50));
                Assert.True(fixture.Gateway.Calls.Any(call => call.MethodName == "LogoutAsync" && (call.Request as LogoutRequest) != null && ((LogoutRequest)call.Request).UserId == 51));
            }
        }

        // P1 回归：旧布防关闭失败触发补偿登出，补偿登出失败（非 17）须保留待清理会话（不再静默丢弃 UserId）。
        // 排空逻辑由 DrainsPendingSdkLogoutOnLogin 单独覆盖（同一登录代码路径）。
        [TestCase]
        public static void DeviceLifecycle_CompensationLogoutFailure_RetainsPendingSession()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.Options.ReconnectBaseDelayMs = 10;
                fixture.Options.ReconnectMaxDelayMs = 10;
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.DelayedScheduler.Start();
                fixture.Gateway.ConfigureResult("SetAlarmAsync", request => new AlarmSetupResponse { AlarmHandle = 900 });
                fixture.Gateway.ConfigureResult("CloseAlarmAsync", request =>
                {
                    var close = request as AlarmCloseRequest;
                    if (close != null && close.AlarmHandle == 900)
                    {
                        throw new DeviceGatewayException("CloseAlarm", SdkError.FromCode(7));
                    }

                    return 0;
                });
                // 补偿登出对重连后的新会话（UserId >= 2）失败（非 17）→ 保留为待清理。
                fixture.Gateway.ConfigureResult("LogoutAsync", request =>
                {
                    var logout = request as LogoutRequest;
                    if (logout != null && logout.UserId >= 2)
                    {
                        throw new DeviceGatewayException("Logout", SdkError.FromCode(7));
                    }

                    return 0;
                });

                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle == 900, "initial alarm was not armed.");
                fixture.Gateway.ConfigureException("GetDeviceInfoAsync", new DeviceGatewayException("Info", SdkError.FromCode(7)));

                fixture.Lifecycle.SubmitHealthCheck(1, wait: true, requestId: "h1");
                fixture.Lifecycle.SubmitHealthCheck(1, wait: true, requestId: "h2");
                fixture.Lifecycle.SubmitHealthCheck(1, wait: true, requestId: "h3");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.Status == DeviceConnectionStatus.ReconnectPending, "reconnect was not scheduled.");

                // 补偿登出失败后，至少一个新会话 UserId 被保留为待清理（不再静默丢弃）。
                WaitUntil(() => fixture.Registry.GetPendingSdkLogouts(1).Any(userId => userId >= 2), "pending SDK logout was not retained after compensation failure.");
                Assert.True(fixture.Gateway.Calls.Any(call => call.MethodName == "LogoutAsync" && (call.Request as LogoutRequest) != null && ((LogoutRequest)call.Request).UserId >= 2), "compensation logout was not attempted for the new session.");
            }
        }

        // P1 回归：延迟调度器不可用时，重连调度失败必须标记 Faulted，不能永久停在 ReconnectPending。
        [TestCase]
        public static void DeviceLifecycle_ReconnectScheduleFailure_MarksFaulted()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.DelayedOptions.Enabled = false;
                fixture.AddRecord(password: "wrong");
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);

                var login = fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-fail");
                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;

                Assert.False(login.Success);
                Assert.Equal(DeviceConnectionStatus.Faulted, snapshot.Status);
                Assert.Equal("RECONNECT_SCHEDULE_FAILED", snapshot.LastErrorCode);
                Assert.Equal(0, fixture.DelayedScheduler.GetSnapshot().DelayedTaskCount);
            }
        }

        // P1 回归：布防重试调度失败时保持当前状态并记录错误，不崩溃、不假装已调度。
        [TestCase]
        public static void DeviceLifecycle_ReArmScheduleFailure_KeepsDegradedState()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.DelayedOptions.Enabled = false;
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Gateway.ConfigureException("SetAlarmAsync", new DeviceGatewayException("Arm", SdkError.FromCode(7)));

                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                WaitUntil(() => fixture.Gateway.Calls.Any(call => call.MethodName == "SetAlarmAsync"), "alarm was not attempted.");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.Status == DeviceConnectionStatus.Degraded, "device did not enter degraded state.");

                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;
                Assert.False(snapshot.AlarmHandle.HasValue);
                Assert.Equal(0, fixture.DelayedScheduler.GetSnapshot().DelayedTaskCount);
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

        private static int FindFirstCallIndex(Stage4Fixture fixture, string methodName)
        {
            var calls = fixture.Gateway.Calls.ToList();
            for (var i = 0; i < calls.Count; i++)
            {
                if (calls[i].MethodName == methodName)
                {
                    return i;
                }
            }

            return -1;
        }

        private static int FindLastCallIndex(Stage4Fixture fixture, string methodName)
        {
            var calls = fixture.Gateway.Calls.ToList();
            for (var i = calls.Count - 1; i >= 0; i--)
            {
                if (calls[i].MethodName == methodName)
                {
                    return i;
                }
            }

            return -1;
        }

        private static ControlDoor.Devices.Management.DeviceRecord NewRecord(int deviceId, string ipAddress)
        {
            return new ControlDoor.Devices.Management.DeviceRecord
            {
                DeviceId = deviceId,
                DeviceName = "门禁-" + deviceId,
                IpAddress = ipAddress,
                Port = 8000,
                Username = "admin",
                Password = "12345",
                Enabled = true,
                Types = new System.Collections.Generic.List<ControlDoor.Devices.Management.DeviceType> { ControlDoor.Devices.Management.DeviceType.Acs }
            };
        }
    }
}
