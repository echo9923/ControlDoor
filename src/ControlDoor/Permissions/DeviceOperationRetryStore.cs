using System;
using System.Collections.Generic;
using System.Linq;
using ControlDoor.Configuration;
using ControlDoor.Database;
using ControlDoor.Observability;

namespace ControlDoor.Permissions
{
    public sealed class DeviceOperationRetryStore : IDeviceOperationRetryWriter
    {
        private const int MaxErrorLength = 2000;
        private readonly IDatabaseClient database;
        private readonly DeviceOperationRetryOptions options;
        private readonly RetryBackoffCalculator backoff;
        private readonly ServiceLogger logger;
        private readonly IUserSyncStatusWriter userSyncWriter;

        public DeviceOperationRetryStore(IDatabaseClient database, DeviceOperationRetryOptions options = null, ServiceLogger logger = null, IUserSyncStatusWriter userSyncWriter = null)
        {
            this.database = database ?? throw new ArgumentNullException(nameof(database));
            this.options = options ?? new DeviceOperationRetryOptions();
            this.logger = logger;
            this.userSyncWriter = userSyncWriter ?? new NullUserSyncStatusWriter();
            backoff = new RetryBackoffCalculator(this.options);
        }

        public DeviceOperationRetryWriteResult UpsertIntent(DeviceOperationRetryIntent intent)
        {
            if (intent == null)
            {
                return DeviceOperationRetryWriteResult.Failed(null, "INVALID_ARGUMENT", "补偿意图不能为空。");
            }

            if (intent.DeviceId <= 0 || string.IsNullOrWhiteSpace(intent.EmployeeId))
            {
                return DeviceOperationRetryWriteResult.Failed(intent, "INVALID_ARGUMENT", "补偿意图缺少设备或员工编号。");
            }

            RetryOperation operation;
            if (!RetryOperationNames.TryParse(intent.Operation, out operation))
            {
                return DeviceOperationRetryWriteResult.Failed(intent, "INVALID_ARGUMENT", "不支持的补偿操作: " + intent.Operation);
            }

            var normalized = NormalizeIntent(intent, operation);
            var record = ExecuteTransactionalUpsert(normalized, operation);
            if (IsUniqueConflict(record))
            {
                record = ExecuteTransactionalUpsert(normalized, operation);
            }

            if (record.Error != null)
            {
                logger?.Error("DeviceOperationRetry", "补偿意图写入失败。", null, new LogFields
                {
                    RequestId = normalized.RequestId,
                    DeviceId = normalized.DeviceId,
                    EmployeeId = normalized.EmployeeId,
                    OperationName = normalized.Operation,
                    ErrorCode = record.Error.SqlErrorNumber.HasValue ? record.Error.SqlErrorNumber.Value.ToString() : record.Error.ExceptionType
                });
                return DeviceOperationRetryWriteResult.Failed(normalized, "DB_ERROR", record.Error.Message);
            }

            logger?.Info("DeviceOperationRetry", "补偿意图已写入。", new LogFields
            {
                RequestId = normalized.RequestId,
                DeviceId = normalized.DeviceId,
                EmployeeId = normalized.EmployeeId,
                OperationName = RetryOperationNames.ToStage5OperationName(operation)
            });
            return DeviceOperationRetryWriteResult.Ok(normalized);
        }

        public IReadOnlyList<DeviceOperationRetryState> LoadDueStates(DateTime now, int? batchSize = null)
        {
            var size = batchSize.HasValue && batchSize.Value > 0 ? batchSize.Value : Math.Max(1, options.BatchSize);
            var rows = database.ExecuteQuery(
                "DeviceOperationRetryStore.LoadDueStates",
                LoadDueSql,
                new DatabaseParameter("@now", now),
                new DatabaseParameter("@batchSize", size));
            return rows.Select(DeviceOperationRetryState.FromRow).ToList();
        }

