using System.IO;
using ControlDoor.Configuration;

namespace ControlDoor.Runtime.Health.Checks
{
    public sealed class ConfigurationFileHealthCheck : IHealthCheck
    {
        public string Name => "配置文件";

        public HealthCheckResult Run(HealthCheckContext context)
        {
            var result = new ConfigurationLoader().Load(context.RunDirectory);
            if (!result.Success)
            {
                return HealthCheckResult.Failed(Name, string.Join("; ", result.Errors));
            }

            if (!File.Exists(result.ConfigPath))
            {
                return HealthCheckResult.Failed(Name, "配置文件不存在: " + result.ConfigPath);
            }

            var message = result.Warnings.Count == 0
                ? "配置文件可读取并解析。"
                : "配置文件可读取，存在 warning: " + string.Join("; ", result.Warnings);
            return result.Warnings.Count == 0
                ? HealthCheckResult.Ok(Name, message)
                : HealthCheckResult.Warning(Name, message);
        }
    }
}
