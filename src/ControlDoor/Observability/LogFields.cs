using System.Collections.Generic;

namespace ControlDoor.Observability
{
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
        public IDictionary<string, string> Extra { get; } = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);

        internal static LogFields Merge(LogFields parent, LogFields fields)
        {
            var result = new LogFields
            {
                RequestId = fields?.RequestId ?? parent?.RequestId,
                TraceId = fields?.TraceId ?? parent?.TraceId,
                DeviceId = fields?.DeviceId ?? parent?.DeviceId,
                EmployeeId = fields?.EmployeeId ?? parent?.EmployeeId,
                OperationName = fields?.OperationName ?? parent?.OperationName,
                ElapsedMs = fields?.ElapsedMs ?? parent?.ElapsedMs,
                ErrorCode = fields?.ErrorCode ?? parent?.ErrorCode,
                Exception = fields?.Exception ?? parent?.Exception
            };
            if (parent != null)
            {
                foreach (var pair in parent.Extra)
                {
                    result.Extra[pair.Key] = pair.Value;
                }
            }
            if (fields != null)
            {
                foreach (var pair in fields.Extra)
                {
                    result.Extra[pair.Key] = pair.Value;
                }
            }
            return result;
        }
    }
}
