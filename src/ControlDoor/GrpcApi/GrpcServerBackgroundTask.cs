using System.Collections.Generic;
using System.Threading.Tasks;
using Grpc.Core;
using ControlDoor.Runtime;

namespace ControlDoor.GrpcApi
{
    public sealed class GrpcServerBackgroundTask : IBackgroundTask
    {
        // grpc C-core 默认单条消息接收上限为 4 MiB，会先于业务校验拒绝合法的人脸批量请求（K2）；
        // 服务端必须显式设置受控上限，并与业务层 MaxBatchFaceBytes 预算保持"传输 > 业务"的关系。
        // 生产配置由 ConfigurationValidator 限制在 1 MiB 至 64 MiB，这里接受任何正值以便测试注入小限额。
        private readonly int port;
        private readonly AccessControlGrpcService accessControlService;
        private readonly PermissionSyncGrpcService permissionSyncService;
        private readonly int maxReceiveMessageBytes;
        private readonly BackgroundTaskStatus status = new BackgroundTaskStatus("GrpcServer", true);
        private Server server;

        public GrpcServerBackgroundTask(int port, AccessControlGrpcService accessControlService, PermissionSyncGrpcService permissionSyncService = null, int maxReceiveMessageBytes = 0)
        {
            this.port = port;
            this.accessControlService = accessControlService;
            this.permissionSyncService = permissionSyncService;
            this.maxReceiveMessageBytes = maxReceiveMessageBytes;
        }

        public string Name => "GrpcServer";

        public bool IsCritical => true;

        public Task StartAsync(BackgroundTaskContext context)
        {
            status.MarkStarting();
            if (maxReceiveMessageBytes > 0)
            {
                server = new Server(new[] { new ChannelOption(ChannelOptions.MaxReceiveMessageLength, maxReceiveMessageBytes) })
                {
                    Ports = { new ServerPort("0.0.0.0", port, ServerCredentials.Insecure) }
                };
            }
            else
            {
                server = new Server
                {
                    Ports = { new ServerPort("0.0.0.0", port, ServerCredentials.Insecure) }
                };
            }

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
