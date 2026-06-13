# 阶段 7 / 任务 06：客户端布防与离线事件上传补偿

## 目标

实现设备恢复在线后的 ACS 离线事件上传补偿。阶段 7 只采用客户端布防方式：布防参数 `byDeployType = 0`，设备恢复后通过 SDK 报警回调补上传离线期间产生的 `COMM_ALARM_ACS` 事件，服务端通过 `byCurrentEvent == 2` 识别离线事件并写入 `dbo.attendance_gate_v2`。

阶段 7 不实现平台主动拉取历史事件，不调用 `NET_DVR_GET_ACS_EVENT`，不投递 `QueryHistoryEvents`，不读写 `dbo.face_event_checkpoint`。

## 布防方式

| 项目 | 要求 |
| --- | --- |
| SDK 接口 | `NET_DVR_SetupAlarmChan_V41`。 |
| 布防参数 | `NET_DVR_SETUPALARM_PARAM.byDeployType = 0`。 |
| 配置语义 | 如保留 `AlarmDeployType` 配置，阶段 7 只允许值 `0`；未配置或非法值回退为 `0`。 |
| 补偿来源 | 设备恢复在线后通过 `COMM_ALARM_ACS` 回调上传离线事件。 |
| 离线标记 | `NET_DVR_ACS_EVENT_INFO_EXTEND.byCurrentEvent == 2`。 |

`byDeployType = 1` 的实时布防和平台主动历史查询方案不在阶段 7 实施范围内。

## 离线事件识别

回调解析 `NET_DVR_ACS_ALARM_INFO` 时，应在扩展结构可用时读取 `NET_DVR_ACS_EVENT_INFO_EXTEND.byCurrentEvent`：

| `byCurrentEvent` | 处理 |
| --- | --- |
| `2` | 标记为设备上传的离线 ACS 事件，继续入队处理。 |
| `0` 或不可用 | 按普通实时 ACS 事件处理。 |
| 其他值 | 保留原值写入 raw payload，按实时事件处理或记录 debug，具体以 SDK 文档语义为准。 |

如果 `OfflineCompensationEnabled = false`，`byCurrentEvent == 2` 的事件应记录后忽略；普通实时事件仍可处理。

## 回调处理流程

| 步骤 | 动作 |
| --- | --- |
| 1 | 设备登录成功后执行客户端布防，确保 `byDeployType = 0`。 |
| 2 | SDK 回调收到 `COMM_ALARM_ACS`。 |
| 3 | 回调线程复制报警结构、抓拍图片和来源设备信息。 |
| 4 | 读取扩展字段 `byCurrentEvent`，判断是否为离线事件上传。 |
| 5 | 过滤非人脸/门禁认证事件。 |
| 6 | 构造 `RawAcsAlarmEvent`，将 `Source` 标记为 `Realtime` 或 `OfflineUpload`。 |
| 7 | 投递 `FaceEventIngestionService` 队列，回调线程立即返回。 |
| 8 | 后台消费者复用阶段 7.3-7.5 的解析、图片保存和入库逻辑。 |
| 9 | 唯一键冲突按重复事件处理，不重复写入。 |

## raw payload 要求

离线事件上传应在 `attendance_gate_v2.raw_payload` 中保留足够定位信息：

| 字段 | 说明 |
| --- | --- |
| `source` | `OfflineUpload`。 |
| `byCurrentEvent` | 原始扩展字段值。 |
| `alarmDeployType` | 当前布防类型，阶段 7 应为 `0`。 |
| `deviceId` | 运行时设备 ID。 |
| `deviceIp` | 来源设备 IP。 |
| `sdkCommand` | `COMM_ALARM_ACS`。 |
| `eventIdGenerated` | 是否使用兜底事件 ID。 |

## 防重复

离线事件上传和实时事件走同一张表、同一个业务流水防重规则：

| 场景 | 处理 |
| --- | --- |
| 设备重复上传同一离线事件 | 命中 `attendance_gate_v2.id` 唯一语义，返回 Duplicate。 |
| 同一事件曾被实时回调写入 | 离线上传再次到达时按 Duplicate 处理。 |
| 设备未提供稳定流水 | 使用确定性兜底 ID，并在 raw payload 标记。 |

重复事件不应视为补偿失败。

## 失败边界

| 场景 | 处理 |
| --- | --- |
| 布防失败 | 由阶段 4 布防重试机制继续重试；阶段 7 不主动查询历史事件兜底。 |
| 设备未上传离线事件 | 记录现场风险；阶段 7 不通过 checkpoint 主动补拉。 |
| 回调队列已满 | 按任务 7.2 的队列降级策略处理。 |
| 数据库连接失败 | 返回可重试失败并由事件消费重试策略处理；不写 checkpoint。 |
| 服务停止 | 尽量处理已入队事件；未入队或设备未上传的离线事件依赖设备下次恢复后再次上传能力。 |

## 不做的事

| 不做内容 | 原因 |
| --- | --- |
| 不调用 `NET_DVR_GET_ACS_EVENT` | 阶段 7 已限定为客户端布防离线事件上传。 |
| 不实现 `QueryHistoryEvents` | 不做平台主动历史查询。 |
| 不读写 `face_event_checkpoint` | 离线补偿进度由设备上传语义承担，服务端只做事件防重和入库。 |
| 不维护历史查询断点 | 没有主动拉取批次，也就没有断点前移语义。 |
| 不新增事件暂存表 | 保持阶段 7 表结构零变更。 |

## 测试

| 测试 | 验证 |
| --- | --- |
| 客户端布防参数 | `NET_DVR_SETUPALARM_PARAM.byDeployType` 使用 `0`。 |
| 离线事件识别 | `byCurrentEvent == 2` 被标记为 `OfflineUpload`。 |
| 离线事件入库 | mock 离线 ACS 回调写入 `attendance_gate_v2`。 |
| 离线补偿关闭 | `OfflineCompensationEnabled = false` 时忽略 `byCurrentEvent == 2`。 |
| 重复离线事件 | 第二次写入返回 Duplicate，不重复入库。 |
| 主动查询禁用 | 不调用 `NET_DVR_GET_ACS_EVENT`，不读写 `face_event_checkpoint`。 |
