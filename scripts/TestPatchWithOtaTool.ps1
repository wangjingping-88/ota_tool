param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("sync", "async", "node", "gateway")]
    [string]$Role,

    [string]$FirmwareDirectory = "D:\serial-log-data\ota\firmware",
    [string]$PackageDirectory = "D:\serial-log-data\ota\packages",
    [string]$WorkDirectory = "",
    [string]$OtaToolPath = "D:\tools\OTA_TOOL\OTA_TOOL.exe",
    [string]$PatchToTest = "",
    [long]$PatchLimitBytes = 0,
    [ValidateRange(0, 1048576)]
    [int]$SkippedBootloaderBytes = 28672,
    [int]$TimeoutSeconds = 60,
    [switch]$KeepToolOpen
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($WorkDirectory)) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $WorkDirectory = Join-Path (Split-Path $PackageDirectory -Parent) "ota-tool-$Role-$timestamp"
}

$oldImage = Join-Path $FirmwareDirectory "$Role-old.bin"
$newImage = Join-Path $FirmwareDirectory "$Role-new.bin"
$patchName = "$Role-old.patch"
$restoredName = "$Role-old.new"
$patchPath = Join-Path $WorkDirectory $patchName
$restoredPath = Join-Path $WorkDirectory $restoredName
$finalPatchPath = Join-Path $PackageDirectory "$Role-a-to-b.patch"

function Get-FileDigest {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]
        [ValidateSet("MD5", "SHA256")]
        [string]$Algorithm
    )

    $stream = [System.IO.File]::OpenRead($Path)
    $hasher = switch ($Algorithm) {
        "MD5" { [System.Security.Cryptography.MD5]::Create() }
        "SHA256" { [System.Security.Cryptography.SHA256]::Create() }
    }
    try {
        return ([System.BitConverter]::ToString($hasher.ComputeHash($stream))).Replace("-", "")
    } finally {
        $hasher.Dispose()
        $stream.Dispose()
    }
}

foreach ($path in @($oldImage, $newImage, $OtaToolPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "File not found: $path"
    }
}

$oldHash = Get-FileDigest -Path $oldImage -Algorithm SHA256
$newHash = Get-FileDigest -Path $newImage -Algorithm SHA256
if ($oldHash -eq $newHash) {
    throw "$Role A/B images are identical."
}

if (Test-Path -LiteralPath $WorkDirectory) {
    if ((Get-ChildItem -LiteralPath $WorkDirectory -Force | Measure-Object).Count -ne 0) {
        throw "Work directory must be empty: $WorkDirectory"
    }
} else {
    New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null
}
New-Item -ItemType Directory -Path $PackageDirectory -Force | Out-Null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if (-not ("OtaToolNativeMethods" -as [type])) {
    Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class OtaToolNativeMethods
{
    public delegate bool EnumWindowsCallback(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDlgItem(IntPtr hWnd, int itemId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint message, IntPtr wParam, string lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
}
'@
}

$wmSetText = 0x000C
$bmClick = 0x00F5
$launchedProcess = $null

function Get-OtaToolProcess {
    $process = Get-Process -Name "OTA_TOOL" -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } |
        Select-Object -First 1
    if ($null -ne $process) {
        return $process
    }

    $script:launchedProcess = Start-Process -FilePath $OtaToolPath -WindowStyle Minimized -PassThru
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 200
        $script:launchedProcess.Refresh()
    } until ($script:launchedProcess.MainWindowHandle -ne 0 -or (Get-Date) -ge $deadline)

    if ($script:launchedProcess.MainWindowHandle -eq 0) {
        throw "Timed out waiting for the OTA_TOOL main window."
    }
    return $script:launchedProcess
}

