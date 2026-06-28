# ControlDoor gRPC 对接指南

本文档面向**外部对接方**，说明如何接入运行在 PC 上的 ControlDoor gRPC 服务。读完本文档你可以：

1. 连上 ControlDoor 服务；
2. 知道有哪些可用接口；
3. 知道每个接口要求的输入格式与返回的输出格式；
4. 知道这些接口各自能做什么。

> 本文档与 `docs/gRPC接口清单.md` 并存：那份偏内部接口参考，本文档偏外部对接实操。若字段细节有出入，以服务端实现为准。

---

## 第 1 章 服务能做什么

ControlDoor 在 PC 上以一个 gRPC 服务的形式运行，监听 `0.0.0.0:5001`（默认端口，可配置）。它把"海康门禁设备的 SDK 调用"封装成一组通用的 gRPC 方法，对外提供两类能力：

### 1.1 设备管理服务 `device.AccessControlService`

负责本地门禁/采集设备清单与连接生命周期：

- **设备清单管理**：新增设备（写入 `Configuration/devices.json`）、删除设备。
- **连接生命周期**：手动断开、手动重连、查询连接状态。
- **报警布防管理**：对已在线设备重新建立布防通道（布防）、关闭布防通道（撤防）、查询布防状态。
- **状态查询**：按设备 ID、ID 列表、IP 或全部设备查询运行时状态，可立即刷新。

### 1.2 权限同步服务 `permission.PermissionSyncService`

负责把"人"和"权限"下发到门禁设备：

- **权限下发**：按员工编号下发门禁权限等级。
- **人员与人脸下发**：下发人员基础信息，可同时下发人脸图片。
- **删除**：从设备端删除指定员工的人脸、删除指定员工的人员信息。
- **查询**：查询员工在设备端的人脸信息。
- **人脸采集**：从人脸录入仪流式采集一帧人脸图片，并查询采集任务状态。

### 1.3 接口总览

| 服务 | 方法 | 类型 | 用途 |
| --- | --- | --- | --- |
| `device.AccessControlService` | `GetDeviceStatus` | Unary | 查询设备连接/布防状态 |
| `device.AccessControlService` | `AddDevice` | Unary | 新增设备并可选立即连接 |
| `device.AccessControlService` | `DeleteDevice` | Unary | 删除设备（默认先断开） |
| `device.AccessControlService` | `DisconnectDevice` | Unary | 手动断开设备 |
| `device.AccessControlService` | `ReconnectDevice` | Unary | 手动重连设备 |
| `device.AccessControlService` | `RearmDeviceAlarm` | Unary | 重新建立报警布防通道 |
| `device.AccessControlService` | `DisarmDeviceAlarm` | Unary | 关闭报警布防通道 |
| `device.AccessControlService` | `GetDeviceAlarmStatus` | Unary | 查询布防状态快照 |
| `permission.PermissionSyncService` | `SyncPermissions` | Unary | 下发权限等级 |
| `permission.PermissionSyncService` | `SyncPersons` | Unary | 下发人员+人脸 |
| `permission.PermissionSyncService` | `DeleteFaces` | Unary | 删除员工人脸 |
| `permission.PermissionSyncService` | `DeletePersons` | Unary | 删除员工人员 |
| `permission.PermissionSyncService` | `GetFaces` | Unary | 查询员工人脸 |
| `permission.PermissionSyncService` | `CaptureFaceStream` | ServerStreaming | 流式采集一帧人脸 |
| `permission.PermissionSyncService` | `GetEnrollmentStatus` | Unary | 查询采集任务状态 |

---

## 第 2 章 怎么连上我的服务

### 2.1 基础信息

| 项目 | 内容 |
| --- | --- |
| 默认地址 | `0.0.0.0:5001`（PC 上就是 `<PC的IP>:5001`） |
| 端口配置 | `Configuration/appsettings.json` 的 `Service.GrpcListenPort`，默认 `5001` |
| 传输协议 | HTTP/2，**明文 gRPC**（`ServerCredentials.Insecure`，无 TLS） |
| 请求/响应类型 | `string`，内容是 **UTF-8 JSON 字符串** |
| 健康检查端点 | **无标准 `grpc.health.v1`**。建议用空参 `GetDeviceStatus {}` 探活（见 2.6） |

### 2.2 关键前提：没有 `.proto` 文件

服务端**没有 `.proto` 文件**。所有方法都是用 `Grpc.Core` 手工注册的 `Method<string, string>`——请求体和响应体都是 **UTF-8 JSON 字符串**，不是 protobuf message。

**因此你不能用 `protoc` 生成 stub。** 两种接入方式：

- **方式 A（推荐）**：用 gRPC 客户端的**通用调用 API**（generic invocation），自己提供一个 `String ↔ UTF-8 bytes` 的 marshaller。下文 Python / Java 示例都用这种方式。
- **方式 B**：本地写一份极简的 `.proto`，把请求/响应都声明成 `string`（只用来生成调用壳，字段契约仍以本文档为准）。这只是适配壳，不是官方契约。

### 2.3 方法全名（拼成 `/服务名/方法名`）

调用时方法名要拼成完整路径：

```
/device.AccessControlService/GetDeviceStatus
/device.AccessControlService/AddDevice
...
/permission.PermissionSyncService/SyncPermissions
...
```

### 2.4 请求头

所有接口**都不需要鉴权**，对接方无需传任何认证头部。服务端只读取以下可选的请求追踪头：

| 头 | 是否必填 | 说明 |
| --- | --- | --- |
| `x-request-id` | 可选，**强烈建议** | 请求追踪 ID。服务端读取顺序：`x-request-id` → `x-trace-id` → `x-correlation-id`。都没传时服务端自动生成一个。 |
| `x-trace-id` | 可选 | 同上，备选用法。 |
| `x-correlation-id` | 可选 | 同时也会被服务端单独记录为关联 ID。 |

> 实践建议：每个请求都生成一个 UUID 放到 `x-request-id`，方便出问题时按 ID 查日志。第 2.5 节的客户端封装已经默认这样做。

