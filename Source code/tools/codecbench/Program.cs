using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CodecBench;
using RemoteSupport.Host;

// ---------------------------------------------------------------------------------------------
// What does the screen pipeline actually cost? Answered on THIS machine alone -- no server, no
// session, no customer machine. It is the "before" and "after" the roadmap's stage 2 insists on:
//
//   "This stage is exactly the kind of work that feels better without being better, and a number
//    is the only defence against that."
//
// It drives the REAL capture path out of RemoteSupport.Host, against a scene it paints itself so
// that two runs a week apart are looking at the same screen. Three conditions -- a static page,
// the same page scrolling, and a moving field standing in for video -- and two passes each:
//
//   capped    at the product's own 12 fps, which is what a customer's uplink actually carries
//   uncapped  as fast as the pipeline will go, which is the ceiling the 12 is sitting under
//
// Both matter and they answer different questions. Bytes per second is only meaningful at the
// rate the product really sends; frames per second is only meaningful with the cap off.
//
//   codecbench [--source gdi] [--seconds N] [--quality Q] [--fps N]
//              [--condition idle|scroll|video|all] [--live] [--monitor N]
//
// --live drops the painted scene and measures whatever is genuinely on the monitor. Use it once,
// with a real film playing, to check the synthetic video condition is not flattering us.
// ---------------------------------------------------------------------------------------------

int seconds = Arg("--seconds", 6, 2, 120);
int quality = Arg("--quality", 55, 20, 95);
int capFps = Arg("--fps", 12, 1, 60);
int monitor = Arg("--monitor", -1, -1, 16);
bool live = Has("--live");
string sourceArg = ArgStr("--source", "gdi");
string condArg = ArgStr("--condition", "all");

Console.WriteLine("codecbench - what the screen pipeline costs, on this machine alone.");
Console.WriteLine();

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

var sources = ResolveSources(sourceArg);
// h264 is a source of the same measurement but not of the same interface -- see Video.cs for why
// it does not and should not implement IScreenSource.
bool wantH264 = sourceArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Any(w => w.Equals("h264", StringComparison.OrdinalIgnoreCase) || w.Equals("all", StringComparison.OrdinalIgnoreCase));
if (sources.Count == 0 && !wantH264) { Console.WriteLine($"No capture source matched {sourceArg}. Known: gdi, dxgi, h264, all."); return 1; }

// Pick the monitor first, with the same code the host uses, so the bench and the product agree
// about which screen index means what.
using (var probe = new ScreenCapture())
{
    probe.Refresh();
    if (monitor < 0) monitor = probe.Monitors.FirstOrDefault(m => m.Primary)?.Index ?? 0;
    var m = probe.Monitors.FirstOrDefault(x => x.Index == monitor);
    if (m == null) { Console.WriteLine($"No monitor with index {monitor}. Found: {string.Join(", ", probe.Monitors.Select(x => x.Index))}"); return 1; }
    Console.WriteLine($"monitor {m.Index}: {m.Width}x{m.Height} at {m.X},{m.Y}{(m.Primary ? " (primary)" : "")}  {m.Name}");
    Program.SceneBounds = new Rectangle(m.X, m.Y, m.Width, m.Height);
    Program.Pixels = (long)m.Width * m.Height;
}
Console.WriteLine($"JPEG quality {quality}, cap {capFps} fps, {seconds} s per pass.");
Console.WriteLine();

// --compare answers the other half of the question, and it is the half that matters first: a
// fast picture that is wrong is not an improvement.
if (Has("--idlewatch"))
    return IdleWatch.Run(monitor, quality, Program.SceneBounds, !live, Has("--warmgdi"));

if (Has("--compare"))
    return Compare.Run(monitor, quality, Program.SceneBounds,
                       ArgStr("--out", Path.Combine(Path.GetTempPath(), "codecbench")));

