using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// Which capture path is a REAL machine using, and what is it costing?
//
// `tools/codecbench` answers that on this laptop, where the host is not a service, does not run as
// SYSTEM, and never sees the Winlogon desktop. This answers it over a real session against a real
// machine, which is the only place the four questions stage 2.1 left open can be settled:
//
//   * does Desktop Duplication work when the host runs as SYSTEM in the interactive session?
//   * what happens at a UAC prompt -- the Winlogon desktop costs an ACCESS_LOST and a rebuild
//   * is the picture right, not merely fast?
//   * and when duplication IS refused, does the GDI fallback fire and say so?
//
// It subscribes the way the console does -- waiting for MonitorsMsg rather than subscribing on its
// own -- so a missing subscription shows up here as "no tiles" rather than being papered over. It
// then measures what actually arrives: time to the first frame, frames and KB per second, tiles
// per frame, and it composes the tiles into a PNG so the picture can be looked at rather than
// inferred from a byte count.
//
//   capturecheck [MACHINE] [--seconds N] [--quality Q] [--fps N] [--out FILE] [--uac] [--no-provoke]
//
// One check here is less obvious than the rest and matters more than any of them. An idle machine
// sending NOTHING is correct -- it is the whole point of 2.1 -- and it is also exactly what a
// capture that died after its first frame looks like. No number distinguishes them. So the harness
// makes the screen change on purpose, through the product's own input path, and requires frames to
// follow and the picture to actually move. --no-provoke turns that off for a machine that must not
// be touched.
//
// --uac waits for you to trigger a UAC prompt on that machine and reports what the capture did
// across it. That one needs a person at the other end and is the reason this is not a unit test.
// ---------------------------------------------------------------------------------------------

string http = "http://localhost:8090", ws = "ws://localhost:8090";
string want = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "DESKTOP-OPHQIDH";
int seconds = ArgInt("--seconds", 12, 3, 300);
int quality = ArgInt("--quality", 55, 20, 95);
int fps = ArgInt("--fps", 12, 1, 30);
bool uac = args.Any(a => a.Equals("--uac", StringComparison.OrdinalIgnoreCase));
// Proving the capture is live means making the screen change, and the only lever this harness has
// on somebody else's machine is the input path. The Start menu is the cheapest change that is
// certain to repaint, and Escape puts it back.
bool provoke = !args.Any(a => a.Equals("--no-provoke", StringComparison.OrdinalIgnoreCase));
string outPng = ArgStr("--out", Path.Combine(AppContext.BaseDirectory, "capture.png"));

int pass = 0, fail = 0;
void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {s}");
void Pass(string what, string detail = "") { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " - " + detail : "")}"); }
void Fail(string what, string detail = "") { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " - " + detail : "")}"); }
void Note(string what) => Console.WriteLine($"  ..    {what}");

using var hc = new HttpClient();
var creds = RemoteSupport.Tools.RsCredentials.Load();
var lr = await hc.PostAsJsonAsync($"{http}/api/login", new { email = creds.Email, password = creds.Password, code = creds.Code() });
if (!lr.IsSuccessStatusCode) { Log($"could not log in to {http} - is the platform running?"); return 2; }
var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;

var tech = new ClientWebSocket();
await tech.ConnectAsync(new Uri($"{ws}/ws/tech?token={tok}"), default);
var tc = new WsConnection(tech);
var tq = new ConcurrentQueue<Msg>();
tc.OnMessage += m => tq.Enqueue(m);
_ = tc.RunAsync();

async Task<T?> W<T>(int ms, Func<T, bool>? f = null) where T : Msg
{
    var e = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < e) { foreach (var m in tq) if (m is T t && (f == null || f(t))) return t; await Task.Delay(50); }
    return null;
}

