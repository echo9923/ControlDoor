# 仓库指引

## 构建、测试与开发命令
- 还原包：`nuget restore ControlEntradaSalida.sln`，用于还原 `packages.config` 依赖。
- Debug 构建：`msbuild ControlEntradaSalida.sln /t:Build /p:Configuration=Debug`。
- Release 构建：`msbuild ControlEntradaSalida.sln /p:Configuration=Release`。
- 推荐构建：`dotnet build ControlEntradaSalida.sln --verbosity minimal`。
- Debug 运行：`bin/Debug/ControlEntradaSalida.exe`。

运行前需确认第三方 DLL（Hikvision SDK、`SqlServerTypes`）已放置在 `bin/` 或引用目录中。

## 编码风格与命名规范
- 缩进使用 4 个空格；C# 代码使用 Allman 大括号风格（`{` 换行）。
- 类、方法和公有成员使用 PascalCase；局部变量和参数使用 camelCase；私有字段使用 camelCase，且不加前导下划线。
- 每个文件仅保留一个 public 类型；不要修改 `.Designer.cs` 中的生成区域。
- UI 文案使用中文，并保持本地化表达一致。

## 测试规范
- 测试框架优先使用 MSTest 或 NUnit，测试项目建议放在 `tests/ControlEntradaSalida.Tests`。
- 测试命名采用 `ClassName_MethodName_ExpectedBehavior`，例如 `Common_Login_ReturnsUserId`。
- 设备和 SDK 调用必须 mock（如 `HCNetSDK`），避免单测依赖真实硬件。
- 集成测试示例放在 `tests/Integration/`，并通过配置开关保护。

