$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root "src\Program.cs"
$assets = Join-Path $root "Assets"
$dist = Join-Path $root "dist"
$appName = [string]([char]0x526A) + [string]([char]0x85CF)
$iconPath = Join-Path $assets ($appName + ".ico")
$exePath = Join-Path $dist ($appName + ".exe")

New-Item -ItemType Directory -Force -Path $assets | Out-Null
New-Item -ItemType Directory -Force -Path $dist | Out-Null

Add-Type -AssemblyName System.Drawing

function New-RoundedRectanglePath {
    param(
        [float] $X,
        [float] $Y,
        [float] $Width,
        [float] $Height,
        [float] $Radius
    )

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $diameter = $Radius * 2
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconDibBytes {
    param([int] $Size)

    $bitmap = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    $rect = New-Object System.Drawing.RectangleF 0, 0, ($Size - 1), ($Size - 1)
    $backgroundPath = New-RoundedRectanglePath 1 1 ($Size - 2) ($Size - 2) ([Math]::Max(4, $Size * 0.22))
    $background = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(22, 164, 150)), ([System.Drawing.Color]::FromArgb(70, 93, 205)), 315
    $graphics.FillPath($background, $backgroundPath)

    $shadowPath = New-RoundedRectanglePath ($Size * 0.29) ($Size * 0.21) ($Size * 0.48) ($Size * 0.60) ($Size * 0.06)
    $shadow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(60, 0, 0, 0))
    $graphics.FillPath($shadow, $shadowPath)

    $backPath = New-RoundedRectanglePath ($Size * 0.24) ($Size * 0.18) ($Size * 0.48) ($Size * 0.60) ($Size * 0.06)
    $backBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(95, 255, 255, 255))
    $graphics.FillPath($backBrush, $backPath)

    $paperPath = New-RoundedRectanglePath ($Size * 0.30) ($Size * 0.24) ($Size * 0.46) ($Size * 0.58) ($Size * 0.06)
    $paperBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(246, 250, 255))
    $graphics.FillPath($paperBrush, $paperPath)

    $clipPath = New-RoundedRectanglePath ($Size * 0.42) ($Size * 0.16) ($Size * 0.22) ($Size * 0.15) ($Size * 0.04)
    $clipBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 227, 118))
    $graphics.FillPath($clipBrush, $clipPath)

    $linePen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(108, 122, 146)), ([Math]::Max(1.0, $Size * 0.035))
    $linePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $linePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawLine($linePen, ($Size * 0.39), ($Size * 0.42), ($Size * 0.67), ($Size * 0.42))
    $graphics.DrawLine($linePen, ($Size * 0.39), ($Size * 0.53), ($Size * 0.65), ($Size * 0.53))
    $graphics.DrawLine($linePen, ($Size * 0.39), ($Size * 0.64), ($Size * 0.57), ($Size * 0.64))

    $dotBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 104, 84))
    $graphics.FillEllipse($dotBrush, ($Size * 0.21), ($Size * 0.66), ($Size * 0.18), ($Size * 0.18))

    $graphics.Dispose()
    $background.Dispose()
    $backgroundPath.Dispose()
    $shadowPath.Dispose()
    $shadow.Dispose()
    $backPath.Dispose()
    $backBrush.Dispose()
    $paperPath.Dispose()
    $paperBrush.Dispose()
    $clipPath.Dispose()
    $clipBrush.Dispose()
    $linePen.Dispose()
    $dotBrush.Dispose()

    $maskStride = [int]([Math]::Floor(($Size + 31) / 32)) * 4
    $imageBytes = ($Size * $Size * 4) + ($maskStride * $Size)
    $memory = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $memory

    $writer.Write([UInt32]40)
    $writer.Write([Int32]$Size)
    $writer.Write([Int32]($Size * 2))
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]0)
    $writer.Write([UInt32]$imageBytes)
    $writer.Write([Int32]0)
    $writer.Write([Int32]0)
    $writer.Write([UInt32]0)
    $writer.Write([UInt32]0)

    for ($y = $Size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $Size; $x++) {
            $pixel = $bitmap.GetPixel($x, $y)
            $writer.Write([byte]$pixel.B)
            $writer.Write([byte]$pixel.G)
            $writer.Write([byte]$pixel.R)
            $writer.Write([byte]$pixel.A)
        }
    }

    $mask = New-Object byte[] ($maskStride * $Size)
    $writer.Write($mask)
    $writer.Flush()

    $bitmap.Dispose()
    $bytes = $memory.ToArray()
    $writer.Dispose()
    $memory.Dispose()
    return ,$bytes
}

function Write-IcoFile {
    param(
        [string] $Path,
        [int[]] $Sizes
    )

    $images = @()
    foreach ($size in $Sizes) {
        $bytes = [byte[]](New-IconDibBytes -Size $size)
        $images += [PSCustomObject]@{
            Size = $size
            Bytes = $bytes
        }
    }

    $stream = [System.IO.File]::Create($Path)
    $writer = New-Object System.IO.BinaryWriter $stream

    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]$images.Count)

    $offset = 6 + (16 * $images.Count)
    foreach ($image in $images) {
        $sizeByte = if ($image.Size -ge 256) { 0 } else { [byte]$image.Size }
        $writer.Write([byte]$sizeByte)
        $writer.Write([byte]$sizeByte)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]32)
        $writer.Write([UInt32]([byte[]]$image.Bytes).Length)
        $writer.Write([UInt32]$offset)
        $offset += ([byte[]]$image.Bytes).Length
    }

    foreach ($image in $images) {
        $writer.Write([byte[]]$image.Bytes)
    }

    $writer.Dispose()
    $stream.Dispose()
}

Write-IcoFile -Path $iconPath -Sizes @(16, 32, 48)

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (!(Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}

if (!(Test-Path $csc)) {
    throw "csc.exe not found"
}

& $csc /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /utf8output /win32icon:$iconPath /out:$exePath /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Runtime.Serialization.dll $src
if ($LASTEXITCODE -ne 0) {
    throw "compile failed"
}

Write-Host "Built: $exePath"
Write-Host "Icon: $iconPath"
