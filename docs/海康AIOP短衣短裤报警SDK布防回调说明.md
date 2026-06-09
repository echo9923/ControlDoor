# 海康 AIOP 短衣短裤报警 SDK 布防回调说明

本文记录 2026-06-09 对网络摄像头短衣短裤/工装检测模型报警的打通过程、SDK 布防链路、实时事件格式、字段含义、解析方式和排障点。本文面向后续开发、联调和维护人员，重点说明如何稳定获取事件，以及拿到事件后如何拆解全部字段。

## 1. 验证结论

本次采用的是 **海康设备网络 SDK 布防回调**，不是主动轮询查询历史报警。

实际调用链路如下：

```text
NET_DVR_Init
  -> NET_DVR_Login_V40
  -> NET_DVR_SetDVRMessageCallBack_V50
  -> NET_DVR_SetupAlarmChan_V41
  -> 设备主动推送 SDK 回调
  -> 回调中复制 pAlarmInfo 原始 buffer
  -> 按 lCommand 和结构头拆分 JSON + JPEG
  -> NET_DVR_CloseAlarmChan_V30
  -> NET_DVR_Logout_V30
  -> NET_DVR_Cleanup
```

实测结果：

| 项目 | 结果 |
| --- | --- |
| SDK 登录 | 成功，`NET_DVR_Login_V40` 返回 `userId=0` |
| V50 回调注册 | 成功，`NET_DVR_SetDVRMessageCallBack_V50` 返回 `true` |
| 实时布防 | 成功，`NET_DVR_SetupAlarmChan_V41` 返回 `alarmHandle=0` |
| 报警回调 | 30 秒内收到 25 条 |
| SDK 命令 | `lCommand=0x4021` |
| 命令含义 | `COMM_UPLOAD_AIOP_VIDEO` |
| 对应结构 | `NET_AIOP_VIDEO_HEAD` |
| 事件来源 | AI 开放平台 AIOP 视频检测报警 |
| 实际命中标签 | `Short_Sleeve_top`，即短袖上衣 |
| 图片数据 | 默认随 SDK buffer 二进制上传，JPEG，样本分辨率 `1920x1104` |

注意：海康 SDK 句柄返回值 `0` 是有效句柄，失败通常是 `< 0`，不能把 `userId=0` 或 `alarmHandle=0` 当失败。

## 2. 设备和模型信息

本次联调设备：

| 项目 | 值 |
| --- | --- |
| 设备 IP | `169.254.103.5` |
| SDK 端口 | `8000` |
| HTTP 端口 | `80` |
| 设备型号 | `DS-2XA8D45F/GF-IZS` |
| 固件 | `V5.8.30 build 241231` |
| 通道 | `1`，流通道 `101` |
| SDK 本地版本 | `0.6.1 (0x00060001)` |
| SDK Build | `0x06010930` |

账号密码不要写入文档或提交到仓库。联调命令中使用环境变量或运行时参数传入即可。

设备 AIOP 模型管理接口中查到的模型：

| 字段 | 值 |
| --- | --- |
| `MPName` | `工装检测` |
| `MPID` | `cbb1296408500001f8ad757d742b1bd9cbb129640850000165e2ef1d25fd14f` |
| `status` | `1` |
| `engine` | `[1]` |
| `modelId` | `825b148172704f19a78854290b385419` |

标签映射：

| `type` | 标签 | 含义 |
| --- | --- | --- |
| `1` | `Shorts` | 短裤 |
| `2` | `Short_Sleeve_top` | 短袖上衣 |
| `3` | `Long_Sleeve_top` | 长袖上衣 |

本次实际报警样本全部命中 `type=2`，也就是短袖上衣。`type=1` 短裤的标签含义已由设备模型元数据确认，但本次 30 秒样本里没有触发短裤报警，因此短裤实际样例还需要现场触发一次再补充。

## 3. 关键 SDK 文档依据

按照仓库要求，SDK 调用前查阅了 `设备网络SDK编程指南（明眸-以人为中心）` 下的本地文档：

