using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Configuration;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Tasks;
using ControlDoor.Devices.Workers;
using ControlDoor.Hikvision;
using ControlDoor.Host;
using ControlDoor.Permissions;

namespace ControlEntradaSalida.Tests
{
    public static class FinalLifecycleRegressionTests
    {
        private static void Prepare(Stage4Fixture fixture, bool online)
        {
            fixture.Options.AlarmEnabled = false;
            fixture.AddRecord();
            fixture.Lifecycle.LoadEnabledDevices(false);
            if (online) Assert.True(fixture.Lifecycle.SubmitLogin(1, true, "review").Success);
        }

        private static DeviceSdkTask Barrier(int deviceId = 1)
        {
            return new DeviceSdkTask(deviceId, DeviceTaskType.HealthCheck, "Barrier", context =>
                Task.FromResult(DeviceTaskResult.FromTask(context.Task, true, "OK", "done", DeviceConnectionStatus.Online, DateTime.Now, DateTime.Now)))
            { Priority = DeviceTaskPriority.Normal, TimeoutMilliseconds = 5000 };
        }

        [TestCase]
        public static void Reconnect_QueueFull_ReschedulesUntilAccepted()
        {
            using (var fixture = new Stage4Fixture())
            using (var entered = new ManualResetEventSlim())
            {
                Prepare(fixture, false);
                fixture.Gateway.ConfigureException("LoginAsync", new DeviceGatewayException("Login", SdkError.FromCode(7)));
                Assert.False(fixture.Lifecycle.SubmitLogin(1, true, "initial-failure").Success);
                Assert.Equal(1, fixture.DelayedScheduler.GetSnapshot().DelayedTaskCount);
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var blocker = new DeviceSdkTask(1, DeviceTaskType.HealthCheck, "Blocker", async context =>
                {
                    entered.Set();
                    await release.Task.ConfigureAwait(false);
                    return DeviceTaskResult.FromTask(context.Task, true, "OK", "done", DeviceConnectionStatus.Offline, DateTime.Now, DateTime.Now);
                }) { TimeoutMilliseconds = 5000 };
                fixture.Dispatcher.Submit(blocker);
                Assert.True(entered.Wait(2000));
                try
                {
                    for (var i = 0; i < 50; i++) Assert.True(fixture.Dispatcher.Submit(Barrier()).Accepted);
                    var outcomes = fixture.DelayedScheduler.DispatchDueTasks(DateTime.Now.AddSeconds(2));
                    Assert.Equal(1, outcomes.Count);
                    Assert.Equal("QUEUE_FULL", outcomes[0].Code);
                    Assert.Equal(1, fixture.DelayedScheduler.GetSnapshot().DelayedTaskCount);
                }
                finally { release.TrySetResult(true); }
                Assert.True(SpinWait.SpinUntil(() => fixture.Dispatcher.GetWorkerSnapshots().All(x => x.QueueLength == 0), 2000));
                Assert.True(fixture.Lifecycle.SubmitHealthCheck(1, true, "manual-refresh").Success);
                Assert.Equal(1, fixture.DelayedScheduler.DispatchDueTasks(DateTime.Now.AddMinutes(2)).Count);
                Assert.True(SpinWait.SpinUntil(() => fixture.Gateway.Calls.Count(x => x.MethodName == "LoginAsync") >= 2, 2000));
                // 第二次登录失败后异步完成路径重排重连需要短暂时间，自旋等待状态落定。
                Assert.True(SpinWait.SpinUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.Status == DeviceConnectionStatus.ReconnectPending, 2000), "重连应已重排为 ReconnectPending。");
                Assert.Equal(2, fixture.Gateway.Calls.Count(x => x.MethodName == "LoginAsync"));
            }
        }

        private static ControlDoorHost CreateHost(Stage4Fixture fixture)
        {
            var host = new ControlDoorHost(AppDomain.CurrentDomain.BaseDirectory);
            SetField(host, "state", ServiceLifecycleState.Running);
            SetField(host, "deviceRegistry", fixture.Registry);
            SetField(host, "deviceDispatcher", fixture.Dispatcher);
            SetField(host, "deviceLifecycle", fixture.Lifecycle);
            SetField(host, "hikvisionGateway", fixture.Gateway);
            return host;
        }

        private static void SetField(object target, string field, object value)
        {
            target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        }

