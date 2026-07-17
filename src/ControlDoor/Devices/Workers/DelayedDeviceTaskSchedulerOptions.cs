namespace ControlDoor.Devices.Workers
{
    public sealed class DelayedDeviceTaskSchedulerOptions
    {
        public bool Enabled { get; set; } = true;

        public int MaxDelayedTaskCount { get; set; } = 10000;

        public int DispatchBatchSize { get; set; } = 100;

        public int WakeupMaxSleepMilliseconds { get; set; } = 30000;

        public bool CoalesceByTaskKey { get; set; } = true;

        // 派发遇到瞬时背压（worker 队列满 / dispatcher 停止中）时，重新入队前的延迟毫秒数。
        public int DispatchRetryDelayMilliseconds { get; set; } = 1000;
    }
}
