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
            CancellationTokenSource source;
            Task task;
            source = stopSource;
            CancelStopSource(source);
            task = loopTask;
            if (task != null)
            {
                await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            }

            if (object.ReferenceEquals(stopSource, source))
            {
                stopSource = null;
            }

            DisposeStopSource(source);
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

                    SelfHealStuckReconnects();
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

        // 补排幂等：ScheduleReconnect 按 taskKey 合并，正常路径的既有重连任务不会被重复创建。
        private void SelfHealStuckReconnects()
        {
            var grace = TimeSpan.FromMilliseconds(Math.Max(0, options.ReconnectSelfHealGraceMs));
            var now = DateTime.Now;
            foreach (var snapshot in lifecycle.GetDeviceSnapshots(includeDisabled: false)
                .Where(item => item.Status == DeviceConnectionStatus.ReconnectPending)
                .ToList())
            {
                var nextReconnectAt = snapshot.Reconnect.NextReconnectAt;
                if (!nextReconnectAt.HasValue || nextReconnectAt.Value.Add(grace) < now)
                {
                    lifecycle.EnsureReconnectScheduled(snapshot.DeviceId);
                }
            }
        }

        private static void CancelStopSource(CancellationTokenSource source)
        {
            if (source == null)
            {
                return;
            }

            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static void DisposeStopSource(CancellationTokenSource source)
        {
            if (source == null)
            {
                return;
            }

            try
            {
                source.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
