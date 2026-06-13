using System.Collections.Generic;

namespace ControlDoor.Deployment
{
    public sealed class Stage8TestLayer
    {
        public Stage8TestLayer(string id, string name, bool automatic, bool requiresRealDevice, string purpose)
        {
            Id = id;
            Name = name;
            Automatic = automatic;
            RequiresRealDevice = requiresRealDevice;
            Purpose = purpose;
        }

        public string Id { get; }

        public string Name { get; }

        public bool Automatic { get; }

        public bool RequiresRealDevice { get; }

        public string Purpose { get; }
    }

    public sealed class Stage8ExecutionStep
    {
        public Stage8ExecutionStep(int order, string commandOrAction, string description)
        {
            Order = order;
            CommandOrAction = commandOrAction;
            Description = description;
        }

        public int Order { get; }

        public string CommandOrAction { get; }

        public string Description { get; }
    }

    public static class Stage8TestMatrix
    {
        public static IReadOnlyList<Stage8TestLayer> Layers { get; } = new List<Stage8TestLayer>
        {
            new Stage8TestLayer("L1", "单元测试", true, false, "验证解析、校验、状态机、退避、字段映射。"),
            new Stage8TestLayer("L2", "gRPC 契约测试", true, false, "验证方法名、JSON string marshaller、请求别名、响应字段。"),
            new Stage8TestLayer("L3", "Mock SDK 集成测试", true, false, "验证设备通道、mock 网关、业务编排。"),
            new Stage8TestLayer("L4", "数据库兼容测试", true, false, "验证现有表读写语义和结构快照。"),
            new Stage8TestLayer("L5", "发布包检查", true, false, "验证文件、目录、配置模板、DLL 放置。"),
            new Stage8TestLayer("L6", "现场设备联调", false, true, "验证真实海康设备和 SDK 行为。")
        }.AsReadOnly();

        public static IReadOnlyList<Stage8ExecutionStep> ExecutionSteps { get; } = new List<Stage8ExecutionStep>
        {
            new Stage8ExecutionStep(1, "nuget restore ControlEntradaSalida.sln", "还原 packages.config 依赖。"),
            new Stage8ExecutionStep(2, "dotnet build ControlEntradaSalida.sln --verbosity minimal", "推荐构建命令。"),
            new Stage8ExecutionStep(3, "msbuild ControlEntradaSalida.sln /t:Build /p:Configuration=Debug", "Debug 构建验证。"),
            new Stage8ExecutionStep(4, "msbuild ControlEntradaSalida.sln /p:Configuration=Release", "Release 构建验证。"),
            new Stage8ExecutionStep(5, "dotnet test tests\\ControlEntradaSalida.Tests\\ControlEntradaSalida.Tests.csproj --verbosity minimal", "单元与契约测试。"),
            new Stage8ExecutionStep(6, "dotnet test tests\\ControlEntradaSalida.IntegrationTests\\ControlEntradaSalida.IntegrationTests.csproj --verbosity minimal", "mock 集成与数据库兼容测试。"),
            new Stage8ExecutionStep(7, "tools\\test-service-package.ps1 -PackageRoot 门禁publish\\ServicePackage", "发布包检查。"),
            new Stage8ExecutionStep(8, "ControlDoor.exe --validate-config", "发布包运行前检查。"),
            new Stage8ExecutionStep(9, "L6 手动联调", "仅在现场设备配置和白名单开启后执行。")
        }.AsReadOnly();
    }
}
