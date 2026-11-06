<#
    Builds the handover pack: one zip holding the plain-English handover documents, the source
    code, and the source code WITH its full git history, ready to copy onto a USB stick for the
    safe.

        powershell -ExecutionPolicy Bypass -File .\tools\Make-HandoverPack.ps1

    Re-run it whenever the product has moved on, and replace the zip on the stick.

    What goes in, and why:
      00-START-HERE.md             the first thing anybody opening the zip should read
      The Remote Support Program - Guide.pdf
                                   the generic guide (docs\brochure), for readers who are not developers
      Handover documents\          docs\handover-pack\*.md, plus the same as one web page
                                   (Handover handbook.html) and, if Edge can print it, a PDF
      Source code\                 git archive of HEAD - every tracked file, nothing else
      Source code with history\    a git bundle of every branch, and how to restore it

    What is deliberately NOT in it: anything .gitignore keeps out of the repository - build
    output, brand.json (tenant key), the database, data\keys, tools\rs-credentials.json and the
    legal\ folder. docs\handover-pack\08-KEYS-AND-ACCOUNTS.md lists those and where they live.
    The script checks the finished zip for them and fails if any slipped in.

    The source copy is the last COMMIT, so commit first: uncommitted changes are left out, and the
    script says so.

    -OutDir   where the zip is written (default: your Desktop)
    -NoPdf    skip printing the handbook to PDF with Edge
    -Version  the pack's version, in the zip's name and the handbook (default V2.5)
