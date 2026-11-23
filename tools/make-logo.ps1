# Gera a logo do Unlock GPU (desenho vetorial em WPF) em PNG e ICO.
# Saida: assets\logo-256.png, assets\logo-512.png e assets\UnlockGPU.ico (16, 24, 32, 48, 64, 128, 256).
param([string]$OutDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'assets'))
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-Brush([string]$hex) { $b = New-Object Windows.Media.SolidColorBrush ([Windows.Media.ColorConverter]::ConvertFromString($hex)); $b.Freeze(); $b }
function New-Pen($brush, [double]$w) { $p = New-Object Windows.Media.Pen($brush, $w); $p.StartLineCap = 'Round'; $p.EndLineCap = 'Round'; $p.LineJoin = 'Round'; $p.Freeze(); $p }

function Draw-Logo([int]$size) {
    $s = $size / 256.0
    $dv = New-Object Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $dc.PushTransform((New-Object Windows.Media.ScaleTransform($s, $s)))

    # Fundo: quadrado arredondado escuro com leve degrade
    $bg = New-Object Windows.Media.LinearGradientBrush
    $bg.StartPoint = '0,0'; $bg.EndPoint = '1,1'
    $bg.GradientStops.Add((New-Object Windows.Media.GradientStop ([Windows.Media.ColorConverter]::ConvertFromString('#232B33')), 0))
    $bg.GradientStops.Add((New-Object Windows.Media.GradientStop ([Windows.Media.ColorConverter]::ConvertFromString('#11161B')), 1))
    $dc.DrawRoundedRectangle($bg, $null, (New-Object Windows.Rect(8, 8, 240, 240)), 52, 52)

    $green = New-Brush '#BAF568'
    $greenDim = New-Brush '#7FA84A'
    $dark = New-Brush '#11161B'

    # Chip: corpo com pinos nos quatro lados
    $pinPen = New-Pen $greenDim 7
    foreach ($i in 0..3) {
        $o = 92 + $i * 24
        $dc.DrawLine($pinPen, (New-Object Windows.Point($o, 62)), (New-Object Windows.Point($o, 78)))   # cima
        $dc.DrawLine($pinPen, (New-Object Windows.Point($o, 178)), (New-Object Windows.Point($o, 194))) # baixo
        $dc.DrawLine($pinPen, (New-Object Windows.Point(62, $o)), (New-Object Windows.Point(78, $o)))   # esquerda
        $dc.DrawLine($pinPen, (New-Object Windows.Point(178, $o)), (New-Object Windows.Point(194, $o))) # direita
    }
    $dc.DrawRoundedRectangle($dark, (New-Pen $green 8), (New-Object Windows.Rect(80, 80, 96, 96)), 16, 16)

    # Cadeado aberto dentro do chip: arco levantado para a direita
    $shackle = New-Object Windows.Media.StreamGeometry
    $ctx = $shackle.Open()
    $ctx.BeginFigure((New-Object Windows.Point(112, 128)), $false, $false)
    $ctx.LineTo((New-Object Windows.Point(112, 112)), $true, $true)
    $ctx.ArcTo((New-Object Windows.Point(144, 112)), (New-Object Windows.Size(16, 16)), 0, $false, 'Clockwise', $true, $true)
    $ctx.LineTo((New-Object Windows.Point(144, 104)), $true, $true)
    $ctx.Close(); $shackle.Freeze()
    $shacklePen = New-Pen $green 7
    $dc.PushTransform((New-Object Windows.Media.RotateTransform(-28, 112, 128)))
    $dc.DrawGeometry($null, $shacklePen, $shackle)
    $dc.Pop()
    # corpo do cadeado
    $dc.DrawRoundedRectangle($green, $null, (New-Object Windows.Rect(102, 126, 52, 38)), 7, 7)
    # fechadura
    $dc.DrawEllipse($dark, $null, (New-Object Windows.Point(128, 141)), 5.5, 5.5)
    $dc.DrawRectangle($dark, $null, (New-Object Windows.Rect(125.5, 143, 5, 11)))

    $dc.Pop()
    $dc.Close()
    $bmp = New-Object Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    return $bmp
}

function Get-PngBytes($bmp) {
    $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object IO.MemoryStream; $enc.Save($ms); return $ms.ToArray()
}

foreach ($sz in 256, 512) { [IO.File]::WriteAllBytes((Join-Path $OutDir "logo-$sz.png"), (Get-PngBytes (Draw-Logo $sz))) }

# ICO com entradas PNG (aceito pelo Windows Vista+)
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = New-Object System.Collections.Generic.List[byte[]]
foreach ($sz in $sizes) { $pngs.Add([byte[]](Get-PngBytes (Draw-Logo $sz))) }
$ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter($ms)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = $sizes[$i]; $len = $pngs[$i].Length
    $w.Write([byte]$(if ($d -ge 256) { 0 } else { $d })); $w.Write([byte]$(if ($d -ge 256) { 0 } else { $d }))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$len); $w.Write([uint32]$offset); $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush(); [IO.File]::WriteAllBytes((Join-Path $OutDir 'UnlockGPU.ico'), $ms.ToArray())
Get-ChildItem $OutDir | Select-Object Name, Length
