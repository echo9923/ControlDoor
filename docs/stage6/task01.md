# 阶段 6 / 任务 01：阶段边界与任务总览

## 目标

阶段 6 实现离线设备写操作补偿机制，使用现有 `dbo.device_operation_retry_states` 表承接阶段 5 产生的补偿意图，完成补偿状态合并、到期扫描、重试投递、结果回写、终态标记、过期清理和可观测日志。

阶段 6 只处理权限、人员、人脸、删除人员、删除人脸这几类设备写操作的补偿，不处理 ACS 人脸事件历史补偿，也不处理摄像头 AIOP 门禁联动恢复。

## 硬性边界

| 边界 | 要求 |
| --- | --- |
| 现有表零结构变更 | 不修改 `device_operation_retry_states` 字段、索引、默认值、唯一约束和类型。 |
| 不新增阶段 6 表 | 阶段 6 的持久化只使用现有补偿表。 |
| 同设备同员工唯一 | 严格遵守 `device_id + employee_id` 唯一状态行。 |
| 设备调用通道化 | 补偿重试必须通过 `DeviceSdkDispatcher` 投递，不能直接调用 SDK 或 ISAPI。 |
| 不改变 gRPC 契约 | 阶段 6 不新增、不修改外部 gRPC 方法。 |
| 不阻塞设备 worker | 延迟、退避、等待下次扫描都不能在设备 worker 中 `Thread.Sleep`。 |
| 不做事件补偿 | `face_event_checkpoint` 和历史事件补偿属于阶段 7。 |
| 不做 AIOP 联动 | 摄像头报警联动门禁常闭属于后续阶段。 |

## 任务拆分

| 任务 | 文件 | 主题 |
| --- | --- | --- |
| 6.1 | `task01.md` | 阶段边界与任务总览。 |
| 6.2 | `task02.md` | 补偿状态写入与合并。 |
| 6.3 | `task03.md` | 到期扫描与领取。 |
| 6.4 | `task04.md` | 重试任务投递与执行编排。 |
| 6.5 | `task05.md` | 成功、失败、终态结果回写。 |
| 6.6 | `task06.md` | 清理、配置、日志和运维观测。 |
| 6.7 | `task07.md` | 阶段测试与验收。 |

## 模块划分

| 模块 | 职责 |
| --- | --- |
| `DeviceOperationRetryStore` | 补偿状态的 upsert、扫描、结果回写、终态标记、清理。 |
| `RetryStateMerger` | 将阶段 5 的补偿意图合并为单行状态。 |
| `DeviceOperationRetryManager` | 后台扫描入口，控制扫描周期、批量大小和并发投递。 |
| `RetryCommandPlanner` | 将一行补偿状态转换为一个或多个设备任务。 |
| `RetryExecutionCoordinator` | 跟踪补偿任务投递、完成回调和状态更新。 |
| `RetryBackoffCalculator` | 根据配置计算下一次重试时间。 |
| `RetryCleanupJob` | 清理超过保留天数的终态失败记录。 |

## 状态行语义

`device_operation_retry_states` 使用一行表示“某个员工在某台设备上的最新待补偿状态”。由于唯一键固定为 `device_id + employee_id`，阶段 6 不保存完整操作历史，而保存最新业务意图。

| 字段组 | 语义 |
| --- | --- |
| `permission_level`、`permission_pending` | 需要把该员工权限同步到指定等级。 |
| `person_payload`、`person_pending` | 需要把人员基础信息下发到设备。 |
| `face_payload`、`face_pending` | 需要把人脸信息下发到设备。 |
| `delete_person_pending` | 需要删除设备端人员。 |
| `delete_face_pending` | 需要删除设备端人脸。 |
| `attempt_count` | 当前状态行连续补偿尝试次数。 |
| `next_retry_at` | 下次允许被扫描的时间。 |
| `last_error`、`last_attempt_at` | 最近一次补偿失败或跳过原因。 |
| `exhausted_at` | 达到终态后不再被正常扫描。 |

## 操作冲突规则

因为表结构不记录操作序列，必须用明确规则表达“最新业务意图”：

| 新意图 | 合并规则 |
| --- | --- |
| 新权限同步 | 覆盖 `permission_level`，置 `permission_pending = 1`。 |
| 新人员下发 | 覆盖 `person_payload`，置 `person_pending = 1`，清除 `delete_person_pending`。 |
| 新人脸下发 | 覆盖 `face_payload`，置 `face_pending = 1`，清除 `delete_face_pending`。 |
| 新删除人员 | 置 `delete_person_pending = 1`，清除 `permission_pending`、`person_pending`、`face_pending`、`delete_face_pending`。 |
| 新删除人脸 | 置 `delete_face_pending = 1`，清除 `face_pending`。 |
| 已终态行收到新意图 | 清空 `exhausted_at`，重置 `attempt_count`，按新意图重新进入补偿。 |

默认原则：同一设备同一员工以最后一次业务请求为准，避免旧补偿覆盖新请求。

## 主流程

| 步骤 | 动作 |
| --- | --- |
| 1 | 阶段 5 产生补偿意图，调用 `DeviceOperationRetryStore.UpsertIntent`。 |
| 2 | Store 在事务内按 `device_id + employee_id` 插入或更新补偿状态。 |
| 3 | 后台 `DeviceOperationRetryManager` 按 `ScanIntervalSeconds` 扫描到期状态。 |
| 4 | 对仍启用且可连接的设备生成 `RetryDeviceOperation` 任务。 |
| 5 | 任务通过 `DeviceSdkDispatcher` 投递到对应设备固定通道。 |
| 6 | 设备任务按人员、权限、人脸、删除规则执行。 |
| 7 | 成功后清除对应 pending；全部 pending 清空后删除状态行。 |
| 8 | 可重试失败更新 `attempt_count`、`next_retry_at`、`last_error`。 |
| 9 | 达到最大次数或不可重试失败写入 `exhausted_at`。 |
| 10 | 清理任务按保留天数删除终态失败记录。 |

## 阶段完成标准

| 标准 | 说明 |
| --- | --- |
| 补偿意图可持久化 | 阶段 5 的离线或可重试失败可落到现有表。 |
| 状态合并可预测 | 同设备同员工重复请求、冲突请求都有固定规则。 |
| 到期扫描可控 | 批量大小、扫描间隔、终态过滤、离线跳过明确。 |
| 重试走设备通道 | 不绕过 `DeviceSdkDispatcher`。 |
| 结果回写完整 | 成功、可重试失败、不可重试失败、达到上限都有明确状态变化。 |
| 运维可观察 | 日志能定位设备、员工、操作、attempt、nextRetry、错误。 |
| 数据库兼容 | 不修改现有表结构，不新增阶段 6 表。 |