var st = await W<StateMsg>(8000);
var dev = st?.Devices.FirstOrDefault(d => d.MachineName.Equals(want, StringComparison.OrdinalIgnoreCase));
if (dev == null) { Log($"device {want} not found. Known: {string.Join(", ", st?.Devices.Select(d => d.MachineName) ?? new List<string>())}"); return 2; }
Log($"device {dev.MachineName} ({dev.DeviceId}) online={dev.Online}");
if (!dev.Online) { Log("that machine is offline"); return 2; }

tc.Send(new ConnectDeviceMsg { DeviceId = dev.DeviceId, Reason = "capturecheck diagnostic" });
var ready = await W<SessionReadyMsg>(25000, s => s.Session.DeviceId == dev.DeviceId);
if (ready == null) { Log("no session started"); return 2; }
Log($"session {ready.Session.SessionId} ready");
Console.WriteLine();

// ---- viewer socket, wired the way the session window wires it -------------------------------
var canvasLock = new object();
Bitmap? canvas = null;
int monitorIndex = 0, monW = 0, monH = 0;
long frames = 0, tiles = 0, bytes = 0;
DateTime firstFrameAt = DateTime.MinValue, subscribedAt = DateTime.MinValue;
var monitors = new List<MonitorInfo>();
var gotMonitors = new TaskCompletionSource();
Dictionary<string, string> sysInfo = new();
var sysInfoUpdates = 0;
var captureSourceSeen = new List<string>();
// Every desktop the host has reported being attached to, in order. "Winlogon" is a UAC prompt,
// the lock screen or Ctrl+Alt+Del.
var desktopsSeen = new List<string>();
bool onSecureDesktop = false;
Bitmap? secureShot = null, secureFirst = null;

var v = new ClientWebSocket();
await v.ConnectAsync(new Uri($"{ws}/ws/session?role=viewer&id={ready.Session.SessionId}&token={tok}"), default);
var vc = new WsConnection(v);

vc.OnMessage += m =>
{
    switch (m)
    {
        case MonitorsMsg mm:
            monitors = mm.Monitors;
            gotMonitors.TrySetResult();
            break;
        // The host re-sends this whenever the capture path changes, so every update is an event
        // worth keeping rather than a refresh to overwrite.
        case SysInfoMsg si:
            sysInfo = new Dictionary<string, string>(si.Info);
            sysInfoUpdates++;
            if (si.Info.TryGetValue("Input desktop", out var dsk) && dsk.Length > 0
                && (desktopsSeen.Count == 0 || desktopsSeen[^1] != dsk))
            {
                desktopsSeen.Add(dsk);
                Log($"host says the input desktop is now: {dsk}");
                // A prompt is on screen for a second or two at most. Rather than trying to time a
                // snapshot against it, keep the newest frame for as long as we are on the secure
                // desktop and save that at the end. The picture of the prompt is the only thing
                // that proves the capture can SEE it, as opposed to merely surviving it.
                bool nowSecure = dsk.Contains("Winlogon", StringComparison.OrdinalIgnoreCase)
                              || dsk.Contains("Screen-saver", StringComparison.OrdinalIgnoreCase);
                // Each entry onto the secure desktop starts a fresh "first frame": a later
                // prompt held open longer is more use than the first one, which may have been
                // dismissed in a blink.
                if (nowSecure && !onSecureDesktop) { secureFirst?.Dispose(); secureFirst = null; }
                onSecureDesktop = nowSecure;
            }
            if (si.Info.TryGetValue("Screen capture", out var src))
            {
                if (captureSourceSeen.Count == 0 || captureSourceSeen[^1] != src)
                {
                    captureSourceSeen.Add(src);
                    Log($"host says it is capturing with: {src}");
                }
            }
            break;
    }
};

