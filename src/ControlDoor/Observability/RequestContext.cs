using System;
using System.Collections.Generic;
using System.Linq;

namespace ControlDoor.Observability
{
    public sealed class RequestContext
    {
        public RequestContext(string requestId, string traceId, string methodName = "", string source = "")
        {
            RequestId = string.IsNullOrWhiteSpace(requestId) ? NewRequestId("req") : requestId.Trim();
            TraceId = string.IsNullOrWhiteSpace(traceId) ? RequestId : traceId.Trim();
            MethodName = methodName ?? string.Empty;
            Source = source ?? string.Empty;
        }

        public string RequestId { get; }

        public string TraceId { get; }

        public string MethodName { get; }

        public string Source { get; }

        public static RequestContext FromMetadata(IDictionary<string, string> metadata, string methodName)
        {
            metadata = metadata ?? new Dictionary<string, string>();
            var requestId = GetFirst(metadata, "x-request-id", "x-correlation-id", "x-trace-id");
            var traceId = GetFirst(metadata, "x-trace-id", "x-correlation-id", "x-request-id");
            return new RequestContext(requestId, traceId, methodName, "grpc");
        }

        public static RequestContext Background(string taskName)
        {
            return new RequestContext(NewRequestId("bg"), null, taskName, "background");
        }

        public static RequestContext Event(string source, string serial)
        {
            var suffix = string.IsNullOrWhiteSpace(serial) ? ShortRandom() : serial.Trim();
            return new RequestContext("evt-" + source + "-" + suffix, null, source, "sdk-callback");
        }

        private static string GetFirst(IDictionary<string, string> metadata, params string[] names)
        {
            foreach (var name in names)
            {
                var match = metadata.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(match.Value))
                {
                    return match.Value;
                }
            }

            return null;
        }

        private static string NewRequestId(string prefix)
        {
            return prefix + "-" + DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + ShortRandom();
        }

        private static string ShortRandom()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 8);
        }
    }
}
