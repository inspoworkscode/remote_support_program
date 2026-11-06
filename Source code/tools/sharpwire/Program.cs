using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Windows.Forms;
using CodecBench;
using RemoteSupport.Media;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// sharpwire - the REAL host's still-screen sharpening and its answer to the server's request for a
// complete picture, end to end (2026-10-15, HANDOVER §142).
//
// codecbench --hostloop proves the rule and the encoder with the host's own parts. This runs the host
// PROGRAM — RemoteSupport.Host.exe, built from this checkout, capturing THIS PC's screen — against a
// platform of its own on port 8181, with a viewer that decodes H.264 exactly as the console does, and
// a full-screen still page (codecbench's Desk) so the screen is known:
//
//   1. connected, then still: frames for about two seconds (the sharpening), then NONE;
//   2. a window opens: frames for about two seconds again, then none — and the picture is clean;
//   3. a text cursor blinks for four seconds: one frame a blink, not a sharpening run each time;
//   4. a moving picture with every viewer keeping up: key frames a minute apart, so hardly any;
//   5. the same with a viewer that stops reading: the server drops frames to it, asks, and the host
//      answers with key frames the healthy viewer can see.
//
// It covers the screen for about forty seconds. Leave the mouse and keyboard alone while it runs.
// Clint's live server and his database are never touched.
//
//   dotnet run -c Release --project tools/sharpwire/sharpwire.csproj
// ---------------------------------------------------------------------------------------------

const int Port = 8181;
string http = $"http://127.0.0.1:{Port}", ws = $"ws://127.0.0.1:{Port}";

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}
void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {s}");

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
Console.WriteLine("sharpwire - the real host sharpens a still screen and answers the server's request for a picture (§142)");
Console.WriteLine();

var repo = FindRepoRoot();
var work = Path.Combine(Path.GetTempPath(), "rs-sharpwire-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
var dataDir = Path.Combine(work, "data");
Directory.CreateDirectory(dataDir);
var serverDir = Path.Combine(work, "server");
var hostDir = Path.Combine(work, "host");
Log($"building the platform and the host into {work}");
foreach (var (proj, dir) in new[] { ("RemoteSupport.Platform", serverDir), ("RemoteSupport.Host", hostDir) })
{
    var b = Run("dotnet", $"build -c Release \"{Path.Combine(repo, "src", proj, proj + ".csproj")}\" -v q --nologo -p:OutDir=\"{dir}\\\"", repo, 400000);
    if (b.code != 0) { Console.WriteLine(b.output); Console.WriteLine($"{proj} would not build"); return 2; }
}
var exe = Path.Combine(serverDir, "RemoteSupport.Platform.exe");
var hostExe = Path.Combine(hostDir, "RemoteSupport.Host.exe");

var env = new Dictionary<string, string>
{
    ["RSP_Platform__DataDirectory"] = dataDir, ["RSP_Security__RequireTwoFactor"] = "false",
    ["RSP_Platform__PublicUrl"] = http,
    ["ASPNETCORE_URLS"] = http,
    ["ASPNETCORE_ENVIRONMENT"] = "Development",
};
HarnessDb.Use(env, dataDir);
string tenantKey = "";
foreach (var cmd in new[]
{
    "create-tenant \"Sharp Wire\" sharp-wire 5",
    "approve sharp-wire",
    "add-tech sharp-wire tech@local.test localtest \"Local Tech\" --admin",
})
{
    var r = Run(exe, cmd, serverDir, 60000, env);
    if (r.code != 0) { Console.WriteLine(r.output); Console.WriteLine("CLI step failed: " + cmd); return 2; }
    var line = r.output.Split('\n').FirstOrDefault(l => l.StartsWith("Tenant key:"));
    if (line != null) tenantKey = line["Tenant key:".Length..].Trim();
}
if (tenantKey.Length == 0) { Console.WriteLine("could not read the tenant key"); return 2; }

var psi = new ProcessStartInfo(exe, $"run --urls {http}") { WorkingDirectory = serverDir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
var server = Process.Start(psi)!;
var serverLog = new ConcurrentQueue<string>();
server.OutputDataReceived += (_, e) => { if (e.Data != null) serverLog.Enqueue(e.Data); };
server.ErrorDataReceived += (_, e) => { if (e.Data != null) serverLog.Enqueue(e.Data); };
server.BeginOutputReadLine(); server.BeginErrorReadLine();
Process? host = null;

using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
bool up = false;
for (int i = 0; i < 60 && !up; i++) { try { up = (await hc.GetAsync($"{http}/health")).IsSuccessStatusCode; } catch { await Task.Delay(500); } }
if (!up) { Console.WriteLine(string.Join("\n", serverLog)); Console.WriteLine("the platform did not come up"); Kill(); return 2; }

var bounds = Screen.PrimaryScreen!.Bounds;
SceneHost? scene = null;
try { await WireAsync(); }
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex); }
finally { scene?.Dispose(); Kill(); }

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
Console.WriteLine($"server, host and pictures kept at {work}");
return fail == 0 ? 0 : 1;

