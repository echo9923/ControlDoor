using System;

namespace ControlDoor.Devices.Runtime
{
    public sealed class DeviceRuntimeCreationOptions
    {
        public int DeviceId { get; set; }

        public string DeviceName { get; set; }

        public string IpAddress { get; set; }

        public int Port { get; set; } = 8000;

        public string Username { get; set; }

        public string Password { get; set; }

        public bool Enabled { get; set; } = true;

        public DateTime? CreatedAt { get; set; }

        public DateTime? LastUsedAt { get; set; }
    }
}
