namespace ControlDoor.Hikvision
{
    public sealed class DeviceInfoRequest
    {
        public DeviceInfoRequest()
        {
            IsapiPath = "/ISAPI/System/deviceInfo";
        }

        public int UserId { get; set; }

        public string IsapiPath { get; set; }
    }
}
