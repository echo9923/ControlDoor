# 阶段 8 / 任务 04：配置模板与运行前检查

## 目标

固定 `Configuration/appsettings.json` 模板内容、运行前检查项和 `--validate-config` 验证行为，确保服务启动前能发现配置、数据库、端口、目录和 SDK DLL 问题。

## 配置模板原则

| 原则 | 说明 |
| --- | --- |
| 唯一路径 | 只读取运行目录 `Configuration/appsettings.json`。 |
| 占位值 | 仓库和发布模板不写真实密钥。 |
| 本地自用 | 是否记录完整字段由配置控制，不强制脱敏。 |
| 默认保守 | 默认不记录完整 gRPC payload 和人脸 Base64，避免日志膨胀。 |
| 兼容保留 | 后续阶段配置可默认关闭，但保留结构。 |

## 必要配置分组

| 分组 | 运行前必须检查 |
| --- | --- |
| `Service` | 端口范围、API Key 策略。 |
| `Database` | 连接字符串、命令超时。 |
| `Logging` | 日志目录、保留天数、payload 日志开关。 |
| `DeviceRuntime` | worker 数、队列容量。 |
| `HikvisionSdk` | DLL 路径、平台、SDK 日志目录。 |
| `DeviceLifecycle` | 登录超时、状态检测、重连策略。 |
| `DeviceOperationRetry` | 扫描间隔、最大次数、保留天数。 |
| `FaceEventLogging` | 抓拍目录、事件开关、离线事件上传补偿。 |
| `FaceEnrollment` | 图片大小限制和任务保留。 |
| `CameraAlarmDoorInterlock` | 后续联动配置，默认关闭。 |

## `--validate-config` 行为

| 步骤 | 检查 |
| --- | --- |
| 1 | 确定运行目录。 |
| 2 | 检查 `Configuration/appsettings.json` 是否存在。 |
| 3 | 解析 JSON 并加载配置模型。 |
| 4 | 校验必填项，补齐可选默认值。 |
| 5 | 检查 `Configuration/devices.json` 或内联 `Devices.Items` 设备清单是否存在、可解析且 `types` 合法。 |
| 6 | 检查日志目录、SDK 日志目录、抓拍目录是否可创建和写入。 |
| 7 | 检查数据库连接和当前仍使用的核心表可读。 |
| 8 | 检查 gRPC 端口是否可用。 |
| 9 | 检查 Hikvision SDK DLL 是否存在、平台是否匹配。 |
| 10 | 可选执行 SDK Init/Cleanup 和版本读取。 |
| 11 | 输出中文检查结果摘要。 |

验证模式不启动 gRPC 服务、不启动设备 worker、不下发设备操作。

## 核心表检查

| 表 | 阶段 8 要求 |
| --- | --- |
| `dbo.system_users` | 必须可读。 |
| `dbo.attendance_gate_v2` | 阶段 7 启用时必须可读写检查。 |
| `dbo.device_operation_retry_states` | 阶段 6 启用时必须可读写检查。 |
| `dbo.face_event_checkpoint` | 阶段 7 不读写；如现场旧库存在，仅做结构不变检查。 |

当前版本的设备主数据来自 `Configuration/devices.json`，运行前检查只校验 JSON 设备清单和仍被业务使用的数据库表。

读写检查应使用低风险方式，例如事务内插入测试再回滚，或只做权限探测；不得改变现场数据。

## 目录检查

| 目录 | 失败策略 |
| --- | --- |
| 日志目录 | 不可创建则启动失败。 |
| SDK 日志目录 | 按 `RequireSdkLog` 决定失败或 warning。 |
| 抓拍目录 | 阶段 7 启用时不可创建则事件模块不可用。 |
| 发布 docs 目录 | 缺失不阻止服务启动，但发布包检查应失败。 |

## 端口检查

| 项目 | 规则 |
| --- | --- |
| `Service.GrpcListenPort` | 必须在 1-65535。 |
| 默认端口 | 5001。 |
| 占用 | 启动失败并记录明确错误。 |
| 监听地址 | 默认 `0.0.0.0`。 |

## 日志字段配置

| 配置 | 行为 |
| --- | --- |
| `EnableGrpcPayloadLogging` | 是否记录 gRPC payload。 |
| `GrpcPayloadLogMode` | `Summary` 或 `Full`。 |
| `IncludeCredentialFields` | 是否允许记录密码、API Key 等字段。 |
| `IncludeFaceImageBase64` | 是否允许记录人脸 Base64。 |
| `EnableSdkTrace` | 是否记录 SDK 调用链路。 |

这些开关只控制日志记录范围，不改变业务处理。现场自用可以按需要开启完整记录。

## 检查输出

验证模式应输出：

| 输出 | 说明 |
| --- | --- |
| 配置文件路径 | 完整路径。 |
| 运行目录 | exe 所在目录。 |
| gRPC 端口 | 端口和占用状态。 |
| 设备清单 | `Configuration/devices.json` 路径、解析结果、设备数量和 `types` 校验摘要。 |
| 数据库 | 连接结果和核心表状态。 |
| SDK | DLL 路径、平台、版本、日志目录。 |
| 日志 | 日志目录和记录策略摘要。 |
| 抓拍 | 抓拍目录状态。 |
| 结论 | Passed / Warning / Failed。 |

当前实现的 `ControlDoor.exe --validate-config` 使用阶段 8 严格检查：只加载运行目录 `Configuration/appsettings.json`，输出运行目录、配置路径、gRPC 端口、日志策略、SDK DLL 目录、SDK 平台、抓拍目录和数据库超时；随后检查配置文件、日志目录、SDK 日志目录、抓拍目录、端口、数据库核心表、`HCNetSDK.dll` 和 `SqlServerTypes`。验证模式只做运行前检查，不启动 gRPC 服务、设备 worker 或设备下发操作。

## 测试

| 测试 | 验证 |
| --- | --- |
| 缺配置文件 | validate 失败且提示路径。 |
| JSON 格式错误 | validate 失败且提示行列或字段。 |
| 端口占用 | validate 失败。 |
| 数据库不可连 | validate 失败。 |
| SDK DLL 缺失 | validate 失败。 |
| 抓拍目录不可写 | 阶段 7 启用时失败。 |
| 日志开关 | 输出摘要符合配置。 |
