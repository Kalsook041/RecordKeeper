$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$projectDirectory = [System.IO.Path]::GetFullPath($PSScriptRoot)
$sourceDirectory = Join-Path $projectDirectory 'src'
$outputDirectory = Join-Path $projectDirectory 'dist'
$outputFile = Join-Path $outputDirectory 'ReplayKeeper.exe'
$manifestFile = Join-Path $projectDirectory 'app.manifest'
$iconFile = Join-Path $projectDirectory 'assets\replaykeeper.ico'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "找不到 .NET Framework x64 C# 编译器：$compiler"
}
if (-not (Test-Path -LiteralPath $sourceDirectory -PathType Container)) {
    throw "找不到源代码目录：$sourceDirectory"
}
if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf)) {
    throw "找不到应用清单：$manifestFile"
}
if (-not (Test-Path -LiteralPath $iconFile -PathType Leaf)) {
    throw "找不到程序图标：$iconFile"
}

$sources = @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' -File | Sort-Object FullName)
if ($sources.Count -eq 0) {
    throw "源代码目录中没有 .cs 文件：$sourceDirectory"
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$stagedFile = Join-Path $outputDirectory ("ReplayKeeper.build-{0}.exe" -f $PID)

try {
    $compilerArguments = @(
        '/nologo',
        '/target:winexe',
        '/platform:x64',
        '/langversion:5',
        '/codepage:65001',
        '/utf8output',
        "/win32manifest:$manifestFile",
        "/win32icon:$iconFile",
        "/resource:$iconFile,ReplayKeeper.AppIcon",
        "/out:$stagedFile",
        '/reference:System.dll',
        '/reference:System.Core.dll',
        '/reference:System.Windows.Forms.dll',
        '/reference:System.Drawing.dll',
        '/reference:System.Web.Extensions.dll'
    ) + @($sources | ForEach-Object { $_.FullName })

    & $compiler @compilerArguments
    $compilerExitCode = $LASTEXITCODE
    if ($compilerExitCode -ne 0) {
        throw "C# 编译失败，退出码：$compilerExitCode"
    }
    if (-not (Test-Path -LiteralPath $stagedFile -PathType Leaf)) {
        throw "编译器没有生成输出文件：$stagedFile"
    }

    Copy-Item -LiteralPath $stagedFile -Destination $outputFile -Force
    Write-Host "构建完成：$outputFile"
}
finally {
    if (Test-Path -LiteralPath $stagedFile -PathType Leaf) {
        Remove-Item -LiteralPath $stagedFile -Force
    }
}
