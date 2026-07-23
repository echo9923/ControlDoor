using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ControlDoor.Devices.Tasks;

namespace ControlDoor.Permissions
{
    public sealed class RetryExecutionResult
    {
        public RetryExecutionResult(
            DeviceOperationRetryState state,
            IEnumerable<RetryOperation> succeededOperations,
            RetryOperation? failedOperation,
            bool retryable,
            string code,
            string message,
            int? sdkErrorCode)
        {
            State = state;
            SucceededOperations = (succeededOperations ?? Enumerable.Empty<RetryOperation>()).ToList().AsReadOnly();
            FailedOperation = failedOperation;
            Retryable = retryable;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
            SdkErrorCode = sdkErrorCode;
        }

        public DeviceOperationRetryState State { get; }

        public IReadOnlyList<RetryOperation> SucceededOperations { get; }

        public RetryOperation? FailedOperation { get; }

        public bool Retryable { get; }

        public string Code { get; }

        public string Message { get; }

        public int? SdkErrorCode { get; }

        public long IntentVersion => State == null ? 0 : State.IntentVersion;

        public string ClaimToken => State == null ? null : State.ClaimToken;

        private int completionApplied;

        public bool IsStale { get; internal set; }

        public bool TryBeginCompletionApplication()
        {
            return Interlocked.Exchange(ref completionApplied, 1) == 0;
        }

        public bool CompletionApplicationStarted => Volatile.Read(ref completionApplied) != 0;

        public DeviceTaskResult FinalDeviceTaskResult { get; internal set; }

        public bool TaskStarted { get; internal set; }

        public bool IsWaitOutcome => FinalDeviceTaskResult != null && FinalDeviceTaskResult.IsWaitOutcome;

        public bool WasCancelledBeforeStart =>
            !IsWaitOutcome &&
            !TaskStarted &&
            FinalDeviceTaskResult != null &&
            (FinalDeviceTaskResult.Code == "CANCELLED" ||
             FinalDeviceTaskResult.Code == "TIMEOUT" ||
             FinalDeviceTaskResult.Code == "DISPATCHER_STOPPING" ||
             FinalDeviceTaskResult.Code == "DISPATCHER_STOPPED");

        public bool AllSucceeded => !FailedOperation.HasValue;
    }
}
