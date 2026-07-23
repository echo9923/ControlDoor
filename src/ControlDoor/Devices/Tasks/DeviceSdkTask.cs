using System;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Devices.Runtime;

namespace ControlDoor.Devices.Tasks
{
    public sealed class DeviceSdkTask
    {
        private readonly object gate = new object();
        private DateTime? enqueuedAt;
        private DateTime? startedAt;
        private DateTime? completedAt;
        private DateTime? deadlineAt;
        private int timeoutMilliseconds;
        private CancellationToken callerCancellationToken;
        private DeviceTaskExecutionState executionState;
        private long? sequence;
        private DeviceTaskResult terminalResult;
        private bool queueReservation;
        private bool callerCancellationTokenAttached;
        private bool completionCountClaimed;

        public DeviceSdkTask(int deviceId, DeviceTaskType taskType, string operationName, Func<DeviceTaskContext, Task<DeviceTaskResult>> executeAsync)
        {
            if (deviceId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(deviceId), "DeviceId must be greater than zero.");
            }

            DeviceId = deviceId;
            TaskType = taskType;
            OperationName = string.IsNullOrWhiteSpace(operationName) ? taskType.ToString() : operationName.Trim();
            ExecuteAsync = executeAsync;
            TaskId = Guid.NewGuid().ToString("N");
            CreatedAt = DateTime.Now;
            RequestId = string.Empty;
            CorrelationId = string.Empty;
            Priority = DeviceTaskPriority.Normal;
            WaitMode = DeviceTaskWaitMode.WaitForResult;
            timeoutMilliseconds = 0;
            callerCancellationToken = CancellationToken.None;
            Payload = DeviceTaskPayload.Empty();
            RetrySource = new DeviceTaskRetrySource();
            Completion = new DeviceTaskCompletion();
            executionState = DeviceTaskExecutionState.Created;
        }

        public string TaskId { get; }

        public string RequestId { get; set; }

        public string CorrelationId { get; set; }

        public int DeviceId { get; }

        public DeviceTaskType TaskType { get; }

        public string OperationName { get; }

        public DeviceTaskPriority Priority { get; set; }

        public DateTime CreatedAt { get; }

        public DateTime? EnqueuedAt
        {
            get
            {
                lock (gate)
                {
                    return enqueuedAt;
                }
            }
        }

        public DateTime? StartedAt
        {
            get
            {
                lock (gate)
                {
                    return startedAt;
                }
            }
        }

        public DateTime? CompletedAt
        {
            get
            {
                lock (gate)
                {
                    return completedAt;
                }
            }
        }

        public DateTime? DeadlineAt
        {
            get
            {
                lock (gate)
                {
                    return deadlineAt;
                }
            }
        }

        public int TimeoutMilliseconds
        {
            get
            {
                lock (gate)
                {
                    return timeoutMilliseconds;
                }
            }
            set
            {
                lock (gate)
                {
                    timeoutMilliseconds = value;
                }
            }
        }

        public CancellationToken CallerCancellationToken
        {
            get
            {
                lock (gate)
                {
                    return callerCancellationToken;
                }
            }
        }

        public DeviceTaskWaitMode WaitMode { get; set; }

        public bool RequiresOnline { get; set; }

        public bool AllowWhenDeleting { get; set; }

        public bool AllowWhenManualDisconnected { get; set; }

        public string IdempotencyKey { get; set; } = string.Empty;

        public DeviceTaskRetrySource RetrySource { get; set; }

        public DeviceTaskPayload Payload { get; set; }

        public Func<DeviceTaskContext, Task<DeviceTaskResult>> ExecuteAsync { get; }

        public DeviceTaskCompletion Completion { get; }

        public DeviceTaskExecutionState ExecutionState
        {
            get
            {
                lock (gate)
                {
                    return executionState;
                }
            }
        }

        public long? Sequence
        {
            get
            {
                lock (gate)
                {
                    return sequence;
                }
            }
        }

        // Compatibility aliases for callers that used boolean lifecycle fields.
        public bool TaskStarted
        {
            get
            {
                lock (gate)
                {
                    return startedAt.HasValue;
                }
            }
        }

        public bool TaskCompleted
        {
            get
            {
                lock (gate)
                {
                    return IsTerminalState(executionState);
                }
            }
        }

