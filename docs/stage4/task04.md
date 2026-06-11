# 阶段 4 / 任务 04：状态检测与重连策略

## 目标

为在线和离线设备建立可控的状态检测、失败判定、自动重连和手动重连机制。所有检测和重连都必须通过设备固定执行通道，不允许业务线程直接调用 SDK。

## 状态枚举

| 状态 | 含义 |
| --- | --- |
| `Loaded` | 已加载但未尝试登录。 |
| `Connecting` | 正在登录。 |
| `Online` | 登录成功，UserID 有效。 |
| `Offline` | 登录失败或检测失败。 |
| `ReconnectPending` | 已安排延迟重连。 |
| `Disconnecting` | 正在撤防/登出。 |
| `Disconnected` | 手动断开。 |
| `InvalidConfig` | 配置非法，不参与自动重连。 |
| `Deleted` | 已从运行时移除。 |

## 状态检测任务

| 字段 | 说明 |
| --- | --- |
| `OperationName` | `DeviceStatusCheck`。 |
| `Priority` | `Low` 或 `Normal`。 |
| `Interval` | `DeviceConnection.StatusCheckIntervalMs`。 |
| `TimeoutMs` | `DeviceConnection.StatusCheckTimeoutMs`。 |

检测方式按实现能力逐步增强：

1. 首期可通过轻量 SDK 状态/能力接口或低风险 ISAPI 查询判断在线。
2. 如果缺少可靠状态接口，允许以登录会话有效性和最近 SDK 调用结果作为状态依据。
3. 状态检测失败不得立即强制登出，先按失败次数和错误分类进入重连策略。

## 重连策略

| 配置 | 说明 |
| --- | --- |
| `ReconnectInitialDelayMs` | 首次重连延迟。 |
| `ReconnectMaxDelayMs` | 最大退避延迟。 |
| `ReconnectBackoffFactor` | 指数退避倍数。 |
| `ReconnectMaxAttempts` | 最大连续自动重连次数。 |
| `ReconnectCooldownMs` | 达到上限后的冷却时间。 |
| `ManualReconnectBypassCooldown` | 手动重连是否绕过冷却。 |

## 自动重连流程

| 步骤 | 动作 |
| --- | --- |
| 1 | SDK 调用或状态检测失败后分类为可重试。 |
| 2 | 更新内存状态为 `ReconnectPending`。 |
| 3 | 通过 `DelayedDeviceTaskScheduler` 安排重连任务。 |
| 4 | 到期后进入设备 worker。 |
| 5 | 如仍有旧 UserID，先撤防和登出。 |
| 6 | 调用登录任务。 |
| 7 | 成功后清零连续失败计数；失败则计算下一次退避。 |

## 手动重连

`ReconnectDevice` gRPC 调用生成手动重连任务：

| 参数 | 行为 |
| --- | --- |
| `force=false` | 如果设备已在线，直接返回当前在线状态；离线时安排立即重连。 |
| `force=true` | 即使在线也执行撤防、登出、重新登录、必要时重新布防。 |

## 不做的事

| 不做内容 | 原因 |
| --- | --- |
| 不写补偿状态 | 离线写操作补偿属于阶段 6。 |
| 不修改 `dbo.devices.status` | 该字段表示启用，不表示在线。 |
| 不强杀 native 调用 | SDK 调用不可安全中断，超时只记录风险。 |
| 不在 worker 中 sleep | 延迟重连由调度器完成。 |

## 测试

| 测试 | 验证 |
| --- | --- |
| 检测成功 | 在线设备更新时间和状态。 |
| 检测失败 | 记录错误并安排重连。 |
| 指数退避 | 多次失败后 delay 增长且不超过最大值。 |
| 达到上限 | 进入冷却，不无限重试。 |
| 手动重连 | 可绕过冷却并立即入队。 |
| 强制重连 | 在线设备先清理旧会话再登录。 |
| 停止服务 | 已安排重连任务被取消或安全忽略。 |
