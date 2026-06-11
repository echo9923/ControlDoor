namespace ControlDoor.Devices.Workers
{
    public sealed class DeviceSdkDispatcherOptions
    {
        public int WorkerCount { get; set; } = 4;

        public int QueueCapacityPerWorker { get; set; } = 1000;

        public int DefaultTaskTimeoutMilliseconds { get; set; } = 30000;
    }
}
