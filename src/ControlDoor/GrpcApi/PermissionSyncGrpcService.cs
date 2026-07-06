using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ControlDoor.Devices.Management;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Tasks;
using ControlDoor.Devices.Workers;
using ControlDoor.Hikvision;
using ControlDoor.Observability;
using ControlDoor.Permissions;

namespace ControlDoor.GrpcApi
{
    public sealed class PermissionSyncGrpcService
    {
        public const string ServiceName = "permission.PermissionSyncService";
        public const string SyncPermissionsFullName = "/permission.PermissionSyncService/SyncPermissions";
        public const string SyncPersonsFullName = "/permission.PermissionSyncService/SyncPersons";
        public const string DeleteFacesFullName = "/permission.PermissionSyncService/DeleteFaces";
        public const string DeletePersonsFullName = "/permission.PermissionSyncService/DeletePersons";
        public const string GetFacesFullName = "/permission.PermissionSyncService/GetFaces";
        public const string CaptureFaceStreamFullName = "/permission.PermissionSyncService/CaptureFaceStream";
        public const string GetEnrollmentStatusFullName = "/permission.PermissionSyncService/GetEnrollmentStatus";

        private const int MaxBatchSize = 500;
        private const int MaxFaceBytes = 200 * 1024;

        private readonly DeviceRuntimeRegistry registry;
        private readonly DeviceSdkDispatcher dispatcher;
        private readonly IHikvisionGateway gateway;
        private readonly IDeviceOperationRetryWriter retryWriter;
        private readonly IUserSyncStatusWriter userSyncWriter;
        private readonly EnrollmentTaskStore enrollmentStore;
        private readonly ServiceLogger logger;
        private readonly GrpcCallLogger grpcLogger;
        private readonly int? defaultFaceCaptureDeviceId;

        public PermissionSyncGrpcService(
            DeviceRuntimeRegistry registry,
            DeviceSdkDispatcher dispatcher,
            IHikvisionGateway gateway,
            IDeviceOperationRetryWriter retryWriter = null,
            IUserSyncStatusWriter userSyncWriter = null,
            EnrollmentTaskStore enrollmentStore = null,
            ServiceLogger logger = null,
            int? defaultFaceCaptureDeviceId = null,
            LogOptions logOptions = null)
        {
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
            this.retryWriter = retryWriter ?? new NullDeviceOperationRetryWriter();
            this.userSyncWriter = userSyncWriter ?? new NullUserSyncStatusWriter();
            this.enrollmentStore = enrollmentStore ?? new EnrollmentTaskStore();
            this.logger = logger;
            grpcLogger = logger == null ? null : new GrpcCallLogger(logger, logOptions);
            this.defaultFaceCaptureDeviceId = defaultFaceCaptureDeviceId;
        }

        public IReadOnlyList<string> MethodFullNames { get; } = new[]
        {
            SyncPermissionsFullName,
            SyncPersonsFullName,
            DeleteFacesFullName,
            DeletePersonsFullName,
            GetFacesFullName,
            CaptureFaceStreamFullName,
            GetEnrollmentStatusFullName
        };

        public string SyncPermissions(string requestJson, GrpcRequestContext context = null)
        {
            return ExecuteUnary("SyncPermissions", requestJson, context, SyncPermissionsCore);
        }

        private string SyncPermissionsCore(string requestJson, GrpcRequestContext context)
        {
            context = EnsureContext(context);
            ParseResult<PermissionCommand> parsed;
            try
            {
                parsed = ParsePermissionCommands(requestJson);
            }
            catch (RequestValidationException ex)
            {
                return Error(context, ex.Code, ex.Message);
            }
            catch (Exception ex)
            {
                return Error(context, "INVALID_ARGUMENT", ex.Message);
            }

            if (!parsed.Success)
            {
                return Error(context, parsed.Code, parsed.Message);
            }

            var devices = GetAcsTargetDevices();
            var onlineDevices = devices.Where(item => item.Enabled && item.IsConnected && item.SdkUserId.HasValue).ToList();
            var offlineDevices = devices.Where(item => item.Enabled && (!item.IsConnected || !item.SdkUserId.HasValue)).ToList();
            var disabledCount = devices.Count(item => !item.Enabled || item.Status == DeviceConnectionStatus.Disabled || item.Status == DeviceConnectionStatus.InvalidConfig);

            var employeeResults = CreateEmployeeResults(parsed.Items.Select(item => item.EmployeeId));
            var queuedDetails = new List<object>();
            var deviceErrors = new List<object>();
            var dbErrors = new List<object>();

            foreach (var command in parsed.Items)
            {
                foreach (var device in onlineDevices)
                {
                    var result = ExecutePermissionTask(device, command, context);
                    var detail = ToDeviceResult(device, result);
                    var shouldQueueRetry = ShouldQueueSyncPermissionRetry(result);
                    if (shouldQueueRetry)
                    {
                        detail.Queued = true;
                    }

                    employeeResults[command.EmployeeId].DeviceResults.Add(detail);
                    if (!result.Success)
                    {
                        deviceErrors.Add(ToDeviceError(device, command.EmployeeId, result));
                    }

                    if (shouldQueueRetry)
                    {
                        var queued = QueueRetry(device, command.EmployeeId, "SyncPermission", PermissionPayload(command), command.PermissionCode, result.Message, context);
                        queuedDetails.Add(queued);
                    }
                }

                foreach (var device in offlineDevices)
                {
                    var queued = QueueRetry(device, command.EmployeeId, "SyncPermission", PermissionPayload(command), command.PermissionCode, "设备离线，已生成补偿意图。", context);
                    queuedDetails.Add(queued);
                    employeeResults[command.EmployeeId].DeviceResults.Add(ToQueuedDeviceResult(device, "SyncPermission"));
                }

                if (IsEmployeeOperationComplete(employeeResults[command.EmployeeId]))
                {
                    TryUpdateUser(dbErrors, () => userSyncWriter.MarkPermissionSynced(command.EmployeeId, command.PermissionCode), command.EmployeeId, "MarkPermissionSynced");
                }
            }

            var succeededEmployees = employeeResults.Values.Count(IsEmployeeOperationComplete);
            var failedEmployees = employeeResults.Values.Count(item => item.DeviceResults.Any(device => !device.Success && !device.Queued));
            var queuedEmployees = employeeResults.Values.Count(item => item.DeviceResults.Any(device => device.Queued));
            var code = DetermineCode(parsed.Items.Count, succeededEmployees, failedEmployees, queuedEmployees);

            return JsonResponse.Create(context.RequestId, code != "FAILED", code, BuildMessage(code), new Dictionary<string, object>
            {
                ["total"] = parsed.Items.Count,
                ["updated"] = succeededEmployees,
                ["skipped"] = disabledCount,
                ["failed"] = failedEmployees,
                ["queued"] = queuedEmployees,
                ["queuedDetails"] = queuedDetails,
                ["items"] = employeeResults.Values.Select(item => item.ToDictionary()).ToList(),
                ["deviceErrors"] = deviceErrors,
                ["dbErrors"] = dbErrors
            });
        }

        public System.Threading.Tasks.Task<string> SyncPermissionsAsync(string requestJson, GrpcRequestContext context = null)
        {
            context = EnsureContext(context);
            return System.Threading.Tasks.Task.Run(() => SyncPermissions(requestJson, context), context.CancellationToken);
        }

