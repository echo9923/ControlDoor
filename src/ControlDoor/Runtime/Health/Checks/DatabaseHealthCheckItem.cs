using ControlDoor.Database;

namespace ControlDoor.Runtime.Health.Checks
{
    public sealed class DatabaseHealthCheckItem : IHealthCheck
    {
        private readonly IDatabaseClient database;

        public DatabaseHealthCheckItem(IDatabaseClient database)
        {
            this.database = database;
        }

        public string Name => "数据库连接";

        public HealthCheckResult Run(HealthCheckContext context)
        {
            if (database == null)
            {
                return HealthCheckResult.Warning(Name, "未提供数据库客户端，跳过真实连接检查。");
            }

            var report = new DatabaseHealthCheck(database).Run();
            if (!report.Success)
            {
                var failureMessage = BuildFailureMessage(report);
                if (!string.IsNullOrWhiteSpace(failureMessage))
                {
                    return HealthCheckResult.Failed(Name, failureMessage);
                }

                return HealthCheckResult.Failed(Name, "数据库连接或核心表只读检查失败。");
            }

            var optionalWarnings = 0;
            foreach (var command in report.Commands)
            {
                if (command.Error != null)
                {
                    optionalWarnings++;
                }
            }

            return optionalWarnings == 0
                ? HealthCheckResult.Ok(Name, "数据库连接和核心表只读检查通过。")
                : HealthCheckResult.Warning(Name, "数据库连接通过，后续阶段表存在 warning: " + optionalWarnings);
        }

        private static string BuildFailureMessage(DatabaseHealthReport report)
        {
            if (report == null)
            {
                return "数据库连接或核心表只读检查失败。";
            }

            foreach (var command in report.Commands)
            {
                if (command.Error == null)
                {
                    continue;
                }

                var code = command.Error.SqlErrorNumber.HasValue
                    ? command.Error.SqlErrorNumber.Value.ToString()
                    : command.Error.ExceptionType;
                return "数据库健康检查失败: " + command.OperationName + "，errorCode=" + code + "，" + command.Error.Message;
            }

            return "数据库连接或核心表只读检查失败。";
        }
    }
}
