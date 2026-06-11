using System;
using System.Threading;
using System.Threading.Tasks;

namespace ControlDoor.Host
{
    public sealed class ControlDoorHost : IControlDoorHost
    {
        private readonly object gate = new object();
        private ServiceLifecycleState state = ServiceLifecycleState.Created;
        private bool disposed;

        public ServiceLifecycleState State
        {
            get
            {
                lock (gate)
                {
                    return state;
                }
            }
        }

        public Task<HostStartupResult> StartAsync(CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                ThrowIfDisposed();

                if (state == ServiceLifecycleState.Running)
                {
                    return Task.FromResult(HostStartupResult.Succeeded("Host 已在运行。"));
                }

                if (state == ServiceLifecycleState.Starting)
                {
                    return Task.FromResult(HostStartupResult.Succeeded("Host 正在启动。"));
                }

                state = ServiceLifecycleState.Starting;
            }

            cancellationToken.ThrowIfCancellationRequested();

            lock (gate)
            {
                state = ServiceLifecycleState.Running;
            }

            return Task.FromResult(HostStartupResult.Succeeded("Host 启动成功。"));
        }

        public Task<HostStopResult> StopAsync(string reason = "Manual", CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                if (disposed)
                {
                    return Task.FromResult(HostStopResult.Succeeded(reason, "Host 已释放。"));
                }

                if (state == ServiceLifecycleState.Stopped || state == ServiceLifecycleState.Created)
                {
                    state = ServiceLifecycleState.Stopped;
                    return Task.FromResult(HostStopResult.Succeeded(reason, "Host 已停止。"));
                }

                state = ServiceLifecycleState.Stopping;
            }

            cancellationToken.ThrowIfCancellationRequested();

            lock (gate)
            {
                state = ServiceLifecycleState.Stopped;
            }

            return Task.FromResult(HostStopResult.Succeeded(reason, "Host 停止成功。"));
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                state = ServiceLifecycleState.Stopped;
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(ControlDoorHost));
            }
        }
    }
}
