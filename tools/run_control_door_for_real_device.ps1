param(
    [string]$Configuration = "Debug",
    [switch]$SkipBuild,
    [switch]$ValidateConfigOnly
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$solutionPath = Join-Path $repoRoot "ControlEntradaSalida.sln"
$exePath = Join-Path $repoRoot "src\ControlDoor\bin\$Configuration\net48\ControlDoor.exe"
$configPath = Join-Path $repoRoot "src\ControlDoor\bin\$Configuration\net48\Configuration\appsettings.json"

Write-Host "ControlDoor 真实设备联调启动辅助"
Write-Host "仓库: $repoRoot"
Write-Host "配置: $Configuration"

if (-not $SkipBuild) {
    Write-Host "构建最新 ControlDoor..."
    dotnet build $solutionPath --configuration $Configuration --verbosity minimal
}

if (-not (Test-Path $exePath)) {
    throw "未找到 ControlDoor.exe: $exePath"
}

if (-not (Test-Path $configPath)) {
    throw "未找到运行配置: $configPath"
}

Write-Host "运行程序: $exePath"
Write-Host "运行配置: $configPath"
Write-Host ""
Write-Host "请确认配置中的数据库、gRPC 端口和 DeviceOperationRetry："
$settings = Get-Content -Path $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
Write-Host ("  gRPC 端口: {0}" -f $settings.Service.GrpcListenPort)
Write-Host ("  数据库: {0}" -f $settings.Database.ConnectionString)
Write-Host ("  Retry ScanIntervalSeconds: {0}" -f $settings.DeviceOperationRetry.ScanIntervalSeconds)
Write-Host ("  Retry InitialRetryDelaySeconds: {0}" -f $settings.DeviceOperationRetry.InitialRetryDelaySeconds)
Write-Host ""

Write-Host "验证配置..."
& $exePath --validate-config
if ($LASTEXITCODE -ne 0) {
    throw "配置验证失败，退出码 $LASTEXITCODE"
}

if ($ValidateConfigOnly) {
    Write-Host "只验证配置，不启动服务。"
    exit 0
}

Write-Host ""
Write-Host "启动 ControlDoor 控制台模式。停止请按 Ctrl+C 或回车。"
& $exePath --console
exit $LASTEXITCODE

