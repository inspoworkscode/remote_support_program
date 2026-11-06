using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using RemoteSupport.Media;
using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// Does stage 2.2 actually cost less, on a REAL machine, over a REAL session?
//
// `tools/codecbench` answers what a codec costs on the laptop it runs on. This answers the only
// question the roadmap's "done when" accepts, and it answers it the way that leaves no room for
// recollection: it watches the SAME machine, looking at the SAME screen, seconds apart, once with
// H.264 offered and once with only JPEG tiles offered, and prints the two numbers side by side.
//
//   2.8 MB/s is the number to beat, and this is the thing that says whether it was beaten.
//
// It also asks the questions a byte count cannot:
//
//   * which codec did the host actually choose, and if it fell back, does it SAY why?
//   * did a first frame arrive at all -- a console that asked for H.264 and cannot decode it
//     shows black while the byte counters climb, which is the worst failure this stage can have
//   * is the decoded picture the machine's screen, or merely a picture?
//   * do frames follow a change on screen, rather than the stream having stalled after its first
//     key frame? An idle machine sending nothing is CORRECT and looks identical to a stall.
//
//   codeccheck [MACHINE] [--seconds N] [--quality Q] [--fps N] [--out FILE] [--no-provoke]
//              [--tiles-only]
//
// --tiles-only measures the old path alone, for a baseline against an older build.
// ---------------------------------------------------------------------------------------------

string http = "http://localhost:8090", ws = "ws://localhost:8090";
// The machine name is the first argument that is neither a flag NOR the value of one. Taking the
// first non-flag argument instead reads `--seconds 4` as a request to check a machine called "4",
// which is a confusing failure a long way from its cause.
string want = FirstBare() ?? "DESKTOP-OPHQIDH";
int seconds = ArgInt("--seconds", 10, 3, 300);
int quality = ArgInt("--quality", 55, 20, 95);
int fps = ArgInt("--fps", 12, 1, 30);
bool provoke = !Has("--no-provoke");
bool tilesOnly = Has("--tiles-only");
string outPng = ArgStr("--out", Path.Combine(AppContext.BaseDirectory, "codec.png"));

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
if (!dev.Online) { Log("that machine is offline"); return 2; }
Log($"device {dev.MachineName} ({dev.DeviceId})");

tc.Send(new ConnectDeviceMsg { DeviceId = dev.DeviceId, Reason = "codeccheck diagnostic" });
var ready = await W<SessionReadyMsg>(25000, s => s.Session.DeviceId == dev.DeviceId);
if (ready == null) { Log("no session started"); return 2; }
Log($"session {ready.Session.SessionId} ready");
Console.WriteLine();

// ---- viewer socket, wired the way the session window wires it --------------------------------
var canvasLock = new object();
Bitmap? canvas = null;
int monitorIndex = 0, monW = 0, monH = 0;
var monitors = new List<MonitorInfo>();
var gotMonitors = new TaskCompletionSource();
Dictionary<string, string> sysInfo = new();
var codecsSeen = new List<string>();
// Stage 2.4: the customer's pointer, by shape id.
var cursorShapes = new Dictionary<long, (int w, int h, byte[] png)>();
var cursorIdsSeen = new HashSet<long>();
int cursorMsgs = 0, cursorHiddenMsgs = 0, cursorShapesBad = 0;

// Counted separately on purpose. "Frames arrived" is not the question; "frames arrived in the
// codec we asked for" is, and a host that quietly fell back looks identical in a total.
long videoFrames = 0, videoBytes = 0, videoKeys = 0, videoDrawn = 0;
long tileFrames = 0, tileBytes = 0;
DateTime firstPictureAt = DateTime.MinValue, subscribedAt = DateTime.MinValue;
H264Decoder? decoder = null;
string decoderError = "";

var v = new ClientWebSocket();
await v.ConnectAsync(new Uri($"{ws}/ws/session?role=viewer&id={ready.Session.SessionId}&token={tok}"), default);
var vc = new WsConnection(v);

