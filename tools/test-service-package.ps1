param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot
)

$ErrorActionPreference = "Stop"

function Add-Result {
    param(
        [string]$Name,
        [bool]$Success,
        [string]$Message
    )

    [PSCustomObject]@{
        Name = $Name
        Success = $Success
        Message = $Message
    }
}

function Test-FileOrDirectory {
    param(
        [string]$RelativePath,
        [bool]$Directory
    )

    $path = Join-Path $script:Root $RelativePath
    if ($Directory) {
        return Add-Result $RelativePath (Test-Path -LiteralPath $path -PathType Container) "Directory: $path"
    }

    return Add-Result $RelativePath (Test-Path -LiteralPath $path -PathType Leaf) "File: $path"
}

function Remove-JsonComments {
    param(
        [string]$JsonText
    )

    $builder = New-Object System.Text.StringBuilder
    $inString = $false
    $i = 0
    while ($i -lt $JsonText.Length) {
        $current = $JsonText[$i]
        if ($inString) {
            [void]$builder.Append($current)
            if ($current -eq '\' -and ($i + 1) -lt $JsonText.Length) {
                [void]$builder.Append($JsonText[$i + 1])
                $i += 2
                continue
            }
            if ($current -eq '"') {
                $inString = $false
            }
            $i++
            continue
        }

        if ($current -eq '"') {
            $inString = $true
            [void]$builder.Append($current)
            $i++
            continue
        }

        if ($current -eq '/' -and ($i + 1) -lt $JsonText.Length -and $JsonText[$i + 1] -eq '/') {
            $i += 2
            while ($i -lt $JsonText.Length -and $JsonText[$i] -ne "`n" -and $JsonText[$i] -ne "`r") {
                $i++
            }
            continue
        }

        if ($current -eq '/' -and ($i + 1) -lt $JsonText.Length -and $JsonText[$i + 1] -eq '*') {
            $i += 2
            while (($i + 1) -lt $JsonText.Length -and -not ($JsonText[$i] -eq '*' -and $JsonText[$i + 1] -eq '/')) {
                $i++
            }
            $i += 2
            continue
        }

        [void]$builder.Append($current)
        $i++
    }

    return $builder.ToString()
}

$script:Root = [System.IO.Path]::GetFullPath($PackageRoot)
$results = New-Object System.Collections.Generic.List[object]

$deployDoc = (-join ([char[]]@([char]0x90E8, [char]0x7F72, [char]0x8BF4, [char]0x660E))) + ".md"
$preflightDoc = (-join ([char[]]@([char]0x8FD0, [char]0x884C, [char]0x524D, [char]0x68C0, [char]0x67E5))) + ".md"
$jointTestDoc = (-join ([char[]]@([char]0x8054, [char]0x8C03, [char]0x8BB0, [char]0x5F55, [char]0x6A21, [char]0x677F))) + ".md"

$results.Add((Add-Result "PackageRoot" (Test-Path -LiteralPath $script:Root -PathType Container) $script:Root))

$layout = @(
    @{ Path = "ControlDoor.exe"; Directory = $false },
    @{ Path = "ControlDoor.exe.config"; Directory = $false },
    @{ Path = "Configuration"; Directory = $true },
    @{ Path = "Configuration\appsettings.json"; Directory = $false },
    @{ Path = "Configuration\devices.json"; Directory = $false },
    @{ Path = "logs"; Directory = $true },
    @{ Path = "snapshots"; Directory = $true },
    @{ Path = "tools\service"; Directory = $true },
    @{ Path = "tools\service\common-service.ps1"; Directory = $false },
    @{ Path = "tools\service\install-service.ps1"; Directory = $false },
    @{ Path = "tools\service\start-service.ps1"; Directory = $false },
    @{ Path = "tools\service\stop-service.ps1"; Directory = $false },
    @{ Path = "tools\service\uninstall-service.ps1"; Directory = $false },
    @{ Path = "docs"; Directory = $true },
    @{ Path = Join-Path "docs" $deployDoc; Directory = $false },
    @{ Path = Join-Path "docs" $preflightDoc; Directory = $false },
    @{ Path = Join-Path "docs" $jointTestDoc; Directory = $false }
)

foreach ($item in $layout) {
    $results.Add((Test-FileOrDirectory $item.Path $item.Directory))
}

$sdkCandidates = @(
    (Join-Path $script:Root "HCNetSDK.dll"),
    (Join-Path $script:Root "sdk\Hikvision\HCNetSDK.dll")
)
$sdkFound = $false
foreach ($candidate in $sdkCandidates) {
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        $sdkFound = $true
        $results.Add((Add-Result "Hikvision SDK DLL" $true "Found HCNetSDK.dll: $candidate"))
        break
    }
}
if (-not $sdkFound) {
    $results.Add((Add-Result "Hikvision SDK DLL" $false "Missing HCNetSDK.dll; expected beside exe or under sdk\Hikvision."))
}

$sqlTypesCandidates = @(
    (Join-Path $script:Root "SqlServerTypes"),
    (Join-Path $script:Root "sdk\SqlServerTypes")
)
$sqlTypesFound = $false
foreach ($candidate in $sqlTypesCandidates) {
    if (Test-Path -LiteralPath $candidate) {
        $sqlTypesFound = $true
        $results.Add((Add-Result "SqlServerTypes" $true "Found SqlServerTypes: $candidate"))
        break
    }
}
if (-not $sqlTypesFound) {
    $results.Add((Add-Result "SqlServerTypes" $false "Missing SqlServerTypes dependency directory."))
}

$appsettings = Join-Path $script:Root "Configuration\appsettings.json"
if (Test-Path -LiteralPath $appsettings -PathType Leaf) {
    try {
        $rawJson = Get-Content -LiteralPath $appsettings -Raw -Encoding UTF8
        $json = Remove-JsonComments $rawJson | ConvertFrom-Json
        $requiredGroups = @(
            "Service",
            "Database",
            "Logging",
            "DeviceRuntime",
            "HikvisionSdk",
            "DeviceLifecycle",
            "DeviceOperationRetry",
            "FaceEventLogging",
            "FaceEnrollment",
            "CameraAlarmDoorInterlock"
        )

        $missing = @()
        foreach ($group in $requiredGroups) {
            if (-not ($json.PSObject.Properties.Name -contains $group)) {
                $missing += $group
            }
        }

        $results.Add((Add-Result "Configuration groups" ($missing.Count -eq 0) ("Missing groups: " + ($missing -join ", "))))
    }
    catch {
        $results.Add((Add-Result "Configuration JSON" $false $_.Exception.Message))
    }
}

foreach ($dir in @("logs", "snapshots")) {
    $path = Join-Path $script:Root $dir
    try {
        New-Item -ItemType Directory -Force -Path $path | Out-Null
        $probe = Join-Path $path ".stage8-write-probe"
        Set-Content -LiteralPath $probe -Value "ok" -Encoding ASCII
        Remove-Item -LiteralPath $probe -Force
        $results.Add((Add-Result "$dir writable" $true $path))
    }
    catch {
        $results.Add((Add-Result "$dir writable" $false $_.Exception.Message))
    }
}

foreach ($result in $results) {
    $prefix = if ($result.Success) { "[PASS]" } else { "[FAIL]" }
    Write-Host ("{0} {1}: {2}" -f $prefix, $result.Name, $result.Message)
}

$failedResults = @($results | Where-Object { -not $_.Success })
if ($failedResults.Count -gt 0) {
    Write-Host "Service package check failed."
    exit 1
}

Write-Host "Service package check passed."
exit 0