        public string SyncPersons(string requestJson, GrpcRequestContext context = null)
        {
            return ExecuteUnary("SyncPersons", requestJson, context, SyncPersonsCore);
        }

        private string SyncPersonsCore(string requestJson, GrpcRequestContext context)
        {
            context = EnsureContext(context);
            ParseResult<PersonSyncCommand> parsed;
            try
            {
                parsed = ParsePersonCommands(requestJson);
            }
            catch (RequestValidationException ex)
            {
                return Error(context, ex.Code, ex.Message);
            }
            catch (Exception ex)
            {
                return Error(context, "INVALID_ARGUMENT", ex.Message);
            }

            if (!parsed.Success)
            {
                return Error(context, parsed.Code, parsed.Message);
            }

            var devices = GetAcsTargetDevices();
            var onlineDevices = devices.Where(item => item.Enabled && item.IsConnected && item.SdkUserId.HasValue).ToList();
            var offlineDevices = devices.Where(item => item.Enabled && (!item.IsConnected || !item.SdkUserId.HasValue)).ToList();
            var employeeResults = CreateEmployeeResults(parsed.Items.Select(item => item.EmployeeId));
            var queuedDetails = new List<object>();
            var deviceErrors = new List<object>();
            var dbErrors = new List<object>();
            var facesUploaded = 0;

            foreach (var command in parsed.Items)
            {
                foreach (var device in onlineDevices)
                {
                    var personResult = ExecutePersonTask(device, command, context);
                    var personDetail = ToDeviceResult(device, personResult, "SyncPerson");
                    if (!personResult.Success && personResult.Retryable)
                    {
                        personDetail.Queued = true;
                    }

                    employeeResults[command.EmployeeId].DeviceResults.Add(personDetail);
                    if (!personResult.Success)
                    {
                        deviceErrors.Add(ToDeviceError(device, command.EmployeeId, personResult));
                        if (personResult.Retryable)
                        {
                            queuedDetails.Add(QueueRetry(device, command.EmployeeId, "SyncPerson", PersonPayload(command), null, personResult.Message, context));
                        }

                        continue;
                    }

                    if (command.HasFace)
                    {
                        var faceResult = ExecuteUploadFaceTask(device, command, context);
                        var faceDetail = ToDeviceResult(device, faceResult, "UploadFace");
                        if (!faceResult.Success && faceResult.Retryable)
                        {
                            faceDetail.Queued = true;
                        }

                        employeeResults[command.EmployeeId].DeviceResults.Add(faceDetail);
                        if (faceResult.Success)
                        {
                            facesUploaded++;
                        }
                        else
                        {
                            deviceErrors.Add(ToDeviceError(device, command.EmployeeId, faceResult));
                            if (faceResult.Retryable)
                            {
                                queuedDetails.Add(QueueRetry(device, command.EmployeeId, "UploadFace", FacePayload(command), null, faceResult.Message, context));
                            }
                        }
                    }
                }

                foreach (var device in offlineDevices)
                {
                    queuedDetails.Add(QueueRetry(device, command.EmployeeId, "SyncPerson", PersonPayload(command), null, "设备离线，已生成补偿意图。", context));
                    employeeResults[command.EmployeeId].DeviceResults.Add(ToQueuedDeviceResult(device, "SyncPerson"));
                    if (command.HasFace)
                    {
                        queuedDetails.Add(QueueRetry(device, command.EmployeeId, "UploadFace", FacePayload(command), null, "设备离线，已生成补偿意图。", context));
                        employeeResults[command.EmployeeId].DeviceResults.Add(ToQueuedDeviceResult(device, "UploadFace"));
                    }
                }

                if (employeeResults[command.EmployeeId].DeviceResults.Any(item => item.Success && item.Operation == "SyncPerson"))
                {
                    TryUpdateUser(dbErrors, () => userSyncWriter.MarkPersonSynced(command.EmployeeId), command.EmployeeId, "MarkPersonSynced");
                }
            }

            var succeededEmployees = employeeResults.Values.Count(item => item.DeviceResults.Any(device => device.Success && device.Operation == "SyncPerson"));
            var failedEmployees = employeeResults.Values.Count(item => item.DeviceResults.Any(device => !device.Success && !device.Queued));
            var queuedEmployees = employeeResults.Values.Count(item => item.DeviceResults.Any(device => device.Queued));
            var code = DetermineCode(parsed.Items.Count, succeededEmployees, failedEmployees, queuedEmployees);

            return JsonResponse.Create(context.RequestId, code != "FAILED", code, BuildMessage(code), new Dictionary<string, object>
            {
                ["total"] = parsed.Items.Count,
                ["succeeded"] = succeededEmployees,
                ["failed"] = failedEmployees,
                ["queued"] = queuedEmployees,
                ["facesUploaded"] = facesUploaded,
                ["targetDevices"] = onlineDevices.Count + offlineDevices.Count,
                ["queuedDetails"] = queuedDetails,
                ["items"] = employeeResults.Values.Select(item => item.ToDictionary()).ToList(),
                ["deviceErrors"] = deviceErrors,
                ["dbErrors"] = dbErrors
            });
        }

        public System.Threading.Tasks.Task<string> SyncPersonsAsync(string requestJson, GrpcRequestContext context = null)
        {
            context = EnsureContext(context);
            return System.Threading.Tasks.Task.Run(() => SyncPersons(requestJson, context), context.CancellationToken);
        }

        public string DeleteFaces(string requestJson, GrpcRequestContext context = null)
        {
            return ExecuteUnary("DeleteFaces", requestJson, context, DeleteFacesCore);
        }

        private string DeleteFacesCore(string requestJson, GrpcRequestContext context)
        {
            return DeleteEmployees(requestJson, context, "DeleteFace");
        }

        public System.Threading.Tasks.Task<string> DeleteFacesAsync(string requestJson, GrpcRequestContext context = null)
        {
            context = EnsureContext(context);
            return System.Threading.Tasks.Task.Run(() => DeleteFaces(requestJson, context), context.CancellationToken);
        }

        public string DeletePersons(string requestJson, GrpcRequestContext context = null)
        {
            return ExecuteUnary("DeletePersons", requestJson, context, DeletePersonsCore);
        }

        private string DeletePersonsCore(string requestJson, GrpcRequestContext context)
        {
            return DeleteEmployees(requestJson, context, "DeletePerson");
        }

        public System.Threading.Tasks.Task<string> DeletePersonsAsync(string requestJson, GrpcRequestContext context = null)
        {
            context = EnsureContext(context);
            return System.Threading.Tasks.Task.Run(() => DeletePersons(requestJson, context), context.CancellationToken);
        }

        public string GetFaces(string requestJson, GrpcRequestContext context = null)
        {
            return ExecuteUnary("GetFaces", requestJson, context, GetFacesCore);
        }

