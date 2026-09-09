using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ControlDoor.Configuration;
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
        public const string SyncPersonsToDevicesFullName = "/permission.PermissionSyncService/SyncPersonsToDevices";
        public const string SyncFacesToDevicesFullName = "/permission.PermissionSyncService/SyncFacesToDevices";
        public const string DeleteFacesFullName = "/permission.PermissionSyncService/DeleteFaces";
        public const string DeletePersonsFullName = "/permission.PermissionSyncService/DeletePersons";
        public const string GetFacesFullName = "/permission.PermissionSyncService/GetFaces";
        public const string CaptureFaceStreamFullName = "/permission.PermissionSyncService/CaptureFaceStream";
        public const string GetEnrollmentStatusFullName = "/permission.PermissionSyncService/GetEnrollmentStatus";

        private const int MaxBatchSize = 500;
        private const int DefaultMaxFaceImageBytes = 200 * 1024;
        private const int DefaultFaceCaptureTimeoutMs = 10000;
        private const int DefaultMaxBatchFaceBytes = 6 * 1024 * 1024;

        private readonly DeviceRuntimeRegistry registry;
        private readonly DeviceSdkDispatcher dispatcher;
        private readonly IHikvisionGateway gateway;
        private readonly IDeviceOperationRetryWriter retryWriter;
        private readonly IUserSyncStatusWriter userSyncWriter;
        private readonly EnrollmentTaskStore enrollmentStore;
        private readonly ServiceLogger logger;
        private readonly GrpcCallLogger grpcLogger;
        private readonly int? defaultFaceCaptureDeviceId;
        private readonly int maxFaceImageBytes;
        private readonly int maxBatchFaceBytes;
        private readonly int faceCaptureTimeoutMs;

        public PermissionSyncGrpcService(
            DeviceRuntimeRegistry registry,
            DeviceSdkDispatcher dispatcher,
            IHikvisionGateway gateway,
            IDeviceOperationRetryWriter retryWriter = null,
            IUserSyncStatusWriter userSyncWriter = null,
            EnrollmentTaskStore enrollmentStore = null,
            ServiceLogger logger = null,
            int? defaultFaceCaptureDeviceId = null,
            LogOptions logOptions = null,
            FaceEnrollmentOptions faceEnrollment = null)
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
            // 复核 R09：人脸采集配置必须接入实际执行，未配置时保持既有固定值。
            maxFaceImageBytes = faceEnrollment != null && faceEnrollment.MaxFaceImageBytes > 0 ? faceEnrollment.MaxFaceImageBytes : DefaultMaxFaceImageBytes;
            // 复核 K2：单请求人脸图片总量预算，超过时业务层返回 REQUEST_TOO_LARGE，
            // 避免合法批量请求先被 gRPC 传输层 ResourceExhausted 拒绝且无法给出拆批提示。
            maxBatchFaceBytes = faceEnrollment != null && faceEnrollment.MaxBatchFaceBytes > 0 ? faceEnrollment.MaxBatchFaceBytes : DefaultMaxBatchFaceBytes;
            faceCaptureTimeoutMs = faceEnrollment != null && faceEnrollment.CaptureTimeoutSeconds > 0 ? faceEnrollment.CaptureTimeoutSeconds * 1000 : DefaultFaceCaptureTimeoutMs;
        }

        public IReadOnlyList<string> MethodFullNames { get; } = new[]
        {
            SyncPermissionsFullName,
            SyncPersonsFullName,
            SyncPersonsToDevicesFullName,
            SyncFacesToDevicesFullName,
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
                IReadOnlyDictionary<int, DeviceOperationRetryState> prepared = null;
                if (retryWriter is DeviceOperationRetryStore persistentStore)
                {
                    try
                    {
                        prepared = persistentStore.PreparePermissionBatch(onlineDevices.Concat(offlineDevices).Select(device =>
                            CreateRetryIntent(device, command.EmployeeId, PermissionPayload(command), command.PermissionCode, "SyncPermission", context)));
                    }
                    catch (Exception ex)
                    {
                        return Error(context, "DB_ERROR", ex.Message);
                    }
                }
                var permissionOutcomes = ExecuteAcrossDeviceLanes(onlineDevices, device =>
                    ExecutePermissionTask(device, command, context, prepared == null ? null : prepared[device.DeviceId]));
                for (var deviceIndex = 0; deviceIndex < onlineDevices.Count; deviceIndex++)
                {
                    var device = onlineDevices[deviceIndex];
                    var result = permissionOutcomes[deviceIndex];
                    var detail = ToDeviceResult(device, result);
                    var shouldQueueRetry = ShouldQueueSyncPermissionRetry(result);
                    employeeResults[command.EmployeeId].DeviceResults.Add(detail);
                    if (!result.Success)
                    {
                        RecordDeviceError(deviceErrors, dbErrors, device, command.EmployeeId, result);
                    }

                    if (shouldQueueRetry)
                    {
                        var queued = QueueRetry(device, command.EmployeeId, "SyncPermission", PermissionPayload(command), command.PermissionCode, result.Message, context, detail, dbErrors, result);
                        queuedDetails.Add(queued);
                    }
                }

                foreach (var device in offlineDevices)
                {
                    var detail = ToQueuedDeviceResult(device, "SyncPermission");
                    queuedDetails.Add(QueueRetry(device, command.EmployeeId, "SyncPermission", PermissionPayload(command), command.PermissionCode,
                        "设备离线，已生成补偿意图。", context, detail, dbErrors,
                        prepared == null ? null : new DeviceTaskResult { RetryPersisted = true, Data = prepared[device.DeviceId] }));
                    employeeResults[command.EmployeeId].DeviceResults.Add(detail);
                }

                if (!(retryWriter is DeviceOperationRetryStore) && IsEmployeeOperationComplete(employeeResults[command.EmployeeId]))
                {
                    TryUpdateUser(dbErrors, () => userSyncWriter.MarkPermissionSynced(command.EmployeeId, command.PermissionCode), command.EmployeeId, "MarkPermissionSynced");
                }
            }

            var succeededEmployees = employeeResults.Values.Count(IsEmployeeOperationComplete);
            var failedEmployees = employeeResults.Values.Count(item => item.DeviceResults.Any(device => !device.Success && !device.Queued));
            var queuedEmployees = employeeResults.Values.Count(item => item.DeviceResults.Any(device => device.Queued));
            var code = DetermineCode(parsed.Items.Count, succeededEmployees, failedEmployees, queuedEmployees);
            if (dbErrors.Count > 0 && code == "OK") code = "DB_ERROR";

            return JsonResponse.Create(context.RequestId, code != "FAILED" && code != "DB_ERROR", code, BuildMessage(code), new Dictionary<string, object>
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
                parsed = ParsePersonCommands(requestJson, maxFaceImageBytes, maxBatchFaceBytes);
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
                var personOutcomes = ExecuteAcrossDeviceLanes(onlineDevices, device =>
                {
                    var personResult = ExecutePersonTask(device, command, context, includeFace: true);
                    DeviceTaskResult faceResult = null;
                    if (personResult.Success && command.HasFace)
                    {
                        faceResult = ExecuteUploadFaceTask(device, command, context, personResult.Data as DeviceOperationRetryState);
                    }

                    return new PersonLaneOutcome { PersonResult = personResult, FaceResult = faceResult };
                });
                for (var deviceIndex = 0; deviceIndex < onlineDevices.Count; deviceIndex++)
                {
                    var device = onlineDevices[deviceIndex];
                    var personResult = personOutcomes[deviceIndex].PersonResult;
                    var faceResult = personOutcomes[deviceIndex].FaceResult;
                    var personDetail = ToDeviceResult(device, personResult, "SyncPerson");
                    employeeResults[command.EmployeeId].DeviceResults.Add(personDetail);
                    if (!personResult.Success)
                    {
                        RecordDeviceError(deviceErrors, dbErrors, device, command.EmployeeId, personResult);
                        if (personResult.Retryable)
                        {
                            queuedDetails.Add(QueueRetry(device, command.EmployeeId, "SyncPerson", PersonPayload(command), null, personResult.Message, context, personDetail, dbErrors, personResult));
                            if (command.HasFace)
                            {
                                var pendingFace = ToQueuedDeviceResult(device, "UploadFace");
                                employeeResults[command.EmployeeId].DeviceResults.Add(pendingFace);
                                queuedDetails.Add(QueueRetry(device, command.EmployeeId, "UploadFace", FacePayload(command), null,
                                    "人员尚未下发，人脸等待人员补偿完成。", context, pendingFace, dbErrors, personResult));
                            }
                        }

                        continue;
                    }

                    if (faceResult != null)
                    {
                        var faceDetail = ToDeviceResult(device, faceResult, "UploadFace");
                        employeeResults[command.EmployeeId].DeviceResults.Add(faceDetail);
                        if (faceResult.Success)
                        {
                            facesUploaded++;
                        }
                        else
                        {
                            RecordDeviceError(deviceErrors, dbErrors, device, command.EmployeeId, faceResult);
                            if (faceResult.Retryable)
                            {
                                queuedDetails.Add(QueueRetry(device, command.EmployeeId, "UploadFace", FacePayload(command), null, faceResult.Message, context, faceDetail, dbErrors, faceResult));
                            }
                        }
                    }
                }

                foreach (var device in offlineDevices)
                {
                    var bundledFace = command.HasFace && retryWriter is IDeviceOperationRetryExecutionStore;
                    var personDetail = QueueOfflineRetry(device, command.EmployeeId, "SyncPerson", PersonPayload(command), null,
                        context, queuedDetails, dbErrors, bundledFace ? FacePayload(command) : null);
                    employeeResults[command.EmployeeId].DeviceResults.Add(personDetail);
                    if (command.HasFace)
                    {
                        if (bundledFace)
                        {
                            var faceDetail = ToQueuedDeviceResult(device, "UploadFace");
                            faceDetail.Queued = personDetail.Queued;
                            faceDetail.Code = personDetail.Code;
                            faceDetail.Message = personDetail.Message;
                            employeeResults[command.EmployeeId].DeviceResults.Add(faceDetail);
                            queuedDetails.Add(CreateRetryIntent(device, command.EmployeeId, FacePayload(command), null,
                                "UploadFace", context).ToDetail(faceDetail.Code, faceDetail.Message));
                        }
                        else
                        {
                            employeeResults[command.EmployeeId].DeviceResults.Add(QueueOfflineRetry(device, command.EmployeeId, "UploadFace", FacePayload(command), null, context, queuedDetails, dbErrors));
                        }
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
            if (dbErrors.Count > 0 && code == "OK") code = "DB_ERROR";

            return JsonResponse.Create(context.RequestId, code != "FAILED" && code != "DB_ERROR", code, BuildMessage(code), new Dictionary<string, object>
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

        public string SyncFacesToDevices(string requestJson, GrpcRequestContext context = null)
        {
            return ExecuteUnary("SyncFacesToDevices", requestJson, context, SyncFacesToDevicesCore);
        }

        private string SyncFacesToDevicesCore(string requestJson, GrpcRequestContext context)
        {
            context = EnsureContext(context);
            TargetedSyncRequest<PersonSyncCommand> request;
            IReadOnlyList<DeviceRuntimeSnapshot> devices;
            try
            {
                request = ParseTargetedFaceRequest(requestJson, maxFaceImageBytes, maxBatchFaceBytes);
                devices = ResolveTargetAcsDevices(request.DeviceIds);
            }
            catch (RequestValidationException ex)
            {
                return Error(context, ex.Code, ex.Message);
            }
            catch (Exception ex)
            {
                return Error(context, "INVALID_ARGUMENT", ex.Message);
            }

            var onlineDevices = devices.Where(item => item.IsConnected && item.SdkUserId.HasValue).ToList();
            var offlineDevices = devices.Where(item => !item.IsConnected || !item.SdkUserId.HasValue).ToList();
            var employeeResults = CreateEmployeeResults(request.Items.Select(item => item.EmployeeId));
            var queuedDetails = new List<object>();
            var deviceErrors = new List<object>();
            var dbErrors = new List<object>();
            var facesUploaded = 0;

            foreach (var command in request.Items)
            {
                var faceOutcomes = ExecuteAcrossDeviceLanes(onlineDevices, device => ExecuteUploadFaceTask(device, command, context));
                for (var deviceIndex = 0; deviceIndex < onlineDevices.Count; deviceIndex++)
                {
                    var device = onlineDevices[deviceIndex];
                    var result = faceOutcomes[deviceIndex];
                    var detail = ToDeviceResult(device, result, "UploadFace");
                    employeeResults[command.EmployeeId].DeviceResults.Add(detail);
                    if (result.Success)
                    {
                        facesUploaded++;
                    }
                    else
                    {
                        RecordDeviceError(deviceErrors, dbErrors, device, command.EmployeeId, result);
                        if (result.Retryable)
                        {
                            queuedDetails.Add(QueueRetry(device, command.EmployeeId, "UploadFace", FacePayload(command), null, result.Message, context, detail, dbErrors, result));
                        }
                    }
                }

                foreach (var device in offlineDevices)
                {
                    employeeResults[command.EmployeeId].DeviceResults.Add(QueueOfflineRetry(device, command.EmployeeId, "UploadFace", FacePayload(command), null, context, queuedDetails, dbErrors));
                }
            }

            var succeededEmployees = employeeResults.Values.Count(item => item.DeviceResults.Any(device => device.Success));
            var failedEmployees = employeeResults.Values.Count(item => item.DeviceResults.Any(device => !device.Success && !device.Queued));
            var queuedEmployees = employeeResults.Values.Count(item => item.DeviceResults.Any(device => device.Queued));
            var code = DetermineCode(request.Items.Count, succeededEmployees, failedEmployees, queuedEmployees);
            if (dbErrors.Count > 0 && code == "OK") code = "DB_ERROR";

            return JsonResponse.Create(context.RequestId, code != "FAILED" && code != "DB_ERROR", code, BuildMessage(code), new Dictionary<string, object>
            {
                ["total"] = request.Items.Count,
                ["succeeded"] = succeededEmployees,
                ["failed"] = failedEmployees,
                ["queued"] = queuedEmployees,
                ["facesUploaded"] = facesUploaded,
                ["targetDevices"] = devices.Count,
                ["queuedDetails"] = queuedDetails,
                ["items"] = employeeResults.Values.Select(item => item.ToDictionary()).ToList(),
                ["deviceErrors"] = deviceErrors,
                ["dbErrors"] = dbErrors
            });
        }

        public System.Threading.Tasks.Task<string> SyncFacesToDevicesAsync(string requestJson, GrpcRequestContext context = null)
        {
            context = EnsureContext(context);
            return System.Threading.Tasks.Task.Run(() => SyncFacesToDevices(requestJson, context), context.CancellationToken);
        }

        public string SyncPersonsToDevices(string requestJson, GrpcRequestContext context = null)
        {
            return ExecuteUnary("SyncPersonsToDevices", requestJson, context, SyncPersonsToDevicesCore);
        }

        private string SyncPersonsToDevicesCore(string requestJson, GrpcRequestContext context)
        {
            context = EnsureContext(context);
            TargetedSyncRequest<PersonSyncCommand> request;
            IReadOnlyList<DeviceRuntimeSnapshot> devices;
            try
            {
                request = ParseTargetedPersonRequest(requestJson, maxFaceImageBytes, maxBatchFaceBytes);
                devices = ResolveTargetAcsDevices(request.DeviceIds);
            }
            catch (RequestValidationException ex)
            {
                return Error(context, ex.Code, ex.Message);
            }
            catch (Exception ex)
            {
                return Error(context, "INVALID_ARGUMENT", ex.Message);
            }

            var onlineDevices = devices.Where(item => item.IsConnected && item.SdkUserId.HasValue).ToList();
            var offlineDevices = devices.Where(item => !item.IsConnected || !item.SdkUserId.HasValue).ToList();
            var employeeResults = CreateEmployeeResults(request.Items.Select(item => item.EmployeeId));
            var queuedDetails = new List<object>();
            var deviceErrors = new List<object>();
            var dbErrors = new List<object>();

            foreach (var command in request.Items)
            {
                var personOutcomes = ExecuteAcrossDeviceLanes(onlineDevices, device => ExecutePersonTask(device, command, context));
                for (var deviceIndex = 0; deviceIndex < onlineDevices.Count; deviceIndex++)
                {
                    var device = onlineDevices[deviceIndex];
                    var result = personOutcomes[deviceIndex];
                    var detail = ToDeviceResult(device, result, "SyncPerson");
                    employeeResults[command.EmployeeId].DeviceResults.Add(detail);
                    if (!result.Success)
                    {
                        RecordDeviceError(deviceErrors, dbErrors, device, command.EmployeeId, result);
                        if (result.Retryable)
                        {
                            queuedDetails.Add(QueueRetry(device, command.EmployeeId, "SyncPerson", PersonPayload(command), null, result.Message, context, detail, dbErrors, result));
                        }
                    }
                }

                foreach (var device in offlineDevices)
                {
                    employeeResults[command.EmployeeId].DeviceResults.Add(QueueOfflineRetry(device, command.EmployeeId, "SyncPerson", PersonPayload(command), null, context, queuedDetails, dbErrors));
                }
            }

            var succeededEmployees = employeeResults.Values.Count(item => item.DeviceResults.Any(device => device.Success));
            var failedEmployees = employeeResults.Values.Count(item => item.DeviceResults.Any(device => !device.Success && !device.Queued));
            var queuedEmployees = employeeResults.Values.Count(item => item.DeviceResults.Any(device => device.Queued));
            var code = DetermineCode(request.Items.Count, succeededEmployees, failedEmployees, queuedEmployees);
            if (dbErrors.Count > 0 && code == "OK") code = "DB_ERROR";

            return JsonResponse.Create(context.RequestId, code != "FAILED" && code != "DB_ERROR", code, BuildMessage(code), new Dictionary<string, object>
            {
                ["total"] = request.Items.Count,
                ["succeeded"] = succeededEmployees,
                ["failed"] = failedEmployees,
                ["queued"] = queuedEmployees,
                ["targetDevices"] = devices.Count,
                ["queuedDetails"] = queuedDetails,
                ["items"] = employeeResults.Values.Select(item => item.ToDictionary()).ToList(),
                ["deviceErrors"] = deviceErrors,
                ["dbErrors"] = dbErrors
            });
        }

        public System.Threading.Tasks.Task<string> SyncPersonsToDevicesAsync(string requestJson, GrpcRequestContext context = null)
        {
            context = EnsureContext(context);
            return System.Threading.Tasks.Task.Run(() => SyncPersonsToDevices(requestJson, context), context.CancellationToken);
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
            return JsonResponse.Create(context.RequestId, code != "FAILED" && code != "DB_ERROR", code, BuildMessage(code), new Dictionary<string, object>
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
            if (imageBytes.Length > maxFaceImageBytes)
            {
                var tooLargeMessage = "采集图片超过 " + (maxFaceImageBytes / 1024) + "KB。";
                enrollmentStore.Fail(taskId, "FACE_TOO_LARGE", tooLargeMessage);
                frames.Add(JsonResponse.Create(context.RequestId, false, "FACE_TOO_LARGE", tooLargeMessage, new Dictionary<string, object>
                {
                    ["taskId"] = taskId,
                    ["employeeId"] = employeeId,
                    ["frameIndex"] = 0,
                    ["faceImageBase64"] = string.Empty,
                    ["faceImageFormat"] = "jpg",
                    ["qualityScore"] = 0,
                    ["recommend"] = false
                }, new List<string> { tooLargeMessage }));
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
                    employeeResults[command.EmployeeId].DeviceResults.Add(detail);
                    if (!result.Success)
                    {
                        RecordDeviceError(deviceErrors, dbErrors, device, command.EmployeeId, result);
                        if (result.Retryable)
                        {
                            queuedDetails.Add(QueueRetry(device, command.EmployeeId, operation, EmployeePayload(command.EmployeeId), null, result.Message, context, detail, dbErrors, result));
                        }
                    }
                }

                foreach (var device in offlineDevices)
                {
                    employeeResults[command.EmployeeId].DeviceResults.Add(QueueOfflineRetry(device, command.EmployeeId, operation, EmployeePayload(command.EmployeeId), null, context, queuedDetails, dbErrors));
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
            if (dbErrors.Count > 0 && code == "OK") code = "DB_ERROR";
            return JsonResponse.Create(context.RequestId, code != "FAILED" && code != "DB_ERROR", code, BuildMessage(code), new Dictionary<string, object>
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

        private DeviceTaskResult ExecutePermissionTask(DeviceRuntimeSnapshot device, PermissionCommand command, GrpcRequestContext context, DeviceOperationRetryState existingState = null)
        {
            return SubmitMutationTask(device, DeviceTaskType.SyncPermission, "SyncPermission", context,
                CreateRetryIntent(device, command.EmployeeId, PermissionPayload(command), command.PermissionCode, "SyncPermission", context), async taskContext =>
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
            }, existingState);
        }

        private DeviceTaskResult ExecutePersonTask(DeviceRuntimeSnapshot device, PersonSyncCommand command, GrpcRequestContext context, bool includeFace = false)
        {
            return SubmitMutationTask(device, DeviceTaskType.SyncPerson, "SyncPerson", context,
                CreateRetryIntent(device, command.EmployeeId, PersonPayload(command), null, "SyncPerson", context, includeFace && command.HasFace ? FacePayload(command) : null), async taskContext =>
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

        private DeviceTaskResult ExecuteUploadFaceTask(DeviceRuntimeSnapshot device, PersonSyncCommand command, GrpcRequestContext context, DeviceOperationRetryState existingState = null)
        {
            return SubmitMutationTask(device, DeviceTaskType.UploadFace, "UploadFace", context,
                CreateRetryIntent(device, command.EmployeeId, FacePayload(command), null, "UploadFace", context), async taskContext =>
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
                    MaxImageBytes = maxFaceImageBytes,
                    Face = command.ToFaceInfo()
                }, taskContext.CancellationToken).ConfigureAwait(false);
                return DeviceTaskResult.FromTask(taskContext.Task, true, "OK", "人脸下发成功。", snapshot.Status, started, DateTime.Now);
            }, existingState);
        }

        private DeviceTaskResult ExecuteDeleteFaceTask(DeviceRuntimeSnapshot device, string employeeId, GrpcRequestContext context)
        {
            return SubmitMutationTask(device, DeviceTaskType.DeleteFace, "DeleteFace", context,
                CreateRetryIntent(device, employeeId, EmployeePayload(employeeId), null, "DeleteFace", context), async taskContext =>
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
            return SubmitMutationTask(device, DeviceTaskType.DeletePerson, "DeletePerson", context,
                CreateRetryIntent(device, employeeId, EmployeePayload(employeeId), null, "DeletePerson", context), async taskContext =>
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
            // 任务期限 = 采集超时 + 5s 余量：避免 dispatcher 默认超时先于采集窗口终止任务（复核 R09）。
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
            }, faceCaptureTimeoutMs + 5000);
        }

        private static DeviceOperationRetryIntent CreateRetryIntent(DeviceRuntimeSnapshot device, string employeeId,
            IDictionary<string, object> payload, int? permissionLevel, string operation, GrpcRequestContext context,
            IDictionary<string, object> relatedFace = null)
        {
            return new DeviceOperationRetryIntent
            {
                DeviceId = device.DeviceId,
                EmployeeId = employeeId,
                Operation = operation,
                PermissionLevel = permissionLevel,
                PayloadJson = payload == null ? null : JsonRequestReader.Serialize(payload),
                RelatedFacePayloadJson = relatedFace == null ? null : JsonRequestReader.Serialize(relatedFace),
                RequestId = context.RequestId,
                NextRetryAt = DateTime.Now.AddMinutes(1)
            };
        }

        private DeviceTaskResult SubmitMutationTask(DeviceRuntimeSnapshot device, DeviceTaskType taskType,
            string operationName, GrpcRequestContext context, DeviceOperationRetryIntent intent,
            Func<DeviceTaskContext, System.Threading.Tasks.Task<DeviceTaskResult>> executeAsync,
            DeviceOperationRetryState existingState = null)
        {
            var store = retryWriter as IDeviceOperationRetryExecutionStore;
            if (store == null)
            {
                return SubmitGatewayTask(device, taskType, operationName, context, executeAsync);
            }

            var state = existingState;
            try
            {
                if (state == null)
                {
                    var written = store.UpsertIntent(intent);
                    if (!written.Success)
                    {
                        return new DeviceTaskResult { Code = "DB_ERROR", Message = written.Message };
                    }
                    state = store.LoadIntent(written.Intent);
                }
                if (state == null)
                {
                    return new DeviceTaskResult { Code = "SUPERSEDED", Message = "请求已被新意图替代。" };
                }

                RetryOperationNames.TryParse(operationName, out var operation);
                var result = SubmitGatewayTask(device, taskType, operationName, context, async taskContext =>
                {
                    DeviceTaskResult completed;
                    try
                    {
                        if (!store.IsCurrent(state))
                        {
                            return DeviceTaskResult.Rejected(taskContext.Task, "SUPERSEDED", "请求已被新意图替代。");
                        }
                        try
                        {
                            completed = await executeAsync(taskContext).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            completed = MapGatewayException(taskContext.Task, ex, taskContext.SnapshotBeforeExecution);
                        }
                        store.CompleteOnlineOperation(state, operation, completed);
                    }
                    catch (Exception ex)
                    {
                        completed = DeviceTaskResult.Rejected(taskContext.Task, "DB_ERROR", ex.Message);
                        completed.Retryable = true;
                    }
                    completed.RetryPersisted = true;
                    completed.Data = state;
                    return completed;
                });
                result.RetryPersisted = true;
                result.Data = state;
                if (!result.Success && (result.Code == "TIMEOUT" || result.Code == "CANCELLED" ||
                    result.Code == "QUEUE_FULL" || result.Code == "DEVICE_OFFLINE" || result.Code == "DEVICE_MANUALLY_DISCONNECTED"))
                {
                    result.Retryable = true;
                }
                return result;
            }
            catch (Exception ex)
            {
                return new DeviceTaskResult { Code = "DB_ERROR", Message = ex.Message };
            }
        }

        private DeviceTaskResult SubmitGatewayTask(DeviceRuntimeSnapshot device, DeviceTaskType taskType, string operationName, GrpcRequestContext context, Func<DeviceTaskContext, System.Threading.Tasks.Task<DeviceTaskResult>> executeAsync, int? timeoutMilliseconds = null)
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
            if (timeoutMilliseconds.HasValue)
            {
                task.TimeoutMilliseconds = timeoutMilliseconds.Value;
            }

            return dispatcher.SubmitAndWaitAsync(task, context.CancellationToken).GetAwaiter().GetResult();
        }

        private DeviceTaskResult MapGatewayException(DeviceSdkTask task, Exception ex, DeviceRuntimeSnapshot snapshot)
        {
            logger?.Debug("PermissionSync", "设备接口异常详细信息。", new LogFields
            {
                RequestId = task.RequestId,
                DeviceId = task.DeviceId,
                OperationName = task.OperationName,
                Exception = ex?.ToString()
            });
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

        private static void RecordDeviceError(IList<object> deviceErrors, IList<object> dbErrors,
            DeviceRuntimeSnapshot device, string employeeId, DeviceTaskResult result)
        {
            var error = ToDeviceError(device, employeeId, result);
            deviceErrors.Add(error);
            if (result.Code == "DB_ERROR")
            {
                dbErrors.Add(error);
            }
        }

        private DeviceOperationDetail QueueOfflineRetry(DeviceRuntimeSnapshot device, string employeeId, string operation,
            IDictionary<string, object> payload, int? permissionLevel, GrpcRequestContext context,
            IList<object> queuedDetails, IList<object> dbErrors, IDictionary<string, object> relatedFace = null)
        {
            var detail = ToQueuedDeviceResult(device, operation);
            queuedDetails.Add(QueueRetry(device, employeeId, operation, payload, permissionLevel,
                "设备离线，已生成补偿意图。", context, detail, dbErrors, relatedFace: relatedFace));
            return detail;
        }

        private object QueueRetry(DeviceRuntimeSnapshot device, string employeeId, string operation, IDictionary<string, object> payload, int? permissionLevel, string message, GrpcRequestContext context, DeviceOperationDetail detail, IList<object> dbErrors, DeviceTaskResult priorResult = null, IDictionary<string, object> relatedFace = null)
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
                RelatedFacePayloadJson = relatedFace == null ? null : JsonRequestReader.Serialize(relatedFace),
                RequestId = context == null ? null : context.RequestId,
                LastError = message,
                CreatedAt = DateTime.Now,
                NextRetryAt = DateTime.Now
            };
            DeviceOperationRetryWriteResult written;
            try
            {
                written = priorResult != null && priorResult.RetryPersisted
                    ? DeviceOperationRetryWriteResult.Ok(intent)
                    : retryWriter.UpsertIntent(intent);
            }
            catch (Exception ex)
            {
                written = DeviceOperationRetryWriteResult.Failed(intent, "DB_ERROR", ex.Message);
            }
            detail.Queued = written.Success;
            if (!written.Success)
            {
                detail.Code = "DB_ERROR";
                detail.Message = written.Message;
                dbErrors.Add(intent.ToDetail("DB_ERROR", written.Message));
            }
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
            fields.Extra["intentVersion"] = written.Intent?.IntentVersion.ToString();
            logger?.Write(written.Success ? LogLevel.Debug : LogLevel.Error, "DeviceOperationRetry",
                written.Success ? "接口补偿意图已持久化，等待设备执行。" : "接口补偿意图持久化失败。", fields);
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

        private static TargetedSyncRequest<PersonSyncCommand> ParseTargetedFaceRequest(string requestJson, int maxFaceImageBytes, int maxBatchFaceBytes)
        {
            var deviceIds = ParseRequiredDeviceIds(requestJson);
            var root = JsonRequestReader.ParseAny(requestJson);
            var items = JsonRequestReader.ReadItems(root, "people", "items", "records", "data");
            ValidateBatch(items.Count);
            var commands = new List<PersonSyncCommand>();
            long batchFaceChars = 0;
            foreach (var item in items)
            {
                var values = JsonRequestReader.AsObject(item);
                var employeeId = TrimRequired(JsonRequestReader.GetString(values, "employee_id", "employeeId", "employee_no", "employeeNo"), "employee_id");
                var faceBase64 = JsonRequestReader.GetString(values, "face_image_base64", "faceImageBase64", "face_base64", "faceBase64", "face_image");
                if (string.IsNullOrWhiteSpace(faceBase64))
                {
                    throw new RequestValidationException("INVALID_ARGUMENT", "face_image_base64 必填。");
                }

                batchFaceChars += faceBase64.Length;
                commands.Add(new PersonSyncCommand
                {
                    EmployeeId = employeeId,
                    FaceImageBase64 = NormalizeBase64(faceBase64),
                    FaceImageBytes = DecodeFaceBytes(faceBase64, maxFaceImageBytes),
                    FaceImageFormat = JsonRequestReader.GetString(values, "face_image_format", "faceImageFormat") ?? InferFormat(faceBase64)
                });
            }

            if (commands.Count == 0)
            {
                throw new RequestValidationException("INVALID_ARGUMENT", "请求至少包含一条记录。");
            }

            ValidateBatchFaceChars(batchFaceChars, maxBatchFaceBytes);
            return new TargetedSyncRequest<PersonSyncCommand>(deviceIds, commands);
        }

        private static TargetedSyncRequest<PersonSyncCommand> ParseTargetedPersonRequest(string requestJson, int maxFaceImageBytes, int maxBatchFaceBytes)
        {
            var deviceIds = ParseRequiredDeviceIds(requestJson);
            var root = JsonRequestReader.ParseAny(requestJson);
            var items = JsonRequestReader.ReadItems(root, "people", "items", "records", "data");
            foreach (var item in items)
            {
                var values = JsonRequestReader.AsObject(item);
                object ignored;
                if (JsonRequestReader.TryGetValue(
                    values,
                    out ignored,
                    "face_image_base64",
                    "faceImageBase64",
                    "face_base64",
                    "faceBase64",
                    "face_image"))
                {
                    throw new RequestValidationException("INVALID_ARGUMENT", "SyncPersonsToDevices 不接受人脸字段，请使用 SyncFacesToDevices。");
                }
            }

            var people = ParsePersonCommands(requestJson, maxFaceImageBytes, maxBatchFaceBytes);
            if (!people.Success)
            {
                throw new RequestValidationException(people.Code, people.Message);
            }

            foreach (var person in people.Items)
            {
                if (string.IsNullOrWhiteSpace(person.Name))
                {
                    person.Name = person.EmployeeId;
                }
            }

            return new TargetedSyncRequest<PersonSyncCommand>(deviceIds, people.Items.ToList());
        }

        private static IReadOnlyList<int> ParseRequiredDeviceIds(string requestJson)
        {
            var root = JsonRequestReader.ParseAny(requestJson);
            var values = JsonRequestReader.AsObject(root, "请求 JSON 必须是对象。");
            object raw;
            if (!JsonRequestReader.TryGetValue(values, out raw, "deviceIds", "device_ids") || raw == null)
            {
                throw new RequestValidationException("INVALID_ARGUMENT", "deviceIds 必填且必须是非空整数数组。");
            }

            var enumerable = raw as IEnumerable;
            if (enumerable == null || raw is string || raw is IDictionary<string, object>)
            {
                throw new RequestValidationException("INVALID_ARGUMENT", "deviceIds 必须是非空整数数组。");
            }

            var result = new List<int>();
            var seen = new HashSet<int>();
            foreach (var item in enumerable)
            {
                int deviceId;
                if (item is int)
                {
                    deviceId = (int)item;
                }
                else if (item is long && (long)item <= int.MaxValue && (long)item >= int.MinValue)
                {
                    deviceId = (int)(long)item;
                }
                else
                {
                    throw new RequestValidationException("INVALID_ARGUMENT", "deviceIds 只能包含整数。");
                }

                if (deviceId <= 0)
                {
                    throw new RequestValidationException("INVALID_ARGUMENT", "deviceIds 只能包含大于 0 的整数。");
                }

                if (seen.Add(deviceId))
                {
                    result.Add(deviceId);
                }
            }

            if (result.Count == 0)
            {
                throw new RequestValidationException("INVALID_ARGUMENT", "deviceIds 必填且不能为空。");
            }

            return result;
        }

        private IReadOnlyList<DeviceRuntimeSnapshot> ResolveTargetAcsDevices(IEnumerable<int> deviceIds)
        {
            var snapshots = GetTargetDevices()
                .Where(item => item != null)
                .GroupBy(item => item.DeviceId)
                .ToDictionary(group => group.Key, group => group.First());
            var result = new List<DeviceRuntimeSnapshot>();
            foreach (var deviceId in deviceIds)
            {
                DeviceRuntimeSnapshot snapshot;
                if (!snapshots.TryGetValue(deviceId, out snapshot))
                {
                    throw new RequestValidationException("INVALID_ARGUMENT", "指定设备不存在: deviceId=" + deviceId + "。");
                }

                if (!snapshot.Enabled)
                {
                    throw new RequestValidationException("INVALID_ARGUMENT", "指定设备未启用: deviceId=" + deviceId + "。");
                }

                if (snapshot.Types == null || !snapshot.Types.Contains(DeviceType.Acs))
                {
                    throw new RequestValidationException("INVALID_ARGUMENT", "指定设备不是 Acs 类型: deviceId=" + deviceId + "。");
                }

                result.Add(snapshot);
            }

            return result;
        }

        private static ParseResult<PersonSyncCommand> ParsePersonCommands(string requestJson, int maxFaceImageBytes, int maxBatchFaceBytes)
        {
            var root = JsonRequestReader.ParseAny(requestJson);
            var items = JsonRequestReader.ReadItems(root, "people", "items", "records", "data");
            ValidateBatch(items.Count);
            var commands = new List<PersonSyncCommand>();
            long batchFaceChars = 0;
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
                var faceBytes = DecodeFaceBytes(faceBase64, maxFaceImageBytes);
                if (!string.IsNullOrWhiteSpace(faceBase64))
                {
                    batchFaceChars += faceBase64.Length;
                }

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

            ValidateBatchFaceChars(batchFaceChars, maxBatchFaceBytes);
            return ParseResult<PersonSyncCommand>.Ok(commands);
        }

        // 单请求人脸图片 base64 总长度预算（K2）：超限必须在业务层给出可指导拆批的错误，
        // 而不是让请求在传输层被 ResourceExhausted 拒绝且无提示。预算以 base64 字符数计，
        // 约为图片二进制字节数的 4/3，保守方向收紧。
        private static void ValidateBatchFaceChars(long batchFaceChars, int maxBatchFaceBytes)
        {
            if (maxBatchFaceBytes <= 0 || batchFaceChars <= maxBatchFaceBytes)
            {
                return;
            }

            throw new RequestValidationException("REQUEST_TOO_LARGE",
                "批量人脸图片过大：本批 base64 总长 " + batchFaceChars + " 字符，超过单请求预算 " + maxBatchFaceBytes +
                " 字符（约 " + (maxBatchFaceBytes / (1024 * 1024)) + " MiB）。请按完整序列化请求字节数拆小批次后重试。");
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

        // 按设备工作线程分道并行执行 SDK 任务（复核 R08）：同道（同设备）保持串行和人员先于人脸的顺序，
        // 跨道并行，一个慢设备不再拖延其他工作线程上的空闲设备。结果按传入设备顺序返回，
        // 便于后续汇总继续以单线程操作非线程安全的列表；异常抛出首个原始异常，语义与串行一致。
        private List<T> ExecuteAcrossDeviceLanes<T>(List<DeviceRuntimeSnapshot> devices, Func<DeviceRuntimeSnapshot, T> work)
        {
            if (devices.Count <= 1)
            {
                return RunSerially(devices, work);
            }

            var groups = devices.GroupBy(device => ResolveDeviceLane(device.DeviceId)).ToList();
            if (groups.Count <= 1)
            {
                return RunSerially(devices, work);
            }

            var outputs = new T[devices.Count];
            var outputIndexByDevice = new Dictionary<int, int>(devices.Count);
            for (var i = 0; i < devices.Count; i++)
            {
                outputIndexByDevice[devices[i].DeviceId] = i;
            }

            var laneErrors = new List<Exception>();
            var errorGate = new object();
            var laneTasks = new List<System.Threading.Tasks.Task>();
            foreach (var group in groups)
            {
                laneTasks.Add(System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        foreach (var device in group)
                        {
                            outputs[outputIndexByDevice[device.DeviceId]] = work(device);
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (errorGate)
                        {
                            laneErrors.Add(ex);
                        }
                    }
                }));
            }

            System.Threading.Tasks.Task.WaitAll(laneTasks.ToArray());
            if (laneErrors.Count > 0)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(laneErrors[0]).Throw();
            }

            return outputs.ToList();
        }

        private static List<T> RunSerially<T>(List<DeviceRuntimeSnapshot> devices, Func<DeviceRuntimeSnapshot, T> work)
        {
            var results = new List<T>(devices.Count);
            foreach (var device in devices)
            {
                results.Add(work(device));
            }

            return results;
        }

        private int ResolveDeviceLane(int deviceId)
        {
            var route = registry.TryGetWorkerRoute(deviceId);
            return route.WorkerIndex ?? -1;
        }

        private sealed class PersonLaneOutcome
        {
            public DeviceTaskResult PersonResult;

            public DeviceTaskResult FaceResult;
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

        private static byte[] DecodeFaceBytes(string value, int maxFaceImageBytes)
        {
            var normalized = NormalizeBase64(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return new byte[0];
            }

            try
            {
                var bytes = Convert.FromBase64String(normalized);
                if (bytes.Length > maxFaceImageBytes)
                {
                    throw new RequestValidationException("FACE_TOO_LARGE", "人脸图片超过 " + (maxFaceImageBytes / 1024) + "KB。");
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
                case "DB_ERROR":
                    return "设备操作已完成，但同步状态写入失败，请查看 dbErrors 并重试请求。";
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

        private sealed class TargetedSyncRequest<T>
        {
            public TargetedSyncRequest(IReadOnlyList<int> deviceIds, IReadOnlyList<T> items)
            {
                DeviceIds = deviceIds;
                Items = items;
            }

            public IReadOnlyList<int> DeviceIds { get; }

            public IReadOnlyList<T> Items { get; }
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