| 文档 | 用途 |
| --- | --- |
| `设备网络SDK编程指南（明眸-以人为中心）/cache/raw-html/sdk/structures/NET_DVR_SETUPALARM_PARAM.html` | 确认布防参数结构体字段、布防类型、图片 URL 位 |
| `设备网络SDK编程指南（明眸-以人为中心）/cache/raw-html/sdk/definitions/NET_DVR_SetDVRMessageCallBack_V50.html` | 确认报警回调注册接口、`lCommand` 和 `pAlarmInfo` 的关系 |
| `设备网络SDK编程指南（明眸-以人为中心）/cache/raw-html/sdk/structures/NET_DVR_ALARMER.html` | 确认回调中报警设备信息字段 |

本地 V50 文档没有列出 `0x4021`，但在线新版本 SDK 文档和现场数据均指向 `COMM_UPLOAD_AIOP_VIDEO / NET_AIOP_VIDEO_HEAD`。现场 buffer 也通过头部长度、JSON 长度、图片长度、JPEG 签名全部校验通过。

## 4. 获取实时报警的完整流程

### 4.1 初始化 SDK

初始化 SDK 并设置连接和重连时间：

```csharp
NET_DVR_Init();
NET_DVR_SetConnectTime(2000, 1);
NET_DVR_SetReconnect(10000, true);
```

Windows 下需要确保 `HCNetSDK.dll` 及其依赖 DLL 能被加载。本次工具运行时做了两件事：

```csharp
SetDllDirectory(nativeDir);
Environment.CurrentDirectory = nativeDir;
```

`nativeDir` 指向仓库下的 `Native` 目录。

### 4.2 登录设备

使用 SDK 端口 `8000` 登录：

```csharp
NET_DVR_Login_V40(ref loginInfo, ref deviceInfo);
```

实测返回：

```text
LOGIN_OK userId=0
```

`deviceInfo` 中读到的关键字段：

| 字段 | 值 |
| --- | --- |
| `SerialNumber` | `DS-2XA8D45F/GF-IZS...GC4189118` |
| `ChannelNum` | `1` |
| `StartChannel` | `1` |
| `DeviceType` | `31` |
| `CharEncodeType` | `2` |
| `PasswordLevel` | `2` |
| `LoginMode` | `0` |

### 4.3 注册报警回调

使用 V50 回调接口：

```csharp
NET_DVR_SetDVRMessageCallBack_V50(0, callback, IntPtr.Zero);
```

回调函数签名：

```csharp
void MsgCallBack(
    int lCommand,
    ref NET_DVR_ALARMER pAlarmer,
    IntPtr pAlarmInfo,
    uint dwBufLen,
    IntPtr pUser);
```

回调里必须尽快复制 `pAlarmInfo`，不要在 SDK 回调线程里做耗时解析、写大文件或联网操作。推荐做法：

```csharp
var rawBytes = new byte[dwBufLen];
Marshal.Copy(pAlarmInfo, rawBytes, 0, rawBytes.Length);
queue.Enqueue(rawBytes);
```

随后在业务线程中解析。

### 4.4 设置布防

使用：

```csharp
NET_DVR_SetupAlarmChan_V41(userId, ref setupParam);
```

本次成功布防参数：

| 字段 | 值 | 说明 |
| --- | --- | --- |
| `dwSize` | `20` | 结构体大小 |
| `byLevel` | `0` | 一级，高优先级 |
| `byAlarmInfoType` | `1` | 新报警信息 |
| `byRetAlarmTypeV40` | `1` | 普通报警返回 V40 可变长结构 |
| `byRetDevInfoVersion` | `1` | 返回新设备信息 |
| `byRetVQDAlarmType` | `0` | VQD 默认 |
| `byFaceAlarmDetection` | `0` | 人脸默认 |
| `bySupport` | `0x00` | 未启用额外支持位 |
| `byBrokenNetHttp` | `0x00` | 未启用断网续传 |
| `wTaskNo` | `0` | 未指定任务号 |
| `byDeployType` | `1` | 实时布防 |
| `byAlarmTypeURL` | `0x00` | 默认二进制图片 |
| `byCustomCtrl` | `0x00` | 默认 |

对应原始字节：

```text
14 00 00 00 00 01 01 01 00 00 00 00 00 00 01 00 00 00 00 00
```

返回：

```text
SETUP_OK alarmHandle=0
```

## 5. 抓取工具

本次新增了独立抓取工具，避免影响主程序业务逻辑：

```text
tools/SdkAlarmCapture.cs
tools/SdkAlarmCapture.csproj
```

