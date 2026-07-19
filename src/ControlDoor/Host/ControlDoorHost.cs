using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Configuration;
using ControlDoor.CameraDoorInterlock;
using ControlDoor.Database;
using ControlDoor.Devices.Management;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Workers;
using ControlDoor.FaceEvents;
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
        private DeviceOperationRetryStore retryStore;
        private DeviceOperationRetryManager retryManager;
        private AccessControlGrpcService accessControlGrpcService;
        private PermissionSyncGrpcService permissionSyncGrpcService;
        private FaceEventIngestionService faceEventIngestionService;
        private AcsAlarmEventRouter acsAlarmEventRouter;
        private CameraDoorInterlockService cameraDoorInterlockService;
        private AiopAlarmEventRouter aiopAlarmEventRouter;

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

        public async Task<HostStartupResult> StartAsync(CancellationToken cancellationToken = default)
        {
            // 锁与状态快速路径保持同步：重复 Start 直接返回已完成 Task。
            // 真正的初始化（配置/健康检查/后台任务启动/设备加载）放到线程池，让 ServiceLifecycleController 的超时可中断。
            Task<HostStartupResult> fastPath;
            lock (gate)
            {
                fastPath = EvaluateStartFastPath();
            }

            if (fastPath != null)
            {
                return await fastPath.ConfigureAwait(false);
            }

            return await Task.Run(() => StartCore(cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        private Task<HostStartupResult> EvaluateStartFastPath()
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
            return null;
        }

        private HostStartupResult StartCore(CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            cancellationToken.ThrowIfCancellationRequested();

            var configurationResult = new ConfigurationLoader().Load(runDirectory);
            if (!configurationResult.Success)
            {
                lock (gate)
                {
                    state = ServiceLifecycleState.Failed;
                }

                return HostStartupResult.Failed("配置加载失败。", configurationResult.Errors);
            }

            settings = configurationResult.Settings;
            var logOptions = LogOptions.FromSettings(runDirectory, settings.Logging, mirrorToConsole: false);
            logger = new ServiceLogger(logOptions);
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

                return HostStartupResult.Failed("基础健康检查失败。", healthSummary.Results.Where(item => item.Status == HealthCheckStatus.Failed).Select(item => item.Message).ToList());
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
                ReArmBaseDelayMs = settings.DeviceConnection.ReArmBaseDelayMs,
                ReArmMaxDelayMs = settings.DeviceConnection.ReArmMaxDelayMs,
                AlarmDeployType = settings.FaceEventLogging.AlarmDeployType,
                AlarmStatusProbeEnabled = settings.DeviceConnection.AlarmStatusProbeEnabled,
                AlarmStatusProbeFailureThreshold = settings.DeviceConnection.AlarmStatusProbeFailureThreshold
            };
            IDeviceRepository deviceRepository;
            try
            {
                deviceRepository = CreateDeviceRepository(settings);
            }
            catch (Exception ex)
            {
                logger.Error("Host", "设备清单初始化失败。", ex);
                lock (gate)
                {
                    state = ServiceLifecycleState.Failed;
                }

                return HostStartupResult.Failed("设备清单初始化失败。", new[] { ex.Message });
            }

            deviceLifecycle = new DeviceLifecycleService(deviceRegistry, deviceDispatcher, delayedScheduler, deviceRepository, hikvisionGateway, deviceOptions, logger);
            accessControlGrpcService = new AccessControlGrpcService(deviceLifecycle, deviceRepository, settings.Service.GrpcManagementApiKey, logger, logOptions);
            retryStore = new DeviceOperationRetryStore(database, settings.DeviceOperationRetry, logger);
            retryManager = new DeviceOperationRetryManager(
                retryStore,
                deviceRegistry,
                new RetryExecutionCoordinator(deviceDispatcher, hikvisionGateway, logger),
                settings.DeviceOperationRetry,
                logger);
            permissionSyncGrpcService = new PermissionSyncGrpcService(
                deviceRegistry,
                deviceDispatcher,
                hikvisionGateway,
                retryStore,
                new SystemUserSyncStatusWriter(database),
                new EnrollmentTaskStore(),
                logger,
                settings.Devices.DefaultFaceCaptureDeviceId,
                logOptions);
            if (settings.FaceEventLogging.Enabled)
            {
                var snapshotStorage = new SnapshotStorage(runDirectory, settings.FaceEventLogging, logger);
                var faceRepository = new FaceEventRepository(database, snapshotStorage, settings.Database.ConnectionString);
                faceEventIngestionService = new FaceEventIngestionService(
                    settings.FaceEventLogging,
                    new AcsFaceEventProcessor(new AcsEventParser(), faceRepository, logger),
                    logger);
                acsAlarmEventRouter = new AcsAlarmEventRouter(deviceRegistry, faceEventIngestionService, settings.FaceEventLogging, logger);
                acsAlarmEventRouter.Attach(hikvisionGateway);
            }

            if (settings.CameraAlarmDoorInterlock.Enabled)
            {
                var interlockResolver = new InterlockMappingResolver(settings.CameraAlarmDoorInterlock, deviceRegistry, logger);
                cameraDoorInterlockService = new CameraDoorInterlockService(
                    settings.CameraAlarmDoorInterlock,
                    interlockResolver,
                    new AiopVideoPayloadParser(),
                    new CameraAlarmWindowManager(settings.CameraAlarmDoorInterlock.WindowSeconds),
                    new DoorTargetStateManager(),
                    new DoorControlTaskFactory(hikvisionGateway, logger),
                    deviceDispatcher,
                    logger: logger);
                aiopAlarmEventRouter = new AiopAlarmEventRouter(deviceRegistry, cameraDoorInterlockService, settings.CameraAlarmDoorInterlock, interlockResolver, logger);
                aiopAlarmEventRouter.Attach(hikvisionGateway);
            }

            backgroundTaskHost = new BackgroundTaskHost(logger);
            backgroundTaskHost.Register(delayedScheduler, startOrder: 10, stopOrder: 80, isCritical: false);
            backgroundTaskHost.Register(new DeviceHealthCheckBackgroundTask(deviceLifecycle, deviceOptions), startOrder: 20, stopOrder: 70, isCritical: false);
            backgroundTaskHost.Register(new GrpcServerBackgroundTask(settings.Service.GrpcListenPort, accessControlGrpcService, permissionSyncGrpcService), startOrder: 30, stopOrder: 60, isCritical: true);
            if (faceEventIngestionService != null)
            {
                backgroundTaskHost.Register(faceEventIngestionService, startOrder: 35, stopOrder: 55, isCritical: false);
            }
            if (cameraDoorInterlockService != null)
            {
                backgroundTaskHost.Register(cameraDoorInterlockService, startOrder: 36, stopOrder: 54, isCritical: false);
            }
            backgroundTaskHost.Register(retryManager, startOrder: 40, stopOrder: 50, isCritical: false);
            backgroundTaskHost.Register(new NoopBackgroundTask("Stage1Bootstrap"), startOrder: 0, stopOrder: 100, isCritical: false);
            var backgroundResult = backgroundTaskHost.StartAsync(cancellationToken).GetAwaiter().GetResult();
            if (!backgroundResult.Success)
            {
                lock (gate)
                {
                    state = ServiceLifecycleState.Failed;
                }

                return HostStartupResult.Failed("后台任务启动失败。", backgroundResult.Errors);
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
                deviceDispatcher?.Dispose();
                lock (gate)
                {
                    state = ServiceLifecycleState.Failed;
                }

                return HostStartupResult.Failed("阶段 4 设备加载失败。", new[] { ex.Message });
            }

            lock (gate)
            {
                state = ServiceLifecycleState.Running;
            }

            logger.Info("Host", "ControlDoor Host 启动成功。", new LogFields { ElapsedMs = stopwatch.ElapsedMilliseconds });

            return HostStartupResult.Succeeded("Host 启动成功。");
        }

        public async Task<HostStopResult> StopAsync(string reason = "Manual", CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                if (disposed)
                {
                    return HostStopResult.Succeeded(reason, "Host 已释放。");
                }

                if (state == ServiceLifecycleState.Stopped || state == ServiceLifecycleState.Created)
                {
                    state = ServiceLifecycleState.Stopped;
                    return HostStopResult.Succeeded(reason, "Host 已停止。");
                }

                state = ServiceLifecycleState.Stopping;
            }

            // 真异步执行：将繁重的同步清理放到线程池，让 ServiceLifecycleController 的超时/取消可生效。
            // 直接同步执行会让本方法在返回 Task 前跑完所有清理，超时永远无法触发。
            return await Task.Run(() => StopCore(reason, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        private HostStopResult StopCore(string reason, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            // 取消请求到达即尽快返回：后续每步前都检查一次，保证 ServiceLifecycleController 的超时可中断清理。
            if (cancellationToken.IsCancellationRequested)
            {
                lock (gate)
                {
                    state = ServiceLifecycleState.Stopped;
                }

                return HostStopResult.Succeeded(reason, "Host 收到取消，已尽快返回。");
            }

            logger?.Info("Host", "ControlDoor Host 正在停止。", new LogFields { Extra = { ["reason"] = reason ?? string.Empty } });

            // 顺序：先卸下事件订阅与门禁联动恢复（需要设备仍在登录态），再停后台生产者与延迟调度（停止投递 reconnect/rearm），
            // 然后清理设备会话（此时不会再被延迟任务重新登录），最后停 dispatcher。
            try
            {
                aiopAlarmEventRouter?.Dispose();
            }
            catch (Exception ex)
            {
                logger?.Error("Host", "阶段 9 AIOP 路由停止失败，继续停止流程。", ex);
            }

            StopCameraDoorInterlockBeforeDeviceLogout();

            if (cancellationToken.IsCancellationRequested)
            {
                return FinalizeStop(reason, stopwatch);
            }

            // 先停后台生产者：healthCheck / retryManager / faceEventIngestion / grpc / cameraDoorInterlock，最后停 delayedScheduler（reconnect/rearm 投递源）。
            // delayedScheduler 一旦 Stopped，Schedule/DispatchDueTasks 均不再投递，reconnect 不可能在设备清理期间重新登录。
            try
            {
                backgroundTaskHost?.StopAsync(TimeSpan.FromMilliseconds(10000)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger?.Error("Host", "后台任务停止失败，继续清理设备。", ex);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return FinalizeStop(reason, stopwatch);
            }

            // 双保险：取消 delayedScheduler 中尚未到期/或已停止前残存的 reconnect/rearm 任务，避免任何残余投递。
            CancelAllDelayedDeviceTasks();

            try
            {
                deviceLifecycle?.StopAllDevicesBestEffort();
            }
            catch (Exception ex)
            {
                logger?.Error("Host", "设备清理失败，继续停止 dispatcher。", ex);
            }

            try
            {
                deviceDispatcher?.StopAsync(TimeSpan.FromMilliseconds(10000)).GetAwaiter().GetResult();
                deviceDispatcher?.Dispose();
            }
            catch (Exception ex)
            {
                logger?.Error("Host", "Dispatcher 停止失败。", ex);
            }

            acsAlarmEventRouter?.Dispose();
            cameraDoorInterlockService?.Dispose();
            faceEventIngestionService?.Dispose();
            database?.Dispose();

            return FinalizeStop(reason, stopwatch);
        }

        private HostStopResult FinalizeStop(string reason, Stopwatch stopwatch)
        {
            lock (gate)
            {
                state = ServiceLifecycleState.Stopped;
            }

            logger?.Info("Host", "ControlDoor Host 停止成功。", new LogFields { ElapsedMs = stopwatch.ElapsedMilliseconds });
            return HostStopResult.Succeeded(reason, "Host 停止成功。");
        }

        private void StopCameraDoorInterlockBeforeDeviceLogout()
        {
            if (cameraDoorInterlockService == null)
            {
                return;
            }

            try
            {
                cameraDoorInterlockService
                    .StopAsync(new BackgroundTaskContext(RequestContext.Background("CameraDoorInterlockService").RequestId, CancellationToken.None, logger))
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                logger?.Error("Host", "阶段 9 门禁联动停止恢复失败，继续停止设备。", ex);
            }
        }

        // 停止设备清理前取消所有设备的延迟 reconnect/rearm 任务，确保延迟调度器即便有残存任务也不会投递重新登录/布防。
        private void CancelAllDelayedDeviceTasks()
        {
            if (deviceLifecycle == null)
            {
                return;
            }

            try
            {
                foreach (var snapshot in deviceRegistry?.GetAllSnapshots() ?? Enumerable.Empty<DeviceRuntimeSnapshot>())
                {
                    try
                    {
                        deviceLifecycle.CancelDelayedDeviceTasksForDevice(snapshot.DeviceId);
                    }
                    catch (Exception ex)
                    {
                        logger?.Warn("Host", "停止期间取消设备延迟任务失败: " + ex.Message, new LogFields { DeviceId = snapshot.DeviceId });
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Warn("Host", "枚举设备快照取消延迟任务失败: " + ex.Message);
            }
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
            acsAlarmEventRouter?.Dispose();
            aiopAlarmEventRouter?.Dispose();
            cameraDoorInterlockService?.Dispose();
            faceEventIngestionService?.Dispose();
            backgroundTaskHost?.Dispose();
            deviceDispatcher?.Dispose();
            database?.Dispose();
            logger?.Dispose();
        }

        private IDeviceRepository CreateDeviceRepository(AppSettings currentSettings)
        {
            var devices = currentSettings.Devices ?? new DeviceStoreOptions();
            logger?.Info("Host", "设备清单使用 JSON 来源。", new LogFields
            {
                Extra =
                {
                    ["filePath"] = devices.FilePath ?? string.Empty,
                    ["inlineCount"] = devices.Items == null ? "0" : devices.Items.Count.ToString()
                }
            });
            return new JsonDeviceRepository(runDirectory, devices, logger);
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
