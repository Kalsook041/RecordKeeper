# Convert the approved PNG artwork to a Windows ICO with native 32-bit frames.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$projectRoot = Split-Path $PSScriptRoot -Parent
$sourcePath = Join-Path $projectRoot 'assets\replaykeeper.png'
$outputPath = Join-Path $projectRoot 'assets\replaykeeper.ico'
$source = [System.Drawing.Bitmap]::new($sourcePath)
$frames = [System.Collections.Generic.List[byte[]]]::new()
$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
try {
    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.DrawImage($source, [System.Drawing.Rectangle]::new(0, 0, $size, $size))
            }
            finally { $graphics.Dispose() }
            $bitmap.Save((Join-Path $projectRoot ("assets\replaykeeper-{0}.png" -f $size)), [System.Drawing.Imaging.ImageFormat]::Png)
            $rectangle = [System.Drawing.Rectangle]::new(0, 0, $size, $size)
            $bits = $bitmap.LockBits($rectangle, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            try {
                if ($bits.Stride -ne $size * 4) { throw 'Unexpected bitmap stride' }
                $pixels = New-Object byte[] ($bits.Stride * $size)
                [System.Runtime.InteropServices.Marshal]::Copy($bits.Scan0, $pixels, 0, $pixels.Length)
            }
            finally { $bitmap.UnlockBits($bits) }
            $memory = [System.IO.MemoryStream]::new()
            $writer = [System.IO.BinaryWriter]::new($memory)
            try {
                $writer.Write([uint32]40)
                $writer.Write([int32]$size)
                $writer.Write([int32]($size * 2))
                $writer.Write([uint16]1)
                $writer.Write([uint16]32)
                $writer.Write([uint32]0)
                $writer.Write([uint32]$pixels.Length)
                for ($i = 0; $i -lt 4; $i++) { $writer.Write([uint32]0) }
                for ($y = $size - 1; $y -ge 0; $y--) {
                    $writer.Write($pixels, $y * $size * 4, $size * 4)
                }
                $maskStride = [int]([math]::Ceiling($size / 32.0) * 4)
                for ($y = $size - 1; $y -ge 0; $y--) {
                    $mask = New-Object byte[] $maskStride
                    for ($x = 0; $x -lt $size; $x++) {
                        if ($pixels[($y * $size + $x) * 4 + 3] -eq 0) {
                            $index = [int][math]::Floor($x / 8.0)
                            $mask[$index] = $mask[$index] -bor (128 -shr ($x % 8))
                        }
                    }
                    $writer.Write($mask)
                }
                $frames.Add($memory.ToArray())
            }
            finally { $writer.Dispose(); $memory.Dispose() }
        }
        finally { $bitmap.Dispose() }
    }
    $file = [System.IO.File]::Open($outputPath, [System.IO.FileMode]::Create)
    $writer = [System.IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length)
            $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write($frame) }
    }
    finally { $writer.Dispose(); $file.Dispose() }
}
finally { $source.Dispose() }
Write-Output ("ICO exported: {0}; sizes: {1}" -f $outputPath, ($sizes -join ', '))
