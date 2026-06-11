using System;
using System.Threading.Tasks;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Tasks;

namespace ControlEntradaSalida.Tests
{
    public static class Stage2Task03DeviceSdkTaskTests
    {
        [TestCase]
        public static void DeviceSdkTask_Defaults_CreateTraceableTaskContract()
        {
            var task = NewTask(7, DeviceTaskType.SyncPerson);

            Assert.NotNull(task.TaskId);
            Assert.Equal(7, task.DeviceId);
            Assert.Equal(DeviceTaskType.SyncPerson, task.TaskType);
            Assert.Equal("SyncPerson", task.OperationName);
            Assert.Equal(DeviceTaskPriority.Normal, task.Priority);
            Assert.Equal(DeviceTaskWaitMode.WaitForResult, task.WaitMode);
            Assert.Equal(DeviceTaskExecutionState.Created, task.ExecutionState);
            Assert.NotNull(task.Payload);
            Assert.NotNull(task.RetrySource);
            Assert.NotNull(task.Completion);
        }

        [TestCase]
        public static void DeviceSdkTask_Queued_ComputesDeadlineAndSequence()
        {
            var task = NewTask(8, DeviceTaskType.Login);
            var enqueuedAt = new DateTime(2026, 1, 1, 8, 0, 0);
            task.TimeoutMilliseconds = 1500;

            task.MarkQueued(enqueuedAt, sequence: 12, effectiveTimeoutMilliseconds: task.GetEffectiveTimeoutMilliseconds(30000));

            Assert.Equal(DeviceTaskExecutionState.Queued, task.ExecutionState);
            Assert.Equal(enqueuedAt, task.EnqueuedAt.Value);
            Assert.Equal(12L, task.Sequence.Value);
            Assert.Equal(enqueuedAt.AddMilliseconds(1500), task.DeadlineAt.Value);
        }

        [TestCase]
        public static void DeviceSdkTask_ResultFactories_MapUnifiedCodes()
        {
            var task = NewTask(9, DeviceTaskType.HealthCheck);

            var queued = DeviceTaskResult.Queued(task);
            var rejected = DeviceTaskResult.Rejected(task, "DEVICE_NOT_FOUND", "missing");
            var timeout = DeviceTaskResult.Timeout(task);
            var cancelled = DeviceTaskResult.Cancelled(task);

            Assert.True(queued.Success);
            Assert.Equal("QUEUED", queued.Code);
            Assert.False(rejected.Success);
            Assert.Equal("DEVICE_NOT_FOUND", rejected.Code);
            Assert.Equal("TIMEOUT", timeout.Code);
            Assert.Equal("CANCELLED", cancelled.Code);
        }

        [TestCase]
        public static void DeviceSdkTask_MarkCompleted_UpdatesLifecycleState()
        {
            var task = NewTask(10, DeviceTaskType.ProbeCapabilities);
            var startedAt = new DateTime(2026, 1, 1, 8, 0, 0);
            var completedAt = startedAt.AddMilliseconds(25);
            var result = DeviceTaskResult.FromTask(task, true, "OK", "done", DeviceConnectionStatus.Online, startedAt, completedAt);

            task.MarkRunning(startedAt);
            task.MarkCompleted(result);

            Assert.Equal(DeviceTaskExecutionState.Succeeded, task.ExecutionState);
            Assert.Equal(completedAt, task.CompletedAt.Value);
            Assert.Equal(25L, result.DurationMilliseconds);
        }

        [TestCase]
        public static void DeviceTaskCompletion_CompletesOnlyOnce()
        {
            var task = NewTask(11, DeviceTaskType.DeleteFace);
            var first = DeviceTaskResult.Queued(task);
            var second = DeviceTaskResult.Cancelled(task);

            Assert.True(task.Completion.TrySetResult(first));
            Assert.False(task.Completion.TrySetResult(second));
            Assert.Equal("QUEUED", task.Completion.Task.GetAwaiter().GetResult().Code);
        }

        [TestCase]
        public static void DeviceTaskPayloadAndRetrySource_AreCloneableForDiagnostics()
        {
            var payload = new DeviceTaskPayload
            {
                PayloadKind = "Person",
                Body = "employee-1",
                PayloadSummary = "employee_id=1",
                PayloadSizeBytes = 42,
                AllowFullPayloadLogging = true
            };
            var retry = new DeviceTaskRetrySource
            {
                IsRetry = true,
                RetryAttempt = 2,
                RetryCategory = "face",
                RetryStateKey = "device:1:employee:2",
                OriginalRequestId = "req-1"
            };

            var payloadClone = payload.Clone();
            var retryClone = retry.Clone();
            payloadClone.PayloadSummary = "changed";
            retryClone.RetryAttempt = 3;

            Assert.Equal("employee_id=1", payload.PayloadSummary);
            Assert.Equal(2, retry.RetryAttempt);
            Assert.True(retryClone.IsRetry);
        }

        [TestCase]
        public static void DeviceTaskType_CoversPlannedStageOperations()
        {
            Assert.True(Enum.IsDefined(typeof(DeviceTaskType), DeviceTaskType.Login));
            Assert.True(Enum.IsDefined(typeof(DeviceTaskType), DeviceTaskType.Logout));
            Assert.True(Enum.IsDefined(typeof(DeviceTaskType), DeviceTaskType.SetupAlarm));
            Assert.True(Enum.IsDefined(typeof(DeviceTaskType), DeviceTaskType.SyncPermission));
            Assert.True(Enum.IsDefined(typeof(DeviceTaskType), DeviceTaskType.SyncPerson));
            Assert.True(Enum.IsDefined(typeof(DeviceTaskType), DeviceTaskType.UploadFace));
            Assert.True(Enum.IsDefined(typeof(DeviceTaskType), DeviceTaskType.RetryDeviceOperation));
            Assert.True(Enum.IsDefined(typeof(DeviceTaskType), DeviceTaskType.QueryHistoryEvents));
            Assert.True(Enum.IsDefined(typeof(DeviceTaskType), DeviceTaskType.ControlGateway));
        }

        private static DeviceSdkTask NewTask(int deviceId, DeviceTaskType type)
        {
            return new DeviceSdkTask(deviceId, type, type.ToString(), context =>
            {
                var now = DateTime.Now;
                return Task.FromResult(DeviceTaskResult.FromTask(context.Task, true, "OK", "ok", DeviceConnectionStatus.Online, now, now));
            });
        }
    }
}
