<#
  Renders the project icon as a multi-size .ico with System.Drawing: a rounded square with a diagonal gradient and a white
  glyph, with clean edges on any background.
  The dark fringe some icons show on white backgrounds comes from transparent pixels whose colour is black: when Windows scales
  the icon it blends that black into the edge. Here the colour under the transparent corners and the antialiased edge is the
  gradient colour itself (only the alpha changes), and the script refuses to write an icon that has a black pixel under any
  non-opaque alpha.
  Sizes up to 48 px are stored as classic 32-bit DIB images, 64 px and larger as PNG (both straight alpha).
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

function New-ColorBitmap([int]$size) {   # opaque, full-bleed: gradient + glyph
    $bitmap = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $brush = New-Object Drawing.Drawing2D.LinearGradientBrush ((New-Object Drawing.PointF 0, 0), (New-Object Drawing.PointF $size, $size), $palette[0], $palette[1])
    $g.FillRectangle($brush, -2, -2, $size + 4, $size + 4)
    $pen = New-Object Drawing.Pen ([Drawing.Color]::White), ([Math]::Max(1.4, $size * 0.085))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $k = $size / 64.0
    foreach ($s in $strokes) { $g.DrawLine($pen, $s[0] * $k, $s[1] * $k, $s[2] * $k, $s[3] * $k) }
    $g.Dispose()
    $bitmap
}

function New-MaskBitmap([int]$size) {    # white rounded square on transparent: only its alpha is used
    $bitmap = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([Drawing.Color]::Transparent)
    $edge = $size - 1.0; $r = $size * 0.22
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0.0, 0.0, 2 * $r, 2 * $r, 180, 90); $path.AddArc($edge - 2 * $r + 1, 0.0, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($edge - 2 * $r + 1, $edge - 2 * $r + 1, 2 * $r, 2 * $r, 0, 90); $path.AddArc(0.0, $edge - 2 * $r + 1, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $g.FillPath([Drawing.Brushes]::White, $path)
    $g.Dispose()
    $bitmap
}

function Get-Pixels($bitmap) {   # straight BGRA, top-down
    $rect = New-Object Drawing.Rectangle 0, 0, $bitmap.Width, $bitmap.Height
    $data = $bitmap.LockBits($rect, 'ReadOnly', [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes = New-Object byte[] ($data.Stride * $bitmap.Height)
    [Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $bitmap.UnlockBits($data)
    , $bytes
}

function New-Frame([int]$size) {
    $colorBitmap = New-ColorBitmap $size; $maskBitmap = New-MaskBitmap $size
    $pixels = Get-Pixels $colorBitmap; $mask = Get-Pixels $maskBitmap
    $colorBitmap.Dispose(); $maskBitmap.Dispose()
    $transparent = 0
    for ($i = 0; $i -lt $pixels.Length; $i += 4) {
        $alpha = $mask[$i + 3]
        $pixels[$i + 3] = $alpha
        if ($alpha -lt 255) {
            $transparent++
            if ($pixels[$i] + $pixels[$i + 1] + $pixels[$i + 2] -lt 60) { throw "Size ${size}: a non-opaque pixel has a black colour; that is what makes a dark fringe." }
        }
    }
    if ($transparent -eq 0) { throw "Size ${size}: no rounded corner was produced." }
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    if ($size -ge 64) {
        $out = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $rect = New-Object Drawing.Rectangle 0, 0, $size, $size
        $data = $out.LockBits($rect, 'WriteOnly', [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        [Runtime.InteropServices.Marshal]::Copy($pixels, 0, $data.Scan0, $pixels.Length)
        $out.UnlockBits($data)
        $stream = New-Object IO.MemoryStream
        $out.Save($stream, [Drawing.Imaging.ImageFormat]::Png); $out.Dispose()
        return , $stream.ToArray()
    }
    # classic DIB: BITMAPINFOHEADER (height doubled), bottom-up straight-alpha BGRA rows, then a 1-bpp AND mask
    $ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ms
    $w.Write([uint32]40); $w.Write([int32]$size); $w.Write([int32]($size * 2)); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]0); $w.Write([uint32]0); $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    $stride = $size * 4
    for ($row = $size - 1; $row -ge 0; $row--) { $w.Write($pixels, $row * $stride, $stride) }
    for ($row = $size - 1; $row -ge 0; $row--) {   # AND mask: 1 where fully transparent
        $line = New-Object byte[] $maskStride
        for ($x = 0; $x -lt $size; $x++) { if ($pixels[$row * $stride + $x * 4 + 3] -eq 0) { $line[$x -shr 3] = $line[$x -shr 3] -bor (0x80 -shr ($x -band 7)) } }
        $w.Write($line)
    }
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
Write-Output "$Output ($((Get-Item $Output).Length) bytes, sizes: $($sizes -join ', '), rounded, no black under transparency)"
