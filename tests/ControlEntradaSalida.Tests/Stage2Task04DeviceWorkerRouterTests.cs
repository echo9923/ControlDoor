using System;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Tasks;

namespace ControlEntradaSalida.Tests
{
    public static class Stage2Task04DeviceWorkerRouterTests
    {
        [TestCase]
        public static void DeviceWorkerRouter_Route_UsesStableDeviceIdModuloWorkerCount()
        {
            var router = new DeviceWorkerRouter(new DeviceWorkerRoutingOptions { WorkerCount = 4 });

            var first = router.Route(9);
            var second = router.Route(9);

            Assert.Equal(1, first.WorkerIndex);
            Assert.Equal(first.WorkerIndex, second.WorkerIndex);
            Assert.Equal(4, first.WorkerCount);
            Assert.Equal("9", first.RouteKey);
        }

        [TestCase]
        public static void DeviceWorkerRouter_Route_IgnoresTaskTypePriorityAndSource()
        {
            var router = new DeviceWorkerRouter(new DeviceWorkerRoutingOptions { WorkerCount = 8 });
            var login = NewTask(17, DeviceTaskType.Login, DeviceTaskPriority.High);
            var health = NewTask(17, DeviceTaskType.HealthCheck, DeviceTaskPriority.Low);
            var sync = NewTask(17, DeviceTaskType.SyncPerson, DeviceTaskPriority.Normal);

            var loginRoute = router.Route(login.DeviceId);
            var healthRoute = router.Route(health.DeviceId);
            var syncRoute = router.Route(sync.DeviceId);

            Assert.Equal(loginRoute.WorkerIndex, healthRoute.WorkerIndex);
            Assert.Equal(loginRoute.WorkerIndex, syncRoute.WorkerIndex);
        }

        [TestCase]
        public static void DeviceWorkerRouter_Route_RejectsInvalidInputs()
        {
            var router = new DeviceWorkerRouter(new DeviceWorkerRoutingOptions { WorkerCount = 2 });

            AssertThrowsArgumentOutOfRange(() => router.Route(0));
            AssertThrowsArgumentOutOfRange(() => new DeviceWorkerRouter(new DeviceWorkerRoutingOptions { WorkerCount = 0 }));
            AssertThrowsArgumentOutOfRange(() => DeviceWorkerRouter.CalculateWorkerIndex(int.MinValue, 4));
        }

        [TestCase]
        public static void DeviceWorkerRouter_Assign_RecordsDiagnosticsSnapshot()
        {
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            var router = new DeviceWorkerRouter(new DeviceWorkerRoutingOptions { WorkerCount = 4 });

            router.Assign(1, now);
            router.Assign(2, now);
            router.Assign(5, now);
            AssertThrowsArgumentOutOfRange(() => router.Route(-1));

            var snapshot = router.GetSnapshot(now.AddSeconds(1));

            Assert.Equal(4, snapshot.WorkerCount);
            Assert.Equal(1, snapshot.RouteFailureCount);
            Assert.Equal(2, snapshot.GetDeviceCount(1));
            Assert.Equal(1, snapshot.GetDeviceCount(2));
            Assert.Equal(now.AddSeconds(1), snapshot.CreatedAt);
        }

        [TestCase]
        public static void DeviceRuntimeRegistry_UsesSameRouterFormulaForRegisteredDevices()
        {
            var registry = new DeviceRuntimeRegistry(new DeviceRuntimeRegistryOptions { WorkerCount = 4 });
            registry.Register(NewOptions(11));

            var route = registry.TryGetWorkerRoute(11);

            Assert.True(route.Found);
            Assert.Equal(DeviceWorkerRouter.CalculateWorkerIndex(11, 4), route.WorkerIndex.Value);
        }

        private static DeviceSdkTask NewTask(int deviceId, DeviceTaskType type, DeviceTaskPriority priority)
        {
            var task = new DeviceSdkTask(deviceId, type, type.ToString(), context => System.Threading.Tasks.Task.FromResult(DeviceTaskResult.Queued(context.Task)));
            task.Priority = priority;
            return task;
        }

        private static DeviceRuntimeCreationOptions NewOptions(int deviceId)
        {
            return new DeviceRuntimeCreationOptions
            {
                DeviceId = deviceId,
                DeviceName = "device-" + deviceId,
                IpAddress = "10.0.2." + deviceId,
                Port = 8000,
                Username = "admin",
                Password = "pwd",
                Enabled = true,
                CreatedAt = new DateTime(2026, 1, 1, 8, 0, 0)
            };
        }

        private static void AssertThrowsArgumentOutOfRange(Action action)
        {
            try
            {
                action();
            }
            catch (ArgumentOutOfRangeException)
            {
                return;
            }

            throw new InvalidOperationException("Expected ArgumentOutOfRangeException.");
        }
    }
}
