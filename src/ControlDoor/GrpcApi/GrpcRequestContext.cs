using System.Collections.Generic;
using System.Threading;

namespace ControlDoor.GrpcApi
{
    public sealed class GrpcRequestContext
    {
        public string RequestId { get; set; } = string.Empty;

        public string CorrelationId { get; set; } = string.Empty;

        public IDictionary<string, string> Metadata { get; } = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);

        public CancellationToken CancellationToken { get; set; }

        public static GrpcRequestContext Empty()
        {
            return new GrpcRequestContext();
        }
    }
}
