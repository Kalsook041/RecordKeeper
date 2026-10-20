param()
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path $root 'dist'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$testExe = Join-Path $output 'MonitorTests.exe'
$sources = @((Join-Path $root 'src\Core.cs'), (Join-Path $root 'src\NvidiaPlatform.cs'), (Join-Path $root 'src\GameActivity.cs'), (Join-Path $root 'tests\MonitorTests.cs'))
& $compiler /nologo /target:exe /platform:x64 /langversion:5 /codepage:65001 /utf8output /warn:4 /warnaserror+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll "/out:$testExe" $sources
if ($LASTEXITCODE -ne 0) { throw '测试编译失败' }
& $testExe
if ($LASTEXITCODE -ne 0) { throw '测试失败' }