void Kill()
{
    try { if (host != null && !host.HasExited) host.Kill(true); } catch { }
    try { if (!server.HasExited) server.Kill(true); } catch { }
}

async Task WireAsync()
{
    scene = SceneHost.Show(SceneKind.Desk, bounds);
    await Task.Delay(900);
    if (!scene.Covering) throw new Exception("the still page is not the window on screen");

    var lr = await hc.PostAsJsonAsync($"{http}/api/login", new { email = "tech@local.test", password = "localtest" });
    var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
    var tc = await Open($"{ws}/ws/tech?token={tok}");
    var tq = new ConcurrentQueue<Msg>();
    tc.OnMessage += m => tq.Enqueue(m);
    if (await Wait<StateMsg>(tq, 8000) == null) throw new Exception("no state from the tech socket");

    // The pretend tray app: on StartSessionMsg it starts the REAL host, as the agent does.
    var tray = await Open($"{ws}/ws/device");
    tray.OnMessage += m =>
    {
        if (m is not StartSessionMsg { HostToken.Length: > 0 } s || host != null) return;
        host = Process.Start(new ProcessStartInfo(hostExe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            Arguments = $"--server {ws} --session {s.SessionId} --token {s.HostToken} --consent auto --tech \"Local Tech\""
        });
        Log($"the real host started (pid {host?.Id})");
    };
    tray.Send(new HelloMsg
    {
        Role = DeviceRole.Client, DeviceId = "sharp-wire-device", DeviceKey = "sharp-wire-key", MachineName = Environment.MachineName,
        UserName = Environment.UserName, Os = "Windows", Version = "1.0", TenantKey = tenantKey
    });
    await Task.Delay(800);

    tc.Send(new ConnectDeviceMsg { DeviceId = "sharp-wire-device", Reason = "sharpwire diagnostic" });
    var ready = await Wait<SessionReadyMsg>(tq, 20000, s => s.Session.DeviceId == "sharp-wire-device");
    if (ready == null) throw new Exception("no session started");
    var sessionId = ready.Session.SessionId;
    Log($"session {sessionId}");

    // The viewer: decodes as ScreenView does, and keeps when each video frame came and whether it was a key.
    var frames = new ConcurrentQueue<(DateTime at, bool key, int bytes)>();
    var monitors = new TaskCompletionSource<List<MonitorInfo>>();
    var codec = "";
    H264Decoder? dec = null; var decLock = new object();
    var v = await Open($"{ws}/ws/session?role=viewer&id={sessionId}&token={tok}");
    v.OnMessage += m =>
    {
        if (m is MonitorsMsg mm) monitors.TrySetResult(mm.Monitors);
        if (m is SysInfoMsg si && si.Info.TryGetValue("Video codec", out var c))
            codec = c + (si.Info.TryGetValue("Video codec fell back because", out var why) ? $" (because: {why})" : "");
    };
    v.OnBinary += f =>
    {
        // Sealed frames (before the hub settles this console-less session as clear) are not ours to open.
        if (f.Length == 0 || (f.Span[0] & BinKind.SealedBit) != 0 || f.Span[0] != BinKind.Video) return;
        var (_, key, w, h, bits) = Bin.ParseVideo(f);
        frames.Enqueue((DateTime.UtcNow, key, f.Length));
        lock (decLock)
        {
            if (dec == null || dec.Width != w || dec.Height != h) { dec?.Dispose(); dec = new H264Decoder(w, h); }
            try { dec.TryDecode(bits.Span, key); } catch { }
        }
    };
    var mons = await Task.WhenAny(monitors.Task, Task.Delay(15000)) == monitors.Task ? monitors.Task.Result : null;
    if (mons == null) throw new Exception("no MonitorsMsg — did the host start?");
    var primary = mons.FirstOrDefault(m => m.Primary) ?? mons[0];
    // This viewer says no end-to-end hello, so the hub treats it as an older console and tells the host
    // to send clear — after a grace. Until then the host is sealing, and REFUSES clear content from a
    // viewer, a subscription included (correctly). Subscribe after the grace, and again if nothing comes.
    for (int attempt = 0; attempt < 4 && frames.IsEmpty; attempt++)
    {
        await Task.Delay(2500);
        v.Send(new SubscribeMsg { Monitor = primary.Index, On = true, Codecs = new List<string> { VideoCodec.H264, VideoCodec.JpegTiles } });
        v.Send(new QualityMsg { Jpeg = 55, Fps = 12 });
        await Task.Delay(1500);
    }

    // Let the connect, the encryption setting settling and the sharpening after them all pass.
    await Task.Delay(6000);
    Check("the host sends H.264", codec.StartsWith("H.264", StringComparison.OrdinalIgnoreCase) && !frames.IsEmpty, $"{codec}; {frames.Count} frames");
    int Count(DateTime from, DateTime to) => frames.Count(x => x.at >= from && x.at < to);
    int Keys(DateTime from, DateTime to) => frames.Count(x => x.key && x.at >= from && x.at < to);

    // ---- 1. still: nothing at all ----
    var s0 = DateTime.UtcNow; await Task.Delay(3000);
    Check("a still screen, after the sharpening, sends nothing", Count(s0, DateTime.UtcNow) == 0, $"{Count(s0, DateTime.UtcNow)} frames in 3 s");

    // ---- 2. a window opens ----
    var d0 = DateTime.UtcNow;
    scene.Dialog(true);
    await Task.Delay(5000);
    int sharpening = Count(d0.AddMilliseconds(300), d0.AddMilliseconds(2000));
    int after = Count(d0.AddMilliseconds(3000), d0.AddMilliseconds(5000));
    Check("a window opening is followed by about two seconds of frames (the sharpening)", sharpening >= 10, $"{sharpening} frames from 0.3 to 2 s");
    Check("...which then stop", after == 0, $"{after} frames from 3 to 5 s");
    double psnr;
    using (var shot = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb))
    {
        using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        if (dec == null) throw new Exception("no H.264 picture was ever decoded");
        Bitmap pic; lock (decLock) pic = ToBitmap(dec);
        using (pic)
        {
            psnr = Psnr(shot, pic);
            pic.Save(Path.Combine(work, "decoded-after-window.png"), ImageFormat.Png);
            shot.Save(Path.Combine(work, "screen-after-window.png"), ImageFormat.Png);
        }
    }
    Check("...and the technician's picture is clean", psnr >= 44, $"{psnr:0.0} dB against the screen (pictures saved)");

    // ---- 3. a blinking cursor ----
    var c0 = DateTime.UtcNow;
    scene.Blink(true);
    await Task.Delay(4000);
    scene.Blink(false);
    int blinks = Count(c0, DateTime.UtcNow);
    Check("a blinking cursor is one frame a blink, not a run of sharpening", blinks is >= 3 and <= 14, $"{blinks} frames in 4 s (about 8 blinks)");
    await Task.Delay(1500);

    // ---- 4. a moving picture, everyone keeping up ----
    v.Send(new QualityMsg { Jpeg = 90, Fps = 12 });
    scene.SetKind(SceneKind.Video, bounds);
    await Task.Delay(1500);
    var m0 = DateTime.UtcNow; await Task.Delay(6000);
    int keysCalm = Keys(m0, DateTime.UtcNow), moving = Count(m0, DateTime.UtcNow);
    Check("a moving picture with every viewer keeping up has key frames a minute apart", keysCalm <= 1 && moving > 30, $"{keysCalm} key frame(s) in {moving} frames");

    // ---- 5. a viewer that stops reading ----
    // A tiny receive buffer, so this viewer is 2 MB behind in seconds: with the operating system's own
    // buffers left at their size, loopback swallowed about 17 s of moving picture first (first run).
    var slow = new ClientWebSocket();
    var tiny = new SocketsHttpHandler
    {
        ConnectCallback = async (ctx, ct) =>
        {
            var sock = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { ReceiveBufferSize = 4096 };
            await sock.ConnectAsync(ctx.DnsEndPoint, ct);
            return new System.Net.Sockets.NetworkStream(sock, ownsSocket: true);
        }
    };
    await slow.ConnectAsync(new Uri($"{ws}/ws/session?role=viewer&id={sessionId}&token={tok}"), new HttpMessageInvoker(tiny), default);
    // Never read from it. Joining is itself an event (participants, the encryption setting): let it pass.
    await Task.Delay(3000);
    var k0 = DateTime.UtcNow; await Task.Delay(15000);
    var keyTimes = frames.Where(x => x.key && x.at >= k0).Select(x => (x.at - k0).TotalSeconds).ToList();
    Check("with a viewer that cannot keep up, the server asks and the host answers with key frames",
        keyTimes.Count >= 3, $"{keyTimes.Count} key frame(s) in 15 s that the healthy viewer received, at {string.Join(", ", keyTimes.Select(t => $"{t:0.0}"))} s");
    var gaps = keyTimes.Zip(keyTimes.Skip(1), (a, c) => c - a).ToList();
    Check("...no more than about one a second", gaps.All(g => g >= 0.9), $"gaps {string.Join(", ", gaps.Select(g => $"{g:0.00}"))} s");
    try { slow.Abort(); } catch { }
    tc.Send(new EndSessionMsg { SessionId = sessionId });
    await Task.Delay(800);
}

