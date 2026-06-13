# 设备交互 API 使用统计


## 1. 总览

项目与设备交互主要分为四类：

| 类别 | 入口/封装 | 主要用途 | 关键文件 |
| --- | --- | --- | --- |
| SDK 生命周期与登录 | `NET_DVR_Init`、`NET_DVR_Login_V40`、`NET_DVR_Logout_V30`、`NET_DVR_Cleanup` | 初始化 SDK、登录门禁/摄像头、释放登录句柄和 SDK 资源 | `ControlEntradaSalidaService.cs`、`DeviceConnectionManager.cs`、`Common.cs` |
| 设备状态/能力查询 | `NET_DVR_GetDeviceAbility`、`NET_DVR_GetDVRConfig` | 校验在线、读取门禁工作状态、读取设备时间 | `DeviceStatusEngine.cs` |
| 人员/权限/人脸同步 | `NET_DVR_STDXMLConfig`、`NET_DVR_StartRemoteConfig`、`NET_DVR_SendWithRecvRemoteConfig`、`NET_DVR_StopRemoteConfig`、`HCNetSDK_Facial.NET_DVR_*` | 下发人员权限、删除人员/人脸、上传人脸、查询人脸、采集人脸 | `Common.cs`、`PermissionRefreshManager.cs` |
| 报警与事件 | `NET_DVR_SetDVRMessageCallBack_V50`、`NET_DVR_SetupAlarmChan_V41`、`NET_DVR_CloseAlarmChan_V30`、`NET_DVR_GetNextRemoteConfig`、`NET_DVR_ControlGateway` | ACS 人脸认证事件入库、历史事件补偿、AIOP 报警联动门禁常闭/恢复 | `FaceEventService.cs`、`CameraDoorInterlockService.cs`、`DoorControlService.cs` |


## 2. SDK 生命周期与登录

### 2.1 `NET_DVR_Init`

| 项目 | 内容 |
| --- | --- |
| P/Invoke 定义 | `HCNetSDK.NET_DVR_Init()` |
| 生产调用 | `Common.InicializarSDKHikVision()`，服务启动时由 `ControlEntradaSalidaService` 调用 |
| 调用时机 | 服务启动、交互式调试模式启动时 |
| 返回处理 | 返回 `false` 时服务启动失败并抛出异常 |
| 关联文件 | `Common.cs`、`ControlEntradaSalidaService.cs` |

使用方式：

1. 服务启动后先初始化日志。
2. 调用 `Common.InicializarSDKHikVision()`。
3. 内部直接调用 `HCNetSDK.NET_DVR_Init()`。
4. 初始化失败时，服务不继续启动设备连接、gRPC、事件服务等模块。

注意事项：

- `Native/` 下必须存在海康 SDK 运行 DLL，并且进程平台需要与 DLL 平台匹配。
- SDK 初始化只在服务生命周期开始时执行一次。

### 2.2 `NET_DVR_Login_V40`

| 项目 | 内容 |
| --- | --- |
| P/Invoke 定义 | `HCNetSDK.NET_DVR_Login_V40(ref NET_DVR_USER_LOGIN_INFO, ref NET_DVR_DEVICEINFO_V40)` |
| 生产调用 | `DeviceConnectionManager.ConnectToDeviceInternal()`，另有旧封装 `Common.Login()` |
| 主要用途 | 登录数据库 `devices` 表加载出的设备，获得 `UserID`，作为后续所有 SDK/ISAPI 调用句柄 |
| 输入来源 | `devices.ip_address`、`devices.port`、`devices.username`、`devices.password` |
| 成功判定 | 返回值 `lUserID >= 0` |
| 失败处理 | `NET_DVR_GetLastError()` 取错误码，标记离线，调度重连 |
| 关联文件 | `DeviceConnectionManager.cs`、`Common.cs` |

生产服务使用流程：

1. `DeviceConnectionManager.LoadAllDevices()` 从数据库读取启用设备。
2. `InitializeDeviceConnectionsAsync()` 并发连接启用设备，受 `maxConcurrentConnections` 限制。
3. 单台设备先获取连接信号量，再获取 `DeviceSdkLock`。
4. 构造 `NET_DVR_USER_LOGIN_INFO`：
   - `sDeviceAddress = ipAddress`
   - `sUserName = username`
   - `sPassword = password`
   - `wPort = port`
   - `byRes3 = new byte[120]`
5. 构造 `NET_DVR_DEVICEINFO_V40`：
   - `struDeviceV30.sSerialNumber = new byte[SERIALNO_LEN]`
   - `byRes2 = new byte[246]`
6. 登录成功后：
   - 保存 `device.UserID`
   - 标记 `IsConnected = true`
   - 读取序列号
   - 建立 `UserID -> deviceId` 索引，用于报警回调反查设备
   - 调用 `DeviceStatusEngine.GetDeviceCapabilities()` 和 `GetDeviceWorkStatus()` 补全能力与状态
   - 更新 `last_used_time`
7. 登录失败后：
   - 调用 `NET_DVR_GetLastError()`
   - 清空 `UserID`
   - 标记离线
   - 调度重连

`Common.Login()` 也封装了相同登录接口，额外对密码错误和用户锁定做了文案区分，但当前设备生命周期主链路使用 `DeviceConnectionManager`。

### 2.3 `NET_DVR_Logout_V30`

| 项目 | 内容 |
| --- | --- |
| P/Invoke 定义 | `HCNetSDK.NET_DVR_Logout_V30(int lUserID)` |
| 生产调用 | `DeviceConnectionManager.DisconnectDevice()` |
| 主要用途 | 断开设备登录会话 |
| 调用时机 | 手动断开、删除/重载设备、服务停止、重连前清理 |
| 资源处理 | 调用后清空 `UserID`、`IsConnected`、`UserID` 索引 |
| 关联文件 | `DeviceConnectionManager.cs` |

使用方式：

1. 获取设备连接信号量。
2. 获取设备级 SDK 锁。
3. 若 `device.UserID >= 0`，调用 `NET_DVR_Logout_V30(userIdToLogout)`。
4. 清除运行时连接状态并触发连接状态变更事件。

