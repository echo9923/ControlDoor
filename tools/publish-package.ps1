# ============================================================
# publish-package.ps1 -- 门禁服务一键打包脚本
# ------------------------------------------------------------
# 用途：编译 Release 版 ControlDoor.exe，并组装出符合
#       Stage8ServicePackageChecker 固定布局的现场服务包，
#       拷到任意 x64 Windows（已装 .NET Framework 4.8）电脑
#       解压后即可按文档安装为 Windows 服务运行。
#
# 典型调用（仓库根目录）：
#   powershell -ExecutionPolicy Bypass -File tools\publish-package.ps1
#   powershell -ExecutionPolicy Bypass -File tools\publish-package.ps1 -SkipBuild
#   powershell -ExecutionPolicy Bypass -File tools\publish-package.ps1 -UseNuGetReferences
#
# 海康 HCNetSDK、SqlServerTypes 不在 .csproj 也不在 git，
# 本脚本从 bin\Debug\net48 复用（该目录需先手动放置完整 SDK）。
# 默认也从 bin\Debug\net48 引用 Grpc.Core 等编译依赖，避免重装系统后
# 本机 NuGet 缓存缺失导致发布脚本无法编译。
# ============================================================

param(
    [string]$Configuration = "Release",
    [string]$OutputDir = "门禁publish\ServicePackage",
    [switch]$SkipBuild,
    [switch]$NoClean,
    [switch]$UseNuGetReferences
)

$ErrorActionPreference = "Stop"
$OutputEncoding = [System.Text.Encoding]::UTF8
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# ------------------------------------------------------------
# 路径定位
# ------------------------------------------------------------
$RepoRoot  = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$Csproj    = Join-Path $RepoRoot "src\ControlDoor\ControlDoor.csproj"
$ProjectDir = Split-Path -Parent $Csproj
$SrcConfig = Join-Path $RepoRoot "src\ControlDoor\Configuration"
$BuildTempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "ControlDoorPublishBuild"
$BuildBaseOut = Join-Path $BuildTempRoot "bin\publish-$Configuration"
$BuildBaseObj = Join-Path $BuildTempRoot "obj-publish"
$ReleaseOut = Join-Path $BuildBaseOut "$Configuration\net48"
$DebugOut  = Join-Path $RepoRoot "src\ControlDoor\bin\Debug\net48"
$ServiceScripts = Join-Path $RepoRoot "tools\service"
$PackageDocs = Join-Path $RepoRoot "docs\stage8\package-docs"
$TestScript = Join-Path $RepoRoot "tools\test-service-package.ps1"
if ([System.IO.Path]::IsPathRooted($OutputDir)) {
    $PackageRoot = [System.IO.Path]::GetFullPath($OutputDir)
} else {
    $PackageRoot = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $OutputDir))
}

function Write-Step([string]$msg)
{
    Write-Host ""
    Write-Host "======================================" -ForegroundColor DarkCyan
    Write-Host $msg -ForegroundColor Cyan
    Write-Host "======================================" -ForegroundColor DarkCyan
}
function Write-Ok([string]$msg)   { Write-Host "[OK]   $msg" -ForegroundColor Green }
function Write-Warn2([string]$msg){ Write-Host "[WARN] $msg" -ForegroundColor Yellow }
function Write-Err([string]$msg)  { Write-Host "[FAIL] $msg" -ForegroundColor Red }

Write-Host "门禁服务打包脚本" -ForegroundColor White
Write-Host "仓库根目录 : $RepoRoot"
Write-Host "输出目录   : $PackageRoot"
Write-Host "编译配置   : $Configuration"
Write-Host "编译依赖   : $(if ($UseNuGetReferences) { 'NuGet' } else { 'bin\Debug\net48 本地 DLL' })"

