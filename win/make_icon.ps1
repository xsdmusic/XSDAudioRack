# Generates app.ico (256x256, PNG-compressed) from 02.png
# Apple-style rounded-square icon: cover-cropped, corner radius ~22.4%.
# ASCII only on purpose (PowerShell 5.1 encoding safety).
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$src = Join-Path $root '02.png'
if (-not (Test-Path $src)) { $src = 'D:\AI\02.png' }
$out = Join-Path $root 'app.ico'

if (-not (Test-Path $src)) { throw "source image not found: $src" }

Add-Type -AssemblyName System.Drawing
$img = [System.Drawing.Image]::FromFile($src)
try {
    $big = 1024      # render large, then downscale for smooth anti-aliased corners
    $size = 256
    $radius = [int][Math]::Round($big * 0.2237)

    $bmpBig = New-Object System.Drawing.Bitmap $big, $big
    $g = [System.Drawing.Graphics]::FromImage($bmpBig)
    try {
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)

        # rounded-square clip path
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = 2 * $radius
        $path.AddArc(0, 0, $d, $d, 180, 90)
        $path.AddArc($big - $d, 0, $d, $d, 270, 90)
        $path.AddArc($big - $d, $big - $d, $d, $d, 0, 90)
        $path.AddArc(0, $big - $d, $d, $d, 90, 90)
        $path.CloseFigure()
        $g.SetClip($path)

        # cover: fill the whole rounded square, crop the overflow
        $scale = [Math]::Max($big / $img.Width, $big / $img.Height)
        $w = [int][Math]::Round($img.Width * $scale)
        $h = [int][Math]::Round($img.Height * $scale)
        $g.DrawImage($img, (($big - $w) / 2), (($big - $h) / 2), $w, $h)
    } finally { $g.Dispose() }

    # downscale to final size
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g2 = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g2.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g2.DrawImage($bmpBig, 0, 0, $size, $size)
    } finally { $g2.Dispose() }
    $bmpBig.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $png = $ms.ToArray()
} finally { $img.Dispose() }

# ICO container: 6-byte header + 16-byte directory entry + raw PNG (Vista+ format)
$ms2 = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms2)
$bw.Write([UInt16]0)           # reserved
$bw.Write([UInt16]1)           # type: icon
$bw.Write([UInt16]1)           # image count
$bw.Write([byte]0)             # width  (0 = 256)
$bw.Write([byte]0)             # height (0 = 256)
$bw.Write([byte]0)             # color count
$bw.Write([byte]0)             # reserved
$bw.Write([UInt16]1)           # color planes
$bw.Write([UInt16]32)          # bits per pixel
$bw.Write([UInt32]$png.Length) # data size
$bw.Write([UInt32](6 + 16))    # data offset
$bw.Write($png)
$bw.Flush()
[System.IO.File]::WriteAllBytes($out, $ms2.ToArray())
Write-Host "rounded app.ico written to $out"