vc.OnBinary += frame =>
{
    var s = frame.Span;
    if (s.Length < 9 || s[0] != BinKind.Tiles) return;
    int monitor = BitConverter.ToInt32(s.Slice(1, 4));
    if (monitor != monitorIndex) return;
    if (firstFrameAt == DateTime.MinValue) firstFrameAt = DateTime.Now;
    frames++; bytes += s.Length;
    int count = BitConverter.ToInt32(s.Slice(5, 4));
    tiles += count;
    int o = 9;
    lock (canvasLock)
    {
        if (canvas == null) return;
        using var g = Graphics.FromImage(canvas);
        g.CompositingMode = CompositingMode.SourceCopy;
        for (int i = 0; i < count && o + 20 <= s.Length; i++)
        {
            int x = BitConverter.ToInt32(s.Slice(o, 4)); o += 4;
            int y = BitConverter.ToInt32(s.Slice(o, 4)); o += 4;
            int w = BitConverter.ToInt32(s.Slice(o, 4)); o += 4;
            int h = BitConverter.ToInt32(s.Slice(o, 4)); o += 4;
            int len = BitConverter.ToInt32(s.Slice(o, 4)); o += 4;
            if (o + len > s.Length) break;
            try
            {
                using var ms = new MemoryStream(frame.Slice(o, len).ToArray());
                using var tile = Image.FromStream(ms);
                g.DrawImage(tile, new Rectangle(x, y, w, h));
            }
            catch { }
            o += len;
        }
    }
    // Both ends of the secure-desktop episode are kept, and the FIRST is the one that matters.
    // Saving only the newest produced a perfectly whole, perfectly promptless picture: dismissing
    // a UAC prompt closes the dialog first and switches desktop a moment later, so the last frame
    // before the switch is reliably the one with nothing on it.
    if (onSecureDesktop)
        lock (canvasLock)
        {
            if (canvas != null)
            {
                if (secureFirst == null) secureFirst = (Bitmap)canvas.Clone();
                secureShot?.Dispose(); secureShot = (Bitmap)canvas.Clone();
            }
        }
};
_ = vc.RunAsync();

// ---- subscribe exactly as SwitchMonitor does ------------------------------------------------
if (await Task.WhenAny(gotMonitors.Task, Task.Delay(8000)) != gotMonitors.Task)
{
    Fail("the monitor list reached the viewer", "no MonitorsMsg in 8 s - the console would never subscribe and would stay black");
    tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId });
    return 1;
}
Pass("the monitor list reached the viewer", string.Join(" | ", monitors.Select(x => $"{x.Index}: {x.Width}x{x.Height}{(x.Primary ? " primary" : "")}")));

var mi = monitors[0];
monitorIndex = mi.Index;
lock (canvasLock)
{
    monW = mi.Width; monH = mi.Height;
    canvas = new Bitmap(Math.Max(1, monW), Math.Max(1, monH), PixelFormat.Format24bppRgb);
    using var g = Graphics.FromImage(canvas);
    g.Clear(Color.FromArgb(20, 20, 20));
}
subscribedAt = DateTime.Now;
vc.Send(new SubscribeMsg { Monitor = mi.Index, On = true });
vc.Send(new QualityMsg { Jpeg = quality, Fps = fps });
Log($"subscribed to monitor {mi.Index}, quality {quality}, {fps} fps");

// ---- question 1: does a forced-full frame arrive, and how fast? -----------------------------
// This is the one that goes wrong quietly. Duplication only hands over what MOVED, so a machine
// sitting at an idle desktop has nothing to give -- and a technician would sit looking at black.
for (int i = 0; i < 100 && firstFrameAt == DateTime.MinValue; i++) await Task.Delay(50);
if (firstFrameAt == DateTime.MinValue)
    Fail("a first frame arrived after subscribing", "nothing in 5 s - this is the black screen, and on an idle desktop it means the buffer was never primed");
else
{
    var ms = (firstFrameAt - subscribedAt).TotalMilliseconds;
    if (ms < 3000) Pass("a first frame arrived after subscribing", $"{ms:0} ms");
    else Fail("a first frame arrived promptly", $"took {ms:0} ms");
}