### 2.5 连接代码示例

下面两个最小客户端封装（`ControlDoorClient`）都做了三件事：

1. 建立明文 channel；
2. 自动注入 `x-request-id`（UUID）；
3. 把 JSON 字符串当请求体发出去、把响应字符串当 JSON 解析回来。

#### 2.5.1 Python 示例

依赖：`grpcio`（`python -m pip install grpcio`）。

```python
import json
import uuid
import grpc

def _enc(s: str) -> bytes:
    return (s or "").encode("utf-8")

def _dec(b: bytes) -> str:
    return (b or b"").decode("utf-8")

class ControlDoorClient:
    """ControlDoor gRPC 通用客户端：请求/响应都是 UTF-8 JSON 字符串。"""

    def __init__(self, host: str = "localhost:5001", timeout: int = 60):
        self.channel = grpc.insecure_channel(host)
        self.timeout = timeout

    def call(self, service: str, method: str, payload, timeout=None):
        """调用 Unary 接口。payload 可以是 dict 或 JSON 字符串。返回解析后的 dict。"""
        full = f"/{service}/{method}"
        text = payload if isinstance(payload, str) else json.dumps(payload, ensure_ascii=False)
        stub = self.channel.unary_unary(full, request_serializer=_enc, response_deserializer=_dec)
        raw = stub(text, timeout=timeout or self.timeout, metadata=self._metadata())
        return json.loads(raw)

    def call_stream(self, service: str, method: str, payload, timeout=None):
        """调用 ServerStreaming 接口。返回每帧解析后的 dict 列表。"""
        full = f"/{service}/{method}"
        text = payload if isinstance(payload, str) else json.dumps(payload, ensure_ascii=False)
        stub = self.channel.unary_stream(full, request_serializer=_enc, response_deserializer=_dec)
        return [json.loads(frame) for frame in stub(text, timeout=timeout or self.timeout, metadata=self._metadata())]

    def _metadata(self):
        return [("x-request-id", uuid.uuid4().hex)]

    def close(self):
        self.channel.close()


# === 用法 ===
ACCESS = "device.AccessControlService"
PERM   = "permission.PermissionSyncService"

client = ControlDoorClient(host="192.168.1.100:5001")

# 查全部设备
print(client.call(ACCESS, "GetDeviceStatus", {}))

# 下发权限
print(client.call(PERM, "SyncPermissions", {"items": [{"employee_id": "10001", "permission_code": 1}]}))

# 人脸采集（流式）
for frame in client.call_stream(PERM, "CaptureFaceStream", {"employee_id": "10001"}):
    print(frame)

client.close()
```

#### 2.5.2 Java 示例

依赖（Maven）：`io.grpc:grpc-core`、`io.grpc:grpc-netty-shaded`、`io.grpc:grpc-stub`、`io.grpc:grpc-protobuf`（仅需其中的 `MethodDescriptor`/`Marshaller`，不生成 stub）。

```java
import io.grpc.*;
import io.grpc.stub.ClientCalls;
import io.grpc.netty.shaded.io.grpc.netty.NettyChannelBuilder;

import java.nio.charset.StandardCharsets;
import java.util.UUID;
import com.google.gson.Gson;
import com.google.gson.JsonElement;
import com.google.gson.JsonParser;

public class ControlDoorClient implements AutoCloseable {

    private static final Gson GSON = new Gson();

    // 关键：服务端方法都是 string->string，自己提供一个 UTF-8 marshaller
    private static final MethodDescriptor.Marshaller<String> UTF8 =
            new MethodDescriptor.Marshaller<>() {
                @Override public byte[] stream(String v) {
                    return (v == null ? "" : v).getBytes(StandardCharsets.UTF_8);
                }
                @Override public String parse(java.io.InputStream in) {
                    try {
                        byte[] buf = in.readAllBytes();
                        return new String(buf, StandardCharsets.UTF_8);
                    } catch (Exception e) { throw new RuntimeException(e); }
                }
            };

    private final ManagedChannel channel;

    public ControlDoorClient(String host, int port) {
        this.channel = NettyChannelBuilder.forAddress(host, port)
                .usePlaintext()                       // 明文，与服务端 Insecure 对齐
                .build();
    }

    /** 调用 Unary 接口。payloadJson 是 JSON 字符串，返回 JSON 字符串。 */
    public String call(String service, String method, String payloadJson) {
        MethodDescriptor<String, String> md = MethodDescriptor.<String, String>newBuilder()
                .setType(MethodDescriptor.MethodType.UNARY)
                .setFullMethodName("/" + service + "/" + method)
                .setRequestMarshaller(UTF8)
                .setResponseMarshaller(UTF8)
                .build();
        return ClientCalls.blockingUnaryCall(channel, md, this::callOptions, payloadJson);
    }

    /** 调用 ServerStreaming 接口，逐帧返回 JSON 字符串。 */
    public java.util.Iterator<String> callStream(String service, String method, String payloadJson) {
        MethodDescriptor<String, String> md = MethodDescriptor.<String, String>newBuilder()
                .setType(MethodDescriptor.MethodType.SERVER_STREAMING)
                .setFullMethodName("/" + service + "/" + method)
                .setRequestMarshaller(UTF8)
                .setResponseMarshaller(UTF8)
                .build();
        return ClientCalls.blockingServerStreamingCall(channel, md, this::callOptions, payloadJson);
    }

    private CallOptions callOptions() {
        Metadata h = new Metadata();
        h.put(Metadata.Key.of("x-request-id", Metadata.ASCII_STRING_MARSHALLER), UUID.randomUUID().toString().replace("-", ""));
        return CallOptions.DEFAULT.withDeadlineAfter(60, java.util.concurrent.TimeUnit.SECONDS)
                .withCredentials(new FixedHeaderCredentials(h));
    }

    @Override public void close() { channel.shutdownNow(); }

    // 固定 metadata 注入（x-request-id）
    private static class FixedHeaderCredentials implements Credentials {
        private final Metadata headers;
        FixedHeaderCredentials(Metadata h) { this.headers = h; }
        @Override public Metadata applyRequestMetadata(Metadata.RequestInfo info, Executor executor, MetadataApplier applier) {
            applier.applyHeaders(headers);
        }
        @Override public void thisUsesUnstableApi() {}
    }

    // === 用法 ===
    public static void main(String[] args) {
        String ACCESS = "device.AccessControlService";
        String PERM = "permission.PermissionSyncService";
        try (ControlDoorClient client = new ControlDoorClient("192.168.1.100", 5001)) {
            // 查全部设备
            System.out.println(client.call(ACCESS, "GetDeviceStatus", "{}"));

            // 下发权限
            System.out.println(client.call(PERM, "SyncPermissions",
                    "{\"items\":[{\"employee_id\":\"10001\",\"permission_code\":1}]}"));

            // 人脸采集（流式）
            Iterator<String> frames = client.callStream(PERM, "CaptureFaceStream", "{\"employee_id\":\"10001\"}");
            while (frames.hasNext()) {
                System.out.println(frames.next());
            }
        }
    }
}
```

