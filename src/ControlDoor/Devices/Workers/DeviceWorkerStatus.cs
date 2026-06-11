namespace ControlDoor.Devices.Workers
{
    public enum DeviceWorkerStatus
    {
        Created = 0,
        Starting = 1,
        Running = 2,
        Stopping = 3,
        Stopped = 4,
        Faulted = 5
    }
}
