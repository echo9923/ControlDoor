using System;
using System.Collections.Generic;
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

        // 声明态设备类型（Acs/FaceCapture/Camera），可多选。
        // 由 JSON 设备清单填写，启动期即可分类。
        public IList<DeviceType> Types { get; set; } = new List<DeviceType>();

        public DeviceRuntimeCreationOptions ToRuntimeOptions(DateTime? now = null)
        {
            return new DeviceRuntimeCreationOptions
            {
                DeviceId = DeviceId,
                DeviceName = DeviceName,
                Description = Description,
                IpAddress = IpAddress,
                Port = Port,
                Username = Username,
                Password = Password,
                Enabled = Enabled,
                Types = Types,
                CreatedAt = now
            };
        }
    }
}
