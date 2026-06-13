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

if ($service.Status -ne "Stopped") {
    Stop-Service -Name $ControlDoorServiceName -ErrorAction Stop
}

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
do {
    $service = Get-ControlDoorService
    if ($service -eq $null -or $service.Status -eq "Stopped") {
        Write-Host "ControlDoor service is Stopped."
        exit 0
    }

    Start-Sleep -Milliseconds 500
} while ((Get-Date) -lt $deadline)

throw "ControlDoor service did not stop within $TimeoutSeconds seconds."
