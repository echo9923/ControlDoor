namespace ControlDoor.Devices.Runtime
{
    public sealed class DeviceRuntimeMutationResult
    {
        private DeviceRuntimeMutationResult(
            bool success,
            string code,
            string message,
            DeviceRuntimeLookupStatus status,
            DeviceRuntimeSnapshot snapshot,
            int? conflictingDeviceId,
            int? workerIndex)
        {
            Success = success;
            Code = code;
            Message = message;
            Status = status;
            Snapshot = snapshot;
            ConflictingDeviceId = conflictingDeviceId;
            WorkerIndex = workerIndex;
        }

        public bool Success { get; private set; }

        public string Code { get; private set; }

        public string Message { get; private set; }

        public DeviceRuntimeLookupStatus Status { get; private set; }

        public DeviceRuntimeSnapshot Snapshot { get; private set; }

        public int? ConflictingDeviceId { get; private set; }

        public int? WorkerIndex { get; private set; }

        public static DeviceRuntimeMutationResult Succeeded(DeviceRuntimeSnapshot snapshot, int? workerIndex = null, string code = "OK", string message = null)
        {
            return new DeviceRuntimeMutationResult(true, code, message ?? "Device runtime mutation succeeded.", DeviceRuntimeLookupStatus.Found, snapshot, null, workerIndex);
        }

        public static DeviceRuntimeMutationResult NotFound(string message = null)
        {
            return new DeviceRuntimeMutationResult(false, "DEVICE_NOT_FOUND", message ?? "Device runtime was not found.", DeviceRuntimeLookupStatus.NotFound, null, null, null);
        }

        public static DeviceRuntimeMutationResult Invalid(string message)
        {
            return new DeviceRuntimeMutationResult(false, "INVALID_ARGUMENT", message, DeviceRuntimeLookupStatus.InvalidArgument, null, null, null);
        }

        public static DeviceRuntimeMutationResult Conflict(int conflictingDeviceId, string code, string message)
        {
            return new DeviceRuntimeMutationResult(false, code, message, DeviceRuntimeLookupStatus.Conflict, null, conflictingDeviceId, null);
        }

        public static DeviceRuntimeMutationResult Deleted(DeviceRuntimeSnapshot snapshot)
        {
            return new DeviceRuntimeMutationResult(true, "DEVICE_DELETED", "Device runtime was deleted.", DeviceRuntimeLookupStatus.Deleted, snapshot, null, null);
        }
    }
}
