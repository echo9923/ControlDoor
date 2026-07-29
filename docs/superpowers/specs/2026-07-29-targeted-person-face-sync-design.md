# 指定设备人员与人脸下发接口设计

## 背景

当前 `/permission.PermissionSyncService/SyncPersons` 接收批量人员数据，并自动选择全部启用的 `Acs` 设备。接口先下发人员；记录中包含人脸图片时，再向同一批设备上传人脸。在线设备立即执行，离线设备写入设备维度的补偿意图。

现有底层设备能力已经拆分为两个独立操作：

- `UpsertPersonAsync`：创建或更新设备端人员。
- `UploadFaceAsync`：向设备端已有人员上传人脸。

新需求是在不改变现有全设备接口语义的前提下，增加两个只操作调用方指定设备的批量接口，并明确拆分人员下发与人脸下发。

## 目标

新增两个 Unary gRPC 方法：

- `/permission.PermissionSyncService/SyncPersonsToDevices`：向指定设备批量下发人员，不上传人脸。
- `/permission.PermissionSyncService/SyncFacesToDevices`：向指定设备批量上传人脸，不创建或更新人员。

两个接口共用顶层 `deviceIds` 数组。同一次请求中的全部业务记录应用于相同的目标设备集合。

## 非目标

- 不修改现有 `SyncPersons` 的请求格式、目标设备选择、人员先于人脸顺序或数据库回写行为。
- 不新增数据库表或字段。
- 不在 `SyncFacesToDevices` 中查询、创建或更新设备端人员。
- 不为缺失或非法的 `deviceIds` 回退到全部设备。
- 不抽象通用的任意设备操作接口。

## 接口契约

### SyncPersonsToDevices

```json
{
  "deviceIds": [1, 3, 5],
  "items": [
    {
      "employee_id": "10001",
      "name": "张三",
      "gender": "male",
      "enabled": true,
      "valid_from": "2026-01-01T00:00:00",
      "valid_to": "2035-12-31T23:59:59"
    }
  ]
}
```

人员字段及别名沿用 `SyncPersons`：

| 字段 | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `employee_id` | `employeeId`、`employee_no`、`employeeNo` | 是 | 员工编号，按原始字符串处理。 |
| `name` | `full_name`、`fullName` | 否 | 姓名。 |
| `gender` | `sex` | 否 | 性别。 |
| `enabled` | `active`、`is_active` | 否 | 是否启用，默认 `true`。 |
| `valid_from` | `validFrom` | 否 | 有效期开始时间。 |
| `valid_to` | `validTo` | 否 | 有效期结束时间。 |

该接口不接受 `face_image_base64` 及其任何别名。请求包含人脸字段时返回 `INVALID_ARGUMENT`，避免调用方误认为人脸已经下发。

### SyncFacesToDevices

```json
{
  "deviceIds": [1, 3, 5],
  "items": [
    {
      "employee_id": "10001",
      "face_image_base64": "base64字符串",
      "face_image_format": "jpg"
    }
  ]
}
```

| 字段 | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `employee_id` | `employeeId`、`employee_no`、`employeeNo` | 是 | 设备端已有人员的员工编号，按原始字符串处理。 |
| `face_image_base64` | `faceImageBase64`、`face_base64`、`faceBase64`、`face_image` | 是 | 人脸图片 Base64，可带 data URI 前缀，解码后最大 200KB。 |
| `face_image_format` | `faceImageFormat` | 否 | 图片格式，仅用于日志和设备参数。 |

该接口只调用人脸上传操作。设备端不存在对应人员时，保留设备返回的失败结果，不自动下发人员。

### 通用设备字段

| 字段 | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `deviceIds` | `device_ids` | 是 | 非空正整数数组；按首次出现顺序去重。 |

所有目标设备必须同时满足：设备存在于运行时注册表、设备已启用、设备声明了 `Acs` 类型。

只要一个设备不满足要求，整次请求返回 `INVALID_ARGUMENT`。服务在完成全部设备和业务记录校验前不得投递设备任务或写入补偿意图。

## 执行流程

1. 解析 JSON 根对象。
2. 解析并校验 `deviceIds`，按首次出现顺序去重。
3. 解析业务记录，单批最多 500 条。
4. 从运行时注册表解析全部指定设备。
5. 原子校验全部设备及全部记录。
6. 将指定设备分为在线设备和离线设备。
7. 对在线设备逐员工投递对应的设备任务。
8. 对离线设备及在线可重试失败写入当前设备的补偿意图。
9. 汇总员工和设备维度结果。

`SyncPersonsToDevices` 只复用现有人员任务执行能力，补偿操作名为 `SyncPerson`。`SyncFacesToDevices` 只复用现有人脸任务执行能力，补偿操作名为 `UploadFace`。

未出现在 `deviceIds` 中的设备不得收到设备任务，也不得产生补偿意图。

