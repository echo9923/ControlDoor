namespace ControlDoor.Devices.Workers
{
    public sealed class DelayedDeviceTaskSchedulerOptions
    {
        public bool Enabled { get; set; } = true;

        public int MaxDelayedTaskCount { get; set; } = 10000;

        public int DispatchBatchSize { get; set; } = 100;

        public int WakeupMaxSleepMilliseconds { get; set; } = 30000;

        public bool CoalesceByTaskKey { get; set; } = true;
    }
}
