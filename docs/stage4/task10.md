# 阶段 1-4 联调测试方案

## 目标

本方案用于在进入阶段 5 前，验证阶段 1 到阶段 4 的基础闭环是否稳定：配置加载、日志、Docker SQL Server 数据库、健康检查、后台任务、设备固定执行通道、海康网关基础路径、设备生命周期和 `device.AccessControlService` 设备管理 gRPC 契约。

本轮联调分为两个层级：

1. **基础环境联调**：使用 Docker SQL Server 和禁用占位设备，确认服务能启动、gRPC 能监听、数据库读写路径可用、日志可观察。此层级不连接真实设备。
2. **真实设备冒烟联调**：在基础环境通过后，由现场接入一台真实设备，再启用该设备记录，观察登录、布防、状态检测、断开、重连和停止清理日志。

## 边界

- 不测试权限、人员、人脸同步；这些属于阶段 5。
- 不测试离线补偿写入与重试；这些属于阶段 6。
- 不测试 ACS 事件入库与历史补偿；这些属于阶段 7。
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

构建后，运行目录的 `Configuration/appsettings.json` 使用以下测试连接字符串：

```json
{
  "Database": {
    "ConnectionString": "Server=127.0.0.1,14333;Database=ruoyi-vue-pro;User Id=door_user;Password=change_me;TrustServerCertificate=True;"
  }
}
```

基础联调阶段，`dbo.devices` 中的 `9001` 设备保持 `status = 0`，确保服务启动时不会触发真实 SDK 登录。

真实设备接入前，将设备记录更新为现场提供的 IP、端口、用户名和密码，但仍先保持 `status = 0`：

```sql
UPDATE dbo.devices
SET device_name = N'阶段1-4真实设备联调',
    ip_address = N'<设备IP>',
    port = N'8000',
    username = N'admin',
    [password] = N'<设备密码>',
    status = 0,
    updated_at = SYSDATETIME()
WHERE device_id = 9001;
```

确认配置无误后，再启用：

```sql
UPDATE dbo.devices
SET status = 1,
    updated_at = SYSDATETIME()
WHERE device_id = 9001;
```

## 基础环境联调步骤

| 步骤 | 操作 | 预期 |
| --- | --- | --- |
| 1 | `dotnet build` 测试项目 | 构建成功，0 Error。 |
| 2 | 执行全量测试 runner | `Total` 全部通过。 |
| 3 | 启动 Docker SQL Server 并执行 seed | `ruoyi-vue-pro` 存在，`devices` 中有 1 条禁用占位设备。 |
| 4 | 运行 `ControlDoor.exe --validate-config` | 配置和数据库健康检查通过；SDK DLL 缺失最多为 Warning。 |
| 5 | 运行 `ControlDoor.exe --console` | Host 启动成功，gRPC 监听 `5001`。 |
| 6 | 调用 `GetDeviceStatus` | 返回 `9001`，`enabled=false`，`status=Disabled` 或数据库禁用态。 |
| 7 | 调用 `AddDevice` 新增禁用设备 | 数据库插入成功，运行时注册成功，不触发登录。 |
| 8 | 调用 `DeleteDevice` 删除测试新增设备 | 数据库和运行时清理成功。 |
| 9 | 停止服务 | 后台任务停止，日志有 Host 停止成功记录。 |

可使用受环境变量保护的自动化冒烟测试执行步骤 4-6：

```powershell
$env:CONTROLDOOR_STAGE14_INTEGRATION = "1"
$env:CONTROLDOOR_STAGE14_CONNECTION_STRING = "Server=127.0.0.1,14333;Database=ruoyi-vue-pro;User Id=door_user;Password=change_me;TrustServerCertificate=True;"
C:\Users\Administrator\AppData\Local\Temp\ControlDoorArtifacts\bin\ControlEntradaSalida.Tests\debug\ControlEntradaSalida.Tests.exe Stage14Integration
Remove-Item Env:\CONTROLDOOR_STAGE14_INTEGRATION
Remove-Item Env:\CONTROLDOOR_STAGE14_CONNECTION_STRING
```

