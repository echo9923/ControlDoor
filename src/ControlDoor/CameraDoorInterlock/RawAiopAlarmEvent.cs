using System;

namespace ControlDoor.CameraDoorInterlock
{
    /// <summary>
    /// 路由器→服务的 AIOP 报警事件（镜像 FaceEvents.RawAcsAlarmEvent）。
    /// 回调线程只复制原始 buffer 并填入来源摄像头标识，JSON 解析放到后台线程。
    /// </summary>
    public sealed class RawAiopAlarmEvent
    {
        public RawAiopAlarmEvent()
        {
            RawPayload = new byte[0];
        }

        public DateTime ReceivedAt { get; set; }

        public int Command { get; set; }

        /// <summary>已识别的来源摄像头标识（IP 优先，否则 Id 字符串）。</summary>
        public string CameraKey { get; set; } = string.Empty;

        public string CameraIp { get; set; } = string.Empty;

        public int CameraDeviceId { get; set; }

        public byte[] RawPayload { get; set; }

        public string RequestId { get; set; } = string.Empty;
    }
}
