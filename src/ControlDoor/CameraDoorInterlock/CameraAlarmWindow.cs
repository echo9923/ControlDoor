using System;
using System.Collections.Generic;

namespace ControlDoor.CameraDoorInterlock
{
    /// <summary>
    /// 单个摄像头的报警窗口（task04）。每个摄像头独立窗口，窗口内重复报警不续期。
    /// </summary>
    public sealed class CameraAlarmWindow
    {
        public CameraAlarmWindow()
        {
            TargetKeys = new List<string>();
        }

        public string CameraKey { get; set; } = string.Empty;

        public DateTime StartedAt { get; set; }

        public DateTime EndsAt { get; set; }

        public int TriggeredCount { get; set; }

        public IList<string> TargetKeys { get; private set; }
    }
}
