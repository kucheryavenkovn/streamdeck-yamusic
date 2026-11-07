$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$folder = Join-Path $root 'dist\local.tux.yamusic.sdPlugin\images'
foreach ($size in @(144, 288)) {
    $bitmap = [Drawing.Bitmap]::new($size, $size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([Drawing.ColorTranslator]::FromHtml('#191b23'))
    $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#ffdd66'))
    try {
        $font = [Drawing.Font]::new('Segoe UI Symbol', [single]($size * 0.55), [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
        $layout = [Drawing.StringFormat]::new()
        $layout.Alignment = [Drawing.StringAlignment]::Center
        $layout.LineAlignment = [Drawing.StringAlignment]::Center
        $rect = [Drawing.RectangleF]::new(0, 0, [single]$size, [single]$size)
        $graphics.DrawString([char]0x266A, $font, $brush, $rect, $layout)
        $name = if ($size -eq 144) { 'music.png' } else { 'music@2x.png' }
        $bitmap.Save((Join-Path $folder $name), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $brush.Dispose(); $font.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
}