// Stage 2.3's floor: what the send queue does to a delta stream when the link cannot carry it.
// Run this after touching WsConnection or either capture path's change detection.
if (Has("--stale"))
    return Stale.Run(monitor, quality, Program.SceneBounds, sourceArg);

// Stage 2.3's controller, against scripted link conditions. Needs nothing at all — no screen, no
// socket, no machine — so run it after touching LinkController and it answers in a second.
if (Has("--adapt"))
    return Adapt.Run();

// Stage 2.4: the customer's real pointer. Renders the standard cursors, checks the two ways a
// pointer can be silently wrong, and writes the PNGs out to be looked at.
if (Has("--cursor"))
    return CursorCheck.Run(ArgStr("--out", Path.Combine(Path.GetTempPath(), "codecbench-cursor")));

// The same question for stage 2.2, and the one to run FIRST after touching anything in
// RemoteSupport.Media: a codec can produce a stream that arrives, decodes and is not the
// customer's screen, and no byte count will ever say so.
// What the machine has, unfiltered. The first thing to run when the codec says it cannot find an
// encoder, because "there is none" and "we asked wrongly" are the same sentence otherwise.
if (Has("--mfts"))
{
    string lastFilter = "";
    foreach (var line in RemoteSupport.Media.MediaInventory.Take())
    {
        string k = line.Category + " | " + line.Filter;
        if (k != lastFilter) { Console.WriteLine(); Console.WriteLine($"{line.Category}  ({line.Filter})"); lastFilter = k; }
        Console.WriteLine($"    {line.Name}");
    }
    return 0;
}

if (Has("--h264check"))
    return Video.Check(monitor, quality, Program.SceneBounds,
                       ArgStr("--out", Path.Combine(Path.GetTempPath(), "codecbench")),
                       ArgStr("--encoder", ""));

// The last frame of a burst, then a still screen — the "only shows after Refresh" report. See Tail.
if (Has("--nv12")) RemoteSupport.Media.H264Encoder.ForceColourConversion = true;
if (Has("--gop")) RemoteSupport.Media.H264Encoder.KeyFrameSeconds = Arg("--gop", 2, 1, 600);
if (Has("--tail"))
    return Tail.Run(monitor, quality, Program.SceneBounds,
                    ArgStr("--out", Path.Combine(Path.GetTempPath(), "codecbench")),
                    ArgStr("--encoder", ""), Arg("--hold", 0, 0, 5));

// What Desktop Duplication says changed, on the real desktop, for StillRefiner (HANDOVER §142).
if (Has("--sharetest"))
{
    using var dx = new DxgiCapture(); dx.Refresh();
    dx.TryAcquireGpuFrame(monitor, true, out _);
    var until = DateTime.UtcNow.AddSeconds(Arg("--seconds", 6, 2, 60));
    while (DateTime.UtcNow < until)
    {
        if (dx.TryAcquireGpuFrame(monitor, false, out _)) Console.WriteLine($"  changed {dx.LastVideoChange:0.00000}  {dx.ShareWhy}");
        Thread.Sleep(83);
    }
    return 0;
}

// The host's own H.264 loop over a twenty-second session, old host against new. See HostLoop.
if (Has("--hostloop"))
    return HostLoop.Run(monitor, quality, Program.SceneBounds,
                        ArgStr("--out", Path.Combine(Path.GetTempPath(), "codecbench-hostloop")), ArgStr("--encoder", ""));

// How close the console's picture gets to the screen, and how fast. See Sharp.
if (Has("--sharp"))
    return Sharp.Run(monitor, quality, Program.SceneBounds,
                     ArgStr("--out", Path.Combine(Path.GetTempPath(), "codecbench-sharp")),
                     ArgStr("--encoder", ""),
                     ArgStr("--scene", "desk").ToLowerInvariant() switch { "colour" => SceneKind.Colour, "idle" => SceneKind.Idle, _ => SceneKind.Desk },
                     Has("--twokeys"));

