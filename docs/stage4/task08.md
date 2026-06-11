# 阶段 4 / 任务 08：DeleteDevice、DisconnectDevice、ReconnectDevice

## 目标

实现设备删除、手动断开和手动重连三个 gRPC 方法。所有设备 SDK 操作都进入设备固定执行通道，数据库仍只操作 `dbo.devices` 现有结构。

## DeleteDevice

### 请求

| 字段 | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `deviceId` | `device_id` | 是 | 设备 ID。 |
| `disconnectFirst` | 无 | 否 | 默认 true。 |

### 流程

| 步骤 | 动作 |
| --- | --- |
| 1 | 校验 API Key 和 deviceId。 |
| 2 | 查找运行时设备和数据库记录。 |
| 3 | 如果 `disconnectFirst=true`，投递撤防和登出任务并等待。 |
| 4 | 停止该设备的延迟重连和状态检测任务。 |
| 5 | 从 `dbo.devices` 删除记录。 |
| 6 | 从运行时注册表删除 ID/IP/UserID/AlarmHandle 索引。 |
| 7 | 返回 `deleted=true` 和 deviceId。 |

### SQL

```sql
DELETE FROM dbo.devices WHERE device_id = @deviceId;
```

## DisconnectDevice

### 请求

```json
{
  "deviceId": 10
}
```

### 流程

| 步骤 | 动作 |
| --- | --- |
| 1 | 校验设备存在。 |
| 2 | 取消未到期的自动重连任务。 |
| 3 | 投递撤防任务。 |
| 4 | 投递登出任务。 |
| 5 | 状态置为 `Disconnected`，标记为手动断开。 |
| 6 | 返回 `isConnected=false`。 |

说明：手动断开后自动重连不应立即恢复该设备，除非后续 `ReconnectDevice` 或服务重启策略明确允许。

## ReconnectDevice

### 请求字段

| 字段 | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `deviceId` | `device_id` | 是 | 设备 ID。 |
| `force` | 无 | 否 | 默认 false。 |

### 流程

| 场景 | 行为 |
| --- | --- |
| 设备不存在 | 返回 `NOT_FOUND`。 |
| `force=false` 且在线 | 返回当前状态，`connected=true`。 |
| `force=false` 且离线 | 投递立即登录任务。 |
| `force=true` | 先撤防、登出、清理索引，再登录和必要时布防。 |

## 优先级

| 操作 | 优先级 |
| --- | --- |
| DeleteDevice 清理 | `Critical`。 |
| DisconnectDevice | `Critical`。 |
| ReconnectDevice(force=true) | `Critical`。 |
| ReconnectDevice(force=false) | `High`。 |

## 不做的事

| 不做内容 | 原因 |
| --- | --- |
| 不删除补偿表记录 | 阶段 6 统一处理补偿状态。 |
| 不删除事件历史 | 设备删除不影响 `attendance_gate_v2` 历史记录。 |
| 不修改 `dbo.devices.status` 表示断开 | 手动断开是运行时状态。 |
| 不调用权限同步 | 阶段 5 负责。 |

## 测试

| 测试 | 验证 |
| --- | --- |
| 删除在线设备 | 撤防、登出、删除 DB、清理索引顺序正确。 |
| 删除离线设备 | 删除 DB 和运行时，不调用无效 SDK。 |
| 删除不存在设备 | 返回 `NOT_FOUND`。 |
| 手动断开在线设备 | 撤防登出，状态为 Disconnected。 |
| 手动断开离线设备 | 状态保持 Disconnected，返回成功。 |
| 重连在线 force=false | 不重复登录。 |
| 重连在线 force=true | 先清理再登录。 |
| 重连失败 | 返回 connected=false，错误码可诊断。 |
