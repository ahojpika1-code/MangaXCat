$ErrorActionPreference = 'Stop'

$projectPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$iconPath = Join-Path $projectPath 'MangaXcat.ico'
Add-Type -AssemblyName System.Drawing

$bitmap = [System.Drawing.Bitmap]::new(64, 64, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)

$background = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(21, 22, 32))
$lime = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(197, 243, 106))
$paper = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(245, 242, 226))
$ink = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(30, 31, 40))
$coral = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(241, 143, 118))
$outline = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(197, 243, 106), 3)
$bookOutline = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(30, 31, 40), 2)

$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc(4, 4, 18, 18, 180, 90)
$path.AddArc(42, 4, 18, 18, 270, 90)
$path.AddArc(42, 42, 18, 18, 0, 90)
$path.AddArc(4, 42, 18, 18, 90, 90)
$path.CloseFigure()
$graphics.FillPath($background, $path)
$graphics.DrawPath($outline, $path)

$leftPage = [System.Drawing.Point[]]@(
    [System.Drawing.Point]::new(13, 16),
    [System.Drawing.Point]::new(30, 19),
    [System.Drawing.Point]::new(30, 49),
    [System.Drawing.Point]::new(13, 45)
)
$rightPage = [System.Drawing.Point[]]@(
    [System.Drawing.Point]::new(34, 19),
    [System.Drawing.Point]::new(51, 16),
    [System.Drawing.Point]::new(51, 45),
    [System.Drawing.Point]::new(34, 49)
)
$graphics.FillPolygon($paper, $leftPage)
$graphics.FillPolygon($paper, $rightPage)
$graphics.DrawPolygon($bookOutline, $leftPage)
$graphics.DrawPolygon($bookOutline, $rightPage)

$graphics.FillRectangle($ink, 16, 22, 10, 10)
$graphics.FillRectangle($coral, 37, 22, 11, 12)
$graphics.FillEllipse($lime, 40, 24, 5, 5)
$graphics.FillRectangle($ink, 40, 29, 6, 4)
$graphics.DrawLine($bookOutline, 16, 36, 27, 38)
$graphics.DrawLine($bookOutline, 37, 38, 48, 36)
$graphics.DrawLine($bookOutline, 31, 20, 32, 48)
$graphics.DrawLine($outline, 49, 12, 53, 12)
$graphics.DrawLine($outline, 51, 10, 51, 14)

$png = New-Object System.IO.MemoryStream
$bitmap.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
$file = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]1)
    $writer.Write([Byte]64)
    $writer.Write([Byte]64)
    $writer.Write([Byte]0)
    $writer.Write([Byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$png.Length)
    $writer.Write([UInt32]22)
    $writer.Write($png.ToArray())
} finally {
    $writer.Dispose()
    $file.Dispose()
    $png.Dispose()
}
$bitmap.Dispose()
$graphics.Dispose()
$path.Dispose()
$background.Dispose()
$lime.Dispose()
$paper.Dispose()
$ink.Dispose()
$coral.Dispose()
$outline.Dispose()
$bookOutline.Dispose()

Write-Output "Created $iconPath"
