using System;

namespace ControlDoor.Observability
{
    public sealed class SdkTraceLogger
    {
        private readonly ServiceLogger logger;
        private readonly bool enabled;

        public SdkTraceLogger(ServiceLogger logger, bool enabled)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.enabled = enabled;
        }

        public void Trace(string operationName, int? deviceId, bool success, long elapsedMs, int? sdkErrorCode = null, string message = null)
        {
            if (!enabled)
            {
                return;
            }

            logger.Info(
                "SdkTrace",
                message ?? "SDK 调用完成。",
                new LogFields
                {
                    OperationName = operationName,
                    DeviceId = deviceId,
                    ElapsedMs = elapsedMs,
                    ErrorCode = sdkErrorCode?.ToString()
                });
        }
    }
}
