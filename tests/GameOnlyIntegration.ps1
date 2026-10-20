# Explicit live test: a temporary sleeping fixture exercises game entry/exit without starting a game.
# It is stopped as soon as detection begins, within the monitor's 20-second input grace period.
$ErrorActionPreference = 'Stop'
$data = Join-Path $env:LOCALAPPDATA 'ReplayKeeper'
$statusPath = Join-Path $data 'status.json'
$configPath = Join-Path $data 'games.txt'
function Read-Report { Get-Content -LiteralPath $statusPath -Raw -Encoding UTF8 | ConvertFrom-Json }
$baseline = Read-Report
if ($baseline.Phase -ne 'waiting-game' -or $baseline.Paused) { throw '需要守护正在等待游戏，且没有手动暂停。' }
$previousConfig = if (Test-Path -LiteralPath $configPath) { [IO.File]::ReadAllBytes($configPath) } else { $null }
$fixtureDirectory = Join-Path ([IO.Path]::GetTempPath()) ('ReplayKeeper.GameTest.' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureDirectory | Out-Null
$sourcePath = Join-Path $fixtureDirectory 'Fixture.cs'
$fixturePath = Join-Path $fixtureDirectory 'ReplayKeeperGameFixture.exe'
$fixture = $null
try {
    [IO.File]::WriteAllText($sourcePath, 'class Fixture { static void Main() { System.Threading.Thread.Sleep(60000); } }')
    & (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') /nologo /target:winexe "/out:$fixturePath" $sourcePath
    if ($LASTEXITCODE -ne 0) { throw '测试进程编译失败' }
    [IO.File]::AppendAllText($configPath, [Environment]::NewLine + $fixturePath + [Environment]::NewLine)
    $fixture = Start-Process -FilePath $fixturePath -WindowStyle Hidden -PassThru
    $deadline = [datetime]::UtcNow.AddSeconds(22)
    do {
        Start-Sleep -Milliseconds 250
        $active = Read-Report
        if ([datetime]::UtcNow -gt $deadline) { throw '测试进程未被识别为配置中的游戏' }
    } while (-not $active.GameRunning -or $active.GameName -ne 'ReplayKeeperGameFixture')
    $fixture.Kill()
    $fixture.WaitForExit()
    Write-Output 'PASS: 配置中的进程启动后自动启用游戏守护'
    $deadline = [datetime]::UtcNow.AddSeconds(8)
    do {
        Start-Sleep -Milliseconds 250
        $idle = Read-Report
        if ([datetime]::UtcNow -gt $deadline) { throw '测试进程退出后未停止检测' }
    } while ($idle.GameRunning -or $idle.Phase -ne 'waiting-game')
    if ($idle.Snapshot.State -ne 0 -or $idle.AttemptCount -ne $baseline.AttemptCount) { throw '非游戏状态仍在检测或测试期间发送了恢复请求' }
    Write-Output 'PASS: 进程退出后停止检测，状态标记未读取，未发送恢复请求'
}
finally {
    if ($null -ne $fixture) {
        if (-not $fixture.HasExited) { $fixture.Kill(); $fixture.WaitForExit() }
        $fixture.Dispose()
    }
    if ($null -ne $previousConfig) { [IO.File]::WriteAllBytes($configPath, [byte[]]$previousConfig) }
    elseif (Test-Path -LiteralPath $configPath) { Remove-Item -LiteralPath $configPath }
    foreach ($file in @($fixturePath, $sourcePath)) { if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file } }
    Remove-Item -LiteralPath $fixtureDirectory
}
