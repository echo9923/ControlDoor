using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.CameraDoorInterlock;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Tasks;
using ControlDoor.Hikvision;

namespace ControlEntradaSalida.Tests
{
    // 代码复核 I1：恢复任务在执行前被拒绝（手动断开、排队过期）属于暂时性阻塞，
    // 不得转配置类终态；重连或设备通道空闲后必须自动恢复，恢复命令真正下发。
    public static class Stage9RestorePreExecutionFailureTests
    {
        [TestCase]
        public static void CameraDoorInterlockService_RestoreRejectedByManualDisconnect_StaysPendingAndRestoresAfterReconnect()
        {
            using (var fixture = new Stage9Fixture(windowSeconds: 5, restoreRetryIntervalMs: 1000))
            {
                var t0 = new DateTime(2026, 1, 1, 8, 0, 0);
                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(t0);
                SpinUntil(() => CountGatewayCalls(fixture, GateControlCommand.AlwaysClose) >= 1, "常闭命令未执行。");

                // 窗口结束前手动断开设备：操作员接管期间恢复任务必须被守卫拒绝，但意图保留。
                var disconnect = fixture.Lifecycle.DisconnectDevice(fixture.DoorDeviceId, "i1-manual-disconnect");
                Assert.True(disconnect.Success, disconnect.Message);
                SpinUntil(
                    () => fixture.Registry.TryGetByDeviceId(fixture.DoorDeviceId).Snapshot.Reconnect.ManualDisconnected,
                    "手动断开标记未生效。");

                var expireResult = fixture.Service.ExpireWindows(t0.AddSeconds(5));
                Assert.Equal(1, expireResult.RestoreSubmissions);

                // 执行前被 DEVICE_MANUALLY_DISCONNECTED 拒绝：非终态、有下次重试、停止恢复仍覆盖该目标。
                DoorTargetActivity activity;
                SpinUntil(
                    () => fixture.TargetManager.TryGetActivity("10:1", out activity)
                        && activity.PendingRestoreAttempt == 1
                        && !activity.RestoreTerminalFailed
                        && activity.RestoreNextRetryAt.HasValue,
                    "手动断开导致的恢复拒绝不得转终态，应记录下次重试。");
                Assert.True(fixture.TargetManager.TryGetActivity("10:1", out activity));
                Assert.False(activity.RestoreTerminalFailed, "手动断开是暂时性阻塞，不是终态。");
                Assert.True(fixture.TargetManager.GetOutstandingTargets().Any(a => a.TargetKey == "10:1"), "停止 best-effort 恢复应继续覆盖该目标。");

                // 普通重连清除手动断开标记后，到期扫描重新投递，恢复命令真正下发并清除目标状态。
                var reconnect = fixture.Lifecycle.ReconnectDevice(fixture.DoorDeviceId, force: false, requestId: "i1-reconnect");
                Assert.True(reconnect.Success, reconnect.Message);
                SpinUntil(
                    () => fixture.Registry.TryGetByDeviceId(fixture.DoorDeviceId).Snapshot.Status == DeviceConnectionStatus.Online
                        && !fixture.Registry.TryGetByDeviceId(fixture.DoorDeviceId).Snapshot.Reconnect.ManualDisconnected,
                    "重连后设备未恢复可用。");

                var retry = fixture.Service.ProcessRestoreRetries(activity.RestoreNextRetryAt.Value);
                Assert.Equal(1, retry.RetriesProcessed);
                SpinUntil(() => CountGatewayCalls(fixture, GateControlCommand.Restore) >= 1, "重连后未重新下发恢复命令。");
                DoorTargetActivity cleared;
                SpinUntil(() => !fixture.TargetManager.TryGetActivity("10:1", out cleared), "恢复成功后应清除门目标活动。");
            }
        }

        [TestCase]
        public static void CameraDoorInterlockService_RestoreExpiredInQueueBeforeExecution_StaysPendingAndRestoresWhenChannelFrees()
        {
            // 注入短期任务期限（150ms），用阻塞任务占住设备通道 600ms 构造"恢复任务在队列中过期"。
            using (var fixture = new Stage9Fixture(windowSeconds: 5, restoreRetryIntervalMs: 1000, taskTimeoutMs: 150))
            {
                var t0 = new DateTime(2026, 1, 1, 8, 0, 0);
                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(t0);
                SpinUntil(() => CountGatewayCalls(fixture, GateControlCommand.AlwaysClose) >= 1, "常闭命令未执行。");

                var blockerStarted = new ManualResetEventSlim(false);
                fixture.Dispatcher.Submit(Blocker(fixture.DoorDeviceId, 600, blockerStarted));
                Assert.True(blockerStarted.Wait(TimeSpan.FromSeconds(2)), "阻塞任务未开始执行。");

                var expireResult = fixture.Service.ExpireWindows(t0.AddSeconds(5));
                Assert.Equal(1, expireResult.RestoreSubmissions);

                // 阻塞 600ms > 150ms 期限：恢复任务执行前在队列中过期（委托从未运行），不得转终态。
                DoorTargetActivity activity;
                SpinUntil(
                    () => fixture.TargetManager.TryGetActivity("10:1", out activity)
                        && activity.PendingRestoreAttempt == 1
                        && !activity.RestoreTerminalFailed
                        && activity.RestoreNextRetryAt.HasValue,
                    "排队过期的恢复任务不得转终态，应记录下次重试。");
                Assert.True(fixture.TargetManager.TryGetActivity("10:1", out activity));
                Assert.False(activity.RestoreTerminalFailed, "排队过期是暂时性阻塞，不是终态。");

                // 通道空闲后到期重试：新任务有新期限，恢复命令真正下发并清除目标状态。
                var retry = fixture.Service.ProcessRestoreRetries(activity.RestoreNextRetryAt.Value);
                Assert.Equal(1, retry.RetriesProcessed);
                SpinUntil(() => CountGatewayCalls(fixture, GateControlCommand.Restore) >= 1, "通道空闲后未重新下发恢复命令。");
                DoorTargetActivity cleared;
                SpinUntil(() => !fixture.TargetManager.TryGetActivity("10:1", out cleared), "恢复成功后应清除门目标活动。");
            }
        }

        private static int CountGatewayCalls(Stage9Fixture fixture, GateControlCommand command)
        {
            return fixture.Gateway.Calls.Count(c =>
                c.MethodName == "ControlGatewayAsync" &&
                c.Request is GateControlRequest request &&
                request.Command == command);
        }

        private static DeviceSdkTask Blocker(int deviceId, int milliseconds, ManualResetEventSlim started)
        {
            return new DeviceSdkTask(deviceId, DeviceTaskType.HealthCheck, "I1Blocker", context => Task.Run(() =>
            {
                started.Set();
                Thread.Sleep(milliseconds);
                var now = DateTime.Now;
                return DeviceTaskResult.FromTask(context.Task, true, "OK", "ok", DeviceConnectionStatus.Online, now, now);
            }));
        }

        private static void SpinUntil(Func<bool> condition, string failureMessage)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                Thread.Sleep(5);
            }

            Assert.True(false, failureMessage);
        }
    }
}
