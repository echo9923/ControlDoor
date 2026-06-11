namespace ControlDoor.Devices.Tasks
{
    public sealed class DeviceTaskRetrySource
    {
        public bool IsRetry { get; set; }

        public string RetryCategory { get; set; } = string.Empty;

        public int RetryAttempt { get; set; }

        public string RetryStateKey { get; set; } = string.Empty;

        public string OriginalRequestId { get; set; } = string.Empty;

        public DeviceTaskRetrySource Clone()
        {
            return new DeviceTaskRetrySource
            {
                IsRetry = IsRetry,
                RetryCategory = RetryCategory,
                RetryAttempt = RetryAttempt,
                RetryStateKey = RetryStateKey,
                OriginalRequestId = OriginalRequestId
            };
        }
    }
}
