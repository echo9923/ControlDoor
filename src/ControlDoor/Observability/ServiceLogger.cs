using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace ControlDoor.Observability
{
    public sealed class ServiceLogger : IDisposable
    {
        private static readonly ISet<string> ReservedFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "timestamp",
            "level",
            "component",
            "message",
            "requestId",
            "traceId",
            "deviceId",
            "employeeId",
            "operationName",
            "elapsedMs",
            "errorCode",
            "exception"
        };

        private readonly object gate = new object();
        private readonly LogOptions options;
        private bool disposed;
        private int writeFailureCount;

        public ServiceLogger(LogOptions options)
        {
            this.options = options ?? new LogOptions();
            Directory.CreateDirectory(this.options.LogDirectory);
            CleanOldLogs();
        }

        public string CurrentLogPath => Path.Combine(options.LogDirectory, "ControlDoor-" + DateTime.Now.ToString("yyyyMMdd") + ".log");

        public int SlowOperationThresholdMs => options.SlowOperationThresholdMs;

        public bool IsSlowOperation(long elapsedMs)
        {
            return elapsedMs >= options.SlowOperationThresholdMs;
        }

        public void Debug(string component, string message, LogFields fields = null)
        {
            Write(LogLevel.Debug, component, message, fields);
        }

        public void Info(string component, string message, LogFields fields = null)
        {
            Write(LogLevel.Info, component, message, fields);
        }

        public void Warn(string component, string message, LogFields fields = null)
        {
            Write(LogLevel.Warn, component, message, fields);
        }

        public void Error(string component, string message, Exception exception = null, LogFields fields = null)
        {
            fields = fields ?? new LogFields();
            if (exception != null)
            {
                fields.Exception = exception.GetType().Name + ": " + exception.Message;
            }

            Write(LogLevel.Error, component, message, fields);
        }

        public void LogPayload(string component, RequestContext context, string payloadJson, LogOptions logOptions = null)
        {
            var formatter = new PayloadLogFormatter();
            Info(component, formatter.Format(payloadJson, logOptions ?? options), new LogFields
            {
                RequestId = context?.RequestId,
                TraceId = context?.TraceId,
                OperationName = context?.MethodName
            });
        }

        public void Write(LogLevel level, string component, string message, LogFields fields = null)
        {
            if (disposed)
            {
                return;
            }

            if (level < options.MinimumLevel)
            {
                return;
            }

            fields = fields ?? new LogFields();
            var line = FormatLine(level, component, message, fields);

            try
            {
                lock (gate)
                {
                    File.AppendAllText(CurrentLogPath, line + Environment.NewLine, Encoding.UTF8);
                    writeFailureCount = 0;
                }
            }
            catch (Exception ex)
            {
                writeFailureCount++;
                if (options.MirrorToConsole || writeFailureCount == 1)
                {
                    Console.Error.WriteLine("日志写入失败: " + ex.Message);
                    Console.Error.WriteLine(line);
                }
            }

            if (options.MirrorToConsole)
            {
                Console.WriteLine(line);
            }
        }

        public void Dispose()
        {
            disposed = true;
        }

        private string FormatLine(LogLevel level, string component, string message, LogFields fields)
        {
            var parts = new List<string>
            {
                "timestamp=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                "level=" + level,
                "component=" + Escape(component),
                "message=" + Escape(message)
            };

            Add(parts, "requestId", fields.RequestId);
            Add(parts, "traceId", fields.TraceId);
            Add(parts, "deviceId", fields.DeviceId?.ToString());
            Add(parts, "employeeId", fields.EmployeeId);
            Add(parts, "operationName", fields.OperationName);
            Add(parts, "elapsedMs", fields.ElapsedMs?.ToString());
            Add(parts, "errorCode", fields.ErrorCode);
            Add(parts, "exception", fields.Exception);

            foreach (var extra in fields.Extra)
            {
                Add(parts, NormalizeExtraKey(extra.Key), extra.Value);
            }

            return string.Join(" ", parts);
        }

        private void CleanOldLogs()
        {
            if (options.RetentionDays < 1)
            {
                return;
            }

            try
            {
                var cutoff = DateTime.Now.Date.AddDays(-options.RetentionDays);
                foreach (var file in Directory.GetFiles(options.LogDirectory, "ControlDoor-*.log"))
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTime.Date < cutoff)
                    {
                        info.Delete();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("日志清理失败: " + ex.Message);
            }
        }

        private static void Add(ICollection<string> parts, string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add(key + "=" + Escape(value));
            }
        }

        private static string NormalizeExtraKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return "extra";
            }

            var trimmed = key.Trim();
            return ReservedFieldNames.Contains(trimmed) ? "extra_" + trimmed : trimmed;
        }

        private static string Escape(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace(Environment.NewLine, " ") + "\"";
        }
    }

    public sealed class LogFields
    {
        public string RequestId { get; set; }

        public string TraceId { get; set; }

        public int? DeviceId { get; set; }

        public string EmployeeId { get; set; }

        public string OperationName { get; set; }

        public long? ElapsedMs { get; set; }

        public string ErrorCode { get; set; }

        public string Exception { get; set; }

        public IDictionary<string, string> Extra { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