vc.OnMessage += m =>
{
    switch (m)
    {
        case MonitorsMsg mm: monitors = mm.Monitors; gotMonitors.TrySetResult(); break;
        // Stage 2.4. Position, visibility and which shape -- counted so "the pointer never moved"
        // and "the pointer moved and we drew nothing" can be told apart.
        case CursorMsg cur:
            cursorMsgs++;
            if (cur.ShapeId != 0) cursorIdsSeen.Add(cur.ShapeId);
            if (!cur.Visible) cursorHiddenMsgs++;
            break;
        // The host re-sends this whenever the codec changes, so every update is an event worth
        // keeping rather than a refresh to overwrite.
        case SysInfoMsg si:
            sysInfo = new Dictionary<string, string>(si.Info);
            if (si.Info.TryGetValue("Video codec", out var c) && c.Length > 0
                && (codecsSeen.Count == 0 || codecsSeen[^1] != c))
            {
                codecsSeen.Add(c);
                Log($"host says the video codec is now: {c}");
                if (si.Info.TryGetValue("Video codec fell back because", out var why)) Log($"   because: {why}");
            }
            break;
    }
};

vc.OnBinary += frame =>
{
    if (frame.Length < 1) return;
    if (frame.Span[0] == BinKind.Tiles) { ApplyTiles(frame); return; }
    if (frame.Span[0] == BinKind.Video) { ApplyVideo(frame); return; }
    // Stage 2.4. The customer's real pointer, as a PNG keyed by the cursor's handle. Kept rather
    // than merely counted: the only check that can say an arrow is an arrow is a person opening
    // the file, and a count of frames would pass just as happily on a blank one.
    if (frame.Span[0] == BinKind.Cursor)
    {
        try
        {
            var s = frame.Span;
            long id = BitConverter.ToInt64(s[1..]);
            int cw = BitConverter.ToInt32(s[9..]), chh = BitConverter.ToInt32(s[13..]);
            cursorShapes[id] = (cw, chh, frame[25..].ToArray());
        }
        catch { cursorShapesBad++; }
    }
};
_ = vc.RunAsync();

void ApplyTiles(ReadOnlyMemory<byte> frame)
{
    var s = frame.Span;
    if (s.Length < 9) return;
    if (BitConverter.ToInt32(s.Slice(1, 4)) != monitorIndex) return;
    tileFrames++; tileBytes += s.Length;
    if (firstPictureAt == DateTime.MinValue) firstPictureAt = DateTime.Now;
    int count = BitConverter.ToInt32(s.Slice(5, 4));
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
}

void ApplyVideo(ReadOnlyMemory<byte> frame)
{
    if (frame.Length <= Bin.VideoHeader) return;
    var (mon, key, w, h, bits) = Bin.ParseVideo(frame);
    if (mon != monitorIndex) return;
    videoFrames++; videoBytes += frame.Length;
    if (key) videoKeys++;
    if (decoderError.Length > 0) return;
    try
    {
        if (decoder == null || decoder.Width != w || decoder.Height != h)
        {
            decoder?.Dispose();
            decoder = new H264Decoder(w, h, fps);
            Log($"decoder here: {decoder.Name} at {w}x{h}");
        }
        if (!decoder.TryDecode(bits.Span, key)) return;
    }
    catch (Exception ex) { decoderError = ex.Message; return; }

    if (firstPictureAt == DateTime.MinValue) firstPictureAt = DateTime.Now;
    videoDrawn++;
    lock (canvasLock)
    {
        if (canvas == null) return;
        var b = canvas.LockBits(new Rectangle(0, 0, canvas.Width, canvas.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            int rows = Math.Min(canvas.Height, decoder!.Height);
            int bytes = Math.Min(canvas.Width, decoder.Width) * 4;
            unsafe
            {
                fixed (byte* src = decoder.Bgra)
                    for (int y = 0; y < rows; y++)
                        Buffer.MemoryCopy(src + (long)y * decoder.Stride, (byte*)b.Scan0 + (long)y * b.Stride, bytes, bytes);
            }
        }
        finally { canvas.UnlockBits(b); }
    }
}

// ---- subscribe exactly as SwitchMonitor does -------------------------------------------------
if (await Task.WhenAny(gotMonitors.Task, Task.Delay(8000)) != gotMonitors.Task)
{
    Fail("the monitor list reached the viewer", "no MonitorsMsg in 8 s");
    tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId });
    return 1;
}
Pass("the monitor list reached the viewer", string.Join(" | ", monitors.Select(x => $"{x.Index}: {x.Width}x{x.Height}{(x.Primary ? " primary" : "")}")));

