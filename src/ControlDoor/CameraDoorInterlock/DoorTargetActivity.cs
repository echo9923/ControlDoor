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

        public string InterlockId { get; set; } = string.Empty;

        public ISet<string> ActiveCameraKeys { get; private set; }

        public DateTime? AlwaysCloseSubmittedAt { get; set; }

        public DateTime? RestoreSubmittedAt { get; set; }

        public int? PendingRestoreAttempt { get; set; }

        public DateTime? RestoreNextRetryAt { get; set; }

        public bool RestoreTerminalFailed { get; set; }

        // 每次新窗口（活动集合从空变非空）递增；迟到的恢复结果据此失效，不覆盖新窗口状态。
        public int Generation { get; set; }

        // 恢复任务已投递未出结果的在途标记，防止重复投递。
        public bool RestoreInFlight { get; set; }

        // 当前在途恢复任务的 TaskId：同一窗口内不同恢复任务的完成结果据此区分（复核 G3）。
        public string RestoreInFlightTaskId { get; set; }

        public bool IsActive => ActiveCameraKeys.Count > 0;
    }
}
