# Prints brochure.html to "The Remote Support Program - Guide.pdf" beside it, with Edge headless.
#   powershell -ExecutionPolicy Bypass -File "docs\brochure\Print-Guide.ps1"
# Its own Edge profile, so an Edge already open on this PC does not swallow the job.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$pdf = Join-Path $here 'The Remote Support Program - Guide.pdf'
$url = 'file:///' + ((Join-Path $here 'brochure.html') -replace '\\', '/' -replace ' ', '%20')
$edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
$profile = Join-Path $env:TEMP ('rsp-guide-print-' + [guid]::NewGuid())
if (Test-Path $pdf) { Remove-Item $pdf }
Start-Process -FilePath $edge -Wait -ArgumentList @('--headless', '--disable-gpu', '--no-pdf-header-footer', "--user-data-dir=$profile", "--print-to-pdf=`"$pdf`"", $url)
Remove-Item $profile -Recurse -Force -ErrorAction SilentlyContinue
if (Test-Path $pdf) { "Printed: $pdf ($([math]::Round((Get-Item $pdf).Length / 1MB, 1)) MB)" } else { 'Edge did not write the PDF.'; exit 1 }