        public bool IsCompleted => TaskCompleted;

        public bool IsTerminal => TaskCompleted;

        public bool CanBeQueued
        {
            get
            {
                lock (gate)
                {
                    return executionState == DeviceTaskExecutionState.Created &&
                        terminalResult == null &&
                        !queueReservation &&
                        !Completion.IsCompleted;
                }
            }
        }

        public DeviceTaskResult TerminalResult
        {
            get
            {
                lock (gate)
                {
                    return terminalResult;
                }
            }
        }

        public bool TryReserveForQueue()
        {
            lock (gate)
            {
                if (executionState != DeviceTaskExecutionState.Created ||
                    terminalResult != null ||
                    queueReservation ||
                    Completion.IsCompleted)
                {
                    return false;
                }

                queueReservation = true;
                return true;
            }
        }

        public void ReleaseQueueReservation()
        {
            lock (gate)
            {
                if (executionState == DeviceTaskExecutionState.Created && terminalResult == null)
                {
                    queueReservation = false;
                }
            }
        }

        public void MarkQueued(DateTime enqueuedAt, long sequence, int effectiveTimeoutMilliseconds)
        {
            TryMarkQueued(enqueuedAt, sequence, effectiveTimeoutMilliseconds);
        }

        public bool TryMarkQueued(DateTime enqueuedAt, long sequence, int effectiveTimeoutMilliseconds)
        {
            lock (gate)
            {
                return TryMarkQueuedLocked(enqueuedAt, sequence, effectiveTimeoutMilliseconds);
            }
        }

        public void AttachCallerCancellationToken(CancellationToken cancellationToken)
        {
            lock (gate)
            {
                if (callerCancellationTokenAttached ||
                    executionState != DeviceTaskExecutionState.Created ||
                    queueReservation ||
                    terminalResult != null ||
                    Completion.IsCompleted)
                {
                    return;
                }

                callerCancellationToken = cancellationToken;
                callerCancellationTokenAttached = true;
            }
        }

        public void MarkRunning(DateTime startedAt)
        {
            TryMarkRunning(startedAt);
        }

        public bool TryMarkRunning(DateTime value)
        {
            lock (gate)
            {
                return TryMarkRunningLocked(value);
            }
        }

        public void MarkCompleted(DeviceTaskResult result)
        {
            TryComplete(result);
        }

        public bool TryMarkCompleted(DeviceTaskResult result)
        {
            return TryComplete(result);
        }

        public void MarkRejected(DeviceTaskResult result)
        {
            TryMarkRejected(result);
        }

        public bool TryMarkRejected(DeviceTaskResult result)
        {
            if (result == null)
            {
                return false;
            }

            lock (gate)
            {
                if (terminalResult != null ||
                    Completion.IsCompleted ||
                    executionState != DeviceTaskExecutionState.Created ||
                    (queueReservation && !IsQueueOwnedRejection(result)))
                {
                    return false;
                }

                ApplyTerminalStateLocked(DeviceTaskExecutionState.Rejected, result);
                EnsureCompletionLocked(terminalResult);
                return true;
            }
        }

        public bool TryRejectBeforeSubmission(DeviceTaskResult result)
        {
            if (result == null)
            {
                return false;
            }

            lock (gate)
            {
                if (executionState != DeviceTaskExecutionState.Created ||
                    queueReservation ||
                    terminalResult != null ||
                    Completion.IsCompleted)
                {
                    return false;
                }

                ApplyTerminalStateLocked(DeviceTaskExecutionState.Rejected, result);
                EnsureCompletionLocked(terminalResult);
                return true;
            }
        }

        public void MarkCancelled(DateTime cancelledAt)
        {
            TryMarkCancelled(cancelledAt);
        }

        public bool TryMarkCancelled(DateTime cancelledAt)
        {
            var result = DeviceTaskResult.Cancelled(this, "Task cancelled.")
                .WithCompletionTiming(cancelledAt, cancelledAt);
            return TryCancelBeforeSubmission(result);
        }

