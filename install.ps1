$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$projectDirectory = [System.IO.Path]::GetFullPath($PSScriptRoot)
$buildScript = Join-Path $projectDirectory 'build.ps1'
if (-not (Test-Path -LiteralPath $buildScript -PathType Leaf)) {
    throw "找不到构建脚本：$buildScript"
}

# Always compile the checked-out sources before changing the installed copy.
& $buildScript
$builtExe = Join-Path $projectDirectory 'dist\ReplayKeeper.exe'
if (-not (Test-Path -LiteralPath $builtExe -PathType Leaf)) {
    throw "找不到构建产物：$builtExe"
}
if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    throw 'LOCALAPPDATA 未设置，无法确定当前用户的安装目录。'
}

$installDirectory = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'ReplayKeeper'))
$installedExe = Join-Path $installDirectory 'ReplayKeeper.exe'
$runSubKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$statusFile = Join-Path $installDirectory 'status.json'
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
        Where-Object { [string]::Equals($_.ExecutablePath, $executable, [System.StringComparison]::OrdinalIgnoreCase) })
}

function Get-CurrentInstalledProcesses {
    @(Get-InstalledProcesses $installedExe | Where-Object { $_.SessionId -eq $currentSessionId })
}

function Assert-NoOtherSessions {
    $other = @(Get-InstalledProcesses $installedExe | Where-Object { $_.SessionId -ne $currentSessionId })
    if ($other.Count -gt 0) {
        $sessions = ($other | Select-Object -ExpandProperty SessionId -Unique) -join ', '
        throw "安装路径的 ReplayKeeper 正在其他登录会话运行（SessionId: $sessions）；为避免覆盖运行中的 exe，未修改本会话进程：$installedExe"
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

function Wait-ForCurrentExit {
    $deadline = (Get-Date).AddSeconds(15)
    while (@(Get-CurrentInstalledProcesses).Count -gt 0) {
        if ((Get-Date) -ge $deadline) {
            throw "本会话 ReplayKeeper 在 15 秒内未退出：$installedExe"
        }
        Start-Sleep -Milliseconds 200
    }
}

function Get-RunSnapshot {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runSubKey, $false)
    if ($null -eq $key) {
        return @{ Exists = $false; Value = $null; Kind = $null }
    }
    try {
        if ($key.GetValueNames() -notcontains 'ReplayKeeper') {
            return @{ Exists = $false; Value = $null; Kind = $null }
        }
        return @{
            Exists = $true
            Value = $key.GetValue('ReplayKeeper', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            Kind = $key.GetValueKind('ReplayKeeper')
        }
    }
    finally {
        $key.Dispose()
    }
}

function Restore-RunSnapshot([hashtable] $snapshot) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runSubKey, $true)
    if ($null -eq $key -and $snapshot.Exists) {
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($runSubKey)
    }
    if ($null -eq $key) {
        return
    }
    try {
        if ($snapshot.Exists) {
            $key.SetValue('ReplayKeeper', $snapshot.Value, $snapshot.Kind)
        }
        else {
            $key.DeleteValue('ReplayKeeper', $false)
        }
    }
    finally {
        $key.Dispose()
    }

    $restored = Get-RunSnapshot
    if ($restored.Exists -ne $snapshot.Exists -or
        ($snapshot.Exists -and ($restored.Kind -ne $snapshot.Kind -or
        -not [System.Collections.StructuralComparisons]::StructuralEqualityComparer.Equals($restored.Value, $snapshot.Value)))) {
        throw '无法读回并确认原有 ReplayKeeper 自启动项。'
    }
}

function Wait-ForNewStatus([datetime] $launchedAtUtc) {
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        $running = @(Get-CurrentInstalledProcesses).Count -gt 0
        $freshStatus = (Test-Path -LiteralPath $statusFile -PathType Leaf) -and
            ((Get-Item -LiteralPath $statusFile).LastWriteTimeUtc -ge $launchedAtUtc)
        if ($running -and $freshStatus -and @(Get-CurrentInstalledProcesses).Count -gt 0) {
            return
        }
        Start-Sleep -Milliseconds 250
    }
    throw "新实例未在 15 秒内持续运行并写出新的 status.json：$installedExe"
}

Assert-OrdinaryPath $installDirectory
Assert-OrdinaryPath $installedExe
Assert-OrdinaryPath $startupShortcut
if (Test-Path -LiteralPath $installDirectory) {
    if (-not (Test-Path -LiteralPath $installDirectory -PathType Container)) {
        throw "安装路径不是文件夹：$installDirectory"
    }
}
else {
    New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
}

