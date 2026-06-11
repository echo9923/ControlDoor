using System;
using ControlDoor.Host;

namespace ControlEntradaSalida.Tests
{
    public static class Task06ServiceLifecycleTests
    {
        [TestCase]
        public static void ServiceLifecycleController_StartSuccess_TransitionsToRunning()
        {
            var host = new FakeControlDoorHost();
            var reporter = new RecordingStatusReporter();
            var lifecycle = new ServiceLifecycleController(host, reporter: reporter);

            var result = lifecycle.StartAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();

            Assert.True(result.Success);
            Assert.Equal(ServiceLifecycleState.Running, lifecycle.State);
            Assert.Equal(ServiceLifecycleState.Starting, reporter.States[0]);
        }

        [TestCase]
        public static void ServiceLifecycleController_StartFailure_TransitionsToFailed()
        {
            var host = new FakeControlDoorHost { FailStart = true };
            var lifecycle = new ServiceLifecycleController(host);

            var result = lifecycle.StartAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();

            Assert.False(result.Success);
            Assert.Equal(ServiceLifecycleState.Failed, lifecycle.State);
        }

        [TestCase]
        public static void ServiceLifecycleController_StopSuccess_TransitionsToStopped()
        {
            var host = new FakeControlDoorHost();
            var lifecycle = new ServiceLifecycleController(host);
            lifecycle.StartAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();

            var result = lifecycle.StopAsync("test", TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();

            Assert.True(result.Success);
            Assert.Equal(ServiceLifecycleState.Stopped, lifecycle.State);
        }

        [TestCase]
        public static void ServiceLifecycleController_StopException_IsCaptured()
        {
            var host = new FakeControlDoorHost { ThrowOnStop = true };
            var lifecycle = new ServiceLifecycleController(host);
            lifecycle.StartAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();

            var result = lifecycle.StopAsync("test", TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();

            Assert.False(result.Success);
            Assert.Equal(ServiceLifecycleState.Stopped, lifecycle.State);
        }

        [TestCase]
        public static void ServiceLifecycleController_StartTimeout_RequestsBestEffortStop()
        {
            var host = new FakeControlDoorHost { StartDelay = TimeSpan.FromMilliseconds(200) };
            var lifecycle = new ServiceLifecycleController(host);

            var result = lifecycle.StartAsync(TimeSpan.FromMilliseconds(10)).GetAwaiter().GetResult();

            Assert.False(result.Success);
            Assert.True(host.StopCount >= 1);
        }

        [TestCase]
        public static void ServiceLifecycleController_Shutdown_UsesShutdownReason()
        {
            var host = new FakeControlDoorHost();
            var lifecycle = new ServiceLifecycleController(host);
            lifecycle.StartAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();

            var result = lifecycle.ShutdownAsync().GetAwaiter().GetResult();

            Assert.True(result.Success);
            Assert.Equal("Shutdown", result.Reason);
        }
    }
}
