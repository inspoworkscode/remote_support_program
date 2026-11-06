using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Windows.Forms;
using RemoteSupport.Host;
using RemoteSupport.Shared;

namespace CodecBench;

/// <summary>
/// What happens to the picture when the link cannot carry it — stage 2.3's floor.
///
/// This is the harness the adaptive-quality brief asks for, and it exists because of a fault that
/// was found by reading rather than by anyone reporting it. `WsConnection`'s send queue used to
/// evict the OLDEST item when it filled. Screen frames are deltas, and both capture paths record
/// what the far end holds at the moment of CAPTURE, not at the moment of delivery — so an evicted
/// tile was marked delivered, the next pass saw nothing new to send, and that rectangle stayed
/// wrong on the technician's screen until something else happened to change it.
///
/// It needs no server, no session, no customer machine: a real WebSocket to a loopback listener
/// that is told when to read, a real <see cref="WsConnection"/>, and the real capture path out of
/// RemoteSupport.Host against a scene it paints itself.
///
///   codecbench --stale [--source gdi|dxgi]
///
/// ---- The question every check here has to survive -------------------------------------------
///
/// "What would this print if the thing it tests were absent?" Five times across 2.1 and 2.2 a
/// harness confidently reported a pass it had not earned, so CHECK 5 runs the OLD policy beside
/// the new one on the same scene and prints both. If the two columns ever agree, this harness has
/// stopped measuring what it claims to.
/// </summary>
public static class Stale
{
    public static int Run(int monitor, int quality, Rectangle bounds, string sourceArg)
    {
        Console.WriteLine("Stale-picture check — what the send queue does to a delta stream under load.");
        Console.WriteLine();

        int pass = 0, fail = 0;
        void Check(string what, bool ok, string detail = "")
        {
            if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
            else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        }

        // ---- part one: the queue's contract, exactly, with no screen in the way -----------------
        Console.WriteLine("The send queue under a socket that has stopped draining:");
        using (var link = StalledLink.Open())
        {
            // Fill until the queue refuses. The far end is not reading, so the socket's own
            // buffers fill first and the channel behind them second; both are real.
            // Frames the size a real one is -- a 1920x1200 screen at quality 55 costs a few
            // hundred KB. Using toy payloads here would need thousands of them to reach the
            // threshold and would say nothing about whether it can ever fire in the product.
            const int FrameBytes = 200 * 1024;
            int framesOffered = 0, framesAccepted = 0;
            while (framesOffered < 4000)
            {
                var f = Frame(BinKind.Tiles, framesOffered, FrameBytes);
                framesOffered++;
                if (link.Ws.SendBinary(f)) framesAccepted++;
                else break;
            }
            Check("the queue refuses a screen frame rather than taking it and losing an older one",
                  framesAccepted < framesOffered, $"{framesAccepted} accepted, then refused at offer {framesOffered}");
            Check("it refuses after roughly a second of backlog, not after a minute of one",
                  framesAccepted < 60, $"{framesAccepted} frames = {framesAccepted * FrameBytes / 1024 / 1024.0:F1} MB queued");
            Check("the caller is told — DroppedFrames counts it",
                  link.Ws.DroppedFrames > 0, $"DroppedFrames={link.Ws.DroppedFrames}");
            Check("the queue reports its depth in bytes, which is what the controller will read",
                  link.Ws.PendingBytes > 0, $"PendingBytes={link.Ws.PendingBytes / 1024}KB, peak {link.Ws.PeakBytes / 1024}KB");

            // Text and file chunks live in the reserve above the frame threshold, so they must
            // still be accepted with the queue in exactly the state that refused a frame.
            bool textTaken = link.Ws.Send(new ClipboardMsg { Text = "reserve-1" });
            bool chunkTaken = link.Ws.SendBinary(Bin.FileChunk("reserve-1", 0, new byte[64]));
            Check("a text message is still accepted with the frame queue full", textTaken);
            Check("a file chunk is still accepted with the frame queue full", chunkTaken);
            Check("nothing critical was dropped", link.Ws.DroppedCritical == 0, $"DroppedCritical={link.Ws.DroppedCritical}");

            // Now let it drain and count what actually arrived. Everything ACCEPTED must arrive:
            // that is the property the host's convergence depends on, because a frame that is
            // accepted and then quietly evicted is a stale rectangle nothing will ever repair.
            link.Drain(TimeSpan.FromSeconds(20), framesAccepted + 2);
            Check("every frame the queue ACCEPTED arrived — nothing was evicted behind our back",
                  link.FramesReceived == framesAccepted, $"{link.FramesReceived} arrived of {framesAccepted} accepted");
            Check("the text message arrived", link.TextReceived >= 1, $"{link.TextReceived} text");
            Check("the file chunk arrived", link.ChunksReceived >= 1, $"{link.ChunksReceived} chunks");
            var gaps = Gaps(link.FrameSeqReceived);
            Check("the frames that arrived are the ones that were accepted, in order",
                  gaps == 0, gaps == 0 ? "no gaps" : $"{gaps} gaps in the sequence");
        }

        // ---- part two: the consequence, in pixels -----------------------------------------------
        Console.WriteLine();
        Console.WriteLine("The same overload against the real capture path, with a scene on screen:");
        Console.WriteLine("  (the OLD policy is run beside the new one — if the two agree, this check is broken)");
        Console.WriteLine();

        var oldPolicy = PictureRun(monitor, quality, bounds, sourceArg, repairOnRefusal: false);
        var newPolicy = PictureRun(monitor, quality, bounds, sourceArg, repairOnRefusal: true);

        if (oldPolicy.Skipped || newPolicy.Skipped)
        {
            // Whichever one skipped, not whichever one is first. Reporting the wrong half's
            // reason printed a blank SKIP the first time this ran, which is a harness telling you
            // nothing in the tone of telling you something.
            Console.WriteLine($"  SKIP  {(oldPolicy.Skipped ? oldPolicy.Why : newPolicy.Why)}");
            Console.WriteLine();
            Console.WriteLine("  The picture half needs this machine's screen to itself. Run it from a terminal");
            Console.WriteLine("  with nothing else in the foreground; the contract checks above need nothing.");
            Console.WriteLine();
            Console.WriteLine($"{pass} passed, {fail} failed, picture check skipped.");
            return fail == 0 ? 0 : 1;
        }

        Console.WriteLine($"  old policy (refusal ignored):  {oldPolicy.WrongTiles} tiles wrong at the far end, worst {oldPolicy.WorstDelta}/255");
        Console.WriteLine($"  new policy (complete frame):   {newPolicy.WrongTiles} tiles wrong at the far end, worst {newPolicy.WorstDelta}/255");
        Console.WriteLine();

        Check("the old policy leaves the far end wrong — the fault this repairs is real and reproduced",
              oldPolicy.WrongTiles > 0, $"{oldPolicy.WrongTiles} stale tiles");
        Check("the new policy converges — the far end matches the screen after the overload",
              newPolicy.WrongTiles == 0, $"{newPolicy.WrongTiles} stale tiles, worst {newPolicy.WorstDelta}/255");
        Check("the two policies disagree, so this check can tell them apart",
              oldPolicy.WrongTiles != newPolicy.WrongTiles);

        Console.WriteLine();
        Console.WriteLine($"{pass} passed, {fail} failed.");
        return fail == 0 ? 0 : 1;
    }

