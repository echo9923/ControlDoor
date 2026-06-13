# 阶段 8 / 任务 05：Windows Service 安装、启动、停止和卸载

## 目标

固定 `ControlDoor` Windows Service 的现场安装、启动、停止、卸载和运行状态检查步骤。正式运行形态为 Windows Service，Debug 阶段允许直接运行 exe。

## 服务身份

| 项目 | 值 |
| --- | --- |
| 服务名 | `ControlDoor`。 |
| 显示名 | `ControlDoor`。 |
| 可执行文件 | `ControlDoor.exe`。 |
| 工作目录 | exe 所在目录。 |
| 配置路径 | `{工作目录}\Configuration\appsettings.json`。 |
| 日志目录 | 默认 `{工作目录}\logs`。 |
| 抓拍目录 | 默认 `{工作目录}\snapshots`。 |

## 安装前检查

| 检查 | 标准 |
| --- | --- |
| 发布包路径 | 路径稳定，不放在临时目录。 |
| 配置文件 | 已按现场数据库、设备和端口修改。 |
| validate | `ControlDoor.exe --validate-config` 通过。 |
| 服务不存在 | 若已存在，先确认是旧版本还是当前版本。 |
| 权限 | 安装账户有创建 Windows Service 权限。 |
| 端口 | gRPC 端口未被占用。 |

## 安装方式

具体安装命令取决于实现采用 `ProjectInstaller`、`installutil` 还是 `sc.exe`。阶段 8 固定要求：

| 要求 | 说明 |
| --- | --- |
| 非交互 | 安装脚本应可重复执行并输出中文结果。 |
| 路径带引号 | 服务路径必须支持中文目录和空格。 |
| 服务名固定 | 不允许安装成其他服务名。 |
| 启动类型 | 默认自动启动或按现场要求手动。 |

推荐发布包提供脚本：

```text
tools\service\install-service.ps1
tools\service\uninstall-service.ps1
tools\service\start-service.ps1
tools\service\stop-service.ps1
```

当前仓库已提供上述非交互 PowerShell 脚本。安装和启动脚本默认先执行 `ControlDoor.exe --validate-config`；服务名、显示名固定为 `ControlDoor`；卸载脚本只移除 Windows Service，保留配置、日志、抓拍和数据库数据。

## 启动流程

| 步骤 | 动作 |
| --- | --- |
| 1 | 启动 Windows Service。 |
| 2 | 查看服务状态是否 Running。 |
| 3 | 查看最新 `logs/ControlDoor-yyyyMMdd.log`。 |
| 4 | 确认配置加载成功。 |
| 5 | 确认数据库连接成功。 |
| 6 | 确认 SDK 初始化和设备加载日志。 |
| 7 | 确认 gRPC 端口监听。 |

## 停止流程

| 步骤 | 动作 |
| --- | --- |
| 1 | 停止接收新 gRPC 请求。 |
| 2 | 停止补偿、人脸事件等后台扫描任务。 |
| 3 | 停止设备状态检测和自动重连。 |
| 4 | 对已布防设备撤防。 |
| 5 | 对已登录设备登出。 |
| 6 | 等待设备 worker 退出。 |
| 7 | SDK Cleanup。 |
| 8 | 释放数据库和日志资源。 |

停止超时必须记录日志，并继续让进程退出，避免服务控制管理器长时间卡住。

## 卸载流程

| 步骤 | 动作 |
| --- | --- |
| 1 | 停止服务。 |
| 2 | 确认进程退出。 |
| 3 | 卸载 Windows Service。 |
| 4 | 保留配置、日志、抓拍和数据库。 |
| 5 | 如需清理文件，由人工确认后执行。 |

卸载服务不删除数据库表、不删除业务数据、不删除抓拍图片。

## Debug 控制台运行

Debug 阶段允许直接运行：

```text
bin/Debug/ControlEntradaSalida.exe
```

或最终产物：

```text
ControlDoor.exe --console
```

控制台模式必须与 Windows Service 共用同一套 `ControlDoorHost`，不能有另一套初始化逻辑。

## 服务恢复

推荐配置 Windows Service Recovery：

| 失败次数 | 动作 |
| --- | --- |
| 第一次 | 重启服务。 |
| 第二次 | 重启服务。 |
| 后续 | 按现场策略重启或报警。 |

恢复策略不代替程序内部错误处理；服务仍需记录崩溃前错误。

## 测试

| 测试 | 验证 |
| --- | --- |
| 安装 | 服务名和路径正确。 |
| 启动 | 服务进入 Running，日志有启动摘要。 |
| 停止 | 撤防、登出、worker 停止、SDK cleanup 日志完整。 |
| 卸载 | 服务被移除，数据和日志保留。 |
| 路径带空格 | 安装和启动正常。 |
| validate 失败 | 安装前检查能阻止启动。 |
