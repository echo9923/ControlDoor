using System;
using System.Collections.Generic;
using System.Linq;
using ControlDoor.Devices.Management;
using ControlDoor.Devices.Runtime;

namespace ControlDoor.GrpcApi
{
    public sealed class AccessControlGrpcService
    {
        public const string ServiceName = "device.AccessControlService";
        public const string GetDeviceStatusFullName = "/device.AccessControlService/GetDeviceStatus";
        public const string AddDeviceFullName = "/device.AccessControlService/AddDevice";
        public const string DeleteDeviceFullName = "/device.AccessControlService/DeleteDevice";
        public const string DisconnectDeviceFullName = "/device.AccessControlService/DisconnectDevice";
        public const string ReconnectDeviceFullName = "/device.AccessControlService/ReconnectDevice";

        private readonly DeviceLifecycleService lifecycle;
        private readonly IDeviceRepository repository;
        private readonly string apiKey;

        public AccessControlGrpcService(DeviceLifecycleService lifecycle, IDeviceRepository repository, string apiKey = null)
        {
            this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
            this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
            this.apiKey = apiKey ?? string.Empty;
        }

        public IReadOnlyList<string> MethodFullNames { get; } = new[]
        {
            GetDeviceStatusFullName,
            AddDeviceFullName,
            DeleteDeviceFullName,
            DisconnectDeviceFullName,
            ReconnectDeviceFullName
        };

        public string GetDeviceStatus(string requestJson, GrpcRequestContext context = null)
        {
            context = EnsureContext(context);
            var auth = Authorize(context);
            if (!auth.Success)
            {
                return Error(context, auth.Code, auth.Message);
            }

            JsonObject request;
            try
            {
                request = JsonObject.Parse(requestJson);
            }
            catch (Exception ex)
            {
                return Error(context, "INVALID_ARGUMENT", ex.Message);
            }

            var includeDisabled = request.GetBool("includeDisabled") ?? true;
            var refresh = request.GetBool("refresh") ?? false;
            var selected = SelectDevices(request, includeDisabled);
            if (selected.Code != "OK")
            {
                return Error(context, selected.Code, selected.Message);
            }

            if (refresh)
            {
                foreach (var snapshot in selected.Snapshots.Where(item => item.Enabled))
                {
                    lifecycle.SubmitHealthCheck(snapshot.DeviceId, wait: true, requestId: context.RequestId);
                }

                selected = SelectDevices(request, includeDisabled);
            }

            return JsonResponse.Create(context.RequestId, true, "OK", "查询成功。", new Dictionary<string, object>
            {
                ["devices"] = selected.Snapshots.Select(ToDeviceStatus).ToList()
            });
        }

        public string AddDevice(string requestJson, GrpcRequestContext context = null)
        {
            context = EnsureContext(context);
            var auth = Authorize(context);
            if (!auth.Success)
            {
                return Error(context, auth.Code, auth.Message);
            }

            JsonObject request;
            try
            {
                request = JsonObject.Parse(requestJson);
            }
            catch (Exception ex)
            {
                return Error(context, "INVALID_ARGUMENT", ex.Message);
            }

            var record = new DeviceRecord
            {
                DeviceId = request.GetInt("deviceId", "device_id") ?? 0,
                DeviceName = request.GetString("deviceName", "device_name") ?? string.Empty,
                IpAddress = request.GetString("ipAddress", "ip_address") ?? string.Empty,
                Port = request.GetInt("port") ?? 8000,
                Username = request.GetString("username") ?? "admin",
                Password = request.GetString("password") ?? string.Empty,
                Description = request.GetString("description") ?? string.Empty,
                Enabled = request.GetBool("enabled") ?? true
            };
            var connectNow = request.GetBool("connectNow") ?? false;
            var add = lifecycle.RegisterDevice(record, persist: true);
            if (!add.Success)
            {
                return Error(context, add.Code, add.Message);
            }

            var connected = false;
            var connectionMessage = connectNow ? "已请求立即连接。" : "未请求立即连接。";
            if (record.Enabled && connectNow)
            {
                var login = lifecycle.SubmitLogin(record.DeviceId, wait: false, requestId: context.RequestId);
                connected = login.Snapshot != null && login.Snapshot.IsConnected;
                connectionMessage = login.Message;
            }

            return JsonResponse.Create(context.RequestId, true, "OK", "新增设备成功。", new Dictionary<string, object>
            {
                ["device"] = ToDeviceStatus(lifecycle.Registry.TryGetByDeviceId(record.DeviceId).Snapshot),
                ["connectNow"] = connectNow,
                ["connected"] = connected,
                ["connectionMessage"] = connectionMessage
            });
        }

