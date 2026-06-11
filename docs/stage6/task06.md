# 阶段 6 / 任务 06：清理、配置、日志和运维观测

## 目标

完善阶段 6 的配置校验、后台任务生命周期、终态记录清理、运行日志和现场排查方式，使补偿机制能长期运行且便于定位问题。

## 配置项

沿用主方案中的 `DeviceOperationRetry` 配置：

| 配置项 | 默认值 | 规则 |
| --- | --- | --- |
| `ScanIntervalSeconds` | `30` | 小于 5 时回退默认值。 |
| `InitialRetryDelaySeconds` | `60` | 小于 1 时回退默认值。 |
| `MaxRetryDelaySeconds` | `3600` | 不得小于初始延迟。 |
| `MaxRetryAttempts` | `10` | 小于 1 时回退默认值。 |
| `FailureRetentionDays` | `7` | 小于 1 时回退默认值。 |
| `BatchSize` | `100` | 小于 1 时回退默认值。 |

不新增必须配置项。若实现需要增加可选项，必须给出默认值并同步主方案、配置模板和文档。

## 后台任务生命周期

| 生命周期 | 行为 |
| --- | --- |
| 服务启动 | 设备运行时启动后注册 `DeviceOperationRetryManager`。 |
| 首次延迟 | 可等待一个扫描周期或短延迟，避免设备登录尚未完成就大量延后。 |
| 正常运行 | 按 `ScanIntervalSeconds` 周期扫描。 |
| 服务停止 | 停止新扫描，不再投递新补偿任务。 |
| 正在执行任务 | 等待短时间完成；超时则保留状态，下次启动继续。 |

## 终态清理

`RetryCleanupJob` 按保留天数删除终态失败记录：

| 条件 | 说明 |
| --- | --- |
| `exhausted_at IS NOT NULL` | 只清理终态失败。 |
| `exhausted_at < now - FailureRetentionDays` | 超过保留天数。 |
| 分批删除 | 每批数量使用 `BatchSize` 或固定上限，避免长事务。 |

清理任务不删除未终态状态，不删除仍有 pending 但未达到保留期的记录。

## 运维查询

可在现场用以下思路排查，不要求新增 SQL 脚本：

| 目的 | 查询条件 |
| --- | --- |
| 查看待补偿数量 | `exhausted_at IS NULL` 且任一 pending 为 1。 |
| 查看到期未执行 | `next_retry_at <= SYSDATETIME()` 且 `exhausted_at IS NULL`。 |
| 查看终态失败 | `exhausted_at IS NOT NULL`。 |
| 查看某员工 | `employee_id = @employeeId`。 |
| 查看某设备 | `device_id = @deviceId`。 |

如果后续需要提供运维 SQL，可以新增独立文档或脚本，但不得修改现有表结构。

## 日志分类

| 事件 | 级别 | 说明 |
| --- | --- | --- |
| 补偿意图写入 | `Info` | 正常排队。 |
| 状态合并 | `Info` | 同设备同员工覆盖或合并。 |
| 扫描开始/结束 | `Debug` 或 `Info` | 记录批量和耗时。 |
| 离线延后 | `Info` | 设备未在线，不投递。 |
| 可重试失败 | `Warn` | 有下一次重试。 |
| 终态失败 | `Error` | 需要人工关注。 |
| 清理终态 | `Info` | 删除过期失败记录。 |
| 数据库异常 | `Error` | 本轮失败，下轮继续。 |

## 日志字段

| 字段 | 说明 |
| --- | --- |
| `requestId` | 来源 gRPC 请求或扫描轮次。 |
| `retryStateId` | 补偿表主键。 |
| `deviceId` | 目标设备。 |
| `employeeId` | 员工编号。 |
| `operations` | pending 操作集合。 |
| `attempt` | 当前尝试次数。 |
| `nextRetryAt` | 下次重试时间。 |
| `exhaustedAt` | 终态时间。 |
| `code` | 业务错误码。 |
| `sdkErrorCode` | SDK 错误码。 |

完整 payload 是否记录由配置控制。本地自用部署可以按配置开启完整记录；默认摘要日志仍应避免把日志量放大到不可维护。

## 指标建议

不要求阶段 6 引入独立监控系统，但日志中应能计算：

| 指标 | 说明 |
| --- | --- |
| `retry.pending.count` | 未终态待补偿数量。 |
| `retry.due.count` | 当前到期数量。 |
| `retry.submitted.count` | 投递设备任务数量。 |
| `retry.success.count` | 成功补偿数量。 |
| `retry.failed.retryable.count` | 可重试失败数量。 |
| `retry.failed.terminal.count` | 终态失败数量。 |
| `retry.cleanup.deleted.count` | 清理终态数量。 |

## 现场处理建议

| 问题 | 处理 |
| --- | --- |
| 大量到期但不执行 | 检查设备是否在线、worker 队列是否满、扫描日志是否报错。 |
| 一直可重试失败 | 查看 `last_error` 和 SDK 错误码，确认设备网络和账号。 |
| 终态失败 | 修复设备或 payload 后重新触发阶段 5 同步请求。 |
| 表记录持续增长 | 检查 `FailureRetentionDays` 清理任务是否运行。 |
| 某员工状态不符合预期 | 按 `device_id + employee_id` 查唯一状态行，确认最新业务意图。 |

## 测试

| 测试 | 验证 |
| --- | --- |
| 配置非法回退 | 小于阈值使用默认值。 |
| 服务停止 | 停止新扫描，不丢持久化状态。 |
| 清理终态 | 只删除超过保留期终态行。 |
| 日志字段完整 | 核心字段可用于追踪一条补偿。 |
| payload 日志开关 | 开关关闭记录摘要，开启可记录完整内容。 |
| 扫描异常不退出 | 后台任务下一轮继续。 |