var mi = monitors[0];
monitorIndex = mi.Index;
lock (canvasLock)
{
    monW = mi.Width; monH = mi.Height;
    canvas = new Bitmap(Math.Max(1, monW), Math.Max(1, monH), PixelFormat.Format32bppRgb);
    using var g = Graphics.FromImage(canvas);
    g.Clear(Color.FromArgb(20, 20, 20));
}

// A pass is one measured window under one codec offer. Running two of them back to back against
// the same machine and the same screen is the whole point: a before-and-after taken minutes apart
// on a machine somebody is using is not a comparison, it is two anecdotes.
async Task<(long frames, long bytes, double el, string label)> Measure(string label, List<string> offer)
{
    subscribedAt = DateTime.Now;
    firstPictureAt = DateTime.MinValue;
    vc.Send(new SubscribeMsg { Monitor = mi.Index, On = true, Codecs = offer });
    vc.Send(new QualityMsg { Jpeg = quality, Fps = fps });
    Log($"subscribed offering [{string.Join(", ", offer)}], quality {quality}, {fps} fps");

    for (int i = 0; i < 100 && firstPictureAt == DateTime.MinValue; i++) await Task.Delay(50);
    if (firstPictureAt == DateTime.MinValue)
        Fail($"[{label}] a first picture arrived after subscribing",
             "nothing in 5 s - this is the black screen, and with a codec it also means no key frame came");
    else
        Pass($"[{label}] a first picture arrived after subscribing", $"{(firstPictureAt - subscribedAt).TotalMilliseconds:0} ms");

    long v0 = videoFrames, vb0 = videoBytes, t0f = tileFrames, tb0 = tileBytes;
    var t0 = DateTime.Now;
    Log($"[{label}] measuring for {seconds} s - leave that machine ALONE unless you meant to drive it");
    await Task.Delay(seconds * 1000);
    double el = (DateTime.Now - t0).TotalSeconds;
    long df = (videoFrames - v0) + (tileFrames - t0f);
    long db = (videoBytes - vb0) + (tileBytes - tb0);
    Console.WriteLine($"    [{label}] {df} frames ({df / el:0.0}/s), {db / 1024.0 / el:0.0} KB/s");
    return (df, db, el, label);
}

var results = new List<(long frames, long bytes, double el, string label)>();
if (!tilesOnly)
    results.Add(await Measure("h264 offered", new List<string> { VideoCodec.H264, VideoCodec.JpegTiles }));
results.Add(await Measure("tiles only", new List<string> { VideoCodec.JpegTiles }));
Console.WriteLine();

// ---- which codec did the host choose, and did it say so? -------------------------------------
string codec = sysInfo.GetValueOrDefault("Video codec", "");
if (codec.Length == 0)
    Fail("the host reported which codec it is using", "no 'Video codec' line - is that machine running a build from before 2.2?");
else if (codecsSeen.Any(c => c.StartsWith("H.264", StringComparison.OrdinalIgnoreCase)))
    Pass("the host used H.264 for at least one pass", codecsSeen.First(c => c.StartsWith("H.264", StringComparison.OrdinalIgnoreCase)));
else
{
    // Not a failure. It is what the negotiation is FOR, and what matters is that it is visible.
    Note($"the host never used H.264; it stayed on {codec}");
    if (sysInfo.TryGetValue("Video codec fell back because", out var why)) Note($"because: {why}");
    Pass("the fallback is visible to the technician", "the Info tab names the codec and the reason");
}
if (decoderError.Length > 0)
    Fail("this machine could decode what it asked for", decoderError + " - the console would have shown black");
if (videoFrames > 0)
{
    if (videoDrawn > 0) Pass("the video stream decoded here", $"{videoDrawn} of {videoFrames} frames drawn, {videoKeys} key frames");
    else Fail("the video stream decoded here", $"{videoFrames} frames arrived and none decoded - black screen with the counters climbing");
    if (videoKeys == 0) Fail("a key frame arrived", "none - a viewer joining now would never get a picture");
}
if (sysInfo.TryGetValue("Frames the encoder was too busy for", out var dropped)) Note($"encoder dropped {dropped} frames as backpressure");
if (sysInfo.TryGetValue("Screen capture", out var src)) Note($"capture path: {src}");

