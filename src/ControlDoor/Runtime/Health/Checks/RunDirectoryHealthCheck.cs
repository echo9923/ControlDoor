using System.IO;

namespace ControlDoor.Runtime.Health.Checks
{
    public sealed class RunDirectoryHealthCheck : IHealthCheck
    {
        public string Name => "运行目录";

        public HealthCheckResult Run(HealthCheckContext context)
        {
            if (Directory.Exists(context.RunDirectory))
            {
                return HealthCheckResult.Ok(Name, context.RunDirectory + " 存在。");
            }

            return HealthCheckResult.Failed(Name, context.RunDirectory + " 不存在。");
        }
    }
}
