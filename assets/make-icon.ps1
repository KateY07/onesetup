<#
  Renders the project icon as a multi-size .ico with System.Drawing: a full-bleed, fully opaque square (no transparent
  corners, no dark fringe) with a diagonal gradient and a white glyph.
  Sizes up to 48 px are stored as classic 32-bit DIB images (what every Windows shell draws without surprises); 64 px and
  larger as PNG. Every pixel is checked to be opaque.
  Usage: assets\make-icon.ps1 -Style onesetup|tcs -Output assets\onesetup.ico
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

function New-Bitmap([int]$size) {
    $bitmap = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear($palette[0])
    # the fill overshoots the canvas so no edge pixel is ever blended with transparency
    $brush = New-Object Drawing.Drawing2D.LinearGradientBrush ((New-Object Drawing.PointF 0, 0), (New-Object Drawing.PointF $size, $size), $palette[0], $palette[1])
    $g.FillRectangle($brush, -2, -2, $size + 4, $size + 4)
    $pen = New-Object Drawing.Pen ([Drawing.Color]::White), ([Math]::Max(1.4, $size * 0.085))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $k = $size / 64.0
    foreach ($s in $strokes) { $g.DrawLine($pen, $s[0] * $k, $s[1] * $k, $s[2] * $k, $s[3] * $k) }
    $g.Dispose()
    $bitmap
}

function Get-Pixels($bitmap) {   # BGRA, top-down
    $rect = New-Object Drawing.Rectangle 0, 0, $bitmap.Width, $bitmap.Height
    $data = $bitmap.LockBits($rect, 'ReadOnly', [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes = New-Object byte[] ($data.Stride * $bitmap.Height)
    [Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $bitmap.UnlockBits($data)
    , $bytes
}

function New-Frame([int]$size) {
    $bitmap = New-Bitmap $size
    $pixels = Get-Pixels $bitmap
    for ($i = 3; $i -lt $pixels.Length; $i += 4) { if ($pixels[$i] -ne 255) { throw "Icon pixel with alpha $($pixels[$i]) at size ${size}: the icon must be fully opaque." } }
    if ($size -ge 64) {
        $stream = New-Object IO.MemoryStream
        $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        return , $stream.ToArray()
    }
    # classic DIB: BITMAPINFOHEADER (height doubled), bottom-up BGRA rows, then a 1-bpp AND mask (all zero = opaque)
    $ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ms
    $w.Write([uint32]40); $w.Write([int32]$size); $w.Write([int32]($size * 2)); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]0); $w.Write([uint32]0); $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    $stride = $size * 4
    for ($row = $size - 1; $row -ge 0; $row--) { $w.Write($pixels, $row * $stride, $stride) }
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $w.Write((New-Object byte[] ($maskStride * $size)))
    $bitmap.Dispose()
    , $ms.ToArray()
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
Write-Output "$Output ($((Get-Item $Output).Length) bytes, sizes: $($sizes -join ', '), all pixels opaque)"
