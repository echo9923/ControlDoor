# 阶段 5 / 任务 07：阶段测试与验收

## 目标

定义阶段 5 的自动化测试、mock 设备集成测试、gRPC 契约测试和数据库兼容检查，证明权限、人员、人脸同步在不依赖真实设备的情况下可验证。

## 单元测试

| 测试类 | 覆盖内容 |
| --- | --- |
| `PermissionRequestParserTests` | `SyncPermissions` 容器解析、字段校验、批量上限。 |
| `PersonRequestParserTests` | `SyncPersons` 容器解析、字段别名、有效期、人脸 Base64。 |
| `EmployeeIdParserTests` | 删除和查询接口的字符串数组、对象数组、别名解析。 |
| `FaceImageValidatorTests` | data URI、Base64、格式、200KB 限制。 |
| `PermissionSyncPlannerTests` | 在线设备任务、离线补偿意图、跳过和失败分类。 |
| `EnrollmentTaskStoreTests` | taskId、状态更新、过期清理、NOT_FOUND。 |

## Mock SDK 集成测试

| 场景 | 预期 |
| --- | --- |
| 权限同步全部成功 | `updated=员工数`，无 queued。 |
| 权限同步部分离线 | 返回 `PARTIAL_SUCCESS` 和 queuedDetails。 |
| 人员人脸全部成功 | 人员先于人脸，facesUploaded 正确。 |
| 人员失败 | 不继续下发人脸。 |
| 人脸过大 | 返回 `FACE_TOO_LARGE`，不投递设备任务。 |
| 删除人脸部分失败 | items 包含设备维度错误。 |
| 查询人脸 | 返回每个员工/设备查询结果。 |
| 采集成功 | stream 返回一帧成功并可查询状态。 |
| 采集失败 | stream 返回一帧失败并可查询状态。 |

## gRPC 契约测试

| 方法 | 必测 |
| --- | --- |
| `SyncPermissions` | 数组、items、records、单对象。 |
| `SyncPersons` | people、items、records、data、单对象。 |
| `DeleteFaces` | 字符串数组、对象数组、items、records。 |
| `DeletePersons` | 同 DeleteFaces。 |
| `GetFaces` | 同 DeleteFaces。 |
| `CaptureFaceStream` | 成功/失败至少一帧 JSON。 |
| `GetEnrollmentStatus` | taskId、task_id 别名。 |

## 数据库兼容测试

| 测试 | 验证 |
| --- | --- |
| `system_users` 结构 | 字段、索引、触发器不变。 |
| 权限字段更新 | 只更新 `access_permission`、`last_synced_level`、`permission_updated_at`、`last_synced_at`。 |
| 不写事件表 | 阶段 5 不写 `attendance_gate_v2`。 |
| 不改设备清单 | 阶段 5 不修改 JSON 设备清单。 |
| 补偿接口边界 | 可生成补偿意图；持久化细节由阶段 6 测试覆盖。 |

## 阶段 5 通过标准

| 标准 | 说明 |
| --- | --- |
| gRPC 兼容 | 7 个权限/采集相关方法契约一致。 |
| 在线设备可执行 | 在线设备通过固定通道执行权限、人员、人脸、删除、查询、采集。 |
| 离线可排队 | 离线或可重试失败能生成补偿意图并返回 queued 明细。 |
| 人脸限制明确 | Base64、data URI、200KB 限制都有测试。 |
| 结果明细完整 | 员工、设备、错误码、排队原因可追踪。 |
| 数据库零结构变更 | 不修改现有表结构，不新增阶段 5 表。 |
