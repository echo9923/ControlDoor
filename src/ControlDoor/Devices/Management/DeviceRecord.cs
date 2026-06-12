using System;
using ControlDoor.Devices.Runtime;

namespace ControlDoor.Devices.Management
{
    public sealed class DeviceRecord
    {
        public int DeviceId { get; set; }

        public string DeviceName { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public string IpAddress { get; set; } = string.Empty;

        public int Port { get; set; } = 8000;

        public string Username { get; set; } = "admin";

        public string Password { get; set; } = string.Empty;

        public bool Enabled { get; set; } = true;

        public DateTime? LastUsedAt { get; set; }

        public DeviceRuntimeCreationOptions ToRuntimeOptions(DateTime? now = null)
        {
            return new DeviceRuntimeCreationOptions
            {
                DeviceId = DeviceId,
                DeviceName = DeviceName,
                IpAddress = IpAddress,
                Port = Port,
                Username = Username,
                Password = Password,
                Enabled = Enabled,
                CreatedAt = now,
                LastUsedAt = LastUsedAt
            };
        }
    }
}
