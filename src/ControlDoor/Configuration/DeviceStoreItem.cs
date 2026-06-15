using System.Collections.Generic;

namespace ControlDoor.Configuration
{
    // 配置文件中的设备清单条目。字段命名与运行时 DeviceRecord 对齐，
    // 由 JsonDeviceRepository 在加载/写回时双向映射。
    public sealed class DeviceStoreItem
    {
        public int DeviceId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string IpAddress { get; set; } = string.Empty;

        public int Port { get; set; } = 8000;

        public string Username { get; set; } = "admin";

        public string Password { get; set; } = string.Empty;

        public bool Enabled { get; set; } = true;

        public string Remark { get; set; } = string.Empty;

        // 声明态设备类型（"Acs"/"FaceCapture"/"Camera"），可多选；
        // 大小写不敏感，由 ConfigurationValidator 归一化为枚举。
        public List<string> Types { get; set; } = new List<string>();
    }
}
