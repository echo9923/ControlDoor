namespace ControlDoor.Devices.Runtime
{
    public sealed class DeviceWorkerRoutingOptions
    {
        public int WorkerCount { get; set; } = 4;

        public int QueueCapacity { get; set; } = 1000;

        public bool EnableRouteDiagnostics { get; set; } = true;
    }
}