#>
param(
    [string]$OutDir = [Environment]::GetFolderPath('Desktop'),
    # The pack's version: V1 was 2 October 2026, V2 6 October 2026, V2.1 7 October 2026, V2.2 15 October 2026, V2.3 16 October 2026, V2.4 27 October 2026, V2.5 28 October 2026 (the last before going live). Move it on when the pack is rewritten.
    [string]$Version = 'V2.5',
    [switch]$NoPdf
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$packDocs = Join-Path $root 'docs\handover-pack'
$date = Get-Date -Format 'yyyy-MM-dd'
$name = "TheRemoteSupportProgram-Handover-$Version-$date"
$stage = Join-Path $env:TEMP ("rsp-handover-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$top = Join-Path $stage $name
$zip = Join-Path $OutDir "$name.zip"
$utf8 = New-Object System.Text.UTF8Encoding($false)

# git writes progress to stderr, which Windows PowerShell 5.1 turns into a terminating error under
# 'Stop'. So git runs under 'Continue' and is judged by its exit code alone. It is called by its
# full path, and the helper is not named "Git": PowerShell names are case-blind, so a function
# called Git that runs "git" calls itself for ever (it did, the first time this was run).
$gitExe = (Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
if (-not $gitExe) { throw 'git is not on PATH.' }
function Invoke-Git {
    $ErrorActionPreference = 'Continue'
    & $gitExe -C $root @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed" }
}

Write-Host ''
Write-Host '== 1. Checking the repository ==' -ForegroundColor Cyan
if (-not (Test-Path (Join-Path $packDocs '00-START-HERE.md'))) { throw "No handover documents in $packDocs." }
$head = (Invoke-Git rev-parse --short HEAD) | Select-Object -First 1
$headDate = (Invoke-Git log -1 --format=%cd --date=short) | Select-Object -First 1
$dirty = @(Invoke-Git status --porcelain)
Write-Host "   Last commit: $head ($headDate)"
if ($dirty.Count -gt 0) {
    Write-Warning "$($dirty.Count) uncommitted change(s). The source copy is the last COMMIT, so these are NOT in the pack:"
    $dirty | Select-Object -First 10 | ForEach-Object { Write-Host "     $_" -ForegroundColor Yellow }
    if ($dirty.Count -gt 10) { Write-Host '     ...' -ForegroundColor Yellow }
}

New-Item -ItemType Directory -Force -Path $top | Out-Null
$docsOut = Join-Path $top 'Handover documents'
$srcOut = Join-Path $top 'Source code'
$histOut = Join-Path $top 'Source code with history'
New-Item -ItemType Directory -Force -Path $docsOut, $srcOut, $histOut | Out-Null

# ---------------------------------------------------------------------------------------------
Write-Host '== 2. The source code ==' -ForegroundColor Cyan
$srcZip = Join-Path $stage 'source.zip'
Invoke-Git archive --format=zip -o $srcZip HEAD
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory($srcZip, $srcOut)
Remove-Item $srcZip
$srcCount = @(Get-ChildItem $srcOut -Recurse -File).Count
Write-Host "   $srcCount files"

# ---------------------------------------------------------------------------------------------
Write-Host '== 3. The source code with its history ==' -ForegroundColor Cyan
$bundle = Join-Path $histOut 'TheRemoteSupportProgram.bundle'
Invoke-Git bundle create $bundle --all 2>$null
Invoke-Git bundle verify $bundle 2>$null | Out-Null
$commits = (Invoke-Git rev-list --all --count) | Select-Object -First 1
$branch = (Invoke-Git rev-parse --abbrev-ref HEAD) | Select-Object -First 1
Write-Host "   $commits commits, verified"
$restore = @"
THE REMOTE SUPPORT PROGRAM - SOURCE CODE WITH FULL HISTORY
==========================================================

TheRemoteSupportProgram.bundle is a complete git repository in one file: every version of every
file, and the reason for every change ($commits commits, the latest $head of $headDate).

To restore it on a computer with git installed (https://git-scm.com):

    git clone TheRemoteSupportProgram.bundle "Remote Support"
    cd "Remote Support"
    git checkout $branch

"$branch" is the branch the work was done on. "git log" shows the history, newest first.

The plain copy in the "Source code" folder is the same as the latest commit, for anybody who
just wants to read the files.

Next: put the project somewhere safe and private online (GitHub, Azure DevOps, GitLab), because
until now the only copies were the founder's laptop and this USB stick:

    git remote add origin <the new private repository's address>
    git push origin --all

Then read "Handover documents\05-BUILD-AND-RELEASE.md".
"@
[IO.File]::WriteAllText((Join-Path $histOut 'HOW-TO-RESTORE.txt'), ($restore -replace "`r?`n", "`r`n"), $utf8)

# ---------------------------------------------------------------------------------------------
Write-Host '== 4. The handover documents ==' -ForegroundColor Cyan
$mdFiles = @(Get-ChildItem $packDocs -Filter '*.md' | Sort-Object Name)
foreach ($f in $mdFiles) { Copy-Item $f.FullName $docsOut }
Copy-Item (Join-Path $packDocs '00-START-HERE.md') $top
# The generic guide (V2.5) at the top, beside the start page: the one thing in the zip a reader who is not a developer
# should open first. Taken from the working copy, so print it fresh (docs\brochure\Print-Guide.ps1) before running this.
$guide = Join-Path $root 'docs\brochure\The Remote Support Program - Guide.pdf'
if (-not (Test-Path $guide)) { throw "No guide at $guide. Print it with docs\brochure\Print-Guide.ps1 first." }
Copy-Item $guide $top

function Escape([string]$s) { $s.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;') }

# Bold, italics, links and `code` on one line of already-escaped text. Code spans are swapped for
# numbered placeholders first, so a * inside one is left alone and a **bold run** may still span
# one; they go back in last.
function Inline([string]$s) {
    $codes = New-Object System.Collections.Generic.List[string]
    $p = [regex]::Replace($s, '`([^`]+)`', { param($m) $codes.Add($m.Groups[1].Value); [string][char]1 + ($codes.Count - 1) + [char]2 })
    $p = [regex]::Replace($p, '\*\*(.+?)\*\*', '<strong>$1</strong>')
    $p = [regex]::Replace($p, '(?<![\*\w])\*(?![\s\*])(.+?)(?<![\s\*])\*(?![\*\w])', '<em>$1</em>')
    $p = [regex]::Replace($p, '\[([^\]]+)\]\(([^)\s]+)\)', '<a href="$2">$1</a>')
    $p = [regex]::Replace($p, '(?<!["=>])(https?://[^\s<|)\x01]+[^\s<|).,;:\x01])', '<a href="$1">$1</a>')
    [regex]::Replace($p, '\x01(\d+)\x02', { param($m) '<code>' + $codes[[int]$m.Groups[1].Value] + '</code>' })
}

# The cells of one table row. Returned as a plain array: a leading comma here would hand the
# pipeline ONE item holding every cell, which drew each row as a single cell the first time.
function Cells([string]$row) {
    $r = $row.Trim()
    if ($r.StartsWith('|')) { $r = $r.Substring(1) }
    if ($r.EndsWith('|')) { $r = $r.Substring(0, $r.Length - 1) }
    foreach ($c in ($r -split '\|')) { $c.Trim() }
}

# A small Markdown reader for the subset these documents use: headings, paragraphs, tables,
# bulleted and numbered lists (nested by indent, with indented continuation lines), block quotes,
# fenced code and rules. There is no Markdown library on a stock Windows PC, and the pack must be
# rebuildable on one.
function Convert-Markdown([string]$md, [string]$prefix) {
    $out = New-Object System.Text.StringBuilder
    $lines = ($md -replace "`r", '') -split "`n"
    $para = New-Object System.Collections.Generic.List[string]
    $quote = New-Object System.Collections.Generic.List[string]
    $lists = New-Object System.Collections.Generic.List[object]
    $i = 0

    $flushPara = {
        if ($para.Count -gt 0) { [void]$out.Append('<p>' + (Inline (Escape ($para -join ' '))) + "</p>`n"); $para.Clear() }
    }
    $flushQuote = {
        if ($quote.Count -gt 0) { [void]$out.Append('<blockquote><p>' + (Inline (Escape ($quote -join ' '))) + "</p></blockquote>`n"); $quote.Clear() }
    }
    $closeLists = {
        while ($lists.Count -gt 0) { $l = $lists[$lists.Count - 1]; [void]$out.Append("</li></$($l.Type)>`n"); $lists.RemoveAt($lists.Count - 1) }
    }

    while ($i -lt $lines.Count) {
        $line = $lines[$i]
        $listMatch = [regex]::Match($line, '^(\s*)([-*]|\d+\.)\s+(.*)$')

        if ($line -match '^```') {
            & $flushPara; & $flushQuote; & $closeLists
            $code = New-Object System.Collections.Generic.List[string]
            $i++
            while ($i -lt $lines.Count -and $lines[$i] -notmatch '^```') { $code.Add($lines[$i]); $i++ }
            [void]$out.Append('<pre><code>' + (Escape ($code -join "`n")) + "</code></pre>`n")
            $i++; continue
        }
        if ($line -match '^\s*$') { & $flushPara; & $flushQuote; & $closeLists; $i++; continue }
        if ($line -match '^(#{1,4})\s+(.*)$') {
            & $flushPara; & $flushQuote; & $closeLists
            $level = $Matches[1].Length + 1   # the document's own # becomes h2 under the page's h1
            $text = $Matches[2]
            $id = $prefix + '-' + (($text.ToLower() -replace '[^a-z0-9]+', '-').Trim('-'))
            [void]$out.Append("<h$level id=""$id"">" + (Inline (Escape $text)) + "</h$level>`n")
            $i++; continue
        }
        if ($line -match '^-{3,}\s*$') { & $flushPara; & $flushQuote; & $closeLists; [void]$out.Append("<hr>`n"); $i++; continue }
        if ($line -match '^>\s?(.*)$') { & $flushPara; & $closeLists; $quote.Add($Matches[1]); $i++; continue }
        if ($line -match '^\s*\|') {
            & $flushPara; & $flushQuote; & $closeLists
            $rows = New-Object System.Collections.Generic.List[string]
            while ($i -lt $lines.Count -and $lines[$i] -match '^\s*\|') { $rows.Add($lines[$i]); $i++ }
            [void]$out.Append("<div class=""tw""><table>`n")
            $hasHead = $rows.Count -gt 1 -and $rows[1] -match '^\s*\|[\s:\-|]+\|\s*$'
            $start = 0
            if ($hasHead) {
                [void]$out.Append('<thead><tr>' + ((Cells $rows[0] | ForEach-Object { '<th>' + (Inline (Escape $_)) + '</th>' }) -join '') + "</tr></thead>`n")
                $start = 2
            }
            [void]$out.Append('<tbody>')
            for ($r = $start; $r -lt $rows.Count; $r++) {
                [void]$out.Append('<tr>' + ((Cells $rows[$r] | ForEach-Object { '<td>' + (Inline (Escape $_)) + '</td>' }) -join '') + "</tr>`n")
            }
            [void]$out.Append("</tbody></table></div>`n")
            continue
        }
        if ($listMatch.Success) {
            & $flushPara; & $flushQuote
            $indent = $listMatch.Groups[1].Value.Length
            $marker = $listMatch.Groups[2].Value
            $type = 'ul'; if ($marker -match '\d') { $type = 'ol' }
            $text = Inline (Escape $listMatch.Groups[3].Value)
            $open = { param($t, $m)
                $attr = ''
                if ($t -eq 'ol' -and $m -ne '1.') { $attr = ' start="' + $m.TrimEnd('.') + '"' }
                [void]$out.Append("<$t$attr>")
                $lists.Add([pscustomobject]@{ Type = $t; Indent = $indent })
            }
            if ($lists.Count -eq 0 -or $indent -gt $lists[$lists.Count - 1].Indent) {
                & $open $type $marker
            } else {
                while ($lists.Count -gt 0 -and $lists[$lists.Count - 1].Indent -gt $indent) {
                    $l = $lists[$lists.Count - 1]; [void]$out.Append("</li></$($l.Type)>"); $lists.RemoveAt($lists.Count - 1)
                }
                if ($lists.Count -eq 0) { & $open $type $marker }
                elseif ($lists[$lists.Count - 1].Type -ne $type) {
                    $l = $lists[$lists.Count - 1]; [void]$out.Append("</li></$($l.Type)>"); $lists.RemoveAt($lists.Count - 1)
                    & $open $type $marker
                } else { [void]$out.Append('</li>') }
            }
            [void]$out.Append("<li>$text")
            $i++; continue
        }
        if ($lists.Count -gt 0 -and $line -match '^\s+\S') {
            [void]$out.Append(' ' + (Inline (Escape $line.Trim())))
            $i++; continue
        }
        & $flushQuote; & $closeLists
        $para.Add($line.Trim()); $i++
    }
    & $flushPara; & $flushQuote; & $closeLists
    $out.ToString()
}

$toc = New-Object System.Text.StringBuilder
$body = New-Object System.Text.StringBuilder
foreach ($f in $mdFiles) {
    $md = [IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8)
    $prefix = 'd' + $f.BaseName.Substring(0, 2)
    $title = ([regex]::Match($md, '(?m)^#\s+(.+)$')).Groups[1].Value
    [void]$toc.Append("<li><a href=""#$prefix"">" + (Escape $title) + "</a></li>`n")
    [void]$body.Append("<section id=""$prefix"">`n" + (Convert-Markdown $md $prefix) + "<p class=""top""><a href=""#contents"">Back to contents</a></p>`n</section>`n")
}

$html = @"
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Handover handbook $Version</title>
<style>
:root { --bg:#fbfaf8; --surface:#ffffff; --ink:#1d1b20; --muted:#5b5763; --line:#e2dfe6; --accent:#6d28d9; --code:#f3f0f7; --quote:#f5f0ff; }
@media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) { --bg:#141317; --surface:#1d1b22; --ink:#ece9f1; --muted:#a9a3b4; --line:#34303d; --accent:#b794f6; --code:#26232d; --quote:#221d2e; } }
:root[data-theme="dark"] { --bg:#141317; --surface:#1d1b22; --ink:#ece9f1; --muted:#a9a3b4; --line:#34303d; --accent:#b794f6; --code:#26232d; --quote:#221d2e; }
* { box-sizing: border-box; }
html { -webkit-text-size-adjust: 100%; }
body { margin:0; background:var(--bg); color:var(--ink); font:16px/1.6 "Segoe UI", system-ui, -apple-system, sans-serif; }
main { max-width: 920px; margin: 0 auto; padding: 32px 16px 64px; }
h1 { font-size: 2rem; line-height:1.2; margin: 0 0 4px; }
h2 { font-size: 1.6rem; line-height:1.25; margin: 0 0 12px; padding-top: 8px; }
h3 { font-size: 1.2rem; margin: 28px 0 8px; }
h4, h5 { font-size: 1.05rem; margin: 20px 0 6px; }
.sub { color: var(--muted); margin: 0 0 24px; }
section { background: var(--surface); border:1px solid var(--line); border-radius: 12px; padding: 24px; margin: 24px 0; }
nav .toc { list-style: none; padding-left: 0; } nav .toc li { margin: 4px 0; }
a { color: var(--accent); }
code { background: var(--code); border-radius: 4px; padding: 1px 5px; font: .9em Consolas, "Cascadia Mono", monospace; overflow-wrap: anywhere; }
pre { background: var(--code); border-radius: 8px; padding: 12px 14px; overflow-x: auto; line-height: 1.3; }
pre code { padding: 0; background: none; overflow-wrap: normal; }
blockquote { margin: 16px 0; padding: 4px 16px; background: var(--quote); border-left: 4px solid var(--accent); border-radius: 6px; }
.tw { overflow-x: auto; margin: 12px 0; }
table { border-collapse: collapse; width: 100%; font-size: .94rem; }
th, td { border: 1px solid var(--line); padding: 7px 9px; text-align: left; vertical-align: top; }
th { background: var(--code); }
hr { border: 0; border-top: 1px solid var(--line); margin: 24px 0; }
.top { font-size: .9rem; margin-top: 24px; }
@media print {
  body { background: #fff; color: #000; font-size: 11pt; }
  section { border: 0; padding: 0; margin: 0; page-break-before: always; }
  .top, nav { display: none; }
  a { color: #000; }
}
</style>
</head>
<body>
<main>
<h1>The Remote Support Program</h1>
<p class="sub">Handover handbook, $Version. Prepared $date from commit $head ($headDate).</p>
<nav id="contents"><h2>Contents</h2><ul class="toc">
$($toc.ToString())</ul></nav>
$($body.ToString())
</main>
</body>
</html>
"@
$handbook = Join-Path $docsOut 'Handover handbook.html'
[IO.File]::WriteAllText($handbook, $html, $utf8)
Write-Host "   $($mdFiles.Count) documents, and Handover handbook.html"

if (-not $NoPdf) {
    $edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($edge) {
        $pdf = Join-Path $docsOut 'Handover handbook.pdf'
        $uri = ([Uri]$handbook).AbsoluteUri
        # A profile of its own, or a headless Edge hands the job to an Edge already open and exits.
        $profileDir = Join-Path $stage 'edge-profile'
        $p = Start-Process -FilePath $edge -ArgumentList @('--headless', '--disable-gpu', '--no-pdf-header-footer',
            "--user-data-dir=""$profileDir""", "--print-to-pdf=""$pdf""", $uri) -PassThru -WindowStyle Hidden
        if (-not $p.WaitForExit(60000)) { try { $p.Kill() } catch {} }
        if (Test-Path $pdf) { Write-Host '   and Handover handbook.pdf, for printing' }
        else { Write-Warning 'Edge did not produce a PDF; the .html opens in any browser and prints from there.' }
    } else { Write-Host '   (no Edge, so no PDF; the .html prints from any browser)' -ForegroundColor DarkGray }
}

# ---------------------------------------------------------------------------------------------
Write-Host '== 5. Making the zip ==' -ForegroundColor Cyan
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }
if (Test-Path $zip) { Remove-Item $zip -Force }
# Entry by entry rather than ZipFile.CreateFromDirectory: under Windows PowerShell 5.1 that writes
# BACKSLASHES into entry names, which Explorer forgives and Mac and Linux unzip as one file per
# entry with "\" in its name. The team receiving this may not be on Windows.
$fs = [IO.File]::Open($zip, [IO.FileMode]::CreateNew)
$za = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $base = (Resolve-Path $stage).Path.TrimEnd('\') + '\'
    foreach ($file in Get-ChildItem $top -Recurse -File -Force) {
        $entryName = $file.FullName.Substring($base.Length).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $za.Dispose(); $fs.Dispose() }

# Read the finished zip back: the files that matter are there, and nothing secret is.
$z = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $entries = @($z.Entries | ForEach-Object { $_.FullName })
} finally { $z.Dispose() }
$must = @("$name/00-START-HERE.md", "$name/The Remote Support Program - Guide.pdf",
          "$name/Handover documents/Handover handbook.html",
          "$name/Handover documents/08-KEYS-AND-ACCOUNTS.md", "$name/Source code/RemoteSupport.sln",
          "$name/Source code/docs/HANDOVER.md", "$name/Source code with history/TheRemoteSupportProgram.bundle",
          "$name/Source code with history/HOW-TO-RESTORE.txt")
$missing = @($must | Where-Object { $entries -notcontains $_ })
$forbidden = @($entries | Where-Object {
    $_ -match '/branding/brand\.json$' -or $_ -match 'rs-credentials\.json$' -or $_ -match '\.db(-wal|-shm)?$' -or
    $_ -match '/data/keys/' -or $_ -match '/legal/' -or $_ -match '\.(pfx|p12|clixml)$' -or $_ -match '/(bin|obj|dist|demo)/' })
if ($missing.Count -gt 0) { throw "The zip is missing: $($missing -join ', ')" }
if (@($entries | Where-Object { $_.Contains([string][char]92) }).Count -gt 0) { throw "The zip has backslashes in its entry names; it would unpack wrongly on a Mac or Linux." }
if ($forbidden.Count -gt 0) { throw "The zip holds files that must not leave this PC: $($forbidden -join ', ')" }

Remove-Item $stage -Recurse -Force
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ''
Write-Host "Done: $zip ($mb MB, $($entries.Count) files)" -ForegroundColor Green
Write-Host '   Checked: the documents, the code and the history are in it; no keys, passwords or database.'
Write-Host '   Copy it onto the USB stick. The secrets it leaves out are listed in 08-KEYS-AND-ACCOUNTS.md.'