Console.WriteLine();
Log($"measuring for {seconds} s - leave that machine ALONE unless you meant to drive it");
var t0 = DateTime.Now;
long f0 = frames, b0 = bytes, ti0 = tiles;
await Task.Delay(seconds * 1000);
double el = (DateTime.Now - t0).TotalSeconds;
long df = frames - f0, db = bytes - b0, dt = tiles - ti0;

Console.WriteLine();
Console.WriteLine($"  while nobody was touching it, over {el:0.0} s, monitor {monitorIndex} at {monW}x{monH}:");
Console.WriteLine($"    frames        {df}  ({df / el:0.0}/s, asked for {fps})");
Console.WriteLine($"    wire          {db / 1024.0 / el:0.0} KB/s");
Console.WriteLine($"    tiles/frame   {(df == 0 ? 0 : (double)dt / df):0.0}");
Console.WriteLine();

// ---- the check the idle measurement above CANNOT make -----------------------------------------
// Nothing arriving from a still screen is correct, and it is the whole point of 2.1: an idle
// machine should cost nothing. It is also precisely what a capture that has died after its first
// frame looks like, and the two are indistinguishable from any number printed above. So make the
// screen change on purpose and require frames to follow. Without this, a stall ships as a feature.
if (df == 0) Note("nothing arrived, which is correct for a still screen - the next check is what tells that apart from a stall");
if (provoke)
{
    Bitmap? before;
    lock (canvasLock) before = canvas == null ? null : (Bitmap)canvas.Clone();

    Log("making the screen change on purpose: opening the Start menu on that machine, then closing it");
    long fb = frames;
    vc.Send(new KeyMsg { Vk = 0x5B, Down = true });          // VK_LWIN
    vc.Send(new KeyMsg { Vk = 0x5B, Down = false });
    await Task.Delay(3000);
    long provoked = frames - fb;
    Bitmap? after;
    lock (canvasLock) after = canvas == null ? null : (Bitmap)canvas.Clone();
    vc.Send(new KeyMsg { Vk = 0x1B, Down = true });          // VK_ESCAPE, put it back
    vc.Send(new KeyMsg { Vk = 0x1B, Down = false });
    await Task.Delay(800);

    if (provoked > 0) Pass("frames followed a change on screen", $"{provoked} frames in 3 s - the capture is LIVE, not stuck on its first frame");
    else Fail("frames followed a change on screen", "none - either the capture has stalled or the keystroke never landed");

    // Frames arriving is not quite enough: they could be arriving and carrying the same pixels.
    // The composed picture having actually changed is the end of the argument.
    if (before != null && after != null)
    {
        double diff = MeanDiff(before, after);
        if (diff > 0.5) Pass("the picture actually changed", $"mean difference {diff:0.0} of 255 between before and after");
        else Fail("the picture actually changed", $"mean difference only {diff:0.0} of 255 - frames arrived but the image did not move");
        var provokedPng = Path.Combine(Path.GetDirectoryName(outPng)!, "capture-provoked.png");
        after.Save(provokedPng, ImageFormat.Png);
        Note($"the changed picture is at {provokedPng} - the Start menu should be open in it");
    }
    before?.Dispose(); after?.Dispose();
    Console.WriteLine();
}

// ---- question 2: which path, and did it say so? ---------------------------------------------
string source = sysInfo.GetValueOrDefault("Screen capture", "");
string runningAs = sysInfo.GetValueOrDefault("Host running as", "");
if (source.Length == 0)
    Fail("the host reported which capture path it is using", "no 'Screen capture' line - is that machine running a build from before 2.1?");
else if (source.Contains("DXGI", StringComparison.OrdinalIgnoreCase))
    Pass("the host is using Desktop Duplication", source);
else
{
    // Not a failure. It is what the fallback is FOR, and the point is that it is visible.
    Note($"the host fell back to {source}");
    if (sysInfo.TryGetValue("Capture fell back because", out var why)) Note($"because: {why}");
    Pass("the fallback is visible to the technician", "the Info tab names the path and the reason");
}
if (runningAs.Length > 0) Note($"host identity: {runningAs}");
if (runningAs.Contains("SYSTEM") && source.Contains("DXGI", StringComparison.OrdinalIgnoreCase))
    Pass("Desktop Duplication works with the host running as SYSTEM", "the question this laptop cannot answer");