        public string DeleteDevice(string requestJson, GrpcRequestContext context = null)
        {
            context = EnsureContext(context);
            var auth = Authorize(context);
            if (!auth.Success)
            {
                return Error(context, auth.Code, auth.Message);
            }

            var parsed = ParseDeviceIdRequest(requestJson, context);
            if (!parsed.Success)
            {
                return Error(context, parsed.Code, parsed.Message);
            }

            var disconnectFirst = parsed.Request.GetBool("disconnectFirst") ?? true;
            var result = lifecycle.DeleteDevice(parsed.DeviceId, disconnectFirst, context.RequestId);
            if (!result.Success)
            {
                return Error(context, result.Code, result.Message);
            }

            return JsonResponse.Create(context.RequestId, true, "OK", "删除设备成功。", new Dictionary<string, object>
            {
                ["deleted"] = true,
                ["deviceId"] = parsed.DeviceId
            });
        }

        public string DisconnectDevice(string requestJson, GrpcRequestContext context = null)
        {
            context = EnsureContext(context);
            var auth = Authorize(context);
            if (!auth.Success)
            {
                return Error(context, auth.Code, auth.Message);
            }

            var parsed = ParseDeviceIdRequest(requestJson, context);
            if (!parsed.Success)
            {
                return Error(context, parsed.Code, parsed.Message);
            }

            var result = lifecycle.DisconnectDevice(parsed.DeviceId, context.RequestId);
            if (!result.Success)
            {
                return Error(context, result.Code, result.Message);
            }

            var snapshot = lifecycle.Registry.TryGetByDeviceId(parsed.DeviceId).Snapshot;
            return JsonResponse.Create(context.RequestId, true, "OK", "断开设备成功。", new Dictionary<string, object>
            {
                ["deviceId"] = parsed.DeviceId,
                ["isConnected"] = snapshot != null && snapshot.IsConnected,
                ["status"] = snapshot == null ? "Deleted" : snapshot.Status.ToString(),
                ["message"] = result.Message
            });
        }

        public string ReconnectDevice(string requestJson, GrpcRequestContext context = null)
        {
            context = EnsureContext(context);
            var auth = Authorize(context);
            if (!auth.Success)
            {
                return Error(context, auth.Code, auth.Message);
            }

            var parsed = ParseDeviceIdRequest(requestJson, context);
            if (!parsed.Success)
            {
                return Error(context, parsed.Code, parsed.Message);
            }

            var force = parsed.Request.GetBool("force") ?? false;
            var result = lifecycle.ReconnectDevice(parsed.DeviceId, force, context.RequestId);
            if (!result.Success)
            {
                return Error(context, result.Code, result.Message);
            }

            var snapshot = lifecycle.Registry.TryGetByDeviceId(parsed.DeviceId).Snapshot;
            return JsonResponse.Create(context.RequestId, true, "OK", "重连设备已处理。", new Dictionary<string, object>
            {
                ["deviceId"] = parsed.DeviceId,
                ["connected"] = snapshot != null && snapshot.IsConnected,
                ["message"] = result.Message
            });
        }

