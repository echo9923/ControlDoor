using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using ControlDoor.Database;

namespace ControlDoor.FaceEvents
{
    public sealed class FaceEventRepository
    {
        private const string ServiceName = "ControlDoor";
        private readonly IDatabaseClient database;
        private readonly SnapshotStorage snapshotStorage;
        private readonly Func<SqlConnection> connectionFactory;

        public FaceEventRepository(IDatabaseClient database, SnapshotStorage snapshotStorage)
            : this(database, snapshotStorage, (Func<SqlConnection>)null)
        {
        }

        public FaceEventRepository(IDatabaseClient database, SnapshotStorage snapshotStorage, string connectionString)
            : this(database, snapshotStorage, CreateDefaultConnectionFactory(connectionString))
        {
        }

        internal FaceEventRepository(IDatabaseClient database, SnapshotStorage snapshotStorage, Func<SqlConnection> connectionFactory)
        {
            this.database = database ?? throw new ArgumentNullException(nameof(database));
            this.snapshotStorage = snapshotStorage ?? throw new ArgumentNullException(nameof(snapshotStorage));
            this.connectionFactory = connectionFactory;
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

        // 批量插入：一次连接 + 一个事务内复用同一 SqlCommand 循环插入，显著减少 DB 往返。
        // 处理顺序：校验 -> 预查去重 -> 批量查昵称 -> 落盘 -> 事务内逐条插入。
        // 事务内遇到唯一约束冲突(2601/2627)整批回滚，降级为逐条 InsertEvent 兜底（靠它自己的 Exists + 错误捕获）。
        public IReadOnlyList<FaceEventInsertResult> InsertEvents(IReadOnlyList<AcsFaceEvent> events)
        {
            if (events == null || events.Count == 0)
            {
                return Array.Empty<FaceEventInsertResult>();
            }

            // 连接工厂缺失（未配置连接串）时退化为逐条 InsertEvent，保持向后兼容。
            if (connectionFactory == null)
            {
                var fallback = new FaceEventInsertResult[events.Count];
                for (var i = 0; i < events.Count; i++)
                {
                    fallback[i] = InsertEvent(events[i]);
                }
                return fallback;
            }

            var results = new FaceEventInsertResult[events.Count];
            var pending = new List<AcsFaceEvent>(events.Count);
            var pendingIndexes = new List<int>(events.Count);

            // 1. 逐条校验，失败的直接出结果。
            for (var i = 0; i < events.Count; i++)
            {
                var faceEvent = events[i];
                var validation = Validate(faceEvent);
                if (!string.IsNullOrEmpty(validation))
                {
                    results[i] = FaceEventInsertResult.Invalid(faceEvent == null ? 0 : faceEvent.EventId, validation);
                    continue;
                }
                pending.Add(faceEvent);
                pendingIndexes.Add(i);
            }

            if (pending.Count == 0)
            {
                return results;
            }

            // 2. 批量预查去重：一条 SELECT id ... WHERE id IN (...) 过滤掉已存在的。
            HashSet<long> existingIds;
            try
            {
                existingIds = QueryExistingIds(pending);
            }
            catch (Exception ex)
            {
                MarkFailures(results, pending, pendingIndexes, ex);
                return results;
            }

            var toInsert = new List<AcsFaceEvent>(pending.Count);
            var toInsertIndexes = new List<int>(pending.Count);
            for (var j = 0; j < pending.Count; j++)
            {
                if (existingIds.Contains(pending[j].EventId))
                {
                    results[pendingIndexes[j]] = FaceEventInsertResult.Duplicate(pending[j].EventId);
                }
                else
                {
                    toInsert.Add(pending[j]);
                    toInsertIndexes.Add(pendingIndexes[j]);
                }
            }

            if (toInsert.Count == 0)
            {
                return results;
            }

            // 3. 批量预查昵称 + 截断字段 + 落盘（落盘 I/O 仍逐条，但路径入参准备好）。
            Dictionary<string, string> nicknames;
            try
            {
                nicknames = LookupNicknames(toInsert);
            }
            catch (Exception ex)
            {
                MarkFailures(results, toInsert, toInsertIndexes, ex);
                return results;
            }

            var snapshots = new string[toInsert.Count];
            for (var k = 0; k < toInsert.Count; k++)
            {
                try
                {
                    var faceEvent = toInsert[k];
                    if (string.IsNullOrWhiteSpace(faceEvent.Nickname))
                    {
                        nicknames.TryGetValue(faceEvent.EmployeeId, out var nickname);
                        faceEvent.Nickname = nickname;
                    }
                    TruncateFields(faceEvent);
                    var snapshot = snapshotStorage.Save(faceEvent);
                    snapshots[k] = snapshot.Saved ? snapshot.SnapshotPath : null;
                }
                catch (Exception ex)
                {
                    MarkFailures(results, toInsert, toInsertIndexes, ex);
                    return results;
                }
            }

            // 4. 单事务内循环插入。
            try
            {
                InsertInTransaction(toInsert, snapshots);
                for (var k = 0; k < toInsert.Count; k++)
                {
                    results[toInsertIndexes[k]] = FaceEventInsertResult.Inserted(toInsert[k].EventId, snapshots[k]);
                }
                return results;
            }
            catch (SqlException ex) when (IsDuplicate(ex.Number))
            {
                // 事务已回滚。降级为逐条 InsertEvent（靠它自己的 Exists + 2601/2627 兜底）。
                for (var k = 0; k < toInsert.Count; k++)
                {
                    results[toInsertIndexes[k]] = InsertEvent(toInsert[k]);
                }
                return results;
            }
            catch (SqlException ex)
            {
                var canRetry = IsTransient(ex.Number);
                var status = canRetry ? FaceEventInsertStatus.RetryableFailure : FaceEventInsertStatus.Failed;
                for (var k = 0; k < toInsert.Count; k++)
                {
                    results[toInsertIndexes[k]] = FaceEventInsertResult.Failure(
                        status,
                        toInsert[k].EventId,
                        canRetry ? "RETRYABLE_FAILURE" : "DATABASE_FAILURE",
                        ex.Message);
                }
                return results;
            }
            catch (Exception ex)
            {
                MarkFailures(results, toInsert, toInsertIndexes, ex);
                return results;
            }
        }

        private static void MarkFailures(
            FaceEventInsertResult[] results,
            IReadOnlyList<AcsFaceEvent> events,
            IReadOnlyList<int> indexes,
            Exception ex)
        {
            var status = IsRetryableFailure(ex)
                ? FaceEventInsertStatus.RetryableFailure
                : FaceEventInsertStatus.Failed;
            var code = status == FaceEventInsertStatus.RetryableFailure ? "RETRYABLE_FAILURE" : "FAILED";
            for (var i = 0; i < events.Count; i++)
            {
                results[indexes[i]] = FaceEventInsertResult.Failure(
                    status,
                    events[i].EventId,
                    code,
                    ex.Message);
            }
        }

        private static bool IsRetryableFailure(Exception ex)
        {
            return ex is SqlException ||
                ex is TimeoutException ||
                ex is InvalidOperationException;
        }

        private void InsertInTransaction(IReadOnlyList<AcsFaceEvent> toInsert, string[] snapshots)
        {
            using (var connection = connectionFactory())
            using (var command = new SqlCommand(InsertSql, connection))
            {
                // 预建 22 个参数（与 BuildParameters 列集一致），循环只换值，避免每条重建参数集合。
                AddCommandParameters(command);
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    command.Transaction = transaction;
                    try
                    {
                        for (var i = 0; i < toInsert.Count; i++)
                        {
                            SetCommandValues(command, toInsert[i], snapshots[i]);
                            command.ExecuteNonQuery();
                        }
                        transaction.Commit();
                    }
                    catch
                    {
                        // 安全回滚；异常向上抛由调用方决定降级还是报错。
                        try { transaction.Rollback(); } catch { /* 回滚失败不再掩盖原始异常 */ }
                        throw;
                    }
                }
            }
        }