        private string GetFacesCore(string requestJson, GrpcRequestContext context)
        {
            context = EnsureContext(context);
            ParseResult<EmployeeCommand> parsed;
            try
            {
                parsed = ParseEmployeeCommands(requestJson);
            }
            catch (RequestValidationException ex)
            {
                return Error(context, ex.Code, ex.Message);
            }
            catch (Exception ex)
            {
                return Error(context, "INVALID_ARGUMENT", ex.Message);
            }

            if (!parsed.Success)
            {
                return Error(context, parsed.Code, parsed.Message);
            }

            var onlineDevices = GetAcsTargetDevices().Where(item => item.Enabled && item.IsConnected && item.SdkUserId.HasValue).ToList();
            var results = CreateEmployeeResults(parsed.Items.Select(item => item.EmployeeId));
            var failedEmployees = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var command in parsed.Items)
            {
                foreach (var device in onlineDevices)
                {
                    var result = ExecuteQueryFaceTask(device, command.EmployeeId, context);
                    var deviceResult = ToDeviceResult(device, result, "QueryFace");
                    var query = result.Data as QueryFaceResponse;
                    if (query != null)
                    {
                        deviceResult.FaceCount = query.TotalCount;
                        deviceResult.Exists = query.Exists;
                        deviceResult.RawResponse = query.RawResponse;
                        deviceResult.Faces = query.Faces.Select(ToFaceDictionary).Cast<object>().ToList();
                    }

                    results[command.EmployeeId].DeviceResults.Add(deviceResult);
                    if (!result.Success)
                    {
                        failedEmployees.Add(command.EmployeeId);
                    }
                }
            }

            var succeeded = results.Values.Count(item => item.DeviceResults.Any(device => device.Success));
            var failed = failedEmployees.Count;
            var code = failed > 0 && succeeded > 0 ? "PARTIAL_SUCCESS" : failed > 0 ? "FAILED" : "OK";
            return JsonResponse.Create(context.RequestId, code != "FAILED", code, BuildMessage(code), new Dictionary<string, object>
            {
                ["total"] = parsed.Items.Count,
                ["succeeded"] = succeeded,
                ["failed"] = failed,
                ["queued"] = 0,
                ["targetDevices"] = onlineDevices.Count,
                ["items"] = results.Values.Select(item => item.ToDictionary()).ToList()
            });
        }

        public System.Threading.Tasks.Task<string> GetFacesAsync(string requestJson, GrpcRequestContext context = null)
        {
            context = EnsureContext(context);
            return System.Threading.Tasks.Task.Run(() => GetFaces(requestJson, context), context.CancellationToken);
        }

        public IEnumerable<string> CaptureFaceStream(string requestJson, GrpcRequestContext context = null)
        {
            return ExecuteStreaming("CaptureFaceStream", requestJson, context, CaptureFaceStreamCore);
        }

        private IReadOnlyList<string> CaptureFaceStreamCore(string requestJson, GrpcRequestContext context)
        {
            context = EnsureContext(context);
            var frames = new List<string>();
            string employeeId;
            try
            {
                employeeId = ParseEmployeeFromObject(requestJson);
            }
            catch (RequestValidationException ex)
            {
                frames.Add(Error(context, ex.Code, ex.Message));
                return frames;
            }
            catch (Exception ex)
            {
                frames.Add(Error(context, "INVALID_ARGUMENT", ex.Message));
                return frames;
            }

            var taskId = Guid.NewGuid().ToString("N");
            enrollmentStore.Start(taskId, employeeId);
            var candidates = GetFaceCaptureTargetDevices();
            DeviceRuntimeSnapshot device = null;
            var failCode = "DEVICE_ERROR";
            var failMessage = "没有可用的人脸采集设备。";
            if (defaultFaceCaptureDeviceId.HasValue)
            {
                // 配置了默认采集设备：固定使用它，离线时严格失败、不回退到其他设备。
                var configured = candidates.FirstOrDefault(item => item.DeviceId == defaultFaceCaptureDeviceId.Value);
                if (configured == null)
                {
                    failMessage = "默认人脸采集设备(deviceId=" + defaultFaceCaptureDeviceId.Value + ")未注册或不属于人脸采集设备。";
                }
                else if (!configured.Enabled ||
                    !configured.IsConnected ||
                    !configured.SdkUserId.HasValue)
                {
                    failMessage = "默认人脸采集设备(deviceId=" + configured.DeviceId + ")当前不可用。";
                }
                else
                {
                    device = configured;
                }
            }
            else
            {
                // 未配置默认设备：维持"按类型取第一个在线设备"的旧行为。
                device = candidates.FirstOrDefault(item =>
                    item.Enabled &&
                    item.IsConnected &&
                    item.SdkUserId.HasValue);
            }

            if (device == null)
            {
                enrollmentStore.Fail(taskId, failCode, failMessage);
                frames.Add(JsonResponse.Create(context.RequestId, false, failCode, failMessage, new Dictionary<string, object>
                {
                    ["taskId"] = taskId,
                    ["employeeId"] = employeeId,
                    ["frameIndex"] = 0,
                    ["faceImageBase64"] = string.Empty,
                    ["faceImageFormat"] = "jpg",
                    ["qualityScore"] = 0,
                    ["recommend"] = false
                }, new List<string> { failMessage }));
                return frames;
            }

            var result = ExecuteCaptureFaceTask(device, employeeId, context);
            if (!result.Success)
            {
                enrollmentStore.Fail(taskId, result.Code, result.Message);
                frames.Add(JsonResponse.Create(context.RequestId, false, result.Code, result.Message, new Dictionary<string, object>
                {
                    ["taskId"] = taskId,
                    ["employeeId"] = employeeId,
                    ["frameIndex"] = 0,
                    ["faceImageBase64"] = string.Empty,
                    ["faceImageFormat"] = "jpg",
                    ["qualityScore"] = 0,
                    ["recommend"] = false
                }, new List<string> { result.Message }));
                return frames;
            }

            var capture = result.Data as FaceCaptureResult;
            var imageBytes = capture == null ? new byte[0] : capture.ImageBytes ?? new byte[0];
            if (imageBytes.Length > MaxFaceBytes)
            {
                enrollmentStore.Fail(taskId, "FACE_TOO_LARGE", "采集图片超过 200KB。");
                frames.Add(JsonResponse.Create(context.RequestId, false, "FACE_TOO_LARGE", "采集图片超过 200KB。", new Dictionary<string, object>
                {
                    ["taskId"] = taskId,
                    ["employeeId"] = employeeId,
                    ["frameIndex"] = 0,
                    ["faceImageBase64"] = string.Empty,
                    ["faceImageFormat"] = "jpg",
                    ["qualityScore"] = 0,
                    ["recommend"] = false
                }, new List<string> { "采集图片超过 200KB。" }));
                return frames;
            }

            enrollmentStore.Succeed(taskId, "采集成功。");
            frames.Add(JsonResponse.Create(context.RequestId, true, "OK", "采集成功。", new Dictionary<string, object>
            {
                ["taskId"] = taskId,
                ["employeeId"] = employeeId,
                ["frameIndex"] = 1,
                ["faceImageBase64"] = Convert.ToBase64String(imageBytes),
                ["faceImageFormat"] = ContentTypeToFormat(capture == null ? null : capture.ContentType),
                ["qualityScore"] = capture == null ? 0 : capture.QualityScore,
                ["recommend"] = capture == null || capture.FaceDetected
            }));
            return frames;
        }

        public System.Threading.Tasks.Task<IReadOnlyList<string>> CaptureFaceStreamAsync(string requestJson, GrpcRequestContext context = null)
        {
            context = EnsureContext(context);
            return System.Threading.Tasks.Task.Run<IReadOnlyList<string>>(() => CaptureFaceStream(requestJson, context).ToList(), context.CancellationToken);
        }