        public bool TryCancelBeforeSubmission(DeviceTaskResult result)
        {
            if (result == null)
            {
                return false;
            }

            lock (gate)
            {
                if (executionState != DeviceTaskExecutionState.Created ||
                    queueReservation ||
                    terminalResult != null ||
                    Completion.IsCompleted)
                {
                    return false;
                }

                result.IsWaitOutcome = false;
                ApplyTerminalStateLocked(DeviceTaskExecutionState.Cancelled, result);
                EnsureCompletionLocked(terminalResult);
                return true;
            }
        }

        // Completes the lifecycle and the completion signal as one worker-owned operation.
        public bool TryComplete(DeviceTaskResult result)
        {
            return TryFinalize(result, out _);
        }

        internal bool TryFinalizeFromWorker(DeviceTaskResult result, out DeviceTaskResult finalResult)
        {
            return TryFinalizeCore(result, out finalResult, allowQueued: true);
        }

        public bool TryFinalize(DeviceTaskResult result, out DeviceTaskResult finalResult)
        {
            return TryFinalizeCore(result, out finalResult, allowQueued: false);
        }

        private bool TryFinalizeCore(DeviceTaskResult result, out DeviceTaskResult finalResult, bool allowQueued)
        {
            if (result == null)
            {
                var now = DateTime.Now;
                result = DeviceTaskResult.FromTask(this, false, "INTERNAL_ERROR", "Task returned null result.", DeviceConnectionStatus.Unknown, now, now);
            }

            lock (gate)
            {
                if (terminalResult != null)
                {
                    finalResult = terminalResult;
                    EnsureCompletionLocked(finalResult);
                    return false;
                }

                if (executionState == DeviceTaskExecutionState.Created && queueReservation)
                {
                    finalResult = null;
                    return false;
                }

                if (executionState == DeviceTaskExecutionState.Queued && !allowQueued)
                {
                    finalResult = null;
                    return false;
                }

                DeviceTaskResult provisionalCompletionResult = null;
                if (Completion.Task.Status == TaskStatus.RanToCompletion)
                {
                    var completedResult = Completion.Task.GetAwaiter().GetResult();
                    if (IsLifecycleCompletionResult(completedResult))
                    {
                        completedResult.TaskStarted = completedResult.TaskStarted || startedAt.HasValue;
                        completedResult.TaskCompleted = true;
                        completedResult.IsWaitOutcome = false;
                        terminalResult = completedResult;
                        executionState = GetExecutionTerminalState(completedResult);
                        completedAt = completedResult.CompletedAt == default(DateTime)
                            ? DateTime.Now
                            : completedResult.CompletedAt;
                        queueReservation = false;
                        finalResult = terminalResult;
                        return false;
                    }

                    provisionalCompletionResult = completedResult;
                }

                if (IsTerminalState(executionState))
                {
                    finalResult = terminalResult;
                    return false;
                }

                DeviceTaskExecutionState terminalState;
                if (executionState == DeviceTaskExecutionState.Running)
                {
                    terminalState = GetExecutionTerminalState(result);
                }
                else if (executionState == DeviceTaskExecutionState.Created ||
                    (executionState == DeviceTaskExecutionState.Queued && allowQueued))
                {
                    if (result.Success)
                    {
                        finalResult = null;
                        return false;
                    }

                    terminalState = GetSubmissionTerminalState(result);
                }
                else
                {
                    finalResult = null;
                    return false;
                }

                result.IsWaitOutcome = false;
                if (provisionalCompletionResult != null)
                {
                    provisionalCompletionResult.CopyFrom(result);
                    result = provisionalCompletionResult;
                }

                ApplyTerminalStateLocked(terminalState, result);
                EnsureCompletionLocked(terminalResult);
                finalResult = terminalResult;
                return true;
            }
        }

        // Compatibility helper for callers that mark a terminal state separately.
        public bool TrySetCompletion(DeviceTaskResult result)
        {
            if (result == null)
            {
                return false;
            }

            lock (gate)
            {
                if (!IsTerminalState(executionState))
                {
                    return false;
                }

                if (terminalResult == null)
                {
                    result.TaskStarted = startedAt.HasValue;
                    result.TaskCompleted = true;
                    result.IsWaitOutcome = false;
                    terminalResult = result;
                }

                EnsureCompletionLocked(terminalResult);
                return Completion.IsCompleted;
            }
        }

