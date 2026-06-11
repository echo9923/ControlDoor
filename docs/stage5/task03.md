# 阶段 5 / 任务 03：人员与人脸同步 SyncPersons

## 目标

实现 `/permission.PermissionSyncService/SyncPersons`，向在线设备下发人员基础信息，并在包含人脸图片时下发人脸。人员必须先于人脸下发。

## 请求字段

| 字段 | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `employee_id` | `employeeId`、`employee_no`、`employeeNo` | 是 | 员工编号。 |
| `name` | `full_name`、`fullName` | 否 | 姓名。 |
| `gender` | `sex` | 否 | 性别。 |
| `enabled` | `active`、`is_active` | 否 | 默认 true。 |
| `valid_from` | `validFrom` | 否 | 有效期开始。 |
| `valid_to` | `validTo` | 否 | 有效期结束。 |
| `face_image_base64` | `faceImageBase64`、`face_base64`、`faceBase64`、`face_image` | 否 | 人脸图片，支持 data URI。 |
| `face_image_format` | `faceImageFormat` | 否 | 图片格式。 |

## 校验规则

| 项目 | 规则 |
| --- | --- |
| 批量数量 | 最大 500。 |
| 员工编号 | 非空。 |
| 有效期 | 可空；如果都存在，开始时间不得晚于结束时间。 |
| 人脸 Base64 | 支持 data URI 前缀，解码失败返回 `INVALID_ARGUMENT`。 |
| 人脸大小 | 超过 200KB 返回 `FACE_TOO_LARGE`。 |
| 图片格式 | 仅用于日志和设备参数，实际格式以图片头和设备要求为准。 |

## 执行流程

| 步骤 | 动作 |
| --- | --- |
| 1 | 解析请求容器 `people/items/records/data` 或单对象。 |
| 2 | 标准化人员字段和人脸图片。 |
| 3 | 获取目标在线设备。 |
| 4 | 对每台设备先投递人员下发任务。 |
| 5 | 人员成功后，如有人脸，再投递人脸下发任务。 |
| 6 | 人员失败时，不继续下发该员工在该设备的人脸。 |
| 7 | 离线或可重试失败产生补偿意图。 |
| 8 | 汇总人员、设备、人脸成功失败明细。 |

## 设备端实现边界

| 能力 | 说明 |
| --- | --- |
| 人员下发 | 可用 ISAPI 或远程配置，具体协议在编码时按 SDK 文档确认。 |
| 人脸下发 | 可用 `NET_DVR_FACE_PARAM_*` 远程配置或 ISAPI。 |
| 设备能力 | 若设备不支持人脸下发，返回设备维度失败。 |
| 日志 | 人脸 Base64 是否完整记录由配置控制，本地自用场景可按配置完整记录。 |

## 响应字段

| 字段 | 说明 |
| --- | --- |
| `total` | 人员总数。 |
| `succeeded` | 成功人数。 |
| `failed` | 失败人数。 |
| `queued` | 离线排队数量。 |
| `facesUploaded` | 已下发人脸数量。 |
| `targetDevices` | 目标设备数量。 |
| `queuedDetails` | 排队明细。 |

## 不做的事

| 不做内容 | 原因 |
| --- | --- |
| 不扫描补偿表 | 阶段 6 负责。 |
| 不采集人脸 | `CaptureFaceStream` 负责。 |
| 不修改人员表结构 | 只使用既有字段。 |

## 测试

| 测试 | 验证 |
| --- | --- |
| 容器别名 | people/items/records/data/单对象均可解析。 |
| 人脸过大 | 返回 `FACE_TOO_LARGE`。 |
| data URI | 正确去除前缀并解码。 |
| 人员先于人脸 | mock 调用顺序正确。 |
| 人员失败 | 不下发人脸。 |
| 部分设备失败 | 响应包含设备维度明细。 |