## 文档目录指引
- `docs/门禁系统设计方案.md`：系统总体设计、阶段规划、模块边界和实施依据。
- `docs/gRPC接口清单.md`：gRPC 接口定义、调用约束和接口实现依据。
- `docs/底层数据库文档.md`：数据库表结构、字段说明、数据关系和落库规则。
- `docs/海康AIOP短衣短裤报警SDK布防回调说明.md`：海康 AIOP 报警布防、回调流程和 SDK 使用说明。
- `docs/stage1/task08.md`：阶段 1 验收清单，适用于确认服务骨架、配置、日志、数据库、后台任务、服务生命周期和健康检查是否完成。
- `docs/stage3/目标.md`：阶段 3 海康 SDK/ISAPI 网关实施目标，适用于确认底层网关接口、SDK/ISAPI 封装、mock 网关和测试验收范围。
- `docs/stage4/task01.md`：阶段 4 边界与任务总览，适用于确认设备生命周期和设备管理 gRPC 的实施范围。
- `docs/stage4/task02.md`：设备清单 JSON 加载与运行时注册方案，适用于实现 `Configuration/devices.json` 加载、设备运行时对象和索引注册。
- `docs/stage4/task03.md`：启动登录与登出清理方案，适用于实现登录任务、登出任务、停止清理和不回写设备清单。
- `docs/stage4/task04.md`：状态检测与重连策略，适用于实现周期检测、自动重连、手动重连和退避规则。
- `docs/stage4/task05.md`：布防编排与撤防策略，适用于实现登录后布防、断开前撤防和 AlarmHandle 管理。
- `docs/stage4/task06.md`：`GetDeviceStatus` 实施方案，适用于实现设备状态查询、筛选、刷新和鉴权。
- `docs/stage4/task07.md`：`AddDevice` 实施方案，适用于实现新增设备写入 JSON 清单、运行时注册和可选立即连接。
- `docs/stage4/task08.md`：`DeleteDevice`、`DisconnectDevice`、`ReconnectDevice` 实施方案，适用于实现设备删除、手动断开和手动重连。
- `docs/stage4/task09.md`：阶段 4 测试与验收方案，适用于验证设备生命周期、设备管理 gRPC、JSON 清单读写、遗留数据库兼容边界和 mock SDK 集成。
- `docs/stage4/task10.md`：阶段 1-4 联调测试方案，适用于使用 Docker 数据库验证服务启动、gRPC、设备生命周期基础闭环，并指导真实设备冒烟联调。
- `docs/stage5/task01.md`：阶段 5 边界与任务总览，适用于确认权限、人员、人脸同步的实施范围和顺序。
- `docs/stage5/task02.md`：权限同步 `SyncPermissions` 方案，适用于实现权限请求解析、在线设备下发和人员权限同步状态更新。
- `docs/stage5/task03.md`：人员与人脸同步 `SyncPersons` 方案，适用于实现人员基础信息、人脸图片校验、人员先于人脸的设备下发流程。
- `docs/stage5/task04.md`：删除人脸与删除人员方案，适用于实现 `DeleteFaces`、`DeletePersons` 的设备端删除和结果明细。
- `docs/stage5/task05.md`：设备端人脸查询 `GetFaces` 方案，适用于实现员工人脸查询、设备维度明细和原始响应边界。
- `docs/stage5/task06.md`：人脸采集 `CaptureFaceStream` 与采集状态方案，适用于实现流式采集、任务状态和失败单帧返回。
- `docs/stage5/task07.md`：阶段 5 测试与验收方案，适用于验证权限、人员、人脸同步的 gRPC 契约、mock 设备和补偿意图。
- `docs/stage6/task01.md`：阶段 6 边界与任务总览，适用于确认离线补偿机制的实施范围、状态语义和阶段完成标准。
- `docs/stage6/task02.md`：补偿状态写入与合并方案，适用于实现 `device_operation_retry_states` 的 upsert、冲突覆盖和补偿意图持久化。
- `docs/stage6/task03.md`：到期扫描与领取方案，适用于实现补偿后台扫描、in-flight 去重、设备状态过滤和异常恢复。
- `docs/stage6/task04.md`：重试任务投递与执行编排方案，适用于实现补偿状态到 `RetryDeviceOperation` 设备任务的转换和执行顺序。
- `docs/stage6/task05.md`：补偿结果回写方案，适用于实现成功清除 pending、失败退避、终态标记和权限完成标记。
- `docs/stage6/task06.md`：补偿清理、配置、日志和运维观测方案，适用于实现终态清理、配置校验、运行日志和现场排查。
- `docs/stage6/task07.md`：阶段 6 测试与验收方案，适用于验证离线补偿的数据库兼容、mock 设备执行、退避终态和契约兼容。
- `docs/stage7/task01.md`：阶段 7 边界与任务总览，适用于确认 ACS 人脸事件入库、离线事件上传补偿和 AIOP 联动边界。
- `docs/stage7/task02.md`：ACS 回调接收与事件队列方案，适用于实现 `COMM_ALARM_ACS` 原始事件复制、设备反查和后台队列投递。
- `docs/stage7/task03.md`：ACS 事件解析与标准模型方案，适用于实现员工、时间、流水、方向、认证结果和 raw payload 映射。
- `docs/stage7/task04.md`：抓拍图片保存与 raw payload 方案，适用于实现抓拍目录、文件命名、路径入库和失败降级。
- `docs/stage7/task05.md`：`attendance_gate_v2` 入库与防重复方案，适用于实现事件字段映射、唯一流水防重和重复事件处理。
- `docs/stage7/task06.md`：客户端布防与离线事件上传补偿方案，适用于实现 `byDeployType=0`、`byCurrentEvent=2` 识别和设备离线事件上传入库。
- `docs/stage7/task07.md`：队列缓冲、配置、测试与验收方案，适用于验证阶段 7 事件链路、离线事件上传补偿、排除设备和真实设备联调。
- `docs/stage8/task01.md`：阶段 8 边界与任务总览，适用于确认测试、发布、运维阶段的交付范围和完成标准。
- `docs/stage8/task02.md`：自动化测试矩阵与执行顺序方案，适用于规划单元、契约、mock 集成、数据库兼容、发布包和现场联调测试。
- `docs/stage8/task03.md`：构建、产物和发布包结构方案，适用于固定构建命令、发布目录、第三方 DLL 和配置模板要求。
- `docs/stage8/task04.md`：配置模板与运行前检查方案，适用于实现 `Configuration/appsettings.json` 模板和 `--validate-config` 检查项。
- `docs/stage8/task05.md`：Windows Service 安装、启动、停止和卸载方案，适用于现场服务部署、运行状态检查和服务生命周期验证。
- `docs/stage8/task06.md`：现场联调、排障、回滚与最终验收方案，适用于真实设备联调、问题定位、服务包回滚和验收资料整理。
- `docs/stage8/package-docs/部署说明.md`：发布包部署说明模板，适用于现场安装前确认发布包结构、服务脚本和回滚步骤。
- `docs/stage8/package-docs/运行前检查.md`：发布包运行前检查模板，适用于现场执行 `ControlDoor.exe --validate-config` 并核对配置、数据库、端口、目录和 SDK 依赖。
- `docs/stage8/package-docs/联调记录模板.md`：现场联调记录模板，适用于记录设备、权限、人脸、离线补偿、ACS 事件、抓拍、回滚和最终验收结果。
- `docs/stage9/task01.md`：阶段 9 边界与任务总览，适用于确认摄像头 AIOP 报警联动门禁常闭窗口的实施范围和关键语义。
- `docs/stage9/task02.md`：AIOP 回调识别与载荷解析方案，适用于实现 `0x4021 / COMM_UPLOAD_AIOP_VIDEO` 识别、来源匹配和 JSON/JPEG 摘要解析。
- `docs/stage9/task03.md`：配置映射与目标解析方案，适用于实现 `CameraAlarmDoorInterlock.Mappings` 的摄像头、门禁设备和门号映射。
- `docs/stage9/task04.md`：摄像头窗口与门目标活动集合方案，适用于实现窗口不续期、多摄像头共享门和最后窗口结束恢复规则。
- `docs/stage9/task05.md`：门禁常闭、恢复和停止清理方案，适用于实现 `NET_DVR_ControlGateway` 常闭/恢复、恢复重试和服务停止 best-effort 恢复。
- `docs/stage9/task06.md`：阶段 9 测试、联调与验收方案，适用于验证 AIOP 触发、窗口语义、多目标联动、恢复可靠性和默认关闭兼容性。
- `docs/stage10/task01.md`：设备清单 JSON 化与设备类型字段方案，适用于确认 `devices.json`、`Devices.FilePath`、`Devices.Items`、`types` 语义、门禁刷脸设备下发范围和 `lastUsedTime` 移除边界。

