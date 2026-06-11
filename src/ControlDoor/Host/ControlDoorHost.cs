using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Configuration;
using ControlDoor.Database;
using ControlDoor.Observability;
using ControlDoor.Runtime;

namespace ControlDoor.Host
{
    public sealed class ControlDoorHost : IControlDoorHost
    {
        private readonly object gate = new object();
        private readonly string runDirectory;
        private ServiceLifecycleState state = ServiceLifecycleState.Created;
        private bool disposed;
        private AppSettings settings;
        private ServiceLogger logger;
        private SqlServerDatabase database;
        private BackgroundTaskHost backgroundTaskHost;

        public ControlDoorHost()
            : this(RuntimePaths.GetRunDirectory())
        {
        }

        public ControlDoorHost(string runDirectory)
        {
            this.runDirectory = string.IsNullOrWhiteSpace(runDirectory) ? RuntimePaths.GetRunDirectory() : runDirectory;
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

            var stopwatch = Stopwatch.StartNew();
            cancellationToken.ThrowIfCancellationRequested();

            var configurationResult = new ConfigurationLoader().Load(runDirectory);
            if (!configurationResult.Success)
            {
                lock (gate)
                {
                    state = ServiceLifecycleState.Failed;
                }

                return Task.FromResult(HostStartupResult.Failed("配置加载失败。", configurationResult.Errors));
            }

            settings = configurationResult.Settings;
            logger = new ServiceLogger(LogOptions.FromSettings(runDirectory, settings.Logging, mirrorToConsole: false));
            logger.Info("Host", "ControlDoor Host 正在启动。", new LogFields
            {
                Extra =
                {
                    ["runDirectory"] = runDirectory,
                    ["configPath"] = configurationResult.ConfigPath,
                    ["grpcPort"] = settings.Service.GrpcListenPort.ToString(),
                    ["workerCount"] = settings.DeviceSdkDispatcher.WorkerCount.ToString()
                }
            });

            foreach (var warning in configurationResult.Warnings)
            {
                logger.Warn("Configuration", warning);
            }

            database = new SqlServerDatabase(settings.Database, logger);
            backgroundTaskHost = new BackgroundTaskHost(logger);
            backgroundTaskHost.Register(new NoopBackgroundTask("Stage1Bootstrap"), startOrder: 0, stopOrder: 100, isCritical: false);
            var backgroundResult = backgroundTaskHost.StartAsync(cancellationToken).GetAwaiter().GetResult();
            if (!backgroundResult.Success)
            {
                lock (gate)
                {
                    state = ServiceLifecycleState.Failed;
                }

                return Task.FromResult(HostStartupResult.Failed("后台任务启动失败。", backgroundResult.Errors));
            }

            lock (gate)
            {
                state = ServiceLifecycleState.Running;
            }

            logger.Info("Host", "ControlDoor Host 启动成功。", new LogFields { ElapsedMs = stopwatch.ElapsedMilliseconds });

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

            var stopwatch = Stopwatch.StartNew();
            cancellationToken.ThrowIfCancellationRequested();

            logger?.Info("Host", "ControlDoor Host 正在停止。", new LogFields { Extra = { ["reason"] = reason ?? string.Empty } });
            backgroundTaskHost?.StopAsync(TimeSpan.FromMilliseconds(10000)).GetAwaiter().GetResult();
            database?.Dispose();

            lock (gate)
            {
                state = ServiceLifecycleState.Stopped;
            }

            logger?.Info("Host", "ControlDoor Host 停止成功。", new LogFields { ElapsedMs = stopwatch.ElapsedMilliseconds });

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

            logger?.Dispose();
            backgroundTaskHost?.Dispose();
            database?.Dispose();
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
