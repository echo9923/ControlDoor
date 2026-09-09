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
    // 代码复核 J1：设备删除失败回滚后，门恢复意图不得因删除中间态的 DEVICE_DELETING 拒绝
    // 而转永久失败终态；删除成功（设备移除）后才按 DEVICE_NOT_FOUND 终态处理。
    public static class CodeReviewJ01DeleteRollbackInterlockTests
    {
        [TestCase]
        public static void CameraDoorInterlockService_RestoreRejectedByDeleting_RecoversAfterDeleteRollback()
        {
            using (var fixture = new Stage9Fixture(windowSeconds: 5, restoreRetryIntervalMs: 1000))
            {
                var t0 = new DateTime(2026, 1, 1, 8, 0, 0);
                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(t0);
                SpinUntil(() => CountGatewayCalls(fixture, GateControlCommand.AlwaysClose) >= 1, "常闭命令未执行。");

                // 删除中间态（SetDeleting）期间窗口到期：恢复任务被 DEVICE_DELETING 拒绝，
                // 属于删除结果未落定的暂时性阻塞，不得转终态。
                Assert.True(fixture.Registry.SetDeleting(fixture.DoorDeviceId, true, DateTime.Now).Success, "设置删除中间态失败。");
                var expireResult = fixture.Service.ExpireWindows(t0.AddSeconds(5));
                Assert.Equal(1, expireResult.RestoreSubmissions);

                DoorTargetActivity activity;
                SpinUntil(
                    () => fixture.TargetManager.TryGetActivity("10:1", out activity)
                        && activity.PendingRestoreAttempt == 1
                        && !activity.RestoreTerminalFailed
                        && activity.RestoreNextRetryAt.HasValue,
                    "删除中间态的恢复拒绝不得转终态，应记录下次重试。");
                Assert.True(fixture.TargetManager.TryGetActivity("10:1", out activity));
                Assert.False(activity.RestoreTerminalFailed, "删除进行中是暂时性阻塞（J1），不是终态。");
                Assert.True(fixture.TargetManager.GetOutstandingTargets().Any(a => a.TargetKey == "10:1"), "停止 best-effort 恢复应继续覆盖该目标。");

                // 模拟清单写入失败回滚：撤销删除标记，设备保持在线；到期重试后恢复命令真正下发。
                Assert.True(fixture.Registry.SetDeleting(fixture.DoorDeviceId, false, DateTime.Now).Success, "撤销删除中间态失败。");
                var retry = fixture.Service.ProcessRestoreRetries(activity.RestoreNextRetryAt.Value);
                Assert.Equal(1, retry.RetriesProcessed);
                SpinUntil(() => CountGatewayCalls(fixture, GateControlCommand.Restore) >= 1, "删除回滚后未重新下发恢复命令。");
                DoorTargetActivity cleared;
                SpinUntil(() => !fixture.TargetManager.TryGetActivity("10:1", out cleared), "恢复成功后应清除门目标活动。");
            }
        }

        [TestCase]
        public static void CameraDoorInterlockService_RestoreAfterDeviceRemoved_MarksTerminal()
        {
            using (var fixture = new Stage9Fixture(windowSeconds: 5, restoreRetryIntervalMs: 1000))
            {
                var t0 = new DateTime(2026, 1, 1, 8, 0, 0);
                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(t0);
                SpinUntil(() => CountGatewayCalls(fixture, GateControlCommand.AlwaysClose) >= 1, "常闭命令未执行。");

                // 删除成功：设备从运行时移除，恢复任务得到 DEVICE_NOT_FOUND，按终态处理（需人工确认）。
                fixture.Registry.RemoveDevice(fixture.DoorDeviceId, DateTime.Now);
                var expireResult = fixture.Service.ExpireWindows(t0.AddSeconds(5));
                Assert.Equal(1, expireResult.RestoreSubmissions);

                DoorTargetActivity activity;
                SpinUntil(
                    () => fixture.TargetManager.TryGetActivity("10:1", out activity) && activity.RestoreTerminalFailed,
                    "设备已移除的恢复失败应转终态，等待人工确认。");
                Assert.False(fixture.TargetManager.GetOutstandingTargets().Any(a => a.TargetKey == "10:1"), "终态目标不应再进入停止 best-effort 恢复。");
            }
        }

        private static int CountGatewayCalls(Stage9Fixture fixture, GateControlCommand command)
        {
            return fixture.Gateway.Calls.Count(c =>
                c.MethodName == "ControlGatewayAsync" &&
                c.Request is GateControlRequest request &&
                request.Command == command);
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
