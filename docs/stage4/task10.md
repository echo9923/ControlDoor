# 阶段 1-4 联调测试方案

## 目标

本方案用于在进入阶段 5 前，验证阶段 1 到阶段 4 的基础闭环是否稳定：配置加载、日志、Docker SQL Server 数据库、健康检查、后台任务、设备固定执行通道、海康网关基础路径、设备生命周期和 `device.AccessControlService` 设备管理 gRPC 契约。

本轮联调分为两个层级：

1. **基础环境联调**：使用 Docker SQL Server 和 `Configuration/devices.json` 中的禁用占位设备，确认服务能启动、gRPC 能监听、数据库业务表可用、JSON 设备管理可写、日志可观察。此层级不连接真实设备。
2. **真实设备冒烟联调**：在基础环境通过后，由现场接入一台真实设备，再启用 JSON 设备记录，观察登录、布防、状态检测、断开、重连和停止清理日志。

## 边界

- 不测试权限、人员、人脸同步；这些属于阶段 5。
- 不测试离线补偿写入与重试；这些属于阶段 6。
- 不测试 ACS 事件入库与离线事件上传补偿；这些属于阶段 7。
- 不测试摄像头 AIOP 联动门禁常闭；这些属于阶段 9。
- 真实设备联调只验证生命周期和 SDK 基础连通，不做人员或人脸业务下发。

## 前置环境

| 项目 | 要求 |
| --- | --- |
| Docker | Docker Desktop Linux 引擎可用。 |
| SQL Server | 推荐镜像 `mcr.microsoft.com/mssql/server:2022-latest`。 |
| 数据库脚本 | 使用 `database/stage1_4_integration_seed.sql`。 |
| 本机端口 | SQL Server 映射到 `127.0.0.1,14333`；gRPC 使用 `5001`。 |
| 运行目录 | 使用构建输出目录或复制出的临时运行目录。 |
| SDK DLL | 基础环境联调可缺失，健康检查只记 Warning；真实设备联调前必须补齐。 |

## Docker 数据库准备

启动 SQL Server 容器：

```powershell
docker run --name controldoor-sqlserver-stage14 `
  -e "ACCEPT_EULA=Y" `
  -e "MSSQL_SA_PASSWORD=ControlDoor@12345" `
  -p 14333:1433 `
  -d mcr.microsoft.com/mssql/server:2022-latest
```

等待数据库可用：

```powershell
docker logs controldoor-sqlserver-stage14
```

日志中出现 SQL Server 已可接收连接后，复制并执行联调 seed：

```powershell
docker cp .\database\stage1_4_integration_seed.sql controldoor-sqlserver-stage14:/tmp/stage1_4_integration_seed.sql
docker exec controldoor-sqlserver-stage14 /bin/bash -lc "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'ControlDoor@12345' -C -i /tmp/stage1_4_integration_seed.sql"
```

如果容器内没有 `/opt/mssql-tools18/bin/sqlcmd`，检查旧路径：

```powershell
docker exec controldoor-sqlserver-stage14 /bin/bash -lc "ls /opt/mssql-tools/bin/sqlcmd /opt/mssql-tools18/bin/sqlcmd"
```

## 测试运行配置

构建后，运行目录的 `Configuration/appsettings.json` 使用以下测试连接字符串，并通过 `Devices.FilePath` 指向独立设备清单：

```json
{
  "Database": {
    "ConnectionString": "Server=127.0.0.1,14333;Database=ruoyi-vue-pro;User Id=admin_user;Password=123456;TrustServerCertificate=True;"
  },
  "Devices": {
    "FilePath": "Configuration\\devices.json",
    "BackupOnWrite": true,
    "Items": []
  }
}
```

基础联调阶段，在运行目录创建 `Configuration/devices.json`，让 `9001` 设备保持 `enabled=false`，确保服务启动时不会触发真实 SDK 登录：

```json
{
  "devices": [
    {
      "deviceId": 9001,
      "name": "阶段1-4真实设备联调",
      "types": [ "Acs", "FaceCapture" ],
      "ipAddress": "<设备IP或占位IP>",
      "port": 8000,
      "username": "admin",
      "password": "<设备密码或占位密码>",
      "enabled": false,
      "remark": "阶段1-4联调占位设备"
    }
  ]
}
```

真实设备接入前，将 `ipAddress`、`port`、`username`、`password` 改为现场提供的值，但仍先保持 `enabled=false`；确认配置无误后，再把 `enabled` 改为 `true` 并重启服务，或通过 `AddDevice connectNow=true` 新增并立即连接测试设备。

## 基础环境联调步骤

