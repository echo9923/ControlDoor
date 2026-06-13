using System.Collections.Generic;
using System.Linq;

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

        public bool AllSucceeded => !FailedOperation.HasValue;
    }
}