// Everything the encoder stands on, on a machine that has no encoder. See Video.PipeCheck.
if (Has("--mfpipe"))
    return Video.PipeCheck(monitor, Program.SceneBounds,
                           ArgStr("--out", Path.Combine(Path.GetTempPath(), "codecbench")));

var conditions = condArg.Equals("all", StringComparison.OrdinalIgnoreCase)
    ? new[] { SceneKind.Idle, SceneKind.Scroll, SceneKind.Video }
    : new[] { Enum.Parse<SceneKind>(condArg, true) };

var rows = new List<Program.Row>();
// ONE scene window for the whole run, repainted between conditions rather than closed and
// reopened. See Scene.SetKind: a second window cannot reliably be put in front of an
// always-on-top application, and a bench measuring the wrong screen is the failure that looks
// most like a result.
var scene = live ? null : SceneHost.Show(conditions[0], Program.SceneBounds);
try
{
    foreach (var (name, make) in sources)
    {
        foreach (var cond in conditions)
        {
            scene?.SetKind(cond, Program.SceneBounds);
            Thread.Sleep(900);                       // let the repaint and the compositor settle
            IScreenSource src;
            // A source that cannot run on this machine is a result, not a crash: it is exactly
            // what the fallback exists for, and the report should say so and carry on.
            try { src = make(); }
            catch (Exception ex)
            {
                Console.WriteLine($"  {name}: unavailable - {ex.Message}");
                rows.Add(new Program.Row { Source = name, Condition = cond.ToString().ToLowerInvariant(), Error = ex.Message });
                continue;
            }
            using var _ = src;
            src.Refresh();
            // A row measured against the wrong screen is worse than a missing row, because it
            // looks like a result. Carry the doubt through to the table rather than printing a
            // warning forty lines earlier and hoping it is remembered.
            bool trust = scene == null || scene.Covering;
            var a = Program.Pass(src, name, cond, live, monitor, quality, true, capFps, seconds);
            var b = Program.Pass(src, name, cond, live, monitor, quality, false, capFps, seconds);
            a.SceneVerified = b.SceneVerified = trust;
            rows.Add(a); rows.Add(b);
        }
    }

    if (wantH264)
    {
        foreach (var cond in conditions)
        {
            scene?.SetKind(cond, Program.SceneBounds);
            Thread.Sleep(900);
            bool trust = scene == null || scene.Covering;
            var a = Video.Pass(cond, live, monitor, quality, true, capFps, seconds);
            var b = Video.Pass(cond, live, monitor, quality, false, capFps, seconds);
            a.SceneVerified = b.SceneVerified = trust;
            rows.Add(a); rows.Add(b);
        }
    }
}
finally { scene?.Dispose(); }

Program.Report(rows, quality, capFps, seconds, live);
return 0;

// ---------------------------------------------------------------------------------------------

static List<(string, Func<IScreenSource>)> ResolveSources(string spec)
{
    var want = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var all = new List<(string, Func<IScreenSource>)>
    {
        ("gdi", () => new ScreenCapture()),
        ("dxgi", () => new DxgiCapture()),
    };
    if (want.Any(w => w.Equals("all", StringComparison.OrdinalIgnoreCase))) return all;
    return all.Where(a => want.Any(w => w.Equals(a.Item1, StringComparison.OrdinalIgnoreCase))).ToList();
}

int Arg(string name, int dflt, int lo, int hi)
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

partial class Program
{
    internal static Rectangle SceneBounds;
    internal static long Pixels;

    [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] internal static extern bool GetThreadTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

    internal sealed class Row
    {
        public string Source = "", Condition = "", Error = "";
        public bool Capped; public int CapFps;
        public int Frames;       // polls that carried at least one tile -- what the technician sees
        public int Polls;        // times the capturer was asked, whether or not anything came back
        public int TilesChanged, TilesTotal;
        public long Bytes, FirstFrameBytes;
        public double Elapsed, GrabMs, DiffMs, EncodeMs, FrameMs, CpuMs, FirstFrameMs;
        public bool SceneVerified = true;   // was the scene proven to be the window on screen?
        /// <summary>Anything about this row the columns cannot hold — which encoder ran, what it dropped.</summary>
        public string Note = "";
    }