该测试会创建临时运行目录、启动真实 `ControlDoorHost`、连接 Docker SQL Server、启动 gRPC server，并通过 gRPC 客户端调用 `GetDeviceStatus` 验证禁用占位设备 `9001` 可见；随后调用 `AddDevice` 新增一台禁用测试设备 `9010`，再调用 `DeleteDevice` 删除它，覆盖设备管理的数据库写路径。

## 真实设备冒烟步骤

| 步骤 | 操作 | 预期 |
| --- | --- | --- |
| 1 | 现场接入设备并提供 IP、端口、用户名、密码 | 本机可访问设备管理端口。 |
| 2 | 更新 `dbo.devices` 的 `9001` 记录但保持禁用 | `GetDeviceStatus includeDisabled=true` 可看到新 IP。 |
| 3 | 确认 SDK DLL 已放入运行目录或 SDK 目录 | 健康检查不再报告海康 SDK DLL Warning。 |
| 4 | 启用 `9001` 并重启服务，或调用 `AddDevice connectNow=true` | 服务投递登录任务。 |
| 5 | 观察日志中的 `DeviceLogin` | 成功时记录 UserID、状态变为 `Online`，并更新 `last_used_time`。 |
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
| `设备加载完成` | `dbo.devices` 加载和运行时注册完成。 |
| `DeviceLogin` | 登录任务进入设备固定执行通道。 |
| `登录成功` | SDK 登录成功，UserID 已写入运行时。 |
| `DeviceArmAlarm` / `布防成功` | AlarmHandle 已注册。 |
| `DeviceHealthCheck` | 状态检测通过固定通道执行。 |
| `ManualDisconnect` | 手动断开执行撤防和登出。 |
| `ReconnectCleanup` / `Stage4Reconnect` | 重连路径执行。 |
| `ControlDoor Host 停止成功` | 服务停止清理完成。 |

## 通过标准

- Docker 数据库可初始化，必需表 `dbo.devices`、`dbo.system_users` 可读。
- 基础环境中服务可通过 `--validate-config` 和 `--console` 启动。
- gRPC 设备管理 5 个方法保持 JSON 契约兼容。
- 基础环境不触发真实设备 SDK 登录。
- 接入真实设备后，登录、布防、刷新状态、断开、重连、停止清理均有明确日志和可解释结果。
- 数据库无结构变更，仅使用既有表和字段。

## 当前基础环境联调记录

| 时间 | 项目 | 结果 |
| --- | --- | --- |
| 2026-06-12 | Docker SQL Server 容器 `controldoor-sqlserver-stage14` | 已启动，端口 `14333 -> 1433`。 |
| 2026-06-12 | `database/stage1_4_integration_seed.sql` | 执行成功，`ruoyi-vue-pro` 中有 1 台禁用占位设备，启用设备数为 0。 |
| 2026-06-12 | `ControlDoor.exe --validate-config` | 通过；数据库在该模式下按现有实现为跳过真实连接的 Warning，SDK DLL/SqlServerTypes 缺失为 Warning。 |
| 2026-06-12 | `Stage14Integration` | 通过；真实 `ControlDoorHost` 启动、连接 Docker SQL Server、启动 gRPC server、`GetDeviceStatus` 返回禁用设备 `9001`、`AddDevice` 新增禁用设备 `9010`、`DeleteDevice` 删除 `9010`。 |
| 2026-06-12 | 全量测试 runner | 通过；集成测试默认跳过，`Total: 274, Failed: 0`。 |
| 2026-06-12 | 联调库残留检查 | 通过；`dbo.devices` 仅剩禁用占位设备 `9001`，`last_used_time` 仍为 `NULL`，未触发真实 SDK 登录。 |

下一步等待现场接入真实设备。接入后先提供设备 IP、端口、用户名和密码，保持 `9001` 禁用状态更新连接信息；确认 SDK DLL 放置和网络连通后，再启用设备并观察登录、布防和状态检测日志。
