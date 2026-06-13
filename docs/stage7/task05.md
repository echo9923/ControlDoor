# 阶段 7 / 任务 05：attendance_gate_v2 入库与防重复

## 目标

实现 `FaceEventRepository.InsertEvent`，把标准 `AcsFaceEvent` 写入现有 `dbo.attendance_gate_v2` 表，并依赖 `id` 唯一索引保证实时事件和离线上传事件不会重复入库。

## 字段映射

| 表字段 | 来源 |
| --- | --- |
| `id` | `AcsFaceEvent.EventId`。 |
| `username` | `AcsFaceEvent.EmployeeId`。 |
| `nickname` | `AcsFaceEvent.Nickname`。 |
| `record_datetime` | `AcsFaceEvent.EventTime`。 |
| `record_date` | `EventTime.Date`。 |
| `record_time` | `EventTime.TimeOfDay`。 |
| `direction` | `AcsFaceEvent.Direction`。 |
| `device_name` | `AcsFaceEvent.DeviceName`。 |
| `device_sn` | `AcsFaceEvent.DeviceSn`。 |
| `card_no` | `AcsFaceEvent.CardNo`。 |
| `snapshot_path` | `SnapshotStorage` 返回路径。 |
| `raw_payload` | 标准 raw payload JSON。 |
| `event_type` | `AcsFaceEvent.EventType`。 |
| `process_status` | 默认 0。 |
| `process_message` | 认证结果或处理说明。 |
| `processed_at` | 可为空。 |
| `creator` | 固定服务标识，例如 `ControlDoor`。 |
| `create_time` | 当前时间或事件入库时间。 |
| `updater` | 固定服务标识。 |
| `update_time` | 当前时间。 |
| `deleted` | 固定 `0`。 |
| `tenant_id` | 默认 1，除非现有配置另有明确值。 |

## 入库流程

| 步骤 | 动作 |
| --- | --- |
| 1 | 校验 `EventId`、`EmployeeId`、`EventTime`。 |
| 2 | 补全昵称。 |
| 3 | 保存抓拍图片并得到路径。 |
| 4 | 构建 insert 命令。 |
| 5 | 执行插入。 |
| 6 | 捕获唯一键冲突，识别为重复事件。 |
| 7 | 返回 `Inserted` 或 `Duplicate` 结果。 |

## 防重复规则

| 场景 | 处理 |
| --- | --- |
| 实时事件重复回调 | 第二次插入命中 `ux_gate_v2_id`，返回 Duplicate。 |
| 离线上传事件补到实时已入库事件 | 命中唯一键，视为成功处理。 |
| 设备重复上传同一离线事件 | 命中唯一键，视为成功处理。 |
| EventId 生成碰撞 | 记录 `Error`，人工排查；实现上仍遵守唯一键。 |

重复事件不应当作为离线补偿失败。

## 事务边界

单条事件入库不需要长事务。离线上传事件与实时事件逐条插入并收集结果。

| 操作 | 事务建议 |
| --- | --- |
| 单条 insert | 单命令即可。 |
| 图片保存 | 在数据库 insert 前完成；insert 重复时可保留已写图片或跳过保存。 |

为了避免重复事件反复写图片，离线上传事件和实时事件可先尝试查重再保存图片；但最终仍以唯一键作为准。

## 错误处理

| 场景 | 处理 |
| --- | --- |
| 唯一键冲突 | 返回 Duplicate，不抛业务失败。 |
| 字段超长 | 截断允许截断的字段，并在 raw payload 记录。 |
| 必填字段缺失 | 不入库，返回 Invalid。 |
| 数据库连接失败 | 返回 RetryableFailure，事件消费重试或记录错误。 |
| SQL 超时 | 返回 RetryableFailure。 |

## 不做的事

| 不做内容 | 原因 |
| --- | --- |
| 不更新已有事件 | 现有表作为流水记录，重复事件只跳过。 |
| 不修改唯一索引 | 现有表结构冻结。 |
| 不删除事件 | 阶段 7 只写入。 |
| 不把事件补偿状态写入补偿表 | 离线事件补偿依赖设备上传，不使用 `device_operation_retry_states`。 |
| 不更新 checkpoint | 阶段 7 不采用平台主动历史查询。 |

## 测试

| 测试 | 验证 |
| --- | --- |
| 正常入库 | 所有字段映射正确。 |
| 重复 EventId | 第二次返回 Duplicate。 |
| 字段超长 | 被安全截断或失败原因明确。 |
| 无昵称 | 仍可入库。 |
| 数据库失败 | 返回可重试失败。 |
| 离线上传重复 | Duplicate 视为处理成功。 |