        public bool TryClaimDueState(DeviceOperationRetryState state, DateTime now)
        {
            if (state == null)
            {
                return false;
            }

            var claimSeconds = Math.Max(60, Math.Max(options.InitialRetryDelaySeconds, options.ScanIntervalSeconds));
            var claimUntil = now.AddSeconds(claimSeconds);
            var record = database.ExecuteNonQuery(
                "DeviceOperationRetryStore.TryClaimDueState",
                ClaimDueSql,
                new DatabaseParameter("@id", state.Id),
                new DatabaseParameter("@now", now),
                new DatabaseParameter("@claimUntil", claimUntil));
            return record.Error == null && (!record.RowsAffected.HasValue || record.RowsAffected.Value > 0);
        }

        public void DeleteEmptyState(DeviceOperationRetryState state)
        {
            if (state == null)
            {
                return;
            }

            database.ExecuteNonQuery(
                "DeviceOperationRetryStore.DeleteEmptyState",
                @"DELETE FROM dbo.device_operation_retry_states
WHERE id = @id
  AND permission_pending = 0
  AND person_pending = 0
  AND face_pending = 0
  AND delete_person_pending = 0
  AND delete_face_pending = 0;",
                new DatabaseParameter("@id", state.Id));
        }

        public void MarkOperationSuccess(DeviceOperationRetryState state, RetryOperation operation)
        {
            if (state == null)
            {
                return;
            }

            var record = database.ExecuteNonQuery(
                "DeviceOperationRetryStore.MarkOperationSuccess",
                SuccessSql(operation),
                SuccessParameters(state));
            if (operation == RetryOperation.Permission &&
                state.PermissionLevel.HasValue &&
                record.Error == null &&
                (!record.RowsAffected.HasValue || record.RowsAffected.Value > 0) &&
                !HasBlockingPermissionStateForEmployee(state))
            {
                userSyncWriter.MarkPermissionSynced(state.EmployeeId, state.PermissionLevel.Value);
            }
        }

        private bool HasBlockingPermissionStateForEmployee(DeviceOperationRetryState state)
        {
            var rows = database.ExecuteQuery(
                "DeviceOperationRetryStore.HasBlockingPermissionStateForEmployee",
                @"SELECT TOP 1 id
FROM dbo.device_operation_retry_states
WHERE employee_id = @employeeId
  AND id <> @id
  AND (
      permission_pending = 1
      OR permission_sync_completion_blocked = 1
  );",
                new DatabaseParameter("@employeeId", state.EmployeeId),
                new DatabaseParameter("@id", state.Id));
            return rows != null && rows.Count > 0;
        }

        public void DeleteIfCompleted(DeviceOperationRetryState state)
        {
            if (state == null)
            {
                return;
            }

            var record = database.ExecuteNonQuery(
                "DeviceOperationRetryStore.DeleteIfCompleted",
                DeleteIfCompletedSql,
                new DatabaseParameter("@id", state.Id));
            LogStateChange("Retry state deleted if completed.", state, "COMPLETED_DELETE", fields =>
            {
                fields.Extra["rowsAffected"] = record.RowsAffected.HasValue ? record.RowsAffected.Value.ToString() : string.Empty;
            });
        }

        public void ApplyExecutionResult(RetryExecutionResult result, DateTime now)
        {
            if (result == null || result.State == null)
            {
                return;
            }

            foreach (var operation in result.SucceededOperations)
            {
                MarkOperationSuccess(result.State, operation);
            }

            if (result.AllSucceeded)
            {
                DeleteIfCompleted(result.State);
                return;
            }

            if (result.Retryable && HasReachedMaxAttempts(result.State.AttemptCount + 1))
            {
                MarkTerminalFailure(result.State, "RETRY_EXHAUSTED", BuildError(result), now);
            }
            else if (IsTerminal(result.Code, result.Retryable, result.State.AttemptCount + 1))
            {
                MarkTerminalFailure(result.State, result.Code, BuildError(result), now);
            }
            else
            {
                ScheduleRetry(result.State, result.Code, BuildError(result), now);
            }
        }

