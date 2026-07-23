using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Tasks;
using ControlDoor.Devices.Workers;
using ControlDoor.Hikvision;
using ControlDoor.Observability;

namespace ControlDoor.Permissions
{
    public sealed class RetryExecutionCoordinator
    {
        private const int MaxFaceBytes = 200 * 1024;
        private readonly DeviceSdkDispatcher dispatcher;
        private readonly IHikvisionGateway gateway;
        private readonly RetryPayloadParser payloadParser;
        private readonly RetryExecutionResultMapper resultMapper;
        private readonly ServiceLogger logger;

        public RetryExecutionCoordinator(DeviceSdkDispatcher dispatcher, IHikvisionGateway gateway, ServiceLogger logger = null)
        {
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
            this.logger = logger;
            payloadParser = new RetryPayloadParser { MaxFaceImageBytes = MaxFaceBytes };
            resultMapper = new RetryExecutionResultMapper();
        }

        public sealed class RetryExecutionHandle
        {
            private readonly TaskCompletionSource<RetryExecutionResult> waitResultSource =
                new TaskCompletionSource<RetryExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<RetryExecutionResult> finalResultSource =
                new TaskCompletionSource<RetryExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal RetryExecutionHandle(DeviceSdkTask deviceTask, DeviceTaskSubmissionResult submission)
            {
                DeviceTask = deviceTask ?? throw new ArgumentNullException(nameof(deviceTask));
                Submission = submission ?? throw new ArgumentNullException(nameof(submission));
            }

            public DeviceSdkTask DeviceTask { get; }

            public DeviceTaskSubmissionResult Submission { get; }

            public bool Accepted => Submission.Accepted;

            public bool HasStarted => DeviceTask.StartedAt.HasValue;

            public Task<DeviceTaskResult> DeviceTaskCompletion => DeviceTask.Completion.Task;

            public Task<DeviceTaskResult> FinalDeviceTaskCompletion => DeviceTaskCompletion;

            // WaitResult may represent a caller timeout/cancellation; it is never used for state mutation.
            public Task<RetryExecutionResult> WaitResult => waitResultSource.Task;

            public Task<RetryExecutionResult> InitialResult => waitResultSource.Task;

            public Task<RetryExecutionResult> Completion => finalResultSource.Task;

            public Task<RetryExecutionResult> FinalResult => finalResultSource.Task;

            public DeviceTaskResult FinalDeviceTaskResult { get; internal set; }

            internal void SetWaitResult(RetryExecutionResult result)
            {
                waitResultSource.TrySetResult(result);
            }

            internal void SetWaitException(Exception exception)
            {
                waitResultSource.TrySetException(exception);
            }

            internal void SetFinalResult(RetryExecutionResult result)
            {
                finalResultSource.TrySetResult(result);
            }

            internal void SetFinalException(Exception exception)
            {
                finalResultSource.TrySetException(exception);
            }
        }

        public RetryExecutionHandle Submit(RetryCommandPlan plan, string requestId)
        {
            return Submit(plan, requestId, CancellationToken.None);
        }

        public RetryExecutionHandle Submit(RetryCommandPlan plan, string requestId, CancellationToken cancellationToken)
        {
            ValidatePlan(plan);
            var task = CreateTask(plan, requestId);
            task.AttachCallerCancellationToken(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                var cancelled = DeviceTaskResult.Cancelled(task, "Caller cancelled before task submission.");
                task.TryComplete(cancelled);
                return CreateCompletedHandle(plan, task, cancelled);
            }

            DeviceTaskSubmissionResult submission;
            try
            {
                submission = dispatcher.Submit(task);
            }
            catch (Exception ex)
            {
                var failed = DeviceTaskResult.FromException(task, ex, DateTime.Now, DateTime.Now);
                task.TryComplete(failed);
                submission = DeviceTaskSubmissionResult.Rejected(task, failed);
            }

            var handle = new RetryExecutionHandle(task, submission);
            var cancellationRegistration = default(CancellationTokenRegistration);
            if (cancellationToken.CanBeCanceled)
            {
                cancellationRegistration = cancellationToken.Register(() =>
                {
                    if (!task.StartedAt.HasValue)
                    {
                        dispatcher.TryCancelQueuedTask(task.TaskId, "Caller cancelled before task started.");
                    }
                });
            }

            _ = ObserveFinalCompletionAsync(plan, handle, cancellationRegistration);
            _ = ObserveWaitOutcomeAsync(plan, handle, cancellationToken);
            return handle;
        }