### 2.4 `NET_DVR_Cleanup`

| 项目 | 内容 |
| --- | --- |
| P/Invoke 定义 | `HCNetSDK.NET_DVR_Cleanup()` |
| 生产调用 | `ControlEntradaSalidaService.StopInternal()` |
| 主要用途 | 服务停止时释放 SDK 全局资源 |
| 调用顺序 | 停止 gRPC/重试/事件服务 -> 断开所有设备 -> Dispose 管理器 -> `NET_DVR_Cleanup()` |
| 关联文件 | `ControlEntradaSalidaService.cs` |

注意：`NET_DVR_Cleanup()` 在服务停止最后阶段调用，不应早于报警撤防、远程配置停止、设备登出等清理动作。

### 2.5 `NET_DVR_GetLastError` 与 `NET_DVR_GetErrorMsg`

| 接口 | 用途 | 当前使用方式 |
| --- | --- | --- |
| `NET_DVR_GetLastError()` | 获取最近一次 SDK 调用失败错误码 | 登录、状态查询、ISAPI、布防、远程配置、门控制、人脸采集失败后调用 |
| `NET_DVR_GetErrorMsg(ref int err)` | 将错误码转为 SDK 错误描述 | 历史事件补偿失败诊断日志中使用 |

设备写操作的离线补偿判断会识别部分可重试错误码：`1`、`7`、`8`、`9`、`10`、`12`、`13`、`15`、`20`。这些错误通常被视为登录失效、连接失败、收发失败、超时、设备忙或资源问题。

## 3. 状态与能力查询

### 3.1 `NET_DVR_GetDeviceAbility`

| 项目 | 内容 |
| --- | --- |
| P/Invoke 定义 | `HCNetSDK.NET_DVR_GetDeviceAbility(int lUserID, uint dwAbilityType, IntPtr pInBuf, uint dwInLength, IntPtr pOutBuf, uint dwOutLength)` |
| 生产调用 | `DeviceStatusEngine.ValidateConnection()`、`DeviceStatusEngine.GetDeviceCapabilities()` |
| 能力类型 | `HCNetSDK.ACS_ABILITY` |
| 输出缓冲 | 约 `10 KB` 非托管缓冲区 |
| 成功判定 | SDK 返回 `true` |
| 关联文件 | `DeviceStatusEngine.cs` |

使用方式：

- `ValidateConnection(userID)` 用 `ACS_ABILITY` 能力查询作为连接有效性验证。
- `GetDeviceCapabilities(userID)` 同样调用 `ACS_ABILITY`，当前只做基本成功判断，成功后默认赋值：
  - `SupportsRemoteControl = true`
  - `SupportsFaceRecognition = true`
  - `SupportsCardAccess = true`
  - `MaxDoorCount = 1`

注意：当前代码没有解析能力 XML/结构体细节，`MaxDoorCount` 默认为 1。后续如果多门控制需要精准门数量，应从设备能力结果中解析，而不是继续固定默认值。

### 3.2 `NET_DVR_GetDVRConfig` 获取 ACS 工作状态

| 项目 | 内容 |
| --- | --- |
| P/Invoke 定义 | `HCNetSDK.NET_DVR_GetDVRConfig(int lUserID, uint dwCommand, int lChannel, IntPtr lpOutBuffer, uint dwOutBufferSize, ref uint lpBytesReturned)` |
| 命令 | `NET_DVR_GET_ACS_WORK_STATUS_V50` |
| 输出结构 | `NET_DVR_ACS_WORK_STATUS_V50` |
| 生产调用 | `DeviceStatusEngine.GetDeviceWorkStatus()` |
| 通道 | 默认 `-1` |
| 关联文件 | `DeviceStatusEngine.cs` |

使用方式：

1. 先调用 `TestConnectivity(userID)`，用设备时间配置读取确认连接有效。
2. 初始化 `NET_DVR_ACS_WORK_STATUS_V50` 并调用 `Init()`。
3. 分配非托管内存，把结构体写入缓冲。
4. 调用 `NET_DVR_GetDVRConfig(userID, NET_DVR_GET_ACS_WORK_STATUS_V50, channelNo, ptr, size, ref returned)`。
5. 成功后从指针还原 `NET_DVR_ACS_WORK_STATUS_V50`。
6. 读取 `byDoorStatus[0]` 映射设备状态：
   - `1` 或 `4`：在线/正常
   - `2`：常开
   - `3`：常闭
   - 其他：在线但门状态未知
7. 失败时设备仍被视为在线，但状态读取失败，记录错误码。

### 3.3 `NET_DVR_GetDVRConfig` 获取设备时间

| 项目 | 内容 |
| --- | --- |
| 命令 | `NET_DVR_GET_TIMECFG` |
| 输出结构 | `NET_DVR_TIME` |
| 生产调用 | `DeviceStatusEngine.TestConnectivity()` |
| 主要用途 | 快速探测登录句柄是否仍有效、设备是否可响应 SDK 配置读取 |

使用方式：

1. 分配 `NET_DVR_TIME` 缓冲。
2. 调用 `NET_DVR_GetDVRConfig(userID, NET_DVR_GET_TIMECFG, -1, ptr, size, ref returned)`。
3. 返回 `true` 表示连通性测试通过。
4. 返回 `false` 时后续状态检查会获取 `NET_DVR_GetLastError()` 并标记设备离线或异常。

## 4. ISAPI 统一封装

### 4.1 `NET_DVR_STDXMLConfig`

| 项目 | 内容 |
| --- | --- |
| P/Invoke 定义 | `HCNetSDK.NET_DVR_STDXMLConfig(int iUserID, ref NET_DVR_XML_CONFIG_INPUT, ref NET_DVR_XML_CONFIG_OUTPUT)` |
| 项目封装 | `Common.ISAPIQuery()`、`Common.ISAPIBinaryRequest()`、内部 `ExecuteStdXmlConfig()` |
| 主要用途 | 通过 SDK 转发 ISAPI 请求，避免直接使用 HTTP 端口访问设备 |
| 请求 URL 格式 | `"METHOD /ISAPI/...?...format=json"`，例如 `PUT /ISAPI/AccessControl/UserInfo/SetUp?format=json` |
| 请求体 | JSON 字符串转 UTF-8 字节 |
| 返回体 | UTF-8 解码为字符串，或二进制字节数组 |
| 关联文件 | `Common.cs`、`PermissionRefreshManager.cs` |

