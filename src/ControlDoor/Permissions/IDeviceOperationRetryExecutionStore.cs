using ControlDoor.Devices.Tasks;

namespace ControlDoor.Permissions
{
    public interface IDeviceOperationRetryExecutionStore : IDeviceOperationRetryWriter
    {
        DeviceOperationRetryState LoadIntent(DeviceOperationRetryIntent intent);

        bool IsCurrent(DeviceOperationRetryState state);

        void CompleteOnlineOperation(DeviceOperationRetryState state, RetryOperation operation, DeviceTaskResult result);
    }
}
