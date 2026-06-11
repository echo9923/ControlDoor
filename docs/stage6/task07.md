# 阶段 6 / 任务 07：阶段测试与验收

## 目标

定义阶段 6 的单元测试、数据库兼容测试、mock 设备集成测试和运维验收标准，证明离线补偿机制在不修改现有表结构的前提下可稳定运行。

## 单元测试

| 测试类 | 覆盖内容 |
| --- | --- |
| `RetryStateMergerTests` | 权限、人员、人脸、删除动作合并和冲突覆盖。 |
| `RetryBackoffCalculatorTests` | 初始延迟、指数退避、最大延迟、最大次数。 |
| `RetryCommandPlannerTests` | pending 组合到操作顺序的转换。 |
| `RetryExecutionResultMapperTests` | 成功、部分成功、可重试失败、终态失败映射。 |
| `RetryOptionsValidatorTests` | 配置非法值回退默认值。 |

## 数据库兼容测试

| 测试 | 验证 |
| --- | --- |
| 表结构快照 | `device_operation_retry_states` 字段、索引、约束不变。 |
| Upsert 插入 | 新补偿状态字段正确。 |
| Upsert 更新 | 同设备同员工只有一行。 |
| 终态重新激活 | 新业务意图可清空 `exhausted_at`。 |
| 到期扫描 | 只扫描未终态、到期、有 pending 的状态。 |
| 成功删除 | 所有 pending 清空后删除状态行。 |
| 失败回写 | attempt、next_retry_at、last_error、last_attempt_at 正确。 |
| 终态写入 | 达到上限写入 exhausted_at。 |
| 清理终态 | 只删除超过保留天数的终态行。 |

## Mock SDK 集成测试

| 场景 | 预期 |
| --- | --- |
| 离线权限补偿后设备在线 | 扫描后投递 `RetryDeviceOperation`，权限成功后状态删除。 |
| 人员和人脸同时 pending | 人员先执行，人脸后执行。 |
| 人员失败 | 人脸不执行，状态保留。 |
| 人员成功人脸失败 | 人员 pending 清除，人脸 pending 保留并退避。 |
| 删除人员 pending | 删除人员成功后状态行删除，不执行其他 pending。 |
| 队列满 | 状态不丢失，next_retry_at 后移。 |
| 设备停用 | 标记终态失败。 |
| 达到最大次数 | 写入 exhausted_at，不再被扫描。 |

## gRPC 间接验证

阶段 6 不修改 gRPC 契约，但需要通过阶段 5 接口验证补偿入口：

| 方法 | 验证 |
| --- | --- |
| `SyncPermissions` | 离线设备返回 queued，补偿表生成权限 pending。 |
| `SyncPersons` | 离线设备返回 queued，补偿表生成人员和人脸 pending。 |
| `DeleteFaces` | 离线设备返回 queued，生成删除人脸 pending。 |
| `DeletePersons` | 离线设备返回 queued，生成删除人员 pending。 |

这些测试只验证响应字段保持兼容，不新增外部响应字段要求。

## 设备通道验证

| 测试 | 验证 |
| --- | --- |
| 同设备串行 | 同一设备多个补偿任务按 dispatcher 顺序执行。 |
| 不同设备并行 | 不同设备可分配到不同 worker。 |
| Retry 优先级 | 补偿任务低于实时 `Normal` 请求。 |
| Retry 不饥饿 | 等待超过 aging 阈值后可被执行。 |
| 不直调 SDK | mock 验证业务层只通过 gateway/dispatcher。 |

## 日志与运维验收

| 验收项 | 标准 |
| --- | --- |
| 补偿写入日志 | 能看到 requestId、deviceId、employeeId、operation。 |
| 扫描日志 | 能看到本轮 due、submitted、offlineDeferred。 |
| 失败日志 | 能看到 attempt、nextRetryAt、lastError。 |
| 终态日志 | 能看到 exhaustedAt 和 terminal code。 |
| 清理日志 | 能看到删除数量和保留天数。 |

## 阶段 6 通过标准

| 标准 | 说明 |
| --- | --- |
| 数据库零结构变更 | 未修改现有表结构，未新增阶段 6 表。 |
| 合并规则确定 | 最新业务意图覆盖旧意图，冲突动作有明确结果。 |
| 扫描可恢复 | 服务重启后可继续扫描未完成状态。 |
| 重试可退避 | 可重试失败不会高频占用设备通道。 |
| 终态可追踪 | 达到上限或不可重试失败保留终态记录。 |
| 清理可控 | 过期终态按保留天数清理。 |
| 契约兼容 | 阶段 5 gRPC 响应仍保持既定 JSON 字段和错误码。 |
