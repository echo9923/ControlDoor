namespace ControlDoor.Permissions
{
    public sealed class NullDeviceOperationRetryWriter : IDeviceOperationRetryWriter
    {
        public DeviceOperationRetryWriteResult UpsertIntent(DeviceOperationRetryIntent intent)
        {
            return DeviceOperationRetryWriteResult.Ok(intent, "补偿意图已记录到内存响应。");
        }
    }
}
