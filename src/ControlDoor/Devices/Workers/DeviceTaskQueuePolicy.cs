namespace ControlDoor.Devices.Workers
{
    public sealed class DeviceTaskQueuePolicy
    {
        public int MaxHighPriorityBurst { get; set; } = 20;

        public int RetryAgingMilliseconds { get; set; } = 30000;

        public int LowPriorityAgingMilliseconds { get; set; } = 60000;

        public bool CriticalBypassFairness { get; set; } = true;

        public bool CoalesceBackgroundTasks { get; set; } = true;

        public bool DropLowPriorityWhenQueueFull { get; set; } = true;
    }
}
