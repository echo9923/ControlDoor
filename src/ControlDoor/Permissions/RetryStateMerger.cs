using System;

namespace ControlDoor.Permissions
{
    public sealed class RetryStateMerger
    {
        public RetryStateMergeResult Merge(DeviceOperationRetryState existing, DeviceOperationRetryIntent intent, DateTime now, bool retryImmediatelyOnNewIntent)
        {
            if (intent == null)
            {
                throw new ArgumentNullException(nameof(intent));
            }

            RetryOperation operation;
            if (!RetryOperationNames.TryParse(intent.Operation, out operation))
            {
                throw new ArgumentException("不支持的补偿操作: " + intent.Operation, nameof(intent));
            }

            var state = existing == null
                ? NewState(intent, now)
                : existing.Clone();
            var hadTerminal = state.ExhaustedAt.HasValue;
            var conflict = existing != null && HasConflict(state, operation);
            var sameKind = existing != null && !conflict && HasSamePending(state, operation);

            ApplyIntent(state, operation, intent);

            if (existing == null || hadTerminal || conflict)
            {
                state.AttemptCount = 0;
            }

            state.ExhaustedAt = null;
            state.LastError = BuildLastError(intent);
            state.NextRetryAt = ResolveNextRetryAt(intent, now, retryImmediatelyOnNewIntent, hadTerminal);
            state.UpdatedAt = now;
            if (state.CreatedAt == DateTime.MinValue)
            {
                state.CreatedAt = intent.CreatedAt == DateTime.MinValue ? now : intent.CreatedAt;
            }

            return new RetryStateMergeResult
            {
                State = state,
                Operation = operation,
                Insert = existing == null,
                ReactivatedTerminal = hadTerminal,
                ConflictReset = conflict,
                SameKindUpdate = sameKind
            };
        }

        private static DeviceOperationRetryState NewState(DeviceOperationRetryIntent intent, DateTime now)
        {
            return new DeviceOperationRetryState
            {
                DeviceId = intent.DeviceId,
                EmployeeId = (intent.EmployeeId ?? string.Empty).Trim(),
                CreatedAt = intent.CreatedAt == DateTime.MinValue ? now : intent.CreatedAt,
                UpdatedAt = now
            };
        }

        private static void ApplyIntent(DeviceOperationRetryState state, RetryOperation operation, DeviceOperationRetryIntent intent)
        {
            switch (operation)
            {
                case RetryOperation.Permission:
                    state.PermissionLevel = intent.PermissionLevel;
                    state.PermissionPending = true;
                    state.PermissionSyncCompletionBlocked = true;
                    state.DeletePersonPending = false;
                    break;
                case RetryOperation.Person:
                    state.PersonPayloadJson = ResolvePersonPayload(intent);
                    state.PersonPending = true;
                    state.DeletePersonPending = false;
                    break;
                case RetryOperation.Face:
                    state.FacePayloadJson = ResolveFacePayload(intent);
                    state.FacePending = true;
                    state.DeleteFacePending = false;
                    state.DeletePersonPending = false;
                    break;
                case RetryOperation.DeleteFace:
                    state.DeleteFacePending = true;
                    state.FacePending = false;
                    state.FacePayloadJson = null;
                    break;
                case RetryOperation.DeletePerson:
                    state.DeletePersonPending = true;
                    state.PermissionPending = false;
                    state.PermissionSyncCompletionBlocked = false;
                    state.PersonPending = false;
                    state.FacePending = false;
                    state.DeleteFacePending = false;
                    state.PersonPayloadJson = null;
                    state.FacePayloadJson = null;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }

        private static bool HasSamePending(DeviceOperationRetryState state, RetryOperation operation)
        {
            switch (operation)
            {
                case RetryOperation.Permission:
                    return state.PermissionPending;
                case RetryOperation.Person:
                    return state.PersonPending;
                case RetryOperation.Face:
                    return state.FacePending;
                case RetryOperation.DeleteFace:
                    return state.DeleteFacePending;
                case RetryOperation.DeletePerson:
                    return state.DeletePersonPending;
                default:
                    return false;
            }
        }

        private static bool HasConflict(DeviceOperationRetryState state, RetryOperation operation)
        {
            if (state.ExhaustedAt.HasValue)
            {
                return true;
            }

            switch (operation)
            {
                case RetryOperation.Permission:
                    return state.DeletePersonPending;
                case RetryOperation.Person:
                    return state.DeletePersonPending;
                case RetryOperation.Face:
                    return state.DeletePersonPending || state.DeleteFacePending;
                case RetryOperation.DeleteFace:
                    return state.FacePending;
                case RetryOperation.DeletePerson:
                    return state.PermissionPending ||
                        state.PersonPending ||
                        state.FacePending ||
                        state.DeleteFacePending;
                default:
                    return false;
            }
        }

        private static string ResolvePersonPayload(DeviceOperationRetryIntent intent)
        {
            return intent.PersonPayloadJson ?? intent.PayloadJson;
        }

        private static string ResolveFacePayload(DeviceOperationRetryIntent intent)
        {
            return intent.FacePayloadJson ?? intent.PayloadJson;
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

            if (!string.IsNullOrWhiteSpace(intent.LastError))
            {
                return intent.LastError.Trim();
            }

            return null;
        }

        private static DateTime? ResolveNextRetryAt(DeviceOperationRetryIntent intent, DateTime now, bool retryImmediatelyOnNewIntent, bool terminalReactivated)
        {
            if (intent.NextRetryAt.HasValue)
            {
                return intent.NextRetryAt.Value;
            }

            if (terminalReactivated && retryImmediatelyOnNewIntent)
            {
                return now;
            }

            return now;
        }
    }
}
