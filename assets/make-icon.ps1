<#
  Renders the project icon as a multi-size .ico (PNG-compressed images, 16-256 px) with System.Drawing.
  Usage: assets\make-icon.ps1 -Style onesetup|tcs -Output assets\onesetup.ico
  The design matches the site favicon: a rounded square with a diagonal gradient and a white glyph.
#>
param(
    [ValidateSet('onesetup', 'tcs')][string]$Style = 'onesetup',
    [Parameter(Mandatory = $true)][string]$Output
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$palette = @{
    onesetup = @([Drawing.Color]::FromArgb(10, 132, 255), [Drawing.Color]::FromArgb(94, 92, 230))    # blue -> indigo
    tcs      = @([Drawing.Color]::FromArgb(14, 165, 233), [Drawing.Color]::FromArgb(20, 184, 166))   # sky -> teal
}[$Style]

# glyph strokes in a 64x64 design space
$strokes = @{
    onesetup = @(@(32, 14, 32, 36), @(22, 28, 32, 38), @(32, 38, 42, 28), @(18, 48, 46, 48))
    tcs      = @(@(18, 22, 30, 32), @(30, 32, 18, 42), @(36, 44, 48, 44))
}[$Style]

function New-Frame([int]$size) {
    $bitmap = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([Drawing.Color]::Transparent)
    $inset = [Math]::Max(0.5, $size * 0.02); $edge = $size - 2 * $inset; $r = $edge * 0.22
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc($inset, $inset, 2 * $r, 2 * $r, 180, 90); $path.AddArc($inset + $edge - 2 * $r, $inset, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($inset + $edge - 2 * $r, $inset + $edge - 2 * $r, 2 * $r, 2 * $r, 0, 90); $path.AddArc($inset, $inset + $edge - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $brush = New-Object Drawing.Drawing2D.LinearGradientBrush ((New-Object Drawing.PointF 0, 0), (New-Object Drawing.PointF $size, $size), $palette[0], $palette[1])
    $g.FillPath($brush, $path)
    $pen = New-Object Drawing.Pen ([Drawing.Color]::White), ([Math]::Max(1.4, $size * 0.085))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $k = $size / 64.0
    foreach ($s in $strokes) { $g.DrawLine($pen, $s[0] * $k, $s[1] * $k, $s[2] * $k, $s[3] * $k) }
    $g.Dispose()
    $stream = New-Object IO.MemoryStream
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    , $stream.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$frames = foreach ($size in $sizes) { , (New-Frame $size) }
$null = New-Item -ItemType Directory -Force (Split-Path -Parent ([IO.Path]::GetFullPath($Output)))
$file = [IO.File]::Create([IO.Path]::GetFullPath($Output))
$w = New-Object IO.BinaryWriter $file
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dimension); $w.Write([byte]$dimension); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $w.Write($frame) }
$w.Dispose(); $file.Dispose()
Write-Output "$Output ($((Get-Item $Output).Length) bytes, sizes: $($sizes -join ', '))"