    internal static Row Pass(IScreenSource src, string sourceName, SceneKind cond, bool live, int monitor,
                             int quality, bool capped, int capFps, int seconds)
    {
        var r = new Row { Source = sourceName, Condition = live ? "live" : cond.ToString().ToLowerInvariant(), Capped = capped, CapFps = capFps };
        var done = new ManualResetEventSlim();

        // A dedicated thread, for the same reason HostEngine uses one: desktop attachment is a
        // per-thread property and a pool thread would lose it on the first continuation. Here it
        // also means GetThreadTimes measures the capture and nothing else -- the scene is
        // painting hard on another thread and would otherwise be charged to the codec.
        var t = new Thread(() =>
        {
            IntPtr me = GetCurrentThread();
            // A warm-up that is measured and thrown away. Two separate things make the opening
            // second unrepresentative: the scene window is still settling, and Desktop
            // Duplication's first real frame after the buffer was primed from GDI differs from it
            // almost everywhere -- the two capture routes do not produce bit-identical pixels, so
            // one whole frame looks as though it changed. Counting that put 88 KB/s against an
            // idle screen that was in fact sending nothing at all.
            double warmup = Math.Min(1.0, seconds / 4.0);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool first = true, counting = false;
            long k0 = 0, u0 = 0;
            double startedAt = 0;

            while (true)
            {
                double now = sw.Elapsed.TotalSeconds;
                if (!counting && now >= warmup) { counting = true; startedAt = now; GetThreadTimes(me, out _, out _, out k0, out u0); }
                if (counting && now - startedAt >= seconds) break;

                long frameStart = System.Diagnostics.Stopwatch.GetTimestamp();
                List<(int x, int y, int w, int h, byte[] jpeg)> tiles;
                try { tiles = src.CaptureTiles(monitor, quality, first); }
                catch (Exception ex) { r.Error = $"{ex.GetType().Name}: {ex.Message}"; break; }

                var tm = src.LastTiming;
                // The first frame is forced full and is not representative of anything; it is
                // counted separately so the report can show what a fresh subscribe really costs.
                if (first) { r.FirstFrameBytes = Wire(tiles); r.FirstFrameMs = Ms(frameStart); first = false; }
                else if (counting)
                {
                    r.Polls++;
                    r.Bytes += Wire(tiles);
                    r.TilesChanged += tm.TilesChanged;
                    r.GrabMs += tm.GrabMs; r.DiffMs += tm.DiffMs; r.EncodeMs += tm.EncodeMs;
                    r.FrameMs += Ms(frameStart);
                    // A poll that produced no tiles is not a frame. Desktop Duplication returns
                    // from an unchanged screen in microseconds, so counting those would report
                    // thousands of frames a second at an idle desktop -- true, and useless.
                    if (tiles.Count > 0) r.Frames++;
                }
                r.TilesTotal = tm.TilesTotal;

                if (capped)
                {
                    int wait = 1000 / capFps - (int)Ms(frameStart);
                    if (wait > 0) Thread.Sleep(wait);
                }
            }
            r.Elapsed = sw.Elapsed.TotalSeconds - startedAt;
            GetThreadTimes(me, out _, out _, out long k1, out long u1);
            r.CpuMs = ((k1 - k0) + (u1 - u0)) / 10000.0;   // 100 ns ticks
            done.Set();
        });
        t.IsBackground = true;
        t.Start();
        done.Wait();
        t.Join(2000);
        return r;
    }

