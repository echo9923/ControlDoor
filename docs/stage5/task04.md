# 阶段 5 / 任务 04：DeleteFaces 与 DeletePersons

## 目标

实现 `/permission.PermissionSyncService/DeleteFaces` 和 `/permission.PermissionSyncService/DeletePersons`，按员工编号删除设备端人脸或人员信息。两个接口请求格式一致，设备端操作不同。

## 请求格式

支持：

- 字符串数组
- 对象数组
- `{ "items": [...] }`
- `{ "records": [...] }`
- 单个对象

员工编号字段别名：

| 字段 | 说明 |
| --- | --- |
| `employee_id` | 员工编号。 |
| `employeeId` | 别名。 |
| `employee_no` | 别名。 |
| `employeeNo` | 别名。 |

## DeleteFaces 流程

| 步骤 | 动作 |
| --- | --- |
| 1 | 解析员工编号列表。 |
| 2 | 校验批量上限和空值。 |
| 3 | 对在线设备投递删除人脸任务。 |
| 4 | 离线或可重试失败产生人脸删除补偿意图。 |
| 5 | 汇总员工/设备维度结果。 |

## DeletePersons 流程

| 步骤 | 动作 |
| --- | --- |
| 1 | 解析员工编号列表。 |
| 2 | 校验批量上限和空值。 |
| 3 | 对在线设备先 best-effort 删除该员工人脸。 |
| 4 | 前置删除人脸失败时记录 Warn 日志，继续删除人员。 |
| 5 | 对在线设备投递删除人员任务。 |
| 6 | 只有最终删除人员离线或可重试失败时，才产生人员删除补偿意图。 |
| 7 | 汇总员工/设备维度结果。 |

`DeletePersons` 以删除人员为主目标。设备端人脸不存在、前置删除人脸超时或 SDK 通讯失败，都不能阻断后续 `DeletePersonAsync`；服务必须记录 `Warn`，字段至少包含 `operationName=DeleteFaceBeforePerson`、`employeeId`、`userId` 和异常摘要。独立调用 `DeleteFaces` 时仍保持原语义：删除人脸本身失败会按设备和员工写入删除人脸补偿意图。

## 响应字段

| 字段 | DeleteFaces | DeletePersons |
| --- | --- | --- |
| `total` | 员工总数 | 员工总数 |
| `succeeded` | 成功数量 | 成功数量 |
| `failed` | 失败数量 | 失败数量 |
| `queued` | 离线排队数量 | 离线排队数量 |
| `targetDevices` | 目标设备数量 | 目标设备数量 |
| `queuedDetails` | 排队明细 | 排队明细 |
| `items` | 每个员工的人脸删除结果 | 每个员工的人员删除结果 |

## 设备端结果处理

| 场景 | 处理 |
| --- | --- |
| 设备返回不存在 | 可按成功或 skipped 处理，响应中说明。 |
| 设备离线 | 进入补偿意图。 |
| 参数错误 | 直接失败，不补偿。 |
| SDK 可重试失败 | 进入补偿意图。 |
| `DeletePersons` 前置删人脸失败 | 记录 Warn，继续删除人员，不单独写删除人员补偿。 |
| 部分设备成功 | 返回 `PARTIAL_SUCCESS`。 |

## 不做的事

| 不做内容 | 原因 |
| --- | --- |
| 不删除 `system_users` | 只删除设备端人员/人脸。 |
| 不清理历史事件 | 历史记录属于事件数据。 |
| 不实现补偿扫描 | 阶段 6 负责。 |

## 测试

| 测试 | 验证 |
| --- | --- |
| 字符串数组 | 正确解析员工编号。 |
| 对象数组 | 正确识别别名。 |
| 删除不存在 | 返回成功或 skipped，语义稳定。 |
| 离线设备 | queuedDetails 正确。 |
| 删除人员顺序 | 先 best-effort 删人脸，再删除人员。 |
| 删除人员前置删人脸失败 | 仍删除人员，接口成功，Warn 日志包含员工、userId 和异常摘要。 |
| 部分失败 | 返回 `PARTIAL_SUCCESS` 和设备明细。 |
