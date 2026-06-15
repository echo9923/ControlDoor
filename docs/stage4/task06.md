# 阶段 4 / 任务 06：GetDeviceStatus

## 目标

实现 `/device.AccessControlService/GetDeviceStatus`，按既定 JSON 契约查询设备运行时状态。请求为空或 `{}` 表示查询全部设备。

## 请求字段

| 字段 | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `deviceId` | `device_id` | 否 | 查询单台设备。 |
| `deviceIds` | `device_ids` | 否 | 查询多台设备。 |
| `ipAddress` | `ip_address` | 否 | 按 IP 查询。 |
| `includeDisabled` | 无 | 否 | 是否包含停用设备，默认 true。 |
| `refresh` | 无 | 否 | 是否立即刷新状态，默认 false。 |

## 响应字段

统一响应字段保持 `requestId`、`success`、`code`、`message`、`errors`、`errorDetails`，业务字段：

| 字段 | 说明 |
| --- | --- |
| `devices` | 设备状态数组。 |

设备对象字段：

| 字段 | 来源 |
| --- | --- |
| `deviceId` | 运行时/JSON 清单。 |
| `deviceName` | `devices[].name`。 |
| `ipAddress` | `devices[].ipAddress`。 |
| `port` | `devices[].port`。 |
| `enabled` | `devices[].enabled`。 |
| `isConnected` | 运行时状态是否 Online。 |
| `status` | 运行时状态枚举字符串。 |
| `statusMessage` | 状态说明。 |
| `types` | 声明态设备类型数组，如 `Acs`、`FaceCapture`、`Camera`。 |
| `lastChecked` | 最近状态检测时间。 |
| `lastErrorCode` | 最近错误码。 |
| `lastErrorMessage` | 最近错误说明。 |

## refresh 行为

| 场景 | 行为 |
| --- | --- |
| `refresh=false` | 直接返回内存快照。 |
| `refresh=true` 且查询单台/少量设备 | 投递状态检测任务并等待结果，受超时控制。 |
| `refresh=true` 且查询全部设备 | 可投递批量状态检测，但等待时间受总超时限制；超时返回已有快照和 warning。 |

## 鉴权

若 `Service.GrpcManagementApiKey` 非空，必须校验 metadata `x-api-key`。失败返回 `UNAUTHENTICATED`。

## 不做的事

| 不做内容 | 原因 |
| --- | --- |
| 不修改 JSON 清单 | 查询接口只读。 |
| 不触发登录 | refresh 只做状态检测，不自动重连；重连由任务 4.4/4.8。 |
| 不返回密码 | 设备状态对象不得包含密码。 |

## 测试

| 测试 | 验证 |
| --- | --- |
| 空请求 | 返回全部设备。 |
| 按 ID 查询 | 返回目标设备或 `NOT_FOUND`。 |
| 按 IP 查询 | 使用 IP 索引，返回目标设备。 |
| includeDisabled | 可包含停用设备但不显示在线。 |
| refresh 成功 | 状态检测任务被投递并更新快照。 |
| refresh 超时 | 返回现有快照和 warning，不阻塞 gRPC。 |
| API Key | 空、正确、错误三种路径符合契约。 |
