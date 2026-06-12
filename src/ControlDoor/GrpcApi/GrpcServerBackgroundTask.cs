using System.Threading.Tasks;
using Grpc.Core;
using ControlDoor.Runtime;

namespace ControlDoor.GrpcApi
{
    public sealed class GrpcServerBackgroundTask : IBackgroundTask
    {
        private readonly int port;
        private readonly AccessControlGrpcService accessControlService;
        private readonly PermissionSyncGrpcService permissionSyncService;
        private readonly BackgroundTaskStatus status = new BackgroundTaskStatus("GrpcServer", true);
        private Server server;

        public GrpcServerBackgroundTask(int port, AccessControlGrpcService accessControlService, PermissionSyncGrpcService permissionSyncService = null)
        {
            this.port = port;
            this.accessControlService = accessControlService;
            this.permissionSyncService = permissionSyncService;
        }

        public string Name => "GrpcServer";

        public bool IsCritical => true;

        public Task StartAsync(BackgroundTaskContext context)
        {
            status.MarkStarting();
            server = new Server
            {
                Ports = { new ServerPort("0.0.0.0", port, ServerCredentials.Insecure) }
            };
            server.Services.Add(new AccessControlGrpcBinder(accessControlService).Bind());
            if (permissionSyncService != null)
            {
                server.Services.Add(new PermissionSyncGrpcBinder(permissionSyncService).Bind());
            }

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
