namespace ControlDoor.Devices.Tasks
{
    public enum DeviceTaskExecutionState
    {
        Created = 0,
        Queued = 1,
        Running = 2,
        Succeeded = 3,
        Failed = 4,
        TimedOut = 5,
        Cancelled = 6,
        Rejected = 7
    }
}
