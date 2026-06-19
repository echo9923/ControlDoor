using System;
using System.Linq;
using ControlDoor.Configuration;
using ControlDoor.Database;
using ControlDoor.FaceEvents;

namespace ControlEntradaSalida.Tests
{
    public static class Bug6DatabaseReadOnlyGuardTests
    {
        [TestCase]
        public static void ReadOnlyDatabaseClient_ParamsExecuteNonQuery_RejectsMutatingSqlBeforeInnerClient()
        {
            var inner = new RecordingDatabaseClient();
            var readOnly = new ReadOnlyDatabaseClient(inner);

            AssertRejected(
                () => readOnly.ExecuteNonQuery(
                    "Bug6.UpdateWithParams",
                    "UPDATE dbo.system_users SET access_permission = @value",
                    new DatabaseParameter("@value", 1)),
                "Expected read-only params ExecuteNonQuery to reject UPDATE before opening a connection.");
            Assert.Equal(0, inner.Commands.Count);
        }

        [TestCase]
        public static void SqlServerDatabase_WriteClient_DoesNotApplyReadOnlyGuardToParameterizedBusinessWrite()
        {
            var database = new SqlServerDatabase(new DatabaseOptions
            {
                ConnectionString = "not a valid connection string"
            });

            var record = database.ExecuteNonQuery(
                "Bug6.BusinessWrite",
                "INSERT INTO dbo.attendance_gate_v2(id, username) VALUES(@id, @username)",
                new DatabaseParameter("@id", 1),
                new DatabaseParameter("@username", "10001"));

            Assert.Equal("Bug6.BusinessWrite", record.OperationName);
            Assert.NotNull(record.Error);
            Assert.False(record.Error.Message.Contains("SELECT"), "Business write client should reach connection-string validation, not the read-only guard.");
        }

        [TestCase]
        public static void SqlServerDatabase_EnsureReadOnly_RejectsBatchAndCommentBypass()
        {
            AssertReadOnlyRejected("SELECT 1; SELECT 2");
            AssertReadOnlyRejected("SELECT 1 -- harmless");
            AssertReadOnlyRejected("SELECT 1 /* harmless */");
            AssertReadOnlyRejected("SELECT 1/*comment*/");
        }

        [TestCase]
        public static void SqlServerDatabase_EnsureReadOnly_RejectsDangerousCommands()
        {
            AssertReadOnlyRejected("SELECT * FROM dbo.system_users WHERE id IN (SELECT id FROM dbo.system_users); INSERT INTO dbo.system_users(username) VALUES('x')");
            AssertReadOnlyRejected("SELECT * FROM dbo.system_users WHERE id IN (SELECT id FROM dbo.system_users); UPDATE dbo.system_users SET deleted = 1");
            AssertReadOnlyRejected("SELECT * FROM dbo.system_users WHERE id IN (SELECT id FROM dbo.system_users); DELETE FROM dbo.system_users");
            AssertReadOnlyRejected("SELECT * FROM dbo.system_users WHERE id IN (SELECT id FROM dbo.system_users); DROP TABLE dbo.system_users");
            AssertReadOnlyRejected("SELECT * FROM dbo.system_users WHERE id IN (SELECT id FROM dbo.system_users); EXEC dbo.RefreshUsers");
            AssertReadOnlyRejected("SELECT * FROM dbo.system_users WHERE id IN (SELECT id FROM dbo.system_users); MERGE dbo.system_users AS target USING dbo.system_users AS source ON 1 = 0 WHEN NOT MATCHED THEN INSERT(username) VALUES('x')");
        }

        [TestCase]
        public static void ReadOnlyDatabaseClient_RejectsMutatingSqlThroughAllExecutionPaths()
        {
            var inner = new RecordingDatabaseClient();
            using (var readOnly = new ReadOnlyDatabaseClient(inner))
            {
                AssertRejected(
                    () => readOnly.ExecuteScalar("Bug6.Scalar", "DELETE FROM dbo.system_users"),
                    "Expected ExecuteScalar to reject DELETE.");
                AssertRejected(
                    () => readOnly.ExecuteNonQuery("Bug6.NonQuery", "INSERT INTO dbo.system_users(username) VALUES('x')"),
                    "Expected ExecuteNonQuery to reject INSERT.");
                AssertRejected(
                    () => readOnly.ExecuteNonQuery("Bug6.NonQueryParams", "UPDATE dbo.system_users SET deleted = @deleted", new DatabaseParameter("@deleted", 1)),
                    "Expected params ExecuteNonQuery to reject UPDATE.");
                AssertRejected(
                    () => readOnly.ExecuteQuery("Bug6.Query", "DROP TABLE dbo.system_users"),
                    "Expected ExecuteQuery to reject DROP.");
            }

            Assert.Equal(0, inner.Commands.Count);
        }

        [TestCase]
        public static void BusinessWriteDatabaseClient_StillAllowsFaceEventRepositoryInsert()
        {
            var database = new RecordingDatabaseClient();
            var repository = new FaceEventRepository(database, new SnapshotStorage(TestWorkspace.Create()));

            var result = repository.InsertEvent(NewEvent());

            Assert.Equal(FaceEventInsertStatus.Inserted, result.Status);
            Assert.True(database.Commands.Any(command => command.OperationName == "InsertFaceEvent"));
            Assert.Contains("INSERT INTO dbo.attendance_gate_v2", database.Commands.Single(command => command.OperationName == "InsertFaceEvent").CommandText);
        }

        private static void AssertReadOnlyRejected(string sql)
        {
            AssertRejected(
                () => SqlServerDatabase.EnsureReadOnly(sql),
                "Expected SQL to be rejected: " + sql);
        }

        private static void AssertRejected(Action action, string message)
        {
            var failed = false;
            try
            {
                action();
            }
            catch (InvalidOperationException)
            {
                failed = true;
            }

            Assert.True(failed, message);
        }

        private static AcsFaceEvent NewEvent()
        {
            var faceEvent = new AcsFaceEvent
            {
                EventId = 60000000001,
                EmployeeId = "10001",
                EventTime = new DateTime(2026, 6, 19, 8, 9, 10),
                Direction = 1,
                DeviceName = "Front Gate",
                DeviceSn = "SN-6",
                DeviceId = 6,
                DeviceIp = "192.168.1.10",
                CardNo = "C100",
                EventType = 75,
                AuthResult = "OK",
                RawPayload = "{}",
                Source = AcsAlarmEventSource.Realtime
            };
            return faceEvent;
        }
    }
}
