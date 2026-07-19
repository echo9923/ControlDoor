using System;
using System.Collections.Generic;
using System.Linq;

namespace ControlDoor.Permissions
{
    public sealed class EnrollmentTaskStore
    {
        // 默认保留策略：最多保留 200 条记录，且 24h 之外的 Succeeded/Failed 任务视为过期。
        // Running 状态的任务不因过期被清除（仍在进行中），但会占用容量；超容量时按 CreatedAt 升序移除最旧记录。
        private const int DefaultMaxRecords = 200;
        private static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(24);

        private readonly object gate = new object();
        private readonly int maxRecords;
        private readonly TimeSpan retention;
        private long nextInsertSequence = 0;
        private readonly IDictionary<string, EnrollmentTaskRecord> records = new Dictionary<string, EnrollmentTaskRecord>(StringComparer.OrdinalIgnoreCase);
        // task insert sequence（升序），用于在 CreatedAt/UpdatedAt 相同时（毫秒级连发常见）提供稳定的淘汰顺序。
        private readonly IDictionary<string, long> insertSequence = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly IDictionary<string, string> latestByEmployee = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public EnrollmentTaskStore()
            : this(DefaultMaxRecords, DefaultRetention)
        {
        }

        public EnrollmentTaskStore(int maxRecords, TimeSpan retention)
        {
            this.maxRecords = maxRecords > 0 ? maxRecords : DefaultMaxRecords;
            this.retention = retention > TimeSpan.Zero ? retention : DefaultRetention;
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
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };

            lock (gate)
            {
                insertSequence[record.TaskId] = nextInsertSequence;
                nextInsertSequence++;
                records[record.TaskId] = record.Clone();
                if (!string.IsNullOrWhiteSpace(record.EmployeeId))
                {
                    latestByEmployee[record.EmployeeId] = record.TaskId;
                }
                EvictExpired();
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
                EvictExpired();
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
                EvictExpired();
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
                EvictExpired();
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

                record.Status = status;
                record.Message = message ?? string.Empty;
                record.ErrorCode = errorCode ?? string.Empty;
                record.UpdatedAt = DateTime.Now;
                EvictExpired();
            }
        }

        // 过期清理：
        //   1. 已结束（Succeeded/Failed）且 UpdatedAt 早于 (now - retention) 的记录移除；
        //   2. 若超过容量上限，按 CreatedAt 升序移除最旧的记录（Running 状态也会被移除，避免容量无限增长，正常路径不会出现 Running 堆积）。
        // 移除后同步清理 latestByEmployee 中指向已删任务的引用，保证 GetLatestByEmployeeId 不会返回 null record。
        private void EvictExpired()
        {
            if (records.Count == 0)
            {
                return;
            }

            var now = DateTime.Now;
            var cutoff = now - retention;
            if (records.Count > 0)
            {
                var expiredKeys = records.Values
                    .Where(item => (item.Status == EnrollmentTaskStatus.Succeeded || item.Status == EnrollmentTaskStatus.Failed)
                        && item.UpdatedAt < cutoff)
                    .Select(item => item.TaskId)
                    .ToList();
                foreach (var key in expiredKeys)
                {
                    records.Remove(key);
                    insertSequence.Remove(key);
                }
            }

            while (records.Count > maxRecords)
            {
                // 排序优先 CreatedAt，连发场景下时间戳相同时以插入序号升序决定淘汰顺序，保证确定性。
                var oldest = records.Values
                    .OrderBy(item => item.CreatedAt)
                    .ThenBy(item => insertSequence.TryGetValue(item.TaskId, out var seq) ? seq : long.MaxValue)
                    .First();
                records.Remove(oldest.TaskId);
                insertSequence.Remove(oldest.TaskId);
            }

            if (records.Count > 0 && latestByEmployee.Count > 0)
            {
                var staleEmployees = latestByEmployee
                    .Where(pair => !records.ContainsKey(pair.Value))
                    .Select(pair => pair.Key)
                    .ToList();
                foreach (var employee in staleEmployees)
                {
                    latestByEmployee.Remove(employee);
                }
            }
            else if (records.Count == 0)
            {
                latestByEmployee.Clear();
            }
        }
    }
}
