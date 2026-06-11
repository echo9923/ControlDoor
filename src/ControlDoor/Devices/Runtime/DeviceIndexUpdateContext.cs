namespace ControlDoor.Devices.Runtime
{
    public sealed class DeviceIndexUpdateContext
    {
        public string RequestId { get; set; }

        public string OperationName { get; set; }

        public string Source { get; set; }

        public string OldValue { get; set; }

        public string NewValue { get; set; }
    }
}