function Get-TopLevelWindowByTitle {
    param([Parameter(Mandatory = $true)][string]$Title)

    $found = [IntPtr]::Zero
    [OtaToolNativeMethods]::EnumWindows(
        {
            param($windowHandle, $unused)
            if (-not [OtaToolNativeMethods]::IsWindowVisible($windowHandle)) {
                return $true
            }
            $text = New-Object System.Text.StringBuilder 512
            [void][OtaToolNativeMethods]::GetWindowText($windowHandle, $text, $text.Capacity)
            if ($text.ToString() -eq $Title) {
                $script:otaDialogHandle = $windowHandle
            }
            return $true
        },
        [IntPtr]::Zero) | Out-Null

    if ($null -ne $script:otaDialogHandle) {
        $found = $script:otaDialogHandle
        Remove-Variable -Scope Script -Name otaDialogHandle -ErrorAction SilentlyContinue
    }
    return $found
}

function Wait-TopLevelWindow {
    param([Parameter(Mandatory = $true)][string]$Title)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $windowHandle = Get-TopLevelWindowByTitle -Title $Title
        if ($windowHandle -ne [IntPtr]::Zero) {
            return $windowHandle
        }
        Start-Sleep -Milliseconds 100
    } until ((Get-Date) -ge $deadline)
    throw "Timed out waiting for window: $Title"
}

function Invoke-OtaButton {
    param(
        [Parameter(Mandatory = $true)]
        [System.Windows.Automation.AutomationElement]$Root,
        [Parameter(Mandatory = $true)]
        [string]$AutomationId
    )

    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $button = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $button) {
        throw "OTA_TOOL control not found: $AutomationId"
    }
    $invoke = $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $invoke.Invoke()
}

function Set-DialogPathAndConfirm {
    param(
        [Parameter(Mandatory = $true)][IntPtr]$DialogHandle,
        [Parameter(Mandatory = $true)][string]$AutomationId,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $dialog = [System.Windows.Automation.AutomationElement]::FromHandle($DialogHandle)
    $idCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $classCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ClassNameProperty,
        "Edit")
    $edit = $dialog.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.AndCondition($idCondition, $classCondition)))
    if ($null -eq $edit -or $edit.Current.NativeWindowHandle -eq 0) {
        throw "Path edit control not found: AutomationId=$AutomationId"
    }

    [void][OtaToolNativeMethods]::SendMessage(
        [IntPtr]$edit.Current.NativeWindowHandle,
        $wmSetText,
        [IntPtr]::Zero,
        $Path)

    $confirmButton = [OtaToolNativeMethods]::GetDlgItem($DialogHandle, 1)
    if ($confirmButton -eq [IntPtr]::Zero) {
        throw "Dialog confirm button not found."
    }
    [void][OtaToolNativeMethods]::SendMessage(
        $confirmButton,
        $bmClick,
        [IntPtr]::Zero,
        [IntPtr]::Zero)
    Start-Sleep -Milliseconds 300
}

function Wait-File {
    param([Parameter(Mandatory = $true)][string]$Path)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $item = Get-Item -LiteralPath $Path -ErrorAction SilentlyContinue
        if ($null -ne $item -and $item.Length -gt 0) {
            Start-Sleep -Milliseconds 300
            return Get-Item -LiteralPath $Path
        }
        Start-Sleep -Milliseconds 200
    } until ((Get-Date) -ge $deadline)
    throw "Timed out waiting for OTA_TOOL output: $Path"
}

