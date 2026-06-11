# 阶段 8 / 任务 03：构建、产物和发布包结构

## 目标

固定构建命令、产物目录和发布包结构，确保现场部署拿到的是可运行、可检查、可回滚的 `ControlDoor` Windows Service 包。

## 构建命令

按照仓库指引，实施阶段使用：

| 目的 | 命令 |
| --- | --- |
| 还原包 | `nuget restore ControlEntradaSalida.sln` |
| 推荐构建 | `dotnet build ControlEntradaSalida.sln --verbosity minimal` |
| Debug 构建 | `msbuild ControlEntradaSalida.sln /t:Build /p:Configuration=Debug` |
| Release 构建 | `msbuild ControlEntradaSalida.sln /p:Configuration=Release` |
| Debug 运行 | `bin/Debug/ControlEntradaSalida.exe` |

最终服务可执行文件名按方案固定为 `ControlDoor.exe`。如果工程文件仍沿用旧解决方案或项目名，发布阶段必须通过输出配置或打包步骤生成目标 exe 名。

## 构建前检查

| 检查项 | 标准 |
| --- | --- |
| NuGet 包 | `packages.config` 依赖已还原。 |
| 目标平台 | 与 Hikvision SDK DLL 位数一致。 |
| 第三方 DLL | Hikvision SDK、`SqlServerTypes` 已放在可加载位置。 |
| 数据库脚本 | `database/` 下初始化和专项脚本齐全。 |
| 配置模板 | `Configuration/appsettings.json` 模板可生成。 |

## 发布包目录

固定结构：

```text
门禁publish/
  ServicePackage/
    ControlDoor.exe
    ControlDoor.exe.config
    Configuration/
      appsettings.json
    sdk/
      Hikvision/
        HCNetSDK.dll
        ...
    logs/
    snapshots/
    docs/
      部署说明.md
      运行前检查.md
      联调记录模板.md
```

## 文件职责

| 文件或目录 | 职责 |
| --- | --- |
| `ControlDoor.exe` | Windows Service 可执行文件。 |
| `ControlDoor.exe.config` | .NET 框架级配置，不承载业务配置。 |
| `Configuration/appsettings.json` | 唯一业务配置入口。 |
| `sdk/Hikvision/` | Hikvision SDK DLL 和依赖文件。 |
| `logs/` | 服务日志目录，可为空。 |
| `snapshots/` | ACS 抓拍保存目录，可为空。 |
| `docs/` | 现场部署、检查、联调说明。 |

## 第三方 DLL 放置

| 类别 | 放置规则 |
| --- | --- |
| Hikvision SDK | 放在 exe 同级或 `sdk/Hikvision/`，以实现时加载策略为准。 |
| SDK 依赖 DLL | 与 `HCNetSDK.dll` 同目录，避免运行时缺依赖。 |
| `SqlServerTypes` | 放在 bin 或引用目录，确保运行时可加载。 |
| gRPC native | 如果项目依赖 native 扩展，随发布包放置到可加载目录。 |

发布包检查必须记录实际加载路径，避免同名 DLL 多份时排查困难。

## appsettings 模板要求

发布包必须包含可直接修改的 `Configuration/appsettings.json`。模板可以使用占位值，但不能缺失必要分组：

| 分组 | 必须包含 |
| --- | --- |
| `Service` | 端口、管理 API Key。 |
| `Database` | SQL Server 连接字符串和命令超时。 |
| `Logging` | 日志目录、保留天数、payload 日志开关。 |
| `DeviceRuntime` | worker 数、队列容量、状态检测。 |
| `HikvisionSdk` | 平台、DLL 路径、SDK 日志。 |
| `DeviceLifecycle` | 登录、重连、状态检测配置。 |
| `DeviceOperationRetry` | 补偿扫描和退避配置。 |
| `FaceEventLogging` | 事件入库、抓拍目录、历史补偿。 |
| `FaceEnrollment` | 人脸大小和采集任务保留。 |
| `CameraAlarmDoorInterlock` | 后续阶段兼容配置，可默认关闭。 |

## 发布包检查清单

| 检查 | 通过标准 |
| --- | --- |
| 文件存在 | exe、config、appsettings、SDK DLL、docs 都存在。 |
| 目录可写 | logs、snapshots 可写。 |
| 配置可读 | `--validate-config` 能读取 appsettings。 |
| DLL 可加载 | validate 能定位 SDK 主 DLL 和依赖。 |
| 平台一致 | 进程位数与 SDK 位数一致。 |
| 数据库可连 | validate 能连接测试或目标数据库。 |
| 端口可用 | validate 检查 gRPC 端口未占用。 |

## 不做的事

| 不做内容 | 原因 |
| --- | --- |
| 不把真实密钥写进仓库模板 | 模板使用占位值，现场自行填写。 |
| 不修改数据库表结构 | 发布阶段只检查和执行既有脚本。 |
| 不混入调试临时文件 | 发布包应只包含运行需要的文件和文档。 |
| 不强制改变服务名 | 服务名固定为 `ControlDoor`。 |

## 测试

| 测试 | 验证 |
| --- | --- |
| Release 构建 | 产物生成成功。 |
| 发布结构 | 目录和文件完整。 |
| 配置模板 | 必要分组齐全。 |
| DLL 缺失模拟 | validate 能明确报错。 |
| 平台不匹配模拟 | validate 能明确报错。 |
| 空日志目录 | 服务可自动创建或报清晰错误。 |
