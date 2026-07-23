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

        public TimeSpan ClaimLeaseDuration
        {
            get
            {
                var scanSeconds = Math.Max(1, options.ScanIntervalSeconds);
                var initialSeconds = Math.Max(1, options.InitialRetryDelaySeconds);
                var leaseSeconds = Math.Max(60L, Math.Max(scanSeconds, initialSeconds));
                return TimeSpan.FromSeconds(leaseSeconds);
            }
        }

        public TimeSpan ClaimRenewalInterval
        {
            get
            {
                var intervalSeconds = Math.Max(1L, Math.Min(
                    Math.Max(1L, (long)(ClaimLeaseDuration.TotalSeconds / 3)),
                    Math.Max(1, options.ScanIntervalSeconds)));
                return TimeSpan.FromSeconds(intervalSeconds);
            }
        }

        public bool TryClaimDueState(DeviceOperationRetryState state, DateTime now)
        {
            return TryClaimDueState(state, now, ClaimLeaseDuration);
        }

        public bool TryClaimDueState(DeviceOperationRetryState state, DateTime now, TimeSpan leaseDuration)
        {
            if (state == null)
            {
                return false;
            }

            var claimUntil = now.Add(leaseDuration <= TimeSpan.Zero ? ClaimLeaseDuration : leaseDuration);
            var claimToken = Guid.NewGuid().ToString("N");
            var record = database.ExecuteNonQuery(
                "DeviceOperationRetryStore.TryClaimDueState",
                ClaimDueSql,
                new DatabaseParameter("@id", state.Id),
                new DatabaseParameter("@intentVersion", state.IntentVersion),
                new DatabaseParameter("@now", now),
                new DatabaseParameter("@claimUntil", claimUntil),
                new DatabaseParameter("@newClaimToken", claimToken));
            var claimed = record.Error == null && (!record.RowsAffected.HasValue || record.RowsAffected.Value > 0);
            if (claimed)
            {
                state.ClaimToken = claimToken;
                state.ClaimUntil = claimUntil;
            }
            return claimed;
        }

        public bool RenewClaim(DeviceOperationRetryState state, DateTime now)
        {
            return RenewClaim(state, now, ClaimLeaseDuration);
        }

        public bool RenewClaim(DeviceOperationRetryState state, DateTime now, TimeSpan leaseDuration)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.ClaimToken))
            {
                return false;
            }

            var claimUntil = now.Add(leaseDuration <= TimeSpan.Zero ? ClaimLeaseDuration : leaseDuration);
            var record = database.ExecuteNonQuery(
                "DeviceOperationRetryStore.RenewClaim",
                RenewClaimSql,
                new DatabaseParameter("@id", state.Id),
                new DatabaseParameter("@intentVersion", state.IntentVersion),
                new DatabaseParameter("@claimToken", state.ClaimToken),
                new DatabaseParameter("@now", now),
                new DatabaseParameter("@newClaimUntil", claimUntil));
            var renewed = WasApplied(record);
            if (renewed)
            {
                state.ClaimUntil = claimUntil;
            }
            return renewed;
        }

        public bool ReleaseClaimWithoutAttempt(DeviceOperationRetryState state, DateTime now, string code, string message)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.ClaimToken))
            {
                return false;
            }

            var nextRetryAt = now.AddSeconds(Math.Max(1, options.InitialRetryDelaySeconds));
            var record = database.ExecuteNonQuery(
                "DeviceOperationRetryStore.ReleaseClaimWithoutAttempt",
                ReleaseClaimWithoutAttemptSql,
                new DatabaseParameter("@id", state.Id),
                new DatabaseParameter("@intentVersion", state.IntentVersion),
                new DatabaseParameter("@claimToken", state.ClaimToken),
                new DatabaseParameter("@now", now),
                new DatabaseParameter("@nextRetryAt", nextRetryAt),
                new DatabaseParameter("@lastError", Trim(FormatError(code, message), MaxErrorLength)));
            var released = WasApplied(record);
            if (released)
            {
                state.ClaimToken = null;
                state.ClaimUntil = null;
            }
            return released;
        }

        public void DeleteEmptyState(DeviceOperationRetryState state)
        {
            TryDeleteEmptyState(state);
        }

        public bool TryDeleteEmptyState(DeviceOperationRetryState state)
        {
            return TryDeleteEmptyState(state, DateTime.Now);
        }

        public bool TryDeleteEmptyState(DeviceOperationRetryState state, DateTime now)
        {
            if (state == null)
            {
                return false;
            }

            var record = database.ExecuteNonQuery(
                "DeviceOperationRetryStore.DeleteEmptyState",
                @"DELETE FROM dbo.device_operation_retry_states
WHERE id = @id
  AND intent_version = @intentVersion
  AND claim_token = @claimToken
  AND claim_until > @now
  AND exhausted_at IS NULL
  AND permission_sync_completion_blocked = 0
  AND permission_pending = 0
  AND person_pending = 0
  AND face_pending = 0
  AND delete_person_pending = 0
  AND delete_face_pending = 0;",
                new DatabaseParameter("@id", state.Id),
                new DatabaseParameter("@intentVersion", state.IntentVersion),
                new DatabaseParameter("@claimToken", (object)state.ClaimToken ?? DBNull.Value),
                new DatabaseParameter("@now", now));
            return WasApplied(record);
        }

        public bool MarkOperationSuccess(DeviceOperationRetryState state, RetryOperation operation)
        {
            return MarkOperationSuccess(state, operation, DateTime.Now);
        }

        public bool MarkOperationSuccess(DeviceOperationRetryState state, RetryOperation operation, DateTime now)
        {
            if (state == null)
            {
                return false;
            }

            var record = database.ExecuteNonQuery(
                "DeviceOperationRetryStore.MarkOperationSuccess",
                SuccessSql(operation),
                SuccessParameters(state, now));
            var applied = WasApplied(record);
            if (operation == RetryOperation.Permission &&
                state.PermissionLevel.HasValue &&
                applied &&
                !HasBlockingPermissionStateForEmployee(state))
            {
                userSyncWriter.MarkPermissionSynced(state.EmployeeId, state.PermissionLevel.Value);
            }

            return applied;
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

        public bool DeleteIfCompleted(DeviceOperationRetryState state)
        {
            return DeleteIfCompleted(state, DateTime.Now);
        }

        public bool DeleteIfCompleted(DeviceOperationRetryState state, DateTime now)
        {
            if (state == null)
            {
                return false;
            }

            var record = database.ExecuteNonQuery(
                "DeviceOperationRetryStore.DeleteIfCompleted",
                DeleteIfCompletedSql,
                new DatabaseParameter("@id", state.Id),
                new DatabaseParameter("@intentVersion", state.IntentVersion),
                new DatabaseParameter("@claimToken", (object)state.ClaimToken ?? DBNull.Value),
                new DatabaseParameter("@now", now));
            var applied = WasApplied(record);
            LogStateChange("Retry state deleted if completed.", state, "COMPLETED_DELETE", fields =>
            {
                fields.Extra["rowsAffected"] = record.RowsAffected.HasValue ? record.RowsAffected.Value.ToString() : string.Empty;
            });
            return applied;
        }

        public int CleanupEmptyStates(DateTime now, int? batchSize = null)
        {
            var size = batchSize.HasValue && batchSize.Value > 0 ? batchSize.Value : Math.Max(1, options.BatchSize);
            var record = database.ExecuteNonQuery(
                "DeviceOperationRetryStore.CleanupEmptyStates",
                CleanupEmptyStatesSql,
                new DatabaseParameter("@batchSize", size),
                new DatabaseParameter("@now", now));
            return record.RowsAffected ?? 0;
        }

        public void ApplyExecutionResult(RetryExecutionResult result, DateTime now)
        {
            if (result == null || result.State == null || result.IsWaitOutcome)
            {
                return;
            }

            if (!result.TryBeginCompletionApplication())
            {
                return;
            }

            foreach (var operation in result.SucceededOperations)
            {
                if (!MarkOperationSuccess(result.State, operation, now))
                {
                    result.IsStale = true;
                    return;
                }
            }

            if (result.AllSucceeded)
            {
                if (!DeleteIfCompleted(result.State, now))
                {
                    result.IsStale = true;
                }
                return;
            }

            bool applied;
            if (result.Retryable && HasReachedMaxAttempts(result.State.AttemptCount + 1))
            {
                applied = MarkTerminalFailure(result.State, "RETRY_EXHAUSTED", BuildError(result), now);
            }
            else if (IsTerminal(result.Code, result.Retryable, result.State.AttemptCount + 1))
            {
                applied = MarkTerminalFailure(result.State, result.Code, BuildError(result), now);
            }
            else
            {
                applied = ScheduleRetry(result.State, result.Code, BuildError(result), now);
            }

            if (!applied)
            {
                result.IsStale = true;
            }
        }

        public bool ScheduleRetry(DeviceOperationRetryState state, string code, string message, DateTime now)
        {
            if (state == null)
            {
                return false;
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
    updated_at = @now,
    claim_token = NULL,
    claim_until = NULL
WHERE id = @id
  AND intent_version = @intentVersion
  AND claim_token = @claimToken
  AND claim_until > @now
  AND exhausted_at IS NULL;",
                new DatabaseParameter("@id", state.Id),
                new DatabaseParameter("@intentVersion", state.IntentVersion),
                new DatabaseParameter("@claimToken", (object)state.ClaimToken ?? DBNull.Value),
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
            return WasApplied(record);
        }

        public bool DeferOffline(DeviceOperationRetryState state, string code, string message, DateTime now)
        {
            if (state == null)
            {
                return false;
            }

            var nextRetryAt = now.AddSeconds(Math.Max(1, options.InitialRetryDelaySeconds));
            var record = database.ExecuteNonQuery(
                "DeviceOperationRetryStore.DeferOffline",
                @"UPDATE dbo.device_operation_retry_states
SET next_retry_at = @nextRetryAt,
    last_error = @lastError,
    updated_at = @now,
    claim_token = NULL,
    claim_until = NULL
WHERE id = @id
  AND intent_version = @intentVersion
  AND claim_token = @claimToken
  AND claim_until > @now
  AND exhausted_at IS NULL;",
                new DatabaseParameter("@id", state.Id),
                new DatabaseParameter("@intentVersion", state.IntentVersion),
                new DatabaseParameter("@claimToken", (object)state.ClaimToken ?? DBNull.Value),
                new DatabaseParameter("@now", now),
                new DatabaseParameter("@nextRetryAt", nextRetryAt),
                new DatabaseParameter("@lastError", Trim(FormatError(code, message), MaxErrorLength)));
            LogStateChange("Retry state deferred while device is offline.", state, code, fields =>
            {
                fields.Extra["nextRetryAt"] = nextRetryAt.ToString("yyyy-MM-dd HH:mm:ss");
                fields.Extra["lastError"] = FormatError(code, message);
                fields.Extra["rowsAffected"] = record.RowsAffected.HasValue ? record.RowsAffected.Value.ToString() : string.Empty;
            });
            return WasApplied(record);
        }

        public bool MarkTerminalFailure(DeviceOperationRetryState state, string code, string message, DateTime now)
        {
            if (state == null)
            {
                return false;
            }

            var record = database.ExecuteNonQuery(
                "DeviceOperationRetryStore.MarkTerminalFailure",
                @"UPDATE dbo.device_operation_retry_states
SET attempt_count = CASE WHEN attempt_count < @attemptCount THEN @attemptCount ELSE attempt_count END,
    last_attempt_at = @now,
    next_retry_at = NULL,
    last_error = @lastError,
    exhausted_at = @now,
    updated_at = @now,
    claim_token = NULL,
    claim_until = NULL
WHERE id = @id
  AND intent_version = @intentVersion
  AND claim_token = @claimToken
  AND claim_until > @now
  AND exhausted_at IS NULL;",
                new DatabaseParameter("@id", state.Id),
                new DatabaseParameter("@intentVersion", state.IntentVersion),
                new DatabaseParameter("@claimToken", (object)state.ClaimToken ?? DBNull.Value),
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
            return WasApplied(record);
        }

        public int CleanupExpiredFailures(DateTime now, int? batchSize = null)
        {
            var size = batchSize.HasValue && batchSize.Value > 0 ? batchSize.Value : Math.Max(1, options.BatchSize);
            var retentionDays = options.FailureRetentionDays > 0 ? options.FailureRetentionDays : options.TerminalRetentionDays;
            var cutoff = now.AddDays(-Math.Max(1, retentionDays));
            var record = database.ExecuteNonQuery(
                "DeviceOperationRetryStore.CleanupExpiredFailures",
                @";WITH candidates AS
(
    SELECT TOP (@batchSize) id, intent_version
    FROM dbo.device_operation_retry_states WITH (UPDLOCK, READPAST, ROWLOCK)
    WHERE exhausted_at IS NOT NULL
      AND exhausted_at < @cutoff
      AND (claim_until IS NULL OR claim_until <= @now)
    ORDER BY exhausted_at ASC, id ASC
)
DELETE state
FROM dbo.device_operation_retry_states AS state
INNER JOIN candidates
    ON candidates.id = state.id
   AND candidates.intent_version = state.intent_version
WHERE state.exhausted_at IS NOT NULL
  AND state.exhausted_at < @cutoff
  AND (state.claim_until IS NULL OR state.claim_until <= @now);",
                new DatabaseParameter("@batchSize", size),
                new DatabaseParameter("@cutoff", cutoff),
                new DatabaseParameter("@now", now));
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

        private static DatabaseParameter[] SuccessParameters(DeviceOperationRetryState state, DateTime now)
        {
            return new[]
            {
                new DatabaseParameter("@id", state.Id),
                new DatabaseParameter("@updatedAt", now),
                new DatabaseParameter("@intentVersion", state.IntentVersion),
                new DatabaseParameter("@claimToken", (object)state.ClaimToken ?? DBNull.Value),
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

        private static bool WasApplied(DatabaseCommandRecord record)
        {
            return record != null &&
                record.Error == null &&
                (!record.RowsAffected.HasValue || record.RowsAffected.Value > 0);
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
  AND intent_version = @intentVersion
  AND claim_token = @claimToken
  AND claim_until > @updatedAt
  AND permission_pending = 1
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
  AND intent_version = @intentVersion
  AND claim_token = @claimToken
  AND claim_until > @updatedAt
  AND person_pending = 1
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
  AND intent_version = @intentVersion
  AND claim_token = @claimToken
  AND claim_until > @updatedAt
  AND face_pending = 1
  AND (
      (face_payload = @facePayload)
      OR (face_payload IS NULL AND @facePayload IS NULL)
  );";
                case RetryOperation.DeleteFace:
                    return @"UPDATE dbo.device_operation_retry_states
SET delete_face_pending = 0,
    updated_at = @updatedAt
WHERE id = @id
  AND intent_version = @intentVersion
  AND claim_token = @claimToken
  AND claim_until > @updatedAt
  AND delete_face_pending = 1;";
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
  AND intent_version = @intentVersion
  AND claim_token = @claimToken
  AND claim_until > @updatedAt
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

        private const string RenewClaimSql = @"
UPDATE dbo.device_operation_retry_states
SET claim_until = @newClaimUntil,
    next_retry_at = @newClaimUntil,
    updated_at = @now
WHERE id = @id
  AND intent_version = @intentVersion
  AND claim_token = @claimToken
  AND claim_until > @now
  AND exhausted_at IS NULL
  AND (
      permission_pending = 1
      OR person_pending = 1
      OR face_pending = 1
      OR delete_person_pending = 1
      OR delete_face_pending = 1
  );";

        private const string ReleaseClaimWithoutAttemptSql = @"
UPDATE dbo.device_operation_retry_states
SET next_retry_at = @nextRetryAt,
    last_error = @lastError,
    updated_at = @now,
    claim_token = NULL,
    claim_until = NULL
WHERE id = @id
  AND intent_version = @intentVersion
  AND claim_token = @claimToken
  AND claim_until > @now
  AND exhausted_at IS NULL
  AND (
      permission_pending = 1
      OR person_pending = 1
      OR face_pending = 1
      OR delete_person_pending = 1
      OR delete_face_pending = 1
  );";

        private const string CleanupEmptyStatesSql = @"
;WITH candidates AS
(
    SELECT TOP (@batchSize) id, intent_version
    FROM dbo.device_operation_retry_states WITH (UPDLOCK, READPAST, ROWLOCK)
    WHERE exhausted_at IS NULL
      AND (claim_until IS NULL OR claim_until <= @now)
      AND permission_sync_completion_blocked = 0
      AND permission_pending = 0
      AND person_pending = 0
      AND face_pending = 0
      AND delete_person_pending = 0
      AND delete_face_pending = 0
    ORDER BY updated_at ASC, id ASC
)
DELETE state
FROM dbo.device_operation_retry_states AS state
INNER JOIN candidates
    ON candidates.id = state.id
   AND candidates.intent_version = state.intent_version
WHERE state.exhausted_at IS NULL
  AND (state.claim_until IS NULL OR state.claim_until <= @now)
  AND state.permission_sync_completion_blocked = 0
  AND state.permission_pending = 0
  AND state.person_pending = 0
  AND state.face_pending = 0
  AND state.delete_person_pending = 0
  AND state.delete_face_pending = 0;";

        private const string LoadDueSql = @"
SELECT TOP (@batchSize) *
FROM dbo.device_operation_retry_states WITH (UPDLOCK, READPAST, ROWLOCK)
WHERE exhausted_at IS NULL
  AND (claim_until IS NULL OR claim_until <= @now)
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
SET claim_token = @newClaimToken,
    claim_until = @claimUntil,
    next_retry_at = @claimUntil,
    last_attempt_at = @now,
    updated_at = @now
WHERE id = @id
  AND intent_version = @intentVersion
  AND exhausted_at IS NULL
  AND (claim_until IS NULL OR claim_until <= @now)
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
    DECLARE @currentIntentVersion BIGINT = 1;
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
        @currentIntentVersion = intent_version,
        @currentExhaustedAt = exhausted_at
    FROM dbo.device_operation_retry_states WITH (UPDLOCK, HOLDLOCK)
    WHERE device_id = @deviceId
      AND employee_id = @employeeId;

    IF @id IS NULL
    BEGIN
        INSERT INTO dbo.device_operation_retry_states (
            intent_version,
            claim_token,
            claim_until,
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
            1,
            NULL,
            NULL,
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
        SET intent_version = @currentIntentVersion + 1,
            claim_token = NULL,
            claim_until = NULL,
            permission_level = CASE
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
  AND intent_version = @intentVersion
  AND claim_token = @claimToken
  AND claim_until > @now
  AND exhausted_at IS NULL
  AND permission_sync_completion_blocked = 0
  AND permission_pending = 0
  AND person_pending = 0
  AND face_pending = 0
  AND delete_person_pending = 0
  AND delete_face_pending = 0;";
    }
}
