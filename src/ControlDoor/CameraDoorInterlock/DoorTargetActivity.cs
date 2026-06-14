using System;
using System.Collections.Generic;

namespace ControlDoor.CameraDoorInterlock
{
    /// <summary>
    /// 单个门目标的活动摄像头集合状态（task04）。
    /// 一个门目标可被多个摄像头同时影响；只有最后一个活动摄像头窗口结束才恢复。
    /// </summary>
    public sealed class DoorTargetActivity
    {
        public DoorTargetActivity()
        {
            ActiveCameraKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public string TargetKey { get; set; } = string.Empty;

        public int DoorDeviceId { get; set; }

        public int DoorNo { get; set; }

        public ISet<string> ActiveCameraKeys { get; private set; }

        public DateTime? AlwaysCloseSubmittedAt { get; set; }

        public DateTime? RestoreSubmittedAt { get; set; }

        public int? PendingRestoreAttempt { get; set; }

        public DateTime? RestoreNextRetryAt { get; set; }

        public bool IsActive => ActiveCameraKeys.Count > 0;
    }
}
