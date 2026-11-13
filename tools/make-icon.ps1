# Renders the CoreWatch app icon (PNG-in-ICO, 256px) with System.Drawing.
param([string]$Out = "$PSScriptRoot\..\Assets\app.ico")

Add-Type -AssemblyName System.Drawing
$size = 256
$bmp = New-Object System.Drawing.Bitmap $size, $size
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.Clear([System.Drawing.Color]::Transparent)

# Rounded graphite tile
$r = 56
$rect = New-Object System.Drawing.Rectangle 8, 8, 240, 240
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc($rect.X, $rect.Y, $r, $r, 180, 90)
$path.AddArc($rect.Right - $r, $rect.Y, $r, $r, 270, 90)
$path.AddArc($rect.Right - $r, $rect.Bottom - $r, $r, $r, 0, 90)
$path.AddArc($rect.X, $rect.Bottom - $r, $r, $r, 90, 90)
$path.CloseFigure()
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 28, 32, 40)), ([System.Drawing.Color]::FromArgb(255, 12, 14, 18)), 90
$g.FillPath($bg, $path)
$edge = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 52, 58, 70)), 3
$g.DrawPath($edge, $path)

# Gauge arc
$arcPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 44, 50, 62)), 20
$arcPen.StartCap = 'Round'; $arcPen.EndCap = 'Round'
$g.DrawArc($arcPen, 56, 56, 144, 144, 135, 270)
$valPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 79, 182, 255)), 20
$valPen.StartCap = 'Round'; $valPen.EndCap = 'Round'
$g.DrawArc($valPen, 56, 56, 144, 144, 135, 185)

# Pulse line through the centre
$pulse = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 123, 224, 138)), 11
$pulse.StartCap = 'Round'; $pulse.EndCap = 'Round'; $pulse.LineJoin = 'Round'
$pts = @(
  (New-Object System.Drawing.PointF 84, 134), (New-Object System.Drawing.PointF 108, 134),
  (New-Object System.Drawing.PointF 120, 104), (New-Object System.Drawing.PointF 138, 158),
  (New-Object System.Drawing.PointF 150, 134), (New-Object System.Drawing.PointF 172, 134))
$g.DrawLines($pulse, [System.Drawing.PointF[]]$pts)
$g.Dispose()

$ms = New-Object System.IO.MemoryStream
$bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$png = $ms.ToArray()

New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]1)          # ICONDIR
$bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([byte]0)  # 256x256, no palette
$bw.Write([uint16]1); $bw.Write([uint16]32)
$bw.Write([uint32]$png.Length); $bw.Write([uint32]22)
$bw.Write($png)
$bw.Close()
"Wrote $Out ($($png.Length) bytes)"
