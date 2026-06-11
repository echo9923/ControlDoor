using System;

namespace ControlDoor.Runtime.Health
{
    public sealed class HealthCheckResult
    {
        private HealthCheckResult(string name, HealthCheckStatus status, string message, long elapsedMs = 0, string error = null)
        {
            Name = name;
            Status = status;
            Message = message ?? string.Empty;
            ElapsedMs = elapsedMs;
            Error = error ?? string.Empty;
        }

        public string Name { get; }

        public HealthCheckStatus Status { get; }

        public string Message { get; }

        public long ElapsedMs { get; private set; }

        public string Error { get; }

        public bool BlocksStartup => Status == HealthCheckStatus.Failed;

        public HealthCheckResult WithElapsed(long elapsedMs)
        {
            return new HealthCheckResult(Name, Status, Message, elapsedMs, Error);
        }

        public static HealthCheckResult Ok(string name, string message)
        {
            return new HealthCheckResult(name, HealthCheckStatus.OK, message);
        }

        public static HealthCheckResult Warning(string name, string message)
        {
            return new HealthCheckResult(name, HealthCheckStatus.Warning, message);
        }

        public static HealthCheckResult Failed(string name, string message)
        {
            return new HealthCheckResult(name, HealthCheckStatus.Failed, message, 0, message);
        }
    }
}
