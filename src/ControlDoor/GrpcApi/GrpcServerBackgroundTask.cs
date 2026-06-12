using System.Threading.Tasks;
using Grpc.Core;
using ControlDoor.Runtime;

namespace ControlDoor.GrpcApi
{
    public sealed class GrpcServerBackgroundTask : IBackgroundTask
    {
        private readonly int port;
        private readonly AccessControlGrpcService accessControlService;
        private readonly BackgroundTaskStatus status = new BackgroundTaskStatus("GrpcServer", true);
        private Server server;

        public GrpcServerBackgroundTask(int port, AccessControlGrpcService accessControlService)
        {
            this.port = port;
            this.accessControlService = accessControlService;
        }

        public string Name => "GrpcServer";

        public bool IsCritical => true;

        public Task StartAsync(BackgroundTaskContext context)
        {
            status.MarkStarting();
            server = new Server
            {
                Services = { new AccessControlGrpcBinder(accessControlService).Bind() },
                Ports = { new ServerPort("0.0.0.0", port, ServerCredentials.Insecure) }
            };
            server.Start();
            status.MarkStarted();
            return Task.CompletedTask;
        }

        public async Task StopAsync(BackgroundTaskContext context)
        {
            if (server != null)
            {
                await server.ShutdownAsync().ConfigureAwait(false);
                server = null;
            }

            status.MarkStopped();
        }

        public BackgroundTaskStatus GetStatus()
        {
            return status.Clone();
        }
    }
}