        private SelectionResult SelectDevices(JsonObject request, bool includeDisabled)
        {
            var ids = request.GetIntList("deviceIds", "device_ids");
            var single = request.GetInt("deviceId", "device_id");
            if (single.HasValue)
            {
                ids.Add(single.Value);
            }

            var ipAddress = request.GetString("ipAddress", "ip_address");
            var snapshots = lifecycle.GetDeviceSnapshots(includeDisabled).ToList();
            if (includeDisabled)
            {
                var knownIds = new HashSet<int>(snapshots.Select(item => item.DeviceId));
                foreach (var record in repository.LoadAllDevices())
                {
                    if (!knownIds.Contains(record.DeviceId))
                    {
                        snapshots.Add(ToSnapshot(record));
                        knownIds.Add(record.DeviceId);
                    }
                }
            }

            if (ids.Count > 0)
            {
                var uniqueIds = ids.Distinct().ToList();
                var found = snapshots.Where(item => uniqueIds.Contains(item.DeviceId)).ToList();
                var missing = uniqueIds.Where(id => found.All(item => item.DeviceId != id)).ToList();
                if (missing.Count > 0)
                {
                    return SelectionResult.Failed("NOT_FOUND", "设备不存在: " + string.Join(",", missing));
                }

                snapshots = found;
            }

            if (!string.IsNullOrWhiteSpace(ipAddress))
            {
                snapshots = snapshots.Where(item => string.Equals(item.IpAddress, ipAddress.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                if (snapshots.Count == 0)
                {
                    return SelectionResult.Failed("NOT_FOUND", "设备不存在。");
                }
            }

            return SelectionResult.Ok(snapshots);
        }

        private ParsedDeviceIdRequest ParseDeviceIdRequest(string requestJson, GrpcRequestContext context)
        {
            JsonObject request;
            try
            {
                request = JsonObject.Parse(requestJson);
            }
            catch (Exception ex)
            {
                return ParsedDeviceIdRequest.Failed("INVALID_ARGUMENT", ex.Message);
            }

            var deviceId = request.GetInt("deviceId", "device_id");
            if (!deviceId.HasValue || deviceId.Value <= 0)
            {
                return ParsedDeviceIdRequest.Failed("INVALID_ARGUMENT", "deviceId 必须大于 0。");
            }

            return ParsedDeviceIdRequest.Ok(request, deviceId.Value);
        }

        private AuthResult Authorize(GrpcRequestContext context)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return AuthResult.Ok();
            }

            string value;
            if (!context.Metadata.TryGetValue("x-api-key", out value) || !string.Equals(value, apiKey, StringComparison.Ordinal))
            {
                return AuthResult.Failed("UNAUTHENTICATED", "x-api-key 无效。");
            }

            return AuthResult.Ok();
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

        private static IDictionary<string, object> ToDeviceStatus(DeviceRuntimeSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return new Dictionary<string, object>();
            }

            return new Dictionary<string, object>
            {
                ["deviceId"] = snapshot.DeviceId,
                ["deviceName"] = snapshot.DeviceName,
                ["ipAddress"] = snapshot.IpAddress,
                ["port"] = snapshot.Port,
                ["enabled"] = snapshot.Enabled,
                ["isConnected"] = snapshot.IsConnected,
                ["status"] = snapshot.Status.ToString(),
                ["statusMessage"] = snapshot.StatusMessage,
                ["lastChecked"] = FormatDate(snapshot.LastCheckedAt),
                ["lastUsed"] = FormatDate(snapshot.LastUsedAt),
                ["lastErrorCode"] = snapshot.LastErrorCode,
                ["lastErrorMessage"] = snapshot.LastErrorMessage
            };
        }

        private static DeviceRuntimeSnapshot ToSnapshot(DeviceRecord record)
        {
            var status = record.Enabled ? DeviceConnectionStatus.Loaded : DeviceConnectionStatus.Disabled;
            return new DeviceRuntimeSnapshot(
                record.DeviceId,
                record.DeviceName,
                record.IpAddress,
                record.Port,
                record.Enabled,
                status,
                false,
                null,
                null,
                string.Empty,
                DeviceCapabilities.Unknown(),
                null,
                null,
                null,
                record.LastUsedAt,
                null,
                ReconnectState.New(),
                DateTime.Now,
                null);
        }

        private static string FormatDate(DateTime? value)
        {
            return value.HasValue ? value.Value.ToString("yyyy-MM-ddTHH:mm:ss") : null;
        }

        private sealed class AuthResult
        {
            public bool Success { get; set; }

            public string Code { get; set; }

            public string Message { get; set; }

            public static AuthResult Ok()
            {
                return new AuthResult { Success = true };
            }

            public static AuthResult Failed(string code, string message)
            {
                return new AuthResult { Success = false, Code = code, Message = message };
            }
        }

        private sealed class SelectionResult
        {
            public string Code { get; set; }

            public string Message { get; set; }

            public IList<DeviceRuntimeSnapshot> Snapshots { get; set; }

            public static SelectionResult Ok(IList<DeviceRuntimeSnapshot> snapshots)
            {
                return new SelectionResult { Code = "OK", Snapshots = snapshots };
            }

            public static SelectionResult Failed(string code, string message)
            {
                return new SelectionResult { Code = code, Message = message, Snapshots = new List<DeviceRuntimeSnapshot>() };
            }
        }

        private sealed class ParsedDeviceIdRequest
        {
            public bool Success { get; set; }

            public string Code { get; set; }

            public string Message { get; set; }

            public JsonObject Request { get; set; }

            public int DeviceId { get; set; }

            public static ParsedDeviceIdRequest Ok(JsonObject request, int deviceId)
            {
                return new ParsedDeviceIdRequest { Success = true, Code = "OK", Request = request, DeviceId = deviceId };
            }

            public static ParsedDeviceIdRequest Failed(string code, string message)
            {
                return new ParsedDeviceIdRequest { Success = false, Code = code, Message = message };
            }
        }
    }
}
