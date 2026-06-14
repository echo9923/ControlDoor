using System;
using System.Collections.Generic;
using ControlDoor.Configuration;
using ControlDoor.Devices.Runtime;
using ControlDoor.Hikvision;
using ControlDoor.Observability;

namespace ControlDoor.FaceEvents
{
    public sealed class AcsAlarmEventRouter : IDisposable
    {
        public const int CommAlarmAcs = 0x5002;

        private readonly DeviceRuntimeRegistry registry;
        private readonly IRawAcsAlarmEventSink sink;
        private readonly FaceEventLoggingOptions options;
        private readonly OfflineAcsEventPolicy offlinePolicy;
        private readonly ServiceLogger logger;
        private IHikvisionGateway gateway;
        private bool disposed;

        public AcsAlarmEventRouter(
            DeviceRuntimeRegistry registry,
            IRawAcsAlarmEventSink sink,
            FaceEventLoggingOptions options = null,
            ServiceLogger logger = null)
        {
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
            this.options = options ?? new FaceEventLoggingOptions();
            offlinePolicy = new OfflineAcsEventPolicy(this.options);
            this.logger = logger;
        }

        public void Attach(IHikvisionGateway gateway)
        {
            if (gateway == null)
            {
                throw new ArgumentNullException(nameof(gateway));
            }

            if (this.gateway != null)
            {
                this.gateway.OnAlarmEvent -= OnAlarmEvent;
            }

            this.gateway = gateway;
            this.gateway.OnAlarmEvent += OnAlarmEvent;
        }

