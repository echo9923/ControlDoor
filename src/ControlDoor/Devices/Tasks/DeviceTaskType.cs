namespace ControlDoor.Devices.Tasks
{
    public enum DeviceTaskType
    {
        Login = 0,
        Logout = 1,
        SetupAlarm = 2,
        CloseAlarm = 3,
        HealthCheck = 4,
        ProbeCapabilities = 5,
        SyncPermission = 6,
        SyncPerson = 7,
        UploadFace = 8,
        DeleteFace = 9,
        DeletePerson = 10,
        GetFace = 11,
        CaptureFace = 12,
        RetryDeviceOperation = 13,
        QueryHistoryEvents = 14,
        ProcessRealtimeEvent = 15,
        ControlGateway = 16,
        Custom = 1000
    }
}
