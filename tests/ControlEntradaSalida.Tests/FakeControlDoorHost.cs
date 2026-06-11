using System;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Host;

namespace ControlEntradaSalida.Tests
{
    public sealed class FakeControlDoorHost : IControlDoorHost
    {
        public ServiceLifecycleState State { get; private set; } = ServiceLifecycleState.Created;

        public bool FailStart { get; set; }

        public bool ThrowOnStop { get; set; }

        public TimeSpan StartDelay { get; set; }

        public TimeSpan StopDelay { get; set; }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public async Task<HostStartupResult> StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            State = ServiceLifecycleState.Starting;
            if (StartDelay > TimeSpan.Zero)
            {
                await Task.Delay(StartDelay, cancellationToken).ConfigureAwait(false);
            }

            if (FailStart)
            {
                State = ServiceLifecycleState.Failed;
                return HostStartupResult.Failed("failed", new[] { "failed" });
            }

            State = ServiceLifecycleState.Running;
            return HostStartupResult.Succeeded("started");
        }

        public async Task<HostStopResult> StopAsync(string reason = "Manual", CancellationToken cancellationToken = default)
        {
            StopCount++;
            State = ServiceLifecycleState.Stopping;
            if (StopDelay > TimeSpan.Zero)
            {
                await Task.Delay(StopDelay, cancellationToken).ConfigureAwait(false);
            }

            if (ThrowOnStop)
            {
                throw new InvalidOperationException("stop failed");
            }

            State = ServiceLifecycleState.Stopped;
            return HostStopResult.Succeeded(reason, "stopped");
        }

        public void Dispose()
        {
            State = ServiceLifecycleState.Stopped;
        }
    }
}
