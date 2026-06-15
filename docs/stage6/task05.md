# 阶段 6 / 任务 05：成功、失败、终态结果回写

## 目标

实现补偿执行结果的数据库回写规则，确保成功操作清除 pending，可重试失败更新下一次重试时间，不可重试失败或达到最大次数写入终态。

## 回写入口

建议 `DeviceOperationRetryStore` 提供以下方法：

| 方法 | 职责 |
| --- | --- |
| `MarkOperationSuccess` | 清除指定成功操作 pending。 |
| `ScheduleRetry` | 更新失败次数、下次重试时间和错误。 |
| `MarkTerminalFailure` | 写入 `exhausted_at`，停止后续扫描。 |
| `DeleteIfCompleted` | 所有 pending 清空后删除状态行。 |
| `ResetForNewIntent` | 新业务意图到达终态行时重新激活。 |

## 成功回写

| 成功操作 | 字段变化 |
| --- | --- |
| 权限同步成功 | `permission_pending = 0`，`permission_sync_completion_blocked = 0`，`permission_payload = NULL`，必要时更新 `system_users.last_synced_level`、`last_synced_at`。 |
| 人员下发成功 | `person_pending = 0`，`person_payload = NULL`。 |
| 人脸下发成功 | `face_pending = 0`，`face_payload = NULL`。 |
| 删除人脸成功 | `delete_face_pending = 0`。 |
| 删除人员成功 | 清除该行全部 pending 和 `permission_payload`、`person_payload`、`face_payload`。 |

成功回写后，如果该状态行所有 pending 均为 0，则删除该行。删除补偿状态表示当前没有未完成补偿，不表示删除业务数据。

## 权限同步完成标记

`permission_sync_completion_blocked` 用于表达“权限同步完成状态是否被补偿阻塞”。

| 场景 | 规则 |
| --- | --- |
| 权限同步进入补偿 | 置为 `1`。 |
| 权限补偿成功 | 置为 `0`。 |
| 权限补偿终态失败 | 保持 `1`，便于人工识别未完成。 |
| 新权限意图到达 | 重新置为 `1`。 |

## 可重试失败

当设备任务返回 `Retryable = true` 且未达到最大次数：

| 字段 | 更新 |
| --- | --- |
| `attempt_count` | 加 1。 |
| `last_attempt_at` | 当前时间。 |
| `next_retry_at` | 由退避策略计算。 |
| `last_error` | 写入错误码、SDK 错误码、操作名和简短说明。 |
| `updated_at` | 当前时间。 |

## 退避策略

默认采用指数退避并限制最大值：

```text
delay = min(InitialRetryDelaySeconds * 2^(attempt_count - 1), MaxRetryDelaySeconds)
```

如果启用抖动，可在 delay 上增加小幅随机偏移，避免大量设备同时到期。抖动只影响 `next_retry_at`，不影响 attempt 计数。

## 终态失败

以下场景写入 `exhausted_at`：

| 场景 | 错误码 |
| --- | --- |
| 达到 `MaxRetryAttempts` | `RETRY_EXHAUSTED`。 |
| 设备不存在 | `DEVICE_NOT_FOUND`。 |
| 设备停用 | `DEVICE_DISABLED`。 |
| 设备能力不支持 | `DEVICE_UNSUPPORTED`。 |
| 参数或 payload 永久非法 | `INVALID_PAYLOAD`。 |
| SDK/DLL 平台错误 | `SDK_CONFIGURATION_ERROR`。 |

终态失败不删除状态行，保留到 `FailureRetentionDays` 后由清理任务删除。

## 不可重试失败

不可重试失败应立即终态，不继续消耗重试次数。`attempt_count` 可加 1 以表示已经尝试过一次，也可保持当前值；建议加 1，并写入 `last_attempt_at`，方便运维判断。

## 并发保护

结果回写必须确认状态行仍对应同一业务意图，避免旧 in-flight 结果覆盖新请求。

建议策略：

| 机制 | 说明 |
| --- | --- |
| 读取当前状态 | 回写前查询当前 pending 和 updated_at。 |
| 操作级清除 | 只清除本次实际成功的 pending。 |
| 不覆盖新 payload | 成功回写不得把当前不同的新 payload 清空。 |
| requestId 日志 | 数据库不新增字段，日志记录旧结果与当前状态差异。 |

由于表中没有版本字段，无法做严格乐观锁。实现时应保持保守：旧任务只清除自己确认成功且当前仍 pending 的操作，不修改无关 pending。

## 日志

| 事件 | 级别 | 内容 |
| --- | --- | --- |
| 补偿成功 | `Info` | deviceId、employeeId、operations、attempt、durationMs。 |
| 部分成功 | `Warn` | succeeded、failedOperation、nextRetryAt。 |
| 可重试失败 | `Warn` | code、sdkErrorCode、attempt、nextRetryAt。 |
| 终态失败 | `Error` | terminalCode、attempt、exhaustedAt、lastError。 |
| 状态删除 | `Info` | retryStateId、deviceId、employeeId。 |

## 测试

| 测试 | 验证 |
| --- | --- |
| 全部成功删除行 | pending 清空后状态行删除。 |
| 部分成功保留失败 pending | 成功字段清除，失败字段保留。 |
| 可重试失败退避 | attempt 增加，next_retry_at 正确。 |
| 达到上限终态 | 写入 exhausted_at，不再扫描。 |
| 不可重试立即终态 | 不安排下次重试。 |
| 权限完成标记 | permission_sync_completion_blocked 成功后清零。 |
| 旧任务回写 | 不清空新业务 payload。 |
