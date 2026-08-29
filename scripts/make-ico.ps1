# Builds a multi-size .ico (PNG-compressed entries) from the app avatar PNG.
# No GUI involved: pure GDI+ resize + binary assembly.
param(
    [string]$PngPath = "$PSScriptRoot\..\apps\PopGlot.Windows\Assets\popglot-app-avatar-v1.png",
    [string]$IcoPath = "$PSScriptRoot\..\apps\PopGlot.Windows\Assets\PopGlot.ico"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 48, 64, 128, 256
$srcImage = [System.Drawing.Image]::FromFile($PngPath)
$pngs = @()

foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($srcImage, 0, 0, $size, $size)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pngs += ,@($size, $ms.ToArray())
    $ms.Dispose()
}
$srcImage.Dispose()

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)

# ICONDIR
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$pngs.Count)

$offset = 6 + 16 * $pngs.Count
foreach ($entry in $pngs) {
    $size = [int]$entry[0]
    $bytes = [byte[]]$entry[1]
    $dim = if ($size -ge 256) { 0 } else { $size }
    $bw.Write([byte]$dim)          # width
    $bw.Write([byte]$dim)          # height
    $bw.Write([byte]0)             # palette
    $bw.Write([byte]0)             # reserved
    $bw.Write([uint16]1)           # planes
    $bw.Write([uint16]32)          # bpp
    $bw.Write([uint32]$bytes.Length)
    $bw.Write([uint32]$offset)
    $offset += $bytes.Length
}
foreach ($entry in $pngs) {
    $bw.Write([byte[]]$entry[1])
}
$bw.Flush()

[System.IO.File]::WriteAllBytes($IcoPath, $out.ToArray())
$bw.Dispose(); $out.Dispose()
Write-Host "ICO written: $IcoPath ($((Get-Item $IcoPath).Length) bytes, $($pngs.Count) sizes)"
