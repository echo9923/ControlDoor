# 阶段 10 / 任务 01：设备清单 JSON 化与设备类型字段

## 目标

阶段 10 将设备主数据固定为独立 `Configuration/devices.json`。设备类型通过同一个 `devices` 列表中的 `types` 多选数组声明，不拆分门禁、人脸采集、摄像头三段配置。

本阶段同时移除运行时和 gRPC 输出中的 `lastUsed` / `LastUsedAt` 维护。

## 设备类型语义

| 类型 | 含义 | 当前消费方 |
| --- | --- | --- |
| `Acs` | 门禁设备，包含自带人脸识别并负责刷脸开门的门禁机。 | 权限同步、人员同步、人脸库上传/删除/查询、AIOP 门禁目标。 |
| `FaceCapture` | 人脸采集设备或具备采集入口能力的设备。 | `CaptureFaceStream` 人脸采集。 |
| `Camera` | 摄像头/AIOP 报警来源设备。 | 阶段 9 AIOP 来源识别和配置映射前置校验。 |

重要边界：

- 自带人脸识别的门禁设备必须声明 `Acs`，因为它需要接收人员信息、人脸库和权限，才能刷脸开门。
- 明眸等设备如果既能刷脸开门又能采集人脸，可声明 `["Acs", "FaceCapture"]`。
- 单纯摄像头只声明 `["Camera"]`，不参与权限、人脸库下发。

## 配置结构

`appsettings.json` 只配置 JSON 设备清单路径和写回策略：

```json
"Devices": {
  "FilePath": "Configuration\\devices.json",
  "BackupOnWrite": true,
  "Items": []
}
```

`Configuration/devices.json` 采用统一列表：

```json
{
  "devices": [
    {
      "deviceId": 1001,
      "name": "1号楼大门门禁",
      "types": ["Acs"],
      "ipAddress": "192.168.1.64",
      "port": 8000,
      "username": "admin",
      "password": "******",
      "enabled": true,
      "remark": "办公区域"
    },
    {
      "deviceId": 2001,
      "name": "前台明眸",
      "types": ["Acs", "FaceCapture"],
      "ipAddress": "192.168.1.65",
      "port": 8000,
      "username": "admin",
      "password": "******",
      "enabled": true
    },
    {
      "deviceId": 3001,
      "name": "周界摄像头01",
      "types": ["Camera"],
      "ipAddress": "192.168.1.100",
      "port": 8000,
      "username": "admin",
      "password": "******",
      "enabled": true
    }
  ]
}
```

`Items` 仅用于测试或临时小规模部署的内联设备清单；若 `Items` 非空，`JsonDeviceRepository` 优先使用内联清单并跳过文件写回。正式部署建议保持 `Items` 为空，并维护 `Configuration/devices.json`。

`remark` 对应 gRPC `description`。为兼容既有业务，权限同步会从该文本中识别区域：包含 `办公` 视为办公区域，包含 `生产` 视为生产区域，为空或不匹配视为 `Other`。

## 持久化与写回

| 项目 | 规则 |
| --- | --- |
| 读缓存 | `JsonDeviceRepository` 启动时读取一次 `devices.json`，后续 `Load*` / `Exists*` 走内存缓存。 |
| 新增设备 | `AddDevice` 解析 `types`，通过 `DeviceLifecycleService.RegisterDevice` 校验后写回 JSON 并注册运行时。 |
| 删除设备 | `DeleteDevice` 先执行必要清理，再从 JSON 缓存和文件删除。 |
| 原子写 | 写入 `devices.json.tmp` 后替换目标文件；`File.Replace` 不可用时回退到同目录 rename 覆盖。 |
| 备份 | `BackupOnWrite=true` 时写前复制当前文件为 `devices.json.bak`。 |
| 回滚 | 写回失败时回滚内存缓存，避免运行时内存态与磁盘态不一致。 |
| 文件缺失 | `devices.json` 不存在且未配置内联 `Items` 时启动失败，并提示创建 `Configuration/devices.json`。 |

## 类型路由规则

| gRPC / 模块 | 目标设备类型 |
| --- | --- |
| `SyncPermissions` | `Acs` |
| `SyncPersons` | `Acs` |
| `DeletePersons` | `Acs` |
| `DeleteFaces` | `Acs` |
| `GetFaces` | `Acs` |
| `CaptureFaceStream` | `FaceCapture` |
| `CameraAlarmDoorInterlock` 摄像头来源 | `Camera` |
| `CameraAlarmDoorInterlock` 门禁目标 | `Acs` |

运行时快照 `Types` 为空时仍按兼容旧对象处理，避免测试或历史内存对象破坏业务；但 JSON 清单和 `AddDevice` 新增设备必须填写至少一个合法类型。

## `lastUsedTime` 移除范围

| 层级 | 处理 |
| --- | --- |
| `IDeviceRepository` | 删除 `UpdateLastUsedTime`。 |
| 设备仓储 | 只保留 JSON 设备仓储。 |
| `DeviceRecord` / runtime options / runtime snapshot | 删除 `LastUsedAt` 字段。 |
| `DeviceLifecycleService` | 登录或业务成功不再回写最近使用时间。 |
| `GetDeviceStatus` | 输出 `types`，不再输出 `lastUsed`。 |

## 验收标准

| 标准 | 说明 |
| --- | --- |
| JSON 清单可启动 | `Configuration/devices.json` 存在或 `Devices.Items` 非空时，服务按 JSON 清单加载启用设备。 |
| JSON-only 清单 | 设备加载、AddDevice 和 DeleteDevice 均只使用 JSON 设备清单。 |
| 类型贯通 | `DeviceRecord.Types` 透传到 runtime state、snapshot 和 `GetDeviceStatus` 响应。 |
| 门禁刷脸下发正确 | 自带人脸识别门禁声明 `Acs` 后，会收到权限、人员、人脸库同步。 |
| 人脸采集独立 | `CaptureFaceStream` 只选择 `FaceCapture` 设备。 |
| AIOP 映射前置发现错配 | 摄像头引用非 `Camera`、门禁目标引用非 `Acs` 时记录配置错误/警告。 |
| `lastUsed` 已退出 | gRPC 状态不再输出 `lastUsed`，运行时不再维护 `LastUsedAt`。 |
| 自动化测试通过 | Stage10、Stage4、Stage8 和完整自研测试运行通过。 |
