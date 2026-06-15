using System.Linq;
using ControlDoor.Database;

namespace ControlEntradaSalida.Tests
{
    public static class Stage1AdvancedDatabaseTests
    {
        [TestCase]
        public static void DatabaseHealthCheck_CommandOrderStartsWithConnectionAndDatabaseName()
        {
            var fake = new RecordingDatabaseClient();

            new DatabaseHealthCheck(fake).Run();

            Assert.Equal("ConnectionTest", fake.Commands[0].OperationName);
            Assert.Equal("SELECT 1", fake.Commands[0].CommandText);
            Assert.Equal("CurrentDatabase", fake.Commands[1].OperationName);
            Assert.Equal("SELECT DB_NAME()", fake.Commands[1].CommandText);
        }

        [TestCase]
        public static void DatabaseHealthCheck_RequiredTablesAreCheckedBeforeOptionalTables()
        {
            var fake = new RecordingDatabaseClient();

            new DatabaseHealthCheck(fake).Run();

            var requiredIndex = fake.Commands.ToList().FindIndex(item => item.OperationName == "ReadTable:dbo.system_users");
            var optionalIndex = fake.Commands.ToList().FindIndex(item => item.OperationName == "ReadOptionalTable:dbo.face_event_checkpoint");

            Assert.True(requiredIndex > 0);
            Assert.True(optionalIndex > requiredIndex);
        }

        [TestCase]
        public static void SqlServerDatabase_EnsureReadOnly_RejectsStoredProcedureExecution()
        {
            AssertSqlRejected("EXEC dbo.DoSomething");
            AssertSqlRejected("execute dbo.DoSomething");
        }

        [TestCase]
        public static void SqlServerDatabase_EnsureReadOnly_RejectsSelectContainingMutatingKeyword()
        {
            AssertSqlRejected("SELECT * FROM dbo.system_users; DROP TABLE dbo.system_users");
            AssertSqlRejected("SELECT * FROM dbo.system_users WHERE name = 'ALTER'");
        }

        [TestCase]
        public static void SqlServerDatabase_EnsureReadOnly_RejectsEmptySql()
        {
            AssertSqlRejected("");
            AssertSqlRejected("   ");
        }

        [TestCase]
        public static void DatabaseHealthReport_OptionalFailureWithRequiredSuccess_IsSuccess()
        {
            var fake = new RecordingDatabaseClient { FailOperationName = "ReadOptionalTable:dbo.face_event_checkpoint" };

            var report = new DatabaseHealthCheck(fake).Run();

            Assert.True(report.Success);
            Assert.True(report.Commands.Any(command => command.Error != null));
        }

        private static void AssertSqlRejected(string sql)
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

            Assert.True(failed, "Expected SQL to be rejected: " + sql);
        }
    }
}
