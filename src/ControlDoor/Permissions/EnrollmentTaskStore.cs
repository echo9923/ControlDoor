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
            }
        }
    }
}
