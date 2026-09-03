param(
    [string] $AssetDirectory = (Join-Path $PSScriptRoot '..\src\RouteFlow\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedRectangle([System.Drawing.RectangleF] $bounds, [float] $radius) {
    $diameter = $radius * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($bounds.Left, $bounds.Top, $diameter, $diameter, 180, 90)
    $path.AddArc($bounds.Right - $diameter, $bounds.Top, $diameter, $diameter, 270, 90)
    $path.AddArc($bounds.Right - $diameter, $bounds.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($bounds.Left, $bounds.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-AppIconPng([int] $size) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.ScaleTransform($size / 512.0, $size / 512.0)

        $background = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#17313A'))
        $white = [System.Drawing.ColorTranslator]::FromHtml('#F4F7F8')
        $green = [System.Drawing.ColorTranslator]::FromHtml('#4FD1A5')
        $orange = [System.Drawing.ColorTranslator]::FromHtml('#FFB45B')
        try {
            $rounded = New-RoundedRectangle ([System.Drawing.RectangleF]::new(16, 16, 480, 480)) 112
            try { $graphics.FillPath($background, $rounded) } finally { $rounded.Dispose() }

            $whitePen = [System.Drawing.Pen]::new($white, 42)
            $greenPen = [System.Drawing.Pen]::new($green, 42)
            try {
                $whitePen.StartCap = $whitePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
                $whitePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
                $greenPen.StartCap = $greenPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
                $greenPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

                $graphics.DrawLine($whitePen, 256, 404, 256, 280)
                $left = [System.Drawing.Drawing2D.GraphicsPath]::new()
                $right = [System.Drawing.Drawing2D.GraphicsPath]::new()
                try {
                    $left.AddBezier(256, 280, 216, 280, 176, 244, 176, 194)
                    $left.AddLine(176, 194, 176, 154)
                    $right.AddBezier(256, 280, 308, 280, 352, 234, 352, 180)
                    $right.AddLine(352, 180, 352, 144)
                    $graphics.DrawPath($whitePen, $left)
                    $graphics.DrawPath($greenPen, $right)
                }
                finally {
                    $left.Dispose()
                    $right.Dispose()
                }
            }
            finally {
                $whitePen.Dispose()
                $greenPen.Dispose()
            }

            foreach ($node in @(
                @{ X = 176; Y = 142; R = 31; Color = $white },
                @{ X = 352; Y = 132; R = 31; Color = $green },
                @{ X = 256; Y = 412; R = 31; Color = $orange },
                @{ X = 256; Y = 280; R = 25; Color = $orange }
            )) {
                $brush = [System.Drawing.SolidBrush]::new($node.Color)
                try { $graphics.FillEllipse($brush, $node.X - $node.R, $node.Y - $node.R, $node.R * 2, $node.R * 2) }
                finally { $brush.Dispose() }
            }
        }
        finally {
            $background.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
    }

    $memory = [System.IO.MemoryStream]::new()
    try {
        $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$memory.ToArray()
    }
    finally {
        $memory.Dispose()
        $bitmap.Dispose()
    }
}

$assetPath = [System.IO.Path]::GetFullPath($AssetDirectory)
New-Item -ItemType Directory -Force $assetPath | Out-Null
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @($sizes | ForEach-Object { New-AppIconPng $_ })
[System.IO.File]::WriteAllBytes((Join-Path $assetPath 'routeflow.png'), $images[-1])

$iconPath = Join-Path $assetPath 'routeflow.ico'
$stream = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16] 0)
    $writer.Write([uint16] 1)
    $writer.Write([uint16] $images.Count)
    $offset = 6 + (16 * $images.Count)
    for ($index = 0; $index -lt $images.Count; $index++) {
        $sizeByte = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte] $sizeByte)
        $writer.Write([byte] $sizeByte)
        $writer.Write([byte] 0)
        $writer.Write([byte] 0)
        $writer.Write([uint16] 1)
        $writer.Write([uint16] 32)
        $writer.Write([uint32] $images[$index].Length)
        $writer.Write([uint32] $offset)
        $offset += $images[$index].Length
    }
    foreach ($image in $images) { $writer.Write($image) }
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}