`ExecuteStdXmlConfig()` 的内存与结构体使用方式：

1. 构造 `NET_DVR_XML_CONFIG_INPUT`，设置：
   - `dwSize`
   - `lpRequestUrl`
   - `dwRequestUrlLen`
   - `lpInBuffer`
   - `dwInBufferSize`
   - `byRes = new byte[32]`
2. 构造 `NET_DVR_XML_CONFIG_OUTPUT`，设置：
   - `dwSize`
   - `lpOutBuffer`
   - `dwOutBufferSize`
   - `lpStatusBuffer`
   - `dwStatusSize`
   - `byRes = new byte[32]`
3. `requestURL` 和 `inputParam` 均按 UTF-8 写入非托管内存并追加 `0` 结尾。
4. `ISAPIQuery()` 输出缓冲使用 `3 MB`，状态缓冲约 `16 KB`。
5. `ISAPIBinaryRequest()` 输出缓冲使用 `1 MB`，状态缓冲约 `4 KB`。
6. 调用失败时取 `NET_DVR_GetLastError()`，返回 `NET_DVR_STDXMLConfig failed, error code= ...`。
7. finally 中释放 URL、payload、输出、状态四块非托管内存。

说明：`ISAPIBinaryRequest()` 当前在生产代码中未发现调用，保留用途是通过 ISAPI 获取二进制结果，例如抓拍图片。

## 5. 生产服务使用的 ISAPI 路由

### 5.1 新增/更新人员与权限

| 项目 | 内容 |
| --- | --- |
| 路由 | `PUT /ISAPI/AccessControl/UserInfo/SetUp?format=json` |
| 调用封装 | `Common.ISAPIQuery()` -> `NET_DVR_STDXMLConfig` |
| 生产调用 | `PermissionRefreshManager.UpdateDeviceAccessCore()`、`UpsertPersonInfoOnDeviceCore()` |
| 业务用途 | 同步员工权限、同步人员基础信息 |
| 成功判定 | 响应 JSON 中 `statusCode == 1`，兼容 `statusString=OK` 且 `subStatusCode` 为空或 `ok` |

权限同步载荷核心结构：

```json
{
  "UserInfo": {
    "employeeNo": "员工编号",
    "name": "姓名",
    "userType": "normal",
    "Valid": {
      "enable": true,
      "beginTime": "2022-01-01T00:00:00",
      "endTime": "2035-12-31T23:59:59",
      "timeType": "local"
    },
    "doorRight": "1",
    "RightPlan": [
      {
        "doorNo": 1,
        "planTemplateNo": "1"
      }
    ]
  }
}
```

人员同步载荷在权限载荷基础上增加：

- `gender`：`male`、`female` 或 `unknown`
- `userVerifyMode = "face"`
- 有效期来自请求的 `ValidFrom` / `ValidTo`，为空时使用默认时间段

门权限生成规则：

- 禁用权限时：`doorRight = ""`，`RightPlan = []`
- 启用权限时：按 `connection.Capabilities.MaxDoorCount` 生成门号，当前默认多为 `1`
- `RightPlan` 中每个门使用 `planTemplateNo = "1"`

失败处理：

- SDK/网络类失败会进入可重试判断，并可写入 `device_operation_retry_states` 等离线补偿状态。
- 响应内容能解析但业务失败时，提取 `statusString`、`subStatusCode`、`errorMsg` 组成错误信息。

### 5.2 删除人员

| 项目 | 内容 |
| --- | --- |
| 路由 | `PUT /ISAPI/AccessControl/UserInfo/Delete?format=json` |
| 调用封装 | `Common.ISAPIQuery()` |
| 生产调用 | `PermissionRefreshManager.DeletePersonOnDeviceCore()` |
| 业务用途 | 从设备端删除员工基础信息 |
| 成功判定 | 同 `statusCode == 1` / `statusString=OK` 规则 |

请求体结构：

```json
{
  "UserInfoDelCond": {
    "EmployeeNoList": [
      {
        "employeeNo": "员工编号"
      }
    ]
  }
}
```

删除人员时，如果是“删除人员和人脸”组合操作，代码先删除人脸，再删除人员。这样可以避免设备端人员删除后人脸库残留或人脸删除条件失效。

### 5.3 上传人脸

| 项目 | 内容 |
| --- | --- |
| 路由 | `PUT /ISAPI/Intelligent/FDLib/FDSetUp?format=json` |
| SDK 命令 | `NET_DVR_FACE_DATA_RECORD` |
| 调用方式 | `NET_DVR_StartRemoteConfig` + `NET_DVR_SendWithRecvRemoteConfig` + `NET_DVR_StopRemoteConfig` |
| 生产调用 | `PermissionRefreshManager.UploadFaceToDeviceInternal()` |
| 人脸库 | `faceLibType = "blackFD"`、`FDID = "1"` |
| 图片限制 | 最大 `200 KB` |

上传流程：

1. 确认人员对象存在且 `HasFace = true`。
2. 校验 `FaceImageBytes.Length <= 200 * 1024`。
3. 将路由字符串写入 UTF-8 非托管内存。
4. 调用 `NET_DVR_StartRemoteConfig(device.UserID, NET_DVR_FACE_DATA_RECORD, urlPtr, urlBytes.Length, null, IntPtr.Zero)`。
5. 构造 JSON 载荷：

```json
{
  "faceLibType": "blackFD",
  "FDID": "1",
  "FPID": "员工编号"
}
```

6. 构造 `NET_DVR_JSON_DATA_CFG`：
   - `lpJsonData` / `dwJsonDataSize` 指向上述 JSON
   - `lpPicData` / `dwPicDataSize` 指向人脸图片二进制
   - `byRes = new byte[256]`
