# gRPC 接口清单

本文档整理当前项目已经存在的 gRPC 接口。当前实现没有 `.proto` 文件，服务端使用 `Grpc.Core` 手工注册 `string -> string` 或 `string -> stream string` 方法；请求体与响应体均为 UTF-8 JSON 字符串。

## 1. 基础信息

| 项目 | 内容 |
| --- | --- |
| gRPC 库 | `Grpc.Core`、`Grpc.Core.Api` |
| 默认监听地址 | `0.0.0.0:5001` |
| 端口配置 | `Configuration/appsettings.json` 的 `Service.GrpcListenPort` |
| 传输凭据 | `ServerCredentials.Insecure`，当前为明文 gRPC |
| 请求/响应类型 | `string`，内容为 JSON |
| 请求追踪头 | 优先读取 `x-request-id`、`x-correlation-id`、`x-trace-id`，没有则服务端生成 |
| 门禁管理鉴权 | 仅 `device.AccessControlService` 支持 `x-api-key`，当 `Service.GrpcManagementApiKey` 为空时不强制鉴权但会记录告警 |
| 批量上限 | 权限、人员、人脸、删除类接口最大 500 条 |

## 2. 统一响应结构

成功、部分成功、失败都会尽量使用统一 JSON 结构。业务失败如果以 `RpcException` 抛出，错误详情放在 gRPC status detail 的 JSON 字符串中。

```json
{
  "requestId": "请求ID",
  "success": true,
  "code": "OK",
  "message": "处理结果说明。",
  "errors": [],
  "errorDetails": []
}
```

常见业务字段会直接追加在同一层 JSON 中，例如 `total`、`updated`、`devices`、`items` 等。

## 3. 统一错误码

| 错误码 | 含义 |
| --- | --- |
| `OK` | 成功 |
| `PARTIAL_SUCCESS` | 部分成功，通常表示部分设备离线已排队、部分人员失败等 |
| `FAILED` | 业务失败 |
| `INVALID_ARGUMENT` | 请求体为空、JSON 格式错误、字段缺失或字段非法 |
| `BATCH_TOO_LARGE` | 批量数量超过 500 |
| `NOT_FOUND` | 设备、任务或目标资源不存在 |
| `INTERNAL_ERROR` | 服务端未知异常 |
| `UNAUTHENTICATED` | 门禁管理接口缺少或传入错误 `x-api-key` |
| `DEVICE_ERROR` | 设备侧操作失败 |
| `DB_ERROR` | 数据库操作失败 |
| `SDK_ERROR` | 海康 SDK 调用失败 |
| `FACE_TOO_LARGE` | 人脸图片超过限制 |

## 4. 权限同步服务 `permission.PermissionSyncService`

### 4.1 `SyncPermissions`

| 项目 | 内容 |
| --- | --- |
| 完整方法名 | `/permission.PermissionSyncService/SyncPermissions` |
| 方法类型 | Unary |
| 用途 | 按员工编号同步门禁权限编号到设备 |
| 服务端处理 | `PermissionRefreshManager.RefreshPermissionsForEmployees` |

请求支持三种结构：数组、对象中的 `items`、对象中的 `records`，也支持单个对象。

```json
{
  "items": [
    {
      "employee_id": "10001",
      "permission_code": 1
    }
  ]
}
```

请求字段：

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `employee_id` | 是 | 员工编号/工号 |
| `permission_code` | 是 | 权限编号，必须可解析为整数 |

响应业务字段：

| 字段 | 说明 |
| --- | --- |
| `total` | 本次处理员工总数 |
| `updated` | 已更新数量 |
| `skipped` | 跳过数量 |
| `failed` | 失败数量 |
| `queued` | 进入离线补偿队列数量 |
| `queuedDetails` | 排队明细，含员工、设备、操作、消息 |

### 4.2 `SyncPersons`

| 项目 | 内容 |
| --- | --- |
| 完整方法名 | `/permission.PermissionSyncService/SyncPersons` |
| 方法类型 | Unary |
| 用途 | 向设备下发人员基础信息，可同时下发人脸图片 |
| 服务端处理 | `PermissionRefreshManager.SyncPersonsToConnectedDevices` |

