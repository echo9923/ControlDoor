namespace ControlDoor.Hikvision
{
    public sealed class LoginResponse
    {
        public int UserId { get; set; }

        public DeviceInfo DeviceInfo { get; set; }
    }
}
