using System.Collections.Generic;

namespace ControlDoor.Configuration
{
    // 设备清单固定从 JSON 读取；正式部署使用 FilePath，Items 仅用于测试或临时小规模配置。
    public sealed class DeviceStoreOptions
    {
        // 设备清单文件相对 runDirectory 的路径。
        // 默认 Configuration/devices.json，与 appsettings.json 同目录，便于运维集中管理。
        public string FilePath { get; set; } = "Configuration\\devices.json";

        // 写回前是否自动备份到 *.bak，避免运行时增删设备损坏清单。
        public bool BackupOnWrite { get; set; } = true;

        // 内联设备清单（可选）。若填写则优先于 FilePath 加载；Add/Delete 仅更新运行时缓存，不写文件。
        // 正式部署建议留空，并通过 Configuration/devices.json 维护设备。
        public List<DeviceStoreItem> Items { get; set; } = new List<DeviceStoreItem>();
    }
}
