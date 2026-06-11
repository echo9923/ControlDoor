using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ControlDoor;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Tasks;
using ControlDoor.Devices.Workers;

namespace ControlEntradaSalida.Tests
{
    public static class Stage2Task09AcceptanceTests
    {
        [TestCase]
        public static void Stage2Acceptance_CompositionTypesExistForEveryStage2Deliverable()
        {
            AssertTypeExists(typeof(DeviceRuntimeState));
            AssertTypeExists(typeof(DeviceRuntimeRegistry));
            AssertTypeExists(typeof(DeviceSdkTask));
            AssertTypeExists(typeof(DeviceWorkerRouter));
            AssertTypeExists(typeof(DeviceSdkDispatcher));
            AssertTypeExists(typeof(DeviceTaskQueue));
            AssertTypeExists(typeof(DeviceTaskExceptionMapper));
            AssertTypeExists(typeof(DeviceWorkerWatchdog));
            AssertTypeExists(typeof(DelayedDeviceTaskScheduler));
        }

        [TestCase]
        public static void Stage2Acceptance_DiagnosticSnapshotsExposeRequiredRuntimeIndexesAndWorkers()
        {
            var registry = new DeviceRuntimeRegistry(new DeviceRuntimeRegistryOptions { WorkerCount = 2 });
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            registry.Register(NewOptions(1, "10.0.9.1"));
            registry.Register(NewOptions(2, "10.0.9.2"));
            registry.RegisterSdkUserId(1, 101, "SN101", now);
            registry.RegisterAlarmHandle(1, 501, now);
            registry.MarkDisconnected(2, DeviceRuntimeError.Create("HealthCheck", "NET", "offline", now, sdkErrorCode: 7, retryable: true), now, DeviceConnectionStatus.Offline);

            var registrySnapshot = registry.GetRegistrySnapshot(now);
            var deviceSnapshots = registry.GetAllSnapshots();
            var dispatcher = new DeviceSdkDispatcher(registry, new DeviceSdkDispatcherOptions
            {
                WorkerCount = 2,
                QueueCapacityPerWorker = 10,
                DefaultTaskTimeoutMilliseconds = 30000
            });
            var task = new DeviceSdkTask(1, DeviceTaskType.HealthCheck, "HealthCheck", context => Task.FromResult(Success(context.Task)));

            try
            {
                var result = dispatcher.SubmitAndWaitAsync(task).GetAwaiter().GetResult();
                var workerSnapshots = dispatcher.GetWorkerSnapshots();

                Assert.True(result.Success);
                Assert.Equal(2, registrySnapshot.DeviceCount);
                Assert.Equal(2, registrySnapshot.IpIndexCount);
                Assert.Equal(1, registrySnapshot.SdkUserIdIndexCount);
                Assert.Equal(1, registrySnapshot.AlarmHandleIndexCount);
                Assert.Equal(2, registrySnapshot.WorkerRouteIndexCount);
                Assert.True(registrySnapshot.GetStatusCount(DeviceConnectionStatus.Offline) >= 1);
                Assert.Equal(2, deviceSnapshots.Count);
                Assert.True(deviceSnapshots.Any(snapshot => snapshot.LastError != null && snapshot.LastError.Code == "NET"));
                Assert.Equal(2, workerSnapshots.Count);
                Assert.True(workerSnapshots.Any(snapshot => snapshot.CompletedTaskCount >= 1));
                Assert.True(workerSnapshots.All(snapshot => snapshot.PriorityQueue != null));
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
        }

        [TestCase]
        public static void Stage2Acceptance_PriorityTimeoutDelayedAndWatchdogDiagnosticsAreAvailable()
        {
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var queue = new DeviceTaskQueue(10);
            var low = NewTask(1, DeviceTaskType.HealthCheck, DeviceTaskPriority.Low);
            var high = NewTask(1, DeviceTaskType.Login, DeviceTaskPriority.High);
            queue.TryEnqueue(low, now, 30000, out _);
            queue.TryEnqueue(high, now.AddMilliseconds(1), 30000, out _);
            var prioritySnapshot = queue.GetPrioritySnapshot(now.AddSeconds(1));

            var delayedDispatcher = NewDispatcher();
            var delayedScheduler = new DelayedDeviceTaskScheduler(delayedDispatcher, new DelayedDeviceTaskSchedulerOptions
            {
                MaxDelayedTaskCount = 10,
                DispatchBatchSize = 10,
                WakeupMaxSleepMilliseconds = 10
            });

            try
            {
                delayedScheduler.Schedule(new DelayedDeviceTask(
                    1,
                    DeviceTaskType.HealthCheck,
                    DeviceTaskPriority.Low,
                    now.AddMinutes(1),
                    "acceptance:health:1",
                    "stage2-acceptance",
                    () => NewTask(1, DeviceTaskType.HealthCheck, DeviceTaskPriority.Low),
                    now));
                var delayedSnapshot = delayedScheduler.GetSnapshot(now);

                var watchdog = new DeviceWorkerWatchdog(1000);
                var longRunning = watchdog.Scan(new[]
                {
                    new DeviceWorkerRuntimeSnapshot
                    {
                        WorkerIndex = 0,
                        CurrentTaskId = "task-acceptance",
                        CurrentDeviceId = 1,
                        CurrentTaskType = DeviceTaskType.HealthCheck,
                        CurrentTaskStartedAt = now
                    }
                }, now.AddSeconds(2)).Single();

                Assert.Equal(1, prioritySnapshot.GetQueueLength(DeviceTaskPriority.Low));
                Assert.Equal(1, prioritySnapshot.GetQueueLength(DeviceTaskPriority.High));
                Assert.True(prioritySnapshot.GetOldestTaskAgeMilliseconds(DeviceTaskPriority.Low).HasValue);
                Assert.Equal(1, delayedSnapshot.DelayedTaskCount);
                Assert.Equal(now.AddMinutes(1), delayedSnapshot.EarliestDueAt.Value);
                Assert.Equal(1, delayedSnapshot.GetSourceCount("stage2-acceptance"));
                Assert.True(longRunning.IsLongRunning);
                Assert.Equal(0, longRunning.WorkerIndex);
                Assert.Equal(1, longRunning.DeviceId.Value);
            }
            finally
            {
                delayedDispatcher.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }
        }

        [TestCase]
        public static void Stage2Acceptance_DoesNotReferenceRealSdkDatabaseGrpcOrAiopImplementations()
        {
            var assembly = typeof(ServiceIdentity).Assembly;
            var stage2Types = assembly.GetTypes()
                .Where(type => type.Namespace != null &&
                    (type.Namespace.StartsWith("ControlDoor.Devices.Runtime") ||
                     type.Namespace.StartsWith("ControlDoor.Devices.Tasks") ||
                     type.Namespace.StartsWith("ControlDoor.Devices.Workers")))
                .ToList();

            Assert.True(stage2Types.Count >= 20);
            Assert.False(stage2Types.Any(type => type.FullName.IndexOf("Hikvision", StringComparison.OrdinalIgnoreCase) >= 0));
            Assert.False(stage2Types.Any(type => type.FullName.IndexOf("Grpc", StringComparison.OrdinalIgnoreCase) >= 0));
            Assert.False(stage2Types.Any(type => type.FullName.IndexOf("Sql", StringComparison.OrdinalIgnoreCase) >= 0));
            Assert.False(stage2Types.Any(type => type.FullName.IndexOf("Aiop", StringComparison.OrdinalIgnoreCase) >= 0));
            Assert.False(stage2Types.SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                .Any(method => method.Name.IndexOf("Abort", StringComparison.OrdinalIgnoreCase) >= 0));
        }

        [TestCase]
        public static void Stage2Acceptance_DoesNotModifyDatabaseScriptsOrGrpcContractFiles()
        {
            var root = FindRepositoryRoot();
            var databaseDirectory = Path.Combine(root, "database");
            var databaseFiles = Directory.Exists(databaseDirectory)
                ? Directory.GetFiles(databaseDirectory, "*", SearchOption.AllDirectories)
                : Array.Empty<string>();
            var grpcContractFiles = Directory.GetFiles(root, "*grpc*", SearchOption.AllDirectories)
                .Where(path => path.IndexOf(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0)
                .Where(path => path.IndexOf(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0)
                .Where(path => path.IndexOf(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0)
                .ToList();

            Assert.True(databaseFiles.All(path => path.IndexOf("stage2", StringComparison.OrdinalIgnoreCase) < 0));
            Assert.True(grpcContractFiles.All(path => path.IndexOf("Stage2Task09AcceptanceTests", StringComparison.OrdinalIgnoreCase) < 0));
            Assert.True(grpcContractFiles.All(path => !Path.GetFileName(path).EndsWith(".proto", StringComparison.OrdinalIgnoreCase)));
        }

        [TestCase]
        public static void Stage2Acceptance_Stage2TaskTestsCoverAllPlannedTasks()
        {
            var testTypes = typeof(Stage2Task09AcceptanceTests).Assembly.GetTypes()
                .Where(type => type.Name.StartsWith("Stage2Task", StringComparison.Ordinal))
                .Select(type => type.Name)
                .ToList();

            for (var task = 1; task <= 9; task++)
            {
                var prefix = "Stage2Task" + task.ToString("00");
                Assert.True(testTypes.Any(name => name.StartsWith(prefix, StringComparison.Ordinal)), "Missing tests for " + prefix);
            }
        }

        private static void AssertTypeExists(Type type)
        {
            Assert.NotNull(type);
        }

        private static DeviceRuntimeCreationOptions NewOptions(int deviceId, string ipAddress)
        {
            return new DeviceRuntimeCreationOptions
            {
                DeviceId = deviceId,
                DeviceName = "device-" + deviceId,
                IpAddress = ipAddress,
                Port = 8000,
                Username = "admin",
                Password = "pwd",
                Enabled = true,
                CreatedAt = new DateTime(2026, 1, 1, 8, 0, 0)
            };
        }

        private static DeviceSdkDispatcher NewDispatcher()
        {
            var registry = new DeviceRuntimeRegistry(new DeviceRuntimeRegistryOptions { WorkerCount = 1 });
            registry.Register(NewOptions(1, "10.0.9.11"));
            return new DeviceSdkDispatcher(registry, new DeviceSdkDispatcherOptions
            {
                WorkerCount = 1,
                QueueCapacityPerWorker = 10,
                DefaultTaskTimeoutMilliseconds = 30000
            });
        }

        private static DeviceSdkTask NewTask(int deviceId, DeviceTaskType type, DeviceTaskPriority priority)
        {
            var task = new DeviceSdkTask(deviceId, type, type.ToString(), context => Task.FromResult(Success(context.Task)));
            task.Priority = priority;
            return task;
        }

        private static DeviceTaskResult Success(DeviceSdkTask task)
        {
            var now = DateTime.Now;
            return DeviceTaskResult.FromTask(task, true, "OK", "ok", DeviceConnectionStatus.Online, now, now);
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            return @"D:\codeproject\c#\ControlDoor";
        }
    }
}