try {
    $process = Get-OtaToolProcess
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)

    Invoke-OtaButton -Root $root -AutomationId "BsDiff.centralWidget.groupBox.verticalLayoutWidget_3.BrowserOldButton"
    $dialog = Wait-TopLevelWindow -Title "Open Old File"
    Set-DialogPathAndConfirm -DialogHandle $dialog -AutomationId "1148" -Path $oldImage

    Invoke-OtaButton -Root $root -AutomationId "BsDiff.centralWidget.groupBox.verticalLayoutWidget_3.BrowserNewButton"
    $dialog = Wait-TopLevelWindow -Title "Open New File"
    Set-DialogPathAndConfirm -DialogHandle $dialog -AutomationId "1148" -Path $newImage

    Invoke-OtaButton -Root $root -AutomationId "BsDiff.centralWidget.groupBox.verticalLayoutWidget_3.BrowserPathcButton"
    $dialog = Wait-TopLevelWindow -Title "Open Output Directory"
    Set-DialogPathAndConfirm -DialogHandle $dialog -AutomationId "1152" -Path $WorkDirectory

    if ([string]::IsNullOrWhiteSpace($PatchToTest)) {
        Invoke-OtaButton -Root $root -AutomationId "BsDiff.centralWidget.groupBox.horizontalLayoutWidget.GenerateButton"
        $patchItem = Wait-File -Path $patchPath
    } else {
        if (-not (Test-Path -LiteralPath $PatchToTest -PathType Leaf)) {
            throw "Patch file not found: $PatchToTest"
        }
        Copy-Item -LiteralPath $PatchToTest -Destination $patchPath -Force
        $patchItem = Get-Item -LiteralPath $patchPath
    }

    Invoke-OtaButton -Root $root -AutomationId "BsDiff.centralWidget.groupBox.horizontalLayoutWidget.TestButton"
    $restoredItem = Wait-File -Path $restoredPath

    $restoredHash = Get-FileDigest -Path $restoredPath -Algorithm SHA256
    $expectedBytes = [System.IO.File]::ReadAllBytes($newImage)
    if ($SkippedBootloaderBytes -gt 0) {
        $oldBytes = [System.IO.File]::ReadAllBytes($oldImage)
        if ($oldBytes.Length -lt $SkippedBootloaderBytes -or $expectedBytes.Length -lt $SkippedBootloaderBytes) {
            throw "Firmware image is smaller than skipped bootloader prefix: $SkippedBootloaderBytes bytes"
        }
        [System.Array]::Copy($oldBytes, 0, $expectedBytes, 0, $SkippedBootloaderBytes)
    }
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $expectedHash = ([System.BitConverter]::ToString($sha256.ComputeHash($expectedBytes))).Replace("-", "")
    } finally {
        $sha256.Dispose()
    }
    if ($restoredItem.Length -ne $expectedBytes.Length -or $restoredHash -ne $expectedHash) {
        throw "PatchTest output mismatch: expected=$expectedHash actual=$restoredHash expected_bytes=$($expectedBytes.Length) actual_bytes=$($restoredItem.Length) skipped_bootloader_bytes=$SkippedBootloaderBytes"
    }
    if ($PatchLimitBytes -gt 0 -and $patchItem.Length -gt $PatchLimitBytes) {
        throw "Patch exceeds configured limit $PatchLimitBytes bytes: $($patchItem.Length) bytes"
    }

    Copy-Item -LiteralPath $patchPath -Destination $finalPatchPath -Force
    $finalItem = Get-Item -LiteralPath $finalPatchPath
    $result = [ordered]@{
        role = $Role
        old_image = $oldImage
        new_image = $newImage
        patch = $finalPatchPath
        work_directory = $WorkDirectory
        patch_bytes = $finalItem.Length
        patch_md5 = (Get-FileDigest -Path $finalPatchPath -Algorithm MD5).ToLowerInvariant()
        patch_sha256 = (Get-FileDigest -Path $finalPatchPath -Algorithm SHA256).ToLowerInvariant()
        restored_sha256 = $restoredHash.ToLowerInvariant()
        new_image_sha256 = $newHash.ToLowerInvariant()
        expected_restored_sha256 = $expectedHash.ToLowerInvariant()
        skipped_bootloader_bytes = $SkippedBootloaderBytes
        patch_test = "passed"
    }
    $result | ConvertTo-Json
} finally {
    if (-not $KeepToolOpen -and $null -ne $launchedProcess -and -not $launchedProcess.HasExited) {
        $launchedProcess.CloseMainWindow() | Out-Null
        Start-Sleep -Milliseconds 500
        $launchedProcess.Refresh()
        if (-not $launchedProcess.HasExited) {
            Stop-Process -Id $launchedProcess.Id -Force
        }
    }
}
