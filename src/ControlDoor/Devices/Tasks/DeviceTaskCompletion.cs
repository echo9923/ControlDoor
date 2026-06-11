using System;
using System.Threading.Tasks;

namespace ControlDoor.Devices.Tasks
{
    public sealed class DeviceTaskCompletion
    {
        private readonly TaskCompletionSource<DeviceTaskResult> source = new TaskCompletionSource<DeviceTaskResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DeviceTaskResult> Task => source.Task;

        public bool IsCompleted => source.Task.IsCompleted;

        public bool TrySetResult(DeviceTaskResult result)
        {
            return source.TrySetResult(result);
        }

        public bool TrySetException(Exception exception)
        {
            return source.TrySetException(exception);
        }

        public bool TrySetCancelled()
        {
            return source.TrySetCanceled();
        }
    }
}
