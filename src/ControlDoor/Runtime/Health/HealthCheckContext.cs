using System.Threading;
using ControlDoor.Configuration;
using ControlDoor.Observability;

namespace ControlDoor.Runtime.Health
{
    public sealed class HealthCheckContext
    {
        public HealthCheckContext(string runDirectory, AppSettings settings, ServiceLogger logger, CancellationToken cancellationToken)
        {
            RunDirectory = runDirectory;
            Settings = settings;
            Logger = logger;
            CancellationToken = cancellationToken;
        }

        public string RunDirectory { get; }

        public AppSettings Settings { get; }

        public ServiceLogger Logger { get; }

        public CancellationToken CancellationToken { get; }
    }
}
