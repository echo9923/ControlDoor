namespace ControlDoor.Devices.Tasks
{
    public sealed class DeviceTaskPayload
    {
        public string PayloadKind { get; set; } = string.Empty;

        public object Body { get; set; }

        public string PayloadSummary { get; set; } = string.Empty;

        public int PayloadSizeBytes { get; set; }

        public bool AllowFullPayloadLogging { get; set; }

        public static DeviceTaskPayload Empty()
        {
            return new DeviceTaskPayload();
        }

        public DeviceTaskPayload Clone()
        {
            return new DeviceTaskPayload
            {
                PayloadKind = PayloadKind,
                Body = Body,
                PayloadSummary = PayloadSummary,
                PayloadSizeBytes = PayloadSizeBytes,
                AllowFullPayloadLogging = AllowFullPayloadLogging
            };
        }
    }
}