> 说明：上面 Java 示例用了一个 `FixedHeaderCredentials` 来把 metadata 注入进去。也可以改成用 `ClientInterceptors.intercept(channel, new HeaderClientInterceptor())`。两种都行。

### 2.6 连通性自检

调一次空参设备查询，能正常返回 JSON（不抛异常）就代表连上了：

```python
# Python
resp = client.call(ACCESS, "GetDeviceStatus", {})
assert resp.get("success") is True    # 连通了
```

```java
// Java
String resp = client.call(ACCESS, "GetDeviceStatus", "{}");
```

如果抛 `UNAVAILABLE` / `connection refused`：端口或网络不通，或服务没启动。

---

## 第 3 章 统一的输入/输出约定

每个接口的输入/输出细节见第 4 章，本章先说**所有接口共用**的约定。

### 3.1 业务错误在 JSON 里返回，gRPC RPC 本身保持 OK

**重要**：除了网络/超时这类 RPC 层错误，服务端**不会**用 gRPC 状态码（如 `NotFound`/`InvalidArgument`）表达业务失败。无论成功失败，gRPC 调用本身都返回 OK，**业务结果写在响应 JSON 的 `success` / `code` 字段里**。

所以对接方判断成败要读 JSON，不要按 HTTP/gRPC 状态码判断。

### 3.2 统一响应结构

每个响应都是一个 JSON 对象，固定包含以下字段，业务字段追加在同一层：

```json
{
  "requestId": "本次请求的 ID",
  "success": true,
  "code": "OK",
  "message": "处理结果说明。",
  "errors": [],
  "errorDetails": []
}
```

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `requestId` | string | 请求 ID（来自 `x-request-id` 等，没传则服务端生成） |
| `success` | bool | 业务是否成功（部分成功时 `code=PARTIAL_SUCCESS`，`success=true`） |
| `code` | string | 业务结果码，见 3.4 |
| `message` | string | 结果说明 |
| `errors` | string[] | 错误信息列表（失败时通常含 message） |
| `errorDetails` | object[] | 错误明细（结构因接口而异） |
| 业务字段 | 各接口不同 | 如 `devices`、`items`、`total`、`queued` 等，见第 4 章 |

### 3.3 输入通用约定

- 请求体是 **UTF-8 JSON 字符串**（不是 protobuf message）。
- 字段命名**同时支持 `snake_case` 和 `camelCase`**（如 `employee_id` 与 `employeeId` 等价），各接口支持哪些别名见第 4 章字段表。
- 批量类接口（权限、人员、人脸、删除）**单批最多 500 条**，超限返回 `BATCH_TOO_LARGE`。
- 人脸图片（下发与采集）**单张最大 200KB**，超限返回 `FACE_TOO_LARGE`。
- 人脸图片字段传 **Base64 字符串**，支持 data URI 前缀（`data:image/jpeg;base64,...`）。
- `code=PARTIAL_SUCCESS` 通常表示**部分设备离线已入补偿队列**，**不代表设备端已执行完成**，需后续确认。

### 3.4 错误码表

| 错误码 | 含义 |
| --- | --- |
| `OK` | 成功 |
| `PARTIAL_SUCCESS` | 部分成功，通常表示部分设备离线已排队、部分人员失败等 |
| `FAILED` | 业务失败 |
| `INVALID_ARGUMENT` | 请求体为空、JSON 格式错误、字段缺失或字段非法 |
| `BATCH_TOO_LARGE` | 批量数量超过 500 |
| `NOT_FOUND` | 设备、任务或目标资源不存在 |
| `INTERNAL_ERROR` | 服务端未知异常 |
| `DEVICE_ERROR` | 设备侧操作失败（如设备未在线） |
| `DB_ERROR` | 数据库操作失败 |
| `SDK_ERROR` | 海康 SDK 调用失败 |
| `FACE_TOO_LARGE` | 人脸图片超过 200KB |
| `DEVICE_UNSUPPORTED` | 设备不支持该能力（SDK 错误码 23） |
| `DEVICE_OFFLINE` | 设备未在线（设备任务返回） |
| `QUEUED` | 操作已入离线补偿队列 |
| `TIMEOUT` | 设备任务执行超时（如人脸采集、SDK 调用超时） |
| `INVALID_CONFIG` | 设备配置非法（如清单字段缺失/非法） |
| `INVALID_PAYLOAD` | 离线补偿重放时 payload 非法（一般对接方不会触发） |

### 3.5 设备状态对象（多个接口共用）