7. 调用 `NET_DVR_SendWithRecvRemoteConfig()`。
8. `NET_SDK_CONFIG_STATUS_SUCCESS` 或 `NET_SDK_CONFIG_STATUS_FINISH` 视为成功。
9. 其他状态读取响应缓冲，进入失败或可重试判断。
10. finally 中停止远程配置并释放所有非托管内存。

### 5.4 删除人脸

| 项目 | 内容 |
| --- | --- |
| 路由 | `PUT /ISAPI/Intelligent/FDLib/FDSearch/Delete?format=json&FDID=1&faceLibType=blackFD` |
| 调用封装 | `Common.ISAPIQuery()` |
| 生产调用 | `PermissionRefreshManager.DeleteFaceOnDeviceCore()` |
| 业务用途 | 从设备端人脸库删除指定员工人脸 |

请求体结构：

```json
{
  "FPID": [
    {
      "value": "员工编号"
    }
  ]
}
```

响应处理：

- 设备可能返回 multipart 格式，代码先用 `ExtractJsonFromMultipart()` 提取 JSON。
- 如果设备返回“人脸本来不存在”的语义，`DeviceDeleteResponsePolicy.IsDeleteFaceAlreadyAbsent()` 会把它视为成功。
- 其他非成功响应按设备错误处理。

### 5.5 查询人脸

| 项目 | 内容 |
| --- | --- |
| 路由 | `POST /ISAPI/Intelligent/FDLib/FDSearch?format=json` |
| 调用封装 | `Common.ISAPIQuery()` |
| 生产调用 | `PermissionRefreshManager.QueryFaceOnDevice()` |
| 业务用途 | 按员工编号查询设备端人脸 |
| 查询范围 | `faceLibType = "blackFD"`、`FDID = "1"`、最多返回 1 条 |

请求体结构：

```json
{
  "searchResultPosition": 0,
  "maxResults": 1,
  "faceLibType": "blackFD",
  "FDID": "1",
  "FPID": "员工编号"
}
```

响应解析方式：

- 先从 multipart 中提取 JSON。
- 成功判断沿用 `statusCode == 1` / `statusString=OK` 规则。
- 人脸图片字段按兼容顺序读取：
  - `facePicBinary`
  - `FacePicBinary`
  - `facePic`
  - `FacePic`
  - `modelData`
- `numOfMatches > 0` 或 `totalMatches > 0` 视为查询成功；否则检查 `MatchList` / `FaceDataRecord` 是否有数据。

## 6. 远程配置通道

### 6.1 `NET_DVR_StartRemoteConfig`

| 场景 | 命令 | 输入结构/内容 | 后续读取/发送 | 文件 |
| --- | --- | --- | --- | --- |
| 上传人脸 | `NET_DVR_FACE_DATA_RECORD` | ISAPI URL 字符串指针 | `NET_DVR_SendWithRecvRemoteConfig` 发送 `NET_DVR_JSON_DATA_CFG` | `PermissionRefreshManager.cs` |
| 历史 ACS 事件补偿 | `NET_DVR_GET_ACS_EVENT` | `NET_DVR_ACS_EVENT_COND` | `NET_DVR_GetNextRemoteConfig` 读取 `NET_DVR_ACS_EVENT_CFG` | `FaceEventService.cs` |
| 人脸采集 | `HCNetSDK_Facial.NET_DVR_CAPTURE_FACE_INFO` | `NET_DVR_CAPTURE_FACE_COND` | `HCNetSDK_Facial.NET_DVR_GetNextRemoteConfig` 读取 `NET_DVR_CAPTURE_FACE_CFG` | `PermissionRefreshManager.cs` |

共同规则：

- 启动失败返回 handle `< 0`，立即调用 `NET_DVR_GetLastError()`。
- 成功 handle 必须在 finally 或停止逻辑中调用 `NET_DVR_StopRemoteConfig()` / `HCNetSDK_Facial.NET_DVR_StopRemoteConfig()`。
- 所有远程配置调用都应持有设备级 SDK 锁。

### 6.2 `NET_DVR_SendWithRecvRemoteConfig`

当前只用于人脸上传。

| 项目 | 内容 |
| --- | --- |
| 输入 | `NET_DVR_JSON_DATA_CFG`，包含人脸元数据 JSON 和图片二进制 |
| 输出 | 约 `2048` 字节响应缓冲 |
| 成功状态 | `NET_SDK_CONFIG_STATUS_SUCCESS`、`NET_SDK_CONFIG_STATUS_FINISH` |
| 可重试状态 | `NEEDWAIT`、`EXCEPTION`；`FAILED` 在响应为空或属于传输类错误时可重试 |

### 6.3 `NET_DVR_GetNextRemoteConfig` 读取历史 ACS 事件

| 项目 | 内容 |
| --- | --- |
| 命令来源 | `NET_DVR_StartRemoteConfig(..., NET_DVR_GET_ACS_EVENT, ...)` |
| 输入条件 | `NET_DVR_ACS_EVENT_COND` |
| 输出结构 | `NET_DVR_ACS_EVENT_CFG` |
| 生产调用 | `FaceEventService.FetchHistory()` |

历史补偿查询条件：

- `dwMajor = MAJOR_EVENT`
- `dwMinor = 0`
- `struStartTime`：断点时间或当前时间向前 `CompensationLookbackMinutes`
- `struEndTime`：补偿 fence 时间或当前时间
- `dwBeginSerialNo`：断点流水号 + 1；没有断点时为 0
- `byPicEnable = 1`
- `byTimeType = 0`
- `bySearchType = 1`
- `dwIOTChannelNo = 1`

兼容降级策略：

1. 如果带 `BeginSerialNo` 启动失败，改为 `dwBeginSerialNo = 0` 重试。
2. 如果带图片失败，改为 `byPicEnable = 0` 重试。
3. 如果 `dwMajor = MAJOR_EVENT` 失败，改为 `dwMajor = 0` 重试。
4. 最后退回 `bySearchType = 0`、`dwIOTChannelNo = 0` 重试。

读取循环处理：

