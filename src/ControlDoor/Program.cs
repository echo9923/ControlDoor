using System;
using System.IO;
using System.ServiceProcess;
using System.Threading;
using ControlDoor.Host;

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
                Console.CancelKeyPress += (sender, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    stopSignal.Set();
                };

                Console.WriteLine("ControlDoor 控制台模式启动。");
                Console.WriteLine("运行目录: " + RuntimePaths.GetRunDirectory());
                Console.WriteLine("配置路径: " + RuntimePaths.GetConfigPath(RuntimePaths.GetRunDirectory()));

                var startResult = host.StartAsync().GetAwaiter().GetResult();
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

                var stopResult = host.StopAsync("Console").GetAwaiter().GetResult();
                Console.WriteLine(stopResult.Message);
                return stopResult.Success ? 0 : 2;
            }
        }

        private static int RunValidateConfig()
        {
            Console.WriteLine("ControlDoor 配置验证模式。");
            Console.WriteLine("运行目录: " + RuntimePaths.GetRunDirectory());
            Console.WriteLine("配置路径: " + RuntimePaths.GetConfigPath(RuntimePaths.GetRunDirectory()));
            Console.WriteLine("配置加载和健康检查将在阶段 1.2-1.7 完成。");
            return 0;
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
    }
}
