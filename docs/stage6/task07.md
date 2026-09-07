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
| 删除人员 pending 且前置删人脸失败 | 删除人员仍继续执行；成功后状态行删除，Warn 日志可查。 |
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
| 删除人员前置删人脸失败日志 | 能看到 `RetryDeleteFaceBeforePerson`、employeeId、userId、异常类型或 SDK 错误码。 |
| 终态日志 | 能看到 exhaustedAt 和 terminal code。 |
| 清理日志 | 能看到删除数量和保留天数。 |

## 补偿意图版本回归

代码审查修复新增 `intent_version`，不再适用原阶段 6 的零结构变更约束。升级前执行 `database/专项_20260309_设备操作重试状态表.sql`；已有记录会补齐版本，重复执行不重复添加字段。

本机回归运行 `ControlEntradaSalida.Tests.exe RegressionTests`，验证在线写入失败、人员失败后人脸保留、旧请求延迟失败、旧补偿跳过，以及所有结果回写的版本条件。

真实 SQL 验证使用 `tests/Integration/RetrySqlIntegrationTests.cs`：将 `CONTROLDOOR_RETRY_SQL_INTEGRATION` 设为 `1`，并将 `CONTROLDOOR_STAGE14_CONNECTION_STRING` 指向可丢弃的 Docker 测试数据库后，运行 `ControlEntradaSalida.Tests.exe RetrySqlIntegrationTests`。测试执行两次迁移，再验证旧删除任务、旧退避和旧终态回写均不能改变新意图，最后清理本次生成的测试记录。未设置开关时明确跳过，不使用真实设备。

## 阶段 6 通过标准

最终审查回归运行 `ControlEntradaSalida.Tests.exe Final`，覆盖跨操作覆盖、迟到成功确认、权限状态事务回滚、终态设备阻塞全局完成、不同工作线程并发，以及设备删除、撤防和服务停止的竞争场景。实际 SQL 集成测试另覆盖完整目标登记、权限 payload 保留和失败事务回滚，未连接 Docker 数据库时不代表这些 SQL 已在真实数据库执行。

| 标准 | 说明 |
| --- | --- |
| 数据库零结构变更 | 未修改现有表结构，未新增阶段 6 表。 |
| 合并规则确定 | 最新业务意图覆盖旧意图，冲突动作有明确结果。 |
| 扫描可恢复 | 服务重启后可继续扫描未完成状态。 |
| 重试可退避 | 可重试失败不会高频占用设备通道。 |
| 终态可追踪 | 达到上限或不可重试失败保留终态记录。 |
| 清理可控 | 过期终态按保留天数清理。 |
| 契约兼容 | 阶段 5 gRPC 响应仍保持既定 JSON 字段和错误码。 |
