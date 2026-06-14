# 阶段 9 / 任务 05：门禁常闭、恢复和停止清理

## 目标

实现门目标常闭和恢复任务。阶段 9 通过设备固定执行通道调用 `NET_DVR_ControlGateway`，窗口开始时把门控制为常闭，窗口结束后恢复为普通受控状态。

## 门控命令

| 操作 | SDK 命令 |
| --- | --- |
| 常闭 | `NET_DVR_GATEWAY_CONTROL_ALWAYS_CLOSE`。 |
| 恢复普通受控 | `NET_DVR_GATEWAY_CONTROL_CLOSE`。 |

具体常量值和函数签名在编码时必须以 `HCNetSDK.h` 和本地 SDK 文档为准。

## 设备任务参数

| 字段 | 常闭 | 恢复 |
| --- | --- | --- |
| `OperationName` | `ControlGatewayAlwaysClose` | `ControlGatewayRestoreControlled` |
| `Priority` | `High` | `Critical` |
| `RequiresOnline` | true | true |
| `DoorNo` | 配置门号 | 配置门号 |
| `TaskKey` | `targetKey + AlwaysClose` | `targetKey + Restore` |
| `TimeoutMs` | 设备任务默认或门控配置 | 设备任务默认或门控配置 |

恢复优先级高于常闭，避免窗口结束后恢复被普通任务长期排队。

## 常闭流程

| 步骤 | 动作 |
| --- | --- |
| 1 | 门目标首次进入活动集合。 |
| 2 | 创建常闭设备任务。 |
| 3 | 通过 `DeviceSdkDispatcher` 投递到目标门禁设备。 |
| 4 | SDK 网关调用 `NET_DVR_ControlGateway`。 |
| 5 | 记录成功或失败。 |

常闭失败时不应阻塞窗口状态。实现可记录错误，并在下一次报警或短延迟后重试常闭；默认至少记录明显 `Error`。

## 恢复流程

| 步骤 | 动作 |
| --- | --- |
| 1 | 门目标活动摄像头集合清空。 |
| 2 | 创建恢复任务。 |
| 3 | 通过 `DeviceSdkDispatcher` 以 `Critical` 优先级投递。 |
| 4 | SDK 网关调用恢复普通受控命令。 |
| 5 | 成功后清理门目标恢复状态。 |
| 6 | 失败时按 `RestoreRetryIntervalMs` 延迟重试。 |

恢复任务对可重试错误（设备离线、网络抖动、SDK 超时等）**持续重试，不设最大次数**，直到成功或被新报警窗口重置。设备恢复常闭是实时安全动作，门必须尽快恢复到普通受控状态。

只有不可重试错误（如非法门号等配置类错误）才转终态并记录 Error 日志，提示运维人工确认配置。恢复失败重试不得在 worker 中等待，必须通过延迟调度重新投递。

## 服务停止清理

服务停止时必须处理仍处于活动状态的门目标：

| 步骤 | 动作 |
| --- | --- |
| 1 | 停止接收新的 AIOP 事件。 |
| 2 | 读取所有活动门目标。 |
| 3 | 投递 best-effort 恢复任务。 |
| 4 | 等待短时间。 |
| 5 | 记录成功、失败和未完成目标。 |
| 6 | 继续服务停止流程。 |

停止清理不能无限等待。若恢复失败，日志必须清楚列出门目标，方便人工处理。

## 离线设备处理

| 场景 | 常闭 | 恢复 |
| --- | --- | --- |
| 门禁设备离线 | 记录失败，不进入阶段 6 补偿表。 | 记录失败并按恢复重试策略重试。 |
| 设备正在重连 | 可延迟短时间投递。 | 以 `Critical` 延迟重试。 |
| 设备停用 | 记录终态失败，提示人工确认。 | 记录终态失败，提示人工确认。 |

阶段 9 不使用 `device_operation_retry_states`，因为门控窗口是实时安全动作，不适合在设备长时间离线后按旧窗口补偿执行。

## 日志

| 事件 | 内容 |
| --- | --- |
| 常闭投递 | targetKey、deviceId、doorNo、cameraKeys、taskId。 |
| 常闭成功 | targetKey、durationMs。 |
| 常闭失败 | targetKey、sdkErrorCode、retryable。 |
| 恢复投递 | targetKey、attempt、taskId。 |
| 恢复成功 | targetKey、attempt、durationMs。 |
| 恢复失败 | targetKey、attempt、nextRetryAt。 |
| 停止恢复 | activeTargetCount、success、failed。 |

## 不做的事

| 不做内容 | 原因 |
| --- | --- |
| 不恢复为常开 | 用户已确认窗口结束恢复普通受控状态。 |
| 不写补偿表 | 门控窗口是实时状态，不做离线补偿。 |
| 不阻塞 worker 等重试 | 延迟调度负责重试。 |
| 不直接调用 SDK | 必须通过设备通道。 |

## 测试

| 测试 | 验证 |
| --- | --- |
| 常闭任务 | SDK 命令和门号正确。 |
| 恢复任务 | 恢复命令和优先级正确。 |
| 恢复失败重试 | 按配置次数和间隔重试。 |
| 离线恢复 | 不写补偿表，记录错误。 |
| 停止清理 | 活动门目标收到 best-effort 恢复。 |
| 优先级 | 恢复优先于普通状态检测。 |