请求支持数组、对象中的 `people/items/records/data`，也支持单个对象。

```json
{
  "people": [
    {
      "employee_id": "10001",
      "name": "张三",
      "gender": "male",
      "enabled": true,
      "valid_from": "2026-01-01T00:00:00",
      "valid_to": "2035-12-31T23:59:59",
      "face_image_base64": "base64字符串",
      "face_image_format": "jpg"
    }
  ]
}
```

请求字段：

| 字段 | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `employee_id` | `employeeId`、`employee_no`、`employeeNo` | 是 | 员工编号/工号 |
| `name` | `full_name`、`fullName` | 否 | 姓名 |
| `gender` | `sex` | 否 | 性别，建议 `male/female/unknown` |
| `enabled` | `active`、`is_active` | 否 | 是否启用，默认 true |
| `valid_from` | `validFrom` | 否 | 有效期开始时间 |
| `valid_to` | `validTo` | 否 | 有效期结束时间 |
| `face_image_base64` | `faceImageBase64`、`face_base64`、`faceBase64`、`face_image` | 否 | 人脸图片 Base64，支持 data URI 前缀 |
| `face_image_format` | `faceImageFormat` | 否 | 图片格式，仅用于日志展示 |

响应业务字段：

| 字段 | 说明 |
| --- | --- |
| `total` | 本次人员总数 |
| `succeeded` | 成功人数 |
| `failed` | 失败人数 |
| `queued` | 离线排队数量 |
| `facesUploaded` | 已下发人脸数量 |
| `targetDevices` | 目标设备数量 |
| `queuedDetails` | 排队明细 |

### 4.3 `DeleteFaces`

| 项目 | 内容 |
| --- | --- |
| 完整方法名 | `/permission.PermissionSyncService/DeleteFaces` |
| 方法类型 | Unary |
| 用途 | 从设备端删除指定员工的人脸 |
| 服务端处理 | `PermissionRefreshManager.DeleteFacesOnDevices` |

请求支持字符串数组、对象数组、`items`、`records`、单个对象。

```json
{
  "items": [
    { "employee_id": "10001" },
    { "employee_id": "10002" }
  ]
}
```

员工编号字段别名：`employee_id`、`employeeId`、`employee_no`、`employeeNo`。

响应业务字段：

| 字段 | 说明 |
| --- | --- |
| `total` | 员工总数 |
| `succeeded` | 成功数量 |
| `failed` | 失败数量 |
| `queued` | 离线排队数量 |
| `targetDevices` | 目标设备数量 |
| `queuedDetails` | 排队明细 |
| `items` | 每个员工的人脸操作结果 |

### 4.4 `DeletePersons`

| 项目 | 内容 |
| --- | --- |
| 完整方法名 | `/permission.PermissionSyncService/DeletePersons` |
| 方法类型 | Unary |
| 用途 | 从设备端删除指定员工人员信息 |
| 服务端处理 | `PermissionRefreshManager.DeletePersonsFromDevices` |

请求格式同 `DeleteFaces`。

响应业务字段：

| 字段 | 说明 |
| --- | --- |
| `total` | 员工总数 |
| `succeeded` | 成功数量 |
| `failed` | 失败数量 |
| `queued` | 离线排队数量 |
| `targetDevices` | 目标设备数量 |
| `queuedDetails` | 排队明细 |
| `items` | 每个员工的删除结果，含成功设备、失败设备与设备错误 |

### 4.5 `GetFaces`

| 项目 | 内容 |
| --- | --- |
| 完整方法名 | `/permission.PermissionSyncService/GetFaces` |
| 方法类型 | Unary |
| 用途 | 查询指定员工在设备端的人脸信息 |
| 服务端处理 | `PermissionRefreshManager.GetFacesFromDevices` |

请求格式同 `DeleteFaces`。

响应业务字段：

| 字段 | 说明 |
| --- | --- |
| `total` | 员工总数 |
| `succeeded` | 成功数量 |
| `failed` | 失败数量 |
| `queued` | 待补齐数量 |
| `targetDevices` | 目标设备数量 |
| `items` | 查询结果，含 `employeeId`、`success`、`faceImageBase64`、`rawResponse`、`error` |