        public void ScheduleRetry(DeviceOperationRetryState state, string code, string message, DateTime now)
        {
            if (state == null)
            {
                return;
            }

            var nextAttempt = Math.Max(0, state.AttemptCount) + 1;
            var nextRetryAt = backoff.CalculateNextRetryAt(now, nextAttempt);
            var record = database.ExecuteNonQuery(
                "DeviceOperationRetryStore.ScheduleRetry",
                @"UPDATE dbo.device_operation_retry_states
SET attempt_count = @attemptCount,
    last_attempt_at = @now,
    next_retry_at = @nextRetryAt,
    last_error = @lastError,
    updated_at = @now
WHERE id = @id
  AND exhausted_at IS NULL;",
                new DatabaseParameter("@id", state.Id),
                new DatabaseParameter("@attemptCount", nextAttempt),
                new DatabaseParameter("@now", now),
                new DatabaseParameter("@nextRetryAt", nextRetryAt),
                new DatabaseParameter("@lastError", Trim(FormatError(code, message), MaxErrorLength)));
            LogStateChange("Retry state scheduled for retry.", state, code, fields =>
            {
                fields.Extra["attemptCount"] = nextAttempt.ToString();
                fields.Extra["nextRetryAt"] = nextRetryAt.ToString("yyyy-MM-dd HH:mm:ss");
                fields.Extra["lastError"] = FormatError(code, message);
                fields.Extra["rowsAffected"] = record.RowsAffected.HasValue ? record.RowsAffected.Value.ToString() : string.Empty;
            });
        }

        public void DeferOffline(DeviceOperationRetryState state, string code, string message, DateTime now)
        {
            if (state == null)
            {
                return;
            }

            var nextRetryAt = now.AddSeconds(Math.Max(1, options.InitialRetryDelaySeconds));
            var record = database.ExecuteNonQuery(
                "DeviceOperationRetryStore.DeferOffline",
                @"UPDATE dbo.device_operation_retry_states
SET next_retry_at = @nextRetryAt,
    last_error = @lastError,
    updated_at = @now
WHERE id = @id
  AND exhausted_at IS NULL;",
                new DatabaseParameter("@id", state.Id),
                new DatabaseParameter("@now", now),
                new DatabaseParameter("@nextRetryAt", nextRetryAt),
                new DatabaseParameter("@lastError", Trim(FormatError(code, message), MaxErrorLength)));
            LogStateChange("Retry state deferred while device is offline.", state, code, fields =>
            {
                fields.Extra["nextRetryAt"] = nextRetryAt.ToString("yyyy-MM-dd HH:mm:ss");
                fields.Extra["lastError"] = FormatError(code, message);
                fields.Extra["rowsAffected"] = record.RowsAffected.HasValue ? record.RowsAffected.Value.ToString() : string.Empty;
            });
        }

        public void MarkTerminalFailure(DeviceOperationRetryState state, string code, string message, DateTime now)
        {
            if (state == null)
            {
                return;
            }

            var record = database.ExecuteNonQuery(
                "DeviceOperationRetryStore.MarkTerminalFailure",
                @"UPDATE dbo.device_operation_retry_states
SET attempt_count = CASE WHEN attempt_count < @attemptCount THEN @attemptCount ELSE attempt_count END,
    last_attempt_at = @now,
    next_retry_at = NULL,
    last_error = @lastError,
    exhausted_at = @now,
    updated_at = @now
WHERE id = @id;",
                new DatabaseParameter("@id", state.Id),
                new DatabaseParameter("@attemptCount", Math.Max(1, state.AttemptCount + 1)),
                new DatabaseParameter("@now", now),
                new DatabaseParameter("@lastError", Trim(FormatError(code, message), MaxErrorLength)));
            LogStateChange("Retry state marked terminal failure.", state, code, fields =>
            {
                fields.Extra["attemptCount"] = Math.Max(1, state.AttemptCount + 1).ToString();
                fields.Extra["exhaustedAt"] = now.ToString("yyyy-MM-dd HH:mm:ss");
                fields.Extra["lastError"] = FormatError(code, message);
                fields.Extra["rowsAffected"] = record.RowsAffected.HasValue ? record.RowsAffected.Value.ToString() : string.Empty;
            });
        }

