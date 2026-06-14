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
                Assert.True(fixture.TargetManager.TryGetActivity("10:1", out var activity));
                Assert.Equal(1, activity.PendingRestoreAttempt);
                Assert.True(activity.RestoreNextRetryAt.HasValue, "首次恢复失败后应排下次重试。");

                // 模拟设备长时间离线，连续多次重试都失败，attempt 应持续递增、始终非终态。
                DateTime now = t0.AddSeconds(6);
                for (var i = 2; i <= 8; i++)
                {
                    var retry = fixture.Service.ProcessRestoreRetries(now);
                    Assert.Equal(1, retry.RetriesProcessed);
                    Assert.True(fixture.TargetManager.TryGetActivity("10:1", out activity), "活动门目标必须保留，不能因重试次数转终态。");
                    Assert.Equal(i, activity.PendingRestoreAttempt);
                    Assert.True(activity.RestoreNextRetryAt.HasValue, "可重试错误必须始终排下一次重试，不得有最大次数限制。");
                    now = now.AddSeconds(1);
                }

                // 设备恢复在线，下一次重试应成功，并清除门目标活动。
                fixture.Gateway.ConfigureResult<GateControlResponse>("ControlGatewayAsync", new GateControlResponse { Success = true, Code = "OK", Message = "ok", GateIndex = 1, Command = GateControlCommand.Restore });
                var successRetry = fixture.Service.ProcessRestoreRetries(now);
                Assert.Equal(1, successRetry.RetriesProcessed);
                Assert.False(fixture.TargetManager.TryGetActivity("10:1", out var cleared), "恢复成功应清除门目标活动。");
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

                Assert.True(fixture.TargetManager.TryGetActivity("10:1", out var activity));
                Assert.False(activity.RestoreNextRetryAt.HasValue, "不可重试错误应直接终态。");
                Assert.Equal(0, fixture.Service.ProcessRestoreRetries(t0.AddSeconds(100)).RetriesProcessed);
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
                Assert.True(fixture.TargetManager.TryGetActivity("10:1", out var pendingBefore));
                Assert.True(pendingBefore.RestoreNextRetryAt.HasValue);

                fixture.Gateway.ConfigureResult<GateControlResponse>("ControlGatewayAsync", new GateControlResponse { Success = true, Code = "OK", Message = "ok", GateIndex = 1, Command = GateControlCommand.Restore });
                var retry = fixture.Service.ProcessRestoreRetries(t0.AddSeconds(7));

                Assert.Equal(1, retry.RetriesProcessed);
                Assert.False(fixture.TargetManager.TryGetActivity("10:1", out var cleared), "恢复成功应清除门目标活动。");
            }
        }
    }
}
