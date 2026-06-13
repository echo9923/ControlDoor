using System.Collections.Generic;
using System.Linq;

namespace ControlDoor.Permissions
{
    public sealed class RetryCommandPlan
    {
        public RetryCommandPlan(DeviceOperationRetryState state, IEnumerable<RetryOperationStep> steps)
        {
            State = state;
            Steps = (steps ?? Enumerable.Empty<RetryOperationStep>()).ToList().AsReadOnly();
        }

        public DeviceOperationRetryState State { get; }

        public IReadOnlyList<RetryOperationStep> Steps { get; }

        public bool HasSteps => Steps.Count > 0;

        public string RetryCategory => string.Join(",", Steps.Select(item => item.RetryCategory));
    }
}
