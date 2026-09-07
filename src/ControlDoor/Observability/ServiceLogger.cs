using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ControlDoor.Observability
{
    public sealed class ServiceLogger : IDisposable
    {
        private static readonly ISet<string> ReservedFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "timestamp", "level", "component", "message", "requestId", "traceId", "deviceId",
            "employeeId", "operationName", "elapsedMs", "errorCode", "exception"
        };
        private static readonly IDictionary<string, string> DailyLabels = new Dictionary<string, string>
        {
            ["doorNo"] = "门号", ["doorIndex"] = "门号", ["cameraId"] = "摄像头", ["cameraDeviceId"] = "摄像头",
            ["total"] = "总数", ["succeeded"] = "成功", ["updated"] = "已更新", ["failed"] = "失败",
            ["queued"] = "待补偿", ["facesUploaded"] = "人脸已下发", ["targetDevices"] = "目标设备数",
            ["countBasis"] = "统计口径", ["terminal"] = "终态失败", ["submitted"] = "已投递",
            ["offlineDeferred"] = "离线延后", ["succeededCount"] = "成功数", ["failedCount"] = "失败数",
            ["attemptCount"] = "重试次数", ["nextRetryAt"] = "下次重试", ["dueAt"] = "执行时间",
            ["repeatCount"] = "重复次数", ["reason"] = "原因", ["errorMessage"] = "原因",
            ["message"] = "说明", ["sdkErrorCode"] = "SDK错误码", ["stateId"] = "补偿状态",
            ["status"] = "状态", ["success"] = "成功", ["count"] = "数量", ["detail"] = "详情",
            ["cameraKey"] = "摄像头", ["interlockId"] = "联动编号"
        };
        private readonly object gate = new object();
        private readonly LogOptions options;
        private readonly Func<DateTime> clock;
        private readonly RollingLogFile daily;
        private readonly RollingLogFile diagnostic;
        private readonly AsyncLocal<LogFields> scope = new AsyncLocal<LogFields>();
        private readonly Dictionary<string, RepeatedWarning> warnings = new Dictionary<string, RepeatedWarning>();
        private readonly Timer maintenanceTimer;
        private bool disposed;

        public ServiceLogger(LogOptions options)
            : this(options, () => DateTime.Now)
        {
            maintenanceTimer = new Timer(_ => Maintain(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        internal ServiceLogger(LogOptions options, Func<DateTime> clock)
        {
            this.options = options ?? new LogOptions();
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            daily = new RollingLogFile(this.options.LogDirectory, "ControlDoor-", this.options.RetentionDays,
                this.options.MaxFileSizeMB * 1024L * 1024, this.options.MaxTotalSizeMB * 1024L * 1024);
            diagnostic = new RollingLogFile(Path.Combine(this.options.LogDirectory, "diagnostic"), "ControlDoor-diagnostic-",
                this.options.DiagnosticRetentionDays, this.options.MaxFileSizeMB * 1024L * 1024,
                this.options.DiagnosticMaxTotalSizeMB * 1024L * 1024);
            daily.Initialize(clock());
            diagnostic.Initialize(clock());
        }

        public string CurrentLogPath
        {
            get
            {
                lock (gate)
                {
                    return daily.CurrentPath(clock());
                }
            }
        }

        public string CurrentDiagnosticLogPath
        {
            get
            {
                lock (gate)
                {
                    return diagnostic.CurrentPath(clock());
                }
            }
        }
        public int SlowOperationThresholdMs => options.SlowOperationThresholdMs;
        internal LogOptions Options => options;
        internal int? CurrentScopeDeviceId => scope.Value?.DeviceId;
        public bool IsSlowOperation(long elapsedMs) => elapsedMs >= options.SlowOperationThresholdMs;

        public IDisposable BeginScope(LogFields fields)
        {
            var previous = scope.Value;
            scope.Value = LogFields.Merge(previous, fields);
            return new LogScope(() => scope.Value = previous);
        }

        public void Debug(string component, string message, LogFields fields = null) => Write(LogLevel.Debug, component, message, fields);
        public void Info(string component, string message, LogFields fields = null) => Write(LogLevel.Info, component, message, fields);
        public void Warn(string component, string message, LogFields fields = null) => Write(LogLevel.Warn, component, message, fields);

        public void Error(string component, string message, Exception exception = null, LogFields fields = null)
        {
            var copy = LogFields.Merge(null, fields);
            if (exception != null)
            {
                copy.Exception = exception.ToString();
                copy.Extra["errorMessage"] = exception.Message;
            }
            Write(LogLevel.Error, component, message, copy);
        }

        public void LogPayload(string component, RequestContext context, string payloadJson, LogOptions logOptions = null)
        {
            var payloadOptions = logOptions ?? options;
            if (!payloadOptions.EnableGrpcPayloadLogging)
            {
                return;
            }
            Debug(component, "接口报文。", new LogFields
            {
                RequestId = context?.RequestId, TraceId = context?.TraceId, OperationName = context?.MethodName,
                Extra = { ["payload"] = new PayloadLogFormatter().Format(payloadJson, payloadOptions) }
            });
        }

        public void Write(LogLevel level, string component, string message, LogFields fields = null)
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }
                var now = clock();
                FlushWarnings(now);
                WriteCore(now, level, component, message, LogFields.Merge(scope.Value, fields));
            }
        }

        public void WarnRepeated(string component, string message, LogFields fields)
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }
                var now = clock();
                FlushWarnings(now);
                var merged = LogFields.Merge(scope.Value, fields);
                var key = WarningKey(component, merged.DeviceId, merged.OperationName);
                RepeatedWarning warning;
                var first = !warnings.TryGetValue(key, out warning) || warning.Fields.ErrorCode != merged.ErrorCode;
                if (first)
                {
                    if (warning != null)
                    {
                        FlushWarning(now, warning);
                    }
                    if (warnings.Count >= 2048)
                    {
                        var oldest = warnings.OrderBy(pair => pair.Value.LastSeen).First();
                        FlushWarning(now, oldest.Value);
                        warnings.Remove(oldest.Key);
                    }
                    warnings[key] = new RepeatedWarning { Component = component, Message = message, Fields = merged, LastReport = now, LastSeen = now };
                }
                else
                {
                    warning.Count++;
                    warning.LastSeen = now;
                    warning.Fields = merged;
                    warning.Message = message;
                }
                WriteCore(now, LogLevel.Warn, component, message, merged, includeDaily: first);
            }
        }

        public bool ClearRepeatedWarnings(string component, int? deviceId, string operationName)
        {
            lock (gate)
            {
                if (disposed)
                {
                    return false;
                }
                var key = WarningKey(component, deviceId, operationName);
                RepeatedWarning warning;
                if (!warnings.TryGetValue(key, out warning))
                {
                    return false;
                }
                FlushWarning(clock(), warning);
                warnings.Remove(key);
                return true;
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }
                foreach (var warning in warnings.Values)
                {
                    FlushWarning(clock(), warning);
                }
                warnings.Clear();
                disposed = true;
                maintenanceTimer?.Dispose();
            }
        }

        internal void Maintain()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }
                var now = clock();
                FlushWarnings(now);
                daily.Maintain(now);
                diagnostic.Maintain(now);
            }
        }

        private void WriteCore(DateTime now, LogLevel level, string component, string message, LogFields fields, bool includeDaily = true)
        {
            if (level >= options.DiagnosticMinimumLevel)
            {
                diagnostic.Append(now, FormatDiagnostic(now, level, component, message, fields));
            }
            if (includeDaily && level >= options.MinimumLevel)
            {
                var line = FormatDaily(now, level, component, message, fields);
                daily.Append(now, line);
                if (options.MirrorToConsole)
                {
                    try
                    {
                        Console.WriteLine(line);
                    }
                    catch (IOException)
                    {
                    }
                }
            }
        }

        private void FlushWarnings(DateTime now)
        {
            foreach (var pair in warnings.ToArray())
            {
                if (now - pair.Value.LastReport >= TimeSpan.FromSeconds(60))
                {
                    FlushWarning(now, pair.Value);
                }
                if (now - pair.Value.LastSeen > TimeSpan.FromDays(1))
                {
                    warnings.Remove(pair.Key);
                }
            }
        }

        private void FlushWarning(DateTime now, RepeatedWarning warning)
        {
            if (warning.Count == 0)
            {
                return;
            }
            var fields = LogFields.Merge(null, warning.Fields);
            fields.Extra["repeatCount"] = warning.Count.ToString();
            WriteCore(now, LogLevel.Warn, warning.Component, "重复告警汇总：" + warning.Message, fields);
            warning.Count = 0;
            warning.LastReport = now;
        }

        private static string WarningKey(string component, int? deviceId, string operationName)
            => component + "|" + deviceId + "|" + operationName;

        private static string FormatDaily(DateTime now, LogLevel level, string component, string message, LogFields fields)
        {
            var details = new List<string>();
            AddDaily(details, "模块", component);
            AddDaily(details, "设备", fields.DeviceId?.ToString());
            AddDaily(details, "员工", fields.EmployeeId);
            AddDaily(details, "操作", fields.OperationName);
            AddDaily(details, "耗时", fields.ElapsedMs.HasValue ? fields.ElapsedMs + "ms" : null);
            if (fields.ErrorCode != "OK")
            {
                AddDaily(details, "错误码", fields.ErrorCode);
            }
            foreach (var pair in fields.Extra)
            {
                string label;
                if (DailyLabels.TryGetValue(pair.Key, out label))
                {
                    AddDaily(details, label, pair.Value);
                }
            }
            AddDaily(details, "requestId", fields.RequestId);
            return now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level.ToString().ToUpperInvariant() + "] " + SingleLine(message)
                + (details.Count == 0 ? string.Empty : " | " + string.Join(" ", details));
        }

        private static string FormatDiagnostic(DateTime now, LogLevel level, string component, string message, LogFields fields)
        {
            var parts = new List<string>
            {
                "timestamp=" + now.ToString("yyyy-MM-dd HH:mm:ss.fff"), "level=" + level,
                "component=" + Escape(component), "message=" + Escape(message)
            };
            Add(parts, "requestId", fields.RequestId);
            Add(parts, "traceId", fields.TraceId);
            Add(parts, "deviceId", fields.DeviceId?.ToString());
            Add(parts, "employeeId", fields.EmployeeId);
            Add(parts, "operationName", fields.OperationName);
            Add(parts, "elapsedMs", fields.ElapsedMs?.ToString());
            Add(parts, "errorCode", fields.ErrorCode);
            Add(parts, "exception", fields.Exception);
            foreach (var pair in fields.Extra)
            {
                var key = string.IsNullOrWhiteSpace(pair.Key) ? "extra" : pair.Key.Trim();
                Add(parts, ReservedFieldNames.Contains(key) ? "extra_" + key : key, pair.Value);
            }
            return string.Join(" ", parts);
        }

        private static void Add(ICollection<string> parts, string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add(key + "=" + Escape(value));
            }
        }

        private static void AddDaily(ICollection<string> parts, string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                var text = SingleLine(value);
                parts.Add(key + "=" + (text.Length <= 256 ? text : text.Substring(0, 256) + "..."));
            }
        }

        private static string SingleLine(string value) => (value ?? string.Empty).Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ");
        private static string Escape(string value) => "\"" + SingleLine(value).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private sealed class LogScope : IDisposable
        {
            private Action restore;
            internal LogScope(Action restore)
            {
                this.restore = restore;
            }
            public void Dispose()
            {
                Interlocked.Exchange(ref restore, null)?.Invoke();
            }
        }

        private sealed class RepeatedWarning
        {
            internal string Component;
            internal string Message;
            internal LogFields Fields;
            internal DateTime LastReport;
            internal DateTime LastSeen;
            internal int Count;
        }
    }
}
