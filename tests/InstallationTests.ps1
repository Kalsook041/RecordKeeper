# 显式安装集成测试：会短暂停止本项目守护并重新安装，不操作 NVIDIA 回放开关。
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$projectRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $projectRoot 'tools\installation-paths.ps1')
$appDirectory = Join-Path $env:LOCALAPPDATA 'ReplayKeeper'
$exe = Join-Path $appDirectory 'ReplayKeeper.exe'
if (Test-Path -LiteralPath $exe -PathType Leaf) {
    $exe = [ReplayKeeper.InstallationPaths]::Resolve($exe)
    $appDirectory = Split-Path $exe -Parent
}
$status = Join-Path $appDirectory 'status.json'
$sessionId = (Get-Process -Id $PID).SessionId
$backupStatus = Join-Path $appDirectory ('status.test-backup-' + [guid]::NewGuid().ToString('N') + '.json')
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startupShortcut = Join-Path ([Environment]::GetFolderPath('Startup')) 'ReplayKeeper.lnk'

function Running {
    @(Get-CimInstance Win32_Process -Filter "Name = 'ReplayKeeper.exe'" |
        Where-Object { (Test-ReplayKeeperSameFile $_.ExecutablePath $exe) -and $_.SessionId -eq $sessionId })
}
function Stop-Guard {
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { return }
    $control = Start-Process -FilePath $exe -ArgumentList '--exit' -WindowStyle Hidden -PassThru
    try {
        if (-not $control.WaitForExit(10000) -or $control.ExitCode -ne 0) { throw '无法通知守护退出' }
    } finally { $control.Dispose() }
    $deadline = [datetime]::UtcNow.AddSeconds(15)
    while (@(Running).Count -gt 0) {
        if ([datetime]::UtcNow -gt $deadline) { throw '守护没有退出' }
        Start-Sleep -Milliseconds 200
    }
}
function Assert-Test([bool] $condition, [string] $message) {
    if (-not $condition) { throw ('FAIL: ' + $message) }
    Write-Output ('PASS: ' + $message)
}