        public int CleanupExpiredFailures(DateTime now, int? batchSize = null)
        {
            var size = batchSize.HasValue && batchSize.Value > 0 ? batchSize.Value : Math.Max(1, options.BatchSize);
            var retentionDays = options.FailureRetentionDays > 0 ? options.FailureRetentionDays : options.TerminalRetentionDays;
            var cutoff = now.AddDays(-Math.Max(1, retentionDays));
            var record = database.ExecuteNonQuery(
                "DeviceOperationRetryStore.CleanupExpiredFailures",
                @"DELETE FROM dbo.device_operation_retry_states
WHERE id IN (
    SELECT TOP (@batchSize) id
    FROM dbo.device_operation_retry_states
    WHERE exhausted_at IS NOT NULL
      AND exhausted_at < @cutoff
    ORDER BY exhausted_at ASC, id ASC
);",
                new DatabaseParameter("@batchSize", size),
                new DatabaseParameter("@cutoff", cutoff));
            var deleted = record.RowsAffected ?? 0;
            if (deleted > 0)
            {
                var fields = new LogFields
                {
                    OperationName = "CleanupExpiredFailures"
                };
                fields.Extra["deletedCount"] = deleted.ToString();
                fields.Extra["retentionDays"] = Math.Max(1, retentionDays).ToString();
                fields.Extra["cutoff"] = cutoff.ToString("yyyy-MM-dd HH:mm:ss");
                fields.Extra["batchSize"] = size.ToString();
                logger?.Info("DeviceOperationRetry", "Expired terminal retry states cleaned.", fields);
            }

            return deleted;
        }

        private void LogStateChange(string message, DeviceOperationRetryState state, string code, Action<LogFields> configure = null)
        {
            if (logger == null || state == null)
            {
                return;
            }

            var fields = new LogFields
            {
                DeviceId = state.DeviceId,
                EmployeeId = state.EmployeeId,
                OperationName = "DeviceOperationRetryStore",
                ErrorCode = code
            };
            fields.Extra["stateId"] = state.Id.ToString();
            fields.Extra["permissionPending"] = state.PermissionPending.ToString();
            fields.Extra["personPending"] = state.PersonPending.ToString();
            fields.Extra["facePending"] = state.FacePending.ToString();
            fields.Extra["deletePersonPending"] = state.DeletePersonPending.ToString();
            fields.Extra["deleteFacePending"] = state.DeleteFacePending.ToString();
            configure?.Invoke(fields);
            logger.Info("DeviceOperationRetry", message, fields);
        }

        private DatabaseCommandRecord ExecuteTransactionalUpsert(DeviceOperationRetryIntent intent, RetryOperation operation)
        {
            return database.ExecuteNonQuery(
                "DeviceOperationRetryStore.UpsertIntent",
                TransactionalUpsertSql,
                UpsertParameters(intent, operation));
        }