### 4.6 `CaptureFaceStream`

| 项目 | 内容 |
| --- | --- |
| 完整方法名 | `/permission.PermissionSyncService/CaptureFaceStream` |
| 方法类型 | ServerStreaming |
| 用途 | 从“人脸录入仪”采集员工人脸，并以流式响应返回采集结果 |
| 服务端处理 | `PermissionRefreshManager.CaptureFaceFromEnrollmentDevice` |

请求示例：

```json
{
  "employee_id": "10001"
}
```

请求字段：

| 字段 | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `employee_id` | `employeeId`、`employee_no`、`employeeNo` | 是 | 员工编号/工号 |

成功时流式返回一帧：

```json
{
  "taskId": "任务ID",
  "employeeId": "10001",
  "frameIndex": 1,
  "faceImageBase64": "base64字符串",
  "faceImageFormat": "jpg",
  "qualityScore": null,
  "recommend": true,
  "requestId": "请求ID",
  "success": true,
  "code": "OK",
  "message": "采集成功。",
  "errors": [],
  "errorDetails": []
}
```

失败时返回一帧失败结果，常见错误码为 `DEVICE_ERROR` 或 `FACE_TOO_LARGE`。

### 4.7 `GetEnrollmentStatus`

| 项目 | 内容 |
| --- | --- |
| 完整方法名 | `/permission.PermissionSyncService/GetEnrollmentStatus` |
| 方法类型 | Unary |
| 用途 | 查询人脸采集任务状态 |
| 服务端处理 | `EnrollmentTaskStore.Get` |

请求示例：

```json
{
  "taskId": "任务ID"
}
```

请求字段：

| 字段 | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `taskId` | `task_id` | 是 | 采集任务 ID |

响应业务字段：

| 字段 | 说明 |
| --- | --- |
| `taskId` | 任务 ID |
| `employeeId` | 员工编号 |
| `action` | 任务动作 |
| `status` | 任务状态 |
| `message` | 状态说明 |
| `errorCode` | 错误码 |

## 5. 门禁设备管理服务 `device.AccessControlService`

该服务用于管理本地服务中的门禁设备连接与数据库中的 `devices` 表。若配置了 `Service.GrpcManagementApiKey`，调用方必须在 metadata 中传入：

```text
x-api-key: 配置的APIKey
```

### 5.1 `GetDeviceStatus`

| 项目 | 内容 |
| --- | --- |
| 完整方法名 | `/device.AccessControlService/GetDeviceStatus` |
| 方法类型 | Unary |
| 用途 | 查询设备连接状态，可按设备 ID、设备 ID 列表、IP 或全部查询 |

请求为空或 `{}` 表示查询全部设备。

```json
{
  "deviceIds": [10, 11],
  "includeDisabled": true,
  "refresh": false
}
```

请求字段：

| 字段 | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `deviceId` | `device_id` | 否 | 查询单台设备 |
| `deviceIds` | `device_ids` | 否 | 查询多台设备 |
| `ipAddress` | `ip_address` | 否 | 按 IP 查询设备 |
| `includeDisabled` | 无 | 否 | 是否包含停用设备，默认 true |
| `refresh` | 无 | 否 | 是否立即刷新设备状态，默认 false |

响应业务字段：

| 字段 | 说明 |
| --- | --- |
| `devices` | 设备状态数组 |

设备状态对象字段：

| 字段 | 说明 |
| --- | --- |
| `deviceId` | 设备 ID |
| `deviceName` | 设备名称 |
| `ipAddress` | 设备 IP |
| `port` | 端口 |
| `enabled` | 是否启用 |
| `isConnected` | 是否已连接 |
| `status` | 状态枚举字符串 |
| `statusMessage` | 状态说明 |
| `lastChecked` | 最近检查时间 |
| `lastUsed` | 最近使用时间 |
| `lastErrorCode` | 最近 SDK 错误码 |
| `lastErrorMessage` | 最近错误说明 |

### 5.2 `AddDevice`

