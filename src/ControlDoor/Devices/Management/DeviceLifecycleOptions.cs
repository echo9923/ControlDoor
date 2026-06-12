namespace ControlDoor.Devices.Management
{
    public sealed class DeviceLifecycleOptions
    {
        public int LoginTimeoutMs { get; set; } = 15000;

        public int LogoutTimeoutMs { get; set; } = 10000;

        public int HealthCheckTimeoutMs { get; set; } = 10000;

        public int HealthCheckIntervalMs { get; set; } = 30000;

        public int ReconnectBaseDelayMs { get; set; } = 1000;

        public int ReconnectMaxDelayMs { get; set; } = 60000;

        public int MaxReconnectAttempts { get; set; } = 10;

        public int FailureThreshold { get; set; } = 3;

        public bool AlarmEnabled { get; set; } = true;
    }
}
