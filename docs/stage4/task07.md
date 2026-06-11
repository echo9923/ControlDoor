# 阶段 4 / 任务 07：AddDevice

## 目标

实现 `/device.AccessControlService/AddDevice`，新增设备到 `dbo.devices`，注册到运行时，并按 `connectNow` 可选立即连接。接口必须保持 JSON 契约完全兼容。

## 请求字段

| 字段 | 别名 | 必填 | 规则 |
| --- | --- | --- | --- |
| `deviceId` | `device_id` | 是 | 大于 0，主键非自增。 |
| `deviceName` | `device_name` | 是 | 非空。 |
| `ipAddress` | `ip_address` | 是 | 非空，按原样 Trim 存储。 |
| `port` | 无 | 否 | 默认 `8000`，必须为 1-65535。 |
| `username` | 无 | 否 | 默认 `admin`。 |
| `password` | 无 | 是 | 非空。 |
| `description` | 无 | 否 | 可空。 |
| `enabled` | 无 | 否 | 默认 true。 |
| `connectNow` | 无 | 否 | 默认 false。 |

## 数据库写入

```sql
INSERT INTO dbo.devices
(
    device_id,
    device_name,
    description,
    ip_address,
    port,
    username,
    password,
    status
)
VALUES
(
    @deviceId,
    @deviceName,
    @description,
    @ipAddress,
    @port,
    @username,
    @password,
    @status
);
```

约束：

- 不新增字段。
- 不修改索引、触发器、默认值。
- 插入前检查 `device_id` 和 `ip_address` 冲突，冲突返回 `FAILED` 或 `INVALID_ARGUMENT`。

## 执行流程

| 步骤 | 动作 | 失败处理 |
| --- | --- | --- |
| 1 | 校验 API Key。 | 失败返回 `UNAUTHENTICATED`。 |
| 2 | 解析 JSON 和字段别名。 | 失败返回 `INVALID_ARGUMENT`。 |
| 3 | 校验 ID、IP、端口、密码。 | 失败返回 `INVALID_ARGUMENT`。 |
| 4 | 检查运行时和数据库是否已存在 ID/IP。 | 冲突返回 `FAILED`。 |
| 5 | 插入 `dbo.devices`。 | DB 失败返回 `DB_ERROR`。 |
| 6 | 注册运行时对象。 | 注册失败时尝试删除刚插入记录或返回需要人工处理的错误。 |
| 7 | 如 `enabled && connectNow`，投递登录任务并等待结果。 | 连接失败不回滚数据库，响应 `connected=false`。 |
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
| 不新增数据库表 | 阶段 4 不需要新表。 |

## 测试

| 测试 | 验证 |
| --- | --- |
| 新增成功 | 数据库插入，运行时注册。 |
| ID 冲突 | 不插入，返回失败。 |
| IP 冲突 | 不插入，返回失败。 |
| connectNow 成功 | 登录并返回 connected=true。 |
| connectNow 失败 | 设备保留，返回 connected=false。 |
| 运行时注册失败 | 有明确补偿或人工处理日志。 |
| API Key | 鉴权符合契约。 |
