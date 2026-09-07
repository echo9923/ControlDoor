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

        public void Trace(string operationName, int? deviceId, bool success, long elapsedMs, int? sdkErrorCode = null, string message = null, Exception exception = null)
        {
            if (!enabled)
            {
                return;
            }

            var fields = new LogFields
            {
                OperationName = operationName,
                DeviceId = deviceId,
                ElapsedMs = elapsedMs,
                ErrorCode = sdkErrorCode?.ToString(),
                Exception = exception?.ToString(),
                Extra = { ["success"] = success.ToString(), ["errorMessage"] = message }
            };
            if (!success)
            {
                if (operationName == "Login" || operationName == "GetAlarmDeploymentStatus")
                {
                    logger.WarnRepeated("SdkTrace", "SDK 调用失败。", fields);
                }
                else
                {
                    logger.Warn("SdkTrace", "SDK 调用失败。", fields);
                }
                return;
            }
            if (logger.ClearRepeatedWarnings("SdkTrace", deviceId ?? logger.CurrentScopeDeviceId, operationName))
            {
                logger.Info("SdkTrace", "SDK 调用已恢复。", fields);
            }
            if (logger.IsSlowOperation(elapsedMs))
            {
                logger.Warn("SdkTrace", "SDK 调用完成，但耗时较长。", fields);
            }
            else
            {
                logger.Debug("SdkTrace", "SDK 调用完成。", fields);
            }
        }
    }
}