async Task<WsConnection> Open(string url)
{
    var cws = new ClientWebSocket();
    await cws.ConnectAsync(new Uri(url), CancellationToken.None);
    var c = new WsConnection(cws);
    _ = c.RunAsync(CancellationToken.None);
    return c;
}

async Task<T?> Wait<T>(ConcurrentQueue<Msg> q, int ms, Func<T, bool>? f = null) where T : Msg
{ var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { foreach (var m in q) if (m is T t && (f == null || f(t))) return t; await Task.Delay(50); } return null; }

static unsafe Bitmap ToBitmap(H264Decoder d)
{
    var bmp = new Bitmap(d.Width, d.Height, PixelFormat.Format32bppRgb);
    var bits = bmp.LockBits(new Rectangle(0, 0, d.Width, d.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
    try { fixed (byte* src = d.Bgra) for (int y = 0; y < d.Height; y++) Buffer.MemoryCopy(src + (long)y * d.Stride, (byte*)bits.Scan0 + (long)y * bits.Stride, d.Stride, d.Stride); }
    finally { bmp.UnlockBits(bits); }
    return bmp;
}

/// <summary>Brightness PSNR, BT.601, over the overlap — codecbench --sharp's measure.</summary>
static unsafe double Psnr(Bitmap a, Bitmap b)
{
    int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
    var ra = a.LockBits(new Rectangle(0, 0, a.Width, a.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
    var rb = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
    try
    {
        double s = 0; long n = 0;
        for (int y = 0; y < h; y++)
        {
            byte* pa = (byte*)ra.Scan0 + (long)y * ra.Stride, pb = (byte*)rb.Scan0 + (long)y * rb.Stride;
            for (int x = 0; x < w; x++, pa += 4, pb += 4)
            {
                double d = 0.299 * (pa[2] - pb[2]) + 0.587 * (pa[1] - pb[1]) + 0.114 * (pa[0] - pb[0]);
                s += d * d; n++;
            }
        }
        double mse = s / Math.Max(1, n);
        return mse <= 1e-9 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }
    finally { a.UnlockBits(ra); b.UnlockBits(rb); }
}

static (int code, string output) Run(string file, string argLine, string cwd, int ms, Dictionary<string, string>? env = null)
{
    var psi = new ProcessStartInfo(file, argLine) { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
    using var p = Process.Start(psi)!;
    var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    p.WaitForExit(ms);
    return (p.HasExited ? p.ExitCode : -1, o);
}

static string FindRepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        if (Directory.Exists(Path.Combine(d.FullName, "src", "RemoteSupport.Platform"))) return d.FullName;
    throw new InvalidOperationException("could not find the repository root");
}
