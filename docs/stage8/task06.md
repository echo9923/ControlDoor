# 阶段 8 / 任务 06：现场联调、排障、回滚与最终验收

## 目标

固定现场联调步骤、问题排查入口、回滚策略和最终验收标准，确保方案能够从开发验证顺利进入现场运行。

## 现场联调前置条件

| 条件 | 标准 |
| --- | --- |
| 发布包 | `门禁publish\ServicePackage` 结构完整。 |
| 配置 | `Configuration/appsettings.json` 已填现场值。 |
| 设备清单 | `Configuration/devices.json` 已填现场设备 ID/IP、账号、端口、`enabled` 和 `types`。 |
| validate | `ControlDoor.exe --validate-config` 通过。 |
| 数据库 | 初始化脚本和专项脚本已按顺序执行。 |
| 设备 | 测试设备 ID/IP、账号、端口、协议确认。 |
| SDK | DLL 位数和进程位数一致。 |
| 白名单 | 真实设备测试白名单已配置。 |
| 测试员工 | 准备专用测试员工、权限和人脸图片。 |

## 联调顺序

| 顺序 | 场景 | 通过标准 |
| --- | --- | --- |
| 1 | 服务启动 | 日志显示配置、数据库、SDK、gRPC 启动成功。 |
| 2 | 设备登录 | 测试设备登录成功，状态 Online。 |
| 3 | 设备管理 gRPC | `GetDeviceStatus` 返回设备状态。 |
| 4 | 权限同步 | 测试员工权限下发成功。 |
| 5 | 人员人脸同步 | 人员先于人脸下发，人脸大小限制生效。 |
| 6 | 删除操作 | 删除人脸、删除人员结果可追踪。 |
| 7 | 离线补偿 | 设备离线时 queued，恢复后补偿成功。 |
| 8 | ACS 实时事件 | 现场认证后写入 `attendance_gate_v2`。 |
| 9 | 抓拍保存 | `snapshots` 下有图片，表中有路径。 |
| 10 | 离线事件上传补偿 | 设备恢复后收到 `byCurrentEvent=2` 回调并入库。 |
| 11 | 服务停止 | 撤防、登出、SDK cleanup 日志完整。 |

## 排障入口

| 问题 | 优先检查 |
| --- | --- |
| 服务启动失败 | validate 输出、日志目录、配置 JSON、端口占用。 |
| 数据库连接失败 | 连接字符串、账号权限、Docker SQL Server 状态、核心表是否存在。 |
| SDK 初始化失败 | DLL 路径、位数、依赖 DLL、SDK 日志目录。 |
| 设备登录失败 | IP、端口、账号密码、网络、防火墙、SDK 错误码。 |
| gRPC 调用失败 | 端口监听、API Key、方法名、请求 JSON。 |
| 权限下发失败 | 设备在线状态、设备能力、SDK/ISAPI 错误、补偿表。 |
| 补偿不执行 | `device_operation_retry_states`、next_retry_at、设备状态、worker 队列。 |
| 事件不入库 | 布防状态、ACS 回调日志、事件队列、`attendance_gate_v2` 唯一键。 |
| 抓拍不保存 | 抓拍目录权限、路径长度、图片字节是否为空。 |
| 离线事件未上传 | 客户端布防状态、`byCurrentEvent` 回调日志、设备离线事件能力、事件队列。 |

## 常用数据库检查

| 目的 | 检查对象 |
| --- | --- |
| 设备是否加载 | `Configuration/devices.json` 和 `GetDeviceStatus includeDisabled=true`。 |
| 用户权限字段 | `dbo.system_users`。 |
| 离线补偿状态 | `dbo.device_operation_retry_states`。 |
| 实时事件记录 | `dbo.attendance_gate_v2`。 |
| 离线事件记录 | `dbo.attendance_gate_v2`。 |

现场查询应只读为主。需要修改数据时，必须先备份并明确目标字段。

## 回滚策略

| 对象 | 回滚方式 |
| --- | --- |
| 服务包 | 停止服务，切回上一版 `ServicePackage`，启动旧服务。 |
| 配置 | 恢复上一版 `Configuration/appsettings.json`。 |
| Windows Service | 服务名不变，只切换 `binPath` 或目录。 |
| 数据库脚本 | 现有表结构不改；新增表如后续阶段引入，必须有独立回滚说明。 |
| 日志/抓拍 | 默认保留，不随回滚删除。 |

阶段 8 自身不修改表结构，因此回滚主要是服务包和配置回退。

## 验收资料

| 资料 | 内容 |
| --- | --- |
| 构建日志 | restore、build、test 结果。 |
| 发布包清单 | 文件、目录、DLL、配置模板。 |
| validate 输出 | Passed/Warning/Failed 明细。 |
| 联调记录 | 每个联调项的时间、设备、员工、结果。 |
| 数据库截图或查询结果 | 关键表记录证明。 |
| 服务日志 | 启动、设备、gRPC、补偿、事件、停止日志。 |
| 问题记录 | 未解决问题、临时规避、后续任务。 |

当前仓库提供发布包文档模板：`docs/stage8/package-docs/部署说明.md`、`docs/stage8/package-docs/运行前检查.md`、`docs/stage8/package-docs/联调记录模板.md`。打包时复制到 `ServicePackage/docs/`，并由 `tools\test-service-package.ps1` 检查是否存在。

## 最终验收标准

| 标准 | 说明 |
| --- | --- |
| gRPC 完全兼容 | 所有既定方法、请求、响应、错误码通过契约测试。 |
| 数据库零结构变更 | 现有表字段、索引、约束未被修改。 |
| 设备链路可用 | 登录、状态、设备管理可验证。 |
| 同步链路可用 | 权限、人员、人脸、删除、查询、采集可验证。 |
| 补偿链路可用 | 离线排队、恢复重试、终态清理可验证。 |
| 事件链路可用 | ACS 实时入库、抓拍、离线事件上传补偿可验证。 |
| 发布包可部署 | 现场可以按文档安装、启动、停止、卸载。 |
| 排障可定位 | 出问题能通过日志、表、SDK 错误码定位。 |
