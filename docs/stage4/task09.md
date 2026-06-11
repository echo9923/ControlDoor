# 阶段 4 / 任务 09：阶段测试与验收

## 目标

定义阶段 4 完成所需的自动化测试、mock 集成测试、数据库兼容测试和手动联调检查。阶段 4 的测试必须证明设备生命周期和设备管理 gRPC 可用，同时不破坏阶段 0 冻结的兼容边界。

## 单元测试

| 测试类 | 覆盖内容 |
| --- | --- |
| `DeviceRecordMapperTests` | 数据库记录到运行时对象的映射、端口解析、状态转换。 |
| `DeviceRuntimeRegistrationTests` | ID/IP/UserID/AlarmHandle 索引注册、冲突和清理。 |
| `DeviceLoginTaskTests` | 登录成功、失败、重复登录、索引失败补偿登出。 |
| `DeviceLogoutTaskTests` | 撤防、登出、索引清理、失败 warning。 |
| `DeviceReconnectPolicyTests` | 指数退避、最大次数、冷却、手动绕过。 |
| `DeviceAlarmOrchestrationTests` | 登录后布防、删除前撤防、布防失败策略。 |
| `AccessControlGrpcContractTests` | 设备管理 5 个方法 JSON 字段和响应字段。 |

## Mock SDK 集成测试

| 场景 | 预期 |
| --- | --- |
| 启动加载 3 台设备 | 全部注册，按 worker 路由投递登录。 |
| 部分设备登录失败 | 成功设备 Online，失败设备 ReconnectPending。 |
| 登录成功后布防失败 | 设备 Online，但事件功能状态失败。 |
| 自动重连成功 | 失败计数清零，UserID 更新，重新布防。 |
| 手动断开 | 自动重连任务取消，状态 Disconnected。 |
| 强制重连 | 旧 UserID/AlarmHandle 清理后重新登录。 |
| 删除设备 | DB 删除、运行时移除、索引清理。 |

## gRPC 契约测试

| 方法 | 必测内容 |
| --- | --- |
| `GetDeviceStatus` | 空请求、按 ID、按 IP、refresh、includeDisabled。 |
| `AddDevice` | 字段别名、默认值、connectNow 成功/失败。 |
| `DeleteDevice` | disconnectFirst 默认值、删除成功、不存在。 |
| `DisconnectDevice` | 在线、离线、不存在。 |
| `ReconnectDevice` | force true/false、在线、离线、失败。 |

所有响应必须包含统一字段：`requestId`、`success`、`code`、`message`、`errors`、`errorDetails`。

## 数据库兼容测试

| 测试 | 验证 |
| --- | --- |
| 表结构快照 | `dbo.devices` 字段、索引、触发器未改变。 |
| 加载查询 | 只读取既有字段。 |
| 新增设备 | 插入既有字段，不新增列。 |
| 删除设备 | 只按 `device_id` 删除目标行。 |
| last_used_time | 登录成功只更新 `last_used_time`。 |
| status 语义 | 不把在线/离线写入 `status`。 |

## 手动联调

| 联调项 | 检查 |
| --- | --- |
| 启动加载 | 日志输出加载设备数量和登录结果。 |
| 查询状态 | `GetDeviceStatus` 返回在线/离线明细。 |
| 新增测试设备 | `AddDevice(connectNow=false)` 只写库和内存。 |
| 立即连接 | `AddDevice(connectNow=true)` 尝试登录并返回连接结果。 |
| 手动断开 | 设备撤防、登出，状态变为 Disconnected。 |
| 手动重连 | 设备重新登录，必要时重新布防。 |
| 删除测试设备 | 删除后查询不到，数据库记录消失。 |

## 阶段 4 通过标准

| 标准 | 说明 |
| --- | --- |
| 生命周期闭环 | 加载、登录、状态检测、重连、布防、撤防、登出、删除都有明确流程。 |
| gRPC 兼容 | 5 个设备管理方法契约与 `docs/gRPC接口清单.md` 一致。 |
| 数据库零变更 | 没有修改现有表结构，没有新增阶段 4 表。 |
| 通道一致 | 所有设备 SDK 操作都通过 `DeviceSdkDispatcher`。 |
| 测试充分 | 单元测试、mock 集成测试、数据库兼容测试覆盖主要成功和失败路径。 |
| 后续可承载 | 阶段 5 权限同步、阶段 6 离线补偿、阶段 7 事件入库能基于设备在线状态和任务通道继续实现。 |