`GetDeviceStatus`、`AddDevice` 等返回的 `devices`/`device` 数组里，每个设备对象字段如下：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `deviceId` | int | 设备 ID |
| `deviceName` | string | 设备名称 |
| `description` | string | 设备描述（权限同步用其识别办公/生产区域） |
| `ipAddress` | string | 设备 IP |
| `port` | string | 端口（注意是字符串） |
| `enabled` | bool | 是否启用 |
| `isConnected` | bool | 是否已连接 |
| `status` | string | 状态枚举字符串（如 `Online`/`Offline`/`Disconnected`/`Disabled`/`Loaded`/`InvalidConfig`/`Deleted`） |
| `statusMessage` | string | 状态说明 |
| `isAlarmArmed` | bool | 是否已布防（由运行时布防句柄推导，不暴露内部句柄） |
| `alarmStatus` | string | 布防状态：`Armed`/`NotArmed`/`ManuallyDisarmed`/`Unavailable` |
| `alarmStatusMessage` | string | 布防状态中文说明：`已布防`/`在线但未布防`/`已手动撤防`/`设备不可布防` |
| `types` | string[] | 设备类型数组，合法值 `Acs`/`FaceCapture`/`Camera` |
| `lastChecked` | string | 最近检查时间（`yyyy-MM-ddTHH:mm:ss`，可空） |
| `lastErrorCode` | int/null | 最近 SDK 错误码 |
| `lastErrorMessage` | string/null | 最近错误说明 |

> 设备类型含义：`Acs`=门禁刷脸设备（接受权限/人员/人脸下发）；`FaceCapture`=人脸录入仪（用于采集）；`Camera`=IP 摄像头（AIOP 报警联动）。明眸"人脸+门禁"一体设备同时声明 `["Acs","FaceCapture"]`。

---

## 第 4 章 接口清单与输入/输出格式

### 4.1 设备管理服务 `device.AccessControlService`

#### 4.1.1 `GetDeviceStatus`

| 项 | 内容 |
| --- | --- |
| 完整方法名 | `/device.AccessControlService/GetDeviceStatus` |
| 类型 | Unary |
| 用途 | 查询设备连接/布防状态，可按设备 ID、ID 列表、IP 或全部查询 |

**输入字段：**

| 字段 | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `deviceId` | `device_id` | 否 | 查询单台设备 |
| `deviceIds` | `device_ids` | 否 | 查询多台设备（int 数组） |
| `ipAddress` | `ip_address` | 否 | 按 IP 查询设备 |
| `includeDisabled` | 无 | 否 | 是否包含停用设备，默认 true |
| `refresh` | 无 | 否 | 是否立即刷新设备状态，默认 false |

**输入示例：**

```json
{
  "deviceIds": [10, 11],
  "includeDisabled": true,
  "refresh": false
}
```

**输出字段：**

| 字段 | 说明 |
| --- | --- |
| `devices` | 设备状态对象数组（结构见 3.5） |

**输出示例（成功）：**

```json
{
  "requestId": "f1b2...",
  "success": true,
  "code": "OK",
  "message": "查询成功。",
  "errors": [],
  "errorDetails": [],
  "devices": [
    {
      "deviceId": 10,
      "deviceName": "东门门禁进",
      "description": "生产区域",
      "ipAddress": "10.98.26.80",
      "port": "8000",
      "enabled": true,
      "isConnected": true,
      "status": "Online",
      "statusMessage": "在线",
      "isAlarmArmed": true,
      "alarmStatus": "Armed",
      "alarmStatusMessage": "已布防",
      "types": ["Acs"],
      "lastChecked": "2026-06-28T10:00:00",
      "lastErrorCode": null,
      "lastErrorMessage": null
    }
  ]
}
```

**输出示例（失败，设备不存在）：**

```json
{
  "requestId": "f1b2...",
  "success": false,
  "code": "NOT_FOUND",
  "message": "设备不存在: 99",
  "errors": ["设备不存在: 99"],
  "errorDetails": []
}
```

#### 4.1.2 `AddDevice`

| 项 | 内容 |
| --- | --- |
| 完整方法名 | `/device.AccessControlService/AddDevice` |
| 类型 | Unary |
| 用途 | 新增设备到 `devices.json` 和运行时，可选立即连接 |

**输入字段：**

| 字段 | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `deviceId` | `device_id` | 是 | 设备 ID，必须 >0，在清单内唯一 |
| `deviceName` | `device_name` | 是 | 设备名称 |
| `ipAddress` | `ip_address` | 是 | 设备 IP |
| `port` | 无 | 否 | 端口（字符串），默认 `8000`，必须 1-65535 |
| `username` | 无 | 否 | 登录用户名，默认 `admin` |
| `password` | 无 | 是 | 登录密码 |
| `types` | `deviceTypes`/`device_types` | 是 | 设备类型数组，合法值 `Acs`/`FaceCapture`/`Camera`，可多选 |
| `description` | 无 | 否 | 设备描述 |
| `enabled` | 无 | 否 | 是否启用，默认 true |
| `connectNow` | 无 | 否 | 新增后是否立即连接，默认 false |

**输入示例：**

```json
{
  "deviceId": 30,
  "deviceName": "东门门禁进",
  "ipAddress": "10.98.26.80",
  "port": "8000",
  "username": "admin",
  "password": "your-password",
  "types": ["Acs"],
  "description": "生产区域",
  "enabled": true,
  "connectNow": false
}
```

**输出字段：**

| 字段 | 说明 |
| --- | --- |
| `device` | 新增后的设备状态对象（见 3.5） |
| `connectNow` | 是否请求立即连接 |
| `connected` | 是否连接成功 |
| `connectionMessage` | 连接结果说明 |

**输出说明：** `connectNow=false` 或连接成功时 `code=OK`；`connectNow=true` 但连接失败时 `code=PARTIAL_SUCCESS`（设备已入库）。

**输出示例（成功，未立即连接）：**

```json
{
  "requestId": "f1b2...",
  "success": true,
  "code": "OK",
  "message": "新增成功。",
  "errors": [],
  "errorDetails": [],
  "device": { "deviceId": 30, "..." : "..." },
  "connectNow": false,
  "connected": false,
  "connectionMessage": "未请求立即连接。"
}
```

#### 4.1.3 `DeleteDevice`

| 项 | 内容 |
| --- | --- |
| 完整方法名 | `/device.AccessControlService/DeleteDevice` |
| 类型 | Unary |
| 用途 | 删除设备，默认先断开连接，再从 `devices.json` 和运行时索引移除 |

