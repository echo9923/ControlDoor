using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ControlDoor;
using ControlDoor.Deployment;

namespace ControlEntradaSalida.Tests
{
    public static class Stage8DeliveryConsistencyTests
    {
        [TestCase]
        public static void Stage8Delivery_ServicePackageChecker_RequiresExactlyTheDocumentedCoreLayout()
        {
            var required = Stage8ServicePackageChecker.RequiredLayout
                .Where(item => item.Required)
                .Select(item => item.RelativePath)
                .ToList();

            foreach (var path in new[]
            {
                "ControlDoor.exe",
                "ControlDoor.exe.config",
                "Configuration",
                "Configuration\\appsettings.json",
                "logs",
                "snapshots",
                "docs",
                "docs\\" + DeployDocName(),
                "docs\\" + PreflightDocName(),
                "docs\\" + JointTestDocName()
            })
            {
                Assert.True(required.Contains(path), path);
            }
        }

        [TestCase]
        public static void Stage8Delivery_PowerShellPackageChecker_StaysAlignedWithManagedChecker()
        {
            var script = File.ReadAllText(Path.Combine("tools", "test-service-package.ps1"), Encoding.UTF8);

            foreach (var path in new[]
            {
                "ControlDoor.exe",
                "ControlDoor.exe.config",
                "Configuration\\appsettings.json",
                "logs",
                "snapshots",
                "docs",
                "sdk\\Hikvision\\HCNetSDK.dll",
                "SqlServerTypes"
            })
            {
                Assert.Contains(path, script);
            }

            Assert.Contains("0x90E8", script);
            Assert.Contains("0x8FD0", script);
            Assert.Contains("0x8054", script);
            Assert.Contains("Remove-JsonComments", script);
            Assert.Contains("DeviceRuntime", script);
            Assert.Contains("DeviceLifecycle", script);
            Assert.Contains("CameraAlarmDoorInterlock", script);
        }

        [TestCase]
        public static void Stage8Delivery_TargetDocumentMentionsImplementedAutomationEntrypoints()
        {
            var target = File.ReadAllText(TargetDocumentPath(), Encoding.UTF8);

            foreach (var token in new[]
            {
                "nuget restore ControlEntradaSalida.sln",
                "dotnet build ControlEntradaSalida.sln --verbosity minimal",
                "msbuild ControlEntradaSalida.sln /t:Build /p:Configuration=Debug",
                "msbuild ControlEntradaSalida.sln /p:Configuration=Release",
                "dotnet test tests\\ControlEntradaSalida.Tests\\ControlEntradaSalida.Tests.csproj --verbosity minimal",
                "ControlDoor.exe --validate-config",
                "L1",
                "L2",
                "L3",
                "L4",
                "L5",
                "L6"
            })
            {
                Assert.Contains(token, target);
            }
        }

        [TestCase]
        public static void Stage8Delivery_AgentsDirectoryReferencesEveryStage8TaskAndPackageTemplate()
        {
            var agents = File.ReadAllText("AGENTS.md", Encoding.UTF8);
            var expected = new List<string>();
            expected.AddRange(Directory.GetFiles(Path.Combine("docs", "stage8"), "task*.md")
                .Select(NormalizeRelativePath));
            expected.Add(NormalizeRelativePath(Path.Combine("docs", "stage8", "package-docs", DeployDocName())));
            expected.Add(NormalizeRelativePath(Path.Combine("docs", "stage8", "package-docs", PreflightDocName())));
            expected.Add(NormalizeRelativePath(Path.Combine("docs", "stage8", "package-docs", JointTestDocName())));

            foreach (var path in expected.OrderBy(item => item))
            {
                Assert.Contains(path, agents);
            }
        }

        [TestCase]
        public static void Stage8Delivery_ServiceIdentityMatchesServiceScripts()
        {
            var common = File.ReadAllText(Path.Combine("tools", "service", "common-service.ps1"), Encoding.UTF8);
            var install = File.ReadAllText(Path.Combine("tools", "service", "install-service.ps1"), Encoding.UTF8);
            var projectInstaller = File.ReadAllText(Path.Combine("src", "ControlDoor", "ProjectInstaller.cs"), Encoding.UTF8);

            Assert.Equal("ControlDoor", ServiceIdentity.ServiceName);
            Assert.Equal("ControlDoor", ServiceIdentity.DisplayName);
            Assert.Contains("$ControlDoorServiceName = \"ControlDoor\"", common);
            Assert.Contains("$ControlDoorDisplayName = \"ControlDoor\"", common);
            Assert.Contains("New-Service", install);
            Assert.Contains("ServiceIdentity.ServiceName", projectInstaller);
            Assert.Contains("ServiceIdentity.DisplayName", projectInstaller);
        }

        private static string NormalizeRelativePath(string path)
        {
            return path.Replace('\\', '/');
        }

        private static string TargetDocumentPath()
        {
            return "\u76ee\u6807.md";
        }

        private static string DeployDocName()
        {
            return "\u90e8\u7f72\u8bf4\u660e.md";
        }

        private static string PreflightDocName()
        {
            return "\u8fd0\u884c\u524d\u68c0\u67e5.md";
        }

        private static string JointTestDocName()
        {
            return "\u8054\u8c03\u8bb0\u5f55\u6a21\u677f.md";
        }
    }
}
