using System;

namespace ControlDoor.Devices.Runtime
{
    public sealed class DeviceRuntimeError
    {
        public string OperationName { get; set; }

        public string Code { get; set; }

        public int? SdkErrorCode { get; set; }

        public string Message { get; set; }

        public DateTime OccurredAt { get; set; }

        public string RequestId { get; set; }

        public bool Retryable { get; set; }

        public static DeviceRuntimeError Create(
            string operationName,
            string code,
            string message,
            DateTime occurredAt,
            string requestId = null,
            int? sdkErrorCode = null,
            bool retryable = false)
        {
            return new DeviceRuntimeError
            {
                OperationName = operationName,
                Code = code,
                Message = message,
                OccurredAt = occurredAt,
                RequestId = requestId,
                SdkErrorCode = sdkErrorCode,
                Retryable = retryable
            };
        }

        public DeviceRuntimeError Clone()
        {
            return new DeviceRuntimeError
            {
                OperationName = OperationName,
                Code = Code,
                SdkErrorCode = SdkErrorCode,
                Message = Message,
                OccurredAt = OccurredAt,
                RequestId = RequestId,
                Retryable = Retryable
            };
        }
    }
}
