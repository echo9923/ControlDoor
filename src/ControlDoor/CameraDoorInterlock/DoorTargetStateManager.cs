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

        public DoorTargetChange OnCameraWindowOpened(string cameraKey, DoorTarget target, DateTime now)
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
                    activity.AlwaysCloseSubmittedAt = now;
                    activity.RestoreSubmittedAt = null;
                    activity.PendingRestoreAttempt = null;
                    activity.RestoreNextRetryAt = null;
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
                var shouldRestore = activity.ActiveCameraKeys.Count == 0;
                return new DoorTargetChange
                {
                    ShouldSubmitAlwaysClose = false,
                    ShouldSubmitRestore = shouldRestore,
                    Activity = activity
                };
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

        public void MarkRestoreSucceeded(string targetKey, DateTime now)
        {
            lock (gate)
            {
                DoorTargetActivity activity;
                if (!activitiesByKey.TryGetValue(targetKey, out activity) || activity == null)
                {
                    return;
                }

                activity.RestoreSubmittedAt = now;
                activity.PendingRestoreAttempt = null;
                activity.RestoreNextRetryAt = null;
                if (activity.ActiveCameraKeys.Count == 0)
                {
                    activitiesByKey.Remove(targetKey);
                }
            }
        }

        public void RecordRestoreFailure(string targetKey, int attempt, DateTime? nextRetryAt, DateTime now)
        {
            lock (gate)
            {
                DoorTargetActivity activity;
                if (!activitiesByKey.TryGetValue(targetKey, out activity) || activity == null)
                {
                    return;
                }

                activity.RestoreSubmittedAt = now;
                activity.PendingRestoreAttempt = attempt;
                activity.RestoreNextRetryAt = nextRetryAt;
            }
        }

        public IReadOnlyList<DoorTargetActivity> GetDueRestoreRetries(DateTime now)
        {
            lock (gate)
            {
                return activitiesByKey.Values
                    .Where(a => a.PendingRestoreAttempt.HasValue && a.RestoreNextRetryAt.HasValue && a.RestoreNextRetryAt.Value <= now)
                    .ToList();
            }
        }

        public IReadOnlyList<DoorTargetActivity> GetOutstandingTargets()
        {
            lock (gate)
            {
                return activitiesByKey.Values.ToList();
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
