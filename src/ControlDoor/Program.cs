using System;
using System.IO;
using System.ServiceProcess;
using System.Threading;
using ControlDoor.Configuration;
using ControlDoor.Database;
using ControlDoor.Host;
using ControlDoor.Observability;
using ControlDoor.Runtime.Health;

namespace ControlDoor
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var options = CommandLineOptions.Parse(args, Environment.UserInteractive);

            switch (options.Mode)
            {
                case RunMode.Help:
                    PrintHelp();
                    return 0;
                case RunMode.Version:
                    PrintVersion();
                    return 0;
                case RunMode.ValidateConfig:
                    return RunValidateConfig();
                case RunMode.Console:
                    return RunConsole();
                default:
                    ServiceBase.Run(new ControlDoorService());
                    return 0;
            }
        }

        private static int RunConsole()
        {
            using (var host = new ControlDoorHost())
            using (var stopSignal = new ManualResetEventSlim(false))
            {
                var lifecycle = new ServiceLifecycleController(host);
                Console.CancelKeyPress += (sender, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    stopSignal.Set();
                };

                Console.WriteLine("ControlDoor 控制台模式启动。");
                Console.WriteLine("运行目录: " + RuntimePaths.GetRunDirectory());
                Console.WriteLine("配置路径: " + RuntimePaths.GetConfigPath(RuntimePaths.GetRunDirectory()));

                var startResult = lifecycle.StartAsync(TimeSpan.FromMilliseconds(120000)).GetAwaiter().GetResult();
                if (!startResult.Success)
                {
                    Console.Error.WriteLine(startResult.Message);
                    return 1;
                }

                Console.WriteLine("ControlDoor 已启动，按 Ctrl+C 或回车停止。");
                var inputTask = System.Threading.Tasks.Task.Run(() => Console.ReadLine());
                while (!stopSignal.IsSet && !inputTask.IsCompleted)
                {
                    stopSignal.Wait(200);
                }

                var stopResult = lifecycle.StopAsync("Console", TimeSpan.FromMilliseconds(60000)).GetAwaiter().GetResult();
                Console.WriteLine(stopResult.Message);
                return stopResult.Success ? 0 : 2;
            }
        }

        private static int RunValidateConfig()
        {
            Console.WriteLine("ControlDoor 配置验证模式。");
            var runDirectory = RuntimePaths.GetRunDirectory();
            Console.WriteLine("运行目录: " + runDirectory);

            var result = new ConfigurationLoader().Load(runDirectory);
            Console.WriteLine("配置路径: " + result.ConfigPath);
            Console.WriteLine("配置结果: " + (result.Success ? "OK" : "Failed"));
            WriteList("Errors", result.Errors);
            WriteList("Warnings", result.Warnings);

            if (result.Success)
            {
                Console.WriteLine("gRPC 端口: " + result.Settings.Service.GrpcListenPort);
                Console.WriteLine("日志目录: " + result.Settings.Logging.LogDirectory);
                Console.WriteLine("SDK DLL 目录: " + result.Settings.HikvisionSdk.DllDirectory);
                Console.WriteLine("SDK 平台: " + result.Settings.HikvisionSdk.Platform);
                Console.WriteLine("抓拍目录: " + result.Settings.FaceEventLogging.SnapshotRootDirectory);
                Console.WriteLine("数据库命令超时: " + result.Settings.Database.CommandTimeoutSeconds + " 秒");
                Console.WriteLine("设备 worker 数: " + result.Settings.DeviceSdkDispatcher.WorkerCount);
                Console.WriteLine("日志策略: payload=" + result.Settings.Logging.EnableGrpcPayloadLogging +
                    ", mode=" + result.Settings.Logging.GrpcPayloadLogMode +
                    ", credentials=" + result.Settings.Logging.IncludeCredentialFields +
                    ", faceBase64=" + result.Settings.Logging.IncludeFaceImageBase64 +
                    ", sdkTrace=" + result.Settings.Logging.EnableSdkTrace);

                try
                {
                    using (var logger = new ServiceLogger(LogOptions.FromSettings(runDirectory, result.Settings.Logging, mirrorToConsole: false)))
                    using (var database = new SqlServerDatabase(result.Settings.Database, logger))
                    {
                        logger.Info("ValidateConfig", "配置验证模式已完成配置加载。");
                        Console.WriteLine("日志检查: OK");
                        Console.WriteLine("日志文件: " + logger.CurrentLogPath);

                        var summary = HealthCheckService
                            .CreateStage8(runDirectory, result.Settings, database)
                            .Run(new HealthCheckContext(runDirectory, result.Settings, logger, CancellationToken.None));
                        WriteHealthSummary(summary);
                        if (!summary.Success)
                        {
                            return 1;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("日志检查: Failed " + ex.Message);
                    return 1;
                }
            }

            return result.Success ? 0 : 1;
        }

        private static void PrintVersion()
        {
            Console.WriteLine("ControlDoor");
            Console.WriteLine("版本: " + VersionInfo.Version);
            Console.WriteLine("构建时间: " + VersionInfo.BuildTime);
            Console.WriteLine("运行目录: " + RuntimePaths.GetRunDirectory());
            Console.WriteLine("配置路径: " + RuntimePaths.GetConfigPath(RuntimePaths.GetRunDirectory()));
        }

        private static void PrintHelp()
        {
            Console.WriteLine("ControlDoor 使用方式:");
            Console.WriteLine("  ControlDoor.exe --console");
            Console.WriteLine("  ControlDoor.exe --validate-config");
            Console.WriteLine("  ControlDoor.exe --version");
        }

        private static void WriteList(string title, System.Collections.Generic.IReadOnlyList<string> values)
        {
            Console.WriteLine(title + ": " + values.Count);
            foreach (var value in values)
            {
                Console.WriteLine("  - " + value);
            }
        }

        private static void WriteHealthSummary(HealthCheckSummary summary)
        {
            Console.WriteLine("健康检查: OK=" + summary.OkCount + " Warning=" + summary.WarningCount + " Failed=" + summary.FailedCount);
            foreach (var item in summary.Results)
            {
                Console.WriteLine("  - " + item.Status + " " + item.Name + ": " + item.Message);
            }
        }
    }
}
