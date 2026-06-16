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

        public async Task<RetryExecutionResult> ExecuteAsync(RetryCommandPlan plan, string requestId, CancellationToken cancellationToken)
        {
            if (plan == null || plan.State == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            var state = plan.State;
            var task = new DeviceSdkTask(state.DeviceId, DeviceTaskType.RetryDeviceOperation, "RetryDeviceOperation", context => ExecutePlanInsideWorkerAsync(plan, context))
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

            var result = await dispatcher.SubmitAndWaitAsync(task, cancellationToken).ConfigureAwait(false);
            var executionResult = result?.Data as RetryExecutionResult;
            if (executionResult != null)
            {
                return executionResult;
            }

            return resultMapper.Map(state, plan, new[] { result });
        }

        private async Task<DeviceTaskResult> ExecutePlanInsideWorkerAsync(RetryCommandPlan plan, DeviceTaskContext context)
        {
            var taskResults = new List<DeviceTaskResult>();
            foreach (var step in plan.Steps)
            {
                var result = await ExecuteStepAsync(plan.State, step.Operation, context).ConfigureAwait(false);
                taskResults.Add(result);
                if (!result.Success || step.Operation == RetryOperation.DeletePerson)
                {
                    break;
                }
            }

            var mapped = resultMapper.Map(plan.State, plan, taskResults);
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
                    await gateway.DeleteFaceAsync(new DeleteFaceRequest { UserId = userId, EmployeeId = state.EmployeeId }, cancellationToken).ConfigureAwait(false);
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
