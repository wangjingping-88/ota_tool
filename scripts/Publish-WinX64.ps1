[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'publish\win-x64'),
    [string]$Version = '0.2.2',
    [string]$SourceRevisionId = ''
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src\OtaTool.App\OtaTool.App.csproj'
$updaterProjectPath = Join-Path $repositoryRoot 'src\OtaTool.Updater\OtaTool.Updater.csproj'
$updaterOutputPath = Join-Path $repositoryRoot 'artifacts\updater-win-x64'
$resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$allowedOutputRoots = @(
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'publish')),
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
)
$isAllowedOutput = $allowedOutputRoots | Where-Object {
    $resolvedOutputPath.StartsWith("$($_)$([System.IO.Path]::DirectorySeparatorChar)", [System.StringComparison]::OrdinalIgnoreCase)
}
if (-not $isAllowedOutput) {
    throw '发布输出目录必须位于仓库的 publish 或 artifacts 子目录内。'
}
$OutputPath = $resolvedOutputPath

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "版本号必须是 MAJOR.MINOR.PATCH：$Version"
}

$informationalVersion = $Version

if (Test-Path -LiteralPath $OutputPath) {
    $outputPathPrefix = $OutputPath.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $runningOutputProcesses = @(Get-Process -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            $processPath = $_.Path
            if (-not [string]::IsNullOrWhiteSpace($processPath) -and
                [System.IO.Path]::GetFullPath($processPath).StartsWith(
                    $outputPathPrefix,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                "$($_.ProcessName) (PID $($_.Id))"
            }
        } catch {
            # 某些系统进程不允许读取可执行文件路径，不影响目标目录占用检查。
        }
    })
    if ($runningOutputProcesses.Count -gt 0) {
        throw "发布目录仍有运行中的程序：$($runningOutputProcesses -join '、')。请关闭这些程序，或通过 -OutputPath 发布到其他目录；本次未清理任何文件。"
    }
}

if (Test-Path -LiteralPath $OutputPath) {
    Remove-Item -LiteralPath $OutputPath -Recurse -Force
}
if (Test-Path -LiteralPath $updaterOutputPath) {
    Remove-Item -LiteralPath $updaterOutputPath -Recurse -Force
}

dotnet publish $projectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $OutputPath `
    -p:Version=$Version `
    -p:VersionPrefix=$Version `
    -p:AssemblyVersion="$Version.0" `
    -p:FileVersion="$Version.0" `
    -p:InformationalVersion=$informationalVersion `
    -p:SourceRevisionId=$SourceRevisionId

dotnet publish $updaterProjectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $updaterOutputPath `
    -p:Version=$Version `
    -p:VersionPrefix=$Version `
    -p:AssemblyVersion="$Version.0" `
    -p:FileVersion="$Version.0" `
    -p:InformationalVersion=$informationalVersion `
    -p:SourceRevisionId=$SourceRevisionId

Copy-Item -LiteralPath (Join-Path $updaterOutputPath 'OtaTool.Updater.exe') -Destination $OutputPath -Force

# v0.1.2～v0.1.7 的更新器会在解压后检查这些旧版路径。仅 v0.1.9 热修复包
# 保留零字节占位文件用于跨版本更新；v0.1.9 及后续更新器不再检查这些文件。
$legacyUpdaterCompatibilityFiles = @(
    'Tools\OTA_TOOL\OTA_TOOL.exe',
    'Tools\OTA_TOOL\Qt5Core.dll',
    'Tools\OTA_TOOL\platforms\qwindows.dll',
    'Scripts\TestPatchWithOtaTool.ps1'
)
if ($Version -eq '0.1.9') {
    foreach ($compatibilityFile in $legacyUpdaterCompatibilityFiles) {
        $compatibilityPath = Join-Path $OutputPath $compatibilityFile
        $compatibilityDirectory = Split-Path -Parent $compatibilityPath
        New-Item -ItemType Directory -Path $compatibilityDirectory -Force | Out-Null
        [System.IO.File]::WriteAllBytes($compatibilityPath, [byte[]]@())
    }

    $compatibilityNoticePath = Join-Path $OutputPath 'Tools\OTA_TOOL\README.txt'
    Set-Content -LiteralPath $compatibilityNoticePath -Encoding UTF8 -Value @'
这些文件仅用于兼容 v0.1.2～v0.1.7 更新器的历史文件清单检查。
当前版本使用 partition_patch_verify.exe 完成原生 Patch 还原验证，不会加载此目录中的占位文件。
'@
}

$requiredFiles = @(
    'OtaTool.App.exe',
    'OtaTool.Updater.exe',
    'bsdiff_cmd.exe',
    'partition_patch_verify.exe',
    'Licenses\partition_patch_verify.md',
    'analyze_ota_logs.py'
)
if ($Version -eq '0.1.9') {
    $requiredFiles += $legacyUpdaterCompatibilityFiles + @('Tools\OTA_TOOL\README.txt')
}
foreach ($requiredFile in $requiredFiles) {
    $requiredPath = Join-Path $OutputPath $requiredFile
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "发布目录缺少必要文件：$requiredFile"
    }
}

Write-Host "OTA 测试平台已发布到：$OutputPath"
