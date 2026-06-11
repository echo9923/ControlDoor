using System;
using System.IO;

namespace ControlDoor.Runtime.Health.Checks
{
    public sealed class DirectoryHealthCheck : IHealthCheck
    {
        private readonly string name;
        private readonly string directory;
        private readonly bool required;

        public DirectoryHealthCheck(string name, string directory, bool required)
        {
            this.name = name;
            this.directory = directory;
            this.required = required;
        }

        public string Name => name;

        public HealthCheckResult Run(HealthCheckContext context)
        {
            try
            {
                var path = directory;
                if (!Path.IsPathRooted(path))
                {
                    path = Path.Combine(context.RunDirectory, path);
                }

                Directory.CreateDirectory(path);
                var probe = Path.Combine(path, ".write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return HealthCheckResult.Ok(name, path + " 可创建且可写。");
            }
            catch (Exception ex)
            {
                return required
                    ? HealthCheckResult.Failed(name, ex.Message)
                    : HealthCheckResult.Warning(name, ex.Message);
            }
        }
    }
}