**输入字段：**

| 字段 | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `deviceId` | `device_id` | 是 | 设备 ID，必须 >0 |
| `disconnectFirst` | 无 | 否 | 删除前是否先断开，默认 true |

**输入示例：**

```json
{ "deviceId": 30, "disconnectFirst": true }
```

**输出字段：**

| 字段 | 说明 |
| --- | --- |
| `deleted` | 是否删除成功 |
| `deviceId` | 被删除设备 ID |

**输出示例（成功）：**

```json
{
  "requestId": "f1b2...",
  "success": true,
  "code": "OK",
  "message": "删除设备成功。",
  "errors": [],
  "errorDetails": [],
  "deleted": true,
  "deviceId": 30
}
```

#### 4.1.4 `DisconnectDevice`

| 项 | 内容 |
| --- | --- |
| 完整方法名 | `/device.AccessControlService/DisconnectDevice` |
| 类型 | Unary |
| 用途 | 手动断开指定设备（撤防 + 登出） |

**输入字段：**

| 字段 | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `deviceId` | `device_id` | 是 | 设备 ID，必须 >0 |

**输入示例：** `{ "deviceId": 10 }`

**输出字段：**

| 字段 | 说明 |
| --- | --- |
| `deviceId` | 设备 ID |
| `isConnected` | 执行后是否仍连接 |
| `status` | 设备状态 |
| `message` | 处理说明 |

**输出示例：**

```json
{
  "requestId": "f1b2...",
  "success": true,
  "code": "OK",
  "message": "断开设备成功。",
  "errors": [],
  "errorDetails": [],
  "deviceId": 10,
  "isConnected": false,
  "status": "Disconnected",
  "message": "登出清理成功。"
}
```

#### 4.1.5 `ReconnectDevice`

| 项 | 内容 |
| --- | --- |
| 完整方法名 | `/device.AccessControlService/ReconnectDevice` |
| 类型 | Unary |
| 用途 | 手动重连指定设备 |

**输入字段：**

| 字段 | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `deviceId` | `device_id` | 是 | 设备 ID，必须 >0 |
| `force` | 无 | 否 | 是否强制重连，默认 false。`force=false` 且已在线时直接返回"设备已在线。" |

**输入示例：** `{ "deviceId": 10, "force": false }`

**输出字段：**

| 字段 | 说明 |
| --- | --- |
| `deviceId` | 设备 ID |
| `connected` | 是否连接成功 |
| `message` | 连接结果说明（如 `设备已在线。`、`登录成功。` 等） |

**输出示例（force=false 且设备已在线，幂等直接返回）：**

```json
{
  "requestId": "f1b2...",
  "success": true,
  "code": "OK",
  "message": "重连设备已处理。",
  "errors": [],
  "errorDetails": [],
  "deviceId": 10,
  "connected": true,
  "message": "设备已在线。"
}
```

> 说明：`force=true` 重连成功时内层 `message` 为「登录成功。」（重连失败则返回对应错误码与说明）。

#### 4.1.6 `RearmDeviceAlarm`

| 项 | 内容 |
| --- | --- |
| 完整方法名 | `/device.AccessControlService/RearmDeviceAlarm` |
| 类型 | Unary |
| 用途 | 对已在线设备重新建立报警布防通道，不登出、不重连、不改清单 |

**输入字段：**

| 字段 | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `deviceId` | `device_id` | 是 | 设备 ID，必须 >0 |
| `force` | 无 | 否 | 是否强制重新布防，**默认 true**；`force=false` 且已布防时直接返回已布防状态 |

**处理语义：** 设备必须已在线且有有效 `SdkUserId`，否则返回 `DEVICE_ERROR`，不会自动登录或重连。`force=true` 且已有布防句柄时，先关闭旧句柄再重新布防；前置撤防失败则返回 SDK 错误、不继续。

**输入示例：** `{ "deviceId": 10, "force": true }`

**输出字段（报警操作对象，4.1.6–4.1.8 共用）：**

| 字段 | 说明 |
| --- | --- |
| `deviceId` | 设备 ID |
| `armed` | 执行后是否有有效布防句柄 |
| `alarmStatus` | 布防状态（`Armed`/`NotArmed`/`ManuallyDisarmed`/`Unavailable`） |
| `alarmStatusMessage` | 布防状态中文说明 |
| `alarmHandle` | 当前布防句柄；未布防时为 null |
| `connected` | 设备是否仍在线 |
| `status` | 设备运行时状态 |
| `message` | 处理说明 |

**输出示例：**

```json
{
  "requestId": "f1b2...",
  "success": true,
  "code": "OK",
  "message": "重新布防已处理。",
  "errors": [],
  "errorDetails": [],
  "deviceId": 10,
  "armed": true,
  "alarmStatus": "Armed",
  "alarmStatusMessage": "已布防",
  "alarmHandle": 12345,
  "connected": true,
  "status": "Online",
  "message": "布防成功。"
}
```

#### 4.1.7 `DisarmDeviceAlarm`

| 项 | 内容 |
| --- | --- |
| 完整方法名 | `/device.AccessControlService/DisarmDeviceAlarm` |
| 类型 | Unary |
| 用途 | 关闭指定设备的报警布防通道，不登出、不改连接状态、不改清单 |

**输入字段：**

| 字段 | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `deviceId` | `device_id` | 是 | 设备 ID，必须 >0 |

**处理语义：** 没有 `AlarmHandle` 时幂等成功（返回"设备未布防，跳过撤防。"）；关闭布防成功后才清理本地句柄；同时取消该设备挂起的 ReArm 延迟任务。

**输入示例：** `{ "deviceId": 10 }`

**输出字段：** 同 4.1.6 报警操作对象（撤防后 `armed=false`、`alarmHandle=null`）。

#### 4.1.8 `GetDeviceAlarmStatus`

| 项 | 内容 |
| --- | --- |
| 完整方法名 | `/device.AccessControlService/GetDeviceAlarmStatus` |
| 类型 | Unary |
| 用途 | 查询单台设备当前运行时报警布防状态，**只读快照**，不调 SDK、不刷新、不改状态 |