    // ---- the picture run ------------------------------------------------------------------------

    private sealed class Result
    {
        public bool Skipped; public string Why = "";
        public int WrongTiles; public int WorstDelta;
    }

    /// <summary>
    /// One overload, start to finish, against a real capture source and a real socket.
    ///
    /// <paramref name="repairOnRefusal"/> is the whole difference between the two policies, and it
    /// is one line in <c>HostEngine</c>'s capture loop: a refused frame demands a complete one on
    /// the next pass. False reproduces what the product did before stage 2.3 — the refusal ignored,
    /// the delta treated as delivered. The eviction end differs from the old code (it dropped the
    /// oldest, this refuses the newest) and the consequence does not: either way a delta that never
    /// arrives is never sent again, which is the thing being measured.
    /// </summary>
    private static Result PictureRun(int monitor, int quality, Rectangle bounds, string sourceArg, bool repairOnRefusal)
    {
        var r = new Result();
        IScreenSource? src = null;
        try
        {
            bool wantDxgi = !sourceArg.Contains("gdi", StringComparison.OrdinalIgnoreCase);
            if (wantDxgi) { try { var d = new DxgiCapture(); d.Refresh(); src = d; } catch { } }
            src ??= new ScreenCapture();
            src.Refresh();

            // A still screen, not an animating one, and the first version of this got it wrong in
            // a way worth recording. It used the video scene and froze it after the overload — but
            // freezing an animating scene CHANGES every pixel, so the next capture found every
            // tile different and resent the lot. Both policies converged, the two columns agreed,
            // and the harness said so rather than claiming a pass. The stale region has to be one
            // that stops changing, which means the screen has to be still and the change has to be
            // something this code makes deliberately.
            using var scene = StillScene.Show(bounds);
            Thread.Sleep(500);
            if (!scene.HasTheScreen(bounds)) { r.Skipped = true; r.Why = "the scene is not the window on screen, so nothing here would be measuring it"; return r; }

            using var link = StalledLink.Open();
            var far = new FarEnd(bounds.Width, bounds.Height);
            link.OnTiles = far.Apply;

            // Phase 1 — a complete frame while the socket is healthy, so the far end starts right.
            link.Reading = true;
            var first = src.CaptureTiles(monitor, quality, true);
            if (first.Count == 0) { r.Skipped = true; r.Why = "the first capture produced nothing"; return r; }
            link.Ws.SendBinary(Encode(monitor, first));
            link.Drain(TimeSpan.FromSeconds(5), 1);

            // Phase 2 — stall the far end and back the queue up past the threshold. These are real
            // captured frames of the unchanged screen, sent again: correct data, harmless to apply,
            // and their only job is to occupy the queue the way a slow link does.
            link.Reading = false;

            // Phase 3 — the screen changes, and the capture of that change is the delta that has
            // to be lost. Capture it FIRST: the source marks these tiles delivered as it hands
            // them over, which is the whole fault, and nothing after this point will offer them
            // again unless a complete frame is demanded.
            var patch = new Rectangle(bounds.Width / 3, bounds.Height / 3, 384, 384);
            scene.Paint(patch, Color.FromArgb(220, 40, 40));
            Thread.Sleep(250);
            var changed = src.CaptureTiles(monitor, quality, false);
            if (changed.Count == 0) { r.Skipped = true; r.Why = "the patch never reached the screen, so there was no delta to lose"; return r; }

            // Only now fill the queue, and fill it until it actually refuses rather than until a
            // threshold is nominally crossed. The first version topped up to the threshold and
            // then slept while painting -- and a stalled socket still absorbs megabytes into its
            // own buffers, so the queue drained back under the line and the changed frame was
            // accepted. Filling immediately before the send is what makes this deterministic.
            var filler = Encode(monitor, first);
            int pushed = 0;
            while (pushed < 400 && link.Ws.SendBinary(filler)) pushed++;
            if (pushed >= 400) { r.Skipped = true; r.Why = "the queue never refused anything, so no overload happened"; return r; }

            bool refused = !link.Ws.SendBinary(Encode(monitor, changed));
            if (!refused) { r.Skipped = true; r.Why = "the changed frame was accepted, so nothing was lost and there is nothing to measure"; return r; }

            // Phase 4 — the link comes back, and from here nothing else on screen changes. The
            // only thing that can put the picture right is the sender knowing what it lost, which
            // is the single line of difference between the two policies.
            link.Reading = true;
            link.Drain(TimeSpan.FromSeconds(10), 0);
            for (int i = 0; i < 10; i++)
            {
                bool full = refused && repairOnRefusal;
                refused = false;
                var tiles = src.CaptureTiles(monitor, quality, full);
                if (tiles.Count > 0 && !link.Ws.SendBinary(Encode(monitor, tiles))) refused = true;
                Thread.Sleep(60);
            }
            link.Drain(TimeSpan.FromSeconds(10), 0);

            // The verdict: what the far end now holds against what is genuinely on the screen.
            // Captured through the same source, so a difference is the stream's, not the camera's.
            using var truth = Truth(bounds);
            (r.WrongTiles, r.WorstDelta) = far.CompareTo(truth);
            return r;
        }
        catch (Exception ex) { r.Skipped = true; r.Why = ex.Message; return r; }
        finally { (src as IDisposable)?.Dispose(); }
    }