// ---- question 3: is the picture right, not merely fast? -------------------------------------
lock (canvasLock)
{
    if (canvas == null) Fail("a canvas was composed", "none");
    else
    {
        double sum = 0; int n = 0, nonBlack = 0;
        for (int y = 0; y < canvas.Height; y += 17)
        for (int x = 0; x < canvas.Width; x += 17)
        {
            var c = canvas.GetPixel(x, y);
            double l = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
            sum += l; n++; if (l > 28) nonBlack++;
        }
        double mean = sum / Math.Max(1, n), lit = 100.0 * nonBlack / Math.Max(1, n);
        canvas.Save(outPng, ImageFormat.Png);
        // A desktop is never uniformly dark. This does not prove the picture is CORRECT -- only a
        // pair of eyes on the PNG does that -- but it does rule out the failure that matters.
        if (lit > 5) Pass("the composed picture is not black", $"mean luminance {mean:0}/255, {lit:0}% of samples lit");
        else Fail("the composed picture is not black", $"mean luminance {mean:0}/255, only {lit:0}% lit - LOOK AT THE PNG");
        Console.WriteLine($"  ..    picture written to {outPng} - open it, that is the actual test");
    }
}

// ---- question 4: the Winlogon desktop ---------------------------------------------------------
if (uac)
{
    Console.WriteLine();
    Log("UAC CHECK. On that machine, make a UAC prompt appear - right-click anything and");
    Log("choose 'Run as administrator' is enough. LEAVE IT OPEN a few seconds before you");
    Log("dismiss it - a prompt gone within a frame or two proves the switch but leaves no");
    Log("picture of the prompt itself. You have 45 seconds.");
    Log("The capture thread should follow to the Winlogon desktop, lose duplication, rebuild,");
    Log("and keep sending. Watch for the frame count to keep climbing.");
    long before = frames;
    int rebuildsBefore = Rebuilds(sysInfo);
    desktopsSeen.Clear();
    for (int i = 0; i < 45; i++)
    {
        await Task.Delay(1000);
        if (i % 5 == 4) Log($"  {frames - before} frames, desktop {sysInfo.GetValueOrDefault("Input desktop", "?")}");
    }
    long after = frames - before;
    int rebuildsAfter = Rebuilds(sysInfo);
    Console.WriteLine();

    // The whole verdict turns on one thing: did the machine ever go to the Winlogon desktop? A
    // capture that stopped because a prompt took the screen away and a capture that stopped
    // because nothing was happening produce the SAME zero. Asking the host which desktop it is
    // attached to is the only thing that separates them -- so if no prompt appeared, this reports
    // nothing rather than a pass or a fail, because it measured nothing.
    bool sawSecureDesktop = desktopsSeen.Any(d => d.Contains("Winlogon", StringComparison.OrdinalIgnoreCase)
                                               || d.Contains("Screen-saver", StringComparison.OrdinalIgnoreCase));
    // A host too old to report its desktop is NOT a host that stayed on Default, and saying so
    // cost a run: the machine reported two rebuilds and a switch to Winlogon in its own trace
    // while this printed "no prompt appeared". Absence of an answer is not an answer.
    if (!sysInfo.ContainsKey("Input desktop"))
    {
        Note("THAT MACHINE IS RUNNING AN OLDER HOST - it does not report which desktop it is on,");
        Note("so this check cannot see a prompt even if one appeared. Ship the current build to it");
        Note("and run this again. Until then the only evidence is the host's own trace:");
        Note("  fetchfile DESKTOP-OPHQIDH \"C:\\ProgramData\\RemoteSupport\\host-trace.log\"");
        Note($"(rebuilds reported: {rebuildsAfter}. Any number above zero means the desktop DID switch.)");
    }
    else if (!sawSecureDesktop)
    {
        Note($"NO PROMPT APPEARED. The host never left the {sysInfo.GetValueOrDefault("Input desktop", "Default")} desktop,");
        Note("so this check proved nothing either way - it is not a pass and not a failure.");
        Note($"({after} frames arrived, which on a still screen is simply what a still screen sends.)");
        Note("Run it again and raise a real UAC prompt while the countdown is running.");
    }
    else
    {
        Pass("the machine went to the secure desktop", string.Join(" -> ", desktopsSeen));
        if (after > 5) Pass("frames kept arriving across the UAC prompt", $"{after} frames - the capture followed and recovered");
        else Fail("frames kept arriving across the UAC prompt", $"only {after} - the capture did NOT survive the desktop switch");
        if (rebuildsAfter > rebuildsBefore)
            Pass("the duplication was rebuilt", $"{rebuildsBefore} -> {rebuildsAfter}");
        else
            Note($"no rebuild was counted ({rebuildsAfter}) - unexpected on a desktop switch, worth a look at the host trace");
        // Surviving the switch and being able to SEE the secure desktop are different claims, and
        // only one of them can be settled by a picture.
        if (secureShot != null)
        {
            var dir = Path.GetDirectoryName(outPng)!;
            var securePng = Path.Combine(dir, "capture-winlogon.png");
            (secureFirst ?? secureShot).Save(securePng, ImageFormat.Png);
            var latePng = Path.Combine(dir, "capture-winlogon-last.png");
            secureShot.Save(latePng, ImageFormat.Png);
            Pass("a frame was captured WHILE the secure desktop was up", securePng);
            Note("That is the FIRST frame of the last prompt - the one most likely to have the");
            Note("dialog still on it. The last frame is beside it as capture-winlogon-last.png,");
            Note("and is normally the dimmed desktop alone, because dismissing a prompt closes the");
            Note("dialog a moment before Windows switches desktop back.");
            Note("The dialog being visible is what proves the capture can see UAC at all - a host");
            Note("that is not SYSTEM gets a frozen picture here and no error whatsoever.");
        }
        else
        {
            Note("No frame arrived while the secure desktop was up, so whether the prompt itself can");
            Note("be SEEN is still unanswered - only that the capture survived the switch. A prompt");
            Note("dismissed within a frame or two is the usual reason; hold the next one open longer.");
        }
    }
}

