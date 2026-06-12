using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Tasks;

namespace ControlDoor.Devices.Management
{
    public sealed class DeviceOperationResult
    {
        public bool Success { get; set; }

        public string Code { get; set; } = "OK";

        public string Message { get; set; } = string.Empty;

        public int DeviceId { get; set; }

        public DeviceRuntimeSnapshot Snapshot { get; set; }

        public DeviceTaskResult TaskResult { get; set; }

        public static DeviceOperationResult FromSnapshot(bool success, string code, string message, DeviceRuntimeSnapshot snapshot)
        {
            return new DeviceOperationResult
            {
                Success = success,
                Code = code,
                Message = message,
                DeviceId = snapshot == null ? 0 : snapshot.DeviceId,
                Snapshot = snapshot
            };
        }
    }
}
