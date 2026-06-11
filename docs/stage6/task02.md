# 阶段 6 / 任务 02：补偿状态写入与合并

## 目标

实现 `DeviceOperationRetryStore.UpsertIntent`，把阶段 5 产生的补偿意图写入现有 `dbo.device_operation_retry_states` 表，并在同一设备同一员工已有状态行时按最新业务意图合并。

## 输入模型

建议内部使用 `DeviceOperationRetryIntent`：

| 字段 | 说明 |
| --- | --- |
| `DeviceId` | 目标设备 ID，对应 `device_id`。 |
| `EmployeeId` | 员工编号，对应 `employee_id`。 |
| `Operation` | `Permission`、`Person`、`Face`、`DeletePerson`、`DeleteFace`。 |
| `PermissionLevel` | 权限同步目标值。 |
| `PersonPayloadJson` | 人员下发载荷 JSON。 |
| `FacePayloadJson` | 人脸下发载荷 JSON。 |
| `ReasonCode` | 产生补偿的原因，例如 `DEVICE_OFFLINE`、`SDK_RETRYABLE_ERROR`。 |
| `ReasonMessage` | 最近错误或排队说明。 |
| `RequestId` | 来源请求 ID，用于日志关联。 |
| `CreatedAt` | 业务意图产生时间。 |

## 写入事务

补偿写入必须在一个数据库事务内完成，避免并发请求下丢失 pending 标记。

| 步骤 | 动作 |
| --- | --- |
| 1 | 校验 `DeviceId > 0`、`EmployeeId` 非空。 |
| 2 | 以 `UPDLOCK, HOLDLOCK` 查询 `device_id + employee_id` 状态行。 |
| 3 | 不存在则插入新行。 |
| 4 | 存在则按合并规则更新。 |
| 5 | 设置 `next_retry_at` 为当前时间或当前时间加初始延迟。 |
| 6 | 新业务意图进入已终态行时清空 `exhausted_at`。 |
| 7 | 写入 `last_error`、`updated_at`。 |
| 8 | 提交事务并返回状态行摘要。 |

## 合并规则

| 意图 | 字段变化 |
| --- | --- |
| 权限同步 | `permission_level = 新值`，`permission_pending = 1`，`permission_sync_completion_blocked = 1`。 |
| 人员下发 | `person_payload = 新 payload`，`person_pending = 1`，`delete_person_pending = 0`。 |
| 人脸下发 | `face_payload = 新 payload`，`face_pending = 1`，`delete_face_pending = 0`。 |
| 删除人脸 | `delete_face_pending = 1`，`face_pending = 0`，`face_payload = NULL`。 |
| 删除人员 | `delete_person_pending = 1`，`person_pending = 0`，`face_pending = 0`，`delete_face_pending = 0`，`person_payload = NULL`，`face_payload = NULL`。 |

删除人员表示设备端该员工不应继续存在，因此会清除人员、人脸相关待下发动作。权限 pending 默认一并清除，避免删除人员后又单独补偿权限；如果后续业务重新下发人员或权限，会生成新的补偿意图。

## attempt_count 处理

| 场景 | 规则 |
| --- | --- |
| 插入新状态行 | `attempt_count = 0`。 |
| 已存在未终态行收到同类新意图 | 保留 `attempt_count`，但更新 payload 和错误原因。 |
| 已存在未终态行收到冲突新意图 | 重置 `attempt_count = 0`，从新业务意图重新计算。 |
| 已终态行收到任何新意图 | `attempt_count = 0`，`exhausted_at = NULL`。 |

## next_retry_at 处理

| 场景 | 规则 |
| --- | --- |
| 设备离线产生补偿 | `next_retry_at = now + InitialRetryDelaySeconds`，避免刚离线立即扫到。 |
| 在线设备可重试失败 | `next_retry_at = now + InitialRetryDelaySeconds`。 |
| 手动重连成功后触发立即补偿 | 可由阶段 4 生命周期调用 Store 将该设备到期时间提前到 `now`。 |
| 新业务意图覆盖旧终态 | `next_retry_at = now` 或初始延迟，按配置 `RetryImmediatelyOnNewIntent` 决定；默认 `now`。 |

如果不新增配置，默认采用 `now + InitialRetryDelaySeconds`，手动重连成功可显式提前。

## last_error 内容

`last_error` 长度受 `NVARCHAR(2000)` 限制，写入前应截断。

建议格式：

```text
code=DEVICE_OFFLINE; message=设备离线，已进入补偿; requestId=...
```

本地自用场景日志可按配置记录完整 payload；数据库 `last_error` 仍只保存定位补偿原因的短文本，不保存大块人脸 Base64。

## 并发与幂等

| 场景 | 处理 |
| --- | --- |
| 两个请求同时插入同一设备员工 | 一个插入成功，另一个捕获唯一键冲突后重试更新。 |
| 同一请求重复调用 | 覆盖为相同 pending 状态，不生成多行。 |
| 批量请求中重复员工 | 阶段 5 先按最后一次请求合并；阶段 6 再按设备员工合并。 |
| Store 异常 | 返回明确错误给阶段 5，gRPC 响应可标记该项 failed。 |

## 不做的事

| 不做内容 | 原因 |
| --- | --- |
| 不修改表结构 | 现有数据库零结构变更。 |
| 不记录完整操作历史 | 唯一键决定一行只表达最新状态。 |
| 不执行设备调用 | 本任务只写补偿状态。 |
| 不清理终态 | 清理由任务 6.6 负责。 |

## 测试

| 测试 | 验证 |
| --- | --- |
| 插入权限补偿 | pending、权限值、next_retry_at 正确。 |
| 合并人员和人脸 | 两个 pending 同时存在，payload 正确覆盖。 |
| 删除人脸覆盖人脸下发 | `face_pending = 0`，`delete_face_pending = 1`。 |
| 删除人员覆盖人员人脸 | 人员、人脸 payload 清空，删除人员 pending 生效。 |
| 终态重新激活 | `exhausted_at` 清空，attempt 重置。 |
| 并发 upsert | 同设备同员工最终只有一行。 |
| last_error 截断 | 超长错误不导致 SQL 写入失败。 |