## 在线、离线与失败语义

- 启用、已连接且具有 `SdkUserId` 的指定设备立即执行。
- 启用但离线或没有 `SdkUserId` 的指定设备写入设备维度补偿意图。
- 在线执行失败且设备结果标记为可重试时，写入同一指定设备的补偿意图。
- 在线执行发生不可重试失败时，返回设备维度失败，不写补偿。
- `SyncFacesToDevices` 遇到设备端人员不存在时，不补做人员下发；是否可重试以现有设备错误映射为准。

## 数据库状态边界

两个定向接口都不得调用 `IUserSyncStatusWriter.MarkPersonSynced`。只向部分设备下发成功不能代表人员已经同步到全部门禁设备。

离线补偿继续复用 `device_operation_retry_states` 的设备维度状态。现有补偿成功处理不会为 `SyncPerson` 或 `UploadFace` 回写全局人员同步完成状态，因此无需修改数据库结构或补偿表契约。

原有 `SyncPersons` 继续保留当前 `MarkPersonSynced` 行为。

## 响应设计

```json
{
  "requestId": "请求标识",
  "success": true,
  "code": "OK",
  "message": "处理成功。",
  "total": 2,
  "succeeded": 2,
  "failed": 0,
  "queued": 0,
  "targetDevices": 3,
  "queuedDetails": [],
  "items": [],
  "deviceErrors": [],
  "dbErrors": []
}
```

`SyncFacesToDevices` 额外返回 `facesUploaded`。`items` 中每个员工的 `devices` 明细继续提供设备 ID、设备名称、操作名、成功状态、排队状态、错误码和消息。

汇总代码沿用现有语义：

- 全部执行成功：`OK`。
- 存在离线或可重试结果并已排队：`PARTIAL_SUCCESS`。
- 存在不可重试失败且没有成功或排队结果：`FAILED`。

汇总数字按员工统计；同一员工在不同设备上可能同时具有成功、失败或排队明细，调用方应以 `items[].devices[]` 判断设备维度结果。

## 错误处理

以下情况返回 `INVALID_ARGUMENT`，且保持零设备调用、零补偿写入：

- 缺少 `deviceIds`、数组为空、包含非整数或非正数。
- 任一指定设备不存在、未启用或不是 `Acs` 类型。
- `items` 为空或超过 500 条。
- 员工编号为空。
- 人员有效期开始时间晚于结束时间。
- 人员接口携带任何人脸图片字段。
- 人脸接口缺少人脸图片、Base64 无法解码或解码后超过 200KB。

错误消息应指出具体非法字段或设备 ID，但不得包含人脸 Base64 原文。

## 日志与可观测性

两个新方法复用 `ExecuteUnary` 和 `GrpcCallLogger`，记录独立的操作名、请求 ID、耗时和响应码。设备任务及补偿日志继续使用已有结构化字段。

日志可以记录目标设备数量和去重后的设备 ID 摘要，但不得默认记录完整人脸 Base64。人脸载荷日志继续受现有日志配置控制。

## 兼容性

- `PermissionSyncGrpcBinder` 仅追加两个方法，不修改已有方法名称或绑定。
- `PermissionSyncGrpcService.MethodFullNames` 追加两个完整方法名，供兼容性检查和文档测试使用。
- 原有 `SyncPersons` 保持全 `Acs` 设备下发语义。
- 新接口不接受缺失 `deviceIds` 的请求，因此不会与原接口的全设备行为混淆。
- 员工编号继续按字符串处理，不进行数值转换，不改变前导零或字符串 `"0"`。

## 测试策略

采用 TDD 增加以下自动化覆盖：

- gRPC Binder 和完整方法名清单包含两个新接口。
- 只向指定设备执行，未指定设备零调用。
- 重复设备 ID 只执行一次并保持首次出现顺序。
- 任一设备校验失败时整批零调用、零补偿。
- 在线设备立即执行，离线指定设备仅写自身补偿。
- 人员接口只调用 `UpsertPersonAsync`，不调用 `UploadFaceAsync`。
- 人脸接口只调用 `UploadFaceAsync`，不调用 `UpsertPersonAsync`。
- 两个接口都不调用 `MarkPersonSynced`。
- 人员接口携带人脸、人员有效期非法、人脸缺失、Base64 非法和人脸超限均被拒绝。
- 人脸上传失败不会触发人员创建。
- 响应包含指定设备维度的成功、失败和排队明细。
- 原有 `SyncPersons` 继续面向全部 `Acs` 设备，并保持人员先于人脸。

## 文档同步

实现时同步更新：

- `docs/gRPC接口清单.md`：登记两个完整方法名、请求字段、响应字段和处理规则。
- `docs/gRPC对接指南.md`：提供调用示例、字段别名、校验规则、离线补偿和错误语义。
- `AGENTS.md`：登记本设计文档和后续实施计划文档。
