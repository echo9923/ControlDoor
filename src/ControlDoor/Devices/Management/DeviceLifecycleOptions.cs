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

        // 布防失败后无限重试的指数退避参数。
        public int ReArmBaseDelayMs { get; set; } = 1000;

        public int ReArmMaxDelayMs { get; set; } = 60000;

        // 0 或负数表示不限重连次数（默认无限重试）；显式设为正数则作为最大重连次数刹车。
        public int MaxReconnectAttempts { get; set; } = 0;

        public int FailureThreshold { get; set; } = 3;

        public bool AlarmEnabled { get; set; } = true;

        public int AlarmDeployType { get; set; }

        public bool AlarmStatusProbeEnabled { get; set; } = true;

        public int AlarmStatusProbeFailureThreshold { get; set; } = 2;
    }
}
