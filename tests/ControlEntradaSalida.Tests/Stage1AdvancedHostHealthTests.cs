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