- `NET_SDK_CONFIG_STATUS_SUCCESS`：还原 `NET_DVR_ACS_EVENT_CFG`，过滤非人脸认证事件，构建入库记录。
- `NET_SDK_CONFIG_STATUS_FINISH`：补偿结束。
- `NET_SDK_CONFIG_STATUS_NEEDWAIT`：等待约 `200 ms` 后继续。
- `NET_SDK_CONFIG_STATUS_FAILED`：记录错误码，可继续短暂重试。
- `NET_SDK_CONFIG_STATUS_EXCEPTION` 或未知状态：记录并终止补偿。

### 6.4 `HCNetSDK_Facial.NET_DVR_*` 人脸采集

| 接口 | 用途 |
| --- | --- |
| `HCNetSDK_Facial.NET_DVR_StartRemoteConfig` | 启动人脸采集远程配置 |
| `HCNetSDK_Facial.NET_DVR_GetNextRemoteConfig` | 轮询采集进度和图片 |
| `HCNetSDK_Facial.NET_DVR_StopRemoteConfig` | 停止采集通道 |

生产调用位置：`PermissionRefreshManager.CaptureFaceFromEnrollmentDevice()`。

使用方式：

1. 找到名称为“人脸录入仪”的设备。
2. 若未连接则允许立即重连。
3. 构造 `NET_DVR_CAPTURE_FACE_COND`，设置 `dwSize`。
4. 调用 `HCNetSDK_Facial.NET_DVR_StartRemoteConfig(device.UserID, NET_DVR_CAPTURE_FACE_INFO, condPtr, size, null, IntPtr.Zero)`。
5. 最多轮询 `100` 次，每次调用 `HCNetSDK_Facial.NET_DVR_GetNextRemoteConfig(handle, ref faceCfg, size)`。
6. `NET_SDK_GET_NEXT_STATUS_SUCCESS` 且 `byCaptureProgress == 100` 时读取 `pFacePicBuffer` / `dwFacePicSize`。
7. 图片超过 `200 KB` 视为失败；否则转 Base64 返回。
8. `NEED_WAIT` 时等待 `100 ms`。
9. `FINISH` 但无有效人脸、`FAILED`、超时均返回失败。
10. finally 中停止远程配置并释放条件结构内存。

## 7. 报警布防、回调与事件

### 7.1 `NET_DVR_SetDVRMessageCallBack_V50`

| 项目 | 内容 |
| --- | --- |
| P/Invoke 定义 | `HCNetSDK.NET_DVR_SetDVRMessageCallBack_V50(int iIndex, MSGCallBack fMessageCallBack, IntPtr pUser)` |
| 生产调用 | `FaceEventService.Start()` |
| 回调函数 | `FaceEventService.AlarmMessageCallback()` |
| 主要用途 | 接收 ACS 人脸认证报警与 AIOP 视频报警 |
| 关键要求 | 回调委托保存到字段 `alarmCallback`，避免被 GC 回收 |

使用方式：

1. 事件服务启动时创建 `alarmCallback = AlarmMessageCallback`。
2. 调用 `NET_DVR_SetDVRMessageCallBack_V50(0, alarmCallback, IntPtr.Zero)`。
3. 注册失败时取 `NET_DVR_GetLastError()` 并终止事件服务启动。

### 7.2 `NET_DVR_SetupAlarmChan_V41`

| 项目 | 内容 |
| --- | --- |
| P/Invoke 定义 | `HCNetSDK.NET_DVR_SetupAlarmChan_V41(int lUserID, ref NET_DVR_SETUPALARM_PARAM lpSetupParam)` |
| 生产调用 | `FaceEventService.SetupAlarm()` |
| 主要用途 | 对已登录设备布防，使设备向 SDK 回调推送报警 |
| 成功判定 | 返回 alarm handle `>= 0` |
| 失败处理 | 记录错误码，进入报警布防重试 |

布防参数使用方式：

- 创建 `NET_DVR_SETUPALARM_PARAM` 并调用 `Init()`。
- `byLevel = 1`。
- `byDeployType` 由配置 `FaceEvent.AlarmDeployType` 决定：
  - `0`：客户端布防，依赖设备离线事件上传。
  - `1`：实时布防，历史事件通过主动拉取补偿。
  - 非 `0/1` 值回退为 `0`。
- 布防前若设备已有 `AlarmHandle >= 0`，先调用 `NET_DVR_CloseAlarmChan_V30(previousAlarmHandle)`。
- 布防成功后写回 `device.AlarmHandle`。

### 7.3 `NET_DVR_CloseAlarmChan_V30`

| 项目 | 内容 |
| --- | --- |
| P/Invoke 定义 | `HCNetSDK.NET_DVR_CloseAlarmChan_V30(int lAlarmHandle)` |
| 生产调用 | `FaceEventService.SetupAlarm()`、`CloseAlarm()`、设备断开处理 |
| 主要用途 | 关闭报警布防通道 |

使用时机：

- 重新布防前关闭旧 handle。
- 设备断开时关闭当前报警 handle。
- 事件服务停止或设备清理时关闭布防。

### 7.4 `COMM_ALARM_ACS` 回调解析

| 项目 | 内容 |
| --- | --- |
| 回调命令 | `HCNetSDK.COMM_ALARM_ACS` |
| 结构体 | `NET_DVR_ACS_ALARM_INFO`，兼容较短 buffer 的 `NET_DVR_ACS_ALARM_INFO_V1` |
| 生产调用 | `FaceEventService.AlarmMessageCallback()` |
| 业务用途 | 门禁人脸认证通过/失败事件入库 |

处理流程：