构建时如果 `tools/obj` 目录权限异常，可以把中间目录和输出目录放到系统临时目录：

```powershell
$tmp = Join-Path $env:TEMP 'SdkAlarmCaptureBuild'
dotnet build tools\SdkAlarmCapture.csproj --verbosity minimal `
  -p:BaseIntermediateOutputPath="$tmp\obj\" `
  -p:BaseOutputPath="$tmp\bin\" `
  -p:MSBuildProjectExtensionsPath="$tmp\obj\"
```

运行示例：

```powershell
$env:HIK_PASSWORD = '设备密码'
$exe = Join-Path $env:TEMP 'SdkAlarmCaptureBuild\bin\Debug\net8.0\SdkAlarmCapture.dll'
dotnet $exe `
  --ip 169.254.103.5 `
  --port 8000 `
  --username admin `
  --password $env:HIK_PASSWORD `
  --seconds 60 `
  --deploy-type 1 `
  --output "$env:TEMP\hikvision-alarm-captures"
```

工具输出 JSON，路径示例：

```text
C:\Users\Administrator\AppData\Local\Temp\hikvision-alarm-captures\sdk-alarms-20260609-112332.json
```

工具记录内容包括：

| 字段 | 说明 |
| --- | --- |
| `StartedAt` / `FinishedAt` | 抓取开始和结束时间 |
| `Device` | 设备 IP、端口、用户、布防参数 |
| `Init` | SDK 初始化结果 |
| `SdkVersion` / `SdkBuildVersionRaw` | SDK 版本 |
| `Login` / `UserId` / `LoginDeviceInfo` | 登录结果和设备信息 |
| `CallbackRegistration` | 回调注册结果 |
| `SetupParam` | 完整布防参数和原始 hex |
| `SetupAlarm` / `AlarmHandle` | 布防结果 |
| `Events[]` | SDK 回调事件数组 |
| `Events[].RawBase64` | 完整 `pAlarmInfo` 原始 buffer，base64 |
| `Events[].AiopVideo` | `0x4021` 事件解析后的 AIOP 头部、JSON 和图片签名 |

## 6. SDK 回调事件总体格式

本次收到的每条事件：

| 字段 | 值 |
| --- | --- |
| `lCommand` | `0x4021` |
| `CommandName` | `COMM_UPLOAD_AIOP_VIDEO` |
| `pAlarmInfo` 对应结构 | `NET_AIOP_VIDEO_HEAD` |
| `dwBufLen` | 约 `339318` 到 `426580` 字节 |
| 图片 | JPEG 二进制 |
| JSON | UTF-8 文本 |

`pAlarmInfo` 实际布局：

```text
+-------------------------+
| NET_AIOP_VIDEO_HEAD     | 376 bytes
+-------------------------+
| AIOP JSON               | dwAIOPDataSize bytes
+-------------------------+
| JPEG picture            | dwPictureSize bytes
+-------------------------+
```

第一条样本：

| 字段 | 值 |
| --- | --- |
| `dwBufLen` | `399275` |
| `HeaderLen` | `376` |
| `JsonLen` | `1744` |
| `PicLen` | `397155` |
| 校验 | `376 + 1744 + 397155 = 399275` |
| JPEG 偏移 | `2120` |
| JPEG 起始 | `FF D8 FF` |
| JPEG 结束 | `FF D9` |
| JPEG 分辨率 | `1920 x 1104` |

## 7. NET_AIOP_VIDEO_HEAD 已验证字段

现场可稳定解析的头部字段：

| 偏移 | 字段 | 示例值 | 说明 |
| --- | --- | --- | --- |
| `0` | `dwSize` / `HeaderLen` | `376` | 头部结构长度 |
| `4` | `dwChannel` | `1` | 通道号 |
| `24` | `taskID` | `cbb1298adf100001` | AIOP 任务 ID |
| `88` | `dwAIOPDataSize` / `JsonLen` | `1744` | AIOP JSON 长度 |
| `92` | `dwPictureSize` / `PicLen` | `397155` | 图片长度 |
| `96` | `modelPackageId` / `MPID` | `cbb1296408500001f8ad757d742b1bd9cbb129640850000165e2ef1d25fd14f` | 模型包 ID |

解析步骤：

