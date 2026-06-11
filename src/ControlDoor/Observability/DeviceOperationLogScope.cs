namespace ControlDoor.Observability
{
    public sealed class DeviceOperationLogScope
    {
        public int? DeviceId { get; set; }

        public string OperationName { get; set; } = string.Empty;

        public string Priority { get; set; } = string.Empty;

        public int TimeoutMs { get; set; }
    }
}
