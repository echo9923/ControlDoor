namespace ControlDoor.Devices.Workers
{
    public sealed class DeviceTaskTimeoutOptions
    {
        public int DefaultTaskTimeoutMilliseconds { get; set; } = 30000;

        public int LongRunningTaskWarningMilliseconds { get; set; } = 60000;

        public int WorkerStopTimeoutMilliseconds { get; set; } = 30000;
    }
}
