# 阶段 9 / 任务 02：AIOP 回调识别与载荷解析

## 目标

实现摄像头 AIOP 报警回调识别。阶段 9 关注 `lCommand = 0x4021`，对应 `COMM_UPLOAD_AIOP_VIDEO`，`pAlarmInfo` 按 `NET_AIOP_VIDEO_HEAD` 解析，提取 AIOP JSON 和 JPEG 图片摘要用于日志和联调诊断。

## 文档依据

本任务以 `docs/海康AIOP短衣短裤报警SDK布防回调说明.md` 和本地 SDK 头文件为依据。已验证事实：

| 项目 | 结论 |
| --- | --- |
| SDK 命令 | `0x4021`。 |
| 命令名 | `COMM_UPLOAD_AIOP_VIDEO`。 |
| 结构 | `NET_AIOP_VIDEO_HEAD`。 |
| 载荷 | 头部 + AIOP JSON + JPEG。 |
| 回调模式 | SDK 布防后设备主动推送。 |

本地旧 V50 文档未列出 `0x4021`，实施时仍以现场验证和头文件为准。

## 输入

| 字段 | 说明 |
| --- | --- |
| `lCommand` | 回调命令。 |
| `NET_DVR_ALARMER` | 来源摄像头信息。 |
| `pAlarmInfo` | 指向 AIOP 结构和可变载荷。 |
| `dwBufLen` | 总 buffer 长度。 |
| `lUserID` | 可用于 IP 为空时反查设备。 |

## 回调线程规则

| 规则 | 说明 |
| --- | --- |
| 只复制 buffer | 回调线程复制 `pAlarmInfo` 原始字节。 |
| 不解析大 JSON | 解析放到后台任务。 |
| 不写文件 | 阶段 9 不保存 AIOP 图片文件。 |
| 不控制门 | 回调线程不直接调用 `NET_DVR_ControlGateway`。 |
| 不阻塞 | 队列满时记录并降级。 |

## 来源摄像头识别

优先级：

| 优先级 | 来源 |
| --- | --- |
| 1 | `NET_DVR_ALARMER.sDeviceIP`。 |
| 2 | `lUserID` 反查设备运行时。 |
| 3 | 设备序列号反查。 |

只有来源命中 `CameraAlarmDoorInterlock.Mappings` 中的摄像头，才进入联动流程。未命中只记录日志。

## 解析内容

`AiopVideoPayloadParser` 输出：

| 字段 | 说明 |
| --- | --- |
| `Command` | `0x4021`。 |
| `CameraDeviceId` | 来源摄像头设备 ID。 |
| `CameraIp` | 来源摄像头 IP。 |
| `TaskId` | AIOP 任务 ID，如能解析。 |
| `JsonLength` | JSON 字节长度。 |
| `JsonText` | 可选完整 JSON，受日志配置控制。 |
| `ModelId` | 模型 ID，如能解析。 |
| `DetectedTypes` | JSON 中目标类型列表，仅用于日志。 |
| `ImageLength` | 图片字节长度。 |
| `ImageIsJpeg` | 是否 JPEG 签名。 |
| `ParseSucceeded` | 解析是否成功。 |
| `ParseError` | 解析失败原因。 |

## 触发规则

| 场景 | 是否触发联动 |
| --- | --- |
| `0x4021` 且摄像头命中配置 | 是。 |
| JSON 解析失败但摄像头命中配置 | 是。 |
| JSON 中 type 不是短袖/短裤 | 是，不按 type 过滤。 |
| 来源摄像头未命中配置 | 否，只记录日志。 |
| 非 `0x4021` 回调 | 否。 |

## 日志

| 事件 | 内容 |
| --- | --- |
| AIOP 命中 | cameraId、cameraIp、command、jsonLength、imageLength、targetCount。 |
| 解析失败 | cameraId、cameraIp、错误原因、bufferLength。 |
| 未命中映射 | cameraIp、command、原因。 |
| 队列满 | 丢弃数量、cameraIp、command。 |

完整 JSON 是否记录由配置控制。本地自用可以开启完整记录；默认记录摘要即可。

## 不做的事

| 不做内容 | 原因 |
| --- | --- |
| 不按 type 过滤 | 用户已确认命中配置摄像头即联动。 |
| 不保存 AIOP 图片 | 阶段 9 只需要触发门控和诊断摘要。 |
| 不写数据库 | 阶段 9 默认使用内存窗口状态。 |
| 不在回调线程控制门 | 必须走设备通道。 |

## 测试

| 测试 | 验证 |
| --- | --- |
| `0x4021` 识别 | 进入 AIOP 队列。 |
| 非 `0x4021` | 不进入联动。 |
| IP 识别 | 通过 `sDeviceIP` 匹配摄像头。 |
| UserID 识别 | IP 为空时可反查。 |
| JSON 解析失败 | 仍触发联动。 |
| type 非目标 | 仍触发联动。 |
| 未命中配置 | 不触发门控。 |
