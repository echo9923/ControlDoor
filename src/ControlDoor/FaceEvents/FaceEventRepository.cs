using System;
using System.Collections.Generic;
using ControlDoor.Database;

namespace ControlDoor.FaceEvents
{
    public sealed class FaceEventRepository
    {
        private const string ServiceName = "ControlDoor";
        private readonly IDatabaseClient database;
        private readonly SnapshotStorage snapshotStorage;

        public FaceEventRepository(IDatabaseClient database, SnapshotStorage snapshotStorage)
        {
            this.database = database ?? throw new ArgumentNullException(nameof(database));
            this.snapshotStorage = snapshotStorage ?? throw new ArgumentNullException(nameof(snapshotStorage));
        }

        public FaceEventInsertResult InsertEvent(AcsFaceEvent faceEvent)
        {
            var validation = Validate(faceEvent);
            if (!string.IsNullOrEmpty(validation))
            {
                return FaceEventInsertResult.Invalid(faceEvent == null ? 0 : faceEvent.EventId, validation);
            }

            if (Exists(faceEvent.EventId))
            {
                return FaceEventInsertResult.Duplicate(faceEvent.EventId);
            }

            faceEvent.Nickname = string.IsNullOrWhiteSpace(faceEvent.Nickname)
                ? LookupNickname(faceEvent.EmployeeId)
                : faceEvent.Nickname;

            TruncateFields(faceEvent);
            var snapshot = snapshotStorage.Save(faceEvent);
            var record = database.ExecuteNonQuery(
                "InsertFaceEvent",
                InsertSql,
                BuildParameters(faceEvent, snapshot.Saved ? snapshot.SnapshotPath : null));

            if (record.Error == null)
            {
                return FaceEventInsertResult.Inserted(faceEvent.EventId, snapshot.Saved ? snapshot.SnapshotPath : null);
            }

            if (IsDuplicate(record.Error))
            {
                return FaceEventInsertResult.Duplicate(faceEvent.EventId);
            }

            var status = record.Error.CanRetry ? FaceEventInsertStatus.RetryableFailure : FaceEventInsertStatus.Failed;
            return FaceEventInsertResult.Failure(
                status,
                faceEvent.EventId,
                status == FaceEventInsertStatus.RetryableFailure ? "RETRYABLE_FAILURE" : "DATABASE_FAILURE",
                record.Error.Message);
        }

        private bool Exists(long eventId)
        {
            var rows = database.ExecuteQuery(
                "CheckFaceEventDuplicate",
                "SELECT TOP 1 id FROM dbo.attendance_gate_v2 WHERE id = @id",
                new DatabaseParameter("id", eventId));
            return rows != null && rows.Count > 0;
        }

        private string LookupNickname(string employeeId)
        {
            if (string.IsNullOrWhiteSpace(employeeId))
            {
                return null;
            }

            try
            {
                var rows = database.ExecuteQuery(
                    "LookupFaceEventNickname",
                    "SELECT TOP 1 nickname FROM dbo.system_users WHERE username = @username AND deleted = 0",
                    new DatabaseParameter("username", employeeId));
                if (rows == null || rows.Count == 0)
                {
                    return null;
                }

                object value;
                return rows[0].TryGetValue("nickname", out value) ? Convert.ToString(value) : null;
            }
            catch
            {
                return null;
            }
        }

        private static string Validate(AcsFaceEvent faceEvent)
        {
            if (faceEvent == null)
            {
                return "face event is required";
            }

            if (faceEvent.EventId <= 0)
            {
                return "event id must be greater than 0";
            }

            if (string.IsNullOrWhiteSpace(faceEvent.EmployeeId))
            {
                return "employee id is required";
            }

            if (faceEvent.EventTime == default)
            {
                return "event time is required";
            }

            return null;
        }

        private static void TruncateFields(AcsFaceEvent faceEvent)
        {
            faceEvent.EmployeeId = Truncate(faceEvent.EmployeeId, 30);
            faceEvent.Nickname = Truncate(faceEvent.Nickname, 50);
            faceEvent.DeviceName = Truncate(faceEvent.DeviceName, 100);
            faceEvent.DeviceSn = Truncate(faceEvent.DeviceSn, 100);
            faceEvent.CardNo = Truncate(faceEvent.CardNo, 64);
            faceEvent.RawPayload = Truncate(faceEvent.RawPayload, 8000);
            faceEvent.AuthResult = Truncate(faceEvent.AuthResult, 255);
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, maxLength);
        }

        private static bool IsDuplicate(DatabaseError error)
        {
            return error != null && (error.SqlErrorNumber == 2601 || error.SqlErrorNumber == 2627);
        }

        private static DatabaseParameter[] BuildParameters(AcsFaceEvent faceEvent, string snapshotPath)
        {
            var now = DateTime.Now;
            return new[]
            {
                new DatabaseParameter("id", faceEvent.EventId),
                new DatabaseParameter("username", faceEvent.EmployeeId),
                new DatabaseParameter("nickname", (object)faceEvent.Nickname ?? DBNull.Value),
                new DatabaseParameter("record_datetime", faceEvent.EventTime),
                new DatabaseParameter("record_date", faceEvent.RecordDate),
                new DatabaseParameter("record_time", faceEvent.RecordTime),
                new DatabaseParameter("direction", faceEvent.Direction),
                new DatabaseParameter("device_name", (object)faceEvent.DeviceName ?? DBNull.Value),
                new DatabaseParameter("device_sn", (object)faceEvent.DeviceSn ?? DBNull.Value),
                new DatabaseParameter("card_no", (object)faceEvent.CardNo ?? DBNull.Value),
                new DatabaseParameter("snapshot_path", (object)snapshotPath ?? DBNull.Value),
                new DatabaseParameter("raw_payload", (object)faceEvent.RawPayload ?? DBNull.Value),
                new DatabaseParameter("event_type", faceEvent.EventType.HasValue ? (object)faceEvent.EventType.Value : DBNull.Value),
                new DatabaseParameter("process_status", 0),
                new DatabaseParameter("process_message", (object)faceEvent.AuthResult ?? DBNull.Value),
                new DatabaseParameter("processed_at", DBNull.Value),
                new DatabaseParameter("creator", ServiceName),
                new DatabaseParameter("create_time", now),
                new DatabaseParameter("updater", ServiceName),
                new DatabaseParameter("update_time", now),
                new DatabaseParameter("deleted", "0"),
                new DatabaseParameter("tenant_id", 1L)
            };
        }

        private const string InsertSql = @"
INSERT INTO dbo.attendance_gate_v2
(
    id,
    username,
    nickname,
    record_datetime,
    record_date,
    record_time,
    direction,
    device_name,
    device_sn,
    card_no,
    snapshot_path,
    raw_payload,
    event_type,
    process_status,
    process_message,
    processed_at,
    creator,
    create_time,
    updater,
    update_time,
    deleted,
    tenant_id
)
VALUES
(
    @id,
    @username,
    @nickname,
    @record_datetime,
    @record_date,
    @record_time,
    @direction,
    @device_name,
    @device_sn,
    @card_no,
    @snapshot_path,
    @raw_payload,
    @event_type,
    @process_status,
    @process_message,
    @processed_at,
    @creator,
    @create_time,
    @updater,
    @update_time,
    @deleted,
    @tenant_id
)";
    }
}
