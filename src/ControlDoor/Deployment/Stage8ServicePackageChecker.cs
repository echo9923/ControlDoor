using System;
using System.Collections.Generic;
using System.IO;
using ControlDoor.Configuration;

namespace ControlDoor.Deployment
{
    public sealed class Stage8ServicePackageChecker
    {
        public static IReadOnlyList<Stage8ServicePackageRequirement> RequiredLayout { get; } =
            new List<Stage8ServicePackageRequirement>
            {
                new Stage8ServicePackageRequirement("ControlDoor.exe", isDirectory: false, required: true),
                new Stage8ServicePackageRequirement("ControlDoor.exe.config", isDirectory: false, required: true),
                new Stage8ServicePackageRequirement("Configuration", isDirectory: true, required: true),
                new Stage8ServicePackageRequirement("Configuration\\appsettings.json", isDirectory: false, required: true),
                new Stage8ServicePackageRequirement("Configuration\\devices.json", isDirectory: false, required: true),
                new Stage8ServicePackageRequirement("logs", isDirectory: true, required: true),
                new Stage8ServicePackageRequirement("snapshots", isDirectory: true, required: true),
                new Stage8ServicePackageRequirement("tools\\service", isDirectory: true, required: true),
                new Stage8ServicePackageRequirement("tools\\service\\common-service.ps1", isDirectory: false, required: true),
                new Stage8ServicePackageRequirement("tools\\service\\install-service.ps1", isDirectory: false, required: true),
                new Stage8ServicePackageRequirement("tools\\service\\start-service.ps1", isDirectory: false, required: true),
                new Stage8ServicePackageRequirement("tools\\service\\stop-service.ps1", isDirectory: false, required: true),
                new Stage8ServicePackageRequirement("tools\\service\\uninstall-service.ps1", isDirectory: false, required: true),
                new Stage8ServicePackageRequirement("docs", isDirectory: true, required: true),
                new Stage8ServicePackageRequirement("docs\\部署说明.md", isDirectory: false, required: true),
                new Stage8ServicePackageRequirement("docs\\运行前检查.md", isDirectory: false, required: true),
                new Stage8ServicePackageRequirement("docs\\联调记录模板.md", isDirectory: false, required: true)
            }.AsReadOnly();

        public Stage8ServicePackageCheckResult Check(string packageRoot)
        {
            var result = new Stage8ServicePackageCheckResult();
            if (string.IsNullOrWhiteSpace(packageRoot))
            {
                result.Add(new Stage8ServicePackageCheckItem("发布包路径", false, "发布包路径不能为空。"));
                return result;
            }

            var root = Path.GetFullPath(packageRoot);
            result.Add(Directory.Exists(root)
                ? Ok("发布包路径", root)
                : Failed("发布包路径", "发布包目录不存在: " + root));

            foreach (var requirement in RequiredLayout)
            {
                CheckRequirement(root, requirement, result);
            }

            CheckSdkDll(root, result);
            CheckSqlServerTypes(root, result);
            CheckConfigurationTemplate(root, result);
            CheckWritableDirectory(root, "logs", result);
            CheckWritableDirectory(root, "snapshots", result);

            return result;
        }

        private static void CheckRequirement(string root, Stage8ServicePackageRequirement requirement, Stage8ServicePackageCheckResult result)
        {
            var path = Path.Combine(root, requirement.RelativePath);
            var exists = requirement.IsDirectory ? Directory.Exists(path) : File.Exists(path);
            var typeName = requirement.IsDirectory ? "目录" : "文件";
            if (exists)
            {
                result.Add(Ok(requirement.RelativePath, typeName + "存在。"));
                return;
            }

            result.Add(new Stage8ServicePackageCheckItem(
                requirement.RelativePath,
                !requirement.Required,
                typeName + "缺失: " + path));
        }

        private static void CheckSdkDll(string root, Stage8ServicePackageCheckResult result)
        {
            var candidates = new[]
            {
                Path.Combine(root, "HCNetSDK.dll"),
                Path.Combine(root, "sdk", "Hikvision", "HCNetSDK.dll")
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    result.Add(Ok("Hikvision SDK DLL", "找到 HCNetSDK.dll: " + candidate));
                    return;
                }
            }

            result.Add(Failed("Hikvision SDK DLL", "未找到 HCNetSDK.dll，应位于 exe 同级或 sdk\\Hikvision。"));
        }

        private static void CheckSqlServerTypes(string root, Stage8ServicePackageCheckResult result)
        {
            var candidates = new[]
            {
                Path.Combine(root, "SqlServerTypes"),
                Path.Combine(root, "sdk", "SqlServerTypes")
            };

            foreach (var candidate in candidates)
            {
                if (Directory.Exists(candidate) || File.Exists(candidate))
                {
                    result.Add(Ok("SqlServerTypes", "找到 SqlServerTypes 依赖: " + candidate));
                    return;
                }
            }

            result.Add(Failed("SqlServerTypes", "未找到 SqlServerTypes 依赖目录。"));
        }

        private static void CheckConfigurationTemplate(string root, Stage8ServicePackageCheckResult result)
        {
            var loadResult = new ConfigurationLoader().Load(root);
            if (!loadResult.Success)
            {
                result.Add(Failed("配置模板", string.Join("; ", loadResult.Errors)));
                return;
            }

            var settings = loadResult.Settings;
            var missing = new List<string>();
            if (settings.Service == null) missing.Add("Service");
            if (settings.Database == null) missing.Add("Database");
            if (settings.Logging == null) missing.Add("Logging");
            if (settings.DeviceSdkDispatcher == null) missing.Add("DeviceRuntime/DeviceSdkDispatcher");
            if (settings.DeviceConnection == null) missing.Add("DeviceLifecycle/DeviceConnection");
            if (settings.HikvisionSdk == null) missing.Add("HikvisionSdk");
            if (settings.DeviceOperationRetry == null) missing.Add("DeviceOperationRetry");
            if (settings.FaceEventLogging == null) missing.Add("FaceEventLogging");
            if (settings.FaceEnrollment == null) missing.Add("FaceEnrollment");
            if (settings.CameraAlarmDoorInterlock == null) missing.Add("CameraAlarmDoorInterlock");

            result.Add(missing.Count == 0
                ? Ok("配置模板", "必要配置分组齐全。")
                : Failed("配置模板", "缺少配置分组: " + string.Join(", ", missing)));
        }

        private static void CheckWritableDirectory(string root, string relativePath, Stage8ServicePackageCheckResult result)
        {
            var path = Path.Combine(root, relativePath);
            try
            {
                Directory.CreateDirectory(path);
                var probe = Path.Combine(path, ".stage8-write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                result.Add(Ok(relativePath + " 可写", path));
            }
            catch (Exception ex)
            {
                result.Add(Failed(relativePath + " 可写", ex.Message));
            }
        }

        private static Stage8ServicePackageCheckItem Ok(string name, string message)
        {
            return new Stage8ServicePackageCheckItem(name, true, message);
        }

        private static Stage8ServicePackageCheckItem Failed(string name, string message)
        {
            return new Stage8ServicePackageCheckItem(name, false, message);
        }
    }
}