# ------------------------------------------------------------
# 前置检查：海康 SDK 必须先在 bin\Debug\net48 放好
# ------------------------------------------------------------
Write-Step "1/7 前置检查"
if (-not (Test-Path -LiteralPath $Csproj -PathType Leaf)) {
    Write-Err "找不到工程文件: $Csproj"
    exit 1
}
$hikSdkSource = Join-Path $DebugOut "HCNetSDK.dll"
if (-not (Test-Path -LiteralPath $hikSdkSource -PathType Leaf)) {
    Write-Err "海康 SDK 缺失: $hikSdkSource"
    Write-Err "HCNetSDK.dll 等原生 DLL 不在 .csproj 也不在 git，请先将其完整放置到 bin\Debug\net48\。"
    exit 1
}
Write-Ok "海康 SDK 来源: $DebugOut"

if (-not $UseNuGetReferences) {
    $localReferenceFiles = @(
        "Grpc.Core.dll",
        "Grpc.Core.Api.dll",
        "System.Buffers.dll",
        "System.Memory.dll",
        "System.Numerics.Vectors.dll",
        "System.Runtime.CompilerServices.Unsafe.dll"
    )
    foreach ($file in $localReferenceFiles) {
        $path = Join-Path $DebugOut $file
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            Write-Err "本地编译依赖缺失: $path"
            Write-Err "默认构建会直接引用 bin\Debug\net48\ 下的 DLL；如需改用 NuGet，请加 -UseNuGetReferences。"
            exit 1
        }
    }
    Write-Ok "本地编译依赖齐全: Grpc.Core / System.Memory 等 DLL"
}

# ------------------------------------------------------------
# 2. Release 编译
# ------------------------------------------------------------
if ($SkipBuild) {
    Write-Step "2/7 编译（已跳过 -SkipBuild）"
    if (-not (Test-Path -LiteralPath (Join-Path $ReleaseOut "ControlDoor.exe") -PathType Leaf)) {
        Write-Err "跳过编译但 Release 输出不存在: $ReleaseOut"
        exit 1
    }
    Write-Warn2 "复用现有 Release 产物: $ReleaseOut"
} else {
    Write-Step "2/7 编译 $Configuration"

    # 工程文件已显式排除 obj* 中间源码；这里仅尽量清理旧输出，清理失败不阻断新打包。
    $defaultObj = Join-Path $ProjectDir "obj"
    if (Test-Path -LiteralPath $defaultObj -PathType Container) {
        foreach ($stale in @("Debug", "Release")) {
            $stalePath = Join-Path $defaultObj $stale
            if (Test-Path -LiteralPath $stalePath -PathType Container) {
                Write-Host "清理旧中间输出: $stalePath"
                try {
                    Remove-Item -LiteralPath $stalePath -Recurse -Force
                }
                catch {
                    Write-Warn2 "旧中间输出清理失败，已跳过: $($_.Exception.Message)"
                }
            }
        }
    }

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnet) {
        Write-Host "使用 dotnet 编译..."
        $buildArgs = @(
            "build",
            $Csproj,
            "-c",
            $Configuration,
            "--verbosity",
            "minimal"
        )
        $buildArgs += "/p:BaseOutputPath=$BuildBaseOut\"
        $buildArgs += "/p:BaseIntermediateOutputPath=$BuildBaseObj\"
        if (-not $UseNuGetReferences) {
            $buildArgs += "--no-restore"
            $buildArgs += "--no-incremental"
            $buildArgs += "/p:UseLocalDllReferences=true"
            $buildArgs += "/p:LocalDependencyDir=$DebugOut"
            $buildArgs += "/p:SkipResolvePackageAssets=true"
        }
        & dotnet @buildArgs
        if ($LASTEXITCODE -ne 0) {
            Write-Err "dotnet build 失败，退出码 $LASTEXITCODE。"
            exit $LASTEXITCODE
        }
    } else {
        $msbuild = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
        if (-not (Test-Path -LiteralPath $msbuild -PathType Leaf)) {
            Write-Err "找不到 dotnet，也找不到 MSBuild: $msbuild"
            exit 1
        }
        Write-Host "dotnet 不可用，回退到 MSBuild..."
        $msbuildTarget = if ($UseNuGetReferences) { "/t:Restore;Build" } else { "/t:Build" }
        $msbuildArgs = @(
            $Csproj,
            $msbuildTarget,
            "/p:Configuration=$Configuration",
            "/verbosity:minimal",
            "/p:BaseOutputPath=$BuildBaseOut\",
            "/p:BaseIntermediateOutputPath=$BuildBaseObj\"
        )
        if (-not $UseNuGetReferences) {
            $msbuildArgs += "/p:UseLocalDllReferences=true"
            $msbuildArgs += "/p:LocalDependencyDir=$DebugOut"
            $msbuildArgs += "/p:SkipResolvePackageAssets=true"
        }
        & $msbuild @msbuildArgs
        if ($LASTEXITCODE -ne 0) {
            Write-Err "MSBuild 失败，退出码 $LASTEXITCODE。"
            exit $LASTEXITCODE
        }
    }
    Write-Ok "编译完成: $ReleaseOut"
}