        private static DatabaseParameter[] UpsertParameters(DeviceOperationRetryIntent intent, RetryOperation operation)
        {
            var operationName = RetryOperationNames.ToStage5OperationName(operation);
            return new[]
            {
                new DatabaseParameter("@operation", operationName),
                new DatabaseParameter("@deviceId", intent.DeviceId),
                new DatabaseParameter("@employeeId", intent.EmployeeId),
                new DatabaseParameter("@permissionLevel", (object)intent.PermissionLevel ?? DBNull.Value),
                new DatabaseParameter("@permissionPayload", operation == RetryOperation.Permission ? (object)(intent.PermissionPayloadJson ?? intent.PayloadJson ?? string.Empty) : DBNull.Value),
                new DatabaseParameter("@personPayload", operation == RetryOperation.Person ? (object)(intent.PersonPayloadJson ?? intent.PayloadJson ?? string.Empty) : DBNull.Value),
                new DatabaseParameter("@facePayload", operation == RetryOperation.Face ? (object)(intent.FacePayloadJson ?? intent.PayloadJson ?? string.Empty) : DBNull.Value),
                new DatabaseParameter("@nextRetryAt", (object)intent.NextRetryAt ?? DBNull.Value),
                new DatabaseParameter("@lastError", (object)Trim(BuildLastError(intent), MaxErrorLength) ?? DBNull.Value),
                new DatabaseParameter("@createdAt", intent.CreatedAt == DateTime.MinValue ? DateTime.Now : intent.CreatedAt),
                new DatabaseParameter("@updatedAt", DateTime.Now)
            };
        }

        private static DatabaseParameter[] SuccessParameters(DeviceOperationRetryState state)
        {
            return new[]
            {
                new DatabaseParameter("@id", state.Id),
                new DatabaseParameter("@updatedAt", DateTime.Now),
                new DatabaseParameter("@permissionLevel", (object)state.PermissionLevel ?? DBNull.Value),
                new DatabaseParameter("@permissionPayload", (object)state.PermissionPayloadJson ?? DBNull.Value),
                new DatabaseParameter("@personPayload", (object)state.PersonPayloadJson ?? DBNull.Value),
                new DatabaseParameter("@facePayload", (object)state.FacePayloadJson ?? DBNull.Value),
                new DatabaseParameter("@deletePersonPending", state.DeletePersonPending ? 1 : 0),
                new DatabaseParameter("@deleteFacePending", state.DeleteFacePending ? 1 : 0),
                new DatabaseParameter("@personPending", state.PersonPending ? 1 : 0),
                new DatabaseParameter("@facePending", state.FacePending ? 1 : 0),
                new DatabaseParameter("@permissionPending", state.PermissionPending ? 1 : 0)
            };
        }

        private DeviceOperationRetryIntent NormalizeIntent(DeviceOperationRetryIntent intent, RetryOperation operation)
        {
            var normalized = new DeviceOperationRetryIntent
            {
                DeviceId = intent.DeviceId,
                EmployeeId = (intent.EmployeeId ?? string.Empty).Trim(),
                Operation = RetryOperationNames.ToStage5OperationName(operation),
                PermissionLevel = intent.PermissionLevel,
                PermissionPayloadJson = intent.PermissionPayloadJson,
                PayloadJson = intent.PayloadJson,
                PersonPayloadJson = intent.PersonPayloadJson,
                FacePayloadJson = intent.FacePayloadJson,
                ReasonCode = intent.ReasonCode,
                ReasonMessage = intent.ReasonMessage,
                RequestId = intent.RequestId,
                LastError = intent.LastError,
                CreatedAt = intent.CreatedAt == DateTime.MinValue ? DateTime.Now : intent.CreatedAt,
                NextRetryAt = intent.NextRetryAt
            };

            if (!normalized.NextRetryAt.HasValue)
            {
                normalized.NextRetryAt = DateTime.Now.AddSeconds(Math.Max(1, options.InitialRetryDelaySeconds));
            }

            return normalized;
        }

        private bool IsTerminal(string code, bool retryable, int nextAttempt)
        {
            if (HasReachedMaxAttempts(nextAttempt))
            {
                return true;
            }

            if (retryable)
            {
                return false;
            }

            switch ((code ?? string.Empty).Trim())
            {
                case "DEVICE_NOT_FOUND":
                case "DEVICE_DISABLED":
                case "DEVICE_CONFIG_INVALID":
                case "DEVICE_UNSUPPORTED":
                case "INVALID_PAYLOAD":
                case "SDK_CONFIGURATION_ERROR":
                    return true;
                default:
                    return false;
            }
        }

