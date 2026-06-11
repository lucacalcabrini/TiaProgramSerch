<#
  Genera l'icona dell'app (lente d'ingrandimento) come .ico multi-risoluzione.
  Disegna con GDI+ a 256px e impacchetta PNG 256/64/48/32/16 in un unico .ico.
  Uso:  .\make-icon.ps1   ->  scrive ..\TiaVarAnalyzer\icon.ico
#>
[CmdletBinding()]
param(
    [string]$OutPath = (Join-Path $PSScriptRoot '..\TiaVarAnalyzer\icon.ico')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-LensBitmap([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap($S, $S)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($S / 256.0, $S / 256.0)   # disegno sempre in spazio 256

    # --- manico arancione (disegnato prima: la lente lo copre dove serve) ---
    $orange = [System.Drawing.Color]::FromArgb(245, 166, 35)
    $pen = New-Object System.Drawing.Pen($orange, 44)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($pen, 150, 150, 216, 216)
    # bordo inferiore piu' scuro per profondita'
    $penDark = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(224, 142, 26), 12)
    $penDark.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $penDark.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($penDark, 168, 178, 214, 224)

    # --- cerchione (rim) blu-grigio ---
    $rim = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(99, 128, 156))
    $g.FillEllipse($rim, 16, 16, 164, 164)        # centro (98,98) r=82

    # --- lente (gradiente azzurro chiaro) ---
    $rect = New-Object System.Drawing.Rectangle(34, 34, 128, 128)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(236, 247, 253),
        [System.Drawing.Color]::FromArgb(190, 222, 239),
        90.0)
    $g.FillEllipse($grad, 34, 34, 128, 128)       # centro (98,98) r=64

    # --- riflesso bianco in alto a sinistra ---
    $hl = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(200, 255, 255, 255))
    $g.FillEllipse($hl, 58, 54, 42, 26)

    $g.Dispose()
    return $bmp
}

$sizes = 256, 64, 48, 32, 16
$entries = @()
foreach ($s in $sizes) {
    $b = New-LensBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $entries += , @{ size = $s; bytes = $ms.ToArray() }
    $ms.Dispose(); $b.Dispose()
}

$out = New-Object System.IO.MemoryStream
$bw  = New-Object System.IO.BinaryWriter($out)
$bw.Write([uint16]0)                # reserved
$bw.Write([uint16]1)                # type = icon
$bw.Write([uint16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dim = if ($e.size -ge 256) { 0 } else { $e.size }
    $bw.Write([byte]$dim)           # width
    $bw.Write([byte]$dim)           # height
    $bw.Write([byte]0)              # palette
    $bw.Write([byte]0)              # reserved
    $bw.Write([uint16]1)            # planes
    $bw.Write([uint16]32)           # bpp
    $bw.Write([uint32]$e.bytes.Length)
    $bw.Write([uint32]$offset)
    $offset += $e.bytes.Length
}
foreach ($e in $entries) { $bw.Write($e.bytes) }
$bw.Flush()

$full = [System.IO.Path]::GetFullPath($OutPath)
[System.IO.File]::WriteAllBytes($full, $out.ToArray())
$bw.Dispose(); $out.Dispose()
Write-Host "Icona creata: $full ($((Get-Item $full).Length) byte, $($entries.Count) risoluzioni)" -ForegroundColor Green
