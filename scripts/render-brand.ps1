# Renderiza a marca vetorial compartilhada em PNG e ícone Windows de vários tamanhos.
# Execute com Windows PowerShell em STA.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$assets = Join-Path $root 'apps\desktop\Assets'
$reader = [System.Xml.XmlReader]::Create((Join-Path $assets 'Brand.xaml'))
try { $resources = [System.Windows.Markup.XamlReader]::Load($reader) } finally { $reader.Dispose() }
$logo = $resources['BrandLogo']
$frames = @()
foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    $context.DrawRoundedRectangle([System.Windows.Media.Brushes]::Black, $null, [System.Windows.Rect]::new(0, 0, $size, $size), $size * 0.16, $size * 0.16)
    $padding = $size * 0.12
    $context.DrawImage($logo, [System.Windows.Rect]::new($padding, $padding, $size - 2 * $padding, $size - 2 * $padding))
    $context.Close()
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $buffer = New-Object System.IO.MemoryStream
    try { $encoder.Save($buffer); $frames += ,$buffer.ToArray() } finally { $buffer.Dispose() }
}
$stream = [System.IO.File]::Create((Join-Path $assets 'Ximbuismo.ico'))
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    for ($i = 0; $i -lt $frames.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose(); $stream.Dispose() }
[System.IO.File]::WriteAllBytes((Join-Path $assets 'Ximbuismo.png'), $frames[-1])
Write-Output 'Marca renderizada: Ximbuismo.ico e Ximbuismo.png'
