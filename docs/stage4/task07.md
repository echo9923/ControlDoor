# 阶段 4 / 任务 07：AddDevice

## 目标

实现 `/device.AccessControlService/AddDevice`，新增设备到 `Configuration/devices.json`，注册到运行时，并按 `connectNow` 可选立即连接。接口必须保持 JSON 契约完全兼容。

## 请求字段

| 字段 | 别名 | 必填 | 规则 |
| --- | --- | --- | --- |
| `deviceId` | `device_id` | 是 | 大于 0，在 JSON 清单内唯一。 |
| `deviceName` | `device_name` | 是 | 非空。 |
| `ipAddress` | `ip_address` | 是 | 非空，按原样 Trim 存储。 |
| `port` | 无 | 否 | 默认 `8000`，必须为 1-65535。 |
| `username` | 无 | 否 | 默认 `admin`。 |
| `password` | 无 | 是 | 非空。 |
| `description` | 无 | 否 | 可空。 |
| `enabled` | 无 | 否 | 默认 true。 |
| `connectNow` | 无 | 否 | 默认 false。 |
| `types` | `deviceTypes`、`device_types` | 是 | 至少一个合法值：`Acs`、`FaceCapture`、`Camera`。 |

## JSON 写入

```json
{
  "deviceId": 1001,
  "name": "1号楼大门门禁",
  "types": ["Acs"],
  "ipAddress": "192.168.1.64",
  "port": 8000,
  "username": "admin",
  "password": "******",
  "enabled": true,
  "remark": "东门主通道"
}
```

约束：

- 写入前检查 `deviceId` 和 `ipAddress` 冲突，冲突返回 `FAILED` 或 `INVALID_ARGUMENT`。
- 写入 `devices.json.tmp` 后替换目标文件，失败时回滚内存缓存。
- `BackupOnWrite=true` 时写入前生成 `devices.json.bak`。
- 若启用内联 `Devices.Items`，`AddDevice` 只更新当前进程的仓储缓存和运行时对象，不写回 `appsettings.json` 或 `devices.json`；该模式仅用于测试或临时小规模部署，正式部署应保持 `Items` 为空。

## 执行流程

| 步骤 | 动作 | 失败处理 |
| --- | --- | --- |
| 1 | 校验 API Key。 | 失败返回 `UNAUTHENTICATED`。 |
| 2 | 解析 JSON 和字段别名。 | 失败返回 `INVALID_ARGUMENT`。 |
| 3 | 校验 ID、IP、端口、密码。 | 失败返回 `INVALID_ARGUMENT`。 |
| 4 | 检查运行时和 JSON 清单是否已存在 ID/IP。 | 冲突返回 `FAILED`。 |
| 5 | 写入 `Configuration/devices.json`。 | 写回失败返回 `WRITE_FAILED`。 |
| 6 | 注册运行时对象。 | 注册失败时尝试删除刚插入记录或返回需要人工处理的错误。 |
| 7 | 如 `enabled && connectNow`，投递登录任务并等待结果。 | 连接失败不回滚 JSON 清单，响应 `connected=false`。 |
| 8 | 返回设备状态对象。 | 保持统一响应结构。 |

## 响应字段

| 字段 | 说明 |
| --- | --- |
| `device` | 新增后的设备状态对象。 |
| `connectNow` | 是否请求立即连接。 |
| `connected` | 是否连接成功。 |
| `connectionMessage` | 连接结果说明。 |

## 不做的事

| 不做内容 | 原因 |
| --- | --- |
| 不校验真实设备账号密码 | 只有 `connectNow=true` 才尝试登录。 |
| 不下发人员/权限 | 阶段 5 负责。 |
| 不写补偿 | 阶段 6 负责。 |
| 不写业务数据库 | 当前版本设备主数据只维护 JSON 清单。 |
| 不把内联 Items 当正式维护入口 | 内联模式不做文件写回，进程重启后不会保留 gRPC 新增设备。 |

## 测试

| 测试 | 验证 |
| --- | --- |
| 新增成功 | JSON 写入，运行时注册。 |
| ID 冲突 | 不插入，返回失败。 |
| IP 冲突 | 不插入，返回失败。 |
| connectNow 成功 | 登录并返回 connected=true。 |
| connectNow 失败 | 设备保留，返回 connected=false。 |
| 运行时注册失败 | 有明确补偿或人工处理日志。 |
| API Key | 鉴权符合契约。 |
