using System.IO;

namespace ControlDoor.Runtime.Health.Checks
{
    public sealed class DllPresenceHealthCheck : IHealthCheck
    {
        private readonly string name;
        private readonly bool required;
        private readonly string[] relativePaths;

        public DllPresenceHealthCheck(string name, params string[] relativePaths)
            : this(name, required: false, relativePaths)
        {
        }

        public DllPresenceHealthCheck(string name, bool required, params string[] relativePaths)
        {
            this.name = name;
            this.required = required;
            this.relativePaths = relativePaths;
        }

        public string Name => name;

        public HealthCheckResult Run(HealthCheckContext context)
        {
            foreach (var relativePath in relativePaths)
            {
                var path = Path.Combine(context.RunDirectory, relativePath);
                if (File.Exists(path) || Directory.Exists(path))
                {
                    return HealthCheckResult.Ok(name, "找到依赖: " + path);
                }
            }

            var message = "未找到依赖: " + string.Join(", ", relativePaths);
            return required
                ? HealthCheckResult.Failed(name, message)
                : HealthCheckResult.Warning(name, "阶段 1 未找到依赖，后续 SDK 阶段需要补齐。");
        }
    }
}
