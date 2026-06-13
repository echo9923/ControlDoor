$ControlDoorServiceName = "ControlDoor"
$ControlDoorDisplayName = "ControlDoor"

function Get-ControlDoorPackageRoot {
    param(
        [string]$PackageRoot
    )

    if ([string]::IsNullOrWhiteSpace($PackageRoot)) {
        return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
    }

    return [System.IO.Path]::GetFullPath($PackageRoot)
}

function Get-ControlDoorExePath {
    param(
        [string]$PackageRoot
    )

    return Join-Path (Get-ControlDoorPackageRoot $PackageRoot) "ControlDoor.exe"
}

function Get-ControlDoorService {
    return Get-Service -Name $ControlDoorServiceName -ErrorAction SilentlyContinue
}

function Assert-ControlDoorExe {
    param(
        [string]$PackageRoot
    )

    $exePath = Get-ControlDoorExePath $PackageRoot
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
        throw "ControlDoor.exe not found: $exePath"
    }

    return $exePath
}

function Invoke-ControlDoorValidateConfig {
    param(
        [string]$PackageRoot
    )

    $exePath = Assert-ControlDoorExe $PackageRoot
    Push-Location (Split-Path -Parent $exePath)
    try {
        & $exePath --validate-config
        if ($LASTEXITCODE -ne 0) {
            throw "ControlDoor.exe --validate-config failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}
