using System;
using System.Net;
using System.Net.Sockets;

namespace ControlDoor.Runtime.Health.Checks
{
    public sealed class PortHealthCheck : IHealthCheck
    {
        public string Name => "gRPC 端口";

        public HealthCheckResult Run(HealthCheckContext context)
        {
            var port = context.Settings.Service.GrpcListenPort;
            if (port < 1 || port > 65535)
            {
                return HealthCheckResult.Failed(Name, "端口不在 1-65535 范围内: " + port);
            }

            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                listener.Stop();
                return HealthCheckResult.Ok(Name, "端口可绑定: " + port);
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Failed(Name, "端口不可用 " + port + ": " + ex.Message);
            }
        }
    }
}
