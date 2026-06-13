using System;
using System.Collections.Generic;
using System.Linq;
using ControlDoor.Devices.Tasks;

namespace ControlDoor.Permissions
{
    public sealed class RetryExecutionResultMapper
    {
        public RetryExecutionResult Map(DeviceOperationRetryState state, RetryCommandPlan plan, IEnumerable<DeviceTaskResult> taskResults)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            plan = plan ?? new RetryCommandPlan(state, Enumerable.Empty<RetryOperationStep>());
            var results = (taskResults ?? Enumerable.Empty<DeviceTaskResult>()).ToList();
            var succeeded = new List<RetryOperation>();

            for (var index = 0; index < results.Count && index < plan.Steps.Count; index++)
            {
                var result = results[index];
                var operation = plan.Steps[index].Operation;
                if (result != null && result.Success)
                {
                    succeeded.Add(operation);
                    if (operation == RetryOperation.DeletePerson)
                    {
                        return new RetryExecutionResult(state, succeeded, null, false, "OK", "删除人员补偿成功。", null);
                    }

                    continue;
                }

                return new RetryExecutionResult(
                    state,
                    succeeded,
                    operation,
                    result != null && result.Retryable,
                    result == null ? "INTERNAL_ERROR" : result.Code,
                    result == null ? "设备任务没有返回结果。" : result.Message,
                    result?.SdkErrorCode);
            }

            return new RetryExecutionResult(state, succeeded, null, false, "OK", "补偿执行成功。", null);
        }
    }
}