    private static long Wire(List<(int x, int y, int w, int h, byte[] jpeg)> tiles)
    {
        // Exactly what HostEngine.EncodeTiles puts on the socket: a 9-byte frame header and 20
        // bytes of rectangle per tile. At 128 px tiles that framing is not noise -- a frame of
        // 300 small tiles carries 6 KB of headers before a single pixel.
        if (tiles.Count == 0) return 0;
        long n = 1 + 4 + 4;
        foreach (var t in tiles) n += 20 + t.jpeg.Length;
        return n;
    }

    internal static double Ms(long since) => (System.Diagnostics.Stopwatch.GetTimestamp() - since) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    internal static void Report(List<Row> rows, int quality, int capFps, int seconds, bool live)
    {
        Console.WriteLine();
        Console.WriteLine("| source | condition | pass | fps | KB/s | tiles/frame | ms/poll (grab/diff/jpeg) | CPU |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|");
        foreach (var r in rows)
        {
            if (r.Error.Length > 0) { Console.WriteLine($"| {r.Source} | {r.Condition} | {(r.Capped ? "capped" : "uncapped")} | unavailable: {r.Error} |"); continue; }
            double el = Math.Max(0.001, r.Elapsed);
            double p = Math.Max(1, r.Polls);
            double f = Math.Max(1, r.Frames);
            // An H.264 row has no tiles to count. Saying so beats printing "0.0 of 0", which reads
            // as a measurement of nothing rather than as a column that does not apply.
            string tiles = r.TilesTotal > 0 ? $"{r.TilesChanged / f,5:0.0} of {r.TilesTotal}" : "  whole frame";
            Console.WriteLine($"| {r.Source} | {r.Condition} | {(r.Capped ? $"capped {r.CapFps}" : "uncapped")} " +
                              $"| {r.Frames / el,5:0.0} | {r.Bytes / 1024.0 / el,7:0.0} | {tiles} " +
                              $"| {r.FrameMs / p,5:0.0}  ({r.GrabMs / p:0.0}/{r.DiffMs / p:0.0}/{r.EncodeMs / p:0.0}) " +
                              $"| {r.CpuMs / el / 10.0,4:0.0}% |{(r.SceneVerified ? "" : "  <- NOT the scene; measured the real desktop")}");
        }
        foreach (var r in rows.Where(r => r.Note.Length > 0).DistinctBy(r => r.Source + r.Note))
            Console.WriteLine($"{r.Source}: {r.Note}");
        Console.WriteLine();
        Console.WriteLine($"JPEG quality {quality}; {seconds} s per pass after a warm-up that is discarded; screen {Pixels / 1_000_000.0:0.0} MP.");
        Console.WriteLine("fps counts only polls that carried pixels, so an idle screen correctly reads 0.");
        Console.WriteLine("The uncapped CPU figure for a still screen is an artefact of this bench and not of the");
        Console.WriteLine("product: with no cap it asks for a frame as fast as it can and Desktop Duplication says");
        Console.WriteLine("no in microseconds. The host's capture loop always sleeps to the frame budget.");
        foreach (var r in rows.Where(r => r.FirstFrameBytes > 0 && r.Capped && r.Condition != "scroll" && r.Condition != "video"))
            Console.WriteLine($"{r.Source}: a forced-full frame -- what every subscribe and every desktop switch costs -- was {r.FirstFrameBytes / 1024.0:0} KB in {r.FirstFrameMs:0} ms.");
        foreach (var r in rows.Where(r => r.Condition == "idle" && r.Capped && r.Error.Length == 0))
            Console.WriteLine($"{r.Source}: idle sent something on {r.Frames} of {r.Polls} polls. A still screen should send nothing at all.");
        if (!live)
        {
            Console.WriteLine();
            Console.WriteLine("The 'video' condition is a moving field with noise, NOT a film: reproducible on purpose,");
            Console.WriteLine("harsher than scrolling text and cheaper than real footage. Run --live against a real");
            Console.WriteLine("video once before trusting it as an upper bound.");
        }
    }
}
