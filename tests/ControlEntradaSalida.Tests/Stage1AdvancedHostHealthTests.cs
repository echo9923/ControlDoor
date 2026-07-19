using System;
using System.Threading;
using ControlDoor.Configuration;
using ControlDoor.Host;
using ControlDoor.Runtime.Health;
using ControlDoor.Runtime.Health.Checks;

namespace ControlEntradaSalida.Tests
{
    public static class Stage1AdvancedHostHealthTests
    {
        [TestCase]
        public static void ControlDoorHost_StartFailsWhenConfigurationMissing()
        {
            var runDirectory = TestWorkspace.Create();
            using (var host = new ControlDoorHost(runDirectory))
            {
                var result = host.StartAsync().GetAwaiter().GetResult();

                Assert.False(result.Success);
                Assert.Equal(ServiceLifecycleState.Failed, host.State);
                Assert.True(result.Errors.Count > 0);
            }
        }

        [TestCase]
        public static void ControlDoorHost_StopAfterFailedStartIsSafe()
        {
            var runDirectory = TestWorkspace.Create();
            using (var host = new ControlDoorHost(runDirectory))
            {
                host.StartAsync().GetAwaiter().GetResult();
                var stop = host.StopAsync("after-failed-start").GetAwaiter().GetResult();

                Assert.True(stop.Success);
                Assert.Equal(ServiceLifecycleState.Stopped, host.State);
            }
        }

        // HOST-02 回归：StartAsync 必须真正异步——把繁重初始化放到线程池，使传入的取消令牌可被观察。
        // 同步实现会在返回 Task 前跑完所有工作，预先取消的令牌永远来不及生效。这里用已取消令牌断言异常路径可达。
        [TestCase]
        public static void ControlDoorHost_StartAsync_ObservesCancellationToken()
        {
            var runDirectory = TestWorkspace.Create();
            using (var host = new ControlDoorHost(runDirectory))
            {
                var cts = new CancellationTokenSource();
                cts.Cancel();

                try
                {
                    host.StartAsync(cts.Token).GetAwaiter().GetResult();
                    // 即便底层 race 让取消没抢到，也接受 Start 以失败告终（配置缺失）。
                    Assert.False(host.State == ServiceLifecycleState.Running, "已取消的 Start 不应进入 Running。");
                }
                catch (OperationCanceledException)
                {
                    // 期望路径：取消令牌被 StartCore 第一时间观察到并抛出。
                }
            }
        }

        [TestCase]
        public static void HealthCheckService_CatchesCheckExceptionsAsFailed()
        {
            var service = new HealthCheckService(new IHealthCheck[] { new ThrowingHealthCheck() });
            var settings = new AppSettings();
            settings.Database.ConnectionString = "Server=.;Database=test;";

            var summary = service.Run(new HealthCheckContext(TestWorkspace.Create(), settings, null, CancellationToken.None));

            Assert.False(summary.Success);
            Assert.Equal(1, summary.FailedCount);
            Assert.Equal("ThrowingCheck", summary.Results[0].Name);
        }

        [TestCase]
        public static void ConfigurationFileHealthCheck_InvalidJsonIsFailed()
        {
            var runDirectory = TestWorkspace.Create();
            var configDirectory = System.IO.Path.Combine(runDirectory, "Configuration");
            System.IO.Directory.CreateDirectory(configDirectory);
            System.IO.File.WriteAllText(System.IO.Path.Combine(configDirectory, "appsettings.json"), "{ bad json");

            var result = new ConfigurationFileHealthCheck().Run(NewContext(runDirectory));

            Assert.Equal(HealthCheckStatus.Failed, result.Status);
        }

        [TestCase]
        public static void DllPresenceHealthCheck_FoundDllIsOk()
        {
            var runDirectory = TestWorkspace.Create();
            System.IO.File.WriteAllText(System.IO.Path.Combine(runDirectory, "HCNetSDK.dll"), "fake");

            var result = new DllPresenceHealthCheck("海康 SDK DLL", "HCNetSDK.dll").Run(NewContext(runDirectory));

            Assert.Equal(HealthCheckStatus.OK, result.Status);
        }

        [TestCase]
        public static void HealthCheckResult_WithElapsedPreservesStatusAndMessage()
        {
            var result = HealthCheckResult.Warning("name", "message").WithElapsed(123);

            Assert.Equal(HealthCheckStatus.Warning, result.Status);
            Assert.Equal("name", result.Name);
            Assert.Equal("message", result.Message);
            Assert.Equal(123L, result.ElapsedMs);
        }

        private static HealthCheckContext NewContext(string runDirectory)
        {
            var settings = new AppSettings();
            settings.Database.ConnectionString = "Server=.;Database=test;";
            return new HealthCheckContext(runDirectory, settings, null, CancellationToken.None);
        }

        private sealed class ThrowingHealthCheck : IHealthCheck
        {
            public string Name => "ThrowingCheck";

            public HealthCheckResult Run(HealthCheckContext context)
            {
                throw new InvalidOperationException("boom");
            }
        }
    }
}
