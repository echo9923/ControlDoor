using System;

namespace ControlDoor.Hikvision
{
    public sealed class DeviceGatewayException : Exception
    {
        public DeviceGatewayException(string operationName, SdkError error)
            : base(BuildMessage(operationName, error))
        {
            OperationName = operationName;
            Error = error ?? SdkError.FromCode(-1);
        }

        public DeviceGatewayException(string operationName, SdkError error, Exception innerException)
            : base(BuildMessage(operationName, error), innerException)
        {
            OperationName = operationName;
            Error = error ?? SdkError.FromCode(-1);
        }

        public string OperationName { get; }

        public SdkError Error { get; }

        private static string BuildMessage(string operationName, SdkError error)
        {
            var safeOperation = string.IsNullOrWhiteSpace(operationName) ? "DeviceGateway" : operationName;
            var safeError = error ?? SdkError.FromCode(-1);
            return safeOperation + " 失败: " + safeError.Message + " (" + safeError.Code + ")";
        }
    }
}