1. 非 `COMM_ALARM_ACS` 或 `alarmInfo == IntPtr.Zero` 直接忽略。
2. 根据 `bufferLength` 判断使用 `NET_DVR_ACS_ALARM_INFO` 或 `NET_DVR_ACS_ALARM_INFO_V1`。
3. 过滤非人脸认证事件：只保留 `IsFaceVerifyMinor(dwMinor)` 返回 true 的事件。
4. 通过 `alarmer.sDeviceIP` 反查运行时设备；必要时用 `lUserID` 反查。
5. 对 `byCurrentEvent == 2` 的离线事件，结合 `OfflineCompensationEnabled` 和 `AlarmDeployType` 判断是否忽略。
6. 构建 `FaceEventRecord`：
   - 员工编号：优先扩展字段 `byEmployeeNo`，兼容 `dwEmployeeNo`
   - 时间：SDK 时间结构转本地时间
   - 事件类型：根据 minor 判断通过/失败
   - 流水号：`dwSerialNo`
   - 图片：从回调图片指针复制，或识别 URL 形式
7. 如果设备正在历史补偿，实时事件先进入补偿缓冲；否则直接入队落库。
8. 更新设备 `LastSerialNo` 和 `LastFaceEventTime`。

### 7.5 `COMM_UPLOAD_AIOP_VIDEO` / `0x4021` 回调解析

| 项目 | 内容 |
| --- | --- |
| 回调命令 | `0x4021`，代码中命名为 `CommUploadAiopVideo` / `COMM_UPLOAD_AIOP_VIDEO` |
| 生产调用 | `FaceEventService.HandleAiopAlarm()` |
| 业务用途 | 摄像头 AIOP 报警联动门禁常闭 |
| 联动服务 | `CameraDoorInterlockService` |

处理流程：

1. 仅当 `CameraAlarmDoorInterlock.Enabled = true` 时处理。
2. 通过 `NET_DVR_ALARMER.sDeviceIP` 或 `lUserID` 反查摄像头设备。
3. 检查该摄像头是否在联动映射中。
4. 解析 AIOP buffer：
   - offset `0` 读取 `headerLen`
   - offset `88` 读取 `jsonLen`
   - offset `92` 读取 `picLen`
   - 从 `headerLen` 开始按 UTF-8 读取 JSON
   - 从 JSON 中提取 `ModelId`、`TargetType`、`RuleId`、`RuleName`、`AlertInfoCount` 等摘要字段
5. 调用 `CameraDoorInterlockService.OnCameraAlarm(camera, metadata)`。

## 8. 门禁控制

### 8.1 `NET_DVR_ControlGateway`

| 项目 | 内容 |
| --- | --- |
| P/Invoke 定义 | `HCNetSDK.NET_DVR_ControlGateway(int lUserID, int lGatewayIndex, uint dwStaic)` |
| 生产调用 | `DoorControlService.SetDoorMode()` |
| 业务用途 | 设置门禁门状态，当前用于 AIOP 报警联动常闭和窗口结束恢复 |
| 门号校验 | `doorNo > 0 && doorNo <= HCNetSDK.MAX_DOOR_NUM_256` |
| 成功判定 | SDK 返回 `true` |

当前使用的命令映射：

| 业务模式 | SDK 命令 |
| --- | --- |
| `DoorControlMode.AlwaysClose` | `NET_DVR_GATEWAY_CONTROL_ALWAYS_CLOSE` |
| `DoorControlMode.Ordinary` | `NET_DVR_GATEWAY_CONTROL_CLOSE` |

使用方式：

1. 从 `DeviceConnectionInfo` 读取 `UserID` 和 `IsConnected`。
2. 校验设备在线且 `UserID >= 0`。
3. 获取设备级 SDK 锁，默认门控制锁等待时间来自 `CameraAlarmDoorInterlock.DoorControlSdkLockTimeoutMs`，最小 `100 ms`。
4. 调用 `NET_DVR_ControlGateway(userId, doorNo, command)`。
5. 成功记录操作日志。
6. 失败调用 `NET_DVR_GetLastError()`，返回错误码和错误信息。

AIOP 联动语义：

- 同一摄像头窗口内重复报警不刷新窗口，也不重复下发常闭。
- 多摄像头可共享同一个门目标，只有最后一个活动窗口结束时才恢复普通状态。
- 服务停止时会 best-effort 恢复仍处于联动常闭的门。
- 恢复失败按 `RestoreRetryAttempts` 和 `RestoreRetryIntervalMs` 重试。

## 9. 工具脚本中的设备 API

`tools/` 下脚本主要用于现场排查、能力探测和报警抓包。它们不是服务运行必需链路，但会直接与设备交互。

### 9.1 `tools/SdkAlarmCapture.cs`

| 接口 | 用途 |
| --- | --- |
| `NET_DVR_Init` / `NET_DVR_Cleanup` | 独立进程初始化和清理 SDK |
| `NET_DVR_SetConnectTime` | 设置连接超时和尝试次数 |
| `NET_DVR_SetReconnect` | 设置 SDK 自动重连参数 |
| `NET_DVR_GetSDKVersion` / `NET_DVR_GetSDKBuildVersion` | 输出 SDK 版本与构建版本，便于现场比对 |
| `NET_DVR_Login_V40` / `NET_DVR_Logout_V30` | 登录/登出目标设备 |
| `NET_DVR_SetDVRMessageCallBack_V50` | 注册报警回调 |
| `NET_DVR_SetupAlarmChan_V41` / `NET_DVR_CloseAlarmChan_V30` | 布防并关闭报警通道 |

使用特点：

- 运行时显式设置 `Native` 目录到 DLL 搜索路径。
- 把初始化、登录、回调注册、布防、等待、撤防、登出、清理各阶段写入 JSON 报告。
- 支持配置 `DeployType`、`AlarmTypeUrl`、`BrokenNetHttp`、`Level` 等布防参数，适合排查不同设备/固件报警行为差异。

### 9.2 `tools/CaptureHikvisionSdkAlarms.ps1`

用途与 `SdkAlarmCapture.cs` 类似：通过 PowerShell 加载项目内 `HCNetSDK.cs`，调用 SDK 初始化、登录、注册回调、布防、等待、撤防、登出，用于快速抓取报警回调。

主要接口：

- `NET_DVR_Init`
- `NET_DVR_Login_V40`
- `NET_DVR_SetDVRMessageCallBack_V50`
- `NET_DVR_SetupAlarmChan_V41`
- `NET_DVR_CloseAlarmChan_V30`
- `NET_DVR_Logout_V30`
- `NET_DVR_Cleanup`
- `NET_DVR_GetLastError`