**输入字段：**

| 字段 | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `deviceId` | `device_id` | 是 | 设备 ID，必须 >0 |

**输入示例：** `{ "deviceId": 10 }`

**输出字段：** 同 4.1.6 报警操作对象。

> 与 `GetDeviceStatus` 区别：本接口**会**返回内部 `alarmHandle`（用于现场排查），而 `GetDeviceStatus` 只返回 `isAlarmArmed` 布尔，不暴露句柄。

**输出示例：**

```json
{
  "requestId": "f1b2...",
  "success": true,
  "code": "OK",
  "message": "查询布防状态成功。",
  "errors": [],
  "errorDetails": [],
  "deviceId": 10,
  "armed": true,
  "alarmStatus": "Armed",
  "alarmStatusMessage": "已布防",
  "alarmHandle": 12345,
  "connected": true,
  "status": "Online",
  "message": "查询布防状态成功。"
}
```

---

### 4.2 权限同步服务 `permission.PermissionSyncService`

**通用说明（适用于 4.2.1–4.2.5 批量接口）：**

- 请求体容器因接口而异：
  - `SyncPermissions` / `DeleteFaces` / `DeletePersons`：数组、`{"items":[...]}`、`{"records":[...]}`、单个对象/字符串。
  - `SyncPersons`：数组、`{"people":[...]}`、`{"items":[...]}`、`{"records":[...]}`、`{"data":[...]}`、单个对象。
  - 下文统一用 `items` 示例。
- 员工编号字段 `employee_id` 同时接受别名 `employeeId`/`employee_no`/`employeeNo`。
- 单批最多 500 条。
- 在线设备（启用+已连接+有 `SdkUserId`）才会立即执行；离线设备会入**离线补偿队列**，返回 `PARTIAL_SUCCESS` + `queued`/`queuedDetails`。
- 数据库写入失败会体现在 `dbErrors` 数组里（每条带员工/错误信息）。

#### 4.2.1 `SyncPermissions`

| 项 | 内容 |
| --- | --- |
| 完整方法名 | `/permission.PermissionSyncService/SyncPermissions` |
| 类型 | Unary |
| 用途 | 按员工编号下发门禁权限等级到设备 |

**输入字段：**

| 字段 | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `employee_id` | `employeeId`/`employee_no`/`employeeNo` | 是 | 员工编号 |
| `permission_code` | `permissionCode`/`permission_level`/`permissionLevel` | 是 | 权限等级，整数。`0`=全部禁用；`1`=仅办公区域启用；`2`=办公/生产/其他均启用；其他值全部禁用 |
| `name` | `full_name`/`fullName`/`name_alias` | 否 | 员工姓名，空则设备端用员工编号当姓名 |

**输入示例：**

```json
{
  "items": [
    { "employee_id": "10001", "permission_code": 2, "name": "张三" }
  ]
}
```

**输出字段：**

| 字段 | 说明 |
| --- | --- |
| `total` | 本次处理员工总数 |
| `updated` | 已更新数量 |
| `skipped` | 跳过数量 |
| `failed` | 失败数量 |
| `queued` | 进入离线补偿队列数量 |
| `queuedDetails` | 排队明细（员工/设备/操作/消息） |
| `items` | 每个员工的明细 |
| `deviceErrors` | 设备错误明细 |
| `dbErrors` | 数据库错误明细 |

**输出示例（成功）：**

```json
{
  "requestId": "f1b2...",
  "success": true,
  "code": "OK",
  "message": "处理成功。",
  "errors": [],
  "errorDetails": [],
  "total": 1,
  "updated": 1,
  "skipped": 0,
  "failed": 0,
  "queued": 0,
  "queuedDetails": [],
  "items": [],
  "deviceErrors": [],
  "dbErrors": []
}
```

#### 4.2.2 `SyncPersons`

| 项 | 内容 |
| --- | --- |
| 完整方法名 | `/permission.PermissionSyncService/SyncPersons` |
| 类型 | Unary |
| 用途 | 下发人员基础信息，可同时下发人脸图片 |

**输入字段：**

| 字段 | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `employee_id` | `employeeId`/`employee_no`/`employeeNo` | 是 | 员工编号 |
| `name` | `full_name`/`fullName` | 否 | 姓名 |
| `gender` | `sex` | 否 | 性别，建议 `male`/`female`/`unknown` |
| `enabled` | `active`/`is_active` | 否 | 是否启用，默认 true |
| `valid_from` | `validFrom` | 否 | 有效期开始时间 |
| `valid_to` | `validTo` | 否 | 有效期结束时间 |
| `face_image_base64` | `faceImageBase64`/`face_base64`/`faceBase64`/`face_image` | 否 | 人脸图片 Base64，支持 data URI 前缀，最大 200KB |
| `face_image_format` | `faceImageFormat` | 否 | 图片格式，仅用于日志展示 |

**输入示例：**

```json
{
  "items": [
    {
      "employee_id": "10001",
      "name": "张三",
      "gender": "male",
      "enabled": true,
      "valid_from": "2026-01-01T00:00:00",
      "valid_to": "2035-12-31T23:59:59",
      "face_image_base64": "base64字符串...",
      "face_image_format": "jpg"
    }
  ]
}
```

**输出字段：**

| 字段 | 说明 |
| --- | --- |
| `total` | 本次人员总数 |
| `succeeded` | 成功人数 |
| `failed` | 失败人数 |
| `queued` | 离线排队数量 |
| `facesUploaded` | 已下发人脸数量 |
| `targetDevices` | 目标设备数量 |
| `queuedDetails` | 排队明细 |
| `items` | 每个人员的明细 |
| `deviceErrors` | 设备错误明细 |
| `dbErrors` | 数据库错误明细 |

#### 4.2.3 `DeleteFaces`

| 项 | 内容 |
| --- | --- |
| 完整方法名 | `/permission.PermissionSyncService/DeleteFaces` |
| 类型 | Unary |
| 用途 | 从设备端删除指定员工的人脸 |

