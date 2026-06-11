using System;
using System.Threading;
using System.Threading.Tasks;

namespace ControlDoor.Devices.Tasks
{
    public sealed class DeviceSdkTask
    {
        public DeviceSdkTask(int deviceId, DeviceTaskType taskType, string operationName, Func<DeviceTaskContext, Task<DeviceTaskResult>> executeAsync)
        {
            if (deviceId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(deviceId), "DeviceId must be greater than zero.");
            }

            DeviceId = deviceId;
            TaskType = taskType;
            OperationName = string.IsNullOrWhiteSpace(operationName) ? taskType.ToString() : operationName.Trim();
            ExecuteAsync = executeAsync;
            TaskId = Guid.NewGuid().ToString("N");
            CreatedAt = DateTime.Now;
            RequestId = string.Empty;
            CorrelationId = string.Empty;
            Priority = DeviceTaskPriority.Normal;
            WaitMode = DeviceTaskWaitMode.WaitForResult;
            TimeoutMilliseconds = 0;
            CallerCancellationToken = CancellationToken.None;
            Payload = DeviceTaskPayload.Empty();
            RetrySource = new DeviceTaskRetrySource();
            Completion = new DeviceTaskCompletion();
            ExecutionState = DeviceTaskExecutionState.Created;
        }

        public string TaskId { get; }

        public string RequestId { get; set; }

        public string CorrelationId { get; set; }

        public int DeviceId { get; }

        public DeviceTaskType TaskType { get; }

        public string OperationName { get; }

        public DeviceTaskPriority Priority { get; set; }

        public DateTime CreatedAt { get; }

        public DateTime? EnqueuedAt { get; private set; }

        public DateTime? StartedAt { get; private set; }

        public DateTime? CompletedAt { get; private set; }

        public DateTime? DeadlineAt { get; private set; }

        public int TimeoutMilliseconds { get; set; }

        public CancellationToken CallerCancellationToken { get; private set; }

        public DeviceTaskWaitMode WaitMode { get; set; }

        public bool RequiresOnline { get; set; }

        public bool AllowWhenDeleting { get; set; }

        public bool AllowWhenManualDisconnected { get; set; }

        public string IdempotencyKey { get; set; } = string.Empty;

        public DeviceTaskRetrySource RetrySource { get; set; }

        public DeviceTaskPayload Payload { get; set; }

        public Func<DeviceTaskContext, Task<DeviceTaskResult>> ExecuteAsync { get; }

        public DeviceTaskCompletion Completion { get; }

        public DeviceTaskExecutionState ExecutionState { get; private set; }

        public long? Sequence { get; private set; }

        public void MarkQueued(DateTime enqueuedAt, long sequence, int effectiveTimeoutMilliseconds)
        {
            EnqueuedAt = enqueuedAt;
            Sequence = sequence;
            DeadlineAt = effectiveTimeoutMilliseconds > 0 ? enqueuedAt.AddMilliseconds(effectiveTimeoutMilliseconds) : (DateTime?)null;
            ExecutionState = DeviceTaskExecutionState.Queued;
        }

        public void AttachCallerCancellationToken(CancellationToken cancellationToken)
        {
            CallerCancellationToken = cancellationToken;
        }

        public void MarkRunning(DateTime startedAt)
        {
            StartedAt = startedAt;
            ExecutionState = DeviceTaskExecutionState.Running;
        }

        public void MarkCompleted(DeviceTaskResult result)
        {
            CompletedAt = result == null ? DateTime.Now : result.CompletedAt;
            if (result == null)
            {
                ExecutionState = DeviceTaskExecutionState.Failed;
                return;
            }

            if (result.Code == "TIMEOUT")
            {
                ExecutionState = DeviceTaskExecutionState.TimedOut;
            }
            else if (result.Code == "CANCELLED")
            {
                ExecutionState = DeviceTaskExecutionState.Cancelled;
            }
            else if (result.Success)
            {
                ExecutionState = DeviceTaskExecutionState.Succeeded;
            }
            else
            {
                ExecutionState = DeviceTaskExecutionState.Failed;
            }
        }

        public void MarkRejected(DeviceTaskResult result)
        {
            CompletedAt = result == null ? DateTime.Now : result.CompletedAt;
            ExecutionState = DeviceTaskExecutionState.Rejected;
        }

        public void MarkCancelled(DateTime cancelledAt)
        {
            CompletedAt = cancelledAt;
            ExecutionState = DeviceTaskExecutionState.Cancelled;
        }

        public int GetEffectiveTimeoutMilliseconds(int defaultTimeoutMilliseconds)
        {
            return TimeoutMilliseconds > 0 ? TimeoutMilliseconds : defaultTimeoutMilliseconds;
        }
    }
}
