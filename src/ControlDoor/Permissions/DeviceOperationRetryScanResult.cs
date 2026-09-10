using System;

namespace ControlDoor.Permissions
{
    public sealed class DeviceOperationRetryScanResult
    {
        public string RequestId { get; set; } = string.Empty;

        public int Due { get; set; }

        // 复核 M1：本轮候选摘要读到第 BatchSize+1 条即置位——明确的积压标志，
        // 由候选查询结果直接判定，不用成功数、提交数或完整记录加载数推断；
        // 记录在查询后被更新或领取不应让扫描节奏失真。
        public bool HasMoreDue { get; set; }

        public int Submitted { get; set; }

        public int InFlightSkipped { get; set; }

        public int ClaimSkipped { get; set; }

        public int OfflineDeferred { get; set; }

        public int Terminal { get; set; }

        public int EmptyDeleted { get; set; }

        public int Succeeded { get; set; }

        public int Failed { get; set; }

        public int CleanupDeleted { get; set; }

        public long ElapsedMs { get; set; }

        public DateTime ScannedAt { get; set; }
    }
}
