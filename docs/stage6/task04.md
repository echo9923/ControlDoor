# 阶段 6 / 任务 04：重试任务投递与执行编排

## 目标

实现补偿状态到设备任务的转换与执行编排。阶段 6 必须把每条补偿状态转换为 `RetryDeviceOperation` 任务，并通过 `DeviceSdkDispatcher` 投递到目标设备固定通道。

## 执行计划模型

建议内部使用 `RetryExecutionPlan`：

| 字段 | 说明 |
| --- | --- |
| `RetryStateId` | 补偿状态主键。 |
| `DeviceId` | 目标设备。 |
| `EmployeeId` | 员工编号。 |
| `Attempt` | 本次尝试次数，通常为当前 `attempt_count + 1`。 |
| `Operations` | 需要执行的操作列表。 |
| `RequestId` | 扫描或触发来源 ID。 |
| `RetryStateKey` | `deviceId + employeeId` 摘要，用于任务去重。 |

## 操作顺序

同一状态行可能包含多个 pending。执行顺序必须固定：

| 顺序 | 操作 | 条件 |
| --- | --- | --- |
| 1 | 删除人员 | `delete_person_pending = 1`。 |
| 2 | 删除人脸 | `delete_face_pending = 1` 且未执行删除人员。 |
| 3 | 人员下发 | `person_pending = 1`。 |
| 4 | 权限同步 | `permission_pending = 1`。 |
| 5 | 人脸下发 | `face_pending = 1` 且人员下发成功或设备已有人员。 |

删除人员优先级最高。删除人员执行时会先 best-effort 删除同一员工的人脸；前置删人脸失败必须记录 `Warn` 日志，字段至少包含 `operationName=RetryDeleteFaceBeforePerson`、`employeeId`、`userId` 和异常摘要，但不阻断后续删除人员。删除人员成功后，不再执行同一行中的其他人员、人脸、权限下发动作。

## 设备任务参数

| 字段 | 值 |
| --- | --- |
| `OperationName` | `RetryDeviceOperation`。 |
| `Priority` | `Retry`。 |
| `RequiresOnline` | `true`。 |
| `TimeoutMs` | 使用对应业务操作超时；没有配置时使用设备任务默认超时。 |
| `RetrySource.IsRetry` | `true`。 |
| `RetrySource.RetryCategory` | 根据 pending 组合填写，例如 `Permission,Person,Face`。 |
| `RetrySource.RetryAttempt` | 当前尝试次数。 |
| `RetrySource.RetryStateKey` | `deviceId + employeeId`。 |

## 投递规则

| 场景 | 处理 |
| --- | --- |
| 投递成功 | 记录 in-flight，等待设备任务完成回调。 |
| 设备队列满 | 不丢状态，调用 `ScheduleRetry` 延后。 |
| 设备已离线 | 不投递，调用 `ScheduleRetry` 延后。 |
| 设备任务创建失败 | 按可重试失败回写。 |
| 服务正在停止 | 不再投递新任务，保留状态等待下次启动。 |

## 执行结果模型

建议设备任务返回 `RetryExecutionResult`：

| 字段 | 说明 |
| --- | --- |
| `RetryStateId` | 补偿状态主键。 |
| `DeviceId` | 设备 ID。 |
| `EmployeeId` | 员工编号。 |
| `SucceededOperations` | 成功操作集合。 |
| `FailedOperation` | 首个失败操作。 |
| `FailedCode` | 失败错误码。 |
| `FailedMessage` | 失败说明。 |
| `SdkErrorCode` | SDK 错误码。 |
| `Retryable` | 是否可重试。 |
| `DurationMs` | 本次任务耗时。 |

## 部分成功规则

| 场景 | 处理 |
| --- | --- |
| 人员成功、权限失败 | 清除 `person_pending`，保留 `permission_pending` 和后续未执行 pending。 |
| 人员成功、人脸失败 | 清除 `person_pending`，保留 `face_pending`。 |
| 权限成功、人脸失败 | 清除 `permission_pending`，保留 `face_pending`。 |
| 删除人脸成功 | 清除 `delete_face_pending`。 |
| 删除人员前置删人脸失败、删除人员成功 | 清除所有该行 pending，并删除状态行；前置失败只通过日志观测。 |
| 删除人员失败 | 保留删除人员 pending，按失败结果退避或终态。 |
| 删除人员成功 | 清除所有该行 pending，并删除状态行。 |

部分成功必须回写已经成功的 pending，避免下次重复执行已成功操作。

## 与阶段 5 的关系

阶段 5 负责实时 gRPC 请求的在线执行和补偿意图生成。阶段 6 的重试执行复用相同底层设备能力，但来源、优先级、响应方式不同：

| 项目 | 阶段 5 | 阶段 6 |
| --- | --- | --- |
| 来源 | 外部 gRPC 请求。 | 补偿表扫描。 |
| 优先级 | `Normal`。 | `Retry`。 |
| 响应 | 直接返回 gRPC JSON。 | 通过日志和补偿状态观察。 |
| 失败处理 | 产生补偿意图。 | 回写 attempt、next_retry_at 或 exhausted_at。 |

权限补偿执行时不再调用设备端 `UserRight/SetUp`。后台任务读取状态行中的 `permission_level` 和可选 `permission_payload`，按当前设备快照 `Description` 识别办公、生产或 Other 区域，计算该员工在本设备上是否启用，然后通过 `UserInfo/SetUp` 的人员写入能力更新 `Valid.enable`、`doorRight` 和 `RightPlan`。旧补偿数据没有 `permission_payload` 或没有姓名时，设备端姓名使用员工号兜底。

删除人员补偿与阶段 5 的在线删除保持同一折中语义：前置删人脸是可观测的 best-effort 步骤，失败只记录日志；只有最终 `DeletePersonAsync` 离线、超时或可重试失败时，才回写删除人员补偿状态。

## 不做的事

| 不做内容 | 原因 |
| --- | --- |
| 不直接访问 `HCNetSDK` | 必须通过设备通道。 |
| 不在 worker 内等待下次重试 | 延迟由扫描和调度控制。 |
| 不修改 gRPC 响应 | 补偿是后台任务。 |
| 不做历史事件查询 | 阶段 7 负责。 |

## 测试

| 测试 | 验证 |
| --- | --- |
| 操作顺序 | 删除、人员、权限、人脸顺序固定。 |
| 删除人员短路 | 删除人员成功后不再执行其他操作。 |
| 删除人员前置删人脸失败 | 仍执行删除人员，并写入 Warn 日志。 |
| 部分成功 | 已成功 pending 被清除，失败 pending 保留。 |
| 队列满 | 状态未丢失并延后重试。 |
| 优先级 | 补偿任务使用 `Retry` 优先级。 |
| 通道化 | mock 验证没有绕过 dispatcher。 |
| 停止中投递 | 服务停止时不投递新任务。 |
