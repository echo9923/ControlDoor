using System;
using ControlDoor.Configuration;
using ControlDoor.Devices.Runtime;
using ControlDoor.Hikvision;
using ControlDoor.Observability;

namespace ControlDoor.CameraDoorInterlock
{
    /// <summary>
    /// 从 SDK 回调识别 0x4021 / COMM_UPLOAD_AIOP_VIDEO 报警并反查来源摄像头，
    /// 命中配置映射后投递给 CameraDoorInterlockService（镜像 FaceEvents.AcsAlarmEventRouter）。
    /// 回调线程只复制 buffer 并填入来源标识，不解析 JSON、不控制门。
    /// </summary>
    public sealed class AiopAlarmEventRouter : IDisposable
    {
        public const int CommUploadAiopVideo = 0x4021;

        private readonly DeviceRuntimeRegistry registry;
        private readonly IAiopAlarmEventSink sink;
        private readonly CameraAlarmDoorInterlockOptions options;
        private readonly InterlockMappingResolver resolver;
        private readonly ServiceLogger logger;
        private IHikvisionGateway gateway;
        private bool disposed;

        public AiopAlarmEventRouter(
            DeviceRuntimeRegistry registry,
            IAiopAlarmEventSink sink,
            CameraAlarmDoorInterlockOptions options,
            InterlockMappingResolver resolver,
            ServiceLogger logger = null)
        {
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
            this.options = options ?? new CameraAlarmDoorInterlockOptions();
            this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
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

        public AiopAlarmEnqueueResult Route(AlarmEventData data)
        {
            if (data == null)
            {
                return AiopAlarmEnqueueResult.Rejected("INVALID_ARGUMENT", "alarm data is required", 0, 0);
            }

            if (!options.Enabled)
            {
                return AiopAlarmEnqueueResult.Rejected("DISABLED", "camera door interlock is disabled", 0, 0);
            }

            if (!IsAiopCommand(data))
            {
                logger?.Debug("AiopAlarmEventRouter", "非 AIOP 报警已忽略。", new LogFields
                {
                    Extra =
                    {
                        ["command"] = data.Command.ToString(),
                        ["eventType"] = data.EventType ?? string.Empty
                    }
                });
                return AiopAlarmEnqueueResult.Rejected("IGNORED_NON_AIOP", "non-AIOP alarm ignored", 0, 0);
            }

            try
            {
                if (!resolver.TryIdentifyCamera(data.DeviceIpAddress, data.UserId, data.AlarmHandle, data.DeviceSerialNumber, out var cameraKey))
                {
                    logger?.Info("AiopAlarmEventRouter", "AIOP 报警来源未命中配置摄像头，忽略。", new LogFields
                    {
                        Extra =
                        {
                            ["cameraIp"] = data.DeviceIpAddress ?? string.Empty,
                            ["userId"] = data.UserId.ToString(),
                            ["serial"] = data.DeviceSerialNumber ?? string.Empty,
                            ["command"] = data.Command.ToString()
                        }
                    });
                    return AiopAlarmEnqueueResult.Rejected("IGNORED_UNCONFIGURED_CAMERA", "camera is not configured for interlock", 0, 0);
                }

                var cameraDeviceId = ResolveCameraDeviceId(data);
                var rawEvent = new RawAiopAlarmEvent
                {
                    ReceivedAt = DateTime.Now,
                    Command = data.Command == 0 ? CommUploadAiopVideo : data.Command,
                    CameraKey = cameraKey,
                    CameraIp = data.DeviceIpAddress ?? string.Empty,
                    CameraDeviceId = cameraDeviceId,
                    RawPayload = CopyBytes(data.RawPayload),
                    RequestId = Guid.NewGuid().ToString("N")
                };

                var result = sink.TryEnqueue(rawEvent);
                if (!result.Accepted)
                {
                    logger?.Warn("AiopAlarmEventRouter", "AIOP 报警入队失败: " + result.Code, new LogFields
                    {
                        RequestId = rawEvent.InterlockId,
                        OperationName = "AiopAlarmRoute",
                        Extra =
                        {
                            ["interlockId"] = rawEvent.InterlockId,
                            ["cameraKey"] = cameraKey,
                            ["cameraIp"] = rawEvent.CameraIp,
                            ["queueDepth"] = result.QueueDepth.ToString(),
                            ["capacity"] = result.Capacity.ToString()
                        }
                    });
                }
                else
                {
                    logger?.Info("AiopAlarmEventRouter", "AIOP 报警已入队。", new LogFields
                    {
                        RequestId = rawEvent.InterlockId,
                        OperationName = "AiopAlarmRoute",
                        Extra =
                        {
                            ["interlockId"] = rawEvent.InterlockId,
                            ["cameraKey"] = cameraKey,
                            ["cameraIp"] = rawEvent.CameraIp,
                            ["cameraDeviceId"] = rawEvent.CameraDeviceId.ToString(),
                            ["payloadBytes"] = rawEvent.RawPayload.Length.ToString()
                        }
                    });
                }

                return result;
            }
            catch (Exception ex)
            {
                logger?.Error("AiopAlarmEventRouter", "AIOP 报警路由失败。", ex);
                return AiopAlarmEnqueueResult.Rejected("ROUTE_ERROR", ex.Message, 0, 0);
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

        private static bool IsAiopCommand(AlarmEventData data)
        {
            return data.Command == CommUploadAiopVideo ||
                string.Equals(data.EventType, "COMM_UPLOAD_AIOP_VIDEO", StringComparison.OrdinalIgnoreCase);
        }

        private int ResolveCameraDeviceId(AlarmEventData data)
        {
            if (registry == null)
            {
                return 0;
            }

            if (!string.IsNullOrWhiteSpace(data.DeviceIpAddress))
            {
                var lookup = registry.TryGetByIpAddress(data.DeviceIpAddress);
                if (lookup.Found && lookup.Snapshot != null)
                {
                    return lookup.Snapshot.DeviceId;
                }
            }

            if (data.UserId >= 0)
            {
                var lookup = registry.TryGetBySdkUserId(data.UserId);
                if (lookup.Found && lookup.Snapshot != null)
                {
                    return lookup.Snapshot.DeviceId;
                }
            }

            return 0;
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
    }
}
