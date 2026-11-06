using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// Black-screen harness (docs/HANDOVER.md 6b).
//
// Connects as a real session viewer and then reproduces the technician console's receive and
// render path as SessionForm/ScreenView do it:
//   * it does NOT subscribe on its own — it waits for MonitorsMsg and subscribes the way
//     SwitchMonitor does, so "no MonitorsMsg" shows up here as "no tiles";
//   * it applies tiles with the ApplyTiles logic, including the monitor-index test;
//   * it writes the composed canvas out as a PNG, so we can look at what the console would
//     have shown rather than at a byte count.
// ---------------------------------------------------------------------------------------------

string http = "http://localhost:8090", ws = "ws://localhost:8090";
string want = args.Length > 0 ? args[0] : "DESKTOP-L2B85US";
string outPng = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "canvas.png");
int watchMs = 8000;

void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {s}");

using var hc = new HttpClient();
var creds = RemoteSupport.Tools.RsCredentials.Load();
var lr = await hc.PostAsJsonAsync($"{http}/api/login", new { email = creds.Email, password = creds.Password, code = creds.Code() });
var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
var tech = new ClientWebSocket(); await tech.ConnectAsync(new Uri($"{ws}/ws/tech?token={tok}"), default);
var tc = new WsConnection(tech); var tq = new ConcurrentQueue<Msg>();
tc.OnMessage += m => tq.Enqueue(m); _ = tc.RunAsync();

async Task<T?> W<T>(ConcurrentQueue<Msg> q, int ms, Func<T, bool>? f = null) where T : Msg
{ var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { foreach (var m in q) if (m is T t && (f == null || f(t))) return t; await Task.Delay(50); } return null; }

var st = await W<StateMsg>(tq, 8000);
var dev = st?.Devices.FirstOrDefault(d => d.MachineName.Equals(want, StringComparison.OrdinalIgnoreCase));
if (dev == null) { Log($"device {want} not found"); return; }
Log($"device {dev.MachineName} ({dev.DeviceId}) online={dev.Online}");

tc.Send(new ConnectDeviceMsg { DeviceId = dev.DeviceId, Reason = "blackscreen diagnostic" });
var ready = await W<SessionReadyMsg>(tq, 20000, s => s.Session.DeviceId == dev.DeviceId);
if (ready == null) { Log("no session"); return; }
Log($"session {ready.Session.SessionId} ready");

// ---- viewer socket, wired like the session window ---------------------------------------
var canvasLock = new object();
Bitmap? canvas = null;
int monitorIndex = 0;                      // ScreenView.MonitorIndex
int monW = 0, monH = 0;
long frames = 0, applied = 0, tilesDrawn = 0, decodeFail = 0, rejMonitor = 0, rejNoCanvas = 0;
var rejectedMonitors = new ConcurrentDictionary<int, long>();
List<MonitorInfo> monitors = new();
var gotMonitors = new TaskCompletionSource();
var msgLog = new ConcurrentBag<string>();

var v = new ClientWebSocket();
await v.ConnectAsync(new Uri($"{ws}/ws/session?role=viewer&id={ready.Session.SessionId}&token={tok}"), default);
var vc = new WsConnection(v);

vc.OnMessage += m =>
{
    msgLog.Add(m.GetType().Name);
    if (m is MonitorsMsg mm)
    {
        monitors = mm.Monitors;
        Log("MonitorsMsg: " + string.Join(" | ", monitors.Select(x => $"Index={x.Index} {x.Width}x{x.Height} at {x.X},{x.Y}{(x.Primary ? " primary" : "")}")));
        gotMonitors.TrySetResult();
    }
};

// ScreenView.ApplyTiles, same behaviour.
vc.OnBinary += frame =>
{
    var s = frame.Span;
    if (s.Length < 1 || s[0] != BinKind.Tiles) return;
    frames++;
    if (s.Length < 9) return;
    int monitor = BitConverter.ToInt32(s.Slice(1, 4));
    if (monitor != monitorIndex) { rejMonitor++; rejectedMonitors.AddOrUpdate(monitor, 1, (_, c) => c + 1); return; }
    int count = BitConverter.ToInt32(s.Slice(5, 4));
    int o = 9, drawn = 0;
    lock (canvasLock)
    {
        if (canvas == null) { rejNoCanvas++; return; }
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
                drawn++;
            }
            catch { decodeFail++; }
            o += len;
        }
    }
    applied++; tilesDrawn += drawn;
    if (applied <= 3) Log($"applied frame #{applied}: monitor={monitor} tiles={count} drawn={drawn}");
};
_ = vc.RunAsync();
Log("viewer socket connected — NOT subscribing; waiting for MonitorsMsg the way the console does");

// Does the replayed MonitorsMsg actually turn up on the viewer socket?
var raced = await Task.WhenAny(gotMonitors.Task, Task.Delay(6000));
if (raced != gotMonitors.Task)
{
    Log("*** no MonitorsMsg within 6 s — the console would never subscribe, and would stay black ***");
}
else
{
    // exactly what SwitchMonitor does for the first (slot 0) monitor
    var mi = monitors[0];
    monitorIndex = mi.Index;
    lock (canvasLock)
    {
        monW = mi.Width; monH = mi.Height;
        canvas = new Bitmap(Math.Max(1, monW), Math.Max(1, monH), PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(canvas);
        g.Clear(Color.FromArgb(20, 20, 20));           // ScreenView.Reset
    }
    Log($"SwitchMonitor: MonitorIndex={monitorIndex}, canvas {monW}x{monH}; sending SubscribeMsg(Monitor={mi.Index}, On=true)");
    vc.Send(new SubscribeMsg { Monitor = mi.Index, On = true });
    vc.Send(new QualityMsg { Jpeg = 55, Fps = 12 });
}

await Task.Delay(watchMs);

// ---- verdict ----------------------------------------------------------------------------
Log("");
Log("messages on viewer socket : " + string.Join(", ", msgLog.GroupBy(x => x).Select(g => $"{g.Key}x{g.Count()}")));
Log($"tile frames received      : {frames}");
Log($"  applied                 : {applied}  (tiles drawn {tilesDrawn}, decode failures {decodeFail})");
Log($"  rejected, wrong monitor : {rejMonitor}" + (rejectedMonitors.IsEmpty ? "" : "  headers said: " + string.Join(", ", rejectedMonitors.Select(k => $"{k.Key}x{k.Value}")) + $"  (view wanted {monitorIndex})"));
Log($"  rejected, no canvas     : {rejNoCanvas}");

lock (canvasLock)
{
    if (canvas == null) { Log("no canvas was ever created — nothing to save"); }
    else
    {
        // Is the composed image actually black? Sample a grid and report mean luminance.
        double sum = 0; int n = 0, nonBlack = 0;
        for (int y = 0; y < canvas.Height; y += 17)
        for (int x = 0; x < canvas.Width; x += 17)
        {
            var c = canvas.GetPixel(x, y);
            double l = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
            sum += l; n++; if (l > 28) nonBlack++;
        }
        Log($"canvas {canvas.Width}x{canvas.Height}: mean luminance {sum / Math.Max(1, n):0.0}/255, {100.0 * nonBlack / Math.Max(1, n):0.0}% of sampled pixels brighter than the Reset fill");
        canvas.Save(outPng, ImageFormat.Png);
        Log($"canvas written to {outPng}");
    }
}

tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId });
await Task.Delay(600);