**输入格式：** 员工编号列表，支持字符串数组、对象数组、`{"items":[...]}`/`{"records":[...]}`、单个对象/字符串。

```json
{
  "items": [
    { "employee_id": "10001" },
    { "employee_id": "10002" }
  ]
}
```

**输出字段：**

| 字段 | 说明 |
| --- | --- |
| `total` | 员工总数 |
| `succeeded` | 成功数量 |
| `failed` | 失败数量 |
| `queued` | 离线排队数量 |
| `targetDevices` | 目标设备数量 |
| `queuedDetails` | 排队明细 |
| `items` | 每个员工的人脸操作结果 |
| `deviceErrors` | 设备错误明细 |
| `dbErrors` | 数据库错误明细 |

#### 4.2.4 `DeletePersons`

| 项 | 内容 |
| --- | --- |
| 完整方法名 | `/permission.PermissionSyncService/DeletePersons` |
| 类型 | Unary |
| 用途 | 从设备端删除指定员工人员信息 |

**输入格式：** 同 `DeleteFaces`。

**处理语义：** 在线设备会先 best-effort 删除人脸，再删除人员；前置删除人脸失败不阻断删除人员。只有删除人员可重试/离线失败时才入删除人员补偿队列。

**输出字段：**

| 字段 | 说明 |
| --- | --- |
| `total` | 员工总数 |
| `succeeded` | 成功数量 |
| `failed` | 失败数量 |
| `queued` | 离线排队数量 |
| `targetDevices` | 目标设备数量 |
| `queuedDetails` | 排队明细 |
| `items` | 每个员工的删除结果（含成功设备、失败设备与设备错误） |
| `deviceErrors` | 设备错误明细 |
| `dbErrors` | 数据库错误明细 |

#### 4.2.5 `GetFaces`

| 项 | 内容 |
| --- | --- |
| 完整方法名 | `/permission.PermissionSyncService/GetFaces` |
| 类型 | Unary |
| 用途 | 查询指定员工在设备端的人脸信息（只读，离线设备不入队列） |

**输入格式：** 同 `DeleteFaces`。

**输出字段：**

| 字段 | 说明 |
| --- | --- |
| `total` | 员工总数 |
| `succeeded` | 成功数量 |
| `failed` | 失败数量 |
| `queued` | 待补齐数量（本接口恒为 0） |
| `targetDevices` | 目标设备数量 |
| `items` | 查询结果，每项含 `employeeId`、`success`、`queued`、`devices`（每台设备固定含 `deviceId`/`deviceName`/`operation`/`success`/`queued`/`code`/`message`；查询成功时追加 `faceCount`/`exists`/`faces`/`rawResponse`，即使 `exists=false`/`faceCount=0` 也会出现。错误信息在 `code`/`message`，无 `error` 字段） |

#### 4.2.6 `CaptureFaceStream`（ServerStreaming）

| 项 | 内容 |
| --- | --- |
| 完整方法名 | `/permission.PermissionSyncService/CaptureFaceStream` |
| 类型 | **ServerStreaming** |
| 用途 | 从人脸录入仪采集一帧人脸图片，流式返回 |

**输入字段：**

| 字段 | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `employee_id` | `employeeId`/`employee_no`/`employeeNo` | 是 | 员工编号 |

**输入示例：** `{ "employee_id": "10001" }`

**输出说明：** 每次调用流式返回**恰好一帧**（成功或失败）。

**帧字段：**

| 字段 | 说明 |
| --- | --- |
| `taskId` | 采集任务 ID（用于后续 `GetEnrollmentStatus` 查询） |
| `employeeId` | 员工编号 |
| `frameIndex` | 帧序号，成功为 1，失败为 0 |
| `faceImageBase64` | 采集到的人脸图片 Base64（失败时为空串） |
| `faceImageFormat` | 图片格式 |
| `qualityScore` | 人脸质量分（失败为 0） |
| `recommend` | 是否推荐使用该帧（失败为 false） |
| `requestId` | 请求 ID |
| `success` | 是否采集成功 |
| `code` | 结果码 |
| `message` | 结果说明 |
| `errors` | 错误信息列表 |

> 说明：业务帧字段（`taskId`/`employeeId`/`frameIndex`/...）在**采集流程启动后**的成功与失败分支都会返回。**唯一例外**是请求 JSON 本身解析失败（如 `employee_id` 缺失或为空 → `INVALID_ARGUMENT`）：此时返回的帧只含标准信封（`requestId`/`success`/`code`/`message`/`errors`/`errorDetails`），不含这些业务帧字段。

> 失败码不止 `DEVICE_ERROR`/`FACE_TOO_LARGE`：采集任务执行过程中还可能返回 `SDK_ERROR`、`DEVICE_UNSUPPORTED`（SDK 错误码 23）、`TIMEOUT`、`DEVICE_OFFLINE`。各码含义见 3.4。

**输出示例（成功）：**

```json
{
  "taskId": "a3f4c9...",
  "employeeId": "10001",
  "frameIndex": 1,
  "faceImageBase64": "base64字符串...",
  "faceImageFormat": "jpg",
  "qualityScore": 85,
  "recommend": true,
  "requestId": "f1b2...",
  "success": true,
  "code": "OK",
  "message": "采集成功。",
  "errors": [],
  "errorDetails": []
}
```

**输出示例（失败，无可用采集设备）：**

```json
{
  "taskId": "a3f4c9...",
  "employeeId": "10001",
  "frameIndex": 0,
  "faceImageBase64": "",
  "faceImageFormat": "jpg",
  "qualityScore": 0,
  "recommend": false,
  "requestId": "f1b2...",
  "success": false,
  "code": "DEVICE_ERROR",
  "message": "没有可用的人脸采集设备。",
  "errors": ["没有可用的人脸采集设备。"],
  "errorDetails": []
}
```

#### 4.2.7 `GetEnrollmentStatus`

