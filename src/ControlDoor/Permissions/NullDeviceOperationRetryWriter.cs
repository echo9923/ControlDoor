namespace ControlDoor.Permissions
{
    public sealed class NullDeviceOperationRetryWriter : IDeviceOperationRetryWriter
    {
        public DeviceOperationRetryWriteResult UpsertIntent(DeviceOperationRetryIntent intent)
        {
            return DeviceOperationRetryWriteResult.Failed(intent, "DB_ERROR", "未配置补偿存储，无法持久化补偿意图。");
        }
    }
}
