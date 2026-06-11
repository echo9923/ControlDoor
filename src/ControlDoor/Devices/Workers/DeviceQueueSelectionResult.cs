using ControlDoor.Devices.Tasks;

namespace ControlDoor.Devices.Workers
{
    public sealed class DeviceQueueSelectionResult
    {
        public DeviceQueueSelectionResult(DeviceTaskPriority priority, bool fairnessApplied)
        {
            Priority = priority;
            FairnessApplied = fairnessApplied;
        }

        public DeviceTaskPriority Priority { get; private set; }

        public bool FairnessApplied { get; private set; }
    }
}