```csharp
uint headerLen = BitConverter.ToUInt32(bytes, 0);
uint jsonLen = BitConverter.ToUInt32(bytes, 88);
uint picLen = BitConverter.ToUInt32(bytes, 92);

int jsonOffset = (int)headerLen;
int picOffset = jsonOffset + (int)jsonLen;

string aiopJson = Encoding.UTF8.GetString(bytes, jsonOffset, (int)jsonLen);
byte[] jpeg = bytes[picOffset..(picOffset + (int)picLen)];
```

必须校验：

```text
headerLen + jsonLen + picLen == dwBufLen
jpeg[0..3] == FF D8 FF
jpeg[-2..] == FF D9
```

## 8. AIOP JSON 字段

### 8.1 顶层字段

样本：

```json
{
  "errorcode": 0,
  "version": "2.1.0",
  "width": "1920",
  "height": "1104",
  "frameNum": 64740,
  "timeStamp": 2622270,
  "aitype": 1003,
  "targets": [],
  "events": {}
}
```

字段说明：

| 字段 | 类型 | 示例 | 说明 |
| --- | --- | --- | --- |
| `errorcode` | number | `0` | 算法返回错误码，`0` 表示正常 |
| `version` | string | `2.1.0` | AIOP JSON 版本 |
| `width` | string | `1920` | 图像宽度 |
| `height` | string | `1104` | 图像高度 |
| `frameNum` | number | `64740` | 帧号 |
| `timeStamp` | number | `2622270` | 设备/算法时间戳 |
| `aitype` | number | `1003` | AI 类型 |
| `targets` | array | `[...]` | 当前帧所有检测目标 |
| `events` | object | `{...}` | 实际触发报警的事件集合 |

注意：`width` 和 `height` 在样本中是字符串，不是数字；业务入库时不要强依赖数值类型。

### 8.2 targets[]

`targets[]` 是当前帧检测到的目标列表，不等同于报警列表。它可能包含多个候选目标，其中只有部分目标进入 `events.alertInfo[]`。

样本：

```json
{
  "nodeID": 0,
  "obj": {
    "modelID": "825b148172704f19a78854290b385419",
    "id": 637,
    "type": 2,
    "confidence": 830,
    "valid": 1,
    "visible": 1,
    "rect": {
      "x": "0.000781",
      "y": "0.258585",
      "w": "0.578125",
      "h": "0.711110"
    }
  }
}
```

字段说明：

| 字段 | 类型 | 示例 | 说明 |
| --- | --- | --- | --- |
| `targets[].nodeID` | number | `0` | 节点 ID |
| `targets[].obj.modelID` | string | `825b...5419` | 模型 ID |
| `targets[].obj.id` | number | `637` | 目标跟踪 ID |
| `targets[].obj.type` | number | `2` | 标签类型，需结合模型元数据解释 |
| `targets[].obj.confidence` | number | `830` | 置信度，样本看起来为千分制 |
| `targets[].obj.valid` | number | `1` | 目标是否有效 |
| `targets[].obj.visible` | number | `1` | 目标是否可见 |
| `targets[].obj.rect.x` | string | `0.000781` | 归一化矩形左上角 X |
| `targets[].obj.rect.y` | string | `0.258585` | 归一化矩形左上角 Y |
| `targets[].obj.rect.w` | string | `0.578125` | 归一化矩形宽 |
| `targets[].obj.rect.h` | string | `0.711110` | 归一化矩形高 |

坐标是归一化比例值。换算像素坐标时：

```text
pixelX = rect.x * width
pixelY = rect.y * height
pixelW = rect.w * width
pixelH = rect.h * height
```

### 8.3 events.alertInfo[]

`events.alertInfo[]` 是实际触发报警的列表。业务处理报警时应以它为主，而不是以 `targets[]` 全量目标为主。

样本：

```json
{
  "nodeID": 0,
  "target": {
    "modelID": "825b148172704f19a78854290b385419",
    "id": 637,
    "type": 2,
    "confidence": 830,
    "region": {
      "rect": {
        "x": "0.000781",
        "y": "0.258585",
        "w": "0.578125",
        "h": "0.711110"
      }
    }
  },
  "ruleInfo": {
    "ruleID": 2,
    "triggerType": 1073758209,
    "movDir": 0,
    "region": {
      "polygon": [
        { "x": "0.012000", "y": "0.016507" },
        { "x": "0.988000", "y": "0.016507" },
        { "x": "0.988000", "y": "0.959362" },
        { "x": "0.008000", "y": "0.954507" }
      ]
    },
    "ruleName": "规则2"
  }
}
```

