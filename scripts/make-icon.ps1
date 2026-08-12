<#
.SYNOPSIS
    Gera o ícone do Tempus: ampulheta branca sobre azul, em todos os tamanhos.

.DESCRIPTION
    Desenha por código em vez de guardar um binário opaco no repositório — assim dá para ajustar
    cor ou proporção sem ferramenta gráfica, e o diff conta o que mudou.

    Cada tamanho é redesenhado do zero, não reamostrado: aos 16px um ícone reduzido vira borrão,
    enquanto formas grossas desenhadas naquele tamanho continuam legíveis.
#>
[CmdletBinding()]
param(
    [string]$Output = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Tempus\Assets\tempus.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Azul moderno, com gradiente sutil para não ficar chapado.
$blueTop = [System.Drawing.Color]::FromArgb(255, 59, 130, 246)   # #3B82F6
$blueBottom = [System.Drawing.Color]::FromArgb(255, 29, 78, 216) # #1D4ED8
$white = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
$sand = [System.Drawing.Color]::FromArgb(255, 191, 219, 254)     # #BFDBFE

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

# Coordenadas normalizadas (0..1) viram PointF. Cada argumento entra entre parênteses próprios:
# dentro de "New-Object PointF($a * $b, $c)" o PowerShell lê a vírgula com precedência maior que o
# operador, e a expressão vira "$a * ($b, $c)" — conta com array, erro incompreensível.
function New-Pt([single]$nx, [single]$ny, [single]$s) {
    return New-Object System.Drawing.PointF(([single]($nx * $s)), ([single]($ny * $s)))
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [single]$size

    # Fundo: quadrado arredondado com gradiente vertical.
    $radius = [Math]::Max(2.0, $s * 0.22)
    $bg = New-RoundedPath 0 0 $s $s $radius
    $rect = New-Object System.Drawing.RectangleF(0, 0, $s, $s)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $blueTop, $blueBottom, 90.0)
    $g.FillPath($brush, $bg)
    $brush.Dispose()
    $bg.Dispose()

    # A ampulheta É o T de Tempus: a barra de cima serve de traço horizontal da letra, o funil
    # desce até a cintura, e a cintura continua como haste vertical.
    #
    # Não existe barra inferior de propósito. Ela fecharia a ampulheta, mas transformaria a
    # silhueta num "I" e mataria a leitura da letra — o monte de areia faz o papel de pé sem
    # virar serifa.
    #
    # Pisos em pixels: abaixo de ~2px um traço some na rasterização, e aos 16px a haste é o
    # primeiro elemento a desaparecer.
    $whiteBrush = New-Object System.Drawing.SolidBrush($white)

    # Traço horizontal do T, largo.
    $barW = $s * 0.74
    $barH = [Math]::Max(2.0, $s * 0.100)
    $barX = ($s - $barW) / 2.0
    $barY = $s * 0.155

    $barRadius = [Math]::Max(0.5, $barH * 0.34)
    $topBar = New-RoundedPath $barX $barY $barW $barH $barRadius
    $g.FillPath($whiteBrush, $topBar); $topBar.Dispose()

    # A haste do T é a própria ampulheta: estreita, dois bulbos encontrando-se na cintura.
    # Uma primeira tentativa fez o bulbo superior largo, ocupando toda a barra — virou taça de
    # martini. Estreitar a ampulheta é o que devolve a leitura da letra.
    $glassLeft = 0.355
    $glassRight = 0.645
    $glassTop = ($barY + $barH) / $s
    $waistY = 0.545
    $glassBottom = 0.790

    $upper = New-Object System.Drawing.Drawing2D.GraphicsPath
    $upper.AddPolygon(@(
        (New-Pt $glassLeft $glassTop $s),
        (New-Pt $glassRight $glassTop $s),
        (New-Pt 0.500 $waistY $s)
    ))
    $g.FillPath($whiteBrush, $upper); $upper.Dispose()

    $lower = New-Object System.Drawing.Drawing2D.GraphicsPath
    $lower.AddPolygon(@(
        (New-Pt 0.500 $waistY $s),
        (New-Pt $glassRight $glassBottom $s),
        (New-Pt $glassLeft $glassBottom $s)
    ))
    $g.FillPath($whiteBrush, $lower); $lower.Dispose()

    # Base: mesma largura da ampulheta, bem menor que o traço do T — fecha o vidro sem virar a
    # serifa que faria a letra parecer um "I".
    $baseW = ($glassRight - $glassLeft) * $s
    $baseH = [Math]::Max(2.0, $s * 0.085)
    $baseX = $glassLeft * $s
    $baseY = $glassBottom * $s
    $baseBar = New-RoundedPath $baseX $baseY $baseW $baseH ([Math]::Max(0.5, $baseH * 0.34))
    $g.FillPath($whiteBrush, $baseBar); $baseBar.Dispose()

    # Areia já caída, no bulbo de baixo. Só onde cabe sem virar sujeira.
    if ($size -ge 32) {
        $sandBrush = New-Object System.Drawing.SolidBrush($sand)
        $remaining = New-Object System.Drawing.Drawing2D.GraphicsPath
        $remaining.AddPolygon(@(
            (New-Pt 0.412 0.712 $s),
            (New-Pt 0.588 0.712 $s),
            (New-Pt 0.628 0.782 $s),
            (New-Pt 0.372 0.782 $s)
        ))
        $g.FillPath($sandBrush, $remaining)
        $remaining.Dispose(); $sandBrush.Dispose()
    }

    $whiteBrush.Dispose()
    $g.Dispose()
    return $bmp
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$pngs = @()

foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,@{ Size = $size; Bytes = $ms.ToArray() }
    $ms.Dispose(); $bmp.Dispose()
}

$dir = Split-Path -Parent $Output
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

# Container ICO. Entradas PNG são aceitas pelo Windows desde o Vista e evitam a máscara AND
# do formato BMP antigo, que estragaria as bordas suavizadas do quadrado arredondado.
$fs = [System.IO.File]::Create($Output)
$bw = New-Object System.IO.BinaryWriter($fs)

$bw.Write([UInt16]0)                # reservado
$bw.Write([UInt16]1)                # 1 = ícone
$bw.Write([UInt16]$pngs.Count)

$offset = 6 + (16 * $pngs.Count)
foreach ($png in $pngs) {
    $dim = if ($png.Size -ge 256) { 0 } else { $png.Size }  # 0 significa 256
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]0)              # cores da paleta
    $bw.Write([Byte]0)              # reservado
    $bw.Write([UInt16]1)            # planos
    $bw.Write([UInt16]32)           # bits por pixel
    $bw.Write([UInt32]$png.Bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $png.Bytes.Length
}

foreach ($png in $pngs) { $bw.Write($png.Bytes) }

$bw.Flush(); $bw.Dispose(); $fs.Dispose()

$kb = [math]::Round((Get-Item $Output).Length / 1KB, 1)
Write-Host "Icone gerado: $Output ($kb KB, $($pngs.Count) tamanhos)" -ForegroundColor Green
