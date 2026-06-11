using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Observability;

namespace ControlDoor.Host
{
    public sealed class ServiceLifecycleController
    {
        private readonly IControlDoorHost host;
        private readonly ServiceLogger logger;
        private readonly IServiceControlStatusReporter reporter;
        private readonly object gate = new object();
        private ServiceLifecycleState state = ServiceLifecycleState.Created;

        public ServiceLifecycleController(IControlDoorHost host, ServiceLogger logger = null, IServiceControlStatusReporter reporter = null)
        {
            this.host = host ?? throw new ArgumentNullException(nameof(host));
            this.logger = logger;
            this.reporter = reporter ?? new NoopServiceControlStatusReporter();
        }

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

        public async Task<HostStartupResult> StartAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                if (state == ServiceLifecycleState.Running || state == ServiceLifecycleState.Starting)
                {
                    return HostStartupResult.Succeeded("服务已启动或正在启动。");
                }

                state = ServiceLifecycleState.Starting;
            }

            var stopwatch = Stopwatch.StartNew();
            logger?.Info("ServiceLifecycle", "服务启动请求。");
            reporter.ReportPending(ServiceLifecycleState.Starting, timeout);

            try
            {
                using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    var startTask = host.StartAsync(timeoutSource.Token);
                    var completed = await Task.WhenAny(startTask, Task.Delay(timeout, timeoutSource.Token)).ConfigureAwait(false);
                    if (completed != startTask)
                    {
                        timeoutSource.Cancel();
                        SetState(ServiceLifecycleState.Failed);
                        logger?.Error("ServiceLifecycle", "服务启动超时。", null, new LogFields { ElapsedMs = stopwatch.ElapsedMilliseconds });
                        await StopBestEffortAsync("StartTimeout", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                        return HostStartupResult.Failed("服务启动超时。", new[] { "Host 启动超过 " + timeout.TotalMilliseconds + "ms。" });
                    }

                    var result = await startTask.ConfigureAwait(false);
                    SetState(result.Success ? ServiceLifecycleState.Running : ServiceLifecycleState.Failed);
                    logger?.Info("ServiceLifecycle", result.Success ? "服务启动完成。" : "服务启动失败。", new LogFields { ElapsedMs = stopwatch.ElapsedMilliseconds });
                    return result;
                }
            }
            catch (Exception ex)
            {
                SetState(ServiceLifecycleState.Failed);
                logger?.Error("ServiceLifecycle", "服务启动异常。", ex, new LogFields { ElapsedMs = stopwatch.ElapsedMilliseconds });
                await StopBestEffortAsync("StartException", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                return HostStartupResult.Failed("服务启动异常。", new[] { ex.Message });
            }
        }

        public async Task<HostStopResult> StopAsync(string reason, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                if (state == ServiceLifecycleState.Stopped || state == ServiceLifecycleState.Created)
                {
                    state = ServiceLifecycleState.Stopped;
                    return HostStopResult.Succeeded(reason, "服务已停止。");
                }

                state = ServiceLifecycleState.Stopping;
            }

            var stopwatch = Stopwatch.StartNew();
            logger?.Info("ServiceLifecycle", "服务停止请求。", new LogFields { Extra = { ["reason"] = reason ?? string.Empty } });
            reporter.ReportPending(ServiceLifecycleState.Stopping, timeout);

            try
            {
                using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    var stopTask = host.StopAsync(reason, timeoutSource.Token);
                    var completed = await Task.WhenAny(stopTask, Task.Delay(timeout, timeoutSource.Token)).ConfigureAwait(false);
                    if (completed != stopTask)
                    {
                        timeoutSource.Cancel();
                        SetState(ServiceLifecycleState.Stopped);
                        logger?.Warn("ServiceLifecycle", "服务停止超时，继续释放可释放资源。", new LogFields { ElapsedMs = stopwatch.ElapsedMilliseconds });
                        return HostStopResult.Failed(reason, "服务停止超时。");
                    }

                    var result = await stopTask.ConfigureAwait(false);
                    SetState(ServiceLifecycleState.Stopped);
                    logger?.Info("ServiceLifecycle", "服务停止完成。", new LogFields { ElapsedMs = stopwatch.ElapsedMilliseconds });
                    return result;
                }
            }
            catch (Exception ex)
            {
                SetState(ServiceLifecycleState.Stopped);
                logger?.Error("ServiceLifecycle", "服务停止异常。", ex, new LogFields { ElapsedMs = stopwatch.ElapsedMilliseconds });
                return HostStopResult.Failed(reason, ex.Message);
            }
        }

        public Task<HostStopResult> ShutdownAsync()
        {
            return StopAsync("Shutdown", TimeSpan.FromMilliseconds(30000));
        }

        private async Task StopBestEffortAsync(string reason, TimeSpan timeout)
        {
            try
            {
                await host.StopAsync(reason, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.Error("ServiceLifecycle", "启动失败后的兜底停止异常。", ex);
            }
        }

        private void SetState(ServiceLifecycleState nextState)
        {
            lock (gate)
            {
                state = nextState;
            }
        }
    }
}
