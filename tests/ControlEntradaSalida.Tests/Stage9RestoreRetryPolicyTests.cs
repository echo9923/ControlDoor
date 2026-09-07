using System;
using System.Threading;
using ControlDoor.CameraDoorInterlock;
using ControlDoor.Hikvision;

namespace ControlEntradaSalida.Tests
{
    public static class Stage9RestoreRetryPolicyTests
    {
        [TestCase]
        public static void Stage9RestoreRetry_RetryableFailure_RetriesIndefinitelyUntilSuccess()
        {
            // 设备离线/网络抖动等可重试错误，恢复任务必须持续重试，无最大次数限制。
            using (var fixture = new Stage9Fixture(windowSeconds: 5, restoreRetryIntervalMs: 1000))
            {
                fixture.Gateway.ConfigureException("ControlGatewayAsync", new DeviceGatewayException("ControlGateway", SdkError.FromCode(7, "busy")));
                var t0 = new DateTime(2026, 1, 1, 8, 0, 0);

                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(t0);

                var expireResult = fixture.Service.ExpireWindows(t0.AddSeconds(5));
                Assert.Equal(1, expireResult.RestoreSubmissions);
                DoorTargetActivity activity;
                SpinUntil(() => fixture.TargetManager.TryGetActivity("10:1", out activity) && activity.PendingRestoreAttempt == 1, "首次恢复失败结果未记录。");
                Assert.True(fixture.TargetManager.TryGetActivity("10:1", out activity));
                Assert.Equal(1, activity.PendingRestoreAttempt);
                Assert.True(activity.RestoreNextRetryAt.HasValue, "首次恢复失败后应排下次重试。");

                // 模拟设备长时间离线，连续多次重试都失败，attempt 应持续递增、始终非终态。
                // 恢复为异步投递（复核 R07），每次提交后等待完成回调记录结果再进入下一轮。
                DateTime now = t0.AddSeconds(6);
                for (var i = 2; i <= 8; i++)
                {
                    now = activity.RestoreNextRetryAt.Value;
                    var retry = fixture.Service.ProcessRestoreRetries(now);
                    Assert.Equal(1, retry.RetriesProcessed);
                    var expectedAttempt = i;
                    SpinUntil(() => fixture.TargetManager.TryGetActivity("10:1", out activity) && activity.PendingRestoreAttempt == expectedAttempt, "重试结果未记录。");
                    Assert.True(fixture.TargetManager.TryGetActivity("10:1", out activity), "活动门目标必须保留，不能因重试次数转终态。");
                    Assert.Equal(i, activity.PendingRestoreAttempt);
                    Assert.True(activity.RestoreNextRetryAt.HasValue, "可重试错误必须始终排下一次重试，不得有最大次数限制。");
                }

                // 设备恢复在线，下一次重试应成功，并清除门目标活动。
                fixture.Gateway.ConfigureResult<GateControlResponse>("ControlGatewayAsync", new GateControlResponse { Success = true, Code = "OK", Message = "ok", GateIndex = 1, Command = GateControlCommand.Restore });
                now = activity.RestoreNextRetryAt.Value;
                var successRetry = fixture.Service.ProcessRestoreRetries(now);
                Assert.Equal(1, successRetry.RetriesProcessed);
                DoorTargetActivity cleared;
                SpinUntil(() => !fixture.TargetManager.TryGetActivity("10:1", out cleared), "恢复成功应清除门目标活动。");
            }
        }

        [TestCase]
        public static void Stage9RestoreRetry_NonRetryableError_DoesNotRetry()
        {
            // 不可重试错误（如非法门号等配置类问题）重试无意义，转终态需人工确认。
            using (var fixture = new Stage9Fixture(windowSeconds: 5, restoreRetryIntervalMs: 1000))
            {
                fixture.Gateway.ConfigureException("ControlGatewayAsync", new DeviceGatewayException("ControlGateway", SdkError.FromCode(4, "门号非法")));
                var t0 = new DateTime(2026, 1, 1, 8, 0, 0);

                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(t0);
                fixture.Service.ExpireWindows(t0.AddSeconds(5));

                DoorTargetActivity activity;
                SpinUntil(() => fixture.TargetManager.TryGetActivity("10:1", out activity) && activity.RestoreTerminalFailed, "不可重试失败应记录终态。");
                Assert.True(fixture.TargetManager.TryGetActivity("10:1", out activity));
                Assert.False(activity.RestoreNextRetryAt.HasValue, "不可重试错误应直接终态。");
                Assert.Equal(0, fixture.Service.ProcessRestoreRetries(t0.AddSeconds(100)).RetriesProcessed);
            }
        }

