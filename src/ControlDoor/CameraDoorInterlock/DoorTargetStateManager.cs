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
                    activity.Generation++;
                    activity.InterlockId = interlockId ?? string.Empty;
                    activity.AlwaysCloseSubmittedAt = now;
                    activity.RestoreSubmittedAt = null;
                    activity.PendingRestoreAttempt = null;
                    activity.RestoreNextRetryAt = null;
                    activity.RestoreTerminalFailed = false;
                    activity.RestoreInFlight = false;
                }
                else if (string.IsNullOrWhiteSpace(activity.InterlockId) && !string.IsNullOrWhiteSpace(interlockId))
                {
                    activity.InterlockId = interlockId;
                }

                return new DoorTargetChange
                {
                    ShouldSubmitAlwaysClose = wasEmpty,
                    ShouldSubmitRestore = false,
                    Activity = activity
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

                activity.ActiveCameraKeys.Remove(cameraKey ?? string.Empty);
                // 已有恢复在途时不再重复投递：在途恢复完成后会按当前活动集合决定后续状态。
                var shouldRestore = activity.ActiveCameraKeys.Count == 0 && !activity.RestoreInFlight;
                return new DoorTargetChange
                {
                    ShouldSubmitAlwaysClose = false,
                    ShouldSubmitRestore = shouldRestore,
                    Activity = activity
                };
            }
        }

        public void MarkRestoreSubmitted(string targetKey, int generation, string taskId, int attempt, DateTime now)
        {
            lock (gate)
            {
                DoorTargetActivity activity;
                if (activitiesByKey.TryGetValue(targetKey, out activity) && activity != null)
                {
                    // 仅当提交代次仍是当前代次时置在途标记；不回写代次，避免竞争下倒退（复核 F03）。
                    if (activity.Generation != generation)
                    {
                        return;
                    }

                    activity.RestoreInFlight = true;
                    activity.RestoreInFlightTaskId = taskId ?? string.Empty;
                    activity.RestoreSubmittedAt = now;
                    activity.PendingRestoreAttempt = attempt;
                }
            }
        }

        // 完成态更新（成功/失败/清在途）在同一把锁内校验"窗口代次 + 在途任务归属"后原子应用：
        // 迟到的旧代次或同代次旧任务结果整体忽略，不得清除新任务的防重复标记或改写其重试安排（复核 G3）。
        private static bool IsStaleCompletion(DoorTargetActivity activity, int generation, string taskId)
        {
            if (activity.Generation != generation)
            {
                return true;
            }

            // 在途属于更新的同代次任务时，旧任务结果忽略；在途未置（如投递拒绝路径）则放行。
            return activity.RestoreInFlight &&
                !string.Equals(activity.RestoreInFlightTaskId, taskId ?? string.Empty, System.StringComparison.Ordinal);
        }

        public bool ClearRestoreInFlight(string targetKey, int generation, string taskId)
        {
            lock (gate)
            {
                DoorTargetActivity activity;
                if (!activitiesByKey.TryGetValue(targetKey, out activity) || activity == null)
                {
                    return false;
                }

                if (IsStaleCompletion(activity, generation, taskId))
                {
                    return false;
                }

                activity.RestoreInFlight = false;
                activity.RestoreInFlightTaskId = null;
                return true;
            }
        }

        public void MarkAlwaysCloseSubmitted(string targetKey, DateTime now)
        {
            lock (gate)
            {
                DoorTargetActivity activity;
                if (activitiesByKey.TryGetValue(targetKey, out activity) && activity != null)
                {
                    activity.AlwaysCloseSubmittedAt = now;
                }
            }
        }

        public bool MarkRestoreSucceeded(string targetKey, int generation, string taskId, DateTime now)
        {
            lock (gate)
            {
                DoorTargetActivity activity;
                if (!activitiesByKey.TryGetValue(targetKey, out activity) || activity == null)
                {
                    return false;
                }

                if (IsStaleCompletion(activity, generation, taskId))
                {
                    return false;
                }

                activity.RestoreSubmittedAt = now;
                activity.PendingRestoreAttempt = null;
                activity.RestoreNextRetryAt = null;
                activity.RestoreTerminalFailed = false;
                activity.RestoreInFlight = false;
                activity.RestoreInFlightTaskId = null;
                if (activity.ActiveCameraKeys.Count == 0)
                {
                    activitiesByKey.Remove(targetKey);
                }

                return true;
            }
        }

        public bool RecordRestoreFailure(string targetKey, int generation, string taskId, int attempt, DateTime? nextRetryAt, DateTime now)
        {
            lock (gate)
            {
                DoorTargetActivity activity;
                if (!activitiesByKey.TryGetValue(targetKey, out activity) || activity == null)
                {
                    return false;
                }

                if (IsStaleCompletion(activity, generation, taskId))
                {
                    return false;
                }

                activity.RestoreSubmittedAt = now;
                activity.PendingRestoreAttempt = attempt;
                activity.RestoreNextRetryAt = nextRetryAt;
                activity.RestoreTerminalFailed = !nextRetryAt.HasValue;
                activity.RestoreInFlight = false;
                activity.RestoreInFlightTaskId = null;
                return true;
            }
        }

        public IReadOnlyList<DoorTargetActivity> GetDueRestoreRetries(DateTime now)
        {
            lock (gate)
            {
                return activitiesByKey.Values
                    .Where(a => !a.RestoreTerminalFailed && !a.RestoreInFlight && a.PendingRestoreAttempt.HasValue && a.RestoreNextRetryAt.HasValue && a.RestoreNextRetryAt.Value <= now)
                    .ToList();
            }
        }

        public IReadOnlyList<DoorTargetActivity> GetOutstandingTargets()
        {
            lock (gate)
            {
                return activitiesByKey.Values.Where(a => !a.RestoreTerminalFailed).ToList();
            }
        }

        public bool IsActive(string targetKey)
        {
            lock (gate)
            {
                return activitiesByKey.TryGetValue(targetKey, out var activity) && activity.IsActive;
            }
        }

        public bool TryGetActivity(string targetKey, out DoorTargetActivity activity)
        {
            lock (gate)
            {
                return activitiesByKey.TryGetValue(targetKey, out activity);
            }
        }
    }

    public struct DoorTargetChange
    {
        public bool ShouldSubmitAlwaysClose { get; set; }

        public bool ShouldSubmitRestore { get; set; }

        public DoorTargetActivity Activity { get; set; }
    }
}
