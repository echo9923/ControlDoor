using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ControlDoor.FaceEvents;

namespace ControlEntradaSalida.Tests
{
    public static class Stage7FaceEventRepositoryTests
    {
        [TestCase]
        public static void FaceEventRepository_InsertEvent_MapsAllFields()
        {
            var database = new RecordingDatabaseClient();
            database.QueryRowsByOperation["LookupFaceEventNickname"] = new List<IReadOnlyDictionary<string, object>>
            {
                new Dictionary<string, object> { ["nickname"] = "张三" }
            };
            var repository = NewRepository(database, out var storage);
            var faceEvent = NewEvent();

            var result = repository.InsertEvent(faceEvent);

            Assert.Equal(FaceEventInsertStatus.Inserted, result.Status);
            Assert.True(Path.IsPathRooted(result.SnapshotPath), "snapshot path must be absolute");
            Assert.True(File.Exists(result.SnapshotPath));
            var insert = database.Commands.Single(item => item.OperationName == "InsertFaceEvent");
            Assert.Contains("INSERT INTO dbo.attendance_gate_v2", insert.CommandText);
            Assert.Contains("username=10001", insert.CommandText);
            Assert.Contains("nickname=张三", insert.CommandText);
            Assert.Contains("snapshot_path=" + result.SnapshotPath, insert.CommandText);
            Assert.Contains("creator=ControlDoor", insert.CommandText);
            Assert.Contains("tenant_id=1", insert.CommandText);
        }

        [TestCase]
        public static void FaceEventRepository_DuplicateQuery_ReturnsDuplicateWithoutInsert()
        {
            var database = new RecordingDatabaseClient();
            database.QueryRowsByOperation["CheckFaceEventDuplicate"] = new List<IReadOnlyDictionary<string, object>>
            {
                new Dictionary<string, object> { ["id"] = 70000000345L }
            };
            var repository = NewRepository(database, out var _);

            var result = repository.InsertEvent(NewEvent());

            Assert.Equal(FaceEventInsertStatus.Duplicate, result.Status);
            Assert.False(database.Commands.Any(item => item.OperationName == "InsertFaceEvent"));
        }

        [TestCase]
        public static void FaceEventRepository_UniqueKeyError_ReturnsDuplicate()
        {
            var database = new RecordingDatabaseClient
            {
                FailOperationName = "InsertFaceEvent",
                FailSqlErrorNumber = 2627
            };
            var repository = NewRepository(database, out var _);

            var result = repository.InsertEvent(NewEvent());

            Assert.Equal(FaceEventInsertStatus.Duplicate, result.Status);
        }

        [TestCase]
        public static void FaceEventRepository_DatabaseFailure_ReturnsRetryableFailure()
        {
            var database = new RecordingDatabaseClient
            {
                FailOperationName = "InsertFaceEvent",
                FailSqlErrorNumber = -2,
                FailCanRetry = true
            };
            var repository = NewRepository(database, out var _);

            var result = repository.InsertEvent(NewEvent());

            Assert.Equal(FaceEventInsertStatus.RetryableFailure, result.Status);
        }

        [TestCase]
        public static void FaceEventRepository_NoNickname_StillInserts()
        {
            var database = new RecordingDatabaseClient();
            var repository = NewRepository(database, out var _);

            var result = repository.InsertEvent(NewEvent());

            Assert.Equal(FaceEventInsertStatus.Inserted, result.Status);
            var insert = database.Commands.Single(item => item.OperationName == "InsertFaceEvent");
            Assert.Contains("nickname=", insert.CommandText);
        }

        [TestCase]
        public static void FaceEventRepository_EmployeeIdWithLeadingZero_IsPreserved()
        {
            var database = new RecordingDatabaseClient();
            var repository = NewRepository(database, out var _);
            var faceEvent = NewEvent();
            faceEvent.EmployeeId = "0976";

            var result = repository.InsertEvent(faceEvent);

            Assert.Equal(FaceEventInsertStatus.Inserted, result.Status);
            var insert = database.Commands.Single(item => item.OperationName == "InsertFaceEvent");
            Assert.Contains("username=0976", insert.CommandText);
        }

        [TestCase]
        public static void FaceEventRepository_FieldTooLong_IsTruncated()
        {
            var database = new RecordingDatabaseClient();
            var repository = NewRepository(database, out var _);
            var faceEvent = NewEvent();
            faceEvent.EmployeeId = new string('a', 80);
            faceEvent.DeviceName = new string('b', 150);

            var result = repository.InsertEvent(faceEvent);

            Assert.Equal(FaceEventInsertStatus.Inserted, result.Status);
            Assert.Equal(30, faceEvent.EmployeeId.Length);
            Assert.Equal(100, faceEvent.DeviceName.Length);
        }

        private static FaceEventRepository NewRepository(RecordingDatabaseClient database, out SnapshotStorage storage)
        {
            var workspace = TestWorkspace.Create();
            storage = new SnapshotStorage(workspace);
            return new FaceEventRepository(database, storage);
        }

        private static AcsFaceEvent NewEvent()
        {
            var faceEvent = new AcsFaceEvent
            {
                EventId = 70000000345,
                EmployeeId = "10001",
                EventTime = new DateTime(2026, 6, 13, 8, 9, 10),
                Direction = 1,
                DeviceName = "Front Gate",
                DeviceSn = "SN-7",
                DeviceId = 7,
                DeviceIp = "192.168.1.10",
                CardNo = "C100",
                EventType = 75,
                AuthResult = "认证成功",
                PictureBytes = new byte[] { 0xFF, 0xD8, 0x11, 0x22, 0xFF, 0xD9 },
                RawPayload = "{}",
                Source = AcsAlarmEventSource.Realtime
            };
            faceEvent.RawPayloadFields["source"] = "Realtime";
            return faceEvent;
        }
    }
}
