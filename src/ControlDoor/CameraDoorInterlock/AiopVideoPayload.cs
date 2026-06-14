using System.Collections.Generic;

namespace ControlDoor.CameraDoorInterlock
{
    /// <summary>
    /// 阶段 9 AIOP 报警载荷解析结果。仅用于日志和联调诊断，
    /// 解析失败不影响联动触发（命中配置摄像头即触发）。
    /// 对应 docs/stage9/task02.md 的 AiopVideoPayloadParser 输出。
    /// </summary>
    public sealed class AiopVideoPayload
    {
        public AiopVideoPayload()
        {
            DetectedTypes = new List<string>();
        }

        public int Command { get; set; }

        public int CameraDeviceId { get; set; }

        public string CameraIp { get; set; } = string.Empty;

        public string TaskId { get; set; } = string.Empty;

        public int JsonLength { get; set; }

        public string JsonText { get; set; } = string.Empty;

        public string ModelId { get; set; } = string.Empty;

        public IList<string> DetectedTypes { get; private set; }

        public int ImageLength { get; set; }

        public bool ImageIsJpeg { get; set; }

        public bool ParseSucceeded { get; set; }

        public string ParseError { get; set; } = string.Empty;
    }
}
