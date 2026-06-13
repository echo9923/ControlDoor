param(
    [string]$PackageRoot = "",
    [ValidateSet("Automatic", "Manual", "Disabled")]
    [string]$StartupType = "Automatic",
    [switch]$SkipValidate
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\common-service.ps1"

$root = Get-ControlDoorPackageRoot $PackageRoot
$exePath = Assert-ControlDoorExe $root

if (-not $SkipValidate) {
    Invoke-ControlDoorValidateConfig $root
}

$existing = Get-ControlDoorService
if ($existing -ne $null) {
    $current = Get-CimInstance Win32_Service -Filter "Name='$ControlDoorServiceName'"
    if ($current -ne $null -and $current.PathName -ne ('"' + $exePath + '"')) {
        Write-Host ("Service already exists; updating binPath from {0} to {1}" -f $current.PathName, $exePath)
        sc.exe config $ControlDoorServiceName binPath= ('"' + $exePath + '"') DisplayName= $ControlDoorDisplayName | Out-Host
    }
    else {
        Write-Host "Service already exists with matching path."
    }
}
else {
    New-Service -Name $ControlDoorServiceName -DisplayName $ControlDoorDisplayName -BinaryPathName ('"' + $exePath + '"') -StartupType $StartupType | Out-Null
    Write-Host ("Service installed: {0}" -f $ControlDoorServiceName)
}

sc.exe description $ControlDoorServiceName "ControlDoor access control service" | Out-Host
sc.exe failure $ControlDoorServiceName reset= 86400 actions= restart/60000/restart/60000/none/0 | Out-Host
Set-Service -Name $ControlDoorServiceName -StartupType $StartupType
Write-Host ("Service startup type: {0}" -f $StartupType)