字段说明：

| 字段 | 类型 | 示例 | 说明 |
| --- | --- | --- | --- |
| `events.alertInfo[].nodeID` | number | `0` | 节点 ID |
| `events.alertInfo[].target.modelID` | string | `825b...5419` | 模型 ID |
| `events.alertInfo[].target.id` | number | `637` | 报警目标 ID |
| `events.alertInfo[].target.type` | number | `2` | 报警标签类型 |
| `events.alertInfo[].target.confidence` | number | `830` | 报警目标置信度 |
| `events.alertInfo[].target.region.rect.x` | string | `0.000781` | 报警目标矩形 X |
| `events.alertInfo[].target.region.rect.y` | string | `0.258585` | 报警目标矩形 Y |
| `events.alertInfo[].target.region.rect.w` | string | `0.578125` | 报警目标矩形 W |
| `events.alertInfo[].target.region.rect.h` | string | `0.711110` | 报警目标矩形 H |
| `events.alertInfo[].ruleInfo.ruleID` | number | `2` | 触发规则 ID |
| `events.alertInfo[].ruleInfo.triggerType` | number | `1073758209` | 触发类型，厂商算法规则位 |
| `events.alertInfo[].ruleInfo.movDir` | number | `0` | 运动方向 |
| `events.alertInfo[].ruleInfo.region.polygon[]` | array | 4 个点 | 规则区域多边形 |
| `events.alertInfo[].ruleInfo.ruleName` | string | `规则2` | 规则名称 |

## 9. 已验证字段全集

本次 25 条事件中出现的字段全集：

```text
errorcode
version
width
height
frameNum
timeStamp
aitype
targets
targets[].nodeID
targets[].obj
targets[].obj.modelID
targets[].obj.id
targets[].obj.type
targets[].obj.confidence
targets[].obj.valid
targets[].obj.visible
targets[].obj.rect
targets[].obj.rect.x
targets[].obj.rect.y
targets[].obj.rect.w
targets[].obj.rect.h
events
events.alertInfo
events.alertInfo[].nodeID
events.alertInfo[].target
events.alertInfo[].target.modelID
events.alertInfo[].target.id
events.alertInfo[].target.type
events.alertInfo[].target.confidence
events.alertInfo[].target.region
events.alertInfo[].target.region.rect
events.alertInfo[].target.region.rect.x
events.alertInfo[].target.region.rect.y
events.alertInfo[].target.region.rect.w
events.alertInfo[].target.region.rect.h
events.alertInfo[].ruleInfo
events.alertInfo[].ruleInfo.ruleID
events.alertInfo[].ruleInfo.triggerType
events.alertInfo[].ruleInfo.movDir
events.alertInfo[].ruleInfo.region
events.alertInfo[].ruleInfo.region.polygon
events.alertInfo[].ruleInfo.region.polygon[].x
events.alertInfo[].ruleInfo.region.polygon[].y
events.alertInfo[].ruleInfo.ruleName
```

## 10. 实测事件统计

30 秒样本统计：

| 项目 | 结果 |
| --- | --- |
| 事件总数 | `25` |
| `lCommand` | 全部 `0x4021` |
| JSON 版本 | 全部 `2.1.0` |
| 图像尺寸 | 全部 `1920x1104` |
| `aitype` | 全部 `1003` |
| 每条 `events.alertInfo[]` 数量 | 全部 `1` |
| 触发规则 | 全部 `ruleID=2`，`ruleName=规则2` |
| 实际报警标签 | 全部 `type=2 Short_Sleeve_top` |

`targets[]` 候选目标统计：

| 标签 | 出现次数 |
| --- | --- |
| `type=2 Short_Sleeve_top` | `50` |
| `type=3 Long_Sleeve_top` | `43` |
| `type=1 Shorts` | `0` |

实际报警 `events.alertInfo[]` 统计：

| 标签 | 出现次数 |
| --- | --- |
| `type=2 Short_Sleeve_top` | `25` |

## 11. 设备侧模型元数据获取