        private bool HasReachedMaxAttempts(int nextAttempt)
        {
            return nextAttempt >= Math.Max(1, options.MaxRetryAttempts);
        }

        private static bool IsUniqueConflict(DatabaseCommandRecord record)
        {
            return record != null &&
                record.Error != null &&
                (record.Error.SqlErrorNumber == 2601 || record.Error.SqlErrorNumber == 2627);
        }

        private static string BuildLastError(DeviceOperationRetryIntent intent)
        {
            if (!string.IsNullOrWhiteSpace(intent.ReasonCode) && !string.IsNullOrWhiteSpace(intent.ReasonMessage))
            {
                return intent.ReasonCode.Trim() + ": " + intent.ReasonMessage.Trim();
            }

            if (!string.IsNullOrWhiteSpace(intent.ReasonMessage))
            {
                return intent.ReasonMessage.Trim();
            }

            return intent.LastError;
        }

        private static string BuildError(RetryExecutionResult result)
        {
            if (result == null)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            if (result.FailedOperation.HasValue)
            {
                parts.Add("operation=" + RetryOperationNames.ToStage5OperationName(result.FailedOperation.Value));
            }

            if (result.SdkErrorCode.HasValue)
            {
                parts.Add("sdkErrorCode=" + result.SdkErrorCode.Value);
            }

            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                parts.Add(result.Message);
            }

            return string.Join("; ", parts);
        }

