param(
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\common-service.ps1"

$service = Get-ControlDoorService
if ($service -eq $null) {
    Write-Host "ControlDoor service is not installed."
    exit 0
}

& "$PSScriptRoot\stop-service.ps1" -TimeoutSeconds $TimeoutSeconds
if ($LASTEXITCODE -ne 0) {
    throw "Unable to stop ControlDoor service before uninstall."
}

sc.exe delete $ControlDoorServiceName | Out-Host
Write-Host "ControlDoor service removed. Configuration, logs, snapshots, and database data are retained."
