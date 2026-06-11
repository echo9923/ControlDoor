using System;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Tasks;

namespace ControlDoor.Devices.Workers
{
    public static class DeviceTaskExceptionMapper
    {
        public static DeviceTaskResult Map(DeviceSdkTask task, Exception exception, DateTime startedAt, DateTime completedAt)
        {
            if (exception is ArgumentException)
            {
                var invalid = DeviceTaskResult.FromTask(task, false, "INVALID_ARGUMENT", exception.Message, DeviceConnectionStatus.Unknown, startedAt, completedAt);
                invalid.ExceptionType = exception.GetType().Name;
                return invalid;
            }

            if (exception is OperationCanceledException)
            {
                var cancelled = DeviceTaskResult.FromTask(task, false, "CANCELLED", "Task cancellation was observed.", DeviceConnectionStatus.Unknown, startedAt, completedAt);
                cancelled.ExceptionType = exception.GetType().Name;
                return cancelled;
            }

            return DeviceTaskResult.FromException(task, exception, startedAt, completedAt);
        }
    }
}
