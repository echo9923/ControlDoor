namespace ControlDoor.Devices.Runtime
{
    public sealed class DeviceRuntimeLookupResult
    {
        private DeviceRuntimeLookupResult(
            DeviceRuntimeLookupStatus status,
            string code,
            string message,
            DeviceRuntimeSnapshot snapshot,
            int? deviceId,
            int? conflictingDeviceId,
            int? workerIndex)
        {
            Status = status;
            Code = code;
            Message = message;
            Snapshot = snapshot;
            DeviceId = deviceId;
            ConflictingDeviceId = conflictingDeviceId;
            WorkerIndex = workerIndex;
        }

        public DeviceRuntimeLookupStatus Status { get; private set; }

        public string Code { get; private set; }

        public string Message { get; private set; }

        public DeviceRuntimeSnapshot Snapshot { get; private set; }

        public int? DeviceId { get; private set; }

        public int? ConflictingDeviceId { get; private set; }

        public int? WorkerIndex { get; private set; }

        public bool Found => Status == DeviceRuntimeLookupStatus.Found;

        public static DeviceRuntimeLookupResult FoundResult(DeviceRuntimeSnapshot snapshot, int workerIndex)
        {
            return new DeviceRuntimeLookupResult(
                DeviceRuntimeLookupStatus.Found,
                "OK",
                "Device runtime was found.",
                snapshot,
                snapshot == null ? (int?)null : snapshot.DeviceId,
                null,
                workerIndex);
        }

        public static DeviceRuntimeLookupResult NotFound(string message = null)
        {
            return new DeviceRuntimeLookupResult(DeviceRuntimeLookupStatus.NotFound, "DEVICE_NOT_FOUND", message ?? "Device runtime was not found.", null, null, null, null);
        }

        public static DeviceRuntimeLookupResult Invalid(string message)
        {
            return new DeviceRuntimeLookupResult(DeviceRuntimeLookupStatus.InvalidArgument, "INVALID_ARGUMENT", message, null, null, null, null);
        }

        public static DeviceRuntimeLookupResult Conflict(int deviceId, int conflictingDeviceId, string code, string message)
        {
            return new DeviceRuntimeLookupResult(DeviceRuntimeLookupStatus.Conflict, code, message, null, deviceId, conflictingDeviceId, null);
        }

        public static DeviceRuntimeLookupResult Deleted(int deviceId, DeviceRuntimeSnapshot snapshot = null)
        {
            return new DeviceRuntimeLookupResult(DeviceRuntimeLookupStatus.Deleted, "DEVICE_DELETED", "Device runtime was deleted.", snapshot, deviceId, null, null);
        }
    }
}
