namespace ControlDoor.Devices.Tasks
{
    public enum DeviceTaskWaitMode
    {
        WaitForResult = 0,
        QueueAndReturn = 1,
        FireAndForget = 2,
        StreamProgress = 3
    }
}
