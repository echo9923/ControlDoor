namespace ControlDoor.Devices.Runtime
{
    public static class DeviceIndexKeyNormalizer
    {
        public static string NormalizeIpAddress(string ipAddress)
        {
            return string.IsNullOrWhiteSpace(ipAddress) ? string.Empty : ipAddress.Trim();
        }
    }
}
