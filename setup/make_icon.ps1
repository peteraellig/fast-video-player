param(
    [string]$Src = "$PSScriptRoot\..\logo\fast_video_player.png",
    [string]$Ico = "$PSScriptRoot\..\logo\fast_video_player.ico",
    [string]$Png = "$PSScriptRoot\..\ui\logo.png",
    [int]$PngSize = 128
)
# Builds the program icon (multi-size .ico) and the small header logo of the page from Peter's logo PNG.
# .ico layout: 32-bit BMP entries with alpha up to 128 px, PNG only for 256 px - PNG entries below 256 px could not be read by
# .NET Framework System.Drawing (tested 2026-11-25), BMP entries work everywhere. High-quality downscaling.
# Run:  powershell -NoProfile -ExecutionPolicy Bypass -File setup\make_icon.ps1
Add-Type -AssemblyName System.Drawing
$img = [System.Drawing.Image]::FromFile((Resolve-Path $Src))

function New-Scaled([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($img, 0, 0, $size, $size)
    $g.Dispose()
    return $bmp
}

function Get-PngBytes([int]$size) {
    $bmp = New-Scaled $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

# ICO "DIB" entry: BITMAPINFOHEADER (height doubled), BGRA rows bottom-up, then a 1-bit AND mask (all zero: alpha decides)
function Get-DibBytes([int]$size) {
    $bmp = New-Scaled $size
    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $raw = New-Object byte[] ($stride * $size)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
    $bmp.UnlockBits($data); $bmp.Dispose()
    $maskRow = [int]([Math]::Ceiling($size / 32.0) * 4)
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([UInt32]40); $w.Write([Int32]$size); $w.Write([Int32]($size * 2))
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]0)
    $w.Write([UInt32]($size * $size * 4 + $maskRow * $size))
    $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)
    for ($y = $size - 1; $y -ge 0; $y--) { $w.Write($raw, $y * $stride, $size * 4) }
    $w.Write((New-Object byte[] ($maskRow * $size)))
    $w.Flush()
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$entries = @()
foreach ($s in $sizes) { if ($s -ge 256) { $entries += ,(Get-PngBytes $s) } else { $entries += ,(Get-DibBytes $s) } }

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $data = $entries[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$data.Length); $w.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $entries) { $w.Write($data) }
$w.Flush()
[System.IO.File]::WriteAllBytes($Ico, $out.ToArray())
[System.IO.File]::WriteAllBytes($Png, (Get-PngBytes $PngSize))
$img.Dispose()
"ico: $Ico ($((Get-Item $Ico).Length) bytes, sizes $($sizes -join ','))"
"png: $Png"
