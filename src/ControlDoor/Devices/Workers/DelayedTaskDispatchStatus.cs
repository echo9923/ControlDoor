namespace ControlDoor.Devices.Workers
{
    public enum DelayedTaskDispatchStatus
    {
        Dispatched = 0,
        Rejected = 1,
        QueueFull = 2,
        Cancelled = 3,
        Expired = 4,
        FactoryError = 5,
        SchedulerStopped = 6
    }
}
