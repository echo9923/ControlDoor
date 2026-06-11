using System.Collections.Generic;
using System.Linq;

namespace ControlDoor.Runtime.Health
{
    public sealed class HealthCheckSummary
    {
        private readonly List<HealthCheckResult> results = new List<HealthCheckResult>();

        public IReadOnlyList<HealthCheckResult> Results => results.AsReadOnly();

        public int OkCount => results.Count(item => item.Status == HealthCheckStatus.OK);

        public int WarningCount => results.Count(item => item.Status == HealthCheckStatus.Warning);

        public int FailedCount => results.Count(item => item.Status == HealthCheckStatus.Failed);

        public bool Success => FailedCount == 0;

        public void Add(HealthCheckResult result)
        {
            results.Add(result);
        }
    }
}
