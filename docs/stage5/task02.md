# 阶段 5 / 任务 02：权限同步 SyncPermissions

## 目标

实现 `/permission.PermissionSyncService/SyncPermissions`，按员工编号把门禁权限等级同步到门禁设备。`permission_code` 表示员工权限等级，不是设备端权限码；服务端按设备描述识别办公/生产区域，决定该员工在每台门禁设备上启用或禁用。接口支持数组、`items`、`records` 和单个对象请求结构。

## 请求字段

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `employee_id` | 是 | 员工编号/工号。 |
| `permission_code` | 是 | 门禁权限等级，必须可解析为整数。`0` 全部禁用，`1` 仅办公区域启用，`2` 办公/生产/未分类均启用，其他值全部禁用。 |
| `name` | 否 | 员工姓名，别名 `full_name`、`fullName`、`name_alias`；为空时设备端姓名使用员工编号。 |

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
| 5 | 获取声明为 `Acs` 的门禁设备，并按设备 `description` / JSON `remark` 识别区域。 |
| 6 | 对每台在线设备计算 `shouldEnable` 并投递权限下发任务。 |
| 7 | 离线设备生成补偿意图。 |
| 8 | 设备成功后更新 `system_users` 既有门禁同步字段。 |
| 9 | 汇总 `total`、`updated`、`skipped`、`failed`、`queued`。 |

## 区域与权限等级

| 设备描述 | 区域 |
| --- | --- |
| 包含 `生产` | `Production` |
| 包含 `办公` | `Office` |
| 为空或不匹配 | `Other` |

| 权限等级 | 办公区域 | 生产区域 | 未分类 Other |
| --- | --- | --- | --- |
| `0` | 禁用 | 禁用 | 禁用 |
| `1` | 启用 | 禁用 | 禁用 |
| `2` | 启用 | 启用 | 启用 |
| 其他 | 禁用 | 禁用 | 禁用 |

## 设备任务

| 字段 | 说明 |
| --- | --- |
| `OperationName` | `SyncPermission`。 |
| `Priority` | `Normal`。 |
| `CorrelationId` | 同一次 gRPC 请求共享。 |
| `RetryIntent` | 离线或可重试失败时产生。 |

设备端通过阶段 3 网关的 `UpsertPersonAsync` 调用 `PUT /ISAPI/AccessControl/UserInfo/SetUp?format=json`。启用时写入 `Valid.enable=true`、`doorRight="1"`、`RightPlan`；禁用时写入 `Valid.enable=false`、`doorRight=""`、`RightPlan=[]`。不再把 `permission_code` 当作设备端 `UserRight/SetUp` 权限码直接下发。

## system_users 更新

| 字段 | 行为 |
| --- | --- |
| `access_permission` | 可更新为请求权限等级。 |
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
| 区域矩阵 | 等级 1 仅办公区启用，等级 2 全区域启用，等级 0/未知等级全部禁用。 |
| 离线设备 | queuedDetails 有设备和员工明细。 |
| 部分失败 | 返回 `PARTIAL_SUCCESS`。 |