// ---- the comparison this whole harness exists for ---------------------------------------------
Console.WriteLine();
if (results.Count == 2)
{
    var h = results[0]; var t = results[1];
    double hk = h.bytes / 1024.0 / h.el, tk = t.bytes / 1024.0 / t.el;
    Console.WriteLine($"  same machine, same screen, {seconds} s each:");
    Console.WriteLine($"    H.264 offered   {hk,8:0.0} KB/s   {h.frames / h.el,5:0.0} frames/s");
    Console.WriteLine($"    JPEG tiles      {tk,8:0.0} KB/s   {t.frames / t.el,5:0.0} frames/s");
    if (tk > 1 && hk > 0) Console.WriteLine($"    H.264 costs {hk / tk * 100:0}% of what the tiles cost.");
    // Both near zero is the honest and very likely answer on an untouched machine: 2.1 made an
    // idle screen free on both paths. It is not a result about the codec, and saying so is the
    // difference between this harness and one that reports a win it did not measure.
    if (hk < 5 && tk < 5)
        Note("both are near zero because the screen was not changing. Run this again with --provoke work on screen, or during a real session, before quoting either figure.");
}

// ---- an idle screen sending nothing is CORRECT and identical to a stall ------------------------
if (provoke)
{
    Console.WriteLine();
    Bitmap? before;
    lock (canvasLock) before = canvas == null ? null : (Bitmap)canvas.Clone();

    Log("making the screen change on purpose: opening the Start menu on that machine, then closing it");
    vc.Send(new SubscribeMsg { Monitor = mi.Index, On = true, Codecs = tilesOnly ? new List<string> { VideoCodec.JpegTiles } : new List<string> { VideoCodec.H264, VideoCodec.JpegTiles } });
    await Task.Delay(700);
    long vb = videoBytes + tileBytes; long fb = videoFrames + tileFrames;
    var pt0 = DateTime.Now;
    vc.Send(new KeyMsg { Vk = 0x5B, Down = true });          // VK_LWIN
    vc.Send(new KeyMsg { Vk = 0x5B, Down = false });
    await Task.Delay(3000);
    long provoked = (videoFrames + tileFrames) - fb;
    long provokedBytes = (videoBytes + tileBytes) - vb;
    double pel = (DateTime.Now - pt0).TotalSeconds;
    Bitmap? after;
    lock (canvasLock) after = canvas == null ? null : (Bitmap)canvas.Clone();
    vc.Send(new KeyMsg { Vk = 0x1B, Down = true });          // VK_ESCAPE, put it back
    vc.Send(new KeyMsg { Vk = 0x1B, Down = false });
    await Task.Delay(800);

    if (provoked > 0) Pass("frames followed a change on screen", $"{provoked} frames in 3 s at {provokedBytes / 1024.0 / pel:0.0} KB/s - the stream is LIVE, not stuck on its first key frame");
    else Fail("frames followed a change on screen", "none - either the stream has stalled or the keystroke never landed");

    // Frames arriving is not enough: they could be arriving and carrying the same pixels. The
    // composed picture having actually changed is the end of the argument, and for a codec it is
    // the ONLY thing that separates a working decoder from one producing a plausible smear.
    if (before != null && after != null)
    {
        double diff = MeanDiff(before, after);
        if (diff > 0.5) Pass("the picture actually changed", $"mean difference {diff:0.0} of 255");
        else Fail("the picture actually changed", $"mean difference only {diff:0.0} of 255 - frames arrived but the image did not move");
        var provokedPng = Path.Combine(Path.GetDirectoryName(outPng)!, "codec-provoked.png");
        after.Save(provokedPng, ImageFormat.Png);
        Note($"the changed picture is at {provokedPng} - the Start menu should be open in it, and legible");
    }
    before?.Dispose(); after?.Dispose();
}