    private static Bitmap Truth(Rectangle bounds)
    {
        var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, new Size(bounds.Width, bounds.Height), CopyPixelOperation.SourceCopy);
        return bmp;
    }

    // ---- the far end: what the technician's console would be holding ---------------------------

    /// <summary>
    /// Reassembles the tile stream exactly as the console does — each tile painted where it says
    /// it belongs, everything else left as it was. That "left as it was" is the entire point: it
    /// is what makes a lost delta permanent, and a harness that rebuilt the picture from scratch
    /// each frame would report a perfect picture and prove nothing.
    /// </summary>
    private sealed class FarEnd
    {
        private readonly Bitmap _canvas;
        private readonly object _lock = new();
        public FarEnd(int w, int h) => _canvas = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppRgb);

        public void Apply(byte[] frame)
        {
            lock (_lock)
            {
                int o = 1;
                o += 4; // monitor
                int count = BitConverter.ToInt32(frame, o); o += 4;
                using var g = Graphics.FromImage(_canvas);
                for (int i = 0; i < count; i++)
                {
                    int x = BitConverter.ToInt32(frame, o); o += 4;
                    int y = BitConverter.ToInt32(frame, o); o += 4;
                    int w = BitConverter.ToInt32(frame, o); o += 4;
                    int h = BitConverter.ToInt32(frame, o); o += 4;
                    int len = BitConverter.ToInt32(frame, o); o += 4;
                    using var ms = new MemoryStream(frame, o, len);
                    using var tile = new Bitmap(ms);
                    g.DrawImage(tile, x, y, w, h);
                    o += len;
                }
            }
        }

        /// <summary>
        /// Tiles that differ from the truth, counted in 128 px cells to match how they were sent.
        ///
        /// The threshold is not zero and cannot be: these tiles went through JPEG, so every cell
        /// differs slightly and a strict comparison would call a perfect stream a failure. 24/255
        /// mean absolute error per cell is far above what quality-55 JPEG costs (single figures)
        /// and far below a cell holding the wrong picture entirely, which is what a stale tile of
        /// an animating scene is.
        /// </summary>
        public (int wrong, int worst) CompareTo(Bitmap truth)
        {
            lock (_lock)
            {
                int wrong = 0, worst = 0;
                for (int ty = 0; ty < _canvas.Height; ty += 128)
                for (int tx = 0; tx < _canvas.Width; tx += 128)
                {
                    int w = Math.Min(128, _canvas.Width - tx), h = Math.Min(128, _canvas.Height - ty);
                    long sum = 0; int n = 0;
                    for (int y = ty; y < ty + h; y += 4)
                    for (int x = tx; x < tx + w; x += 4)
                    {
                        var a = _canvas.GetPixel(x, y); var b = truth.GetPixel(x, y);
                        sum += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B); n += 3;
                    }
                    int mae = n == 0 ? 0 : (int)(sum / n);
                    if (mae > worst) worst = mae;
                    if (mae > 24) wrong++;
                }
                return (wrong, worst);
            }
        }
    }

    // ---- a screen this harness controls completely -----------------------------------------------

    /// <summary>
    /// A plain still window covering the monitor, which changes only when this code says so.
    ///
    /// <see cref="SceneHost"/> is the right tool for the timing benches and the wrong one here: its
    /// scenes animate, and an animating screen repairs a lost delta by accident on the next frame.
    /// What this check needs is a screen where exactly one rectangle changes, exactly once, and
    /// then never again.
    /// </summary>
    private sealed class StillScene : IDisposable
    {
        private readonly Thread _thread;
        private Form? _form;
        private readonly ManualResetEventSlim _up = new();

        private StillScene(Rectangle bounds)
        {
            _thread = new Thread(() =>
            {
                _form = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    Bounds = bounds,
                    BackColor = Color.FromArgb(24, 24, 28),
                    TopMost = true,
                    ShowInTaskbar = false,
                };
                _form.Shown += (_, _) => { _form.Activate(); _up.Set(); };
                Application.Run(_form);
            });
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.IsBackground = true;
            _thread.Start();
            _up.Wait(5000);
        }

        public static StillScene Show(Rectangle bounds) => new(bounds);

        /// <summary>Paints a rectangle and does not repaint it — the form keeps no state to
        /// redraw from, which is what makes the change permanent for the rest of the run.</summary>
        public void Paint(Rectangle where, Color colour)
        {
            var f = _form;
            if (f == null) return;
            f.Invoke(() =>
            {
                using var g = f.CreateGraphics();
                using var b = new SolidBrush(colour);
                g.FillRectangle(b, where);
            });
        }

        /// <summary>
        /// Pushes this window to the front and then PROVES it is there, by asking Windows which
        /// process owns the pixel in the middle of the monitor. Being TopMost, being shown and
        /// being activated are all things a window can be while something else is still in front
        /// of it — the same lesson <see cref="SceneHost"/> records, and the same answer.
        /// </summary>
        public bool HasTheScreen(Rectangle bounds)
        {
            var mid = new POINT { X = bounds.X + bounds.Width / 2, Y = bounds.Y + bounds.Height / 2 };
            uint mine = (uint)Environment.ProcessId;
            for (int attempt = 0; attempt < 12; attempt++)
            {
                IntPtr h = _form?.Handle ?? IntPtr.Zero;
                if (h != IntPtr.Zero)
                {
                    SetWindowPos(h, HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_SHOWWINDOW);
                    SetForegroundWindow(h);
                }
                Thread.Sleep(150);
                IntPtr at = WindowFromPoint(mid);
                if (at != IntPtr.Zero) { GetWindowThreadProcessId(at, out uint pid); if (pid == mine) return true; }
            }
            return false;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_SHOWWINDOW = 0x0040;

        public void Dispose()
        {
            try { _form?.Invoke(() => _form!.Close()); } catch { }
            try { _up.Dispose(); } catch { }
        }
    }

    // ---- a socket that stops draining on command -------------------------------------------------

    /// <summary>
    /// A real WebSocket to a loopback listener whose reader can be switched off. Nothing is
    /// simulated: the socket's own buffers fill first and the send channel behind them second,
    /// which is the order it happens on a customer's uplink.
    /// </summary>
    private sealed class StalledLink : IDisposable
    {
        public WsConnection Ws = null!;
        public volatile bool Reading;
        public Action<byte[]>? OnTiles;
        public int FramesReceived, TextReceived, ChunksReceived;
        public readonly List<int> FrameSeqReceived = new();

        private HttpListener _listener = null!;
        private WebSocket _far = null!;
        private ClientWebSocket _near = null!;
        private CancellationTokenSource _cts = null!;
        private Task _pump = null!;

        public static StalledLink Open()
        {
            var l = new StalledLink();
            int port = FreePort();
            l._listener = new HttpListener();
            l._listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            l._listener.Start();
            var accept = l._listener.GetContextAsync();

            // The order here matters and cost a hang the first time. HttpListener does not send
            // the 101 that completes the client's handshake until AcceptWebSocketAsync is called,
            // so blocking on ConnectAsync before accepting deadlocks the two halves against each
            // other. Start the connect, take the context it produces, accept, and only then wait.
            l._near = new ClientWebSocket();
            var connect = l._near.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), CancellationToken.None);
            var ctx = accept.GetAwaiter().GetResult();
            l._far = ctx.AcceptWebSocketAsync(null).GetAwaiter().GetResult().WebSocket;
            connect.GetAwaiter().GetResult();

            l.Ws = new WsConnection(l._near);
            l._cts = new CancellationTokenSource();
            l._pump = Task.Run(() => l.PumpAsync(l._cts.Token));
            return l;
        }

        /// <summary>Reads only while <see cref="Reading"/> is set. While it is not, the socket
        /// backs up for real — which is the whole apparatus.</summary>
        private async Task PumpAsync(CancellationToken ct)
        {
            var buf = new byte[128 * 1024];
            using var ms = new MemoryStream();
            try
            {
                while (!ct.IsCancellationRequested && _far.State == WebSocketState.Open)
                {
                    if (!Reading) { await Task.Delay(15, ct); continue; }
                    ms.SetLength(0);
                    WebSocketReceiveResult res;
                    do
                    {
                        res = await _far.ReceiveAsync(buf, ct);
                        if (res.MessageType == WebSocketMessageType.Close) return;
                        ms.Write(buf, 0, res.Count);
                    } while (!res.EndOfMessage);

                    if (res.MessageType == WebSocketMessageType.Text) { Interlocked.Increment(ref TextReceived); continue; }
                    var data = ms.ToArray();
                    if (data.Length == 0) continue;
                    if (data[0] == BinKind.FileChunk) { Interlocked.Increment(ref ChunksReceived); continue; }
                    if (data[0] != BinKind.Tiles) continue;
                    Interlocked.Increment(ref FramesReceived);
                    if (OnTiles != null) { try { OnTiles(data); } catch { } }
                    else lock (FrameSeqReceived) FrameSeqReceived.Add(BitConverter.ToInt32(data, 1));
                }
            }
            catch { }
        }

        /// <summary>Lets the far end read until it has gone quiet, or until <paramref name="want"/>
        /// frames have arrived. Never a bare sleep: a fixed wait is how a harness reports a pass
        /// that is really a race.</summary>
        public void Drain(TimeSpan limit, int want)
        {
            Reading = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int last = -1, quiet = 0;
            while (sw.Elapsed < limit)
            {
                Thread.Sleep(60);
                int now = FramesReceived;
                if (want > 0 && now >= want && Ws.PendingBytes == 0) return;
                if (now == last && Ws.PendingBytes == 0) { if (++quiet >= 8) return; }
                else quiet = 0;
                last = now;
            }
        }

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { Ws.DisposeAsync().AsTask().Wait(2000); } catch { }
            try { _far.Dispose(); } catch { }
            try { _near.Dispose(); } catch { }
            try { _listener.Stop(); } catch { }
        }
    }

    // ---- small helpers --------------------------------------------------------------------------

    private static byte[] Frame(byte kind, int seq, int bytes)
    {
        var b = new byte[Math.Max(9, bytes)];
        b[0] = kind;
        BitConverter.TryWriteBytes(b.AsSpan(1), seq);
        BitConverter.TryWriteBytes(b.AsSpan(5), 0);
        return b;
    }

    private static byte[] Encode(int monitor, List<(int x, int y, int w, int h, byte[] jpeg)> tiles)
    {
        int size = 1 + 4 + 4;
        foreach (var t in tiles) size += 20 + t.jpeg.Length;
        var buf = new byte[size];
        int o = 0;
        buf[o++] = BinKind.Tiles;
        BitConverter.TryWriteBytes(buf.AsSpan(o), monitor); o += 4;
        BitConverter.TryWriteBytes(buf.AsSpan(o), tiles.Count); o += 4;
        foreach (var t in tiles)
        {
            BitConverter.TryWriteBytes(buf.AsSpan(o), t.x); o += 4;
            BitConverter.TryWriteBytes(buf.AsSpan(o), t.y); o += 4;
            BitConverter.TryWriteBytes(buf.AsSpan(o), t.w); o += 4;
            BitConverter.TryWriteBytes(buf.AsSpan(o), t.h); o += 4;
            BitConverter.TryWriteBytes(buf.AsSpan(o), t.jpeg.Length); o += 4;
            t.jpeg.CopyTo(buf.AsSpan(o)); o += t.jpeg.Length;
        }
        return buf;
    }

    private static int Gaps(List<int> seq)
    {
        lock (seq)
        {
            int gaps = 0;
            for (int i = 1; i < seq.Count; i++) if (seq[i] != seq[i - 1] + 1) gaps++;
            return gaps;
        }
    }
}
