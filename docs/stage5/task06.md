# 阶段 5 / 任务 06：CaptureFaceStream 与 GetEnrollmentStatus

## 目标

实现人脸采集流式接口 `/permission.PermissionSyncService/CaptureFaceStream` 和采集状态查询 `/permission.PermissionSyncService/GetEnrollmentStatus`。采集结果以 `string -> stream string` 返回，成功和失败都至少返回一帧 JSON。

## CaptureFaceStream 请求

| 字段 | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `employee_id` | `employeeId`、`employee_no`、`employeeNo` | 是 | 员工编号。 |

## 流式响应

成功至少返回一帧：

| 字段 | 说明 |
| --- | --- |
| `taskId` | 采集任务 ID。 |
| `employeeId` | 员工编号。 |
| `frameIndex` | 帧序号。 |
| `faceImageBase64` | 人脸图片。 |
| `faceImageFormat` | 图片格式。 |
| `qualityScore` | 可空。 |
| `recommend` | 是否推荐使用。 |
| `requestId` | 请求 ID。 |
| `success` | true。 |
| `code` | `OK`。 |
| `message` | 说明。 |
| `errors` | 空数组。 |
| `errorDetails` | 空数组。 |

失败也返回一帧，`success=false`，常见 `code` 为 `DEVICE_ERROR`、`FACE_TOO_LARGE`、`INVALID_ARGUMENT`。

## 采集流程

| 步骤 | 动作 |
| --- | --- |
| 1 | 解析员工编号。 |
| 2 | 创建内存采集任务，生成 taskId。 |
| 3 | 选择录入设备，必须在线。若 `Devices.DefaultFaceCaptureDeviceId` 已配置，则固定使用该设备，且该设备离线时严格失败、不回退到其他设备；留空时维持"按 `FaceCapture` 类型取第一个在线设备"的旧行为。 |
| 4 | 投递采集设备任务。 |
| 5 | 设备返回图片后校验大小不超过 200KB。 |
| 6 | 写入任务状态。 |
| 7 | 向 stream 写出一帧成功或失败 JSON。 |

### 底层采集 SDK

采集走海康明眸专用接口 `NET_DVR_CAPTURE_FACE_INFO`（命令字 2510），不再使用通用 JPEG 抓拍近似：

| 项目 | 规则 |
| --- | --- |
| SDK 命令字 | `NET_DVR_CAPTURE_FACE_INFO = 2510`（`NET_DVR_StartRemoteConfig`）。 |
| 入参结构 | `NET_DVR_CAPTURE_FACE_COND`（`dwSize + byRes[128]`）。 |
| 出参结构 | `NET_DVR_CAPTURE_FACE_CFG`（图片、质量分 `byFaceQuality1`、进度 `byCaptureProgress`、人脸模板与红外图保留字段）。 |
| 采集方式 | `NET_DVR_GetNextRemoteConfig` 轮询，`byCaptureProgress==100` 且状态 `SUCCESS(1000)` 才取图。 |
| 质量分 | 取设备真实 `byFaceQuality1`（1-100），不再硬编码。 |
| 内存管理 | 只释放托管侧 `AllocHGlobal` 的条件结构；`pFacePicBuffer` 等 SDK 指针由 `NET_DVR_StopRemoteConfig` 释放。 |
| 轮询参数 | 最多 100 次，每次 `NEED_WAIT(1001)` 间隔 100ms，累计约 10 秒超时。 |
| 超时语义 | 循环跑满仍未采到人脸，`Wrapper` 抛 `FACE_CAPTURE_TIMEOUT`。 |
| 实现分层 | `HikvisionSdkNativeClient.CaptureFace` 负责 P/Invoke 与轮询；`HikvisionSdkWrapper.CaptureFaceAsync` 负责状态码映射与异常包装。 |


## GetEnrollmentStatus

| 字段 | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `taskId` | `task_id` | 是 | 采集任务 ID。 |

响应业务字段：

| 字段 | 说明 |
| --- | --- |
| `taskId` | 任务 ID。 |
| `employeeId` | 员工编号。 |
| `action` | 任务动作。 |
| `status` | 任务状态。 |
| `message` | 状态说明。 |
| `errorCode` | 错误码。 |

## 任务存储

| 项目 | 规则 |
| --- | --- |
| 存储位置 | 首期内存存储。 |
| 生命周期 | 按配置保留最近任务，超期清理。 |
| 服务重启 | 任务状态丢失，可返回 `NOT_FOUND`。 |
| 数据库 | 阶段 5 不新增表保存采集任务。 |

## 不做的事

| 不做内容 | 原因 |
| --- | --- |
| 不自动写入人员人脸 | 采集只返回图片，下发由 `SyncPersons`。 |
| 不持久化采集任务 | 首期内存即可。 |
| 不多帧连续视频 | 当前契约要求至少一帧结果。 |

## 测试

| 测试 | 验证 |
| --- | --- |
| 采集成功 | stream 返回一帧成功。 |
| 设备离线 | stream 返回一帧失败。 |
| 图片过大 | 返回 `FACE_TOO_LARGE`。 |
| 任务状态 | `GetEnrollmentStatus` 可查询成功/失败状态。 |
| taskId 不存在 | 返回 `NOT_FOUND`。 |
| 解析失败 | 返回 `INVALID_ARGUMENT` 单帧失败。 |