        public FaceEventEnqueueResult Route(AlarmEventData data)
        {
            if (data == null)
            {
                return FaceEventEnqueueResult.Rejected("INVALID_ARGUMENT", "alarm data is required", 0, 0);
            }

            if (!options.Enabled)
            {
                logger?.Debug("AcsAlarmEventRouter", "Face event logging is disabled.");
                return FaceEventEnqueueResult.Rejected("DISABLED", "face event logging is disabled", 0, 0);
            }

            if (!IsAcsCommand(data))
            {
                logger?.Debug("AcsAlarmEventRouter", "Non-ACS alarm ignored.", new LogFields
                {
                    Extra =
                    {
                        ["command"] = data.Command.ToString(),
                        ["eventType"] = data.EventType ?? string.Empty
                    }
                });
                return FaceEventEnqueueResult.Rejected("IGNORED_NON_ACS", "non-ACS alarm ignored", 0, 0);
            }

            // 纯人脸门禁：只入人脸验证类事件（人脸通过 0x4B / 人脸失败 0x4C / 人脸+指纹密码卡组合验证 0x3C~0x44）。
            // 其它事件（纯刷卡、纯密码、门未关、胁迫报警等）一律不入队，与 main 项目语义一致。
            if (!IsFaceVerifyMinor(data))
            {
                logger?.Debug("AcsAlarmEventRouter", "Non-face-verify ACS alarm ignored.", new LogFields
                {
                    Extra =
                    {
                        ["dwMinor"] = data.Values.ContainsKey("dwMinor") ? data.Values["dwMinor"] : string.Empty
                    }
                });
                return FaceEventEnqueueResult.Rejected("IGNORED_NON_FACE_VERIFY", "non face-verify ACS alarm ignored", 0, 0);
            }

            try
            {
                var receivedAt = DateTime.Now;
                var lookup = ResolveDevice(data);
                if (!lookup.Found)
                {
                    logger?.Warn("AcsAlarmEventRouter", "ACS alarm device could not be resolved.", new LogFields
                    {
                        Extra =
                        {
                            ["deviceIp"] = data.DeviceIpAddress ?? string.Empty,
                            ["userId"] = data.UserId.ToString(),
                            ["alarmHandle"] = data.AlarmHandle.ToString(),
                            ["serialNumber"] = data.DeviceSerialNumber ?? string.Empty
                        }
                    });
                }

                var snapshot = lookup.Snapshot;
                var currentFlag = ResolveCurrentEventFlag(data);
                string ignoreReason;
                if (offlinePolicy.ShouldIgnore(snapshot, snapshot == null ? data.DeviceIpAddress : snapshot.IpAddress, currentFlag, out ignoreReason))
                {
                    logger?.Warn("AcsAlarmEventRouter", "ACS alarm ignored by stage 7 policy.", new LogFields
                    {
                        DeviceId = snapshot == null ? (int?)null : snapshot.DeviceId,
                        ErrorCode = ignoreReason,
                        Extra =
                        {
                            ["deviceIp"] = snapshot == null ? (data.DeviceIpAddress ?? string.Empty) : snapshot.IpAddress,
                            ["byCurrentEvent"] = currentFlag.HasValue ? currentFlag.Value.ToString() : string.Empty
                        }
                    });
                    return FaceEventEnqueueResult.Rejected(ignoreReason, "ACS alarm ignored by stage 7 policy", 0, 0);
                }

                var source = currentFlag == 2 ? AcsAlarmEventSource.OfflineUpload : AcsAlarmEventSource.Realtime;
                var rawEvent = new RawAcsAlarmEvent
                {
                    ReceivedAt = receivedAt,
                    Command = data.Command == 0 ? CommAlarmAcs : data.Command,
                    DeviceId = snapshot == null ? 0 : snapshot.DeviceId,
                    DeviceIp = snapshot == null ? data.DeviceIpAddress : snapshot.IpAddress,
                    DeviceName = snapshot == null ? null : snapshot.DeviceName,
                    DeviceSerialNo = snapshot == null ? data.DeviceSerialNumber : FirstNonEmpty(snapshot.SerialNumber, data.DeviceSerialNumber, snapshot.IpAddress),
                    AlarmHandle = data.AlarmHandle,
                    SdkUserId = data.UserId,
                    AlarmInfoBytes = CopyBytes(data.RawPayload),
                    PictureBytes = CopyBytes(data.PictureBytes),
                    CurrentEventFlag = currentFlag,
                    Source = source,
                    RawSummary = data.RawPayloadSummary,
                    RequestId = Guid.NewGuid().ToString("N")
                };

                foreach (var item in data.Values)
                {
                    rawEvent.Values[item.Key] = item.Value;
                }

                AddIfNotEmpty(rawEvent.Values, "employeeId", data.EmployeeId);
                AddIfNotEmpty(rawEvent.Values, "cardNo", data.CardNumber);
                AddIfNotEmpty(rawEvent.Values, "direction", data.Direction);
                rawEvent.Values["doorIndex"] = data.DoorIndex.ToString();
                rawEvent.Values["success"] = data.Success.ToString();
                if (data.EventTime.HasValue)
                {
                    rawEvent.Values["eventTime"] = data.EventTime.Value.ToString("O");
                }

                var result = sink.TryEnqueue(rawEvent);
                if (!result.Accepted)
                {
                    logger?.Error("AcsAlarmEventRouter", "ACS alarm enqueue failed: " + result.Code, null, new LogFields
                    {
                        DeviceId = rawEvent.DeviceId > 0 ? (int?)rawEvent.DeviceId : null,
                        RequestId = rawEvent.RequestId,
                        ErrorCode = result.Code
                    });
                }

                return result;
            }
            catch (Exception ex)
            {
                logger?.Error("AcsAlarmEventRouter", "ACS alarm routing failed.", ex);
                return FaceEventEnqueueResult.Rejected("ROUTE_ERROR", ex.Message, 0, 0);
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (gateway != null)
            {
                gateway.OnAlarmEvent -= OnAlarmEvent;
            }
        }

        private void OnAlarmEvent(object sender, AlarmEventData data)
        {
            Route(data);
        }

        private DeviceRuntimeLookupResult ResolveDevice(AlarmEventData data)
        {
            if (!string.IsNullOrWhiteSpace(data.DeviceIpAddress))
            {
                var lookup = registry.TryGetByIpAddress(data.DeviceIpAddress);
                if (lookup.Found)
                {
                    return lookup;
                }
            }

            if (data.UserId >= 0)
            {
                var lookup = registry.TryGetBySdkUserId(data.UserId);
                if (lookup.Found)
                {
                    return lookup;
                }
            }

            if (data.AlarmHandle >= 0)
            {
                return registry.TryGetByAlarmHandle(data.AlarmHandle);
            }

            return DeviceRuntimeLookupResult.NotFound();
        }

        private static bool IsAcsCommand(AlarmEventData data)
        {
            return data.Command == CommAlarmAcs ||
                string.Equals(data.EventType, "COMM_ALARM_ACS", StringComparison.OrdinalIgnoreCase);
        }

        // 人脸验证类 minor 范围（与 ControlEntradaSalida-main 的 IsFaceVerifyMinor 对齐）：
        //   0x4B 人脸验证通过、0x4C 人脸验证失败、0x3C~0x44 人脸+指纹/密码/卡组合验证系列。
        // 其余 minor（纯刷卡 0x01、纯密码、门未关、胁迫报警等）一律不入库。
        private static bool IsFaceVerifyMinor(AlarmEventData data)
        {
            string raw;
            if (!data.Values.TryGetValue("dwMinor", out raw) || string.IsNullOrWhiteSpace(raw))
            {
                // dwMinor 缺失时保守放行，避免把格式异常的人脸事件误丢弃。
                return true;
            }

            int minor;
            if (!int.TryParse(raw, out minor))
            {
                return true;
            }

            return IsFaceVerifyMinor(minor);
        }

        private static bool IsFaceVerifyMinor(int minor)
        {
            if (minor == MinorFaceVerifyPass || minor == MinorFaceVerifyFail)
            {
                return true;
            }

            return minor >= MinorCombinedVerifyLow && minor <= MinorCombinedVerifyHigh;
        }

        private const int MinorFaceVerifyPass = 0x4B;    // 人脸验证通过
        private const int MinorFaceVerifyFail = 0x4C;    // 人脸验证失败
        private const int MinorCombinedVerifyLow = 0x3C; // 组合验证（人脸+指纹/密码/卡）范围下限
        private const int MinorCombinedVerifyHigh = 0x44;// 组合验证范围上限

        private static int? ResolveCurrentEventFlag(AlarmEventData data)
        {
            if (data.CurrentEventFlag.HasValue)
            {
                return data.CurrentEventFlag.Value;
            }

            string value;
            if (data.Values.TryGetValue("byCurrentEvent", out value) ||
                data.Values.TryGetValue("CurrentEventFlag", out value))
            {
                int parsed;
                if (int.TryParse(value, out parsed))
                {
                    return parsed;
                }
            }

            return null;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private static byte[] CopyBytes(byte[] source)
        {
            if (source == null || source.Length == 0)
            {
                return new byte[0];
            }

            var copy = new byte[source.Length];
            Buffer.BlockCopy(source, 0, copy, 0, source.Length);
            return copy;
        }

        private static void AddIfNotEmpty(IDictionary<string, string> values, string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !values.ContainsKey(key))
            {
                values[key] = value;
            }
        }
    }
}
