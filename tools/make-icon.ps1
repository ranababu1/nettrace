# Regenerates src/NetTrace.App/Assets/NetTrace.ico and NetTrace.png.
# Usage: pwsh tools/make-icon.ps1 -OutDir src/NetTrace.App/Assets
param([Parameter(Mandatory)][string]$OutDir)
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded square with diagonal gradient (deep blue -> teal).
    $inset = [Math]::Max(0.5, $size * 0.03)
    $r = $size * 0.24
    $rect = New-Object System.Drawing.RectangleF $inset, $inset, ($size - 2 * $inset), ($size - 2 * $inset)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 37, 99, 235)), ([System.Drawing.Color]::FromArgb(255, 20, 184, 166)), 45
    $g.FillPath($brush, $path)

    # Pulse trace: flat, spike up, deep dip, recovery, flat.
    $w = [float]$size
    [System.Drawing.PointF[]]$pts = @(
        (New-Object System.Drawing.PointF ($w * 0.16), ($w * 0.56)),
        (New-Object System.Drawing.PointF ($w * 0.36), ($w * 0.56)),
        (New-Object System.Drawing.PointF ($w * 0.45), ($w * 0.28)),
        (New-Object System.Drawing.PointF ($w * 0.57), ($w * 0.76)),
        (New-Object System.Drawing.PointF ($w * 0.66), ($w * 0.50)),
        (New-Object System.Drawing.PointF ($w * 0.84), ($w * 0.50))
    )
    $stroke = if ($size -le 16) { 2.0 } elseif ($size -le 32) { $size * 0.10 } else { $size * 0.075 }
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $stroke
    $pen.LineJoin = 'Round'; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawLines($pen, $pts)

    $g.Dispose(); $brush.Dispose(); $pen.Dispose(); $path.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256) { $bmp.Save((Join-Path $OutDir 'NetTrace.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    , @($s, $ms.ToArray())
}

# ICO container with PNG-compressed entries.
$fs = [System.IO.File]::Create((Join-Path $OutDir 'NetTrace.ico'))
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $s = $p[0]; $data = $p[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($p in $pngs) { $bw.Write([byte[]]$p[1]) }
$bw.Dispose()
Get-ChildItem $OutDir | Select-Object Name, Length
