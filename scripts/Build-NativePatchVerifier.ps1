[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'assets\native\partition_patch_verify.exe')
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $repositoryRoot 'native\patch_verify\partition_patch_verify.cpp'
$resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $resolvedOutputPath
$buildDirectory = Join-Path $repositoryRoot 'artifacts\native-patch-verify-build'
$objectPath = Join-Path $buildDirectory 'partition_patch_verify.obj'

if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "缺少原生验证器源码：$sourcePath"
}

$vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswherePath -PathType Leaf)) {
    throw '未找到 vswhere.exe，请安装 Visual Studio C++ Build Tools。'
}

$visualStudioPath = (& $vswherePath `
    -latest `
    -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath).Trim()
if ([string]::IsNullOrWhiteSpace($visualStudioPath)) {
    throw '未找到 Visual Studio C++ x64 编译工具。'
}

$vcvarsPath = Join-Path $visualStudioPath 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path -LiteralPath $vcvarsPath -PathType Leaf)) {
    throw "未找到 x64 编译环境脚本：$vcvarsPath"
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $buildDirectory -Force | Out-Null

$compileCommand = @(
    'call', ('"{0}"' -f $vcvarsPath), '1>nul',
    '&&', 'cl.exe', '/nologo', '/std:c++17', '/O2', '/MT', '/EHsc', '/utf-8', '/W4', '/WX',
    ('/Fo:"{0}"' -f $objectPath),
    ('"{0}"' -f $sourcePath),
    ('/Fe:"{0}"' -f $resolvedOutputPath),
    '/link', '/INCREMENTAL:NO'
) -join ' '

& $env:ComSpec /d /s /c $compileCommand
if ($LASTEXITCODE -ne 0) {
    throw "原生 Patch 验证器编译失败，退出码：$LASTEXITCODE"
}
if (-not (Test-Path -LiteralPath $resolvedOutputPath -PathType Leaf)) {
    throw "编译完成但未生成验证器：$resolvedOutputPath"
}

Write-Host "原生 Patch 验证器已生成：$resolvedOutputPath"
