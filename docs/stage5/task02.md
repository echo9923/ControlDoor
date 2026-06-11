# 阶段 5 / 任务 02：权限同步 SyncPermissions

## 目标

实现 `/permission.PermissionSyncService/SyncPermissions`，按员工编号把权限编号同步到在线门禁设备。接口支持数组、`items`、`records` 和单个对象请求结构。

## 请求字段

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `employee_id` | 是 | 员工编号/工号。 |
| `permission_code` | 是 | 权限编号，必须可解析为整数。 |

## 解析规则

| 输入结构 | 处理 |
| --- | --- |
| JSON 数组 | 每个元素解析为一条权限同步记录。 |
| `{ "items": [...] }` | 使用 `items`。 |
| `{ "records": [...] }` | 使用 `records`。 |
| 单个对象 | 解析为一条记录。 |

## 执行流程

| 步骤 | 动作 |
| --- | --- |
| 1 | 校验批量数量不超过 500。 |
| 2 | 校验员工编号和权限编号。 |
| 3 | 合并同一员工的重复记录，保留最后一次权限值。 |
| 4 | 查询或使用请求中的员工编号，不要求必须存在于 `system_users`。 |
| 5 | 获取启用且在线的设备列表。 |
| 6 | 对每台在线设备投递权限下发任务。 |
| 7 | 离线设备生成补偿意图。 |
| 8 | 设备成功后更新 `system_users` 既有门禁同步字段。 |
| 9 | 汇总 `total`、`updated`、`skipped`、`failed`、`queued`。 |

## 设备任务

| 字段 | 说明 |
| --- | --- |
| `OperationName` | `SyncPermission`。 |
| `Priority` | `Normal`。 |
| `CorrelationId` | 同一次 gRPC 请求共享。 |
| `RetryIntent` | 离线或可重试失败时产生。 |

设备端实现可使用阶段 3.5 ISAPI 或阶段 3.6 远程配置，具体 URL/命令号在编码时按 SDK 文档确认。

## system_users 更新

| 字段 | 行为 |
| --- | --- |
| `access_permission` | 可更新为请求权限值。 |
| `last_synced_level` | 全部目标在线设备成功后更新。 |
| `permission_updated_at` | 权限请求处理时间。 |
| `last_synced_at` | 设备同步成功时间。 |

说明：不修改账号体系字段语义，不新增字段。

## 补偿意图

| 场景 | 行为 |
| --- | --- |
| 设备离线 | 生成权限同步补偿意图。 |
| SDK 可重试错误 | 生成权限同步补偿意图。 |
| 参数错误 | 直接失败，不进入补偿。 |
| 设备不支持 | 直接失败或 skipped，不进入补偿。 |

补偿记录的持久化、合并、重试和终态由阶段 6 实现。

## 响应字段

| 字段 | 说明 |
| --- | --- |
| `total` | 员工总数。 |
| `updated` | 已更新数量。 |
| `skipped` | 跳过数量。 |
| `failed` | 失败数量。 |
| `queued` | 进入离线补偿数量。 |
| `queuedDetails` | 排队明细。 |

## 测试

| 测试 | 验证 |
| --- | --- |
| 多容器解析 | 数组、items、records、单对象均可解析。 |
| 批量超限 | 超过 500 返回 `BATCH_TOO_LARGE`。 |
| 权限值非法 | 返回 `INVALID_ARGUMENT`。 |
| 在线设备成功 | 返回 updated，设备任务调用正确。 |
| 离线设备 | queuedDetails 有设备和员工明细。 |
| 部分失败 | 返回 `PARTIAL_SUCCESS`。 |
