using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using ControlDoor.Configuration;
using ControlDoor.Database;
using ControlDoor.Runtime.Health;
using ControlDoor.Runtime.Health.Checks;

namespace ControlEntradaSalida.Tests
{
    public static class Task07HealthCheckTests
    {
        [TestCase]
        public static void HealthCheckSummary_CountsStatuses()
        {
            var summary = new HealthCheckSummary();
            summary.Add(HealthCheckResult.Ok("ok", "ok"));
            summary.Add(HealthCheckResult.Warning("warn", "warn"));
            summary.Add(HealthCheckResult.Failed("fail", "fail"));

            Assert.Equal(1, summary.OkCount);
            Assert.Equal(1, summary.WarningCount);
            Assert.Equal(1, summary.FailedCount);
            Assert.False(summary.Success);
        }

        [TestCase]
        public static void DirectoryHealthCheck_RequiredDirectoryFailure_BlocksStartup()
        {
            var runDirectory = TestWorkspace.Create();
            var invalidDirectory = System.IO.Path.Combine(runDirectory, "file-path");
            System.IO.File.WriteAllText(invalidDirectory, "not a directory");
            var check = new DirectoryHealthCheck("日志目录", invalidDirectory, required: true);

            var result = check.Run(NewContext(runDirectory));

            Assert.Equal(HealthCheckStatus.Failed, result.Status);
            Assert.True(result.BlocksStartup);
        }

        [TestCase]
        public static void DllPresenceHealthCheck_MissingSdk_IsWarningInStage1()
        {
            var runDirectory = TestWorkspace.Create();
            var check = new DllPresenceHealthCheck("海康 SDK DLL", "sdk\\Hikvision\\HCNetSDK.dll");

            var result = check.Run(NewContext(runDirectory));

            Assert.Equal(HealthCheckStatus.Warning, result.Status);
        }

        [TestCase]
        public static void PortHealthCheck_OccupiedPort_Fails()
        {
            var listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            try
            {
                var settings = new AppSettings();
                settings.Database.ConnectionString = "Server=.;Database=test;";
                settings.Service.GrpcListenPort = port;
                var context = new HealthCheckContext(TestWorkspace.Create(), settings, null, CancellationToken.None);

                var result = new PortHealthCheck().Run(context);

                Assert.Equal(HealthCheckStatus.Failed, result.Status);
            }
            finally
            {
                listener.Stop();
            }
        }

        [TestCase]
        public static void HealthCheckService_Stage1OptionalDatabaseTablesRemainWarnings()
        {
            var runDirectory = TestWorkspace.Create();
            TestWorkspace.WriteConfig(runDirectory, @"{""Database"":{""ConnectionString"":""Server=.;Database=test;""}}");
            var settings = new ConfigurationLoader().Load(runDirectory).Settings;
            var database = new RecordingDatabaseClient { FailOperationName = "ReadOptionalTable:dbo.face_event_checkpoint" };

            var summary = HealthCheckService
                .CreateStage1(runDirectory, database)
                .Run(new HealthCheckContext(runDirectory, settings, null, CancellationToken.None));

            Assert.True(summary.Success);
            Assert.True(summary.WarningCount >= 1);
        }

        [TestCase]
        public static void DatabaseHealthCheckItem_RequiredDatabaseFailure_Fails()
        {
            var database = new RecordingDatabaseClient { FailOperationName = "ReadTable:dbo.devices" };
            var result = new DatabaseHealthCheckItem(database).Run(NewContext(TestWorkspace.Create()));

            Assert.Equal(HealthCheckStatus.Failed, result.Status);
        }

        private static HealthCheckContext NewContext(string runDirectory)
        {
            var settings = new AppSettings();
            settings.Database.ConnectionString = "Server=.;Database=test;";
            return new HealthCheckContext(runDirectory, settings, null, CancellationToken.None);
        }
    }
}
