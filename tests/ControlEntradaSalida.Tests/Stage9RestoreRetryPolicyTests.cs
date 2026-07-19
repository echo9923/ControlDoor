using System;
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
                // 恢复任务异步 fire-and-observe（AIOP-05），等待完成回调登记下次重试。
                Stage9Fixture.SpinFor(() =>
                    fixture.TargetManager.TryGetActivity("10:1", out var after) &&
                    after.PendingRestoreAttempt == 1 &&
                    after.RestoreNextRetryAt.HasValue,
                    "首次恢复失败后应排下次重试。");

                fixture.TargetManager.TryGetActivity("10:1", out var activity);

                // 模拟设备长时间离线，连续多次重试都失败，attempt 应持续递增、始终非终态。
                DateTime now = t0.AddSeconds(6);
                for (var i = 2; i <= 8; i++)
                {
                    now = activity.RestoreNextRetryAt.Value;
                    var retry = fixture.Service.ProcessRestoreRetries(now);
                    Assert.Equal(1, retry.RetriesProcessed);
                    var expected = i;
                    Stage9Fixture.SpinFor(() =>
                        fixture.TargetManager.TryGetActivity("10:1", out var current) &&
                        current.PendingRestoreAttempt == expected &&
                        current.RestoreNextRetryAt.HasValue,
                        "可重试错误必须始终排下一次重试，不得有最大次数限制。");
                    Assert.True(fixture.TargetManager.TryGetActivity("10:1", out activity), "活动门目标必须保留，不能因重试次数转终态。");
                }

                // 设备恢复在线，下一次重试应成功，并清除门目标活动。
                fixture.Gateway.ConfigureResult<GateControlResponse>("ControlGatewayAsync", new GateControlResponse { Success = true, Code = "OK", Message = "ok", GateIndex = 1, Command = GateControlCommand.Restore });
                now = activity.RestoreNextRetryAt.Value;
                var successRetry = fixture.Service.ProcessRestoreRetries(now);
                Assert.Equal(1, successRetry.RetriesProcessed);
                Stage9Fixture.SpinFor(() => !fixture.TargetManager.TryGetActivity("10:1", out _), "恢复成功应清除门目标活动。");
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

                // 异步 fire-and-observe：等待终态写入。
                Stage9Fixture.SpinFor(() =>
                    fixture.TargetManager.TryGetActivity("10:1", out var term) &&
                    !term.RestoreNextRetryAt.HasValue &&
                    term.RestoreTerminalFailed,
                    "不可重试错误应直接终态。");

                Assert.True(fixture.TargetManager.TryGetActivity("10:1", out var activity));
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

                // 异步 fire-and-observe：等待终态写入。
                Stage9Fixture.SpinFor(() =>
                    fixture.TargetManager.TryGetActivity("10:1", out var term) && term.RestoreTerminalFailed,
                    "non-retryable restore failure must be explicit terminal state.");

                Assert.True(fixture.TargetManager.TryGetActivity("10:1", out var activity));
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
                Stage9Fixture.SpinFor(() =>
                    fixture.TargetManager.TryGetActivity("10:1", out var pendingBefore) && pendingBefore.RestoreNextRetryAt.HasValue,
                    "首次失败应排下次重试。");

                fixture.Gateway.ConfigureResult<GateControlResponse>("ControlGatewayAsync", new GateControlResponse { Success = true, Code = "OK", Message = "ok", GateIndex = 1, Command = GateControlCommand.Restore });
                var retry = fixture.Service.ProcessRestoreRetries(t0.AddSeconds(7));

                Assert.Equal(1, retry.RetriesProcessed);
                Stage9Fixture.SpinFor(() => !fixture.TargetManager.TryGetActivity("10:1", out _), "恢复成功应清除门目标活动。");
            }
        }

        [TestCase]
        public static void Stage9RestoreRetry_RetryableFailure_UsesBackoffDelay()
        {
            using (var fixture = new Stage9Fixture(windowSeconds: 5, restoreRetryIntervalMs: 1000))
            {
                fixture.Gateway.ConfigureException("ControlGatewayAsync", new DeviceGatewayException("ControlGateway", SdkError.FromCode(7, "busy")));
                var t0 = new DateTime(2026, 1, 1, 8, 0, 0);

                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(t0);
                fixture.Service.ExpireWindows(t0.AddSeconds(5));

                Stage9Fixture.SpinFor(() =>
                    fixture.TargetManager.TryGetActivity("10:1", out var firstFailure) && firstFailure.RestoreNextRetryAt.HasValue,
                    "首次失败应排下次重试。");
                fixture.TargetManager.TryGetActivity("10:1", out var firstFailureAfterWait);
                var firstRetryAt = firstFailureAfterWait.RestoreNextRetryAt.Value;

                fixture.Service.ProcessRestoreRetries(firstRetryAt);

                Stage9Fixture.SpinFor(() =>
                    fixture.TargetManager.TryGetActivity("10:1", out var secondFailure) &&
                    secondFailure.RestoreNextRetryAt.HasValue &&
                    secondFailure.RestoreNextRetryAt.Value > firstRetryAt,
                    "二次失败应排新的下次重试。");
                fixture.TargetManager.TryGetActivity("10:1", out var secondFailureAfterWait);
                Assert.True(
                    secondFailureAfterWait.RestoreNextRetryAt.Value >= firstRetryAt.AddSeconds(2),
                    "second retry should back off beyond the fixed first retry interval.");
            }
        }
        [TestCase]
        public static void Stage9RestoreRetry_RetryableFailure_UsesConfiguredInitialDelay()
        {
            using (var fixture = new Stage9Fixture(windowSeconds: 5, restoreRetryIntervalMs: 750))
            {
                fixture.Gateway.ConfigureException("ControlGatewayAsync", new DeviceGatewayException("ControlGateway", SdkError.FromCode(7, "busy")));
                var t0 = new DateTime(2026, 1, 1, 8, 0, 0);

                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(t0);
                fixture.Service.ExpireWindows(t0.AddSeconds(5));

                Stage9Fixture.SpinFor(() =>
                    fixture.TargetManager.TryGetActivity("10:1", out var failure) && failure.RestoreNextRetryAt.HasValue,
                    "首次失败应按配置安排下次重试。");
                fixture.TargetManager.TryGetActivity("10:1", out var activity);

                Assert.Equal(t0.AddSeconds(5).AddMilliseconds(750), activity.RestoreNextRetryAt.Value);
            }
        }

    }
}