        private HashSet<long> QueryExistingIds(IReadOnlyList<AcsFaceEvent> events)
        {
            var existing = new HashSet<long>();
            if (events.Count == 0)
            {
                return existing;
            }

            var parameterNames = new List<string>(events.Count);
            for (var i = 0; i < events.Count; i++)
            {
                parameterNames.Add("@id" + i);
            }

            var sql = "SELECT id FROM dbo.attendance_gate_v2 WHERE id IN (" + string.Join(",", parameterNames) + ")";
            using (var connection = connectionFactory())
            using (var command = new SqlCommand(sql, connection))
            {
                for (var i = 0; i < events.Count; i++)
                {
                    command.Parameters.AddWithValue(parameterNames[i], events[i].EventId);
                }
                connection.Open();
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        existing.Add(reader.GetInt64(0));
                    }
                }
            }
            return existing;
        }

        private Dictionary<string, string> LookupNicknames(IReadOnlyList<AcsFaceEvent> events)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var employeeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var faceEvent in events)
            {
                if (!string.IsNullOrWhiteSpace(faceEvent.EmployeeId))
                {
                    employeeIds.Add(faceEvent.EmployeeId);
                }
            }

            if (employeeIds.Count == 0)
            {
                return result;
            }

            var parameterNames = new List<string>(employeeIds.Count);
            var index = 0;
            foreach (var employeeId in employeeIds)
            {
                parameterNames.Add("@u" + index);
                index++;
            }

            var sql = "SELECT username, nickname FROM dbo.system_users WHERE deleted = 0 AND username IN (" + string.Join(",", parameterNames) + ")";
            using (var connection = connectionFactory())
            using (var command = new SqlCommand(sql, connection))
            {
                index = 0;
                foreach (var employeeId in employeeIds)
                {
                    command.Parameters.AddWithValue(parameterNames[index], employeeId);
                    index++;
                }
                connection.Open();
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var username = reader.IsDBNull(0) ? null : reader.GetString(0);
                        var nickname = reader.IsDBNull(1) ? null : reader.GetString(1);
                        if (username != null && nickname != null)
                        {
                            result[username] = nickname;
                        }
                    }
                }
            }
            return result;
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

        private static bool IsDuplicate(int sqlErrorNumber)
        {
            return sqlErrorNumber == 2601 || sqlErrorNumber == 2627;
        }

        private static bool IsTransient(int number)
        {
            return number == -2 || number == 4060 || number == 10928 || number == 10929 || number == 40197 || number == 40501 || number == 40613;
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

        // 批量路径专用：预建参数占位，循环只换 Value，避免每条重建参数集合带来的 GC 压力。
        private static void AddCommandParameters(SqlCommand command)
        {
            command.Parameters.Add("@id", System.Data.SqlDbType.BigInt);
            command.Parameters.Add("@username", System.Data.SqlDbType.NVarChar, 30);
            command.Parameters.Add("@nickname", System.Data.SqlDbType.NVarChar, 50);
            command.Parameters.Add("@record_datetime", System.Data.SqlDbType.DateTime2);
            command.Parameters.Add("@record_date", System.Data.SqlDbType.Date);
            command.Parameters.Add("@record_time", System.Data.SqlDbType.Time);
            command.Parameters.Add("@direction", System.Data.SqlDbType.TinyInt);
            command.Parameters.Add("@device_name", System.Data.SqlDbType.NVarChar, 100);
            command.Parameters.Add("@device_sn", System.Data.SqlDbType.NVarChar, 100);
            command.Parameters.Add("@card_no", System.Data.SqlDbType.NVarChar, 64);
            command.Parameters.Add("@snapshot_path", System.Data.SqlDbType.NVarChar, 255);
            command.Parameters.Add("@raw_payload", System.Data.SqlDbType.VarChar, -1);
            command.Parameters.Add("@event_type", System.Data.SqlDbType.TinyInt);
            command.Parameters.Add("@process_status", System.Data.SqlDbType.TinyInt);
            command.Parameters.Add("@process_message", System.Data.SqlDbType.NVarChar, 255);
            command.Parameters.Add("@processed_at", System.Data.SqlDbType.DateTime2);
            command.Parameters.Add("@creator", System.Data.SqlDbType.NVarChar, 64);
            command.Parameters.Add("@create_time", System.Data.SqlDbType.DateTime2);
            command.Parameters.Add("@updater", System.Data.SqlDbType.NVarChar, 64);
            command.Parameters.Add("@update_time", System.Data.SqlDbType.DateTime2);
            command.Parameters.Add("@deleted", System.Data.SqlDbType.VarChar, 1);
            command.Parameters.Add("@tenant_id", System.Data.SqlDbType.BigInt);
        }

        private static void SetCommandValues(SqlCommand command, AcsFaceEvent faceEvent, string snapshotPath)
        {
            var now = DateTime.Now;
            command.Parameters["@id"].Value = faceEvent.EventId;
            command.Parameters["@username"].Value = faceEvent.EmployeeId;
            command.Parameters["@nickname"].Value = (object)faceEvent.Nickname ?? DBNull.Value;
            command.Parameters["@record_datetime"].Value = faceEvent.EventTime;
            command.Parameters["@record_date"].Value = faceEvent.RecordDate;
            command.Parameters["@record_time"].Value = faceEvent.RecordTime;
            command.Parameters["@direction"].Value = (byte)faceEvent.Direction;
            command.Parameters["@device_name"].Value = (object)faceEvent.DeviceName ?? DBNull.Value;
            command.Parameters["@device_sn"].Value = (object)faceEvent.DeviceSn ?? DBNull.Value;
            command.Parameters["@card_no"].Value = (object)faceEvent.CardNo ?? DBNull.Value;
            command.Parameters["@snapshot_path"].Value = (object)snapshotPath ?? DBNull.Value;
            command.Parameters["@raw_payload"].Value = (object)faceEvent.RawPayload ?? DBNull.Value;
            command.Parameters["@event_type"].Value = faceEvent.EventType.HasValue ? (object)faceEvent.EventType.Value : DBNull.Value;
            command.Parameters["@process_status"].Value = (byte)0;
            command.Parameters["@process_message"].Value = (object)faceEvent.AuthResult ?? DBNull.Value;
            command.Parameters["@processed_at"].Value = DBNull.Value;
            command.Parameters["@creator"].Value = ServiceName;
            command.Parameters["@create_time"].Value = now;
            command.Parameters["@updater"].Value = ServiceName;
            command.Parameters["@update_time"].Value = now;
            command.Parameters["@deleted"].Value = "0";
            command.Parameters["@tenant_id"].Value = 1L;
        }

        private static Func<SqlConnection> CreateDefaultConnectionFactory(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return null;
            }
            return () => new SqlConnection(connectionString);
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