        public string GetEnrollmentStatus(string requestJson, GrpcRequestContext context = null)
        {
            return ExecuteUnary("GetEnrollmentStatus", requestJson, context, GetEnrollmentStatusCore);
        }

        private string GetEnrollmentStatusCore(string requestJson, GrpcRequestContext context)
        {
            context = EnsureContext(context);
            try
            {
                var root = JsonRequestReader.ParseAny(requestJson);
                var request = JsonRequestReader.AsObject(root, "GetEnrollmentStatus 请求必须是对象。");
                var taskId = JsonRequestReader.GetString(request, "taskId", "task_id");
                var employeeId = JsonRequestReader.GetString(request, "employee_id", "employeeId", "employee_no", "employeeNo");
                var record = !string.IsNullOrWhiteSpace(taskId)
                    ? enrollmentStore.GetByTaskId(taskId)
                    : enrollmentStore.GetLatestByEmployeeId(employeeId);
                if (record == null)
                {
                    return Error(context, "NOT_FOUND", "采集任务不存在。");
                }

                return JsonResponse.Create(context.RequestId, true, "OK", "查询成功。", record.ToResponseFields());
            }
            catch (Exception ex)
            {
                return Error(context, "INVALID_ARGUMENT", ex.Message);
            }
        }

        public System.Threading.Tasks.Task<string> GetEnrollmentStatusAsync(string requestJson, GrpcRequestContext context = null)
        {
            context = EnsureContext(context);
            return System.Threading.Tasks.Task.Run(() => GetEnrollmentStatus(requestJson, context), context.CancellationToken);
        }

        private string ExecuteUnary(string methodName, string requestJson, GrpcRequestContext context, Func<string, GrpcRequestContext, string> handler)
        {
            context = EnsureContext(context);
            return grpcLogger == null
                ? handler(requestJson, context)
                : grpcLogger.ExecuteUnary(ServiceName, methodName, requestJson, context, handler);
        }

        private IReadOnlyList<string> ExecuteStreaming(string methodName, string requestJson, GrpcRequestContext context, Func<string, GrpcRequestContext, IReadOnlyList<string>> handler)
        {
            context = EnsureContext(context);
            return grpcLogger == null
                ? handler(requestJson, context)
                : grpcLogger.ExecuteStreaming(ServiceName, methodName, requestJson, context, handler);
        }

        private string DeleteEmployees(string requestJson, GrpcRequestContext context, string operation)
        {
            context = EnsureContext(context);
            ParseResult<EmployeeCommand> parsed;
            try
            {
                parsed = ParseEmployeeCommands(requestJson);
            }
            catch (RequestValidationException ex)
            {
                return Error(context, ex.Code, ex.Message);
            }
            catch (Exception ex)
            {
                return Error(context, "INVALID_ARGUMENT", ex.Message);
            }

            if (!parsed.Success)
            {
                return Error(context, parsed.Code, parsed.Message);
            }

            var devices = GetAcsTargetDevices();
            var onlineDevices = devices.Where(item => item.Enabled && item.IsConnected && item.SdkUserId.HasValue).ToList();
            var offlineDevices = devices.Where(item => item.Enabled && (!item.IsConnected || !item.SdkUserId.HasValue)).ToList();
            var employeeResults = CreateEmployeeResults(parsed.Items.Select(item => item.EmployeeId));
            var queuedDetails = new List<object>();
            var deviceErrors = new List<object>();
            var dbErrors = new List<object>();

            foreach (var command in parsed.Items)
            {
                foreach (var device in onlineDevices)
                {
                    var result = operation == "DeleteFace"
                        ? ExecuteDeleteFaceTask(device, command.EmployeeId, context)
                        : ExecuteDeletePersonTask(device, command.EmployeeId, context);
                    var detail = ToDeviceResult(device, result, operation);
                    if (!result.Success && result.Retryable)
                    {
                        detail.Queued = true;
                    }

                    employeeResults[command.EmployeeId].DeviceResults.Add(detail);
                    if (!result.Success)
                    {
                        deviceErrors.Add(ToDeviceError(device, command.EmployeeId, result));
                        if (result.Retryable)
                        {
                            queuedDetails.Add(QueueRetry(device, command.EmployeeId, operation, EmployeePayload(command.EmployeeId), null, result.Message, context));
                        }
                    }
                }

                foreach (var device in offlineDevices)
                {
                    queuedDetails.Add(QueueRetry(device, command.EmployeeId, operation, EmployeePayload(command.EmployeeId), null, "设备离线，已生成补偿意图。", context));
                    employeeResults[command.EmployeeId].DeviceResults.Add(ToQueuedDeviceResult(device, operation));
                }

                if (operation == "DeletePerson" && IsEmployeeOperationComplete(employeeResults[command.EmployeeId]))
                {
                    TryUpdateUser(dbErrors, () => userSyncWriter.MarkPersonDeleted(command.EmployeeId), command.EmployeeId, "MarkPersonDeleted");
                }
            }

            var succeededEmployees = employeeResults.Values.Count(IsEmployeeOperationComplete);
            var failedEmployees = employeeResults.Values.Count(item => item.DeviceResults.Any(device => !device.Success && !device.Queued));
            var queuedEmployees = employeeResults.Values.Count(item => item.DeviceResults.Any(device => device.Queued));
            var code = DetermineCode(parsed.Items.Count, succeededEmployees, failedEmployees, queuedEmployees);
            return JsonResponse.Create(context.RequestId, code != "FAILED", code, BuildMessage(code), new Dictionary<string, object>
            {
                ["total"] = parsed.Items.Count,
                ["succeeded"] = succeededEmployees,
                ["failed"] = failedEmployees,
                ["queued"] = queuedEmployees,
                ["targetDevices"] = onlineDevices.Count + offlineDevices.Count,
                ["queuedDetails"] = queuedDetails,
                ["items"] = employeeResults.Values.Select(item => item.ToDictionary()).ToList(),
                ["deviceErrors"] = deviceErrors,
                ["dbErrors"] = dbErrors
            });
        }

        private DeviceTaskResult ExecutePermissionTask(DeviceRuntimeSnapshot device, PermissionCommand command, GrpcRequestContext context)
        {
            return SubmitGatewayTask(device, DeviceTaskType.SyncPermission, "SyncPermission", context, async taskContext =>
            {
                var started = DateTime.Now;
                var snapshot = taskContext.SnapshotBeforeExecution;
                if (snapshot == null || !snapshot.SdkUserId.HasValue)
                {
                    var offline = DeviceTaskResult.FromTask(taskContext.Task, false, "DEVICE_OFFLINE", "设备未在线。", DeviceConnectionStatus.Offline, started, DateTime.Now);
                    offline.Retryable = true;
                    return offline;
                }

                await gateway.UpsertPersonAsync(new UpsertPersonRequest
                {
                    UserId = snapshot.SdkUserId.Value,
                    Person = command.ToPermissionPersonInfo(snapshot.Description),
                    ProvisioningMode = PersonProvisioningMode.Permission
                }, taskContext.CancellationToken).ConfigureAwait(false);
                return DeviceTaskResult.FromTask(taskContext.Task, true, "OK", "权限同步成功。", snapshot.Status, started, DateTime.Now);
            });
        }

