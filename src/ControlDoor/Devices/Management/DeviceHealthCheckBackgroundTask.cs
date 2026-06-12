using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Devices.Runtime;
using ControlDoor.Runtime;

namespace ControlDoor.Devices.Management
{
    public sealed class DeviceHealthCheckBackgroundTask : IBackgroundTask
    {
        private readonly DeviceLifecycleService lifecycle;
        private readonly DeviceLifecycleOptions options;
        private readonly BackgroundTaskStatus status = new BackgroundTaskStatus("DeviceHealthCheckBackgroundTask", false);
        private CancellationTokenSource stopSource;
        private Task loopTask;

        public DeviceHealthCheckBackgroundTask(DeviceLifecycleService lifecycle, DeviceLifecycleOptions options)
        {
            this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
            this.options = options ?? new DeviceLifecycleOptions();
        }

        public string Name => "DeviceHealthCheckBackgroundTask";

        public bool IsCritical => false;

        public Task StartAsync(BackgroundTaskContext context)
        {
            status.MarkStarting();
            stopSource = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            loopTask = Task.Run(() => RunLoop(stopSource.Token));
            status.MarkStarted();
            return Task.CompletedTask;
        }

        public async Task StopAsync(BackgroundTaskContext context)
        {
            stopSource?.Cancel();
            var task = loopTask;
            if (task != null)
            {
                await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            }

            status.MarkStopped();
        }

        public BackgroundTaskStatus GetStatus()
        {
            return status.Clone();
        }

        private async Task RunLoop(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    foreach (var snapshot in lifecycle.GetDeviceSnapshots(includeDisabled: false)
                        .Where(item => item.Status == DeviceConnectionStatus.Online || item.Status == DeviceConnectionStatus.Degraded)
                        .ToList())
                    {
                        lifecycle.SubmitHealthCheck(snapshot.DeviceId, wait: false, requestId: string.Empty);
                    }
                }
                catch (Exception ex)
                {
                    status.MarkFailed(ex);
                }

                try
                {
                    await Task.Delay(Math.Max(1000, options.HealthCheckIntervalMs), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
