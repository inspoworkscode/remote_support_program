# One card from the background on its own, for ads: twice the size, with its shadow, as a
# transparent PNG and a copy on white. Cards: hotkup, ait, eset, rsp.
#   powershell -ExecutionPolicy Bypass -File docs\backgrounds\source\Render-Card.ps1 -Card hotkup
param([ValidateSet('hotkup', 'ait', 'eset', 'rsp')][string]$Card = 'hotkup')
# Not 'Stop': Edge writes harmless noise to stderr, which PowerShell 5.1 would treat as a failure.
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
$names = @{ hotkup = 'HotKup'; ait = 'Anywhere IT'; eset = 'ESET'; rsp = 'The Remote Support Program' }
$scale = 2
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
$page = (Join-Path $PSScriptRoot 'backgrounds.html') -replace '\\', '/'
$out = Split-Path $PSScriptRoot -Parent
$full = Join-Path $env:TEMP "rsp-card-$Card.png"
if (Test-Path $full) { Remove-Item $full }
& $edge --headless=new --disable-gpu --hide-scrollbars --allow-file-access-from-files `
    --force-device-scale-factor=$scale --default-background-color=00000000 --virtual-time-budget=4000 `
    --window-size=1920,1080 --screenshot="$full" "file:///$page`?only=$Card" 2>$null | Out-Null
if (-not (Test-Path $full)) { throw "Edge made no picture" }

# Crop to whatever is not fully transparent: the card and its shadow.
$bmp = [System.Drawing.Bitmap]::FromFile($full)
$rect = New-Object System.Drawing.Rectangle 0, 0, $bmp.Width, $bmp.Height
$d = $bmp.LockBits($rect, 'ReadOnly', 'Format32bppArgb'); $st = $d.Stride
$px = New-Object byte[] ($st * $bmp.Height); [Runtime.InteropServices.Marshal]::Copy($d.Scan0, $px, 0, $px.Length)
$bmp.UnlockBits($d)
$x0 = $bmp.Width; $y0 = $bmp.Height; $x1 = 0; $y1 = 0
for ($y = 0; $y -lt $bmp.Height; $y += 1) { $row = $y * $st
    for ($x = 0; $x -lt $bmp.Width; $x += 1) { if ($px[$row + $x * 4 + 3] -gt 2) {
        if ($x -lt $x0) { $x0 = $x }; if ($x -gt $x1) { $x1 = $x }; if ($y -lt $y0) { $y0 = $y }; if ($y -gt $y1) { $y1 = $y } } } }
$crop = $bmp.Clone((New-Object System.Drawing.Rectangle $x0, $y0, ($x1 - $x0 + 1), ($y1 - $y0 + 1)), $bmp.PixelFormat)
$bmp.Dispose(); Remove-Item $full

$clear = Join-Path $out "$($names[$Card]) card.png"
$white = Join-Path $out "$($names[$Card]) card - on white.png"
$crop.Save($clear, [System.Drawing.Imaging.ImageFormat]::Png)
$onWhite = New-Object System.Drawing.Bitmap $crop.Width, $crop.Height
$g = [System.Drawing.Graphics]::FromImage($onWhite); $g.Clear([System.Drawing.Color]::White); $g.DrawImage($crop, 0, 0, $crop.Width, $crop.Height); $g.Dispose()
$onWhite.Save($white, [System.Drawing.Imaging.ImageFormat]::Png)
"Made $clear ($($crop.Width) x $($crop.Height))"; "Made $white"
$crop.Dispose(); $onWhite.Dispose()
