# 阶段 7 / 任务 03：ACS 事件解析与标准模型

## 目标

把 `RawAcsAlarmEvent` 解析为标准 `AcsFaceEvent`，提取员工编号、事件时间、业务流水、方向、设备、卡号、认证结果、事件类型和原始载荷摘要，为图片保存和数据库入库提供稳定输入。

## 标准模型

建议内部使用 `AcsFaceEvent`：

| 字段 | 说明 |
| --- | --- |
| `EventId` | 写入 `attendance_gate_v2.id` 的业务流水。 |
| `EmployeeId` | 员工编号，写入 `username`。 |
| `Nickname` | 从 `system_users` 查询补全，可为空。 |
| `EventTime` | 事件时间，写入 `record_datetime`。 |
| `RecordDate` | 事件日期。 |
| `RecordTime` | 事件时间。 |
| `Direction` | 进出方向，无法识别时按既有默认值 1。 |
| `DeviceName` | 设备名称。 |
| `DeviceSn` | 设备序列号、IP 或运行时标识。 |
| `CardNo` | 卡号，可为空。 |
| `EventType` | 事件类型。 |
| `AuthResult` | 认证结果，用于 `process_message` 或 raw payload。 |
| `PictureBytes` | 抓拍图片。 |
| `RawPayload` | 原始结构摘要或 JSON。 |
| `Source` | `Realtime` 或 `History`。 |

## 字段来源

| 目标字段 | 来源 |
| --- | --- |
| `EventId` | 优先使用 ACS 事件流水号；没有稳定流水时用设备、时间、员工、事件类型生成确定性 long。 |
| `EmployeeId` | `NET_DVR_ACS_EVENT_INFO` 中的员工号/工号字段，或卡号兜底。 |
| `EventTime` | ACS 事件时间结构。 |
| `Direction` | ACS 事件中的进出方向字段；无法识别时默认 1。 |
| `DeviceName` | 阶段 4 运行时设备信息。 |
| `DeviceSn` | 设备序列号优先，其次设备 IP。 |
| `CardNo` | ACS 事件卡号字段。 |
| `PictureBytes` | ACS 报警图片指针复制内容。 |
| `RawPayload` | 解析出的关键字段 JSON 或原始摘要。 |

具体结构字段名和偏移必须在编码时以本地 SDK 文档、`HCNetSDK.h` 和 C# 示例为准。本任务固定解析边界和落库语义，不替代头文件确认。

## 事件过滤

阶段 7 只写入可以形成有效门禁进出记录的人脸/门禁认证事件。

| 场景 | 处理 |
| --- | --- |
| 员工编号为空且卡号为空 | 不入库，记录 `Warn`。 |
| 事件时间无效 | 使用设备上报时间失败时可用接收时间兜底，并在 raw payload 说明。 |
| 设备无法识别 | 不入库，记录 `Warn`。 |
| 非门禁 ACS 事件 | 可记录 debug，不写业务表。 |
| 认证失败事件 | 可入库，`process_message` 标记失败原因。 |

## EventId 生成规则

优先使用设备提供的稳定业务流水，保证历史补偿和实时回调能得到同一个 `id`。

兜底生成规则必须满足：

| 要求 | 说明 |
| --- | --- |
| 确定性 | 同一设备同一事件重复解析得到同一个值。 |
| 低碰撞 | 包含设备标识、事件时间、员工/卡号、事件类型。 |
| long 范围 | 结果必须适配 `BIGINT`。 |

建议只有在设备没有可用流水时才使用哈希兜底，并在日志中标记 `eventIdGenerated=true`。

## 昵称补全

| 步骤 | 动作 |
| --- | --- |
| 1 | 用 `EmployeeId` 查询或缓存 `system_users`。 |
| 2 | 找到昵称则填入 `nickname`。 |
| 3 | 找不到则 `nickname` 为空，不阻塞入库。 |

昵称补全只读 `system_users`，不修改人员表。

## raw payload

`attendance_gate_v2.raw_payload` 为 `VARCHAR(MAX)`，建议保存 UTF-8 JSON 字符串的 ASCII-safe 内容或经过数据库兼容处理的文本，包含：

| 字段 | 说明 |
| --- | --- |
| `source` | `Realtime` / `History`。 |
| `deviceId` | 设备 ID。 |
| `deviceIp` | 设备 IP。 |
| `eventType` | 事件类型。 |
| `authResult` | 认证结果。 |
| `sdkCommand` | 命令号。 |
| `eventIdGenerated` | 是否使用兜底 ID。 |

是否记录更多原始字段由日志和 payload 配置控制。

## 不做的事

| 不做内容 | 原因 |
| --- | --- |
| 不改变表字段映射 | 现有表结构冻结。 |
| 不依赖 AIOP JSON | ACS 和 AIOP 是不同事件来源。 |
| 不因昵称缺失失败 | 历史记录仍应可追踪员工编号。 |
| 不在解析阶段写文件或写库 | 后续任务负责。 |

## 测试

| 测试 | 验证 |
| --- | --- |
| 标准 ACS 成功事件 | 字段映射完整。 |
| 认证失败事件 | 可入库并标记失败。 |
| 员工为空 | 不入库并记录原因。 |
| 事件时间异常 | 接收时间兜底或明确失败。 |
| EventId 稳定 | 同一输入重复解析得到同一 ID。 |
| 昵称补全 | 找到用户时写 nickname，找不到不失败。 |