        [TestCase]
        public static void Stage9RestoreRetry_NonRetryableError_IsTerminalAndExcludedFromStopRestore()
        {
            using (var fixture = new Stage9Fixture(windowSeconds: 5, restoreRetryIntervalMs: 1000))
            {
                fixture.Gateway.ConfigureException("ControlGatewayAsync", new DeviceGatewayException("ControlGateway", SdkError.FromCode(4, "invalid door")));
                var t0 = new DateTime(2026, 1, 1, 8, 0, 0);

                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(t0);
                fixture.Service.ExpireWindows(t0.AddSeconds(5));

                DoorTargetActivity activity;
                SpinUntil(() => fixture.TargetManager.TryGetActivity("10:1", out activity) && activity.RestoreTerminalFailed, "不可重试失败应记录终态。");
                Assert.True(fixture.TargetManager.TryGetActivity("10:1", out activity));
                Assert.True(activity.RestoreTerminalFailed, "non-retryable restore failure must be explicit terminal state.");

                var beforeStopRestoreCount = fixture.Gateway.Calls.Count;
                var result = fixture.Service.RestoreActiveTargetsBestEffort(TimeSpan.FromSeconds(5));

                Assert.Equal(0, result.Total);
                Assert.Equal(beforeStopRestoreCount, fixture.Gateway.Calls.Count);
            }
        }

        [TestCase]
        public static void Stage9RestoreRetry_RetrySucceeds_ClearsPendingState()
        {
            using (var fixture = new Stage9Fixture(windowSeconds: 5, restoreRetryIntervalMs: 1000))
            {
                var t0 = new DateTime(2026, 1, 1, 8, 0, 0);
                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(t0);

                fixture.Gateway.ConfigureException("ControlGatewayAsync", new DeviceGatewayException("ControlGateway", SdkError.FromCode(7, "busy")));
                fixture.Service.ExpireWindows(t0.AddSeconds(5));
                DoorTargetActivity pendingBefore;
                SpinUntil(() => fixture.TargetManager.TryGetActivity("10:1", out pendingBefore) && pendingBefore.RestoreNextRetryAt.HasValue, "首次失败结果未记录。");
                Assert.True(fixture.TargetManager.TryGetActivity("10:1", out pendingBefore));
                Assert.True(pendingBefore.RestoreNextRetryAt.HasValue);

                fixture.Gateway.ConfigureResult<GateControlResponse>("ControlGatewayAsync", new GateControlResponse { Success = true, Code = "OK", Message = "ok", GateIndex = 1, Command = GateControlCommand.Restore });
                // 退避基于完成时刻（复核 F06）：用状态记录的下次重试时间驱动，而不是提交期虚拟时钟。
                var retry = fixture.Service.ProcessRestoreRetries(pendingBefore.RestoreNextRetryAt.Value);

                Assert.Equal(1, retry.RetriesProcessed);
                DoorTargetActivity cleared;
                SpinUntil(() => !fixture.TargetManager.TryGetActivity("10:1", out cleared), "恢复成功应清除门目标活动。");
            }
        }

        [TestCase]
        public static void Stage9RestoreRetry_RetryableFailure_UsesBackoffDelay()
        {
            // 可控时钟：验证退避从实际完成时刻起算，而不是提交时刻（复核 F06）。
            var controlled = new DateTime(2026, 1, 1, 8, 0, 0);
            using (var fixture = new Stage9Fixture(windowSeconds: 5, restoreRetryIntervalMs: 1000, clock: () => controlled))
            {
                fixture.Gateway.ConfigureException("ControlGatewayAsync", new DeviceGatewayException("ControlGateway", SdkError.FromCode(7, "busy")));
                var t0 = controlled;

                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(t0);
                fixture.Service.ExpireWindows(t0.AddSeconds(5));

                DoorTargetActivity firstFailure;
                SpinUntil(() => fixture.TargetManager.TryGetActivity("10:1", out firstFailure) && firstFailure.RestoreNextRetryAt.HasValue, "首次失败结果未记录。");
                Assert.True(fixture.TargetManager.TryGetActivity("10:1", out firstFailure));
                var firstRetryAt = firstFailure.RestoreNextRetryAt.Value;
                Assert.True(firstRetryAt >= t0.AddSeconds(1), "首次退避应从完成时刻起算。");

                // 推进时钟 5 秒模拟第二次失败发生在更晚时刻：退避必须基于完成时刻。
                controlled = controlled.AddSeconds(5);
                fixture.Service.ProcessRestoreRetries(firstRetryAt);

                DoorTargetActivity secondFailure;
                SpinUntil(() => fixture.TargetManager.TryGetActivity("10:1", out secondFailure) && secondFailure.PendingRestoreAttempt == 2, "第二次失败结果未记录。");
                Assert.True(fixture.TargetManager.TryGetActivity("10:1", out secondFailure));
                Assert.True(
                    secondFailure.RestoreNextRetryAt.Value >= controlled.AddSeconds(2),
                    "second retry should back off from the actual completion time.");
            }
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
