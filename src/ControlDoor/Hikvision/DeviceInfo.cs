namespace ControlDoor.Hikvision
{
    public sealed class DeviceInfo
    {
        public string Model { get; set; }

        public string SerialNumber { get; set; }

        public string FirmwareVersion { get; set; }

        public string DeviceName { get; set; }

        public int DoorCount { get; set; }

        public int ChannelCount { get; set; }

        public string MacAddress { get; set; }

        public string IpAddress { get; set; }
    }
}
