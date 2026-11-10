$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sizes = @(16,24,32,48,64,128,256)
$images = @()
foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap(256,256)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(4,4,104,104,180,90)
    $path.AddArc(148,4,104,104,270,90)
    $path.AddArc(148,148,104,104,0,90)
    $path.AddArc(4,148,104,104,90,90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(207,82,62))
    $graphics.FillPath($brush,$path)
    $font = New-Object System.Drawing.Font('Segoe UI',139,[System.Drawing.FontStyle]::Bold,[System.Drawing.GraphicsUnit]::Pixel)
    $graphics.DrawString('B',$font,[System.Drawing.Brushes]::White,31,27)
    $graphics.FillEllipse([System.Drawing.Brushes]::White,185,145,23,23)
    $scaled = New-Object System.Drawing.Bitmap($size,$size)
    $scaledGraphics = [System.Drawing.Graphics]::FromImage($scaled)
    $scaledGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $scaledGraphics.DrawImage($bitmap,0,0,$size,$size)
    $stream = New-Object System.IO.MemoryStream
    $scaled.Save($stream,[System.Drawing.Imaging.ImageFormat]::Png)
    $images += ,$stream.ToArray()
    if ($size -eq 256) { $scaled.Save((Join-Path $PSScriptRoot 'app-icon.png'),[System.Drawing.Imaging.ImageFormat]::Png) }
    $stream.Dispose()
    $scaledGraphics.Dispose()
    $scaled.Dispose()
    $font.Dispose()
    $brush.Dispose()
    $path.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}
$output = [System.IO.File]::Create((Join-Path $PSScriptRoot 'app.ico'))
$writer = New-Object System.IO.BinaryWriter($output)
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]$sizes.Length)
$offset = 6 + 16 * $sizes.Length
for ($index=0; $index -lt $sizes.Length; $index++) {
    $sizeByte = if ($sizes[$index] -eq 256) {0} else {$sizes[$index]}
    $writer.Write([Byte]$sizeByte)
    $writer.Write([Byte]$sizeByte)
    $writer.Write([Byte]0)
    $writer.Write([Byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$images[$index].Length)
    $writer.Write([UInt32]$offset)
    $offset += $images[$index].Length
}
foreach ($bytes in $images) { $writer.Write([Byte[]]$bytes) }
$writer.Dispose()
$output.Dispose()
