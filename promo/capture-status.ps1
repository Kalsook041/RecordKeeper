$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
$exe = Join-Path $env:LOCALAPPDATA 'ReplayKeeper\ReplayKeeper.exe'
$assembly = [Reflection.Assembly]::LoadFile($exe)
$data = Get-Content -LiteralPath (Join-Path $env:LOCALAPPDATA 'ReplayKeeper\status.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$snapshot = $assembly.CreateInstance('ReplayKeeper.ReplaySnapshot')
$snapshot.State = [Enum]::ToObject($assembly.GetType('ReplayKeeper.ReplayState'), [int]$data.Snapshot.State)
$snapshot.OverlayReady = $data.Snapshot.OverlayReady
$snapshot.Detail = $data.Snapshot.Detail
$snapshot.HotkeyDisplay = $data.Snapshot.HotkeyDisplay
$report = $assembly.CreateInstance('ReplayKeeper.MonitorReport')
foreach ($name in @('Message', 'RecoveryCount', 'AttemptCount', 'Paused', 'Phase')) { $report.$name = $data.$name }
$report.Snapshot = $snapshot
$startup = '未启用'
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Startup')) 'ReplayKeeper.lnk'
if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $null
    try {
        $link = $shell.CreateShortcut($shortcutPath)
        if ($link.TargetPath -eq $exe -and $link.Arguments -eq '') { $startup = '已启用' }
    }
    finally {
        if ($null -ne $link) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) | Out-Null }
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
    }
}
$type = $assembly.GetType('ReplayKeeper.TrayApplication').GetNestedType('StatusWindow', [Reflection.BindingFlags]::NonPublic)
$window = [Activator]::CreateInstance($type, $true)
try {
    $type.GetMethod('UpdateStatus').Invoke($window, @($report, [datetime]$data.LastCheck, [bool]$data.Paused, $false, $false, $startup)) | Out-Null
    $window.ShowInTaskbar = $false
    $window.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $window.Location = [Drawing.Point]::new(-30000, -30000)
    $window.Show()
    [Windows.Forms.Application]::DoEvents()
    $window.Update()
    $bitmap = [Drawing.Bitmap]::new($window.Width, $window.Height)
    try {
        $window.DrawToBitmap($bitmap, [Drawing.Rectangle]::new(0, 0, $window.Width, $window.Height))
        $bitmap.Save((Join-Path $PSScriptRoot 'status-window.png'), [Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bitmap.Dispose() }
}
finally { $window.Close(); $window.Dispose() }
Write-Output ('Captured actual application status: ' + $data.Phase + ', verified recoveries: ' + $data.RecoveryCount)
