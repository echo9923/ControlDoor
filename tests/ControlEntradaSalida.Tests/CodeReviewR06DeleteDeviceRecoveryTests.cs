using System;
using System.Linq;
using System.Threading;
using ControlDoor.Devices.Runtime;

namespace ControlEntradaSalida.Tests
{
    // 代码复核 R06：删除设备时配置写入失败后，已登出的设备必须有恢复措施——
    // 原自动在线设备补排登录布防，原手动断开设备保持手动断开语义。
    public static class CodeReviewR06DeleteDeviceRecoveryTests
    {
        [TestCase]
        public static void DeviceLifecycle_DeleteDeviceWriteFailed_RecoversAutoOnlineDevice()
        {
            using (var fixture = new Stage4Fixture())
            {
                // 补偿走统一重连状态机（F02）：需要延迟调度器运行来派发重连任务。
                var schedulerContext = new ControlDoor.Runtime.BackgroundTaskContext("r06-scheduler", System.Threading.CancellationToken.None, null);
                fixture.DelayedScheduler.StartAsync(schedulerContext).GetAwaiter().GetResult();
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "r06-login");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.Status == DeviceConnectionStatus.Online, "初始登录未完成。");
                var loginCount = fixture.Gateway.Calls.Count(call => call.MethodName == "LoginAsync");

                fixture.Repository.FailWrites = true;
                var failed = fixture.Lifecycle.DeleteDevice(1, disconnectFirst: true, requestId: "r06-delete-fail");

                Assert.False(failed.Success);
                Assert.Equal("WRITE_FAILED", failed.Code);
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.Status == DeviceConnectionStatus.Online, "删除失败后设备未恢复在线。");
                WaitUntil(() => fixture.Gateway.Calls.Count(call => call.MethodName == "LoginAsync") > loginCount, "删除失败后没有补排登录。");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue, "恢复登录后未重新布防。");

                // 再次删除成功后设备移除，不残留。
                fixture.Repository.FailWrites = false;
                var retried = fixture.Lifecycle.DeleteDevice(1, disconnectFirst: true, requestId: "r06-delete-retry");
                WaitUntil(() => !fixture.Registry.TryGetByDeviceId(1).Found, "设备未从运行时移除。");

                Assert.True(retried.Success);
            }
        }

        [TestCase]
        public static void DeviceLifecycle_DeleteDeviceWriteFailed_KeepsManualDisconnectSemantics()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "r06-login");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.Status == DeviceConnectionStatus.Online, "初始登录未完成。");
                fixture.Lifecycle.DisconnectDevice(1, "r06-manual-disconnect");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.Status == DeviceConnectionStatus.Disconnected, "手动断开未完成。");
                var loginCount = fixture.Gateway.Calls.Count(call => call.MethodName == "LoginAsync");

                fixture.Repository.FailWrites = true;
                var failed = fixture.Lifecycle.DeleteDevice(1, disconnectFirst: true, requestId: "r06-delete-manual");

                Assert.False(failed.Success);
                Thread.Sleep(200);
                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;
                Assert.True(snapshot.Reconnect.ManualDisconnected, "手动断开语义未被恢复。");
                Assert.Equal(loginCount, fixture.Gateway.Calls.Count(call => call.MethodName == "LoginAsync"));
                Assert.Equal(0, fixture.DelayedScheduler.GetSnapshot().GetSourceCount("Stage4Reconnect"));
            }
        }

        private static void WaitUntil(Func<bool> condition, string message)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                Thread.Sleep(20);
            }

            Assert.True(condition(), message);
        }
    }
}
