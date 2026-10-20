$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'tools\installation-paths.ps1')

if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    throw 'LOCALAPPDATA 未设置，无法确定当前用户的安装目录。'
}

$installDirectory = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'ReplayKeeper'))
$installedExe = Join-Path $installDirectory 'ReplayKeeper.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$expectedRunValue = '"' + $installedExe + '"'
$startupShortcut = Join-Path ([Environment]::GetFolderPath('Startup')) 'ReplayKeeper.lnk'
$currentSessionId = (Get-Process -Id $PID).SessionId

function Assert-OrdinaryPath([string] $path) {
    if (Test-Path -LiteralPath $path) {
        $item = Get-Item -LiteralPath $path -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "拒绝操作重解析路径：$path"
        }
    }
}

function Get-InstalledProcesses([string] $executable) {
    @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'ReplayKeeper.exe'" |
        Where-Object { Test-ReplayKeeperSameFile $_.ExecutablePath $executable })
}

function Get-CurrentInstalledProcesses {
    @(Get-InstalledProcesses $installedExe | Where-Object { $_.SessionId -eq $currentSessionId })
}

function Assert-NoOtherSessions {
    $other = @(Get-InstalledProcesses $installedExe | Where-Object { $_.SessionId -ne $currentSessionId })
    if ($other.Count -gt 0) {
        $sessions = ($other | Select-Object -ExpandProperty SessionId -Unique) -join ', '
        throw "安装路径的 ReplayKeeper 正在其他登录会话运行（SessionId: $sessions）；未修改本会话进程或安装文件：$installedExe"
    }
}

function Invoke-ControlCommand([string] $executable, [string] $argument) {
    $controlProcess = Start-Process -FilePath $executable -ArgumentList $argument -WindowStyle Hidden -PassThru
    try {
        if (-not $controlProcess.WaitForExit(10000)) {
            $controlProcess.Kill()
            throw "控制命令超时：$argument"
        }
        if ($controlProcess.ExitCode -ne 0) {
            throw "控制命令失败：$argument，退出码：$($controlProcess.ExitCode)"
        }
    }
    finally {
        $controlProcess.Dispose()
    }
}

function Get-StartupCommand {
    $properties = Get-ItemProperty -LiteralPath $runKey -Name 'ReplayKeeper' -ErrorAction SilentlyContinue
    if ($null -eq $properties) {
        return $null
    }
    return [string] $properties.ReplayKeeper
}

Assert-OrdinaryPath $installDirectory
Assert-OrdinaryPath $installedExe
Assert-OrdinaryPath $startupShortcut
if ((Test-Path -LiteralPath $installDirectory) -and -not (Test-Path -LiteralPath $installDirectory -PathType Container)) {
    throw "安装路径不是文件夹：$installDirectory"
}

$hasInstalledExe = Test-Path -LiteralPath $installedExe -PathType Leaf
Assert-NoOtherSessions
if ($hasInstalledExe -and @(Get-CurrentInstalledProcesses).Count -gt 0) {
    Invoke-ControlCommand $installedExe '--exit'
    $deadline = (Get-Date).AddSeconds(15)
    while (@(Get-CurrentInstalledProcesses).Count -gt 0) {
        if ((Get-Date) -ge $deadline) {
            throw "ReplayKeeper 在 15 秒内未退出；未删除安装文件：$installedExe"
        }
        Start-Sleep -Milliseconds 200
    }
}

Assert-NoOtherSessions
$currentRunValue = Get-StartupCommand
if ($null -ne $currentRunValue -and
    -not [string]::Equals($currentRunValue, $expectedRunValue, [System.StringComparison]::OrdinalIgnoreCase)) {
    Write-Warning "保留了指向其他路径的 ReplayKeeper 自启动项：$currentRunValue"
}
if ($hasInstalledExe) {
    Invoke-ControlCommand $installedExe '--disable-startup'
}
elseif ($null -ne $currentRunValue -and $currentRunValue -eq $expectedRunValue) {
    # The binary is already absent. Remove only this product's exact Run value.
    Remove-ItemProperty -LiteralPath $runKey -Name 'ReplayKeeper'
}

# Also clean up the exact installed target if its binary has already been removed.
if (Test-Path -LiteralPath $startupShortcut -PathType Leaf) {
    $startupShell = New-Object -ComObject WScript.Shell
    $link = $null
    try {
        $link = $startupShell.CreateShortcut($startupShortcut)
        if ((Test-ReplayKeeperSameFile $link.TargetPath $installedExe) -and $link.Arguments -eq '') {
            Remove-Item -LiteralPath $startupShortcut -Force
        }
        else { Write-Warning '保留了指向其他程序的 ReplayKeeper.lnk。' }
    }
    finally {
        if ($null -ne $link) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) | Out-Null }
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($startupShell) | Out-Null
    }
}

if ($null -ne $currentRunValue -and
    [string]::Equals($currentRunValue, $expectedRunValue, [System.StringComparison]::OrdinalIgnoreCase) -and
    $null -ne (Get-StartupCommand)) {
    throw '自启动项未成功移除；未删除安装文件。'
}

if ($hasInstalledExe) {
    Assert-NoOtherSessions
    Remove-Item -LiteralPath $installedExe -Force
}
Write-Host "卸载完成：$installedExe"
Write-Host "日志和状态文件仍保存在：$installDirectory"
