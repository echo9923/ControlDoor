using System;
using System.Threading.Tasks;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Tasks;
using ControlDoor.Hikvision;
using ControlDoor.Observability;

namespace ControlDoor.CameraDoorInterlock
{
    /// <summary>
    /// 创建门禁常闭/恢复设备任务（task05）。任务通过 DeviceSdkDispatcher 投递到设备固定执行通道，
    /// 最终调用 IHikvisionGateway.ControlGatewayAsync（NET_DVR_ControlGateway）。
    /// 常闭 = GateControlCommand.AlwaysClose（High）；恢复 = Restore（Critical）。
    /// </summary>
    public sealed class DoorControlTaskFactory
    {
        public const string AlwaysCloseOperation = "ControlGatewayAlwaysClose";
        public const string RestoreOperation = "ControlGatewayRestoreControlled";

        private const int DefaultTimeoutMs = 10000;

        private static readonly int[] RetryableSdkErrorCodes = { 7, 8, 9, 10, 12, 13, 15, 20, 41, 43, 52, 408, 500 };

        private readonly IHikvisionGateway gateway;
        private readonly ServiceLogger logger;

        public DoorControlTaskFactory(IHikvisionGateway gateway, ServiceLogger logger = null)
        {
            this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
            this.logger = logger;
        }

        public DeviceSdkTask CreateAlwaysClose(int doorDeviceId, int doorNo, string targetKey, string requestId)
        {
            return CreateTask(
                doorDeviceId,
                doorNo,
                targetKey,
                requestId,
                GateControlCommand.AlwaysClose,
                AlwaysCloseOperation,
                DeviceTaskPriority.High,
                idempotencySuffix: ":AlwaysClose",
                payloadKind: "DoorAlwaysClose",
                attempt: 0);
        }

        public DeviceSdkTask CreateRestore(int doorDeviceId, int doorNo, string targetKey, string requestId, int attempt)
        {
            return CreateTask(
                doorDeviceId,
                doorNo,
                targetKey,
                requestId,
                GateControlCommand.Restore,
                RestoreOperation,
                DeviceTaskPriority.Critical,
                idempotencySuffix: ":Restore",
                payloadKind: "DoorRestoreControlled",
                attempt: attempt);
        }

        private DeviceSdkTask CreateTask(
            int doorDeviceId,
            int doorNo,
            string targetKey,
            string requestId,
            GateControlCommand command,
            string operationName,
            DeviceTaskPriority priority,
            string idempotencySuffix,
            string payloadKind,
            int attempt)
        {
            if (doorDeviceId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(doorDeviceId), "DoorDeviceId must be greater than zero.");
            }

            if (doorNo <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(doorNo), "DoorNo must be greater than zero.");
            }

            var safeTargetKey = targetKey ?? string.Empty;
            var safeRequestId = requestId ?? string.Empty;

            var task = new DeviceSdkTask(doorDeviceId, DeviceTaskType.ControlGateway, operationName, async taskContext =>
            {
                var startedAt = taskContext.Task.StartedAt ?? DateTime.Now;
                var snapshot = taskContext.SnapshotBeforeExecution;
                var statusAfter = snapshot == null ? DeviceConnectionStatus.Unknown : snapshot.Status;

                if (snapshot == null || !snapshot.SdkUserId.HasValue)
                {
                    return DeviceTaskResult.FromTask(taskContext.Task, false, "DEVICE_OFFLINE", "门禁设备未在线。", DeviceConnectionStatus.Offline, startedAt, DateTime.Now);
                }

                try
                {
                    var request = new GateControlRequest
                    {
                        UserId = snapshot.SdkUserId.Value,
                        GateIndex = doorNo,
                        Command = command,
                        TimeoutMilliseconds = DefaultTimeoutMs
                    };

                    var response = await gateway.ControlGatewayAsync(request, taskContext.CancellationToken).ConfigureAwait(false);
                    return DeviceTaskResult.FromTask(taskContext.Task, response.Success, response.Code, response.Message, statusAfter, startedAt, DateTime.Now);
                }
                catch (Exception ex)
                {
                    return MapException(taskContext.Task, ex, statusAfter, startedAt);
                }
            })
            {
                RequiresOnline = true,
                Priority = priority,
                WaitMode = DeviceTaskWaitMode.WaitForResult,
                TimeoutMilliseconds = DefaultTimeoutMs,
                RequestId = safeRequestId,
                CorrelationId = safeRequestId,
                IdempotencyKey = safeTargetKey + idempotencySuffix,
                Payload = new DeviceTaskPayload
                {
                    PayloadKind = payloadKind,
                    Body = new
                    {
                        targetKey = safeTargetKey,
                        doorDeviceId = doorDeviceId,
                        doorNo = doorNo,
                        command = command.ToString(),
                        attempt = attempt
                    },
                    PayloadSummary = "deviceId=" + doorDeviceId + ", door=" + doorNo + ", command=" + command + (attempt > 0 ? ", attempt=" + attempt : string.Empty)
                }
            };

            if (attempt > 0)
            {
                task.RetrySource = new DeviceTaskRetrySource
                {
                    IsRetry = true,
                    RetryCategory = "CameraDoorInterlockRestore",
                    RetryAttempt = attempt,
                    RetryStateKey = safeTargetKey
                };
            }

            return task;
        }

        private static DeviceTaskResult MapException(DeviceSdkTask task, Exception ex, DeviceConnectionStatus statusAfter, DateTime startedAt)
        {
            var gatewayEx = ex as DeviceGatewayException;
            if (gatewayEx != null)
            {
                var code = gatewayEx.Error.Code == 23 ? "DEVICE_UNSUPPORTED" : "SDK_ERROR";
                var result = DeviceTaskResult.FromTask(task, false, code, gatewayEx.Error.Message, statusAfter, startedAt, DateTime.Now);
                result.SdkErrorCode = gatewayEx.Error.Code;
                result.Retryable = IsRetryableSdkError(gatewayEx.Error.Code);
                return result;
            }

            if (ex is TimeoutException || ex is OperationCanceledException)
            {
                var timeout = DeviceTaskResult.FromTask(task, false, "TIMEOUT", ex.Message, statusAfter, startedAt, DateTime.Now);
                timeout.Retryable = true;
                return timeout;
            }

            return DeviceTaskResult.FromTask(task, false, "DEVICE_ERROR", ex.Message, statusAfter, startedAt, DateTime.Now);
        }

        private static bool IsRetryableSdkError(int code)
        {
            return Array.IndexOf(RetryableSdkErrorCodes, code) >= 0;
        }
    }
}
