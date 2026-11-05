# Erzeugt src/NxMix.App/Assets/app.ico: das NX-MIX-Logo (blaues abgerundetes Quadrat mit drei Fadern, wie in der Seitenleiste)
# in den Groessen 16-256. Aufruf: powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$out = Join-Path $root 'src\NxMix.App\Assets\app.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $s / 34.0                                   # Logo ist auf 34x34 entworfen
    $m = [Math]::Max(0.5, $s * 0.02)
    $bg = New-RoundRect $m $m ($s - 2 * $m) ($s - 2 * $m) (10 * $k)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0, 111, 255))), $bg)
    $lineW = [single][Math]::Max(1.0, 1.8 * $k)
    $line = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(115, 255, 255, 255)), $lineW
    $line.StartCap = 'Round'; $line.EndCap = 'Round'
    $knob = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $pos = 0.62, 0.32, 0.70
    for ($i = 0; $i -lt 3; $i++) {
        $x = (10.5 + $i * 6.5) * $k
        $g.DrawLine($line, [single]$x, [single](8 * $k), [single]$x, [single](26 * $k))
        $ky = (8 + 18 * $pos[$i]) * $k
        $kw = [Math]::Max(3.0, 6.0 * $k); $kh = [Math]::Max(2.0, 4.0 * $k)
        $kp = New-RoundRect ([single]($x - $kw / 2)) ([single]($ky - $kh / 2)) ([single]$kw) ([single]$kh) ([single]([Math]::Min($kh / 2, 1.6 * $k)))
        $g.FillPath($knob, $kp)
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pngs += , $ms.ToArray()
}

# ICO-Datei mit PNG-Eintraegen schreiben
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
Write-Host "Geschrieben: $out"
