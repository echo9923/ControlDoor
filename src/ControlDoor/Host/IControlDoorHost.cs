using System;
using System.Threading;
using System.Threading.Tasks;

namespace ControlDoor.Host
{
    public interface IControlDoorHost : IDisposable
    {
        ServiceLifecycleState State { get; }

        Task<HostStartupResult> StartAsync(CancellationToken cancellationToken = default);

        Task<HostStopResult> StopAsync(string reason = "Manual", CancellationToken cancellationToken = default);
    }
}
