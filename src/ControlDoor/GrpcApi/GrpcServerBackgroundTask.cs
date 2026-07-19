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
            // 500 人+ 人脸契约（含大图 base64）单条消息可超过默认 4MB 上限；统一放宽到 100MB 以匹配批量下发与抓拍回传。
            // Grpc.Core 2.46 的 Server 没有公开 MaxReceive/MaxSend 属性，需要通过 ChannelOption 在构造时传入。
            const int MaxMessageLengthBytes = 100 * 1024 * 1024;
            server = new Server(new[]
            {
                new ChannelOption(ChannelOptions.MaxReceiveMessageLength, MaxMessageLengthBytes),
                new ChannelOption(ChannelOptions.MaxSendMessageLength, MaxMessageLengthBytes)
            })
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
