using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Configuration;
using ControlDoor.Database;
using ControlDoor.Devices.Management;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Workers;
using ControlDoor.GrpcApi;
using ControlDoor.Hikvision;
using ControlDoor.Observability;
using ControlDoor.Permissions;
using ControlDoor.Runtime;
using ControlDoor.Runtime.Health;

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
        private DeviceRuntimeRegistry deviceRegistry;
        private DeviceSdkDispatcher deviceDispatcher;
        private DelayedDeviceTaskScheduler delayedScheduler;
        private DeviceLifecycleService deviceLifecycle;
        private IHikvisionGateway hikvisionGateway;
        private AccessControlGrpcService accessControlGrpcService;
        private PermissionSyncGrpcService permissionSyncGrpcService;

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
            var healthSummary = HealthCheckService
                .CreateStage1(runDirectory, database)
                .Run(new HealthCheckContext(runDirectory, settings, logger, cancellationToken));
            if (!healthSummary.Success)
            {
                lock (gate)
                {
                    state = ServiceLifecycleState.Failed;
                }

                return Task.FromResult(HostStartupResult.Failed("基础健康检查失败。", healthSummary.Results.Where(item => item.Status == HealthCheckStatus.Failed).Select(item => item.Message).ToList()));
            }

            deviceRegistry = new DeviceRuntimeRegistry(new DeviceRuntimeRegistryOptions { WorkerCount = settings.DeviceSdkDispatcher.WorkerCount });
            deviceDispatcher = new DeviceSdkDispatcher(deviceRegistry, new Devices.Workers.DeviceSdkDispatcherOptions
            {
                WorkerCount = settings.DeviceSdkDispatcher.WorkerCount,
                QueueCapacityPerWorker = settings.DeviceSdkDispatcher.QueueCapacity,
                DefaultTaskTimeoutMilliseconds = settings.DeviceSdkDispatcher.DefaultTaskTimeoutMs
            }, logger);
            delayedScheduler = new DelayedDeviceTaskScheduler(deviceDispatcher, logger: logger);
            hikvisionGateway = new HikvisionSdkWrapper();
            var deviceOptions = new DeviceLifecycleOptions
            {
                LoginTimeoutMs = settings.DeviceConnection.LoginTimeoutMs,
                LogoutTimeoutMs = settings.DeviceConnection.LogoutTimeoutMs,
                HealthCheckTimeoutMs = settings.DeviceConnection.StatusCheckIntervalMs,
                HealthCheckIntervalMs = settings.DeviceConnection.StatusCheckIntervalMs,
                ReconnectBaseDelayMs = settings.DeviceConnection.ReconnectBaseDelayMs,
                ReconnectMaxDelayMs = settings.DeviceConnection.ReconnectMaxDelayMs,
                MaxReconnectAttempts = settings.DeviceOperationRetry.MaxRetryAttempts
            };
            var deviceRepository = new SqlDeviceRepository(database);
            deviceLifecycle = new DeviceLifecycleService(deviceRegistry, deviceDispatcher, delayedScheduler, deviceRepository, hikvisionGateway, deviceOptions, logger);
            accessControlGrpcService = new AccessControlGrpcService(deviceLifecycle, deviceRepository, settings.Service.GrpcManagementApiKey);
            permissionSyncGrpcService = new PermissionSyncGrpcService(
                deviceRegistry,
                deviceDispatcher,
                hikvisionGateway,
                new DeviceOperationRetryStore(database),
                new SystemUserSyncStatusWriter(database),
                new EnrollmentTaskStore());

            backgroundTaskHost = new BackgroundTaskHost(logger);
            backgroundTaskHost.Register(delayedScheduler, startOrder: 10, stopOrder: 80, isCritical: false);
            backgroundTaskHost.Register(new DeviceHealthCheckBackgroundTask(deviceLifecycle, deviceOptions), startOrder: 20, stopOrder: 70, isCritical: false);
            backgroundTaskHost.Register(new GrpcServerBackgroundTask(settings.Service.GrpcListenPort, accessControlGrpcService, permissionSyncGrpcService), startOrder: 30, stopOrder: 60, isCritical: true);
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

            try
            {
                deviceLifecycle.LoadEnabledDevices(enqueueLogin: true);
            }
            catch (Exception ex)
            {
                logger.Error("Host", "阶段 4 设备加载失败。", ex);
                backgroundTaskHost?.StopAsync(TimeSpan.FromMilliseconds(10000)).GetAwaiter().GetResult();
                deviceDispatcher?.StopAsync(TimeSpan.FromMilliseconds(10000)).GetAwaiter().GetResult();
                lock (gate)
                {
                    state = ServiceLifecycleState.Failed;
                }

                return Task.FromResult(HostStartupResult.Failed("阶段 4 设备加载失败。", new[] { ex.Message }));
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
            deviceLifecycle?.StopAllDevicesBestEffort();
            backgroundTaskHost?.StopAsync(TimeSpan.FromMilliseconds(10000)).GetAwaiter().GetResult();
            deviceDispatcher?.StopAsync(TimeSpan.FromMilliseconds(10000)).GetAwaiter().GetResult();
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

            deviceLifecycle?.Dispose();
            hikvisionGateway?.Dispose();
            backgroundTaskHost?.Dispose();
            deviceDispatcher?.StopAsync(TimeSpan.FromMilliseconds(100)).GetAwaiter().GetResult();
            database?.Dispose();
            logger?.Dispose();
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
