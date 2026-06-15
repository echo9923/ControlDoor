using System;
using System.Collections.Generic;
using ControlDoor.Devices.Management;

namespace ControlDoor.Devices.Runtime
{
    public sealed class DeviceRuntimeCreationOptions
    {
        public int DeviceId { get; set; }

        public string DeviceName { get; set; }

        public string Description { get; set; }

        public string IpAddress { get; set; }

        public int Port { get; set; } = 8000;

        public string Username { get; set; }

        public string Password { get; set; }

        public bool Enabled { get; set; } = true;

        // 声明态设备类型，由 DeviceRecord.ToRuntimeOptions 透传，
        // 启动期即可分类，不依赖登录后的能力探测。
        public IList<DeviceType> Types { get; set; } = new List<DeviceType>();

        public DateTime? CreatedAt { get; set; }
    }
}
