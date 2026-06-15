# 阶段 4 / 任务 02：设备加载与运行时注册

## 目标

从 `Configuration/devices.json` 加载设备主数据，转换为内存运行时对象，并注册到 `DeviceRuntimeRegistry` 的 ID、IP、UserID、AlarmHandle 索引中。任务 4.2 只负责加载和注册，不登录设备，不布防，不调用真实 SDK。

## 数据来源

| 字段 | 来源 | 用途 |
| --- | --- | --- |
| `deviceId` | `devices[].deviceId` | 设备主键、任务路由键。 |
| `name` | `devices[].name` | 状态展示和日志。 |
| `remark` | `devices[].remark` | 状态展示。 |
| `ipAddress` | `devices[].ipAddress` | SDK 登录地址和 IP 反查。 |
| `port` | `devices[].port` | SDK 登录端口。 |
| `username` | `devices[].username` | SDK 登录用户名。 |
| `password` | `devices[].password` | SDK 登录密码。 |
| `enabled` | `devices[].enabled` | `true` 表示启用。 |
| `types` | `devices[].types` | 声明态设备类型，合法值为 `Acs`、`FaceCapture`、`Camera`。 |

## 清单格式

```sql
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
      "remark": "东门主通道"
    }
  ]
}
```

说明：

- 阶段 4 启动加载默认只加载 `enabled=true` 的设备。
- `GetDeviceStatus(includeDisabled=true)` 从 JSON 清单补齐停用设备；停用设备不进入登录、布防、状态检测调度。
- `enabled` 是静态启用标记，不表示运行时在线状态。

## 运行时对象

| 类型 | 职责 |
| --- | --- |
| `DeviceRecord` | JSON 设备记录，字段与 `devices.json` 对齐。 |
| `DeviceConnectionOptions` | SDK 登录参数，包含 IP、端口、用户名、密码、登录模式。 |
| `DeviceRuntimeState` | 内存状态，包含连接状态、UserID、AlarmHandle、最近错误。 |
| `DeviceRuntimeRegistry` | 管理设备 ID、IP、UserID、AlarmHandle 反查索引。 |

## 注册流程

| 步骤 | 动作 | 失败处理 |
| --- | --- | --- |
| 1 | 读取 JSON 启用设备记录。 | 文件缺失、解析失败或字段非法则阶段 4 启动失败。 |
| 2 | 校验 `device_id > 0`、IP 非空、端口合法。 | 单条记录非法时标记为 `InvalidConfig`，不投递登录。 |
| 3 | 规范化 IP：`Trim()`，不做补零或格式重写。 | 空 IP 拒绝注册。 |
| 4 | 构造 `DeviceRuntimeState`，初始状态为 `Loaded`。 | 构造失败记录错误并跳过该设备。 |
| 5 | 注册 ID 和 IP 索引。 | ID/IP 冲突时保留第一条，冲突设备标记错误。 |
| 6 | 为设备预分配 worker 路由。 | 路由失败视为内部错误。 |
| 7 | 生成设备加载摘要日志。 | 记录成功、跳过、冲突、非法数量。 |

## 状态初始值

| 字段 | 初始值 |
| --- | --- |
| `ConnectionStatus` | `Loaded` 或 `Disabled`。 |
| `SdkUserId` | null。 |
| `AlarmHandle` | null。 |
| `LastCheckedAt` | null。 |
| `Types` | 声明态设备类型；JSON 清单必须至少填写一个合法类型。 |
| `LastErrorCode` | null。 |
| `LastErrorMessage` | null。 |
| `ReconnectState` | 未开始。 |

## 不做的事

| 不做内容 | 原因 |
| --- | --- |
| 不调用 `NET_DVR_Login_V40` | 登录编排属于任务 4.3。 |
| 不布防 | 布防编排属于任务 4.5。 |
| 不更新 JSON 文件 | 加载阶段只读。 |
| 不改 `enabled` 语义 | `enabled` 是启用标记，不是在线状态。 |
| 不写补偿 | 补偿属于阶段 6。 |

## 测试

| 测试 | 验证 |
| --- | --- |
| 空表加载 | 返回 0 台设备，服务仍可启动。 |
| 正常加载 | 多台启用设备进入运行时注册表。 |
| 停用设备 | `enabled=false` 不进入登录调度，但状态查询可展示。 |
| IP 冲突 | 冲突记录被标记，索引不覆盖已有设备。 |
| 端口非法 | 设备状态为 `InvalidConfig`，不投递登录。 |
| JSON 文件失败 | 启动失败并返回设备清单加载错误日志。 |
