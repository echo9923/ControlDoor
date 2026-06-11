using System;

namespace ControlDoor.Devices.Workers
{
    public sealed class RetryBackoffPolicy
    {
        public int InitialDelayMilliseconds { get; set; } = 1000;

        public int MaxDelayMilliseconds { get; set; } = 60000;

        public double Multiplier { get; set; } = 2.0;

        public bool UseExponentialBackoff { get; set; }

        public bool EnableJitter { get; set; }

        public int JitterMilliseconds { get; set; }

        public TimeSpan CalculateDelay(int attempt)
        {
            var normalizedAttempt = attempt <= 0 ? 1 : attempt;
            var initial = InitialDelayMilliseconds < 0 ? 0 : InitialDelayMilliseconds;
            var max = MaxDelayMilliseconds <= 0 ? initial : MaxDelayMilliseconds;
            double delay = initial;

            if (UseExponentialBackoff)
            {
                var multiplier = Multiplier <= 1.0 ? 2.0 : Multiplier;
                delay = initial * Math.Pow(multiplier, normalizedAttempt - 1);
            }

            if (EnableJitter && JitterMilliseconds > 0)
            {
                delay += Math.Min(JitterMilliseconds, max);
            }

            delay = Math.Min(delay, max);
            return TimeSpan.FromMilliseconds(delay);
        }

        public static RetryBackoffPolicy Fixed(TimeSpan delay)
        {
            var milliseconds = ToMilliseconds(delay);
            return new RetryBackoffPolicy
            {
                InitialDelayMilliseconds = milliseconds,
                MaxDelayMilliseconds = milliseconds,
                UseExponentialBackoff = false
            };
        }

        public static RetryBackoffPolicy Exponential(TimeSpan initialDelay, TimeSpan maxDelay)
        {
            return new RetryBackoffPolicy
            {
                InitialDelayMilliseconds = ToMilliseconds(initialDelay),
                MaxDelayMilliseconds = ToMilliseconds(maxDelay),
                UseExponentialBackoff = true,
                Multiplier = 2.0
            };
        }

        private static int ToMilliseconds(TimeSpan delay)
        {
            if (delay <= TimeSpan.Zero)
            {
                return 0;
            }

            return delay.TotalMilliseconds >= int.MaxValue ? int.MaxValue : (int)delay.TotalMilliseconds;
        }
    }
}
