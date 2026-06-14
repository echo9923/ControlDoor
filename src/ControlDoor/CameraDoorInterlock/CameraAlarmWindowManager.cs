using System;
using System.Collections.Generic;
using System.Linq;

namespace ControlDoor.CameraDoorInterlock
{
    /// <summary>
    /// 管理每个摄像头独立的报警窗口（task04）。窗口内重复报警不刷新 EndsAt，只增加计数。
    /// 所有时间由调用方显式传入，便于单元测试。
    /// </summary>
    public sealed class CameraAlarmWindowManager
    {
        private readonly object gate = new object();
        private readonly int windowSeconds;
        private readonly Dictionary<string, CameraAlarmWindow> windowsByKey =
            new Dictionary<string, CameraAlarmWindow>(StringComparer.OrdinalIgnoreCase);

        public CameraAlarmWindowManager(int windowSeconds)
        {
            if (windowSeconds < 1)
            {
                windowSeconds = 1;
            }

            this.windowSeconds = windowSeconds;
        }

        public int WindowSeconds => windowSeconds;

        public CameraWindowOpenResult OpenOrRecord(string cameraKey, IReadOnlyList<string> targetKeys, DateTime now)
        {
            if (string.IsNullOrEmpty(cameraKey))
            {
                throw new ArgumentNullException(nameof(cameraKey));
            }

            lock (gate)
            {
                CameraAlarmWindow existing;
                if (windowsByKey.TryGetValue(cameraKey, out existing))
                {
                    existing.TriggeredCount++;
                    return new CameraWindowOpenResult { OpenedNew = false, Window = existing };
                }

                var window = new CameraAlarmWindow
                {
                    CameraKey = cameraKey,
                    StartedAt = now,
                    EndsAt = now.AddSeconds(windowSeconds),
                    TriggeredCount = 1
                };

                if (targetKeys != null)
                {
                    foreach (var targetKey in targetKeys)
                    {
                        if (!string.IsNullOrEmpty(targetKey))
                        {
                            window.TargetKeys.Add(targetKey);
                        }
                    }
                }

                windowsByKey[cameraKey] = window;
                return new CameraWindowOpenResult { OpenedNew = true, Window = window };
            }
        }

        public IReadOnlyList<CameraAlarmWindow> ExpireDue(DateTime now)
        {
            lock (gate)
            {
                if (windowsByKey.Count == 0)
                {
                    return new List<CameraAlarmWindow>();
                }

                var expired = new List<CameraAlarmWindow>();
                var staleKeys = new List<string>();
                foreach (var pair in windowsByKey)
                {
                    if (pair.Value.EndsAt <= now)
                    {
                        expired.Add(pair.Value);
                        staleKeys.Add(pair.Key);
                    }
                }

                foreach (var key in staleKeys)
                {
                    windowsByKey.Remove(key);
                }

                return expired;
            }
        }

        public IReadOnlyList<CameraAlarmWindow> GetActive()
        {
            lock (gate)
            {
                return windowsByKey.Values.ToList();
            }
        }

        public bool HasActiveWindow(string cameraKey)
        {
            if (string.IsNullOrEmpty(cameraKey))
            {
                return false;
            }

            lock (gate)
            {
                return windowsByKey.ContainsKey(cameraKey);
            }
        }
    }

    public struct CameraWindowOpenResult
    {
        public bool OpenedNew { get; set; }

        public CameraAlarmWindow Window { get; set; }
    }
}