        private static string FormatError(string code, string message)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return message;
            }

            return code + ": " + (message ?? string.Empty);
        }

        private static string Trim(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, maxLength);
        }

        private static string SuccessSql(RetryOperation operation)
        {
            switch (operation)
            {
                case RetryOperation.Permission:
                    return @"UPDATE dbo.device_operation_retry_states
SET permission_pending = 0,
    permission_sync_completion_blocked = 0,
    permission_payload = NULL,
    updated_at = @updatedAt
WHERE id = @id
  AND (
      (permission_level = @permissionLevel)
      OR (permission_level IS NULL AND @permissionLevel IS NULL)
  )
  AND (
      (permission_payload = @permissionPayload)
      OR (permission_payload IS NULL AND @permissionPayload IS NULL)
  );";
                case RetryOperation.Person:
                    return @"UPDATE dbo.device_operation_retry_states
SET person_pending = 0,
    person_payload = NULL,
    updated_at = @updatedAt
WHERE id = @id
  AND (
      (person_payload = @personPayload)
      OR (person_payload IS NULL AND @personPayload IS NULL)
  );";
                case RetryOperation.Face:
                    return @"UPDATE dbo.device_operation_retry_states
SET face_pending = 0,
    face_payload = NULL,
    updated_at = @updatedAt
WHERE id = @id
  AND (
      (face_payload = @facePayload)
      OR (face_payload IS NULL AND @facePayload IS NULL)
  );";
                case RetryOperation.DeleteFace:
                    return @"UPDATE dbo.device_operation_retry_states
SET delete_face_pending = 0,
    updated_at = @updatedAt
WHERE id = @id;";
                case RetryOperation.DeletePerson:
                    // 仅在扫描时看到的 pending/payload 快照仍匹配时才清空；并发 upsert 新意图后整行清零会被拒绝。
                    return @"UPDATE dbo.device_operation_retry_states
SET permission_pending = 0,
    permission_sync_completion_blocked = 0,
    permission_payload = NULL,
    person_pending = 0,
    face_pending = 0,
    delete_person_pending = 0,
    delete_face_pending = 0,
    person_payload = NULL,
    face_payload = NULL,
    updated_at = @updatedAt
WHERE id = @id
  AND delete_person_pending = 1
  AND delete_person_pending = @deletePersonPending
  AND delete_face_pending = @deleteFacePending
  AND person_pending = @personPending
  AND face_pending = @facePending
  AND permission_pending = @permissionPending
  AND (
      (person_payload = @personPayload)
      OR (person_payload IS NULL AND @personPayload IS NULL)
  )
  AND (
      (face_payload = @facePayload)
      OR (face_payload IS NULL AND @facePayload IS NULL)
  )
  AND (
      (permission_payload = @permissionPayload)
      OR (permission_payload IS NULL AND @permissionPayload IS NULL)
  );";
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }

        private const string LoadDueSql = @"
SELECT TOP (@batchSize) *
FROM dbo.device_operation_retry_states WITH (UPDLOCK, READPAST, ROWLOCK)
WHERE exhausted_at IS NULL
  AND (next_retry_at IS NULL OR next_retry_at <= @now)
  AND (
      permission_pending = 1
      OR person_pending = 1
      OR face_pending = 1
      OR delete_person_pending = 1
      OR delete_face_pending = 1
  )
ORDER BY next_retry_at ASC, updated_at ASC, id ASC;";

        private const string ClaimDueSql = @"
UPDATE dbo.device_operation_retry_states
SET next_retry_at = @claimUntil,
    last_attempt_at = @now,
    updated_at = @now
WHERE id = @id
  AND exhausted_at IS NULL
  AND (next_retry_at IS NULL OR next_retry_at <= @now)
  AND (
      permission_pending = 1
      OR person_pending = 1
      OR face_pending = 1
      OR delete_person_pending = 1
      OR delete_face_pending = 1
  );";

        private const string TransactionalUpsertSql = @"
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @id BIGINT;
    DECLARE @currentPermissionPending BIT = 0;
    DECLARE @currentPersonPending BIT = 0;
    DECLARE @currentFacePending BIT = 0;
    DECLARE @currentDeletePersonPending BIT = 0;
    DECLARE @currentDeleteFacePending BIT = 0;
    DECLARE @currentAttemptCount INT = 0;
    DECLARE @currentExhaustedAt DATETIME2(0);
    DECLARE @conflict BIT = 0;

    SELECT
        @id = id,
        @currentPermissionPending = permission_pending,
        @currentPersonPending = person_pending,
        @currentFacePending = face_pending,
        @currentDeletePersonPending = delete_person_pending,
        @currentDeleteFacePending = delete_face_pending,
        @currentAttemptCount = attempt_count,
        @currentExhaustedAt = exhausted_at
    FROM dbo.device_operation_retry_states WITH (UPDLOCK, HOLDLOCK)
    WHERE device_id = @deviceId
      AND employee_id = @employeeId;

    IF @id IS NULL
    BEGIN
        INSERT INTO dbo.device_operation_retry_states (
            device_id,
            employee_id,
            permission_level,
            permission_payload,
            permission_pending,
            permission_sync_completion_blocked,
            person_payload,
            person_pending,
            face_payload,
            face_pending,
            delete_person_pending,
            delete_face_pending,
            attempt_count,
            next_retry_at,
            last_error,
            last_attempt_at,
            exhausted_at,
            created_at,
            updated_at)
        VALUES (
            @deviceId,
            @employeeId,
            CASE WHEN @operation = N'SyncPermission' THEN @permissionLevel ELSE NULL END,
            CASE WHEN @operation = N'SyncPermission' THEN @permissionPayload ELSE NULL END,
            CASE WHEN @operation = N'SyncPermission' THEN 1 ELSE 0 END,
            CASE WHEN @operation = N'SyncPermission' THEN 1 ELSE 0 END,
            CASE WHEN @operation = N'SyncPerson' THEN @personPayload ELSE NULL END,
            CASE WHEN @operation = N'SyncPerson' THEN 1 ELSE 0 END,
            CASE WHEN @operation = N'UploadFace' THEN @facePayload ELSE NULL END,
            CASE WHEN @operation = N'UploadFace' THEN 1 ELSE 0 END,
            CASE WHEN @operation = N'DeletePerson' THEN 1 ELSE 0 END,
            CASE WHEN @operation = N'DeleteFace' THEN 1 ELSE 0 END,
            0,
            @nextRetryAt,
            @lastError,
            NULL,
            NULL,
            @createdAt,
            @updatedAt);
    END
    ELSE
    BEGIN
        SET @conflict = CASE
            WHEN @currentExhaustedAt IS NOT NULL THEN 1
            WHEN @operation = N'SyncPermission' AND @currentDeletePersonPending = 1 THEN 1
            WHEN @operation = N'SyncPerson' AND @currentDeletePersonPending = 1 THEN 1
            WHEN @operation = N'UploadFace' AND (@currentDeletePersonPending = 1 OR @currentDeleteFacePending = 1) THEN 1
            WHEN @operation = N'DeleteFace' AND @currentFacePending = 1 THEN 1
            WHEN @operation = N'DeletePerson' AND (
                @currentPermissionPending = 1
                OR @currentPersonPending = 1
                OR @currentFacePending = 1
                OR @currentDeleteFacePending = 1) THEN 1
            ELSE 0
        END;

        UPDATE dbo.device_operation_retry_states
        SET permission_level = CASE
                WHEN @operation = N'SyncPermission' THEN @permissionLevel
                WHEN @operation = N'DeletePerson' THEN NULL
                ELSE permission_level
            END,
            permission_pending = CASE
                WHEN @operation = N'SyncPermission' THEN 1
                WHEN @operation = N'DeletePerson' THEN 0
                ELSE permission_pending
            END,
            permission_payload = CASE
                WHEN @operation = N'SyncPermission' THEN @permissionPayload
                WHEN @operation = N'DeletePerson' THEN NULL
                ELSE permission_payload
            END,
            permission_sync_completion_blocked = CASE
                WHEN @operation = N'SyncPermission' THEN 1
                WHEN @operation = N'DeletePerson' THEN 0
                ELSE permission_sync_completion_blocked
            END,
            person_payload = CASE
                WHEN @operation = N'SyncPerson' THEN @personPayload
                WHEN @operation = N'DeletePerson' THEN NULL
                ELSE person_payload
            END,
            person_pending = CASE
                WHEN @operation = N'SyncPerson' THEN 1
                WHEN @operation = N'DeletePerson' THEN 0
                ELSE person_pending
            END,
            face_payload = CASE
                WHEN @operation = N'UploadFace' THEN @facePayload
                WHEN @operation IN (N'DeleteFace', N'DeletePerson') THEN NULL
                ELSE face_payload
            END,
            face_pending = CASE
                WHEN @operation = N'UploadFace' THEN 1
                WHEN @operation IN (N'DeleteFace', N'DeletePerson') THEN 0
                ELSE face_pending
            END,
            delete_person_pending = CASE
                WHEN @operation = N'DeletePerson' THEN 1
                WHEN @operation IN (N'SyncPermission', N'SyncPerson', N'UploadFace') THEN 0
                ELSE delete_person_pending
            END,
            delete_face_pending = CASE
                WHEN @operation = N'DeleteFace' THEN 1
                WHEN @operation IN (N'UploadFace', N'DeletePerson') THEN 0
                ELSE delete_face_pending
            END,
            attempt_count = CASE
                WHEN @currentExhaustedAt IS NOT NULL OR @conflict = 1 THEN 0
                ELSE attempt_count
            END,
            next_retry_at = @nextRetryAt,
            last_error = @lastError,
            exhausted_at = NULL,
            updated_at = @updatedAt
        WHERE id = @id;
    END

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
    BEGIN
        ROLLBACK TRANSACTION;
    END

    THROW;
END CATCH";

        private const string DeleteIfCompletedSql = @"
DELETE FROM dbo.device_operation_retry_states
WHERE id = @id
  AND permission_pending = 0
  AND person_pending = 0
  AND face_pending = 0
  AND delete_person_pending = 0
  AND delete_face_pending = 0;";
    }
}
