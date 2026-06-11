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
- `docs/stage4/task01.md`：阶段 4 边界与任务总览，适用于确认设备生命周期和设备管理 gRPC 的实施范围。
- `docs/stage4/task02.md`：设备加载与运行时注册方案，适用于实现 `dbo.devices` 加载、设备运行时对象和索引注册。
- `docs/stage4/task03.md`：启动登录与登出清理方案，适用于实现登录任务、登出任务、停止清理和 `last_used_time` 更新。
- `docs/stage4/task04.md`：状态检测与重连策略，适用于实现周期检测、自动重连、手动重连和退避规则。
- `docs/stage4/task05.md`：布防编排与撤防策略，适用于实现登录后布防、断开前撤防和 AlarmHandle 管理。
- `docs/stage4/task06.md`：`GetDeviceStatus` 实施方案，适用于实现设备状态查询、筛选、刷新和鉴权。
- `docs/stage4/task07.md`：`AddDevice` 实施方案，适用于实现新增设备入库、运行时注册和可选立即连接。
- `docs/stage4/task08.md`：`DeleteDevice`、`DisconnectDevice`、`ReconnectDevice` 实施方案，适用于实现设备删除、手动断开和手动重连。
- `docs/stage4/task09.md`：阶段 4 测试与验收方案，适用于验证设备生命周期、设备管理 gRPC、数据库兼容和 mock SDK 集成。
- `docs/stage5/task01.md`：阶段 5 边界与任务总览，适用于确认权限、人员、人脸同步的实施范围和顺序。
- `docs/stage5/task02.md`：权限同步 `SyncPermissions` 方案，适用于实现权限请求解析、在线设备下发和人员权限同步状态更新。
- `docs/stage5/task03.md`：人员与人脸同步 `SyncPersons` 方案，适用于实现人员基础信息、人脸图片校验、人员先于人脸的设备下发流程。
- `docs/stage5/task04.md`：删除人脸与删除人员方案，适用于实现 `DeleteFaces`、`DeletePersons` 的设备端删除和结果明细。
- `docs/stage5/task05.md`：设备端人脸查询 `GetFaces` 方案，适用于实现员工人脸查询、设备维度明细和原始响应边界。
- `docs/stage5/task06.md`：人脸采集 `CaptureFaceStream` 与采集状态方案，适用于实现流式采集、任务状态和失败单帧返回。
- `docs/stage5/task07.md`：阶段 5 测试与验收方案，适用于验证权限、人员、人脸同步的 gRPC 契约、mock 设备和补偿意图。

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