可通过 ISAPI 读取设备 AIOP 模型管理信息，用于映射 `modelID` 和 `type`：

```powershell
curl.exe --digest -u "admin:设备密码" --max-time 10 `
  "http://169.254.103.5/ISAPI/Intelligent/AIOpenPlatform/algorithmModel/management?format=json"
```

该接口本次返回了多个模型包，其中启用的 `工装检测` 模型包含：

```json
{
  "MPID": "cbb1296408500001f8ad757d742b1bd9cbb129640850000165e2ef1d25fd14f",
  "MPName": "工装检测",
  "status": 1,
  "engine": [1],
  "description": [
    {
      "aiopType": "v1.0",
      "labels": [
        "1 Shorts",
        "2 Short_Sleeve_top",
        "3 Long_Sleeve_top"
      ],
      "modelId": "825b148172704f19a78854290b385419",
      "type": 1
    }
  ]
}
```

生产环境建议启动时或配置变更时缓存模型标签映射，报警解析时用 `modelID + type` 转换为业务标签。

## 12. 与 ISAPI alertStream 的区别

本次主链路是 SDK 布防回调。之前也验证过 ISAPI 实时事件流：

```text
GET /ISAPI/Event/notification/alertStream
```

它返回的是 `multipart/mixed`，分片内容为 `EventNotificationAlert` XML。实测当时抓到的是周期性 `videoloss inactive`，不是 AIOP 工装检测报警。

因此：

| 方式 | 本次用途 | 结果 |
| --- | --- | --- |
| SDK 布防回调 | 获取短衣短裤/工装检测报警 | 已打通，收到 `0x4021` AIOP 报警 |
| ISAPI alertStream | 对比基础事件上报 | 可通，但本次没有拿到 AIOP 短衣短裤报警 |

如果业务目标是实时识别模型报警，应优先使用 SDK 布防回调。

## 13. 常见问题和处理

### 13.1 `userId=0` 或 `alarmHandle=0` 是否失败

不是。海康 SDK 中 `0` 是有效句柄。判断失败应使用：

```text
userId < 0
alarmHandle < 0
```

失败后再调用：

```csharp
NET_DVR_GetLastError()
```

### 13.2 设置布防成功但没有回调

可能原因：

| 原因 | 说明 |
| --- | --- |
| 现场没有触发模型报警 | 布防成功只代表通道建立，不代表一定有事件 |
| 检测规则未命中 | 例如规则区域、阈值、布防计划或模型状态不满足 |
| 监听时间太短 | AIOP 事件不是固定周期推送，建议联调时至少监听 60 秒 |
| 模型未启用 | 检查 AIOP 模型 `status` 和 `engine` |
| 回调委托被 GC | C# 中回调 delegate 必须保存在静态字段或长生命周期对象中 |

### 13.3 PowerShell 版脚本清理阶段可能崩溃

早期 PowerShell `Add-Type` 版本能成功登录和布防，但在 SDK cleanup 或委托回调线程退出时可能异常退出，导致最终 JSON 写不完整。建议后续使用独立 C# 控制台工具：

```text
tools/SdkAlarmCapture.cs
```

### 13.4 仓库 `HCNetSDK.cs` 中布防结构体定义偏旧

当前仓库主 SDK 封装里的 `NET_DVR_SETUPALARM_PARAM` 只有：

```text
dwSize
byLevel
byAlarmInfoType
byRetAlarmTypeV40
byRetDevInfoVersion
byRetVQDAlarmType
byFaceAlarmDetection
byRes[10]
```

但本地 SDK 文档给出的完整结构为：

```c
DWORD dwSize;
BYTE byLevel;
BYTE byAlarmInfoType;
BYTE byRetAlarmTypeV40;
BYTE byRetDevInfoVersion;
BYTE byRetVQDAlarmType;
BYTE byFaceAlarmDetection;
BYTE bySupport;
BYTE byBrokenNetHttp;
WORD wTaskNo;
BYTE byDeployType;
BYTE byRes1[3];
BYTE byAlarmTypeURL;
BYTE byCustomCtrl;
```

本次抓取工具按完整结构定义，`dwSize=20`，避免扩展字段错位。

### 13.5 `0x4021` 在本地旧文档中查不到

本地 `NET_DVR_SetDVRMessageCallBack_V50.html` 没列出 `0x4021`，但实际设备会推送该命令。结合官方新文档和现场 buffer 校验，应按：

```text
0x4021 = COMM_UPLOAD_AIOP_VIDEO
pAlarmInfo = NET_AIOP_VIDEO_HEAD
```

处理。

### 13.6 `byAlarmTypeURL=0x02` 的测试结果

文档中 `byAlarmTypeURL` 的 bit1 表示 `EVENT_JSON / COMM_VCA_ALARM` 图片上传方式，`0` 为二进制，`1` 为 URL。

本次对 `byAlarmTypeURL=0x02` 做过 15 秒布防测试：

```text
布防成功
15 秒内没有收到事件
```

因此不能确认它对 `COMM_UPLOAD_AIOP_VIDEO / 0x4021` 是否生效。默认 `0x00` 已确认会收到二进制 JPEG。

### 13.7 不要把图片和 raw base64 全量写业务日志

单条事件图片约 330 KB 到 420 KB，直接写数据库或日志会迅速膨胀。建议：

| 数据 | 建议 |
| --- | --- |
| JSON 元数据 | 入库 |
| 关键字段 | 单独列化，例如 `modelID`、`type`、`confidence`、`ruleID` |
| 图片 | 写文件或对象存储，只保存路径/URL |
| 原始 base64 | 仅联调保留，生产默认关闭 |

### 13.8 密码和敏感信息

文档、代码、日志中不要提交设备密码。建议使用：

```powershell
$env:HIK_PASSWORD = '设备密码'
```

或者由配置中心/本地加密配置注入。

## 14. 生产接入建议

建议把 SDK 报警接入封装成独立服务或后台组件：

1. 启动时初始化 SDK。
2. 登录所有需要监听的摄像头。
3. 注册一次全局或多路 V50 回调。
4. 每台设备调用 `NET_DVR_SetupAlarmChan_V41`。
5. 回调中只做快速复制、设备识别、入队。
6. 后台消费队列解析 `lCommand`。
7. 对 `0x4021` 按 `NET_AIOP_VIDEO_HEAD` 拆 JSON 和 JPEG。
8. 用设备模型元数据把 `modelID + type` 映射为业务标签。
9. 将报警 JSON、图片路径、设备信息、规则信息、目标框写入业务存储。
10. 服务停止时关闭布防、登出设备、清理 SDK。

多设备场景下用 `NET_DVR_ALARMER` 区分来源：

| 字段 | 用途 |
| --- | --- |
| `lUserID` | 登录句柄 |
| `sSerialNumber` | 设备序列号 |
| `sDeviceIP` | 设备 IP |
| `wLinkPort` | SDK 端口 |

## 15. 最小解析伪代码

```csharp
void OnAlarm(int command, ref NET_DVR_ALARMER alarmer, IntPtr pAlarmInfo, uint len, IntPtr user)
{
    var bytes = new byte[len];
    Marshal.Copy(pAlarmInfo, bytes, 0, bytes.Length);

    if (command == 0x4021)
    {
        uint headerLen = BitConverter.ToUInt32(bytes, 0);
        uint jsonLen = BitConverter.ToUInt32(bytes, 88);
        uint picLen = BitConverter.ToUInt32(bytes, 92);

        if (headerLen + jsonLen + picLen != len)
        {
            // 记录异常，保留 raw buffer
            return;
        }

        int jsonOffset = (int)headerLen;
        int picOffset = jsonOffset + (int)jsonLen;

        string json = Encoding.UTF8.GetString(bytes, jsonOffset, (int)jsonLen);
        byte[] jpg = bytes[picOffset..(picOffset + (int)picLen)];

        // json -> 解析 targets / events.alertInfo
        // jpg -> 保存图片文件或对象存储
    }
}
```

## 16. 待补充事项

| 项目 | 状态 |
| --- | --- |
| 短袖上衣 `type=2` 实际样本 | 已抓到并验证 |
| 短裤 `type=1` 实际样本 | 待现场触发后补充 |
| `byAlarmTypeURL=0x02` 对 `0x4021` 是否生效 | 待有事件时复测 |
| 生产化入库字段设计 | 待结合业务表结构确定 |
| 主程序 SDK 封装是否升级完整 `NET_DVR_SETUPALARM_PARAM` | 待开发任务确认 |