        public bool TryClaimCompletionCount()
        {
            lock (gate)
            {
                if (!IsTerminalState(executionState) || terminalResult == null || completionCountClaimed)
                {
                    return false;
                }

                completionCountClaimed = true;
                return true;
            }
        }

        public int GetEffectiveTimeoutMilliseconds(int defaultTimeoutMilliseconds)
        {
            lock (gate)
            {
                return timeoutMilliseconds > 0 ? timeoutMilliseconds : defaultTimeoutMilliseconds;
            }
        }

        private bool TryMarkQueuedLocked(DateTime value, long taskSequence, int effectiveTimeoutMilliseconds)
        {
            if (executionState != DeviceTaskExecutionState.Created ||
                terminalResult != null ||
                (!queueReservation && Completion.IsCompleted))
            {
                return false;
            }

            enqueuedAt = value;
            sequence = taskSequence;
            deadlineAt = effectiveTimeoutMilliseconds > 0 ? value.AddMilliseconds(effectiveTimeoutMilliseconds) : (DateTime?)null;
            executionState = DeviceTaskExecutionState.Queued;
            queueReservation = false;
            return true;
        }

        private bool TryMarkRunningLocked(DateTime value)
        {
            if (terminalResult != null ||
                (Completion.IsCompleted && !IsProvisionalCompletionLocked()) ||
                IsTerminalState(executionState) ||
                (executionState != DeviceTaskExecutionState.Created && executionState != DeviceTaskExecutionState.Queued))
            {
                return false;
            }

            startedAt = value;
            executionState = DeviceTaskExecutionState.Running;
            return true;
        }

        private void ApplyTerminalStateLocked(DeviceTaskExecutionState terminalState, DeviceTaskResult result)
        {
            completedAt = result == null ? DateTime.Now : result.CompletedAt;
            executionState = terminalState;
            queueReservation = false;
            terminalResult = result;
            if (result != null)
            {
                result.TaskStarted = startedAt.HasValue;
                result.TaskCompleted = true;
            }
        }

        private void EnsureCompletionLocked(DeviceTaskResult result)
        {
            if (result == null)
            {
                return;
            }

            result.TaskStarted = result.TaskStarted || startedAt.HasValue;
            result.TaskCompleted = true;
            result.IsWaitOutcome = false;
            Completion.TrySetResult(result);
        }

        private static bool IsQueueOwnedRejection(DeviceTaskResult result)
        {
            return result != null &&
                (result.Code == "COALESCED" || result.Code == "LOW_PRIORITY_DROPPED");
        }

        private static DeviceTaskExecutionState GetExecutionTerminalState(DeviceTaskResult result)
        {
            if (result == null)
            {
                return DeviceTaskExecutionState.Failed;
            }

            if (result.Code == "TIMEOUT")
            {
                return DeviceTaskExecutionState.TimedOut;
            }

            if (result.Code == "CANCELLED")
            {
                return DeviceTaskExecutionState.Cancelled;
            }

            return result.Success ? DeviceTaskExecutionState.Succeeded : DeviceTaskExecutionState.Failed;
        }

        private bool IsProvisionalCompletionLocked()
        {
            if (Completion.Task.Status != TaskStatus.RanToCompletion)
            {
                return false;
            }

            return !IsLifecycleCompletionResult(Completion.Task.GetAwaiter().GetResult());
        }

        private static bool IsLifecycleCompletionResult(DeviceTaskResult result)
        {
            return result != null && !string.Equals(result.Code, "QUEUED", StringComparison.OrdinalIgnoreCase);
        }

        private static DeviceTaskExecutionState GetSubmissionTerminalState(DeviceTaskResult result)
        {
            if (result != null && result.Code == "CANCELLED")
            {
                return DeviceTaskExecutionState.Cancelled;
            }

            if (result != null && result.Code == "TIMEOUT")
            {
                return DeviceTaskExecutionState.TimedOut;
            }

            return DeviceTaskExecutionState.Rejected;
        }

        private static bool IsTerminalState(DeviceTaskExecutionState state)
        {
            return state == DeviceTaskExecutionState.Succeeded ||
                state == DeviceTaskExecutionState.Failed ||
                state == DeviceTaskExecutionState.TimedOut ||
                state == DeviceTaskExecutionState.Cancelled ||
                state == DeviceTaskExecutionState.Rejected;
        }
    }
}
