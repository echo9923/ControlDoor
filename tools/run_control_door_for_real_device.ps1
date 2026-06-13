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

Write-Host "ControlDoor real-device smoke helper"
Write-Host "Repo: $repoRoot"
Write-Host "Configuration: $Configuration"

if (-not $SkipBuild) {
    Write-Host "Building ControlDoor..."
    dotnet build $solutionPath --configuration $Configuration --verbosity minimal
}

if (-not (Test-Path $exePath)) {
    throw "ControlDoor.exe not found: $exePath"
}

if (-not (Test-Path $configPath)) {
    throw "Runtime config not found: $configPath"
}

Write-Host "Executable: $exePath"
Write-Host "Runtime config: $configPath"
Write-Host ""
Write-Host "Check database, gRPC port, and DeviceOperationRetry in the config:"
$settings = Get-Content -Path $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
Write-Host ("  gRPC port: {0}" -f $settings.Service.GrpcListenPort)
Write-Host ("  Database: {0}" -f $settings.Database.ConnectionString)
Write-Host ("  Retry ScanIntervalSeconds: {0}" -f $settings.DeviceOperationRetry.ScanIntervalSeconds)
Write-Host ("  Retry InitialRetryDelaySeconds: {0}" -f $settings.DeviceOperationRetry.InitialRetryDelaySeconds)
Write-Host ""

Write-Host "Validating config..."
& $exePath --validate-config
if ($LASTEXITCODE -ne 0) {
    throw "Config validation failed with exit code $LASTEXITCODE."
}

if ($ValidateConfigOnly) {
    Write-Host "Config validation only; service is not started."
    exit 0
}

Write-Host ""
Write-Host "Starting ControlDoor console mode. Press Ctrl+C or Enter to stop."
& $exePath --console
exit $LASTEXITCODE