| 步骤 | 操作 | 预期 |
| --- | --- | --- |
| 1 | `dotnet build` 测试项目 | 构建成功，0 Error。 |
| 2 | 执行全量测试 runner | `Total` 全部通过。 |
| 3 | 启动 Docker SQL Server 并执行 seed | `ruoyi-vue-pro` 存在，业务表可读。 |
| 4 | 准备 `Configuration/devices.json` | 有 1 条 `enabled=false` 的占位设备 `9001`，且包含合法 `types`。 |
| 5 | 运行 `ControlDoor.exe --validate-config` | 配置、JSON 设备清单和数据库健康检查通过；SDK DLL 缺失最多为 Warning。 |
| 6 | 运行 `ControlDoor.exe --console` | Host 启动成功，gRPC 监听 `5001`。 |
| 7 | 调用 `GetDeviceStatus` | 返回 `9001`，`enabled=false`，`status=Disabled`。 |
| 8 | 调用 `AddDevice` 新增禁用设备 | 写入 `Configuration/devices.json`，运行时注册成功，不触发登录。 |
| 9 | 调用 `DeleteDevice` 删除测试新增设备 | JSON 记录和运行时清理成功。 |
| 10 | 停止服务 | 后台任务停止，日志有 Host 停止成功记录。 |

可使用受环境变量保护的自动化冒烟测试执行步骤 4-6：

```powershell
$env:CONTROLDOOR_STAGE14_INTEGRATION = "1"
$env:CONTROLDOOR_STAGE14_CONNECTION_STRING = "Server=127.0.0.1,14333;Database=ruoyi-vue-pro;User Id=admin_user;Password=123456;TrustServerCertificate=True;"
C:\Users\Administrator\AppData\Local\Temp\ControlDoorArtifacts\bin\ControlEntradaSalida.Tests\debug\ControlEntradaSalida.Tests.exe Stage14Integration
Remove-Item Env:\CONTROLDOOR_STAGE14_INTEGRATION
Remove-Item Env:\CONTROLDOOR_STAGE14_CONNECTION_STRING
```

该测试会创建临时运行目录、启动真实 `ControlDoorHost`、连接 Docker SQL Server、加载 JSON 设备清单、启动 gRPC server，并通过 gRPC 客户端调用 `GetDeviceStatus` 验证禁用占位设备 `9001` 可见；随后调用 `AddDevice` 新增一台禁用测试设备 `9010`，再调用 `DeleteDevice` 删除它，覆盖设备管理的 JSON 写路径。

## 真实设备冒烟步骤

| 步骤 | 操作 | 预期 |
| --- | --- | --- |
| 1 | 现场接入设备并提供 IP、端口、用户名、密码 | 本机可访问设备管理端口。 |
| 2 | 更新 `Configuration/devices.json` 的 `9001` 记录但保持 `enabled=false` | `GetDeviceStatus includeDisabled=true` 可看到新 IP。 |
| 3 | 确认 SDK DLL 已放入运行目录或 SDK 目录 | 健康检查不再报告海康 SDK DLL Warning。 |
| 4 | 将 `9001.enabled` 改为 `true` 并重启服务，或调用 `AddDevice connectNow=true` | 服务投递登录任务。 |
| 5 | 观察日志中的 `DeviceLogin` | 成功时记录 UserID，状态变为 `Online`。 |
| 6 | 观察 `DeviceArmAlarm` | 成功时记录 AlarmHandle 并建立反查索引。 |
| 7 | 调用 `GetDeviceStatus refresh=true` | 触发状态检测，在线设备保持 `Online`。 |
| 8 | 调用 `DisconnectDevice` | 先撤防再登出，状态变为 `Disconnected`。 |
| 9 | 调用 `ReconnectDevice force=true` | 清理旧连接并重新登录。 |
| 10 | 停止服务 | best-effort 撤防和登出，日志无未清理句柄。 |

## 需要现场提供的信息

真实设备接入时需要以下信息：

- 设备 IP。
- SDK 登录端口，默认 `8000`。
- 用户名，默认 `admin`。
- 密码。
- 本机到设备 IP 的网络连通性确认。
- 设备是否允许 SDK 登录和布防。

## 日志判定重点

| 日志关键词 | 判定 |
| --- | --- |
| `ControlDoor Host 启动成功` | 服务生命周期正常。 |
| `设备加载完成` | `Configuration/devices.json` 加载和运行时注册完成。 |
| `DeviceLogin` | 登录任务进入设备固定执行通道。 |
| `登录成功` | SDK 登录成功，UserID 已写入运行时。 |
| `DeviceArmAlarm` / `布防成功` | AlarmHandle 已注册。 |
| `DeviceHealthCheck` | 状态检测通过固定通道执行。 |
| `ManualDisconnect` | 手动断开执行撤防和登出。 |
| `ReconnectCleanup` / `Stage4Reconnect` | 重连路径执行。 |
| `ControlDoor Host 停止成功` | 服务停止清理完成。 |

## 通过标准

