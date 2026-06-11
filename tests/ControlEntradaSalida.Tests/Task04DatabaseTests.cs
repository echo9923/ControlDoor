using ControlDoor.Configuration;
using ControlDoor.Database;

namespace ControlEntradaSalida.Tests
{
    public static class Task04DatabaseTests
    {
        [TestCase]
        public static void SqlServerDatabase_EnsureReadOnly_AllowsSelect()
        {
            SqlServerDatabase.EnsureReadOnly("SELECT TOP 0 * FROM dbo.devices");
            SqlServerDatabase.EnsureReadOnly("select 1");
        }

        [TestCase]
        public static void SqlServerDatabase_EnsureReadOnly_RejectsMutatingSql()
        {
            var failed = false;
            try
            {
                SqlServerDatabase.EnsureReadOnly("UPDATE dbo.devices SET enabled = 1");
            }
            catch
            {
                failed = true;
            }

            Assert.True(failed);
        }

        [TestCase]
        public static void DatabaseHealthCheck_UsesOnlyReadOnlySql()
        {
            var fake = new RecordingDatabaseClient();
            var report = new DatabaseHealthCheck(fake).Run();

            Assert.True(report.Success);
            Assert.True(fake.Commands.Count >= 5);
            foreach (var command in fake.Commands)
            {
                SqlServerDatabase.EnsureReadOnly(command.CommandText);
            }
        }

        [TestCase]
        public static void DatabaseHealthCheck_RequiredTableFailure_FailsReport()
        {
            var fake = new RecordingDatabaseClient();
            fake.FailOperationName = "ReadTable:dbo.devices";

            var report = new DatabaseHealthCheck(fake).Run();

            Assert.False(report.Success);
        }

        [TestCase]
        public static void DatabaseHealthCheck_OptionalTableFailure_DoesNotFailStage1Report()
        {
            var fake = new RecordingDatabaseClient();
            fake.FailOperationName = "ReadOptionalTable:dbo.attendance_gate_v2";

            var report = new DatabaseHealthCheck(fake).Run();

            Assert.True(report.Success);
        }

        [TestCase]
        public static void ConfigurationValidator_DatabaseRetryDefaultsAreValidated()
        {
            var settings = new AppSettings();
            settings.Database.ConnectionString = "Server=.;Database=test;";
            settings.Database.StartupRetryCount = 0;
            settings.Database.StartupRetryIntervalSeconds = 0;

            var result = new ConfigurationValidator().Validate(settings);

            Assert.True(result.Success);
            Assert.Equal(10, result.Settings.Database.StartupRetryCount);
            Assert.Equal(60, result.Settings.Database.StartupRetryIntervalSeconds);
        }
    }
}
