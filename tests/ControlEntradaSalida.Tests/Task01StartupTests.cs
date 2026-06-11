using ControlDoor;
using ControlDoor.Host;

namespace ControlEntradaSalida.Tests
{
    public static class Task01StartupTests
    {
        [TestCase]
        public static void CommandLineOptions_ParseConsole_UsesConsoleMode()
        {
            var options = CommandLineOptions.Parse(new[] { "--console" }, userInteractive: false);

            Assert.Equal(RunMode.Console, options.Mode);
        }

        [TestCase]
        public static void CommandLineOptions_ParseValidate_UsesValidateMode()
        {
            var options = CommandLineOptions.Parse(new[] { "--validate-config" }, userInteractive: true);

            Assert.Equal(RunMode.ValidateConfig, options.Mode);
        }

        [TestCase]
        public static void CommandLineOptions_ParseService_UsesServiceModeWhenNonInteractive()
        {
            var options = CommandLineOptions.Parse(new string[0], userInteractive: false);

            Assert.Equal(RunMode.Service, options.Mode);
        }

        [TestCase]
        public static void ControlDoorHost_StopIsIdempotentWithoutExternalServices()
        {
            using (var host = new ControlDoorHost())
            {
                var stop = host.StopAsync("test").GetAwaiter().GetResult();
                var stopAgain = host.StopAsync("test").GetAwaiter().GetResult();

                Assert.True(stop.Success);
                Assert.True(stopAgain.Success);
                Assert.Equal(ServiceLifecycleState.Stopped, host.State);
            }
        }

        [TestCase]
        public static void ServiceIdentity_UsesControlDoorName()
        {
            Assert.Equal("ControlDoor", ServiceIdentity.ServiceName);
            Assert.Equal("ControlDoor", ServiceIdentity.DisplayName);
            Assert.Contains("门禁", ServiceIdentity.Description);
        }
    }
}