        private DeviceTaskResult ExecutePersonTask(DeviceRuntimeSnapshot device, PersonSyncCommand command, GrpcRequestContext context)
        {
            return SubmitGatewayTask(device, DeviceTaskType.SyncPerson, "SyncPerson", context, async taskContext =>
            {
                var started = DateTime.Now;
                var snapshot = taskContext.SnapshotBeforeExecution;
                if (snapshot == null || !snapshot.SdkUserId.HasValue)
                {
                    var offline = DeviceTaskResult.FromTask(taskContext.Task, false, "DEVICE_OFFLINE", "设备未在线。", DeviceConnectionStatus.Offline, started, DateTime.Now);
                    offline.Retryable = true;
                    return offline;
                }

                await gateway.UpsertPersonAsync(new UpsertPersonRequest
                {
                    UserId = snapshot.SdkUserId.Value,
                    Person = command.ToPersonInfo()
                }, taskContext.CancellationToken).ConfigureAwait(false);
                return DeviceTaskResult.FromTask(taskContext.Task, true, "OK", "人员同步成功。", snapshot.Status, started, DateTime.Now);
            });
        }

        private DeviceTaskResult ExecuteUploadFaceTask(DeviceRuntimeSnapshot device, PersonSyncCommand command, GrpcRequestContext context)
        {
            return SubmitGatewayTask(device, DeviceTaskType.UploadFace, "UploadFace", context, async taskContext =>
            {
                var started = DateTime.Now;
                var snapshot = taskContext.SnapshotBeforeExecution;
                if (snapshot == null || !snapshot.SdkUserId.HasValue)
                {
                    var offline = DeviceTaskResult.FromTask(taskContext.Task, false, "DEVICE_OFFLINE", "设备未在线。", DeviceConnectionStatus.Offline, started, DateTime.Now);
                    offline.Retryable = true;
                    return offline;
                }

                await gateway.UploadFaceAsync(new UploadFaceRequest
                {
                    UserId = snapshot.SdkUserId.Value,
                    MaxImageBytes = MaxFaceBytes,
                    Face = command.ToFaceInfo()
                }, taskContext.CancellationToken).ConfigureAwait(false);
                return DeviceTaskResult.FromTask(taskContext.Task, true, "OK", "人脸下发成功。", snapshot.Status, started, DateTime.Now);
            });
        }

        private DeviceTaskResult ExecuteDeleteFaceTask(DeviceRuntimeSnapshot device, string employeeId, GrpcRequestContext context)
        {
            return SubmitGatewayTask(device, DeviceTaskType.DeleteFace, "DeleteFace", context, async taskContext =>
            {
                var started = DateTime.Now;
                var snapshot = taskContext.SnapshotBeforeExecution;
                if (snapshot == null || !snapshot.SdkUserId.HasValue)
                {
                    var offline = DeviceTaskResult.FromTask(taskContext.Task, false, "DEVICE_OFFLINE", "设备未在线。", DeviceConnectionStatus.Offline, started, DateTime.Now);
                    offline.Retryable = true;
                    return offline;
                }

                await gateway.DeleteFaceAsync(new DeleteFaceRequest { UserId = snapshot.SdkUserId.Value, EmployeeId = employeeId }, taskContext.CancellationToken).ConfigureAwait(false);
                return DeviceTaskResult.FromTask(taskContext.Task, true, "OK", "删除人脸成功。", snapshot.Status, started, DateTime.Now);
            });
        }

        private DeviceTaskResult ExecuteDeletePersonTask(DeviceRuntimeSnapshot device, string employeeId, GrpcRequestContext context)
        {
            return SubmitGatewayTask(device, DeviceTaskType.DeletePerson, "DeletePerson", context, async taskContext =>
            {
                var started = DateTime.Now;
                var snapshot = taskContext.SnapshotBeforeExecution;
                if (snapshot == null || !snapshot.SdkUserId.HasValue)
                {
                    var offline = DeviceTaskResult.FromTask(taskContext.Task, false, "DEVICE_OFFLINE", "设备未在线。", DeviceConnectionStatus.Offline, started, DateTime.Now);
                    offline.Retryable = true;
                    return offline;
                }

                await DeleteFaceBeforePersonBestEffortAsync(snapshot.SdkUserId.Value, employeeId, taskContext.CancellationToken).ConfigureAwait(false);
                await gateway.DeletePersonAsync(new DeletePersonRequest { UserId = snapshot.SdkUserId.Value, EmployeeId = employeeId }, taskContext.CancellationToken).ConfigureAwait(false);
                return DeviceTaskResult.FromTask(taskContext.Task, true, "OK", "删除人员成功。", snapshot.Status, started, DateTime.Now);
            });
        }

