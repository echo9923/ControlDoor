# 阶段 8 / 任务 02：自动化测试矩阵与执行顺序

## 目标

定义完整测试分层、执行顺序和通过标准。阶段 8 的测试策略必须覆盖兼容契约、数据库零结构变更、设备通道、SDK 网关、设备生命周期、权限人员人脸、离线补偿、人脸事件入库和发布包检查。

## 测试分层

| 层级 | 名称 | 是否自动 | 是否需要真实设备 | 目的 |
| --- | --- | --- | --- | --- |
| L1 | 单元测试 | 是 | 否 | 验证解析、校验、状态机、退避、字段映射。 |
| L2 | gRPC 契约测试 | 是 | 否 | 验证方法名、JSON string marshaller、请求别名、响应字段。 |
| L3 | Mock SDK 集成测试 | 是 | 否 | 验证设备通道、mock 网关、业务编排。 |
| L4 | 数据库兼容测试 | 是 | 否，可用测试库 | 验证现有表读写语义和结构快照。 |
| L5 | 发布包检查 | 是 | 否 | 验证文件、目录、配置模板、DLL 放置。 |
| L6 | 现场设备联调 | 否，手动显式 | 是 | 验证真实海康设备和 SDK 行为。 |

## 推荐执行顺序

| 顺序 | 命令或动作 | 说明 |
| --- | --- | --- |
| 1 | `nuget restore ControlEntradaSalida.sln` | 还原 `packages.config` 依赖。 |
| 2 | `dotnet build ControlEntradaSalida.sln --verbosity minimal` | 推荐构建命令。 |
| 3 | `msbuild ControlEntradaSalida.sln /t:Build /p:Configuration=Debug` | Debug 构建验证。 |
| 4 | `msbuild ControlEntradaSalida.sln /p:Configuration=Release` | Release 构建验证。 |
| 5 | `dotnet test tests\ControlEntradaSalida.Tests\ControlEntradaSalida.Tests.csproj --verbosity minimal` | 单元与契约测试。 |
| 6 | `dotnet test tests\ControlEntradaSalida.IntegrationTests\ControlEntradaSalida.IntegrationTests.csproj --verbosity minimal` | mock 集成与数据库兼容测试。 |
| 7 | 发布包检查脚本或人工清单 | 验证 `门禁publish\ServicePackage`。 |
| 8 | `ControlDoor.exe --validate-config` | 发布包运行前检查。 |
| 9 | L6 手动联调 | 仅在现场设备配置和白名单开启后执行。 |

如果当前工程文件尚未落地，对应命令先作为阶段 8 的实施目标；落地后必须补齐并执行。

## L1 单元测试范围

| 模块 | 覆盖 |
| --- | --- |
| 配置加载 | 路径、默认值、非法值回退、缺失必填。 |
| 日志策略 | payload、凭据、人脸 Base64 记录开关。 |
| gRPC 请求解析 | 容器别名、字段别名、批量上限、错误码。 |
| 设备状态机 | Online、Offline、Connecting、Disabled、Reconnecting。 |
| 设备任务调度 | 路由、优先级、超时、取消、异常隔离。 |
| SDK 网关 | 错误码读取、结果映射、buffer、句柄释放。 |
| 补偿合并 | pending 合并、冲突覆盖、终态重新激活。 |
| ACS 解析 | 事件字段、流水、时间、raw payload。 |

## L2 gRPC 契约测试范围

| 类别 | 必测 |
| --- | --- |
| 服务名 | `/device.AccessControlService/*`、`/permission.PermissionSyncService/*` 保持不变。 |
| 方法类型 | Unary 和 server streaming 类型保持不变。 |
| marshaller | 请求和响应均为 UTF-8 JSON string。 |
| API Key | 管理接口空值、正确、错误三种路径。 |
| 字段别名 | 已确认的别名都能解析。 |
| 批量上限 | 超过 500 返回固定错误。 |
| 响应字段 | `success`、`code`、`message`、`errors/errorDetails` 等兼容字段存在。 |

## L3 Mock SDK 集成测试范围

| 阶段 | 场景 |
| --- | --- |
| 阶段 4 | 设备加载、登录、断开、重连、删除、状态查询。 |
| 阶段 5 | 权限、人员、人脸、删除、查询、采集。 |
| 阶段 6 | 离线补偿扫描、投递、成功、失败、终态。 |
| 阶段 7 | ACS 实时入库、抓拍保存、历史补偿、实时缓冲。 |

## L4 数据库兼容测试范围

| 表 | 验证 |
| --- | --- |
| `devices` | 设备加载、插入、删除、状态字段更新不改结构。 |
| `system_users` | 权限字段更新、昵称查询、不改结构。 |
| `attendance_gate_v2` | 事件 insert、业务 `id` 防重、不改结构。 |
| `device_operation_retry_states` | upsert、扫描、回写、终态、清理、不改结构。 |
| `face_event_checkpoint` | 按设备 IP 读写断点、不改结构。 |

## L5 发布包检查

| 检查项 | 标准 |
| --- | --- |
| 可执行文件 | `ControlDoor.exe` 存在。 |
| .NET 配置 | `ControlDoor.exe.config` 只承载框架级配置。 |
| 业务配置 | `Configuration/appsettings.json` 存在。 |
| SDK DLL | Hikvision SDK 和依赖 DLL 位于约定目录或 exe 同级。 |
| SQL Server Types | 依赖 DLL 位于发布包可加载位置。 |
| 日志目录 | `logs/` 存在或可自动创建。 |
| 抓拍目录 | `snapshots/` 存在或可自动创建。 |
| 说明文档 | 包含运行前检查和服务安装说明。 |

## L6 现场设备联调

| 规则 | 说明 |
| --- | --- |
| 默认关闭 | 不随自动化测试执行。 |
| 设备白名单 | 必须配置允许联调的设备 ID/IP。 |
| 操作白名单 | 必须配置允许执行的真实操作。 |
| 数据隔离 | 使用测试员工、测试权限和测试时间窗口。 |
| 日志留存 | 联调前后保留服务日志和 SDK 日志。 |

## 阶段通过标准

| 标准 | 说明 |
| --- | --- |
| L1-L5 可自动验证 | 不依赖真实设备。 |
| L6 可手动执行 | 有开关、白名单、步骤和通过标准。 |
| 表结构快照通过 | 证明没有修改现有表结构。 |
| gRPC 契约通过 | 证明完全兼容。 |
| 发布包检查通过 | 现场部署前能发现缺失文件和配置错误。 |
