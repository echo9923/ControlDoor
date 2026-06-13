using System;
using ControlDoor.Configuration;

namespace ControlDoor.Permissions
{
    public sealed class RetryBackoffCalculator
    {
        private readonly DeviceOperationRetryOptions options;

        public RetryBackoffCalculator(DeviceOperationRetryOptions options)
        {
            this.options = options ?? new DeviceOperationRetryOptions();
        }

        public TimeSpan CalculateDelay(int nextAttemptCount)
        {
            var attempt = nextAttemptCount <= 0 ? 1 : nextAttemptCount;
            var initialSeconds = options.InitialRetryDelaySeconds < 1 ? 60 : options.InitialRetryDelaySeconds;
            var maxSeconds = options.MaxRetryDelaySeconds < initialSeconds ? 3600 : options.MaxRetryDelaySeconds;
            var factor = Math.Pow(2, attempt - 1);
            var delay = initialSeconds * factor;
            if (delay > maxSeconds)
            {
                delay = maxSeconds;
            }

            return TimeSpan.FromSeconds(delay);
        }

        public DateTime CalculateNextRetryAt(DateTime now, int nextAttemptCount)
        {
            return now.Add(CalculateDelay(nextAttemptCount));
        }
    }
}
