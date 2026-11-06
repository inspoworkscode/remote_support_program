# Takes the 1920 x 1080 pictures of backgrounds.html with Edge, into docs\backgrounds: the
# background with its words, and the logos-only version.
#   powershell -ExecutionPolicy Bypass -File docs\backgrounds\source\Render-Backgrounds.ps1
# Not 'Stop': Edge writes harmless noise to stderr, which PowerShell 5.1 would treat as a failure.
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
$page = (Join-Path $PSScriptRoot 'backgrounds.html') -replace '\\', '/'
$out = Split-Path $PSScriptRoot -Parent
$versions = [ordered]@{ '' = 'Clint - Teams and Zoom background.png'
                        '?plain=1' = 'Clint - Teams and Zoom background - logos only.png' }
foreach ($query in $versions.Keys) {
    $file = Join-Path $out $versions[$query]
    $tall = Join-Path $env:TEMP 'rsp-background-tall.png'
    if (Test-Path $tall) { Remove-Item $tall }
    # Edge sometimes paints a viewport shorter than the window it is given, leaving a white strip at
    # the bottom. So the window is taller than the page, and the picture is trimmed to 1920 x 1080.
    & $edge --headless=new --disable-gpu --hide-scrollbars --allow-file-access-from-files `
        --force-device-scale-factor=1 --virtual-time-budget=4000 --window-size=1920,1300 `
        --screenshot="$tall" "file:///$page$query" 2>$null | Out-Null
    if (-not (Test-Path $tall)) { throw "Edge made no picture for $($versions[$query])" }
    $big = [System.Drawing.Bitmap]::FromFile($tall)
    $pic = $big.Clone((New-Object System.Drawing.Rectangle 0, 0, 1920, 1080), $big.PixelFormat)
    $big.Dispose(); Remove-Item $tall
    $bottom = $pic.GetPixel(960, 1079)
    if ($bottom.R -eq 255 -and $bottom.G -eq 255 -and $bottom.B -eq 255) { $pic.Dispose(); throw "The bottom row of $($versions[$query]) is blank white: the page did not fill the picture" }
    $pic.Save($file, [System.Drawing.Imaging.ImageFormat]::Png); $pic.Dispose()
    Write-Host "Made $file"
}