        [TestCase]
        public static void Host_Stop_WaitsForLoginAndCleansNewSession()
        {
            using (var fixture = new Stage4Fixture())
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                Prepare(fixture, false);
                fixture.Gateway.ConfigureResult("LoginAsync", request =>
                {
                    entered.Set();
                    Assert.True(release.Wait(3000));
                    return new LoginResponse { UserId = 501, DeviceInfo = new DeviceInfo { SerialNumber = "LATE-LOGIN" } };
                });
                Assert.True(fixture.Lifecycle.SubmitLogin(1, false, "inflight-login").Success);
                Assert.True(entered.Wait(2000));
                using (var host = CreateHost(fixture))
                {
                    var stopped = Task.Run(() => host.StopAsync("review"));
                    try
                    {
                        Assert.True(SpinWait.SpinUntil(() => host.State == ServiceLifecycleState.Stopping, 2000));
                        Assert.False(stopped.IsCompleted);
                    }
                    finally { release.Set(); }
                    Assert.True(stopped.GetAwaiter().GetResult().Success);
                    Assert.Equal(ServiceLifecycleState.Stopped, host.State);
                    var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;
                    Assert.False(snapshot.IsConnected);
                    Assert.False(snapshot.SdkUserId.HasValue);
                    Assert.Equal(1, fixture.Gateway.Calls.Count(x => x.MethodName == "LogoutAsync"));
                }
            }
        }

        [TestCase]
        public static void Host_StopTimeout_ReturnsBeforeNativeCleanupCompletes()
        {
            using (var fixture = new Stage4Fixture())
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                Prepare(fixture, true);
                fixture.Gateway.ConfigureResult("LogoutAsync", request =>
                {
                    entered.Set();
                    Assert.True(release.Wait(3000));
                    return 0;
                });
                using (var host = CreateHost(fixture))
                {
                    var controller = new ServiceLifecycleController(host);
                    SetField(controller, "state", ServiceLifecycleState.Running);
                    var watch = Stopwatch.StartNew();
                    var stopped = Task.Run(() => controller.StopAsync("review", TimeSpan.FromMilliseconds(50)));
                    try
                    {
                        Assert.True(entered.Wait(2000));
                        Assert.True(stopped.Wait(1000), "Controller did not enforce the stop timeout.");
                        Assert.False(stopped.Result.Success);
                        Assert.Equal(ServiceLifecycleState.Stopping, host.State);
                    }
                    finally { release.Set(); }
                    Assert.False(stopped.GetAwaiter().GetResult().Success);
                    Assert.True(host.StopAsync("join").GetAwaiter().GetResult().Success);
                }
            }
        }

        [TestCase]
        public static void Retry_SeparateWorkers_RunConcurrently()
        {
            using (var fixture = new Stage4Fixture())
            using (var firstEntered = new ManualResetEventSlim())
            using (var secondEntered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                Prepare(fixture, true);
                fixture.Gateway.DeviceInfo.SerialNumber = "SECOND-DEVICE";
                var added = fixture.Lifecycle.RegisterDevice(new ControlDoor.Devices.Management.DeviceRecord
                {
                    DeviceId = 2, DeviceName = "second", IpAddress = "192.168.1.65", Port = 8000, Username = "test", Password = "test", Enabled = true,
                    Types = new List<ControlDoor.Devices.Management.DeviceType> { ControlDoor.Devices.Management.DeviceType.Acs }
                }, true);
                Assert.True(added.Success, added.Message);
                Assert.True(fixture.Lifecycle.SubmitLogin(2, true, "second-login").Success);
                var firstWorker = fixture.Registry.TryGetWorkerRoute(1).WorkerIndex.Value;
                var secondWorker = fixture.Registry.TryGetWorkerRoute(2).WorkerIndex.Value;
                Assert.True(firstWorker != secondWorker);
                var firstUser = fixture.Registry.TryGetByDeviceId(1).Snapshot.SdkUserId.Value;
                fixture.Gateway.ConfigureResult("UpsertPersonAsync", request =>
                {
                    if (((UpsertPersonRequest)request).UserId == firstUser)
                    {
                        firstEntered.Set();
                        Assert.True(release.Wait(3000));
                    }
                    else secondEntered.Set();
                    return 0;
                });
                var database = new RecordingDatabaseClient { RowsAffected = 1 };
                database.QueryRowsByOperation["DeviceOperationRetryStore.LoadDueStates"] = new List<IReadOnlyDictionary<string, object>>
                {
                    RetryRow(1), RetryRow(2)
                };
                var options = new DeviceOperationRetryOptions { BatchSize = 100 };
                var store = new DeviceOperationRetryStore(database, options);
                var manager = new DeviceOperationRetryManager(store, fixture.Registry, new RetryExecutionCoordinator(fixture.Dispatcher, fixture.Gateway), options);
                var scan = manager.RunOnceAsync("serial-review");
                try
                {
                    Assert.True(firstEntered.Wait(2000));
                    Assert.True(secondEntered.Wait(2000), "Independent device must execute while the first worker is blocked.");
                }
                finally { release.Set(); }
                var result = scan.GetAwaiter().GetResult();
                Assert.True(secondEntered.IsSet);
                Assert.Equal(2, result.Succeeded);
            }
        }

        private static IReadOnlyDictionary<string, object> RetryRow(int deviceId)
        {
            return new Dictionary<string, object>
            {
                ["id"] = (long)deviceId, ["device_id"] = deviceId, ["employee_id"] = "E1", ["intent_version"] = Guid.NewGuid(),
                ["person_pending"] = true, ["person_payload"] = "{\"employeeId\":\"E1\",\"name\":\"review\"}",
                ["next_retry_at"] = DateTime.Now.AddMinutes(-1)
            };
        }
    }
}
