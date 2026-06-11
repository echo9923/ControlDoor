# 阶段 7 / 任务 06：face_event_checkpoint 与历史补偿

## 目标

实现设备恢复在线后的历史 ACS 事件补偿。阶段 7 使用现有 `dbo.face_event_checkpoint` 按设备 IP 记录补偿断点，成功处理历史事件后前移断点，失败时不前移。

## 触发时机

| 场景 | 行为 |
| --- | --- |
| 服务启动设备已在线 | 可按配置执行一次历史补偿。 |
| 设备从离线恢复在线 | 触发该设备历史补偿。 |
| 手动重连成功 | 可触发该设备历史补偿。 |
| 配置关闭 | `HistoryCompensationEnabled = false` 时不触发。 |
| 设备在排除列表 | 不触发。 |

## checkpoint 语义

`face_event_checkpoint` 字段固定：

| 字段 | 语义 |
| --- | --- |
| `DeviceIP` | 设备 IP，主键。 |
| `LastSerialNo` | 已成功补偿到的最后事件流水。 |
| `LastEventTime` | 已成功补偿到的最后事件时间。 |
| `UpdatedAt` | 断点更新时间。 |

若设备 IP 变更，旧 IP 的 checkpoint 不自动迁移。实施时应在设备变更流程或现场运维中明确处理。

## 初始断点

| 场景 | 规则 |
| --- | --- |
| 已有 checkpoint | 从 `LastSerialNo/LastEventTime` 之后查询。 |
| 无 checkpoint | 默认从当前时间开始建点，避免首次启动拉取大量历史事件。 |
| 需要全量补偿 | 作为现场手动运维动作单独触发，不作为默认行为。 |

默认不做全量历史回溯，避免首次上线对设备和数据库造成不可控压力。

## 查询任务

历史事件查询必须通过设备固定执行通道：

| 字段 | 值 |
| --- | --- |
| `OperationName` | `QueryHistoryEvents`。 |
| `Priority` | `Low`。 |
| `RequiresOnline` | `true`。 |
| `TaskKey` | `deviceId + checkpoint范围`。 |
| `TimeoutMs` | 使用历史查询配置或设备任务默认超时。 |

底层可使用 ACS 事件远程配置查询，具体 `NET_DVR_ACS_EVENT_COND`、`NET_DVR_ACS_EVENT_CFG` 字段和命令号在编码时必须按本地 SDK 文档和头文件确认。

## 批次流程

| 步骤 | 动作 |
| --- | --- |
| 1 | 读取设备 checkpoint。 |
| 2 | 生成历史查询条件。 |
| 3 | 投递 `QueryHistoryEvents` 设备任务。 |
| 4 | 设备任务返回最多 `HistoryBatchSize` 条历史事件。 |
| 5 | 逐条走阶段 7.3-7.5 的解析、图片、入库流程。 |
| 6 | 重复事件视为处理成功。 |
| 7 | 批次全部处理成功后，用最大流水和最大事件时间更新 checkpoint。 |
| 8 | 若返回数量达到批量上限，继续下一批。 |
| 9 | 任一不可忽略失败发生时停止本轮，不前移 checkpoint。 |

## 实时缓冲关系

同一设备历史补偿进行中时，实时事件进入内存缓冲。历史补偿完成后，按接收顺序释放实时缓冲事件。实时缓冲策略详见任务 7.7。

## checkpoint 更新

| 场景 | 处理 |
| --- | --- |
| 批次全部成功 | upsert checkpoint。 |
| 批次只有重复事件 | 可前移 checkpoint。 |
| 部分事件入库失败 | 不前移 checkpoint。 |
| 查询设备失败 | 不前移 checkpoint。 |
| 服务停止 | 不前移未完成批次 checkpoint。 |

更新 checkpoint 不要求和事件 insert 在同一事务中跨批次执行；正确性依赖事件表唯一键兜底。即使 checkpoint 更新失败，下次会重复补偿，重复事件由 `attendance_gate_v2.id` 防重。

## 失败退避

阶段 7 不使用 `device_operation_retry_states` 保存历史补偿失败。失败后由内存延迟调度或下次设备恢复/扫描再触发：

| 失败 | 行为 |
| --- | --- |
| 设备离线 | 停止本轮，等待下次在线触发。 |
| 查询超时 | 记录错误，延迟后重试。 |
| 入库失败 | 不前移 checkpoint，延迟后重试。 |
| 解析失败单条 | 若无法形成有效事件，可记录并跳过；是否前移取决于是否能确认该条不应入库。 |

## 不做的事

| 不做内容 | 原因 |
| --- | --- |
| 不新增锁字段 | 现有 checkpoint 表结构冻结。 |
| 不多实例协调 | 当前按单 Windows Service 实例设计。 |
| 不默认全量回溯 | 避免设备和数据库压力。 |
| 不用设备操作补偿表 | 历史事件补偿有独立 checkpoint。 |

## 测试

| 测试 | 验证 |
| --- | --- |
| 无 checkpoint 初始化 | 默认从当前时间建点。 |
| 有 checkpoint 查询 | 查询条件从断点之后开始。 |
| 批次成功 | checkpoint 前移。 |
| 重复事件 | 视为成功并可前移。 |
| 入库失败 | checkpoint 不前移。 |
| 查询失败 | checkpoint 不前移。 |
| 多批次 | 返回达到 batch size 时继续下一批。 |