在 `docs/` 下每新增一个文档，必须在本节同步增加一条目录指引项。目录项应包含文档路径、核心用途和适用场景，确保后续查阅和实施时能快速定位。

## 设备 SDK 使用要求
与设备交互并调用 SDK 接口时，必须严格遵循 `设备网络SDK编程指南（明眸-以人为中心）/` 下的相关技术文档，确保接口调用准确、安全、可追踪。

- 接口调用：按文档要求使用正确接口、参数与返回值，避免使用未定义接口或错误参数类型。
- 错误处理：及时处理错误码，并按错误码进行重试、日志记录或用户提示。
- 性能优化：按文档建议控制调用频率和数据量，避免高频调用导致性能问题。

## 数据库与配置要求
- 数据库应部署在 Docker 中，并使用 `database/` 下现有脚本初始化和变更。
- 连接字符串名称必须为 `mysql`；新增配置项使用脱敏示例，不要提交密钥。
- 若涉及数据库变更，必须通过 `database/` 脚本协同，并同步更新相关文档。
- 依据 SDK 要求确认平台（x86/x64），并确保 DLL 放在 `bin/` 或引用目录中。



## 提交与 PR 规范
- 提交信息使用中文，编码使用 UTF-8。
- 提交格式：`阶段...，任务...，具体修改`。
- 每完成一个任务提交一次。
- 常规提交类型可参考 Conventional Commits（`feat:`、`fix:`、`refactor:`、`perf:`、`docs:`），范围（scope）可选。
- PR 需包含清晰说明、关联 issue、UI 截图、验证步骤，以及 DB/报表变更说明（`database/`、`*.rdlc`）。若改动 `App.config` 键，请同步文档。