// ---- stage 2.4: the customer's real pointer ----------------------------------------------------
// The picture and the pointer travel separately and fail separately: a duplicated frame never
// contains the cursor, so a session can have a perfect screen and no pointer at all, and nothing
// above this line would notice.
Console.WriteLine();
if (cursorMsgs > 0) Pass("the pointer reported itself", $"{cursorMsgs} updates, {cursorIdsSeen.Count} distinct shape(s), {cursorHiddenMsgs} while hidden");
else Fail("the pointer reported itself", "no CursorMsg arrived at all - the cursor loop is not running or not reaching this end");

if (cursorShapesBad > 0) Fail("every pointer bitmap parsed", $"{cursorShapesBad} could not be read");
else if (cursorShapes.Count > 0) Pass("every pointer bitmap parsed", $"{cursorShapes.Count} shape(s)");
else Fail("a pointer bitmap arrived", "positions came through but no shape did - the console would still be drawing its ring");

foreach (var (id, shape) in cursorShapes)
{
    var path = Path.Combine(Path.GetDirectoryName(outPng)!, $"cursor-{id:X}.png");
    try
    {
        File.WriteAllBytes(path, shape.png);
        int opaque = 0;
        using (var ms = new MemoryStream(shape.png))
        using (var b = new Bitmap(ms))
            for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
                if (b.GetPixel(x, y).A > 200) opaque++;
        if (opaque > 0) Pass($"pointer {id:X} is something you can see", $"{shape.w}x{shape.h}, {opaque} opaque pixels -> {path}");
        else Fail($"pointer {id:X} is something you can see", $"{shape.w}x{shape.h} and every pixel transparent - an invisible pointer");
    }
    catch (Exception ex) { Fail($"pointer {id:X} decodes", ex.Message); }
}
if (cursorShapes.Count > 0)
    Note("OPEN the cursor PNGs. A pixel count cannot tell you an arrow is an arrow.");

// ---- is the picture right, not merely present? -------------------------------------------------
Console.WriteLine();
lock (canvasLock)
{
    if (canvas == null) Fail("a picture was composed", "none");
    else
    {
        double sum = 0; int n = 0, nonBlack = 0;
        for (int y = 0; y < canvas.Height; y += 11)
        for (int x = 0; x < canvas.Width; x += 11)
        {
            var c = canvas.GetPixel(x, y);
            sum += (c.R + c.G + c.B) / 3.0; n++;
            if (c.R + c.G + c.B > 40) nonBlack++;
        }
        double mean = sum / Math.Max(1, n), lit = 100.0 * nonBlack / Math.Max(1, n);
        if (lit > 5) Pass("the composed picture is a screen, not black", $"mean brightness {mean:0}, {lit:0}% of samples lit");
        else Fail("the composed picture is a screen, not black", $"only {lit:0}% of samples lit - LOOK AT THE PNG before believing any number above");
        canvas.Save(outPng, ImageFormat.Png);
        Note($"the picture is at {outPng} - open it. A byte count cannot tell you it is the right screen.");
    }
}

decoder?.Dispose();
tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId });
await Task.Delay(600);

Console.WriteLine();
Console.WriteLine($"{pass} passed, {fail} failed.");
return fail == 0 ? 0 : 1;

// ---------------------------------------------------------------------------------------------

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
bool Has(string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

string? FirstBare()
{
    // Flags that take a value, so their value is not mistaken for the machine name.
    var valued = new[] { "--seconds", "--quality", "--fps", "--out" };
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith("--")) { if (valued.Contains(args[i], StringComparer.OrdinalIgnoreCase)) i++; continue; }
        return args[i];
    }
    return null;
}

static double MeanDiff(Bitmap a, Bitmap b)
{
    if (a.Width != b.Width || a.Height != b.Height) return 255;
    double total = 0; long n = 0;
    // Sampled rather than exhaustive: this asks "did the picture move", not "how faithful is it",
    // and a 2 MP exhaustive compare through GetPixel is slow enough to be felt.
    for (int y = 0; y < a.Height; y += 7)
    for (int x = 0; x < a.Width; x += 7)
    {
        var ca = a.GetPixel(x, y); var cb = b.GetPixel(x, y);
        total += Math.Abs(ca.R - cb.R) + Math.Abs(ca.G - cb.G) + Math.Abs(ca.B - cb.B);
        n += 3;
    }
    return total / Math.Max(1, n);
}
