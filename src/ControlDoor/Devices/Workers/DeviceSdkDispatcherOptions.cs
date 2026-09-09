namespace ControlDoor.Devices.Workers
{
    public sealed class DeviceSdkDispatcherOptions
    {
        public int WorkerCount { get; set; } = 4;

        public int QueueCapacityPerWorker { get; set; } = 1000;

        public int DefaultTaskTimeoutMilliseconds { get; set; } = 30000;

        // 观测（复核 R4）：任务排队等待超过阈值输出告警；周期输出每工作通道的积压统计。
        public int SlowQueueWaitWarningMs { get; set; } = 5000;

        public int StatsLogIntervalSeconds { get; set; } = 60;
    }
}
