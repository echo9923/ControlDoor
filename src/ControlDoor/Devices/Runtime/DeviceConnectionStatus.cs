namespace ControlDoor.Devices.Runtime
{
    public enum DeviceConnectionStatus
    {
        Unknown = 0,
        Disabled = 1,
        Offline = 2,
        Connecting = 3,
        Online = 4,
        Degraded = 5,
        Disconnecting = 6,
        Faulted = 7,
        Deleted = 8,
        Loaded = 9,
        InvalidConfig = 10,
        ReconnectPending = 11,
        Disconnected = 12,
        Failed = 13
    }
}
