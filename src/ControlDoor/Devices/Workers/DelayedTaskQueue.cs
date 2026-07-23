using System;
using System.Collections.Generic;
using System.Linq;
using ControlDoor.Devices.Tasks;

namespace ControlDoor.Devices.Workers
{
    public sealed class DelayedTaskQueue
    {
        private readonly List<DelayedDeviceTask> tasks = new List<DelayedDeviceTask>();
        private readonly int capacity;

        public DelayedTaskQueue(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), "Delayed queue capacity must be greater than 0.");
            }

            this.capacity = capacity;
        }

        public int Count => tasks.Count;

        public int Capacity => capacity;

        public DelayedTaskScheduleResult TryEnqueue(DelayedDeviceTask task, bool coalesceByTaskKey)
        {
            if (task == null)
            {
                throw new ArgumentNullException(nameof(task));
            }

            if (!string.IsNullOrWhiteSpace(task.TaskKey))
            {
                var existing = tasks.FirstOrDefault(item => string.Equals(item.TaskKey, task.TaskKey, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    if (!coalesceByTaskKey || task.MergeMode == DelayedTaskMergeMode.None)
                    {
                        return DelayedTaskScheduleResult.Rejected(task, "DUPLICATE_DELAYED_TASK_KEY", "Delayed task key already exists.");
                    }

                    if (task.MergeMode == DelayedTaskMergeMode.Replace)
                    {
                        tasks.Remove(existing);
                        tasks.Add(task);
                        Sort();
                        return DelayedTaskScheduleResult.CoalescedResult(task, existing, "Delayed task was coalesced and replaced.");
                    }

                    if (task.DueAt < existing.DueAt)
                    {
                        tasks.Remove(existing);
                        tasks.Add(task);
                        Sort();
                        return DelayedTaskScheduleResult.CoalescedResult(task, existing, "Delayed task was coalesced and earlier due time was retained.");
                    }

                    return DelayedTaskScheduleResult.CoalescedResult(existing, task, "Delayed task was coalesced and existing earlier due time was retained.");
                }
            }

            if (tasks.Count >= capacity)
            {
                return DelayedTaskScheduleResult.Rejected(task, "DELAYED_QUEUE_FULL", "Delayed task queue is full.");
            }

            tasks.Add(task);
            Sort();
            return DelayedTaskScheduleResult.AcceptedResult(task);
        }

        public bool CancelByTaskId(string delayedTaskId, string reason, out DelayedDeviceTask cancelledTask)
        {
            cancelledTask = null;
            if (string.IsNullOrWhiteSpace(delayedTaskId))
            {
                return false;
            }

            cancelledTask = tasks.FirstOrDefault(task => string.Equals(task.DelayedTaskId, delayedTaskId, StringComparison.OrdinalIgnoreCase));
            return RemoveAndCancel(cancelledTask, reason);
        }

        public bool CancelByTaskKey(string taskKey, string reason, out DelayedDeviceTask cancelledTask)
        {
            cancelledTask = null;
            if (string.IsNullOrWhiteSpace(taskKey))
            {
                return false;
            }

            cancelledTask = tasks.FirstOrDefault(task => string.Equals(task.TaskKey, taskKey, StringComparison.OrdinalIgnoreCase));
            return RemoveAndCancel(cancelledTask, reason);
        }

        public IReadOnlyList<DelayedDeviceTask> TakeDue(DateTime now, int maxCount)
        {
            var limit = maxCount <= 0 ? int.MaxValue : maxCount;
            var due = tasks
                .Where(task => task.DueAt <= now)
                .OrderBy(task => task.DueAt)
                .ThenBy(task => task.CreatedAt)
                .Take(limit)
                .ToList();

            foreach (var task in due)
            {
                tasks.Remove(task);
            }

            return due;
        }

        public IReadOnlyList<DelayedDeviceTask> TakeByDevice(int deviceId)
        {
            var selected = tasks.Where(task => task.DeviceId == deviceId).ToList();
            foreach (var task in selected)
            {
                tasks.Remove(task);
            }

            return selected;
        }

        public DelayedTaskScheduleResult Restore(DelayedDeviceTask task, bool coalesceByTaskKey)
        {
            if (task == null)
            {
                throw new ArgumentNullException(nameof(task));
            }

            if (!string.IsNullOrWhiteSpace(task.TaskKey))
            {
                var existing = tasks.FirstOrDefault(item => string.Equals(item.TaskKey, task.TaskKey, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    if (coalesceByTaskKey && task.MergeMode != DelayedTaskMergeMode.None)
                    {
                        if (task.MergeMode == DelayedTaskMergeMode.Replace || task.DueAt < existing.DueAt)
                        {
                            tasks.Remove(existing);
                            tasks.Add(task);
                            Sort();
                            return DelayedTaskScheduleResult.CoalescedResult(task, existing, "Delayed task was restored and replaced the existing task.");
                        }

                        return DelayedTaskScheduleResult.CoalescedResult(existing, task, "Delayed task was restored and the existing task was retained.");
                    }

                    return DelayedTaskScheduleResult.Rejected(task, "DUPLICATE_DELAYED_TASK_KEY", "Delayed task key already exists.");
                }
            }

            // Restoration is part of a previously accepted checkpoint. Do not drop it because
            // unrelated work filled the queue while the device was being deleted.
            tasks.Add(task);
            Sort();
            return DelayedTaskScheduleResult.AcceptedResult(task);
        }

        public DateTime? GetEarliestDueAt()
        {
            return tasks.Count == 0 ? (DateTime?)null : tasks.Min(task => task.DueAt);
        }

        public int CountDue(DateTime now)
        {
            return tasks.Count(task => task.DueAt <= now);
        }

        public IReadOnlyDictionary<string, int> GetCountBySource()
        {
            return tasks
                .GroupBy(task => string.IsNullOrWhiteSpace(task.Source) ? "Unknown" : task.Source)
                .ToDictionary(group => group.Key, group => group.Count());
        }

        public IReadOnlyDictionary<DeviceTaskPriority, int> GetCountByPriority()
        {
            return tasks
                .GroupBy(task => task.Priority)
                .ToDictionary(group => group.Key, group => group.Count());
        }

        private bool RemoveAndCancel(DelayedDeviceTask task, string reason)
        {
            if (task == null)
            {
                return false;
            }

            tasks.Remove(task);
            task.Cancel(reason);
            return true;
        }

        private void Sort()
        {
            tasks.Sort((left, right) =>
            {
                var due = left.DueAt.CompareTo(right.DueAt);
                return due != 0 ? due : left.CreatedAt.CompareTo(right.CreatedAt);
            });
        }
    }
}