# ------------------------------------------------------------
# 3. 清空并重建输出目录
# ------------------------------------------------------------
Write-Step "3/7 准备输出目录"
if ((Test-Path -LiteralPath $PackageRoot) -and -not $NoClean) {
    Write-Host "清空旧目录: $PackageRoot"
    Remove-Item -LiteralPath $PackageRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $PackageRoot | Out-Null
Write-Ok "输出目录就绪: $PackageRoot"

# ------------------------------------------------------------
# 4. 复制托管产物（来自 Release 输出）
# ------------------------------------------------------------
Write-Step "4/7 复制托管产物"
$managedFiles = @(
    "ControlDoor.exe",
    "ControlDoor.exe.config",
    "ControlDoor.pdb",
    "Grpc.Core.dll",
    "Grpc.Core.Api.dll",
    "grpc_csharp_ext.x64.dll",
    "grpc_csharp_ext.x86.dll",
    "System.Buffers.dll",
    "System.Memory.dll",
    "System.Numerics.Vectors.dll",
    "System.Runtime.CompilerServices.Unsafe.dll"
)
foreach ($file in $managedFiles) {
    $src = Join-Path $ReleaseOut $file
    if (Test-Path -LiteralPath $src -PathType Leaf) {
        Copy-Item -LiteralPath $src -Destination $PackageRoot -Force
    } else {
        Write-Warn2 "Release 缺少（已跳过）: $file"
    }
}
Write-Ok "托管产物已复制"

# ------------------------------------------------------------
# 5. 复制原生依赖（来自 bin\Debug\net48，因为它们不在 .csproj）
#    策略：复制 Debug 里所有 *.dll（-Force 覆盖同名托管 DLL，两边一致）
#          再复制 HCNetSDKCom\ 和 SqlServerTypes\ 子目录
# ------------------------------------------------------------
Write-Step "5/7 复制原生依赖（海康 SDK / SqlServerTypes）"
$nativeDlls = Get-ChildItem -LiteralPath $DebugOut -Filter "*.dll" -File
$copied = 0
foreach ($dll in $nativeDlls) {
    Copy-Item -LiteralPath $dll.FullName -Destination $PackageRoot -Force
    $copied++
}
Write-Ok "平铺复制 $copied 个 DLL 到包根"

$hcComSrc = Join-Path $DebugOut "HCNetSDKCom"
if (Test-Path -LiteralPath $hcComSrc -PathType Container) {
    Copy-Item -LiteralPath $hcComSrc -Destination $PackageRoot -Recurse -Force
    Write-Ok "复制 HCNetSDKCom 子目录"
} else {
    Write-Warn2 "未找到 HCNetSDKCom 子目录（部分海康组件可能依赖它）"
}

$sqlTypesSrc = Join-Path $DebugOut "SqlServerTypes"
if (Test-Path -LiteralPath $sqlTypesSrc -PathType Container) {
    Copy-Item -LiteralPath $sqlTypesSrc -Destination $PackageRoot -Recurse -Force
    Write-Ok "复制 SqlServerTypes 子目录"
} else {
    Write-Err "SqlServerTypes 缺失: $sqlTypesSrc"
    exit 1
}

# ------------------------------------------------------------
# 6. 配置 / 运行期目录 / 服务脚本 / 现场文档
# ------------------------------------------------------------
Write-Step "6/7 组装配置、脚本与文档"

# 6.1 配置目录：用 deploy 模板覆盖 appsettings.json
$cfgDest = Join-Path $PackageRoot "Configuration"
New-Item -ItemType Directory -Force -Path $cfgDest | Out-Null
Copy-Item -LiteralPath (Join-Path $SrcConfig "devices.json") -Destination $cfgDest -Force
Copy-Item -LiteralPath (Join-Path $SrcConfig "appsettings.deploy.json") -Destination $cfgDest -Force
Copy-Item -LiteralPath (Join-Path $SrcConfig "appsettings.deploy.json") -Destination (Join-Path $cfgDest "appsettings.json") -Force
Write-Ok "配置已就位（appsettings.json 由 deploy 模板生成）"

# 6.2 运行期目录（空目录占位）
foreach ($dir in @("logs", "snapshots", "logs\sdk")) {
    $p = Join-Path $PackageRoot $dir
    New-Item -ItemType Directory -Force -Path $p | Out-Null
    Set-Content -LiteralPath (Join-Path $p ".gitkeep") -Value "" -Encoding ASCII
}
Write-Ok "运行期目录已创建（logs / snapshots / logs\sdk）"

# 6.3 服务脚本（-Path 才展开 *.ps1 通配符，-LiteralPath 不会）
$svcDest = Join-Path $PackageRoot "tools\service"
New-Item -ItemType Directory -Force -Path $svcDest | Out-Null
Copy-Item -Path (Join-Path $ServiceScripts "*.ps1") -Destination $svcDest -Force
$svcCount = (Get-ChildItem -LiteralPath $svcDest -Filter "*.ps1" -File).Count
if ($svcCount -lt 5) {
    Write-Err "服务脚本复制不完整，仅 $svcCount 个（预期 5 个）: $svcDest"
    exit 1
}
Write-Ok "服务脚本已复制（$svcCount 个）"

# 6.4 现场文档（中文文件名，同样用 -Path 展开通配符）
$docsDest = Join-Path $PackageRoot "docs"
New-Item -ItemType Directory -Force -Path $docsDest | Out-Null
Copy-Item -Path (Join-Path $PackageDocs "*.md") -Destination $docsDest -Force
$docCount = (Get-ChildItem -LiteralPath $docsDest -Filter "*.md" -File).Count
if ($docCount -lt 3) {
    Write-Err "现场文档复制不完整，仅 $docCount 个（预期 3 个）: $docsDest"
    exit 1
}
Write-Ok "现场文档已复制（$docCount 个）"

# ------------------------------------------------------------
# 7. 完整性自检
# ------------------------------------------------------------
Write-Step "7/7 完整性自检"
if (Test-Path -LiteralPath $TestScript -PathType Leaf) {
    & powershell -ExecutionPolicy Bypass -File $TestScript -PackageRoot $PackageRoot
    $checkExit = $LASTEXITCODE
    if ($checkExit -ne 0) {
        Write-Err "发布包完整性自检未通过（退出码 $checkExit）。"
        Write-Warn2 "若失败项为配置分组（如 FaceEnrollment），属仓库既有不一致，请单独处理；其余失败必须修复。"
        Write-Host ""
        Write-Host "发布包已生成，但请先确认上方失败项再交付现场。" -ForegroundColor Yellow
        exit $checkExit
    }
} else {
    Write-Warn2 "未找到校验脚本，跳过自检: $TestScript"
}

# ------------------------------------------------------------
# 汇总
# ------------------------------------------------------------
$totalSize = (Get-ChildItem -LiteralPath $PackageRoot -Recurse -File | Measure-Object -Property Length -Sum).Sum
$totalSizeMb = "{0:N2}" -f ($totalSize / 1MB)
$fileCount = (Get-ChildItem -LiteralPath $PackageRoot -Recurse -File).Count

Write-Step "打包完成"
Write-Ok "发布包路径: $PackageRoot"
Write-Ok "文件总数  : $fileCount"
Write-Ok "总大小    : $totalSizeMb MB"
Write-Host ""
Write-Host "现场安装步骤见: $PackageRoot\docs\部署说明.md" -ForegroundColor White
Write-Host "拷贝整个 门禁publish 目录到目标机即可。" -ForegroundColor White
exit 0
