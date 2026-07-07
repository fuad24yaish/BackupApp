# Generates assets\app.ico — a "version history" mark (blue rounded square,
# white circular history arrow + clock hands). Run once; the .ico is committed.
Add-Type -AssemblyName System.Drawing

$sizes = 256, 128, 64, 48, 32, 16
$pngStreams = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [math]::Max(1, [int]($s * 0.06))
    $rect = New-Object System.Drawing.Rectangle $pad, $pad, ($s - 2*$pad), ($s - 2*$pad)
    $radius = [int]($s * 0.22)

    # Rounded-rect path
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    # Blue vertical gradient fill
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 59, 130, 246),
        [System.Drawing.Color]::FromArgb(255, 29, 64, 175),
        90.0)
    $g.FillPath($brush, $path)

    # White circular history arrow (open ring with an arrowhead), centered
    $cx = $s / 2.0
    $cy = $s / 2.0
    $ringR = $s * 0.26
    $penW = [math]::Max(1.5, $s * 0.07)
    $white = [System.Drawing.Color]::White
    $pen = New-Object System.Drawing.Pen $white, $penW
    $pen.StartCap = 'Round'
    $pen.EndCap = 'Round'

    # Arc open at the top (gap where the arrowhead sits)
    $arcRect = New-Object System.Drawing.RectangleF (
        [float]($cx - $ringR), [float]($cy - $ringR), [float]($ringR*2), [float]($ringR*2))
    $g.DrawArc($pen, $arcRect, 40, 280)

    # Arrowhead at the top-right end of the arc, pointing counter-clockwise (up)
    $ah = $s * 0.13
    $tipX = $cx + $ringR
    $tipY = $cy
    $pts = @(
        (New-Object System.Drawing.PointF([float]($tipX), [float]($tipY - $ah))),
        (New-Object System.Drawing.PointF([float]($tipX + $ah*0.85), [float]($tipY + $ah*0.15))),
        (New-Object System.Drawing.PointF([float]($tipX - $ah*0.85), [float]($tipY + $ah*0.15)))
    )
    $wbrush = New-Object System.Drawing.SolidBrush $white
    $g.FillPolygon($wbrush, $pts)

    # Clock hands from center (12 o'clock and 4 o'clock)
    $handPen = New-Object System.Drawing.Pen $white, ([math]::Max(1.4, $s*0.05))
    $handPen.StartCap = 'Round'; $handPen.EndCap = 'Round'
    $g.DrawLine($handPen, [float]$cx, [float]$cy, [float]$cx, [float]($cy - $ringR*0.55))
    $g.DrawLine($handPen, [float]$cx, [float]$cy, [float]($cx + $ringR*0.5), [float]($cy + $ringR*0.28))

    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pngStreams += ,($ms.ToArray())
}

# Assemble an ICO container with PNG-encoded frames (supported by modern Windows)
$outDir = Join-Path $PSScriptRoot 'assets'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$icoPath = Join-Path $outDir 'app.ico'
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter $fs

$count = $pngStreams.Count
$bw.Write([UInt16]0)      # reserved
$bw.Write([UInt16]1)      # type = icon
$bw.Write([UInt16]$count) # image count

$offset = 6 + 16 * $count
for ($i = 0; $i -lt $count; $i++) {
    $data = $pngStreams[$i]
    $dim = $sizes[$i]
    $bw.Write([Byte]$(if ($dim -ge 256) {0} else {$dim}))  # width  (0 = 256)
    $bw.Write([Byte]$(if ($dim -ge 256) {0} else {$dim}))  # height (0 = 256)
    $bw.Write([Byte]0)     # palette
    $bw.Write([Byte]0)     # reserved
    $bw.Write([UInt16]1)   # color planes
    $bw.Write([UInt16]32)  # bits per pixel
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $pngStreams) { $bw.Write($data) }
$bw.Flush(); $bw.Close(); $fs.Close()
Write-Output "Wrote $icoPath ($([math]::Round((Get-Item $icoPath).Length/1KB,1)) KB, $count frames)"
