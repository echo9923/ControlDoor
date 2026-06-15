using System;
using System.IO;
using System.Linq;
using System.Reflection;
using ControlDoor;
using ControlDoor.Database;
using ControlDoor.GrpcApi;
using ControlDoor.Host;
using ControlDoor.Runtime.Health;

namespace ControlEntradaSalida.Tests
{
    public static class Task08AcceptanceTests
    {
        [TestCase]
        public static void Acceptance_OutputAssemblyName_IsControlDoorExe()
        {
            var assemblyName = typeof(ServiceIdentity).Assembly.GetName().Name;

            Assert.Equal("ControlDoor", assemblyName);
        }

        [TestCase]
        public static void Acceptance_ServiceName_IsControlDoor()
        {
            Assert.Equal("ControlDoor", ServiceIdentity.ServiceName);
            Assert.Equal("ControlDoor", ServiceIdentity.DisplayName);
        }

        [TestCase]
        public static void Acceptance_Stage4AccessControlGrpcMethodsAreRegisteredByContract()
        {
            Assert.Equal("/device.AccessControlService/GetDeviceStatus", AccessControlGrpcService.GetDeviceStatusFullName);
            Assert.Equal("/device.AccessControlService/AddDevice", AccessControlGrpcService.AddDeviceFullName);
            Assert.Equal("/device.AccessControlService/DeleteDevice", AccessControlGrpcService.DeleteDeviceFullName);
            Assert.Equal("/device.AccessControlService/DisconnectDevice", AccessControlGrpcService.DisconnectDeviceFullName);
            Assert.Equal("/device.AccessControlService/ReconnectDevice", AccessControlGrpcService.ReconnectDeviceFullName);
        }

        [TestCase]
        public static void Acceptance_DatabaseGuardRejectsSchemaChanges()
        {
            AssertMutatingSqlFails("ALTER TABLE dbo.attendance_gate_v2 ADD test int");
            AssertMutatingSqlFails("CREATE TABLE dbo.stage1_test(id int)");
            AssertMutatingSqlFails("DROP TABLE dbo.stage1_test");
        }

        [TestCase]
        public static void Acceptance_ValidateConfigHealthChecksCanPassWithWarnings()
        {
            var runDirectory = TestWorkspace.Create();
            TestWorkspace.WriteConfig(runDirectory, @"{""Service"":{""GrpcListenPort"":" + TestWorkspace.FindAvailablePort() + @"},""Database"":{""ConnectionString"":""Server=.;Database=test;""}}");
            var settings = new ControlDoor.Configuration.ConfigurationLoader().Load(runDirectory).Settings;

            var summary = HealthCheckService
                .CreateStage1(runDirectory)
                .Run(new HealthCheckContext(runDirectory, settings, null, System.Threading.CancellationToken.None));

            Assert.True(summary.Success);
            Assert.True(summary.WarningCount >= 1);
        }

        [TestCase]
        public static void Acceptance_Stage1DocumentIsIndexedInAgents()
        {
            var root = FindRepositoryRoot();
            var agents = File.ReadAllText(Path.Combine(root, "AGENTS.md"));

            Assert.Contains("docs/stage1/task08.md", agents);
        }

        private static void AssertMutatingSqlFails(string sql)
        {
            var failed = false;
            try
            {
                SqlServerDatabase.EnsureReadOnly(sql);
            }
            catch
            {
                failed = true;
            }

            Assert.True(failed, "Expected mutating SQL to fail: " + sql);
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            return @"D:\codeproject\c#\ControlDoor";
        }
    }
}
