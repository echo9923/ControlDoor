using System.IO;

namespace ControlEntradaSalida.Tests
{
    public static class Stage8ServiceScriptTests
    {
        [TestCase]
        public static void Stage8ServiceScripts_AllExpectedEntrypointsExist()
        {
            foreach (var script in new[]
            {
                "common-service.ps1",
                "install-service.ps1",
                "start-service.ps1",
                "stop-service.ps1",
                "uninstall-service.ps1"
            })
            {
                Assert.True(File.Exists(Path.Combine("tools", "service", script)), script);
            }
        }

        [TestCase]
        public static void Stage8ServiceScripts_UseFixedServiceNameAndValidateBeforeStart()
        {
            var common = File.ReadAllText(Path.Combine("tools", "service", "common-service.ps1"));
            var install = File.ReadAllText(Path.Combine("tools", "service", "install-service.ps1"));
            var start = File.ReadAllText(Path.Combine("tools", "service", "start-service.ps1"));

            Assert.Contains("$ControlDoorServiceName = \"ControlDoor\"", common);
            Assert.Contains("Invoke-ControlDoorValidateConfig", install);
            Assert.Contains("Invoke-ControlDoorValidateConfig", start);
            Assert.Contains("New-Service", install);
            Assert.Contains("Start-Service", start);
        }

        [TestCase]
        public static void Stage8ServiceScripts_UninstallRetainsBusinessData()
        {
            var uninstall = File.ReadAllText(Path.Combine("tools", "service", "uninstall-service.ps1"));

            Assert.Contains("sc.exe delete", uninstall);
            Assert.Contains("retained", uninstall);
            Assert.False(uninstall.Contains("Remove-Item"));
            Assert.False(uninstall.Contains("DROP TABLE"));
        }
    }
}