| 项目 | 内容 |
| --- | --- |
| 完整方法名 | `/device.AccessControlService/AddDevice` |
| 方法类型 | Unary |
| 用途 | 新增设备到数据库和内存，可选立即连接 |

```json
{
  "deviceId": 30,
  "deviceName": "东门门禁进",
  "ipAddress": "10.98.26.80",
  "port": "8000",
  "username": "admin",
  "password": "设备密码",
  "description": "生产区域",
  "enabled": true,
  "connectNow": false
}
```

请求字段：

| 字段 | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `deviceId` | `device_id` | 是 | 设备 ID，必须大于 0，数据库主键非自增 |
| `deviceName` | `device_name` | 是 | 设备名称 |
| `ipAddress` | `ip_address` | 是 | 设备 IP |
| `port` | 无 | 否 | 端口，默认 `8000`，必须为 1-65535 |
| `username` | 无 | 否 | 登录用户名，默认 `admin` |
| `password` | 无 | 是 | 登录密码 |
| `description` | 无 | 否 | 设备描述 |
| `enabled` | 无 | 否 | 是否启用，默认 true |
| `connectNow` | 无 | 否 | 新增后是否立即连接，默认 false |

响应业务字段：

| 字段 | 说明 |
| --- | --- |
| `device` | 新增后的设备状态对象 |
| `connectNow` | 是否请求立即连接 |
| `connected` | 是否连接成功 |
| `connectionMessage` | 连接结果说明 |

### 5.3 `DeleteDevice`

| 项目 | 内容 |
| --- | --- |
| 完整方法名 | `/device.AccessControlService/DeleteDevice` |
| 方法类型 | Unary |
| 用途 | 删除设备，默认先断开连接，再删除数据库记录和内存索引 |

```json
{
  "deviceId": 30,
  "disconnectFirst": true
}
```

请求字段：

| 字段 | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `deviceId` | `device_id` | 是 | 设备 ID |
| `disconnectFirst` | 无 | 否 | 删除前是否先断开连接，默认 true |

响应业务字段：

| 字段 | 说明 |
| --- | --- |
| `deleted` | 是否删除成功 |
| `deviceId` | 被删除设备 ID |

### 5.4 `DisconnectDevice`

| 项目 | 内容 |
| --- | --- |
| 完整方法名 | `/device.AccessControlService/DisconnectDevice` |
| 方法类型 | Unary |
| 用途 | 手动断开指定设备连接 |

```json
{
  "deviceId": 10
}
```

响应业务字段：

| 字段 | 说明 |
| --- | --- |
| `deviceId` | 设备 ID |
| `isConnected` | 执行后是否仍连接 |
| `status` | 设备状态 |
| `message` | 处理说明 |

### 5.5 `ReconnectDevice`

| 项目 | 内容 |
| --- | --- |
| 完整方法名 | `/device.AccessControlService/ReconnectDevice` |
| 方法类型 | Unary |
| 用途 | 手动重连指定设备 |

```json
{
  "deviceId": 10,
  "force": false
}
```

请求字段：

| 字段 | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `deviceId` | `device_id` | 是 | 设备 ID |
| `force` | 无 | 否 | 是否强制重连，默认 false |

响应业务字段：

| 字段 | 说明 |
| --- | --- |
| `deviceId` | 设备 ID |
| `connected` | 是否连接成功 |
| `message` | 连接结果说明 |

## 6. 调用注意事项

1. 当前 gRPC 使用明文传输，跨主机或跨网段部署时建议放在受控网络内，或在外层增加 TLS/网关。
2. `device.AccessControlService` 的 API Key 为空时不会强制鉴权，生产环境建议配置 `Service.GrpcManagementApiKey`。
3. 所有接口的入参本质是 JSON 字符串，调用方需要按方法传入原始字符串，不是 protobuf message。
4. 权限、人员、人脸、删除接口最大 500 条，超出会失败。
5. 离线设备操作可能返回 `PARTIAL_SUCCESS`，这表示操作已进入补偿队列，不等于最终设备端已完成。
6. 人脸图片下发与采集存在 200KB 限制，超过时会返回 `FACE_TOO_LARGE` 或设备错误。
