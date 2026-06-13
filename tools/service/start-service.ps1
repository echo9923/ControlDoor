param(
    [string]$PackageRoot = "",
    [switch]$SkipValidate,
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\common-service.ps1"

if (-not $SkipValidate) {
    Invoke-ControlDoorValidateConfig $PackageRoot
}

$service = Get-ControlDoorService
if ($service -eq $null) {
    throw "ControlDoor service is not installed."
}

if ($service.Status -ne "Running") {
    Start-Service -Name $ControlDoorServiceName
}

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
do {
    $service = Get-ControlDoorService
    if ($service -ne $null -and $service.Status -eq "Running") {
        Write-Host "ControlDoor service is Running."
        exit 0
    }

    Start-Sleep -Milliseconds 500
} while ((Get-Date) -lt $deadline)

throw "ControlDoor service did not reach Running within $TimeoutSeconds seconds."
