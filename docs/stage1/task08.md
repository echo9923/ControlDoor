# 阶段 1 验收清单

## 审核结论

阶段 1 的任务边界清晰，任务 1.1 至任务 1.8 均已在 `docs/门禁系统设计方案.md` 中定义目标、完成物、验收标准和不得越界项。本次实现按该文档执行，阶段 1 只落服务骨架与基础设施，不实现真实设备登录、不注册业务 gRPC、不写业务数据、不修改数据库结构、不实现 AIOP 联动。

## 完成范围

| 任务 | 完成内容 |
| --- | --- |
| 1.1 | 新增 `ControlEntradaSalida.sln`、`ControlDoor.exe` 项目、`Program.cs`、`ControlDoorService`、`ProjectInstaller` 和统一 `ControlDoorHost`。 |
| 1.2 | 新增 `Configuration/appsettings.json` 模板、配置模型、固定路径加载、默认值、必填校验和 warning 收集。 |
| 1.3 | 新增文件日志、日志保留清理、链路 ID、payload 摘要/完整记录开关和 SDK trace 入口。 |
| 1.4 | 新增 SQL Server 只读访问封装、命令超时、错误记录、健康查询和只读 SQL 保护。 |
| 1.5 | 新增后台任务宿主、任务注册、启动/停止顺序、取消、异常隔离、停止超时和延迟调度器。 |
| 1.6 | 新增服务生命周期控制器，统一 Debug 控制台、Windows Service、启动失败、停止、关机停止和超时处理。 |
| 1.7 | 新增基础健康检查，覆盖配置、目录、数据库、端口、海康 SDK DLL 和 SqlServerTypes DLL。 |
| 1.8 | 新增阶段 1 验收测试和本清单，汇总自动化验证结果。 |

## 自动化测试

测试入口：

```powershell
dotnet build .\tests\ControlEntradaSalida.Tests\ControlEntradaSalida.Tests.csproj --verbosity minimal --artifacts-path C:\Users\Administrator\AppData\Local\Temp\ControlDoorArtifacts
& C:\Users\Administrator\AppData\Local\Temp\ControlDoorArtifacts\bin\ControlEntradaSalida.Tests\debug\ControlEntradaSalida.Tests.exe
```

当前覆盖：

| 测试类别 | 覆盖内容 |
| --- | --- |
| 启动入口 | 命令行模式解析、服务身份、Host 停止幂等。 |
| 配置加载 | 固定 JSON 路径、缺文件、错误 JSON、必填连接字符串、非法可选值回退。 |
| 日志基础 | 日志目录、生命周期字段、保留清理、链路 ID、payload 开关和字段过滤。 |
| 数据库基础 | 只读 SQL 白名单、核心表只读检查、后续表 warning、重试配置默认值。 |
| 后台任务 | 启动/停止顺序、取消令牌、非关键异常隔离、关键失败阻断、停止超时、延迟调度。 |
| 生命周期 | 启动成功/失败/超时、停止成功/异常、关机停止、pending 上报抽象。 |
| 健康检查 | OK/Warning/Failed 汇总、目录可写、端口占用、DLL warning、数据库必需项失败。 |
| 阶段边界 | 阶段 1 不注册业务 gRPC 方法、不执行结构变更 SQL、输出目标为 `ControlDoor.exe`。 |

## 手动检查建议

| 检查项 | 命令或操作 |
| --- | --- |
| 验证模式 | `ControlDoor.exe --validate-config`。 |
| 版本模式 | `ControlDoor.exe --version`。 |
| 缺配置文件 | 临时移走运行目录 `Configuration/appsettings.json`，确认失败信息包含缺失路径。 |
| 错误 JSON | 写入非法 JSON，确认失败信息包含 JSON 解析失败。 |
| 缺连接字符串 | 清空 `Database.ConnectionString`，确认配置加载失败。 |
| 端口占用 | 占用 `Service.GrpcListenPort`，确认健康检查失败。 |
| 控制台停止 | `ControlDoor.exe --console` 后按 Ctrl+C 或回车，确认停止流程和日志。 |

## 阶段边界

阶段 1 已保持以下边界：

| 禁止项 | 当前结果 |
| --- | --- |
| 不登录真实设备 | 未调用 `HCNetSDK` 登录或设备业务接口。 |
| 不注册外部业务 gRPC | 未引入 gRPC server 或业务方法注册。 |
| 不写业务数据 | 数据库封装和健康检查只允许 `SELECT`。 |
| 不修改数据库结构 | 只读保护拒绝 `ALTER`、`CREATE`、`DROP` 等 SQL。 |
| 不生成最终发布包 | 仅支持 Debug/Release 构建输出，正式发布包留给阶段 8。 |
| 不实现 AIOP 联动 | 仅保留默认关闭配置项。 |

## 验收结论

阶段 1 已具备可构建、可测试、可验证的基础运行骨架。后续阶段可以在当前 Host、配置、日志、数据库封装、后台任务宿主和健康检查基础上接入设备运行时、SDK 网关、设备生命周期、权限同步、离线补偿和事件入库能力。