Console.WriteLine();
foreach (var kv in sysInfo) Console.WriteLine($"  info: {kv.Key}: {kv.Value}");
Console.WriteLine();
Console.WriteLine($"{pass} passed, {fail} failed.");

tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId });
await Task.Delay(600);
return fail == 0 ? 0 : 1;

static int Rebuilds(Dictionary<string, string> info)
{
    if (!info.TryGetValue("Capture rebuilds", out var s)) return 0;
    var digits = new string(s.TakeWhile(char.IsDigit).ToArray());
    return int.TryParse(digits, out var n) ? n : 0;
}

int ArgInt(string name, int dflt, int lo, int hi)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out var n)) return Math.Clamp(n, lo, hi);
    return dflt;
}
string ArgStr(string name, string dflt)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
    return dflt;
}

static double MeanDiff(Bitmap a, Bitmap b)
{
    if (a.Width != b.Width || a.Height != b.Height) return 255;
    double total = 0; long n = 0;
    // Sampled rather than exhaustive: this is asking "did the picture move", not measuring
    // fidelity, and a 2 MP exhaustive compare through GetPixel is slow enough to be felt.
    for (int y = 0; y < a.Height; y += 7)
    for (int x = 0; x < a.Width; x += 7)
    {
        var ca = a.GetPixel(x, y); var cb = b.GetPixel(x, y);
        total += Math.Abs(ca.R - cb.R) + Math.Abs(ca.G - cb.G) + Math.Abs(ca.B - cb.B);
        n += 3;
    }
    return total / Math.Max(1, n);
}
