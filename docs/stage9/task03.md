# 阶段 9 / 任务 03：配置映射与目标解析

## 目标

实现 `CameraAlarmDoorInterlock` 配置加载和映射解析，将来源摄像头映射到一个或多个门禁设备门号。

## 配置结构

沿用顶层配置：

| 配置项 | 默认值 | 说明 |
| --- | --- | --- |
| `Enabled` | `false` | 是否启用阶段 9。 |
| `WindowSeconds` | `5` | 常闭窗口秒数。 |
| `DoorControlSdkLockTimeoutMs` | `5000` | 兼容配置，设备通道模型中不作为互斥等待参数。 |
| `RestoreRetryIntervalMs` | `1000` | 恢复失败重试间隔。恢复任务对可重试错误持续重试，无最大次数。 |
| `Mappings` | `[]` | 摄像头到门禁目标映射。 |

## 映射模型

建议每条 mapping 包含：

| 字段 | 说明 |
| --- | --- |
| `Camera.Id` | 摄像头设备 ID，可选。 |
| `Camera.Ip` | 摄像头 IP，优先使用。 |
| `DoorDevice.Id` | 门禁设备 ID，可选。 |
| `DoorDevice.Ip` | 门禁设备 IP，优先使用。 |
| `DoorNos` | 门号数组，省略默认 `[1]`。 |
| `Enabled` | 单条映射开关，缺省 true。 |
| `Remark` | 现场备注。 |

## 识别规则

| 对象 | 规则 |
| --- | --- |
| 摄像头 | 优先 `Ip`；只有 `Ip` 为空时使用 `Id`。 |
| 门禁设备 | 优先 `Ip`；只有 `Ip` 为空时使用 `Id`。 |
| 门号 | `DoorNos` 为空或缺失时默认 `[1]`。 |
| 映射禁用 | `Enabled = false` 时忽略。 |

## 校验规则

| 配置 | 规则 |
| --- | --- |
| `WindowSeconds` | 小于 1 使用默认值。 |
| `RestoreRetryIntervalMs` | 小于 100 使用默认值。恢复任务对可重试错误持续重试，无最大次数限制。 |
| `Mappings` | 启用时不能为空。 |
| 摄像头标识 | `Camera.Ip` 和 `Camera.Id` 至少一个有效。 |
| 门禁标识 | `DoorDevice.Ip` 和 `DoorDevice.Id` 至少一个有效。 |
| 门号 | 必须为正整数。 |

配置错误不应导致整个服务必然失败；如果 `Enabled = true` 但无有效映射，阶段 9 模块不可用并记录错误，其他首期模块仍可按策略运行。

## 目标解析

`InterlockMappingResolver` 输出 `DoorTarget`：

| 字段 | 说明 |
| --- | --- |
| `DoorDeviceId` | 门禁设备 ID。 |
| `DoorDeviceIp` | 门禁设备 IP。 |
| `DoorNo` | 门号。 |
| `TargetKey` | `doorDeviceId + doorNo` 或 `doorIp + doorNo`。 |
| `MappingId` | 配置项摘要。 |

一个摄像头可解析出多个 `DoorTarget`。多个摄像头可解析到同一个 `DoorTarget`。

## 与 JSON 设备清单关系

| 场景 | 处理 |
| --- | --- |
| 门禁设备在 JSON 清单中存在 | 使用运行时设备 ID 投递任务。 |
| 只配置 IP | 启动时尝试从设备运行时按 IP 反查 ID。 |
| 找不到门禁设备 | 映射无效，记录错误。 |
| 摄像头不在 JSON 清单 | 仍可按 IP 识别回调来源，但推荐纳入设备管理。 |

阶段 9 不要求新增数据库表保存映射，映射来自 JSON 配置。

## 不做的事

| 不做内容 | 原因 |
| --- | --- |
| 不从数据库加载映射 | 阶段 9 默认配置驱动。 |
| 不热更新映射 | 首期按启动时加载；变更后重启服务。 |
| 不修改 `devices` 表结构 | 现有表零变更。 |
| 不要求配置模型字段加密 | 本地自用配置，按现有配置策略管理。 |

## 测试

| 测试 | 验证 |
| --- | --- |
| IP 优先 | 同时配置 IP 和 ID 时使用 IP。 |
| ID 兜底 | IP 为空时使用 ID。 |
| DoorNos 默认 | 缺失时使用 `[1]`。 |
| 多门目标 | 一个摄像头解析多个门目标。 |
| 共享门目标 | 两个摄像头解析到同一 TargetKey。 |
| 无效映射 | 模块记录错误且不触发门控。 |
