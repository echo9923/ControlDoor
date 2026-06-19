# 阶段 6 / 任务 03：到期扫描与领取

## 目标

实现 `DeviceOperationRetryManager` 的后台扫描逻辑，按配置周期读取 `device_operation_retry_states` 中到期且未终态的补偿状态，并把可执行状态转换为待投递计划。

## 启动条件

| 条件 | 说明 |
| --- | --- |
| 配置加载完成 | 读取 `DeviceOperationRetry` 配置。 |
| 数据库健康可用 | 阶段 6 启用后 `device_operation_retry_states` 不可读应视为失败。 |
| 设备运行时已启动 | 需要判断设备启用状态、在线状态和运行时索引。 |
| `BatchSize > 0` | 小于 1 时使用默认值 100。 |

## 扫描 SQL 语义

扫描只读取：

| 条件 | 说明 |
| --- | --- |
| `exhausted_at IS NULL` | 终态失败不再正常重试。 |
| `next_retry_at IS NULL OR next_retry_at <= now` | 到期或历史兼容空值。 |
| 至少一个 pending 为 1 | 没有 pending 的异常行应由清理逻辑处理。 |

排序建议：

1. `next_retry_at ASC`
2. `updated_at ASC`
3. `id ASC`

每轮最多读取 `BatchSize` 行。

## 领取策略

当前表没有 `locked_at` 或 `owner` 字段，不能做严格的持久化领取锁。阶段 6 采用零表结构变更的双层去重策略，尽量避免多个服务实例同时领取同一条到期状态：

| 机制 | 规则 |
| --- | --- |
| 单实例扫描 | `DeviceOperationRetryManager` 在当前进程只允许一个扫描循环运行。 |
| 内存 in-flight | 以 `retryStateId` 或 `deviceId + employeeId` 记录已投递未完成状态。 |
| 到期扫描锁 | `LoadDueStates` 使用 `UPDLOCK, READPAST, ROWLOCK` 读取候选到期行，跳过其它事务正在锁定的行。 |
| 数据库领取 | 投递前调用 guarded update：仅当 `exhausted_at IS NULL` 且 `next_retry_at IS NULL OR next_retry_at <= now` 时，把 `next_retry_at` 推到一个短租约时间并写入 `last_attempt_at`。若受影响行数为 0，说明已被其它实例领取或状态已变化，本轮跳过。 |
| 服务重启 | 如果领取后进程退出但未回写成功/失败，短租约到期后状态会再次被扫描。 |

该方案不新增字段，能显著降低多实例重复领取概率；但由于没有 `owner`、`locked_at`、`lock_token` 等字段，不能表达严格所有权，也不能在设备调用时间超过短租约时提供绝对 exactly-once 语义。若未来要求严格多实例调度，应新增领取字段或使用外部分布式锁。

## 扫描流程

| 步骤 | 动作 |
| --- | --- |
| 1 | 等待启动延迟，避免服务刚启动时设备尚未登录完成。 |
| 2 | 创建扫描 requestId。 |
| 3 | 查询到期补偿状态，最多 `BatchSize`。 |
| 4 | 过滤 in-flight 状态。 |
| 5 | 读取设备运行时快照。 |
| 6 | 设备停用或不存在时按不可执行失败处理。 |
| 7 | 设备离线时推迟 `next_retry_at`，不投递设备任务。 |
| 8 | 设备在线时生成补偿执行计划。 |
| 9 | 投递计划给任务 6.4 的执行编排。 |
| 10 | 记录本轮扫描数量、跳过数量、投递数量和耗时。 |

## 设备状态处理

| 状态 | 行为 |
| --- | --- |
| 在线 | 可投递补偿任务。 |
| 离线 | 不投递，调用 `ScheduleRetry` 延后下次尝试。 |
| 正在重连 | 不投递，短延迟后再扫。 |
| 停用 | 标记终态或保留等待人工处理，默认标记终态 `DEVICE_DISABLED`。 |
| 不存在 | 标记终态 `DEVICE_NOT_FOUND`。 |
| 配置非法 | 标记终态 `DEVICE_CONFIG_INVALID`。 |

## 空 pending 异常行

若扫描到所有 pending 均为 0 的行：

| 处理 | 说明 |
| --- | --- |
| 首选删除 | 这种行已无补偿意义，可以删除。 |
| 日志级别 | `Warn`，包含 id、device_id、employee_id。 |
| 不投递任务 | 避免生成空设备调用。 |

## 扫描异常

| 异常 | 处理 |
| --- | --- |
| 数据库连接失败 | 记录 `Error`，等待下一轮扫描。 |
| SQL 超时 | 记录 `Warn` 或 `Error`，下一轮继续。 |
| 单行解析失败 | 记录该行错误并跳过，不中断整轮。 |
| 执行编排异常 | 移除 in-flight，按可重试失败回写。 |

## 日志字段

| 字段 | 说明 |
| --- | --- |
| `requestId` | 扫描轮次 ID。 |
| `scanBatchSize` | 本轮读取数量。 |
| `dueCount` | 到期状态数量。 |
| `inFlightSkipped` | 因已在执行而跳过数量。 |
| `claimSkipped` | 因数据库领取失败而跳过数量，通常表示其它实例已领取或状态已变化。 |
| `offlineDeferred` | 因设备离线延后数量。 |
| `submitted` | 成功投递数量。 |
| `durationMs` | 本轮扫描耗时。 |

## 测试

| 测试 | 验证 |
| --- | --- |
| 到期查询 | 只读取未终态且到期状态。 |
| 批量限制 | 每轮不超过 `BatchSize`。 |
| 排序稳定 | next_retry_at、updated_at、id 顺序正确。 |
| in-flight 去重 | 已投递未完成状态不重复投递。 |
| 数据库领取去重 | 多实例并发时，guarded update 失败的实例不投递设备任务。 |
| 离线延后 | 设备离线只更新 next_retry_at，不进入 worker。 |
| 停用终态 | 停用设备写入 exhausted_at。 |
| 扫描异常恢复 | 一轮失败不终止后台任务。 |
