using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Tasks;
using ControlDoor.Runtime;

namespace ControlEntradaSalida.Tests
{
    public static class CodeReviewStartupLoginRecoveryTests
    {
        [TestCase]
        public static void DeviceLifecycle_InitialLoginExpires_ReconnectsAutomatically()
        {
            using (var fixture = new Stage4Fixture())
            using (var started = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                Prepare(fixture);
                var blocker = BlockWorker(fixture, started, release);
                try
                {
                    Assert.True(fixture.Lifecycle.SubmitLogin(1, false, "initial-expiry").Success);
                    Thread.Sleep(120);
                    release.Set();
                    blocker.Completion.Task.GetAwaiter().GetResult();
                    WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.Status == DeviceConnectionStatus.ReconnectPending);
                    Assert.Equal(0, fixture.Gateway.Calls.Count(call => call.MethodName == "LoginAsync"));
                    fixture.DelayedScheduler.StartAsync(new BackgroundTaskContext("initial-expiry", CancellationToken.None, null)).GetAwaiter().GetResult();
                    WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.IsConnected);
                    Assert.Equal(1, fixture.Gateway.Calls.Count(call => call.MethodName == "LoginAsync"));
                }
                finally
                {
                    release.Set();
                }
            }
        }

        [TestCase]
        public static void DeviceLifecycle_InitialLoginQueueFull_ReconnectsWhenQueueDrains()
        {
            using (var fixture = new Stage4Fixture())
            using (var started = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                Prepare(fixture);
                var blocker = BlockWorker(fixture, started, release);
                try
                {
                    for (var i = 0; i < 50; i++)
                    {
                        Assert.True(fixture.Dispatcher.Submit(new DeviceSdkTask(1, DeviceTaskType.Login, "QueueFiller",
                            context => Task.FromResult(Ok(context.Task)))).Accepted);
                    }
                    var rejected = fixture.Lifecycle.SubmitLogin(1, false, "initial-queue-full");
                    Assert.Equal("QUEUE_FULL", rejected.Code);
                    WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.Status == DeviceConnectionStatus.ReconnectPending);
                    release.Set();
                    blocker.Completion.Task.GetAwaiter().GetResult();
                    fixture.DelayedScheduler.StartAsync(new BackgroundTaskContext("initial-queue-full", CancellationToken.None, null)).GetAwaiter().GetResult();
                    WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.IsConnected);
                    Assert.Equal(1, fixture.Gateway.Calls.Count(call => call.MethodName == "LoginAsync"));
                }
                finally
                {
                    release.Set();
                }
            }
        }

        [TestCase]
        public static void DeviceLifecycle_LoadWithoutLogin_DoesNotScheduleReconnect()
        {
            using (var fixture = new Stage4Fixture())
            {
                Prepare(fixture);
                Assert.Equal(DeviceConnectionStatus.Loaded, fixture.Registry.TryGetByDeviceId(1).Snapshot.Status);
                Assert.Equal(0, fixture.DelayedScheduler.GetSnapshot().GetSourceCount("Stage4Reconnect"));
                Assert.Equal(0, fixture.Gateway.Calls.Count(call => call.MethodName == "LoginAsync"));
            }
        }

        [TestCase]
        public static void DeviceLifecycle_WaitingLoginCancelledByQueueTimeout_ReconnectsAutomatically()
        {
            using (var fixture = new Stage4Fixture())
            using (var started = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                Prepare(fixture);
                var blocker = BlockWorker(fixture, started, release);
                try
                {
                    var result = fixture.Lifecycle.SubmitLogin(1, true, "waiting-login-timeout");
                    Assert.Equal("TIMEOUT", result.Code);
                    WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.Status == DeviceConnectionStatus.ReconnectPending);
                    release.Set();
                    blocker.Completion.Task.GetAwaiter().GetResult();
                    fixture.DelayedScheduler.StartAsync(new BackgroundTaskContext("waiting-login", CancellationToken.None, null)).GetAwaiter().GetResult();
                    WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.IsConnected);
                    Assert.Equal(1, fixture.Gateway.Calls.Count(call => call.MethodName == "LoginAsync"));
                }
                finally
                {
                    release.Set();
                }
            }
        }

        [TestCase]
        public static void DeviceLifecycle_LoginObserver_RespectsDisconnectDeleteAndStop()
        {
            foreach (var mode in new[] { "disconnect", "delete", "stop" })
            {
                using (var fixture = new Stage4Fixture())
                using (var started = new ManualResetEventSlim())
                using (var release = new ManualResetEventSlim())
                {
                    Prepare(fixture);
                    var blocker = BlockWorker(fixture, started, release);
                    try
                    {
                        Assert.True(fixture.Lifecycle.SubmitLogin(1, false, mode).Success);
                        if (mode == "disconnect") fixture.Registry.SetManualDisconnected(1, true, DateTime.Now);
                        if (mode == "delete") fixture.Registry.SetDeleting(1, true, DateTime.Now);
                        if (mode == "stop") fixture.Lifecycle.BeginStopping();
                        Thread.Sleep(120);
                        release.Set();
                        blocker.Completion.Task.GetAwaiter().GetResult();
                        fixture.Dispatcher.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                        Thread.Sleep(50);
                        Assert.Equal(0, fixture.Gateway.Calls.Count(call => call.MethodName == "LoginAsync"), mode);
                        Assert.Equal(0, fixture.DelayedScheduler.GetSnapshot().GetSourceCount("Stage4Reconnect"), mode);
                    }
                    finally
                    {
                        release.Set();
                    }
                }
            }
        }

        [TestCase]
        public static void DeviceLifecycle_DuplicateLoginExpiry_DoesNotReconnectAnOnlineDevice()
        {
            using (var fixture = new Stage4Fixture())
            using (var started = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                Prepare(fixture);
                fixture.Options.LoginTimeoutMs = 500;
                fixture.Gateway.ConfigureResult<ControlDoor.Hikvision.LoginResponse>("LoginAsync", request =>
                {
                    started.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(3)));
                    return new ControlDoor.Hikvision.LoginResponse { UserId = 1 };
                });
                try
                {
                    fixture.Lifecycle.SubmitLogin(1, false, "first-login");
                    Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
                    fixture.Options.LoginTimeoutMs = 50;
                    fixture.Lifecycle.SubmitLogin(1, false, "duplicate-login");
                    Thread.Sleep(120);
                    release.Set();
                    WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.IsConnected);
                    fixture.Dispatcher.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                    Thread.Sleep(50);
                    Assert.Equal(DeviceConnectionStatus.Online, fixture.Registry.TryGetByDeviceId(1).Snapshot.Status);
                    Assert.Equal(0, fixture.DelayedScheduler.GetSnapshot().GetSourceCount("Stage4Reconnect"));
                }
                finally
                {
                    release.Set();
                }
            }
        }

        private static void Prepare(Stage4Fixture fixture)
        {
            fixture.Options.LoginTimeoutMs = 50;
            fixture.Options.MaxReconnectAttempts = 0;
            fixture.Options.AlarmEnabled = false;
            fixture.AddRecord();
            fixture.Lifecycle.LoadEnabledDevices(false);
        }

        private static DeviceSdkTask BlockWorker(Stage4Fixture fixture, ManualResetEventSlim started, ManualResetEventSlim release)
        {
            var blocker = new DeviceSdkTask(1, DeviceTaskType.Login, "StartupBlocker", context => Task.Run(() =>
            {
                started.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
                return Ok(context.Task);
            }));
            Assert.True(fixture.Dispatcher.Submit(blocker).Accepted);
            Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
            return blocker;
        }

        private static DeviceTaskResult Ok(DeviceSdkTask task)
        {
            return DeviceTaskResult.FromTask(task, true, "OK", "ok", DeviceConnectionStatus.Loaded, DateTime.Now, DateTime.Now);
        }

        private static void WaitUntil(Func<bool> predicate)
        {
            Assert.True(SpinWait.SpinUntil(predicate, TimeSpan.FromSeconds(2)), "Expected device lifecycle transition did not occur.");
        }
    }
}
