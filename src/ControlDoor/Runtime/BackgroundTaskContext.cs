using System;
using System.Threading;
using ControlDoor.Observability;

namespace ControlDoor.Runtime
{
    public sealed class BackgroundTaskContext
    {
        public BackgroundTaskContext(string requestId, CancellationToken cancellationToken, ServiceLogger logger)
        {
            RequestId = string.IsNullOrWhiteSpace(requestId) ? RequestContext.Background("BackgroundTask").RequestId : requestId;
            CancellationToken = cancellationToken;
            Logger = logger;
            StartedAt = DateTime.Now;
        }

        public string RequestId { get; }

        public CancellationToken CancellationToken { get; }

        public ServiceLogger Logger { get; }

        public DateTime StartedAt { get; }
    }
}