- Docker 数据库可初始化，当前业务必需表 `dbo.system_users`、`dbo.attendance_gate_v2`、`dbo.device_operation_retry_states` 可读。
- `Configuration/devices.json` 可解析，设备 `types` 合法，禁用占位设备可通过 `GetDeviceStatus includeDisabled=true` 查询。
- 基础环境中服务可通过 `--validate-config` 和 `--console` 启动。
- gRPC 设备管理 5 个方法保持 JSON 契约兼容。
- 基础环境不触发真实设备 SDK 登录。
- 接入真实设备后，登录、布防、刷新状态、断开、重连、停止清理均有明确日志和可解释结果。
- 数据库无结构变更；设备增删只改 `Configuration/devices.json`。

## 当前基础环境联调记录

| 时间 | 项目 | 结果 |
| --- | --- | --- |
| 2026-06-12 | Docker SQL Server 容器 `controldoor-sqlserver-stage14` | 已启动，端口 `14333 -> 1433`。 |
| 2026-06-12 | `database/stage1_4_integration_seed.sql` | 历史记录已废弃；当前脚本只准备业务数据库表，设备联调入口为 `Configuration/devices.json`。 |
| 2026-06-12 | `ControlDoor.exe --validate-config` | 通过；数据库在该模式下按现有实现为跳过真实连接的 Warning，SDK DLL/SqlServerTypes 缺失为 Warning。 |
| 2026-06-12 | `Stage14Integration` | 历史记录已废弃；当前对应测试应覆盖 JSON 设备清单写路径。 |
| 2026-06-12 | 全量测试 runner | 通过；集成测试默认跳过，`Total: 274, Failed: 0`。 |
| 2026-06-12 | 真实设备接入前联调库残留检查 | 历史记录已废弃；当前真实设备接入前只检查 JSON 清单中的禁用占位设备。 |

## 当前真实设备联调记录

| 时间 | 项目 | 结果 |
| --- | --- | --- |
| 2026-06-12 | 设备网络连通性 | 通过；现场设备 `169.254.66.109:8000` TCP 端口可达。 |
| 2026-06-12 | 运行目录 SDK DLL 检查 | 通过；运行目录已补齐 `HCNetSDK.dll`、`HCNetSDKCom`、`SqlServerTypes` 及海康 SDK 依赖 DLL。 |
| 2026-06-12 | 首次真实设备冒烟 | 未通过；测试进程工作目录不在 ControlDoor 运行目录，`DllImport("HCNetSDK.dll")` 未能命中运行目录 DLL，报 `0x8007007E`。 |
| 2026-06-12 | `Stage14Integration_RealDevice_LoginAndStatusSmoke` | 历史记录；测试在启动 Host 前调用 `SetDllDirectory(runDirectory)`，真实 `ControlDoorHost` 当时连接 Docker SQL Server 后加载设备 `9001`，`GetDeviceStatus refresh=true` 返回 `isConnected=true`、`status=Online`、`lastErrorCode=null`。阶段 10 后真实设备清单应由 `Configuration/devices.json` 提供。 |
| 2026-06-12 | 数据库回写检查 | 历史记录已废弃；当前设备登录不回写设备清单。 |
| 2026-06-12 | 日志检查 | 历史记录；当时日志包含 `UpdateDeviceLastUsedTime`，阶段 10 后后续真实设备联调直接观察 `设备登录成功。`、`设备布防成功。`、`设备撤防成功。` 和 `设备登出成功。`。 |
| 2026-06-12 | 全量测试 runner | 通过；集成测试默认跳过，`Total: 276, Failed: 0`。 |

真实设备冒烟测试默认跳过，只有显式设置环境变量才会运行：

```powershell
$env:CONTROLDOOR_STAGE14_REAL_DEVICE = "1"
$env:CONTROLDOOR_STAGE14_RUN_DIRECTORY = "C:\Users\Administrator\AppData\Local\Temp\ControlDoorArtifacts\bin\ControlDoor\debug"
$env:CONTROLDOOR_STAGE14_DEVICE_ID = "9001"
$env:CONTROLDOOR_STAGE14_REAL_DEVICE_TIMEOUT_SECONDS = "60"
C:\Users\Administrator\AppData\Local\Temp\ControlDoorArtifacts\bin\ControlEntradaSalida.Tests\debug\ControlEntradaSalida.Tests.exe Stage14Integration_RealDevice_LoginAndStatusSmoke
Remove-Item Env:\CONTROLDOOR_STAGE14_REAL_DEVICE
Remove-Item Env:\CONTROLDOOR_STAGE14_RUN_DIRECTORY
Remove-Item Env:\CONTROLDOOR_STAGE14_DEVICE_ID
Remove-Item Env:\CONTROLDOOR_STAGE14_REAL_DEVICE_TIMEOUT_SECONDS
```

后续如果需要继续现场联调，应先按需在 `Configuration/devices.json` 中启用 `9001`，测试结束后再切回 `enabled=false`。
