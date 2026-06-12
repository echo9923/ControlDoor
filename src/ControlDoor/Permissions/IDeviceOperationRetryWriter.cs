namespace ControlDoor.Permissions
{
    public interface IDeviceOperationRetryWriter
    {
        DeviceOperationRetryWriteResult UpsertIntent(DeviceOperationRetryIntent intent);
    }
}