### 9.3 `tools/CaptureHikvisionAlertStream.ps1`

| 项目 | 内容 |
| --- | --- |
| 协议 | 直接 HTTP |
| 路由 | `GET /ISAPI/Event/notification/alertStream` |
| 认证 | `System.Net.NetworkCredential` 用户名/密码 |
| 主要用途 | 直接从设备 HTTP alertStream 抓取事件流，与 SDK 报警回调结果对照 |

该脚本不走 `HCNetSDK.NET_DVR_STDXMLConfig`，而是直接用 `HttpWebRequest` 请求设备 HTTP 端口，读取 multipart 事件流并保存 raw/json 结果。

### 9.4 `tools/GetAcsAbility.ps1`

主要接口：

- `NET_DVR_Init`
- `NET_DVR_Login_V40`
- `NET_DVR_GetDeviceAbility(ACS_ABILITY)`
- `NET_DVR_GetLastError`
- `NET_DVR_Logout_V30`

用途：读取门禁能力，确认设备是否支持相关 ACS 能力。

### 9.5 `tools/ProbeAccessControlSupportedRoutes.ps1`

主要接口：

- `NET_DVR_Init`
- `NET_DVR_Login_V40`
- `NET_DVR_STDXMLConfig`
- `NET_DVR_GetLastError`
- `NET_DVR_Logout_V30`

使用方式：

1. 先请求 `GET /ISAPI/AccessControl/capabilities`。
2. 解析返回 XML 中 `isSupport*` 能力字段。
3. 将能力字段转换为候选路由名。
4. 批量探测：
   - `GET /ISAPI/AccessControl/{routeName}?format=json`
   - `GET /ISAPI/AccessControl/{routeName}/capabilities?format=json`
   - `GET /ISAPI/AccessControl/{routeName}/1?format=json`
   - `GET /ISAPI/AccessControl/{routeName}/1/capabilities?format=json`
5. 输出 `tools/ProbeAccessControlSupportedRoutes.last.json`。

### 9.6 安全帽/口罩相关探测和配置脚本

相关脚本：

- `ProbeHelmetDetection.ps1`
- `ProbeHelmetNameVariants.ps1`
- `ProbeHelmetRoutes.ps1`
- `SetHelmetDetection.ps1`
- `SetMaskDetectionSameValue.ps1`
- `SnapshotHelmetRelatedConfig.ps1`

共同特点：

- 大多通过 `NET_DVR_STDXMLConfig` 调用 ISAPI。
- 用 `NET_DVR_Login_V40` 获取 `UserID`。
- 探测或写入 `/ISAPI/AccessControl/...`、`/ISAPI/Intelligent/...`、`/ISAPI/System/...`、`/ISAPI/Event/...` 下的候选路由。
- `SnapshotHelmetRelatedConfig.ps1` 还会用 `NET_DVR_GetDVRConfig` 读取部分传统配置命令。
- 这些脚本属于现场能力探测/配置尝试工具，不属于当前门禁服务主运行链路。

典型路由包括：

- `GET /ISAPI/System/deviceInfo`
- `GET /ISAPI/System/capabilities?type=all`
- `GET /ISAPI/AccessControl/capabilities`
- `GET /ISAPI/AccessControl/AcsCfg?format=json`
- `GET /ISAPI/AccessControl/CardReaderCfg/1?format=json`
- `GET /ISAPI/AccessControl/MaskDetection?format=json`
- `PUT /ISAPI/AccessControl/MaskDetection?format=json`
- `GET /ISAPI/AccessControl/SafetyHelmetDetection?format=json`
- `PUT /ISAPI/AccessControl/SafetyHelmetDetection?format=json`
- `GET /ISAPI/Intelligent/channels/{ChannelId}/safetyHelmetDetection/advanceConfiguration`

## 10. 统一错误处理与补偿策略

### 10.1 SDK/ISAPI 调用前置检查

人员、权限、人脸写入前通常执行：

1. 检查设备对象不为空。
2. 调用 `TryEnsureDeviceConnected(device, allowReconnect)`。
3. 检查 `device.UserID >= 0`。
4. 获取 `DeviceSdkLock`。
5. 执行 SDK/ISAPI 调用。
6. 根据返回值、错误码、响应内容判断成功、失败或进入补偿。

### 10.2 响应成功判定

ISAPI JSON 响应成功规则：

- 首选：`statusCode` 数值或字符串等于 `1`。
- 兼容：没有明确 `statusCode` 时，`statusString` 等于 `OK`/`ok` 且 `subStatusCode` 为空或 `ok`。

错误消息提取：

- `statusString`
- `subStatusCode`
- `errorMsg`
- 如果无法解析 JSON，则保留原始响应文本。

### 10.3 可重试判定

以下情况通常进入离线补偿或重试：

- SDK 错误码属于可重试集合：`1`、`7`、`8`、`9`、`10`、`12`、`13`、`15`、`20`。
- 文本中包含连接、发送、接收、超时、设备忙、socket、timeout、disconnected、connection、network 等关键词。
- 远程配置返回 `NEEDWAIT` 或 `EXCEPTION`。
- 远程配置返回 `FAILED` 且响应为空或响应内容可识别为传输类失败。

非传输类业务失败通常直接返回失败，不进入补偿。

### 10.4 资源释放要求

项目中每类设备交互都需要明确释放资源：

- 登录成功的 `UserID`：通过 `NET_DVR_Logout_V30` 释放。
- 全局 SDK：服务停止最后调用 `NET_DVR_Cleanup`。
- 报警布防 handle：通过 `NET_DVR_CloseAlarmChan_V30` 释放。
- 远程配置 handle：通过 `NET_DVR_StopRemoteConfig` 或 `HCNetSDK_Facial.NET_DVR_StopRemoteConfig` 释放。
- 非托管内存：所有 `Marshal.AllocHGlobal` 分配的指针在 finally 中 `Marshal.FreeHGlobal`。
- 回调委托：保存为字段，防止 GC 回收导致 SDK 回调异常。

