# 阶段 7 / 任务 01：阶段边界与任务总览

## 目标

阶段 7 实现门禁 ACS 人脸事件入库与离线事件上传补偿。服务通过阶段 3/4 已建立的报警回调和客户端布防能力接收 `COMM_ALARM_ACS`，解析 `NET_DVR_ACS_ALARM_INFO` 相关字段，保存抓拍图片，将实时事件和设备恢复后上传的离线事件写入现有 `dbo.attendance_gate_v2`。

阶段 7 只处理门禁/人脸设备 ACS 事件，不处理摄像头 AIOP 短衣短裤报警联动门禁常闭。

## 硬性边界

| 边界 | 要求 |
| --- | --- |
| 现有表零结构变更 | 不修改 `attendance_gate_v2` 和其他现有表结构。 |
| 不新增阶段 7 表 | 实时事件和离线事件上传补偿只使用 SDK 回调队列与现有事件表。 |
| 单一补偿方式 | 阶段 7 只采用客户端布防下的设备离线事件上传，识别 `byCurrentEvent == 2`。 |
| 不主动拉历史 | 不实现 `QueryHistoryEvents`，不使用 `NET_DVR_GET_ACS_EVENT` 主动查询历史事件，不读写 `face_event_checkpoint`。 |
| 回调线程轻量 | SDK 回调线程只复制必要数据并投递队列，不做数据库写入和复杂解析。 |
| 防重复依赖业务流水 | 使用 `attendance_gate_v2.id` 唯一语义避免重复入库。 |
| 不改变 gRPC 契约 | 阶段 7 不新增、不修改外部 gRPC 方法。 |
| 不做 AIOP 联动 | 摄像头 `COMM_UPLOAD_AIOP_VIDEO / 0x4021` 属于后续阶段。 |

## 任务拆分

| 任务 | 文件 | 主题 |
| --- | --- | --- |
| 7.1 | `task01.md` | 阶段边界与任务总览。 |
| 7.2 | `task02.md` | 回调接收与事件队列。 |
| 7.3 | `task03.md` | ACS 事件解析与标准模型。 |
| 7.4 | `task04.md` | 抓拍图片保存与 raw payload。 |
| 7.5 | `task05.md` | `attendance_gate_v2` 入库与防重复。 |
| 7.6 | `task06.md` | 客户端布防与离线事件上传补偿。 |
| 7.7 | `task07.md` | 队列缓冲、配置、测试与验收。 |

## 模块划分

| 模块 | 职责 |
| --- | --- |
| `FaceEventIngestionService` | 阶段 7 总入口，管理实时/离线上传事件队列和生命周期。 |
| `AcsAlarmEventRouter` | 从 SDK 原始回调识别 ACS 事件、反查设备、投递队列。 |
| `AcsEventParser` | 解析员工编号、事件时间、流水、方向、设备、认证结果和图片。 |
| `SnapshotStorage` | 保存抓拍图片，生成相对或绝对路径。 |
| `FaceEventRepository` | 写入 `attendance_gate_v2`、查询防重、补全昵称。 |
| `OfflineAcsEventPolicy` | 识别 `byCurrentEvent == 2` 离线事件，并按配置决定接收或忽略。 |

## 主流程

| 步骤 | 动作 |
| --- | --- |
| 1 | 阶段 4 完成设备登录和 ACS 客户端布防，`byDeployType` 固定使用 `0`。 |
| 2 | SDK 回调收到实时或设备恢复后上传的 `COMM_ALARM_ACS`。 |
| 3 | 回调线程复制报警结构、图片字节、来源设备信息。 |
| 4 | 识别 `byCurrentEvent`：普通实时事件直接入队，`byCurrentEvent == 2` 标记为离线上传事件后入队。 |
| 5 | 后台消费者解析为标准 `AcsFaceEvent`。 |
| 6 | 保存抓拍图片。 |
| 7 | 生成 `attendance_gate_v2` 记录并插入。 |
| 8 | 若业务 `id` 已存在，按重复事件处理，不重复入库。 |

## 事件来源区分

| 来源 | 处理 |
| --- | --- |
| 实时 ACS 回调 | 入实时队列，尽快解析入库。 |
| 离线 ACS 上传事件 | `byCurrentEvent == 2` 时走同一解析和入库管线，来源标记为 `OfflineUpload`。 |
| AIOP 报警回调 | 阶段 7 不处理，后续阶段处理。 |
| 非 ACS 命令 | 记录 debug 或忽略。 |
| 历史 ACS 主动查询 | 阶段 7 不采用，不实现 `QueryHistoryEvents`。 |

## 阶段完成标准

| 标准 | 说明 |
| --- | --- |
| 实时事件可入库 | mock ACS 回调能写入 `attendance_gate_v2`。 |
| 离线事件可入库 | mock `byCurrentEvent == 2` 回调能写入 `attendance_gate_v2`。 |
| 抓拍可保存 | 图片保存路径写入 `snapshot_path`。 |
| 重复不入库 | 同一业务流水 `id` 不产生重复记录。 |
| 布防类型固定 | 阶段 7 使用客户端布防，`AlarmDeployType/byDeployType` 必须为 `0`。 |
| 主动历史查询不启用 | 不读写 `face_event_checkpoint`，不调用 `NET_DVR_GET_ACS_EVENT`。 |
| 回调线程安全 | 回调线程不做耗时数据库和文件操作。 |
| 数据库兼容 | 不修改现有表结构，不新增阶段 7 表。 |