        private async System.Threading.Tasks.Task DeleteFaceBeforePersonBestEffortAsync(int userId, string employeeId, System.Threading.CancellationToken cancellationToken)
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
                logger?.Warn("PermissionSync", "删除人员前删除人脸失败，忽略并继续删除人员。", CreateBestEffortDeleteFaceLogFields(userId, employeeId, "DeleteFaceBeforePerson", ex));
            }
        }

        private static LogFields CreateBestEffortDeleteFaceLogFields(int userId, string employeeId, string operationName, Exception ex)
        {
            var fields = new LogFields
            {
                EmployeeId = employeeId,
                OperationName = operationName,
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

        private DeviceTaskResult ExecuteQueryFaceTask(DeviceRuntimeSnapshot device, string employeeId, GrpcRequestContext context)
        {
            return SubmitGatewayTask(device, DeviceTaskType.GetFace, "QueryFace", context, async taskContext =>
            {
                var started = DateTime.Now;
                var snapshot = taskContext.SnapshotBeforeExecution;
                if (snapshot == null || !snapshot.SdkUserId.HasValue)
                {
                    return DeviceTaskResult.FromTask(taskContext.Task, false, "DEVICE_OFFLINE", "设备未在线。", DeviceConnectionStatus.Offline, started, DateTime.Now);
                }

                var query = await gateway.QueryFaceAsync(new QueryFaceRequest { UserId = snapshot.SdkUserId.Value, EmployeeId = employeeId }, taskContext.CancellationToken).ConfigureAwait(false);
                var result = DeviceTaskResult.FromTask(taskContext.Task, true, "OK", "查询人脸成功。", snapshot.Status, started, DateTime.Now);
                result.Data = query;
                return result;
            });
        }

        private DeviceTaskResult ExecuteCaptureFaceTask(DeviceRuntimeSnapshot device, string employeeId, GrpcRequestContext context)
        {
            return SubmitGatewayTask(device, DeviceTaskType.CaptureFace, "CaptureFace", context, async taskContext =>
            {
                var started = DateTime.Now;
                var snapshot = taskContext.SnapshotBeforeExecution;
                if (snapshot == null || !snapshot.SdkUserId.HasValue)
                {
                    var offline = DeviceTaskResult.FromTask(taskContext.Task, false, "DEVICE_OFFLINE", "设备未在线。", DeviceConnectionStatus.Offline, started, DateTime.Now);
                    offline.Retryable = true;
                    return offline;
                }

                var capture = await gateway.CaptureFaceAsync(new CaptureRequest { UserId = snapshot.SdkUserId.Value }, taskContext.CancellationToken).ConfigureAwait(false);
                capture.EmployeeId = employeeId;
                var result = DeviceTaskResult.FromTask(taskContext.Task, true, "OK", "采集成功。", snapshot.Status, started, DateTime.Now);
                result.Data = capture;
                return result;
            });
        }

        private DeviceTaskResult SubmitGatewayTask(DeviceRuntimeSnapshot device, DeviceTaskType taskType, string operationName, GrpcRequestContext context, Func<DeviceTaskContext, System.Threading.Tasks.Task<DeviceTaskResult>> executeAsync)
        {
            var task = new DeviceSdkTask(device.DeviceId, taskType, operationName, async taskContext =>
            {
                try
                {
                    return await executeAsync(taskContext).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return MapGatewayException(taskContext.Task, ex, taskContext.SnapshotBeforeExecution);
                }
            });
            task.RequiresOnline = true;
            task.Priority = DeviceTaskPriority.Normal;
            task.WaitMode = DeviceTaskWaitMode.WaitForResult;
            task.RequestId = context.RequestId ?? string.Empty;
            task.CorrelationId = context.CorrelationId ?? context.RequestId ?? string.Empty;
            return dispatcher.SubmitAndWaitAsync(task, context.CancellationToken).GetAwaiter().GetResult();
        }

        private DeviceTaskResult MapGatewayException(DeviceSdkTask task, Exception ex, DeviceRuntimeSnapshot snapshot)
        {
            var started = task.StartedAt ?? DateTime.Now;
            var status = snapshot == null ? DeviceConnectionStatus.Unknown : snapshot.Status;
            var gatewayEx = ex as DeviceGatewayException;
            if (gatewayEx != null)
            {
                var result = DeviceTaskResult.FromTask(task, false, gatewayEx.Error.Code == 23 ? "DEVICE_UNSUPPORTED" : "SDK_ERROR", gatewayEx.Error.Message, status, started, DateTime.Now);
                result.SdkErrorCode = gatewayEx.Error.Code;
                result.Retryable = IsRetryableSdkError(gatewayEx.Error.Code);
                return result;
            }

            if (ex is TimeoutException || ex is OperationCanceledException)
            {
                var timeout = DeviceTaskResult.FromTask(task, false, "TIMEOUT", ex.Message, status, started, DateTime.Now);
                timeout.Retryable = true;
                return timeout;
            }

            return DeviceTaskResult.FromTask(task, false, "DEVICE_ERROR", ex == null ? "设备操作失败。" : ex.Message, status, started, DateTime.Now);
        }

        private object QueueRetry(DeviceRuntimeSnapshot device, string employeeId, string operation, IDictionary<string, object> payload, int? permissionLevel, string message, GrpcRequestContext context = null)
        {
            var payloadJson = payload == null ? null : JsonRequestReader.Serialize(payload);
            var intent = new DeviceOperationRetryIntent
            {
                DeviceId = device.DeviceId,
                EmployeeId = employeeId,
                Operation = operation,
                PermissionLevel = permissionLevel,
                PermissionPayloadJson = string.Equals(operation, "SyncPermission", StringComparison.OrdinalIgnoreCase) && payload != null
                    ? payloadJson
                    : null,
                PayloadJson = payloadJson,
                RequestId = context == null ? null : context.RequestId,
                LastError = message,
                CreatedAt = DateTime.Now,
                NextRetryAt = DateTime.Now
            };
            var written = retryWriter.UpsertIntent(intent);
            var fields = new LogFields
            {
                RequestId = intent.RequestId,
                DeviceId = intent.DeviceId,
                EmployeeId = intent.EmployeeId,
                OperationName = operation,
                ErrorCode = written.Code
            };
            fields.Extra["success"] = written.Success.ToString();
            fields.Extra["permissionLevel"] = permissionLevel.HasValue ? permissionLevel.Value.ToString() : string.Empty;
            fields.Extra["message"] = message ?? string.Empty;
            fields.Extra["writeMessage"] = written.Message ?? string.Empty;
            logger?.Info("DeviceOperationRetry", "Retry intent queued from gRPC.", fields);
            return intent.ToDetail(written.Code, written.Success ? message : written.Message);
        }

        private static ParseResult<PermissionCommand> ParsePermissionCommands(string requestJson)
        {
            var root = JsonRequestReader.ParseAny(requestJson);
            var items = JsonRequestReader.ReadItems(root, "items", "records");
            ValidateBatch(items.Count);
            var commands = new List<PermissionCommand>();
            foreach (var item in items)
            {
                var values = JsonRequestReader.AsObject(item);
                var employeeId = TrimRequired(JsonRequestReader.GetString(values, "employee_id", "employeeId", "employee_no", "employeeNo"), "employee_id");
                var permission = JsonRequestReader.GetInt(values, "permission_code", "permissionCode", "permission_level", "permissionLevel");
                var name = JsonRequestReader.GetString(values, "name", "full_name", "fullName", "name_alias");
                if (!permission.HasValue)
                {
                    throw new RequestValidationException("INVALID_ARGUMENT", "permission_code 必须可解析为整数。");
                }

                commands.RemoveAll(command => string.Equals(command.EmployeeId, employeeId, StringComparison.OrdinalIgnoreCase));
                commands.Add(new PermissionCommand { EmployeeId = employeeId, Name = name, PermissionCode = permission.Value });
            }

            if (commands.Count == 0)
            {
                throw new RequestValidationException("INVALID_ARGUMENT", "请求至少包含一条记录。");
            }

            return ParseResult<PermissionCommand>.Ok(commands);
        }

        private static ParseResult<PersonSyncCommand> ParsePersonCommands(string requestJson)
        {
            var root = JsonRequestReader.ParseAny(requestJson);
            var items = JsonRequestReader.ReadItems(root, "people", "items", "records", "data");
            ValidateBatch(items.Count);
            var commands = new List<PersonSyncCommand>();
            foreach (var item in items)
            {
                var values = JsonRequestReader.AsObject(item);
                var employeeId = TrimRequired(JsonRequestReader.GetString(values, "employee_id", "employeeId", "employee_no", "employeeNo"), "employee_id");
                var validFrom = JsonRequestReader.GetDateTime(values, "valid_from", "validFrom");
                var validTo = JsonRequestReader.GetDateTime(values, "valid_to", "validTo");
                if (validFrom.HasValue && validTo.HasValue && validFrom.Value > validTo.Value)
                {
                    throw new RequestValidationException("INVALID_ARGUMENT", "valid_from 不能晚于 valid_to。");
                }

                var faceBase64 = JsonRequestReader.GetString(values, "face_image_base64", "faceImageBase64", "face_base64", "faceBase64", "face_image");
                var faceBytes = DecodeFaceBytes(faceBase64);
                commands.Add(new PersonSyncCommand
                {
                    EmployeeId = employeeId,
                    Name = JsonRequestReader.GetString(values, "name", "full_name", "fullName"),
                    Gender = JsonRequestReader.GetString(values, "gender", "sex"),
                    Enabled = JsonRequestReader.GetBool(values, "enabled", "active", "is_active") ?? true,
                    ValidFrom = validFrom,
                    ValidTo = validTo,
                    FaceImageBase64 = NormalizeBase64(faceBase64),
                    FaceImageBytes = faceBytes,
                    FaceImageFormat = JsonRequestReader.GetString(values, "face_image_format", "faceImageFormat") ?? InferFormat(faceBase64)
                });
            }

            if (commands.Count == 0)
            {
                throw new RequestValidationException("INVALID_ARGUMENT", "请求至少包含一条记录。");
            }

            return ParseResult<PersonSyncCommand>.Ok(commands);
        }

        private static ParseResult<EmployeeCommand> ParseEmployeeCommands(string requestJson)
        {
            var root = JsonRequestReader.ParseAny(requestJson);
            var items = JsonRequestReader.ReadItems(root, "items", "records");
            ValidateBatch(items.Count);
            var commands = new List<EmployeeCommand>();
            foreach (var item in items)
            {
                string employeeId;
                if (item is string)
                {
                    employeeId = TrimRequired(Convert.ToString(item), "employee_id");
                }
                else
                {
                    var values = JsonRequestReader.AsObject(item);
                    employeeId = TrimRequired(JsonRequestReader.GetString(values, "employee_id", "employeeId", "employee_no", "employeeNo"), "employee_id");
                }

                if (!commands.Any(command => string.Equals(command.EmployeeId, employeeId, StringComparison.OrdinalIgnoreCase)))
                {
                    commands.Add(new EmployeeCommand { EmployeeId = employeeId });
                }
            }

            if (commands.Count == 0)
            {
                throw new RequestValidationException("INVALID_ARGUMENT", "请求至少包含一条员工编号。");
            }

            return ParseResult<EmployeeCommand>.Ok(commands);
        }

        private static string ParseEmployeeFromObject(string requestJson)
        {
            var root = JsonRequestReader.ParseAny(requestJson);
            var values = JsonRequestReader.AsObject(root);
            return TrimRequired(JsonRequestReader.GetString(values, "employee_id", "employeeId", "employee_no", "employeeNo"), "employee_id");
        }

        private IReadOnlyList<DeviceRuntimeSnapshot> GetTargetDevices()
        {
            return registry.GetAllSnapshots();
        }

        private IReadOnlyList<DeviceRuntimeSnapshot> GetAcsTargetDevices()
        {
            return GetTargetDevices()
                .Where(item => HasDeclaredType(item, DeviceType.Acs))
                .ToList();
        }

        private IReadOnlyList<DeviceRuntimeSnapshot> GetFaceCaptureTargetDevices()
        {
            return GetTargetDevices()
                .Where(item => HasDeclaredType(item, DeviceType.FaceCapture))
                .ToList();
        }

        private static bool HasDeclaredType(DeviceRuntimeSnapshot snapshot, DeviceType requiredType)
        {
            return snapshot == null ||
                snapshot.Types == null ||
                snapshot.Types.Count == 0 ||
                snapshot.Types.Contains(requiredType);
        }

        private static void ValidateBatch(int count)
        {
            if (count > MaxBatchSize)
            {
                throw new RequestValidationException("BATCH_TOO_LARGE", "批量数量不能超过 500。");
            }
        }

        private static string TrimRequired(string value, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new RequestValidationException("INVALID_ARGUMENT", fieldName + " 不能为空。");
            }

            return value.Trim();
        }

        private static byte[] DecodeFaceBytes(string value)
        {
            var normalized = NormalizeBase64(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return new byte[0];
            }

            try
            {
                var bytes = Convert.FromBase64String(normalized);
                if (bytes.Length > MaxFaceBytes)
                {
                    throw new RequestValidationException("FACE_TOO_LARGE", "人脸图片超过 200KB。");
                }

                return bytes;
            }
            catch (RequestValidationException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new RequestValidationException("INVALID_ARGUMENT", "face_image_base64 不是有效 Base64。");
            }
        }

        private static string NormalizeBase64(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var trimmed = value.Trim();
            var comma = trimmed.IndexOf(',');
            if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
            {
                return trimmed.Substring(comma + 1);
            }

            return trimmed;
        }

        private static string InferFormat(string faceBase64)
        {
            if (string.IsNullOrWhiteSpace(faceBase64) || !faceBase64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return "jpg";
            }

            var slash = faceBase64.IndexOf('/');
            var semicolon = faceBase64.IndexOf(';');
            if (slash >= 0 && semicolon > slash)
            {
                return faceBase64.Substring(slash + 1, semicolon - slash - 1);
            }

            return "jpg";
        }

        private static Dictionary<string, EmployeeOperationSummary> CreateEmployeeResults(IEnumerable<string> employeeIds)
        {
            var result = new Dictionary<string, EmployeeOperationSummary>(StringComparer.OrdinalIgnoreCase);
            foreach (var employeeId in employeeIds)
            {
                if (!result.ContainsKey(employeeId))
                {
                    result[employeeId] = new EmployeeOperationSummary { EmployeeId = employeeId };
                }
            }

            return result;
        }

        private static DeviceOperationDetail ToDeviceResult(DeviceRuntimeSnapshot device, DeviceTaskResult result, string operation = null)
        {
            return new DeviceOperationDetail
            {
                DeviceId = device.DeviceId,
                DeviceName = device.DeviceName,
                Operation = operation ?? result.OperationName,
                Success = result.Success,
                Queued = false,
                Code = result.Code,
                Message = result.Message
            };
        }

        private static DeviceOperationDetail ToQueuedDeviceResult(DeviceRuntimeSnapshot device, string operation)
        {
            return new DeviceOperationDetail
            {
                DeviceId = device.DeviceId,
                DeviceName = device.DeviceName,
                Operation = operation,
                Success = false,
                Queued = true,
                Code = "QUEUED",
                Message = "设备离线，已生成补偿意图。"
            };
        }

        private static bool IsEmployeeOperationComplete(EmployeeOperationSummary summary)
        {
            return summary != null &&
                summary.DeviceResults.Count > 0 &&
                summary.DeviceResults.All(item => item.Success && !item.Queued);
        }

        private static IDictionary<string, object> ToDeviceError(DeviceRuntimeSnapshot device, string employeeId, DeviceTaskResult result)
        {
            return new Dictionary<string, object>
            {
                ["deviceId"] = device.DeviceId,
                ["deviceName"] = device.DeviceName,
                ["employeeId"] = employeeId,
                ["operation"] = result.OperationName,
                ["code"] = result.Code,
                ["message"] = result.Message,
                ["sdkErrorCode"] = result.SdkErrorCode
            };
        }

        private static IDictionary<string, object> ToFaceDictionary(FaceInfo face)
        {
            return new Dictionary<string, object>
            {
                ["employeeId"] = face.EmployeeId,
                ["cardNumber"] = face.CardNumber,
                ["faceId"] = face.FaceId,
                ["faceImageBase64"] = !string.IsNullOrWhiteSpace(face.ImageBase64) ? face.ImageBase64 : Convert.ToBase64String(face.ImageBytes ?? new byte[0]),
                ["faceImageFormat"] = face.ImageFormat,
                ["qualityScore"] = face.QualityScore
            };
        }

        private static IDictionary<string, object> PermissionPayload(PermissionCommand command)
        {
            return new Dictionary<string, object>
            {
                ["employee_id"] = command.EmployeeId,
                ["name"] = command.Name,
                ["permission_code"] = command.PermissionCode
            };
        }

        private static IDictionary<string, object> PersonPayload(PersonSyncCommand command)
        {
            return new Dictionary<string, object>
            {
                ["employee_id"] = command.EmployeeId,
                ["name"] = command.Name,
                ["gender"] = command.Gender,
                ["enabled"] = command.Enabled,
                ["valid_from"] = command.ValidFrom.HasValue ? command.ValidFrom.Value.ToString("yyyy-MM-ddTHH:mm:ss") : null,
                ["valid_to"] = command.ValidTo.HasValue ? command.ValidTo.Value.ToString("yyyy-MM-ddTHH:mm:ss") : null
            };
        }

        private static IDictionary<string, object> FacePayload(PersonSyncCommand command)
        {
            return new Dictionary<string, object>
            {
                ["employee_id"] = command.EmployeeId,
                ["face_image_base64"] = command.FaceImageBase64,
                ["face_image_format"] = command.FaceImageFormat
            };
        }

        private static IDictionary<string, object> EmployeePayload(string employeeId)
        {
            return new Dictionary<string, object> { ["employee_id"] = employeeId };
        }

        private static void TryUpdateUser(IList<object> dbErrors, Action action, string employeeId, string operation)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                dbErrors.Add(new Dictionary<string, object>
                {
                    ["employeeId"] = employeeId,
                    ["operation"] = operation,
                    ["code"] = "DB_ERROR",
                    ["message"] = ex.Message
                });
            }
        }

        private static string DetermineCode(int total, int succeeded, int failed, int queued)
        {
            if (queued > 0)
            {
                return "PARTIAL_SUCCESS";
            }

            if (total > 0 && succeeded == 0 && failed > 0)
            {
                return "FAILED";
            }

            if (failed > 0)
            {
                return "PARTIAL_SUCCESS";
            }

            return "OK";
        }

        private static string BuildMessage(string code)
        {
            switch (code)
            {
                case "OK":
                    return "处理成功。";
                case "PARTIAL_SUCCESS":
                    return "部分处理成功，失败或离线项已在明细中返回。";
                case "FAILED":
                    return "处理失败。";
                default:
                    return "处理完成。";
            }
        }

        private static bool ShouldQueueSyncPermissionRetry(DeviceTaskResult result)
        {
            if (result == null || result.Success)
            {
                return false;
            }

            return result.Retryable || IsSyncPermissionWaitFailure(result.Code);
        }

        private static bool IsSyncPermissionWaitFailure(string code)
        {
            return string.Equals(code, "CANCELLED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "TIMEOUT", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRetryableSdkError(int code)
        {
            return code == 7 || code == 41 || code == 43 || code == 52 || code == 408 || code == 500;
        }

        private static string ContentTypeToFormat(string contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType))
            {
                return "jpg";
            }

            if (contentType.IndexOf("png", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "png";
            }

            if (contentType.IndexOf("webp", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "webp";
            }

            return "jpg";
        }

        private static GrpcRequestContext EnsureContext(GrpcRequestContext context)
        {
            context = context ?? GrpcRequestContext.Empty();
            if (string.IsNullOrWhiteSpace(context.RequestId))
            {
                context.RequestId = Guid.NewGuid().ToString("N");
            }

            return context;
        }

        private static string Error(GrpcRequestContext context, string code, string message)
        {
            return JsonResponse.Create(context.RequestId, false, code, message, null, new List<string> { message ?? string.Empty });
        }

        private sealed class ParseResult<T>
        {
            public bool Success { get; set; }

            public string Code { get; set; }

            public string Message { get; set; }

            public IList<T> Items { get; set; }

            public static ParseResult<T> Ok(IList<T> items)
            {
                return new ParseResult<T> { Success = true, Code = "OK", Items = items };
            }
        }

        private sealed class RequestValidationException : Exception
        {
            public RequestValidationException(string code, string message)
                : base(message)
            {
                Code = code ?? "INVALID_ARGUMENT";
            }

            public string Code { get; }
        }

        private sealed class PermissionCommand
        {
            public string EmployeeId { get; set; }

            public string Name { get; set; }

            public int PermissionCode { get; set; }

            public PersonInfo ToPermissionPersonInfo(string deviceDescription)
            {
                return new PersonInfo
                {
                    EmployeeId = EmployeeId,
                    Name = string.IsNullOrWhiteSpace(Name) ? EmployeeId : Name.Trim(),
                    Enabled = DevicePermissionAreaPolicy.ShouldEnable(deviceDescription, PermissionCode)
                };
            }
        }

        private sealed class EmployeeCommand
        {
            public string EmployeeId { get; set; }
        }

        private sealed class PersonSyncCommand
        {
            public string EmployeeId { get; set; }

            public string Name { get; set; }

            public string Gender { get; set; }

            public bool Enabled { get; set; }

            public DateTime? ValidFrom { get; set; }

            public DateTime? ValidTo { get; set; }

            public string FaceImageBase64 { get; set; }

            public byte[] FaceImageBytes { get; set; }

            public string FaceImageFormat { get; set; }

            public bool HasFace => FaceImageBytes != null && FaceImageBytes.Length > 0;

            public PersonInfo ToPersonInfo()
            {
                var person = new PersonInfo
                {
                    EmployeeId = EmployeeId,
                    Name = Name,
                    Enabled = Enabled,
                    ValidFrom = ValidFrom,
                    ValidTo = ValidTo
                };
                if (!string.IsNullOrWhiteSpace(Gender))
                {
                    person.Metadata["gender"] = Gender;
                }

                return person;
            }

            public FaceInfo ToFaceInfo()
            {
                return new FaceInfo
                {
                    EmployeeId = EmployeeId,
                    ImageBase64 = FaceImageBase64,
                    ImageBytes = FaceImageBytes ?? new byte[0],
                    ImageFormat = string.IsNullOrWhiteSpace(FaceImageFormat) ? "jpg" : FaceImageFormat
                };
            }
        }

        private sealed class EmployeeOperationSummary
        {
            public string EmployeeId { get; set; }

            public IList<DeviceOperationDetail> DeviceResults { get; } = new List<DeviceOperationDetail>();

            public IDictionary<string, object> ToDictionary()
            {
                var completed = DeviceResults.Count > 0 && DeviceResults.All(item => item.Success && !item.Queued);
                return new Dictionary<string, object>
                {
                    ["employeeId"] = EmployeeId,
                    ["success"] = completed,
                    ["queued"] = DeviceResults.Any(item => item.Queued),
                    ["devices"] = DeviceResults.Select(item => item.ToDictionary()).ToList()
                };
            }
        }

        private sealed class DeviceOperationDetail
        {
            public int DeviceId { get; set; }

            public string DeviceName { get; set; }

            public string Operation { get; set; }

            public bool Success { get; set; }

            public bool Queued { get; set; }

            public string Code { get; set; }

            public string Message { get; set; }

            public int? FaceCount { get; set; }

            public bool? Exists { get; set; }

            public string RawResponse { get; set; }

            public IList<object> Faces { get; set; }

            public IDictionary<string, object> ToDictionary()
            {
                var result = new Dictionary<string, object>
                {
                    ["deviceId"] = DeviceId,
                    ["deviceName"] = DeviceName ?? string.Empty,
                    ["operation"] = Operation ?? string.Empty,
                    ["success"] = Success,
                    ["queued"] = Queued,
                    ["code"] = Code ?? string.Empty,
                    ["message"] = Message ?? string.Empty
                };
                if (FaceCount.HasValue)
                {
                    result["faceCount"] = FaceCount.Value;
                }

                if (Exists.HasValue)
                {
                    result["exists"] = Exists.Value;
                }

                if (RawResponse != null)
                {
                    result["rawResponse"] = RawResponse;
                }

                if (Faces != null)
                {
                    result["faces"] = Faces;
                }

                return result;
            }
        }
    }
}
