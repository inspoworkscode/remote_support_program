<#
    Drives headless Edge or Chrome over the DevTools protocol, for Test-WebChecklist.ps1.
    --dump-dom cannot wait for a page that is still reading files; this can.
#>

# A static file server on localhost, in a background runspace. The self-test needs the browser's
# own scratch storage, which Edge refuses to a page opened from a file.
function Start-StaticServer([string]$root, [int]$port) {
    $ps = [PowerShell]::Create()
    [void]$ps.AddScript({
        param($root, $port)
        $l = New-Object System.Net.HttpListener
        $l.Prefixes.Add("http://localhost:$port/")
        $l.Start()
        while ($l.IsListening) {
            try { $c = $l.GetContext() } catch { break }
            if ($c.Request.Url.AbsolutePath -eq '/__stop') { $c.Response.Close(); $l.Stop(); break }
            $path = Join-Path $root ([Uri]::UnescapeDataString($c.Request.Url.AbsolutePath.TrimStart('/')))
            if (Test-Path $path -PathType Leaf) {
                $bytes = [IO.File]::ReadAllBytes($path)
                $c.Response.ContentType = if ($path -like '*.html') { 'text/html; charset=utf-8' } else { 'application/octet-stream' }
                $c.Response.OutputStream.Write($bytes, 0, $bytes.Length)
            } else { $c.Response.StatusCode = 404 }
            $c.Response.Close()
        }
    }).AddArgument($root).AddArgument($port)
    $handle = $ps.BeginInvoke()
    Start-Sleep -Milliseconds 500
    return @{ PS = $ps; Handle = $handle; Port = $port }
}

function Stop-StaticServer($s) {
    try { Invoke-WebRequest "http://localhost:$($s.Port)/__stop" -UseBasicParsing -TimeoutSec 1 | Out-Null } catch { }
    $s.PS.Stop()
    $s.PS.Dispose()
}

function Start-Browser([string]$exe, [string]$url, [string]$profileDir, [int]$port, [string]$size = '1500,950') {
    $argv = @('--headless=new', '--disable-gpu', "--user-data-dir=$profileDir", '--no-first-run', '--hide-scrollbars',
              "--remote-debugging-port=$port", "--window-size=$size", $url)
    $proc = Start-Process $exe -ArgumentList $argv -PassThru -WindowStyle Hidden
    $ws = $null
    for ($i = 0; $i -lt 100 -and -not $ws; $i++) {
        Start-Sleep -Milliseconds 200
        try {
            $targets = Invoke-RestMethod "http://127.0.0.1:$port/json" -TimeoutSec 2
            $t = $targets | Where-Object { $_.type -eq 'page' } | Select-Object -First 1
            if ($t) { $ws = $t.webSocketDebuggerUrl }
        } catch { }
    }
    if (-not $ws) { Stop-Process $proc -Force -ErrorAction SilentlyContinue; throw 'The browser did not open its debugging port.' }
    $sock = New-Object System.Net.WebSockets.ClientWebSocket
    $sock.Options.KeepAliveInterval = [TimeSpan]::FromSeconds(10)
    $sock.ConnectAsync([Uri]$ws, [Threading.CancellationToken]::None).Wait()
    return @{ Proc = $proc; Sock = $sock; Id = 0; Events = New-Object System.Collections.ArrayList }
}

function Send-Cdp($b, [string]$method, $params = @{}) {
    $b.Id++
    $id = $b.Id
    $json = @{ id = $id; method = $method; params = $params } | ConvertTo-Json -Compress -Depth 10
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $b.Sock.SendAsync((New-Object ArraySegment[byte] (, $bytes)), 'Text', $true, [Threading.CancellationToken]::None).Wait()
    $buf = New-Object byte[] 1048576
    while ($true) {
        $ms = New-Object IO.MemoryStream
        do {
            $r = $b.Sock.ReceiveAsync((New-Object ArraySegment[byte] (, $buf)), [Threading.CancellationToken]::None).Result
            $ms.Write($buf, 0, $r.Count)
        } while (-not $r.EndOfMessage)
        $msg = [Text.Encoding]::UTF8.GetString($ms.ToArray()) | ConvertFrom-Json
        if ($msg.id -eq $id) {
            if ($msg.error) { throw "CDP $method failed: $($msg.error.message)" }
            return $msg.result
        }
        if ($msg.method) { [void]$b.Events.Add($msg) }
    }
}

function Invoke-Js($b, [string]$expression) {
    $r = Send-Cdp $b 'Runtime.evaluate' @{ expression = $expression; awaitPromise = $true; returnByValue = $true }
    if ($r.exceptionDetails) { throw "Page threw: $($r.exceptionDetails.exception.description)" }
    return $r.result.value
}

# Waits until the page's title starts with $prefix, and returns it.
function Wait-Title($b, [string]$prefix, [int]$seconds = 60) {
    $js = "new Promise(res => { const t0 = Date.now(); (function w() { if (document.title.startsWith('$prefix') || Date.now() - t0 > $($seconds * 1000)) res(document.title); else setTimeout(w, 100); })(); })"
    # Asked too early, the question lands on the blank page the browser starts with, which is then
    # replaced: ask again.
    for ($i = 0; ; $i++) {
        try { return Invoke-Js $b $js }
        catch { if ($i -ge 20 -or "$_" -notmatch 'context was destroyed|Cannot find context') { throw }; Start-Sleep -Milliseconds 300 }
    }
}

function Save-Screenshot($b, [string]$path) {
    $r = Send-Cdp $b 'Page.captureScreenshot' @{ format = 'png' }
    [IO.File]::WriteAllBytes($path, [Convert]::FromBase64String($r.data))
}

function Stop-Browser($b) {
    try { $b.Sock.Dispose() } catch { }
    Stop-Process $b.Proc -Force -ErrorAction SilentlyContinue
    Get-CimInstance Win32_Process -Filter "ParentProcessId=$($b.Proc.Id)" -ErrorAction SilentlyContinue |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}