Assert-NoOtherSessions
$hadOldExe = Test-Path -LiteralPath $installedExe -PathType Leaf
$wasRunning = @(Get-CurrentInstalledProcesses).Count -gt 0
$oldRun = Get-RunSnapshot
$oldShortcut = if (Test-Path -LiteralPath $startupShortcut -PathType Leaf) { [IO.File]::ReadAllBytes($startupShortcut) } else { $null }
$backupFile = $null
if ($hadOldExe) {
    $backupFile = Join-Path $installDirectory ("ReplayKeeper.rollback-{0}.exe" -f [guid]::NewGuid().ToString('N'))
    Copy-Item -LiteralPath $installedExe -Destination $backupFile
}

$fileChangeStarted = $false
try {
    if ($wasRunning) {
        Assert-NoOtherSessions
        Invoke-ControlCommand $installedExe '--exit'
        Wait-ForCurrentExit
    }

    Assert-NoOtherSessions
    $fileChangeStarted = $true
    Copy-Item -LiteralPath $builtExe -Destination $installedExe -Force
    Invoke-ControlCommand $installedExe '--enable-startup'

    $startupShell = New-Object -ComObject WScript.Shell
    $link = $null
    try {
        if (-not (Test-Path -LiteralPath $startupShortcut -PathType Leaf)) { throw '启动快捷方式未生成。' }
        $link = $startupShell.CreateShortcut($startupShortcut)
        if ($link.TargetPath -ne $installedExe -or $link.Arguments -ne '') { throw '启动快捷方式目标验证失败。' }
    }
    finally {
        if ($null -ne $link) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) | Out-Null }
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($startupShell) | Out-Null
    }

    $launchedAtUtc = [datetime]::UtcNow
    Start-Process -FilePath $installedExe -WindowStyle Hidden
    Wait-ForNewStatus $launchedAtUtc
}
catch {
    $originalFailure = $_.Exception.Message
    $rollbackErrors = New-Object 'System.Collections.Generic.List[string]'
    $canRestoreFile = $true

    if ($fileChangeStarted) {
        try {
            Assert-NoOtherSessions
            if (@(Get-CurrentInstalledProcesses).Count -gt 0) {
                Invoke-ControlCommand $installedExe '--exit'
                Wait-ForCurrentExit
            }
        }
        catch {
            $canRestoreFile = $false
            $rollbackErrors.Add("退出新实例失败：$($_.Exception.Message)")
        }
        if ($canRestoreFile) {
            try {
                Assert-NoOtherSessions
                Assert-OrdinaryPath $installedExe
                if ($hadOldExe) {
                    Copy-Item -LiteralPath $backupFile -Destination $installedExe -Force
                }
                elseif (Test-Path -LiteralPath $installedExe -PathType Leaf) {
                    Remove-Item -LiteralPath $installedExe -Force
                }
            }
            catch {
                $canRestoreFile = $false
                $rollbackErrors.Add("恢复原 exe 失败：$($_.Exception.Message)")
            }
        }
    }

    try {
        Restore-RunSnapshot $oldRun
    }
    catch {
        $rollbackErrors.Add("恢复原注册表自启动项失败：$($_.Exception.Message)")
    }
    try {
        Assert-OrdinaryPath $startupShortcut
        if ($null -ne $oldShortcut) { [IO.File]::WriteAllBytes($startupShortcut, [byte[]]$oldShortcut) }
        elseif (Test-Path -LiteralPath $startupShortcut -PathType Leaf) { Remove-Item -LiteralPath $startupShortcut -Force }
    }
    catch {
        $rollbackErrors.Add("恢复原启动快捷方式失败：$($_.Exception.Message)")
    }

    if ($wasRunning -and $canRestoreFile) {
        try {
            if (@(Get-CurrentInstalledProcesses).Count -eq 0) {
                Start-Process -FilePath $installedExe -WindowStyle Hidden
                $restartDeadline = (Get-Date).AddSeconds(15)
                while (@(Get-CurrentInstalledProcesses).Count -eq 0) {
                    if ((Get-Date) -ge $restartDeadline) {
                        throw "旧实例在 15 秒内未重新运行：$installedExe"
                    }
                    Start-Sleep -Milliseconds 250
                }
            }
        }
        catch {
            $rollbackErrors.Add("重启旧实例失败：$($_.Exception.Message)")
        }
    }

    $backupNote = if ($null -ne $backupFile) { "；旧 exe 备份保留于：$backupFile" } else { '' }
    if ($rollbackErrors.Count -gt 0) {
        throw "安装失败：$originalFailure；回滚亦失败：$($rollbackErrors -join '；')$backupNote"
    }
    throw "安装失败：$originalFailure；已恢复原 exe、原自启动项和原运行状态$backupNote"
}

if ($null -ne $backupFile) {
    try {
        Remove-Item -LiteralPath $backupFile -Force
    }
    catch {
        throw "安装已成功，但无法删除临时备份 $backupFile：$($_.Exception.Message)"
    }
}
Write-Host "安装完成：$installedExe"
Write-Host '已为当前用户启用登录后自启动。'
Write-Host "启动快捷方式：$startupShortcut"