## 11. 当前实际使用 API 清单

### 11.1 生产服务 SDK 函数

| API | 使用位置 | 业务语义 |
| --- | --- | --- |
| `NET_DVR_Init` | `Common.InicializarSDKHikVision()` | SDK 初始化 |
| `NET_DVR_Cleanup` | `ControlEntradaSalidaService.StopInternal()` | SDK 清理 |
| `NET_DVR_Login_V40` | `DeviceConnectionManager.ConnectToDeviceInternal()`、`Common.Login()` | 登录设备 |
| `NET_DVR_Logout_V30` | `DeviceConnectionManager.DisconnectDevice()` | 登出设备 |
| `NET_DVR_GetLastError` | 多处 | 获取 SDK 错误码 |
| `NET_DVR_GetErrorMsg` | `FaceEventService.GetSdkErrorMessage()` | 获取错误码描述 |
| `NET_DVR_GetDeviceAbility` | `DeviceStatusEngine` | 查询 ACS 能力/验证连接 |
| `NET_DVR_GetDVRConfig` | `DeviceStatusEngine` | 获取设备时间和 ACS 工作状态 |
| `NET_DVR_STDXMLConfig` | `Common.ExecuteStdXmlConfig()` | SDK 转发 ISAPI |
| `NET_DVR_StartRemoteConfig` | `PermissionRefreshManager`、`FaceEventService` | 上传人脸/历史事件补偿 |
| `NET_DVR_SendWithRecvRemoteConfig` | `PermissionRefreshManager.UploadFaceToDeviceInternal()` | 发送人脸 JSON 与图片 |
| `NET_DVR_GetNextRemoteConfig` | `FaceEventService.FetchHistory()` | 拉取历史 ACS 事件 |
| `NET_DVR_StopRemoteConfig` | `PermissionRefreshManager`、`FaceEventService` | 停止远程配置 |
| `NET_DVR_SetDVRMessageCallBack_V50` | `FaceEventService.Start()` | 注册报警回调 |
| `NET_DVR_SetupAlarmChan_V41` | `FaceEventService.SetupAlarm()` | 报警布防 |
| `NET_DVR_CloseAlarmChan_V30` | `FaceEventService` | 撤防/关闭报警通道 |
| `NET_DVR_ControlGateway` | `DoorControlService.SetDoorMode()` | 门禁常闭/恢复普通状态 |
| `HCNetSDK_Facial.NET_DVR_StartRemoteConfig` | `PermissionRefreshManager.CaptureFaceFromEnrollmentDevice()` | 启动人脸采集 |
| `HCNetSDK_Facial.NET_DVR_GetNextRemoteConfig` | 同上 | 轮询采集结果 |
| `HCNetSDK_Facial.NET_DVR_StopRemoteConfig` | 同上 | 停止采集 |

### 11.2 生产服务 ISAPI 路由

| 路由 | 方法 | 用途 | 调用方式 |
| --- | --- | --- | --- |
| `/ISAPI/AccessControl/UserInfo/SetUp?format=json` | `PUT` | 新增/更新人员、权限、有效期、门权限计划 | `NET_DVR_STDXMLConfig` |
| `/ISAPI/AccessControl/UserInfo/Delete?format=json` | `PUT` | 删除人员 | `NET_DVR_STDXMLConfig` |
| `/ISAPI/Intelligent/FDLib/FDSetUp?format=json` | `PUT` | 上传人脸到人脸库 | `NET_DVR_FACE_DATA_RECORD` 远程配置 |
| `/ISAPI/Intelligent/FDLib/FDSearch/Delete?format=json&FDID=1&faceLibType=blackFD` | `PUT` | 删除人脸 | `NET_DVR_STDXMLConfig` |
| `/ISAPI/Intelligent/FDLib/FDSearch?format=json` | `POST` | 查询人脸 | `NET_DVR_STDXMLConfig` |

### 11.3 工具脚本额外使用 API

| API/路由 | 工具 | 用途 |
| --- | --- | --- |
| `NET_DVR_SetConnectTime` | `SdkAlarmCapture.cs` | 设置 SDK 连接超时 |
| `NET_DVR_SetReconnect` | `SdkAlarmCapture.cs` | 设置 SDK 自动重连 |
| `NET_DVR_GetSDKVersion` | `SdkAlarmCapture.cs` | 输出 SDK 版本 |
| `NET_DVR_GetSDKBuildVersion` | `SdkAlarmCapture.cs` | 输出 SDK build |
| `GET /ISAPI/Event/notification/alertStream` | `CaptureHikvisionAlertStream.ps1` | 直接 HTTP 抓取事件流 |
| `GET /ISAPI/AccessControl/capabilities` | 多个 probe 脚本 | 探测门禁 ISAPI 能力 |
| `GET/PUT /ISAPI/AccessControl/MaskDetection...` | `SetMaskDetectionSameValue.ps1` 等 | 口罩检测配置探测/写入 |
| `GET/PUT /ISAPI/AccessControl/SafetyHelmetDetection...` | `SetHelmetDetection.ps1` 等 | 安全帽检测配置探测/写入 |

## 12. 后续维护建议

1. 新增设备交互时优先经过统一封装：SDK 结构体/非托管内存统一释放，ISAPI 请求优先走 `Common.ISAPIQuery()` 或清晰的新封装。
2. 同一设备的 SDK/ISAPI/远程配置调用必须继续持有 `DeviceSdkLock`。
3. 新增 ISAPI 路由时同步补充：路由、方法、请求体、响应成功判定、失败是否可重试。
4. 新增远程配置命令时同步补充：命令号、输入结构、输出结构、状态码处理、停止 handle 的位置。
5. 多门设备支持需要优先完善 `NET_DVR_GetDeviceAbility(ACS_ABILITY)` 的结果解析，否则 `doorRight` 和 `RightPlan` 仍会按默认 1 门生成。
6. 真实设备联调时建议同时保留 SDK 报警抓包和 HTTP alertStream 抓包，便于判断问题发生在设备上报、SDK 回调还是业务解析层。