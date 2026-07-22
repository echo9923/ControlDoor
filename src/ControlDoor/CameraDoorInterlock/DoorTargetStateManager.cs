using System;
using System.Collections.Generic;
using System.Linq;

namespace ControlDoor.CameraDoorInterlock
{
    /// <summary>
    /// 管理每个门目标的活动摄像头集合（task04）。一个门目标可被多个摄像头同时影响；
    /// 只有最后一个活动摄像头窗口结束才恢复。窗口状态纯内存，不持久化。
    /// 所有时间由调用方显式传入，便于单元测试。
    /// </summary>
    public sealed class DoorTargetStateManager
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, DoorTargetActivity> activitiesByKey =
            new Dictionary<string, DoorTargetActivity>(StringComparer.OrdinalIgnoreCase);

        public DoorTargetChange OnCameraWindowOpened(string cameraKey, DoorTarget target, DateTime now, string interlockId = null)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            lock (gate)
            {
                DoorTargetActivity activity;
                var existed = activitiesByKey.TryGetValue(target.TargetKey, out activity);
                if (!existed || activity == null)
                {
                    activity = new DoorTargetActivity
                    {
                        TargetKey = target.TargetKey,
                        DoorDeviceId = target.DoorDeviceId,
                        DoorNo = target.DoorNo
                    };
                    activitiesByKey[target.TargetKey] = activity;
                }

                var wasEmpty = activity.ActiveCameraKeys.Count == 0;
                activity.ActiveCameraKeys.Add(cameraKey ?? string.Empty);
                if (wasEmpty)
                {
                    activity.ActivityGeneration++;
                    activity.InterlockId = interlockId ?? string.Empty;
                    activity.AlwaysCloseOperationToken = NewOperationToken();
                    activity.AlwaysCloseSubmittedAt = now;
                    activity.PendingAlwaysCloseAttempt = null;
                    activity.AlwaysCloseNextRetryAt = null;
                    activity.RestoreOperationToken = string.Empty;
                    activity.RestoreSubmittedAt = null;
                    activity.PendingRestoreAttempt = null;
                    activity.RestoreNextRetryAt = null;
                    activity.RestoreTerminalFailed = false;
                }
                else if (string.IsNullOrWhiteSpace(activity.InterlockId) && !string.IsNullOrWhiteSpace(interlockId))
                {
                    activity.InterlockId = interlockId;
                }

                return new DoorTargetChange
                {
                    ShouldSubmitAlwaysClose = wasEmpty,
                    ShouldSubmitRestore = false,
                    Activity = CloneActivity(activity)
                };
            }
        }

        public DoorTargetChange OnCameraWindowClosed(string cameraKey, string targetKey, DateTime now)
        {
            if (string.IsNullOrEmpty(targetKey))
            {
                return new DoorTargetChange { ShouldSubmitAlwaysClose = false, ShouldSubmitRestore = false, Activity = null };
            }

            lock (gate)
            {
                DoorTargetActivity activity;
                if (!activitiesByKey.TryGetValue(targetKey, out activity) || activity == null)
                {
                    return new DoorTargetChange { ShouldSubmitAlwaysClose = false, ShouldSubmitRestore = false, Activity = null };
                }

                var wasActive = activity.ActiveCameraKeys.Remove(cameraKey ?? string.Empty);
                var shouldRestore = wasActive && activity.ActiveCameraKeys.Count == 0;
                if (shouldRestore)
                {
                    activity.RestoreOperationToken = NewOperationToken();
                    activity.RestoreSubmittedAt = null;
                    activity.PendingRestoreAttempt = null;
                    activity.RestoreNextRetryAt = null;
                    activity.RestoreTerminalFailed = false;
                }

                return new DoorTargetChange
                {
                    ShouldSubmitAlwaysClose = false,
                    ShouldSubmitRestore = shouldRestore,
                    Activity = CloneActivity(activity)
                };
            }
        }

        public void MarkAlwaysCloseSubmitted(string targetKey, DateTime now)
        {
            if (TryGetActivity(targetKey, out var activity))
            {
                MarkAlwaysCloseSubmitted(targetKey, activity.ActivityGeneration, activity.AlwaysCloseOperationToken, now);
            }
        }

        public bool MarkAlwaysCloseSubmitted(string targetKey, long generation, string operationToken, DateTime now)
        {
            lock (gate)
            {
                DoorTargetActivity activity;
                if (!TryGetCurrentOperation(targetKey, generation, operationToken, restoreOperation: false, out activity) || !activity.IsActive)
                {
                    return false;
                }

                activity.AlwaysCloseSubmittedAt = now;
                activity.PendingAlwaysCloseAttempt = null;
                activity.AlwaysCloseNextRetryAt = null;
                return true;
            }
        }

        /// <summary>
        /// 登记 always-close 失败：常闭是实时安全动作，可重试错误持续重试直至成功（task09，AIOP-02）。
        /// 不可重试错误转终态，仅打日志，避免对配置类错误做无意义重试。
        /// </summary>
        public void RecordAlwaysCloseFailure(string targetKey, int attempt, DateTime? nextRetryAt, DateTime now)
        {
            if (TryGetActivity(targetKey, out var activity))
            {
                RecordAlwaysCloseFailure(targetKey, activity.ActivityGeneration, activity.AlwaysCloseOperationToken, attempt, nextRetryAt, now);
            }
        }

        public bool RecordAlwaysCloseFailure(string targetKey, long generation, string operationToken, int attempt, DateTime? nextRetryAt, DateTime now)
        {
            lock (gate)
            {
                DoorTargetActivity activity;
                if (!TryGetCurrentOperation(targetKey, generation, operationToken, restoreOperation: false, out activity) || !activity.IsActive)
                {
                    return false;
                }

                activity.PendingAlwaysCloseAttempt = attempt;
                activity.AlwaysCloseNextRetryAt = nextRetryAt;
                return true;
            }
        }

        public IReadOnlyList<DoorTargetActivity> GetDueAlwaysCloseRetries(DateTime now)
        {
            lock (gate)
            {
                return activitiesByKey.Values
                    .Where(a => a.IsActive && a.PendingAlwaysCloseAttempt.HasValue && a.AlwaysCloseNextRetryAt.HasValue && a.AlwaysCloseNextRetryAt.Value <= now)
                    .Select(CloneActivity)
                    .ToList();
            }
        }

        public void MarkRestoreSucceeded(string targetKey, DateTime now)
        {
            if (TryGetActivity(targetKey, out var activity))
            {
                MarkRestoreSucceeded(targetKey, activity.ActivityGeneration, activity.RestoreOperationToken, now);
            }
        }

        public bool MarkRestoreSucceeded(string targetKey, long generation, string operationToken, DateTime now)
        {
            lock (gate)
            {
                DoorTargetActivity activity;
                if (!TryGetCurrentOperation(targetKey, generation, operationToken, restoreOperation: true, out activity) || activity.IsActive)
                {
                    return false;
                }

                activity.RestoreSubmittedAt = now;
                activity.PendingRestoreAttempt = null;
                activity.RestoreNextRetryAt = null;
                activity.RestoreTerminalFailed = false;
                activitiesByKey.Remove(targetKey);
                return true;
            }
        }

        /// <summary>
        /// 恢复任务已投递但尚未拿到结果（AIOP-05：异步 fire-and-observe）。登记 submitted 时间与 attempt，
        /// 用于重试去重——若同一 attempt 已投递，扫描循环不会再次拉起。
        /// </summary>
        public void RecordRestoreSubmitted(string targetKey, int attempt, DateTime now)
        {
            if (TryGetActivity(targetKey, out var activity))
            {
                RecordRestoreSubmitted(targetKey, activity.ActivityGeneration, activity.RestoreOperationToken, attempt, now);
            }
        }

        public bool RecordRestoreSubmitted(string targetKey, long generation, string operationToken, int attempt, DateTime now)
        {
            lock (gate)
            {
                DoorTargetActivity activity;
                if (!TryGetCurrentOperation(targetKey, generation, operationToken, restoreOperation: true, out activity) || activity.IsActive)
                {
                    return false;
                }

                activity.RestoreSubmittedAt = now;
                activity.PendingRestoreAttempt = attempt;
                // 等待结果期间不再被 GetDueRestoreRetries 触发：设下次重试为 null 直到失败回调写入。
                activity.RestoreNextRetryAt = null;
                activity.RestoreTerminalFailed = false;
                return true;
            }
        }

        public void RecordRestoreFailure(string targetKey, int attempt, DateTime? nextRetryAt, DateTime now)
        {
            if (TryGetActivity(targetKey, out var activity))
            {
                RecordRestoreFailure(targetKey, activity.ActivityGeneration, activity.RestoreOperationToken, attempt, nextRetryAt, now);
            }
        }

        public bool RecordRestoreFailure(string targetKey, long generation, string operationToken, int attempt, DateTime? nextRetryAt, DateTime now)
        {
            lock (gate)
            {
                DoorTargetActivity activity;
                if (!TryGetCurrentOperation(targetKey, generation, operationToken, restoreOperation: true, out activity) || activity.IsActive)
                {
                    return false;
                }

                activity.RestoreSubmittedAt = now;
                activity.PendingRestoreAttempt = attempt;
                activity.RestoreNextRetryAt = nextRetryAt;
                activity.RestoreTerminalFailed = !nextRetryAt.HasValue;
                return true;
            }
        }

        public IReadOnlyList<DoorTargetActivity> GetDueRestoreRetries(DateTime now)
        {
            lock (gate)
            {
                return activitiesByKey.Values
                    .Where(a => !a.IsActive && !a.RestoreTerminalFailed && a.PendingRestoreAttempt.HasValue && a.RestoreNextRetryAt.HasValue && a.RestoreNextRetryAt.Value <= now)
                    .Select(CloneActivity)
                    .ToList();
            }
        }

        public IReadOnlyList<DoorTargetActivity> GetOutstandingTargets()
        {
            lock (gate)
            {
                return activitiesByKey.Values.Where(a => !a.RestoreTerminalFailed).Select(CloneActivity).ToList();
            }
        }

        public bool TryGetActivity(string targetKey, out DoorTargetActivity activity)
        {
            lock (gate)
            {
                DoorTargetActivity current;
                if (!activitiesByKey.TryGetValue(targetKey, out current) || current == null)
                {
                    activity = null;
                    return false;
                }

                activity = CloneActivity(current);
                return true;
            }
        }

        private bool TryGetCurrentOperation(string activityKey, long generation, string operationToken, bool restoreOperation, out DoorTargetActivity activity)
        {
            if (!activitiesByKey.TryGetValue(activityKey, out activity) || activity == null ||
                activity.ActivityGeneration != generation || string.IsNullOrEmpty(operationToken))
            {
                return false;
            }

            return restoreOperation
                ? string.Equals(activity.RestoreOperationToken, operationToken, StringComparison.Ordinal)
                : string.Equals(activity.AlwaysCloseOperationToken, operationToken, StringComparison.Ordinal);
        }

        private static DoorTargetActivity CloneActivity(DoorTargetActivity source)
        {
            var clone = new DoorTargetActivity
            {
                TargetKey = source.TargetKey,
                DoorDeviceId = source.DoorDeviceId,
                DoorNo = source.DoorNo,
                InterlockId = source.InterlockId,
                ActivityGeneration = source.ActivityGeneration,
                AlwaysCloseOperationToken = source.AlwaysCloseOperationToken,
                RestoreOperationToken = source.RestoreOperationToken,
                AlwaysCloseSubmittedAt = source.AlwaysCloseSubmittedAt,
                PendingAlwaysCloseAttempt = source.PendingAlwaysCloseAttempt,
                AlwaysCloseNextRetryAt = source.AlwaysCloseNextRetryAt,
                RestoreSubmittedAt = source.RestoreSubmittedAt,
                PendingRestoreAttempt = source.PendingRestoreAttempt,
                RestoreNextRetryAt = source.RestoreNextRetryAt,
                RestoreTerminalFailed = source.RestoreTerminalFailed
            };
            foreach (var cameraKey in source.ActiveCameraKeys)
            {
                clone.ActiveCameraKeys.Add(cameraKey);
            }
            return clone;
        }

        private static string NewOperationToken()
        {
            return Guid.NewGuid().ToString("N");
        }
    }

    public struct DoorTargetChange
    {
        public bool ShouldSubmitAlwaysClose { get; set; }

        public bool ShouldSubmitRestore { get; set; }

        public DoorTargetActivity Activity { get; set; }
    }
}