| 项 | 内容 |
| --- | --- |
| 完整方法名 | `/permission.PermissionSyncService/GetEnrollmentStatus` |
| 类型 | Unary |
| 用途 | 查询人脸采集任务状态 |

**输入字段：**

| 字段 | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `taskId` | `task_id` | 否（优先） | 采集任务 ID；有则按 ID 查 |
| `employee_id` | `employeeId`/`employee_no`/`employeeNo` | 否 | 员工编号；无 taskId 时按员工取最近一次任务 |

**输入示例：** `{ "taskId": "a3f4c9..." }`

**输出字段：**

| 字段 | 说明 |
| --- | --- |
| `taskId` | 任务 ID |
| `employeeId` | 员工编号 |
| `action` | 任务动作，固定为 `CaptureFaceStream` |
| `status` | 任务状态（枚举：`Running`/`Succeeded`/`Failed`） |
| `message` | 状态说明 |
| `errorCode` | 错误码；成功时为空串 `""`（不是 null） |

**输出示例（成功）：**

```json
{
  "requestId": "f1b2...",
  "success": true,
  "code": "OK",
  "message": "查询成功。",
  "errors": [],
  "errorDetails": [],
  "taskId": "a3f4c9...",
  "employeeId": "10001",
  "action": "CaptureFaceStream",
  "status": "Succeeded",
  "message": "采集成功。",
  "errorCode": ""
}
```

> 注意：采集任务状态**保存在内存**，服务重启会丢失。找不到任务时返回 `NOT_FOUND`「采集任务不存在。」。

---

## 第 5 章 附录

### 5.1 全部接口速查表

| 方法 | 完整路径 | 类型 | 批量 | 用途 |
| --- | --- | --- | --- | --- |
| GetDeviceStatus | `/device.AccessControlService/GetDeviceStatus` | Unary | 否 | 查询设备状态 |
| AddDevice | `/device.AccessControlService/AddDevice` | Unary | 否 | 新增设备 |
| DeleteDevice | `/device.AccessControlService/DeleteDevice` | Unary | 否 | 删除设备 |
| DisconnectDevice | `/device.AccessControlService/DisconnectDevice` | Unary | 否 | 断开设备 |
| ReconnectDevice | `/device.AccessControlService/ReconnectDevice` | Unary | 否 | 重连设备 |
| RearmDeviceAlarm | `/device.AccessControlService/RearmDeviceAlarm` | Unary | 否 | 重新布防 |
| DisarmDeviceAlarm | `/device.AccessControlService/DisarmDeviceAlarm` | Unary | 否 | 撤防 |
| GetDeviceAlarmStatus | `/device.AccessControlService/GetDeviceAlarmStatus` | Unary | 否 | 查询布防状态 |
| SyncPermissions | `/permission.PermissionSyncService/SyncPermissions` | Unary | 是(≤500) | 下发权限 |
| SyncPersons | `/permission.PermissionSyncService/SyncPersons` | Unary | 是(≤500) | 下发人员+人脸 |
| DeleteFaces | `/permission.PermissionSyncService/DeleteFaces` | Unary | 是(≤500) | 删除人脸 |
| DeletePersons | `/permission.PermissionSyncService/DeletePersons` | Unary | 是(≤500) | 删除人员 |
| GetFaces | `/permission.PermissionSyncService/GetFaces` | Unary | 是(≤500) | 查询人脸 |
| CaptureFaceStream | `/permission.PermissionSyncService/CaptureFaceStream` | ServerStreaming | 否 | 采集人脸 |
| GetEnrollmentStatus | `/permission.PermissionSyncService/GetEnrollmentStatus` | Unary | 否 | 查询采集状态 |

### 5.2 字段别名总表

> 注意：别名按接口区分。下表标注「仅 SyncPermissions」的别名仅在该接口生效；其余字段在所有相关接口通用。

| 标准字段 | 接受的别名 |
| --- | --- |
| `employee_id` | `employeeId`、`employee_no`、`employeeNo` |
| `permission_code` | `permissionCode`、`permission_level`、`permissionLevel` |
| `name` | `full_name`、`fullName`；`name_alias` 仅 `SyncPermissions` 支持 |
| `gender` | `sex` |
| `enabled` | `active`、`is_active` |
| `valid_from` | `validFrom` |
| `valid_to` | `validTo` |
| `face_image_base64` | `faceImageBase64`、`face_base64`、`faceBase64`、`face_image` |
| `face_image_format` | `faceImageFormat` |
| `deviceId` | `device_id` |
| `deviceIds` | `device_ids` |
| `ipAddress` | `ip_address` |
| `deviceName` | `device_name` |
| `types` | `deviceTypes`、`device_types` |
| `taskId` | `task_id` |

### 5.3 关键配置项速查

| 配置键（`Configuration/appsettings.json`） | 默认 | 说明 |
| --- | --- | --- |
| `Service.GrpcListenPort` | `5001` | gRPC 监听端口 |
| `FaceEnrollment.MaxFaceImageBytes` | `204800`（200KB） | 人脸图片上限 |
| `FaceEnrollment.CaptureTimeoutSeconds` | `60` | 采集超时秒数 |
| `Devices.FilePath` | `Configuration\devices.json` | 设备清单文件 |
| `DeviceRuntime.WorkerCount` | `4` | 设备任务工作线程数 |
| `DeviceOperationRetry.ScanIntervalSeconds` | `30` | 离线补偿扫描间隔 |

### 5.4 对接方最常踩的坑

1. **按 gRPC 状态码判断成败**：业务结果在 JSON 的 `success`/`code`，不在 RPC 状态里。
2. **期望有 `.proto`**：本项目没有，必须用通用调用 API（见第 2 章）。
3. **把 `PARTIAL_SUCCESS` 当成功**：它表示部分已入离线补偿队列，设备端未必执行完成。
4. **人脸图 >200KB**：会返回 `FACE_TOO_LARGE`。
5. **批量 >500**：会返回 `BATCH_TOO_LARGE`。
6. **明文传输跨网段**：当前是 `Insecure` 明文，跨主机/跨网段请放在受控网络内或外层加 TLS/网关。
