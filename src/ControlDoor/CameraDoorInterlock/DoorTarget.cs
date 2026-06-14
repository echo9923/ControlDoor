namespace ControlDoor.CameraDoorInterlock
{
    /// <summary>
    /// 门禁目标（task03 InterlockMappingResolver 输出）。
    /// 一个摄像头可映射多个 DoorTarget；多个摄像头可映射到同一 TargetKey。
    /// </summary>
    public sealed class DoorTarget
    {
        public int DoorDeviceId { get; set; }

        public string DoorDeviceIp { get; set; } = string.Empty;

        public int DoorNo { get; set; }

        public string TargetKey { get; set; } = string.Empty;

        public string MappingId { get; set; } = string.Empty;
    }
}
