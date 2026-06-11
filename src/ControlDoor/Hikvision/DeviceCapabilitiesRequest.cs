namespace ControlDoor.Hikvision
{
    public sealed class DeviceCapabilitiesRequest
    {
        public DeviceCapabilitiesRequest()
        {
            PreferIsapi = true;
            IsapiPath = "/ISAPI/System/capabilities";
        }

        public int UserId { get; set; }

        public bool PreferIsapi { get; set; }

        public string IsapiPath { get; set; }
    }
}
