namespace ControlDoor.Hikvision
{
    public sealed class AlarmDeploymentStatusRequest
    {
        public int UserId { get; set; }

        public int Channel { get; set; } = -1;

        public int AlarmInputIndex { get; set; }
    }
}