        public async Task<RetryExecutionResult> ExecuteAsync(RetryCommandPlan plan, string requestId, CancellationToken cancellationToken)
        {
            var handle = Submit(plan, requestId, cancellationToken);
            return await handle.Completion.ConfigureAwait(false);
        }

        private RetryExecutionHandle CreateCompletedHandle(RetryCommandPlan plan, DeviceSdkTask task, DeviceTaskResult finalTaskResult)
        {
            var submission = DeviceTaskSubmissionResult.Rejected(task, finalTaskResult);
            var handle = new RetryExecutionHandle(task, submission)
            {
                FinalDeviceTaskResult = finalTaskResult
            };
            var mapped = MapFinalResult(plan, task, finalTaskResult);
            handle.SetWaitResult(mapped);
            handle.SetFinalResult(mapped);
            return handle;
        }

        private async Task ObserveFinalCompletionAsync(
            RetryCommandPlan plan,
            RetryExecutionHandle handle,
            CancellationTokenRegistration cancellationRegistration)
        {
            try
            {
                var finalTaskResult = await handle.DeviceTaskCompletion.ConfigureAwait(false);
                handle.FinalDeviceTaskResult = finalTaskResult;
                var mapped = MapFinalResult(plan, handle.DeviceTask, finalTaskResult);
                handle.SetFinalResult(mapped);
            }
            catch (Exception ex)
            {
                handle.SetFinalException(ex);
            }
            finally
            {
                cancellationRegistration.Dispose();
            }
        }

