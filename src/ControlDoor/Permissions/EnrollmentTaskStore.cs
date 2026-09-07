using System;
using System.Collections.Generic;
using System.Linq;

namespace ControlDoor.Permissions
{
    public sealed class EnrollmentTaskStore
    {
        private readonly object gate = new object();
        private readonly IDictionary<string, EnrollmentTaskRecord> records = new Dictionary<string, EnrollmentTaskRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly IDictionary<string, string> latestByEmployee = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> completedTasks = new Queue<string>();
        private readonly int maxCompletedTasks;
        private readonly TimeSpan retention;
        private readonly Func<DateTime> clock;

        public EnrollmentTaskStore(int maxCompletedTasks = 1000, TimeSpan? retention = null, Func<DateTime> clock = null)
        {
            if (maxCompletedTasks < 1) throw new ArgumentOutOfRangeException(nameof(maxCompletedTasks));
            this.maxCompletedTasks = maxCompletedTasks;
            this.retention = retention ?? TimeSpan.FromHours(24);
            this.clock = clock ?? (() => DateTime.Now);
        }

        public EnrollmentTaskRecord Start(string taskId, string employeeId)
        {
            var record = new EnrollmentTaskRecord
            {
                TaskId = taskId ?? string.Empty,
                EmployeeId = employeeId ?? string.Empty,
                Action = "CaptureFaceStream",
                Status = EnrollmentTaskStatus.Running,
                Message = "采集中。",
                CreatedAt = clock(),
                UpdatedAt = clock()
            };

            lock (gate)
            {
                PruneCompleted();
                records[record.TaskId] = record.Clone();
                if (!string.IsNullOrWhiteSpace(record.EmployeeId))
                {
                    latestByEmployee[record.EmployeeId] = record.TaskId;
                }
            }

            return record.Clone();
        }

        public void Succeed(string taskId, string message)
        {
            Update(taskId, EnrollmentTaskStatus.Succeeded, message, string.Empty);
        }

        public void Fail(string taskId, string code, string message)
        {
            Update(taskId, EnrollmentTaskStatus.Failed, message, code);
        }

        public EnrollmentTaskRecord GetByTaskId(string taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId))
            {
                return null;
            }

            lock (gate)
            {
                PruneCompleted();
                EnrollmentTaskRecord record;
                return records.TryGetValue(taskId, out record) ? record.Clone() : null;
            }
        }

        public EnrollmentTaskRecord GetLatestByEmployeeId(string employeeId)
        {
            if (string.IsNullOrWhiteSpace(employeeId))
            {
                return null;
            }

            lock (gate)
            {
                PruneCompleted();
                string taskId;
                if (!latestByEmployee.TryGetValue(employeeId, out taskId))
                {
                    return null;
                }

                EnrollmentTaskRecord record;
                return records.TryGetValue(taskId, out record) ? record.Clone() : null;
            }
        }

        public IReadOnlyList<EnrollmentTaskRecord> GetAll()
        {
            lock (gate)
            {
                PruneCompleted();
                return records.Values.Select(item => item.Clone()).ToList();
            }
        }

        private void Update(string taskId, EnrollmentTaskStatus status, string message, string errorCode)
        {
            if (string.IsNullOrWhiteSpace(taskId))
            {
                return;
            }

            lock (gate)
            {
                EnrollmentTaskRecord record;
                if (!records.TryGetValue(taskId, out record))
                {
                    return;
                }

                if (record.Status != EnrollmentTaskStatus.Running)
                {
                    return;
                }
                record.Status = status;
                record.Message = message ?? string.Empty;
                record.ErrorCode = errorCode ?? string.Empty;
                record.UpdatedAt = clock();
                completedTasks.Enqueue(taskId);
                PruneCompleted();
            }
        }

        private void PruneCompleted()
        {
            var cutoff = clock() - retention;
            while (completedTasks.Count > 0)
            {
                var id = completedTasks.Peek();
                if (!records.TryGetValue(id, out var record))
                {
                    completedTasks.Dequeue();
                    continue;
                }
                if (completedTasks.Count <= maxCompletedTasks && record.UpdatedAt > cutoff) break;
                completedTasks.Dequeue();
                records.Remove(id);
                if (latestByEmployee.TryGetValue(record.EmployeeId, out var latest) && latest == id)
                {
                    latestByEmployee.Remove(record.EmployeeId);
                }
            }
        }
    }
}
