namespace ControlDoor.Devices.Workers
{
    public enum DelayedTaskScheduleStatus
    {
        Accepted = 0,
        Coalesced = 1,
        Rejected = 2,
        Disabled = 3,
        Stopped = 4
    }
}