if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw '请先安装 ReplayKeeper' }
$originalHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
$originalRunProperties = Get-ItemProperty -LiteralPath $runKey -Name ReplayKeeper -ErrorAction SilentlyContinue
$originalRun = if ($null -ne $originalRunProperties) { $originalRunProperties.ReplayKeeper } else { $null }
$originalShortcutHash = (Get-FileHash -LiteralPath $startupShortcut -Algorithm SHA256).Hash
$previousBackups = @(Get-ChildItem -LiteralPath $appDirectory -Filter 'ReplayKeeper.rollback-*.exe' | Select-Object -ExpandProperty FullName)
Stop-Guard
if (-not (Test-Path -LiteralPath $status -PathType Leaf)) { throw '需要已有的 status.json 作为验证基线' }
Move-Item -LiteralPath $status -Destination $backupStatus
New-Item -ItemType Directory -Path $status | Out-Null
Start-Process -FilePath $exe -WindowStyle Hidden
$startDeadline = [datetime]::UtcNow.AddSeconds(10)
while (@(Running).Count -eq 0) {
    if ([datetime]::UtcNow -gt $startDeadline) { throw '无法启动安装回滚测试的原实例' }
    Start-Sleep -Milliseconds 100
}
$expectedFailure = $false
try {
    try { & (Join-Path $projectRoot 'install.ps1') }
    catch {
        if ($_.Exception.Message -notlike '*新实例未在 15 秒*已恢复原 exe*') { throw }
        $expectedFailure = $true
        Write-Output 'PASS: 状态文件不可写时安装明确失败并回滚'
    }
    Assert-Test $expectedFailure '检测到注入的安装故障'
    Assert-Test ((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -eq $originalHash) '旧 exe 内容已恢复'
    $restoredRunProperties = Get-ItemProperty -LiteralPath $runKey -Name ReplayKeeper -ErrorAction SilentlyContinue
    $restoredRun = if ($null -ne $restoredRunProperties) { $restoredRunProperties.ReplayKeeper } else { $null }
    Assert-Test ($restoredRun -eq $originalRun) '原注册表自启动状态已恢复'
    Assert-Test ((Get-FileHash -LiteralPath $startupShortcut -Algorithm SHA256).Hash -eq $originalShortcutHash) '原启动快捷方式已逐字节恢复'
    Assert-Test (@(Running).Count -eq 1) '原后台运行状态已恢复'
}
finally {
    Stop-Guard
    if (Test-Path -LiteralPath $status -PathType Container) { Remove-Item -LiteralPath $status }
    if (Test-Path -LiteralPath $backupStatus -PathType Leaf) {
        Move-Item -LiteralPath $backupStatus -Destination $status -Force
    }
    # Only remove newly-created rollback backups after their contents were checked.
    if ($expectedFailure) {
        foreach ($backup in @(Get-ChildItem -LiteralPath $appDirectory -Filter 'ReplayKeeper.rollback-*.exe')) {
            if ($previousBackups -notcontains $backup.FullName -and
                (Get-FileHash -LiteralPath $backup.FullName -Algorithm SHA256).Hash -eq $originalHash) {
                Remove-Item -LiteralPath $backup.FullName
            }
        }
    }
    Start-Process -FilePath $exe -WindowStyle Hidden
}

& (Join-Path $projectRoot 'uninstall.ps1')
Assert-Test (-not (Test-Path -LiteralPath $exe)) '卸载移除安装 exe'
Assert-Test (@(Running).Count -eq 0) '卸载退出后台进程'
$run = Get-ItemProperty -LiteralPath $runKey -Name ReplayKeeper -ErrorAction SilentlyContinue
Assert-Test ($null -eq $run) '卸载移除本项目自启动值'
Assert-Test (-not (Test-Path -LiteralPath $startupShortcut)) '卸载移除启动文件夹快捷方式'
Assert-Test (Test-Path -LiteralPath (Join-Path $appDirectory 'keeper.log') -PathType Leaf) '卸载保留日志'
& (Join-Path $projectRoot 'install.ps1')
Assert-Test (Test-Path -LiteralPath $startupShortcut -PathType Leaf) '重新安装创建启动文件夹快捷方式'
Assert-Test (@(Running).Count -eq 1) '重新安装后只有一个后台实例'
$report = Get-Content -LiteralPath $status -Raw -Encoding UTF8 | ConvertFrom-Json
Assert-Test (-not $report.Paused -and $report.LogError -eq '') '后台状态正常且日志可写'

# Exercise the same shortcut Windows will open at sign-in, rather than only launching the exe.
Stop-Guard
$launchedAt = [datetime]::UtcNow
Start-Process -FilePath $startupShortcut
$deadline = [datetime]::UtcNow.AddSeconds(15)
while (@(Running).Count -ne 1 -or (Get-Item -LiteralPath $status).LastWriteTimeUtc -lt $launchedAt) {
    if ([datetime]::UtcNow -gt $deadline) { throw '启动快捷方式未正常启动守护' }
    Start-Sleep -Milliseconds 200
}
Assert-Test (@(Running).Count -eq 1) '启动文件夹快捷方式可启动守护并写出新状态'
Start-Process -FilePath $startupShortcut
Start-Sleep -Seconds 1
Assert-Test (@(Running).Count -eq 1) '重复打开快捷方式仍保持单实例'
$disable = Start-Process -FilePath $exe -ArgumentList '--disable-startup' -WindowStyle Hidden -PassThru -Wait
try { Assert-Test ($disable.ExitCode -eq 0 -and -not (Test-Path -LiteralPath $startupShortcut)) '关闭自启动移除快捷方式' }
finally { $disable.Dispose() }
try {
    # Simulate an old installation's Run registration and check upgrade migration.
    New-ItemProperty -LiteralPath $runKey -Name ReplayKeeper -Value ('"' + $exe + '"') -PropertyType String -Force | Out-Null
    $enable = Start-Process -FilePath $exe -ArgumentList '--enable-startup' -WindowStyle Hidden -PassThru -Wait
    try { Assert-Test ($enable.ExitCode -eq 0 -and (Test-Path -LiteralPath $startupShortcut -PathType Leaf)) '启用自启动重新创建快捷方式' }
    finally { $enable.Dispose() }
    Assert-Test ($null -eq (Get-ItemProperty -LiteralPath $runKey -Name ReplayKeeper -ErrorAction SilentlyContinue)) '旧版 Run 自启动项已迁移移除'
}
finally {
    if (-not (Test-Path -LiteralPath $startupShortcut -PathType Leaf)) {
        Start-Process -FilePath $exe -ArgumentList '--enable-startup' -WindowStyle Hidden -Wait
    }
}
Write-Output 'PASS: 安装集成测试结束，保留守护运行和登录自启动'