        private async Task ObserveWaitOutcomeAsync(RetryCommandPlan plan, RetryExecutionHandle handle, CancellationToken cancellationToken)
        {
            try
            {
                var completion = handle.DeviceTaskCompletion;
                Task deadlineTask = null;
                if (handle.DeviceTask.DeadlineAt.HasValue)
                {
                    var remaining = handle.DeviceTask.DeadlineAt.Value - DateTime.Now;
                    deadlineTask = remaining <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(remaining);
                }

                Task cancellationTask = null;
                if (cancellationToken.CanBeCanceled)
                {
                    cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                if (deadlineTask == null && cancellationTask == null)
                {
                    // 等待层必须表达设备任务真实终态；FinalResult 与 Completion 同源，直接读取最终设备结果映射，
                    // 避免最终 observer 回写 WaitResult 时抢占调用方 timeout/cancel 语义。
                    handle.SetWaitResult(MapFinalResult(plan, handle.DeviceTask, await completion.ConfigureAwait(false)));
                    return;
                }

                // Timer/cancellation tasks precede Completion deliberately. If a final device
                // result lands on the same boundary, the caller's wait outcome remains visible
                // through WaitResult while FinalResult is still completed by the final observer.
                var waiters = new List<Task>();
                if (cancellationTask != null)
                {
                    waiters.Add(cancellationTask);
                }

                if (deadlineTask != null)
                {
                    waiters.Add(deadlineTask);
                }

                waiters.Add(completion);
                var completed = await Task.WhenAny(waiters).ConfigureAwait(false);
                if (completed == completion)
                {
                    handle.SetWaitResult(MapFinalResult(plan, handle.DeviceTask, await completion.ConfigureAwait(false)));
                    return;
                }

                if (completed == cancellationTask)
                {
                    var removed = !handle.DeviceTask.StartedAt.HasValue &&
                        dispatcher.TryCancelQueuedTask(handle.DeviceTask.TaskId, "Caller cancelled before task started.");
                    var waitResult = DeviceTaskResult.Cancelled(
                        handle.DeviceTask,
                        removed ? "Caller cancelled before task started." : "Caller cancelled while waiting for task result.",
                        isWaitOutcome: !removed);
                    handle.SetWaitResult(MapFinalResult(plan, handle.DeviceTask, waitResult));
                    return;
                }

                var removedBeforeExecution = !handle.DeviceTask.StartedAt.HasValue &&
                    dispatcher.TryCancelQueuedTask(handle.DeviceTask.TaskId, "Caller wait timed out before task started.");
                var timeoutResult = DeviceTaskResult.Timeout(
                    handle.DeviceTask,
                    removedBeforeExecution ? "Caller wait timed out before task started." : "Caller wait timed out before task completed.",
                    isWaitOutcome: !removedBeforeExecution);
                handle.SetWaitResult(MapFinalResult(plan, handle.DeviceTask, timeoutResult));
            }
            catch (Exception ex)
            {
                handle.SetWaitException(ex);
            }
        }

        private RetryExecutionResult MapFinalResult(RetryCommandPlan plan, DeviceSdkTask task, DeviceTaskResult finalTaskResult)
        {
            var executionResult = finalTaskResult?.Data as RetryExecutionResult;
            if (executionResult == null)
            {
                executionResult = resultMapper.Map(plan.State, plan, new[] { finalTaskResult });
                executionResult.TaskStarted = task.StartedAt.HasValue;
            }

            executionResult.FinalDeviceTaskResult = finalTaskResult;
            return executionResult;
        }

        private static void ValidatePlan(RetryCommandPlan plan)
        {
            if (plan == null || plan.State == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }
        }

        private DeviceSdkTask CreateTask(RetryCommandPlan plan, string requestId)
        {
            var state = plan.State;
            return new DeviceSdkTask(state.DeviceId, DeviceTaskType.RetryDeviceOperation, "RetryDeviceOperation", context => ExecutePlanInsideWorkerAsync(plan, context))
            {
                RequiresOnline = true,
                Priority = DeviceTaskPriority.Retry,
                WaitMode = DeviceTaskWaitMode.WaitForResult,
                RequestId = requestId ?? string.Empty,
                CorrelationId = requestId ?? string.Empty,
                IdempotencyKey = "retry:" + state.StateKey,
                RetrySource = new DeviceTaskRetrySource
                {
                    IsRetry = true,
                    RetryCategory = plan.RetryCategory,
                    RetryAttempt = state.AttemptCount + 1,
                    RetryStateKey = state.StateKey,
                    OriginalRequestId = requestId ?? string.Empty
                },
                Payload = new DeviceTaskPayload
                {
                    PayloadKind = "DeviceOperationRetryState",
                    Body = state,
                    PayloadSummary = "deviceId=" + state.DeviceId + ", employeeId=" + state.EmployeeId + ", category=" + plan.RetryCategory,
                    AllowFullPayloadLogging = false
                }
            };
        }

        private async Task<DeviceTaskResult> ExecutePlanInsideWorkerAsync(RetryCommandPlan plan, DeviceTaskContext context)
        {
            var taskResults = new List<DeviceTaskResult>();
            var operationStarted = false;
            foreach (var step in plan.Steps)
            {
                if (!operationStarted && context.CancellationToken.IsCancellationRequested)
                {
                    var cancelled = DeviceTaskResult.Cancelled(context.Task, "补偿任务在设备操作开始前取消。");
                    cancelled.OperationName = RetryOperationNames.ToStage5OperationName(step.Operation);
                    taskResults.Add(cancelled);
                    break;
                }

                operationStarted = true;
                var result = await ExecuteStepAsync(plan.State, step.Operation, context).ConfigureAwait(false);
                taskResults.Add(result);
                if (!result.Success || step.Operation == RetryOperation.DeletePerson)
                {
                    break;
                }
            }

            var mapped = resultMapper.Map(plan.State, plan, taskResults);
            mapped.TaskStarted = operationStarted;
            var started = context.Task.StartedAt ?? DateTime.Now;
            var completed = DateTime.Now;
            var outer = DeviceTaskResult.FromTask(
                context.Task,
                mapped.AllSucceeded,
                mapped.Code,
                mapped.Message,
                context.SnapshotBeforeExecution == null ? DeviceConnectionStatus.Unknown : context.SnapshotBeforeExecution.Status,
                started,
                completed);
            outer.Retryable = mapped.Retryable;
            outer.SdkErrorCode = mapped.SdkErrorCode;
            outer.Data = mapped;
            return outer;
        }

        private async Task<DeviceTaskResult> ExecuteStepAsync(DeviceOperationRetryState state, RetryOperation operation, DeviceTaskContext context)
        {
            var task = context.Task;
            var started = DateTime.Now;
            var snapshot = context.SnapshotBeforeExecution;
            if (snapshot == null || !snapshot.SdkUserId.HasValue)
            {
                var offline = DeviceTaskResult.FromTask(task, false, "DEVICE_OFFLINE", "设备未在线。", DeviceConnectionStatus.Offline, started, DateTime.Now);
                offline.OperationName = RetryOperationNames.ToStage5OperationName(operation);
                offline.Retryable = true;
                return offline;
            }

            try
            {
                await ExecuteGatewayOperationAsync(state, operation, snapshot, context.CancellationToken).ConfigureAwait(false);
                var success = DeviceTaskResult.FromTask(task, true, "OK", SuccessMessage(operation), snapshot.Status, started, DateTime.Now);
                success.OperationName = RetryOperationNames.ToStage5OperationName(operation);
                return success;
            }
            catch (Exception ex)
            {
                var failed = MapGatewayException(task, operation, ex, snapshot, started);
                logger?.Warn("DeviceOperationRetry", "补偿步骤执行失败。", new LogFields
                {
                    RequestId = task.RequestId,
                    DeviceId = state.DeviceId,
                    EmployeeId = state.EmployeeId,
                    OperationName = RetryOperationNames.ToStage5OperationName(operation),
                    ErrorCode = failed.Code,
                    Extra = { ["retryable"] = failed.Retryable.ToString() }
                });
                return failed;
            }
        }

        private async Task ExecuteGatewayOperationAsync(DeviceOperationRetryState state, RetryOperation operation, DeviceRuntimeSnapshot snapshot, CancellationToken cancellationToken)
        {
            var userId = snapshot.SdkUserId.Value;
            switch (operation)
            {
                case RetryOperation.DeletePerson:
                    await DeleteFaceBeforePersonBestEffortAsync(userId, state.EmployeeId, cancellationToken).ConfigureAwait(false);
                    await gateway.DeletePersonAsync(new DeletePersonRequest { UserId = userId, EmployeeId = state.EmployeeId }, cancellationToken).ConfigureAwait(false);
                    return;
                case RetryOperation.DeleteFace:
                    await gateway.DeleteFaceAsync(new DeleteFaceRequest { UserId = userId, EmployeeId = state.EmployeeId }, cancellationToken).ConfigureAwait(false);
                    return;
                case RetryOperation.Person:
                    await gateway.UpsertPersonAsync(new UpsertPersonRequest
                    {
                        UserId = userId,
                        Person = payloadParser.ParsePerson(state.PersonPayloadJson, state.EmployeeId)
                    }, cancellationToken).ConfigureAwait(false);
                    return;
                case RetryOperation.Permission:
                    var level = state.PermissionLevel ?? 0;
                    var person = payloadParser.ParsePermissionPerson(state.PermissionPayloadJson, state.EmployeeId);
                    person.Enabled = DevicePermissionAreaPolicy.ShouldEnable(snapshot.Description, level);
                    await gateway.UpsertPersonAsync(new UpsertPersonRequest
                    {
                        UserId = userId,
                        Person = person,
                        ProvisioningMode = PersonProvisioningMode.Permission
                    }, cancellationToken).ConfigureAwait(false);
                    return;
                case RetryOperation.Face:
                    await gateway.UploadFaceAsync(new UploadFaceRequest
                    {
                        UserId = userId,
                        MaxImageBytes = MaxFaceBytes,
                        Face = payloadParser.ParseFace(state.FacePayloadJson, state.EmployeeId)
                    }, cancellationToken).ConfigureAwait(false);
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }

        private async Task DeleteFaceBeforePersonBestEffortAsync(int userId, string employeeId, CancellationToken cancellationToken)
        {
            try
            {
                await gateway.DeleteFaceAsync(new DeleteFaceRequest { UserId = userId, EmployeeId = employeeId }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger?.Warn("RetryExecution", "删除人员补偿前删除人脸失败，忽略并继续删除人员。", CreateBestEffortDeleteFaceLogFields(userId, employeeId, ex));
            }
        }

        private static LogFields CreateBestEffortDeleteFaceLogFields(int userId, string employeeId, Exception ex)
        {
            var fields = new LogFields
            {
                EmployeeId = employeeId,
                OperationName = "RetryDeleteFaceBeforePerson",
                ErrorCode = ResolveErrorCode(ex),
                Exception = ex == null ? string.Empty : ex.GetType().Name + ": " + ex.Message
            };
            fields.Extra["userId"] = userId.ToString();
            return fields;
        }

        private static string ResolveErrorCode(Exception ex)
        {
            var gatewayEx = ex as DeviceGatewayException;
            if (gatewayEx != null && gatewayEx.Error != null)
            {
                return gatewayEx.Error.Code.ToString();
            }

            return ex == null ? string.Empty : ex.GetType().Name;
        }

        private static DeviceTaskResult MapGatewayException(DeviceSdkTask task, RetryOperation operation, Exception ex, DeviceRuntimeSnapshot snapshot, DateTime started)
        {
            var status = snapshot == null ? DeviceConnectionStatus.Unknown : snapshot.Status;
            var gatewayEx = ex as DeviceGatewayException;
            if (gatewayEx != null)
            {
                var result = DeviceTaskResult.FromTask(task, false, gatewayEx.Error.Code == 23 ? "DEVICE_UNSUPPORTED" : "SDK_ERROR", gatewayEx.Error.Message, status, started, DateTime.Now);
                result.OperationName = RetryOperationNames.ToStage5OperationName(operation);
                result.SdkErrorCode = gatewayEx.Error.Code;
                result.Retryable = IsRetryableSdkError(gatewayEx.Error.Code);
                return result;
            }

            if (ex is TimeoutException || ex is OperationCanceledException)
            {
                var timeout = DeviceTaskResult.FromTask(task, false, "TIMEOUT", ex.Message, status, started, DateTime.Now);
                timeout.OperationName = RetryOperationNames.ToStage5OperationName(operation);
                timeout.Retryable = true;
                return timeout;
            }

            var invalidPayload = ex is FormatException || ex is InvalidOperationException || ex is ArgumentException;
            var failed = DeviceTaskResult.FromTask(task, false, invalidPayload ? "INVALID_PAYLOAD" : "DEVICE_ERROR", ex == null ? "设备操作失败。" : ex.Message, status, started, DateTime.Now);
            failed.OperationName = RetryOperationNames.ToStage5OperationName(operation);
            failed.Retryable = !invalidPayload;
            return failed;
        }

        private static string SuccessMessage(RetryOperation operation)
        {
            switch (operation)
            {
                case RetryOperation.Permission:
                    return "权限补偿成功。";
                case RetryOperation.Person:
                    return "人员补偿成功。";
                case RetryOperation.Face:
                    return "人脸补偿成功。";
                case RetryOperation.DeleteFace:
                    return "删除人脸补偿成功。";
                case RetryOperation.DeletePerson:
                    return "删除人员补偿成功。";
                default:
                    return "补偿成功。";
            }
        }

        private static bool IsRetryableSdkError(int code)
        {
            return code == 7 || code == 41 || code == 43 || code == 52 || code == 408 || code == 500;
        }
    }
}
