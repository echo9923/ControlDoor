using System;
using System.Collections.Generic;
using System.Diagnostics;
using ControlDoor.Configuration;
using ControlDoor.Database;
using ControlDoor.Observability;
using ControlDoor.Runtime.Health.Checks;

namespace ControlDoor.Runtime.Health
{
    public sealed class HealthCheckService
    {
        private readonly IList<IHealthCheck> checks;

        public HealthCheckService(IEnumerable<IHealthCheck> checks)
        {
            this.checks = new List<IHealthCheck>(checks ?? new IHealthCheck[0]);
        }

        public HealthCheckSummary Run(HealthCheckContext context)
        {
            var summary = new HealthCheckSummary();
            foreach (var check in checks)
            {
                var stopwatch = Stopwatch.StartNew();
                HealthCheckResult result;
                try
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    result = check.Run(context).WithElapsed(stopwatch.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    result = HealthCheckResult.Failed(check.Name, ex.Message).WithElapsed(stopwatch.ElapsedMilliseconds);
                }

                summary.Add(result);
                context.Logger?.Info("HealthCheck", "健康检查完成。", new LogFields
                {
                    OperationName = check.Name,
                    ElapsedMs = result.ElapsedMs,
                    ErrorCode = result.Status.ToString(),
                    Extra = { ["message"] = result.Message }
                });
            }

            return summary;
        }

        public static HealthCheckService CreateStage1(string runDirectory, IDatabaseClient database = null)
        {
            return new HealthCheckService(new IHealthCheck[]
            {
                new RunDirectoryHealthCheck(),
                new ConfigurationFileHealthCheck(),
                new DirectoryHealthCheck("日志目录", "logs", required: true),
                new DirectoryHealthCheck("抓拍目录", "snapshots", required: false),
                new PortHealthCheck(),
                new DatabaseHealthCheckItem(database),
                new DllPresenceHealthCheck("海康 SDK DLL", "HCNetSDK.dll", "sdk\\Hikvision", "sdk\\Hikvision\\HCNetSDK.dll"),
                new DllPresenceHealthCheck("SqlServerTypes DLL", "SqlServerTypes", "sdk\\SqlServerTypes")
            });
        }

        public static HealthCheckService CreateStage8(string runDirectory, AppSettings settings, IDatabaseClient database)
        {
            settings = settings ?? new AppSettings();
            settings.EnsureGroups();

            return new HealthCheckService(new IHealthCheck[]
            {
                new RunDirectoryHealthCheck(),
                new ConfigurationFileHealthCheck(),
                new DirectoryHealthCheck("日志目录", settings.Logging.LogDirectory, required: true),
                new DirectoryHealthCheck("SDK 日志目录", settings.HikvisionSdk.SdkLogDirectory, required: settings.HikvisionSdk.RequireSdkLog),
                new DirectoryHealthCheck("抓拍目录", settings.FaceEventLogging.SnapshotRootDirectory, required: settings.FaceEventLogging.Enabled),
                new DeviceStoreHealthCheck(),
                new PortHealthCheck(),
                new DatabaseHealthCheckItem(database),
                new DllPresenceHealthCheck("Hikvision SDK DLL", required: true, "HCNetSDK.dll", CombineRelative(settings.HikvisionSdk.DllDirectory, "HCNetSDK.dll"), "sdk\\Hikvision\\HCNetSDK.dll"),
                new DllPresenceHealthCheck("SqlServerTypes DLL", required: true, "SqlServerTypes", "sdk\\SqlServerTypes")
            });
        }

        private static string CombineRelative(string directory, string fileName)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return fileName;
            }

            return System.IO.Path.Combine(directory, fileName);
        }
    }
}
