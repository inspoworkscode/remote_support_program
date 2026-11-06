using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using RemoteSupport.Shared;

namespace RemoteSupport.Technician;

/// <summary>
/// Renders the remote monitor from incremental JPEG tiles and translates local mouse/keyboard
/// into remote input events. Draws the whole canvas scaled to fit ("fit to window").
/// </summary>
public sealed partial class ScreenView : Control
{
    private Bitmap? _canvas;
    private readonly object _canvasLock = new();
    private int _monWidth, _monHeight;
    private Point _remoteCursor;
    public int MonitorIndex { get; set; }
    public bool ControlEnabled { get; set; } = true;

    /// <summary>Raised with a ready-to-send input message (MouseMsg or KeyMsg).</summary>
    public event Action<Msg>? OnInput;
    /// <summary>F11 toggles full screen locally and is never forwarded to the remote machine.</summary>
    public event Action? OnToggleFullScreen;

    public ScreenView()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Black;
        TabStop = true;
    }

    public void Reset(int width, int height)
    {
        Trace.Write($"view: Reset canvas to {width}x{height} (MonitorIndex={MonitorIndex})");
        lock (_canvasLock)
        {
            _monWidth = width; _monHeight = height;
            _canvas?.Dispose();
            // 32 bits per pixel rather than 24. The tile path does not care either way, but a
            // decoded H.264 frame arrives as BGRA and a 32-bit canvas takes it as a row-by-row
            // memcpy; a 24-bit one would need a second pass over every pixel of every frame to
            // throw away a byte.
            _canvas = new Bitmap(Math.Max(1, width), Math.Max(1, height), PixelFormat.Format32bppRgb);
            using var g = Graphics.FromImage(_canvas);
            g.Clear(Color.FromArgb(20, 20, 20));
            // The decoder holds reference frames for a picture that is about to be replaced.
            _decoder?.Reset();
        }
        Invalidate();
    }
    // ---- black-screen diagnostics (see docs/HANDOVER.md 6b) -------------------------------
    // These counters separate "no frames arrive" from "frames arrive but are rejected" from
    // "frames are drawn but nothing shows".
    private long _frames, _applied, _tilesDrawn, _decodeFails;
    private long _rejMonitor, _rejNoCanvas, _rejHeader, _rejTruncated;
    private int _lastRejectedMonitor = int.MinValue;
    private int _paintsLogged;

    /// <summary>Apply a binary Tiles frame (see BinKind.Tiles layout).</summary>
    public void ApplyTiles(ReadOnlyMemory<byte> frame)
    {
        _frames++;
        var s = frame.Span;
        if (s.Length < 9 || s[0] != BinKind.Tiles)
        {
            _rejHeader++;
            if (_rejHeader == 1) Trace.Write($"view: REJECT frame — bad header (len={s.Length}, kind={(s.Length > 0 ? s[0] : -1)})");
            return;
        }
        int monitor = BitConverter.ToInt32(s.Slice(1, 4));
        if (monitor != MonitorIndex)
        {
            _rejMonitor++;
            if (monitor != _lastRejectedMonitor)
            {
                _lastRejectedMonitor = monitor;
                Trace.Write($"view: REJECT frame — monitor mismatch: frame says {monitor}, view is showing {MonitorIndex} (rejected {_rejMonitor} so far)");
            }
            return;
        }
        int count = BitConverter.ToInt32(s.Slice(5, 4));
        int o = 9;
        int drawn = 0;
        lock (_canvasLock)
        {
            if (_canvas == null)
            {
                _rejNoCanvas++;
                if (_rejNoCanvas == 1) Trace.Write("view: REJECT frame — no canvas yet (Reset has never run, so SwitchMonitor never ran)");
                return;
            }
            using var g = Graphics.FromImage(_canvas);
            g.CompositingMode = CompositingMode.SourceCopy;
            for (int i = 0; i < count && o + 20 <= s.Length; i++)
            {
                int x = BitConverter.ToInt32(s.Slice(o, 4)); o += 4;
                int y = BitConverter.ToInt32(s.Slice(o, 4)); o += 4;
                int w = BitConverter.ToInt32(s.Slice(o, 4)); o += 4;
                int h = BitConverter.ToInt32(s.Slice(o, 4)); o += 4;
                int len = BitConverter.ToInt32(s.Slice(o, 4)); o += 4;
                if (o + len > s.Length) { _rejTruncated++; if (_rejTruncated == 1) Trace.Write($"view: tile {i}/{count} truncated (need {len} B, {s.Length - o} left)"); break; }
                try
                {
                    using var ms = new MemoryStream(frame.Slice(o, len).ToArray());
                    using var tile = Image.FromStream(ms);
                    g.DrawImage(tile, new Rectangle(x, y, w, h));
                    drawn++;
                }
                catch (Exception ex) { _decodeFails++; if (_decodeFails == 1) Trace.Write($"view: tile decode FAILED — {ex.GetType().Name}: {ex.Message}"); }
                o += len;
            }
        }
        _applied++; _tilesDrawn += drawn;
        if (_applied <= 5 || _applied % 50 == 0)
            Trace.Write($"view: applied frame #{_applied} — monitor={monitor}, tiles claimed={count}, drawn={drawn}, canvas={_monWidth}x{_monHeight}, control={Width}x{Height}, visible={Visible}, handle={IsHandleCreated}" +
                        $"  [totals frames={_frames} applied={_applied} tiles={_tilesDrawn} rejMonitor={_rejMonitor} rejNoCanvas={_rejNoCanvas} decodeFail={_decodeFails}]");
        Invalidate();
    }

    // ---- the H.264 path (stage 2.2) ---------------------------------------------------------
    //
    // A second way for the same picture to arrive, chosen by the host from what the SubscribeMsg
    // said this console could decode. The two are never mixed within one subscription, but both
    // arrive at this control, because which one is in use is the host's business and not this
    // control's.

    private RemoteSupport.Media.H264Decoder? _decoder;
    private int _decW, _decH;
    private bool _videoRefused;
    private long _videoFrames, _videoDrawn, _videoKeyFrames, _videoRejMonitor;

    /// <summary>
    /// Raised once when this machine cannot decode H.264 at all, with the reason. The session
    /// window answers it by subscribing again asking for JPEG tiles: a console that cannot decode
    /// the stream it asked for must say so rather than show black, and it is the only end that
    /// knows.
    /// </summary>
    public event Action<string>? OnVideoUnavailable;

    /// <summary>Apply a binary Video frame (see BinKind.Video layout).</summary>
    public void ApplyVideo(ReadOnlyMemory<byte> frame)
    {
        _frames++; _videoFrames++;
        if (frame.Length <= Bin.VideoHeader || frame.Span[0] != BinKind.Video)
        {
            _rejHeader++;
            if (_rejHeader == 1) Trace.Write($"view: REJECT video frame — bad header (len={frame.Length})");
            return;
        }
        var (monitor, key, w, h, bitstream) = Bin.ParseVideo(frame);
        if (monitor != MonitorIndex)
        {
            _videoRejMonitor++;
            if (monitor != _lastRejectedMonitor)
            {
                _lastRejectedMonitor = monitor;
                Trace.Write($"view: REJECT video frame — monitor mismatch: frame says {monitor}, view is showing {MonitorIndex}");
            }
            return;
        }
        if (key) _videoKeyFrames++;
        if (_videoRefused) return;

        try
        {
            // The coded size is the picture size rounded up to something H.264 can express, so a
            // decoder built for the monitor's own size would be wrong on an odd-height screen.
            if (_decoder == null || _decW != w || _decH != h)
            {
                _decoder?.Dispose();
                _decoder = new RemoteSupport.Media.H264Decoder(w, h);
                _decW = w; _decH = h;
                Trace.Write($"view: H.264 decoder ready — {_decoder.Name}, {w}x{h}");
            }
            if (!_decoder.TryDecode(bitstream.Span, key)) return;
        }
        catch (Exception ex)
        {
            // Once, and then never again this session. A console that cannot decode will fail on
            // every frame, and a trace of that at twelve a second buries everything else in it.
            _videoRefused = true;
            Trace.Write($"view: H.264 cannot be decoded on this machine — {ex.Message}");
            OnVideoUnavailable?.Invoke(ex.Message);
            return;
        }

        lock (_canvasLock)
        {
            if (_canvas == null)
            {
                _rejNoCanvas++;
                if (_rejNoCanvas == 1) Trace.Write("view: REJECT video frame — no canvas yet (Reset has never run)");
                return;
            }
            var bits = _canvas.LockBits(new Rectangle(0, 0, _canvas.Width, _canvas.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                // Crop rather than scale. The coded frame is the monitor's size rounded up, so the
                // extra is a row or a column of padding the encoder invented; stretching it would
                // shift every pixel on the screen by a fraction and make text shimmer.
                int rows = Math.Min(_canvas.Height, _decoder!.Height);
                int bytes = Math.Min(_canvas.Width, _decoder.Width) * 4;
                unsafe
                {
                    fixed (byte* src = _decoder.Bgra)
                    {
                        byte* dst = (byte*)bits.Scan0;
                        for (int y = 0; y < rows; y++)
                            Buffer.MemoryCopy(src + (long)y * _decoder.Stride, dst + (long)y * bits.Stride, bytes, bytes);
                    }
                }
            }
            finally { _canvas.UnlockBits(bits); }
        }
        _applied++; _videoDrawn++;
        if (_videoDrawn <= 5 || _videoDrawn % 100 == 0)
            Trace.Write($"view: H.264 frame #{_videoDrawn} — {w}x{h}, {bitstream.Length} B{(key ? " (key)" : "")}" +
                        $"  [arrived={_videoFrames} drawn={_videoDrawn} keys={_videoKeyFrames} droppedBeforeKey={_decoder?.DroppedBeforeKey ?? 0} decodeFail={_decoder?.Failures ?? 0}]");
        Invalidate();
    }

    /// <summary>One-line snapshot for the session window's status bar / trace on demand.</summary>
    public string DiagnosticSummary() =>
        $"frames={_frames} applied={_applied} tilesDrawn={_tilesDrawn} rejMonitorMismatch={_rejMonitor} rejNoCanvas={_rejNoCanvas} rejBadHeader={_rejHeader} truncated={_rejTruncated} decodeFail={_decodeFails} " +
        $"video={_videoFrames}/{_videoDrawn} videoKeys={_videoKeyFrames} videoRejMonitor={_videoRejMonitor} videoBeforeKey={_decoder?.DroppedBeforeKey ?? 0} videoFail={_decoder?.Failures ?? 0} codec={(_videoRefused ? "h264 refused here" : _decoder == null ? "jpeg tiles" : "h264")} " +
        $"mouseSent={_mouseSent} mouseOffPicture={_mouseOffPicture} mouseDisabled={_mouseDisabled} keysSent={_keysSent} " +
        $"MonitorIndex={MonitorIndex} canvas={_monWidth}x{_monHeight} control={Width}x{Height} visible={Visible} handle={IsHandleCreated} parent={(Parent?.GetType().Name ?? "none")}";

    /// <summary>
    /// Where the customer's pointer is, and which shape it is wearing. Stage 2.4.
    ///
    /// The shape may not have arrived yet — it travels once per distinct cursor — so an unknown id
    /// is not an error, it is the first frame of a shape. The ring is what gets drawn until then,
    /// which is why it was kept rather than deleted.
    /// </summary>
    public void SetRemoteCursor(int x, int y, bool visible, long shapeId)
    {
        _remoteCursor = new Point(x, y);
        _remoteCursorVisible = visible;
        _remoteCursorShape = shapeId;
        Invalidate();
    }

    /// <summary>
    /// Registers one pointer bitmap. [5][shapeId:i64][w][h][hotX][hotY][png].
    ///
    /// Kept for the life of the session: Windows reuses a small set of cursor handles, so this
    /// holds a handful of small bitmaps and never needs eviction in any session a human sits
    /// through. A shape that arrives twice simply replaces itself.
    /// </summary>
    public void ApplyCursorShape(ReadOnlyMemory<byte> frame)
    {
        try
        {
            var s = frame.Span;
            long id = BitConverter.ToInt64(s[1..]);
            int w = BitConverter.ToInt32(s[9..]), h = BitConverter.ToInt32(s[13..]);
            int hx = BitConverter.ToInt32(s[17..]), hy = BitConverter.ToInt32(s[21..]);
            var png = frame[25..].ToArray();
            using var ms = new MemoryStream(png);
            var bmp = new Bitmap(ms);
            lock (_cursorLock)
            {
                if (_cursorShapes.TryGetValue(id, out var old)) { try { old.bmp.Dispose(); } catch { } }
                _cursorShapes[id] = (bmp, new Point(hx, hy));
            }
            Invalidate();
        }
        catch { /* a pointer we cannot decode costs a ring, not a session */ }
    }

    private readonly Dictionary<long, (Bitmap bmp, Point hot)> _cursorShapes = new();
    private readonly object _cursorLock = new();
    private bool _remoteCursorVisible = true;
    private long _remoteCursorShape;

    protected override void Dispose(bool disposing)
    {
        // The decoder holds a Media Foundation transform, which holds worker threads. Leaving one
        // behind per closed session window is a slow leak that only shows up on a technician who
        // keeps the console open all day, which is every technician.
        if (disposing)
        {
            RemoveShortcutHook();
            try { _decoder?.Dispose(); } catch { } _decoder = null;
            // The pointer bitmaps are GDI objects, and a console kept open all day opens a lot
            // of sessions.
            lock (_cursorLock) { foreach (var s in _cursorShapes.Values) { try { s.bmp.Dispose(); } catch { } } _cursorShapes.Clear(); }
        }
        base.Dispose(disposing);
    }

    private Rectangle DestRect()
    {
        if (_monWidth == 0 || _monHeight == 0) return ClientRectangle;
        double scale = Math.Min((double)Width / _monWidth, (double)Height / _monHeight);
        int w = (int)(_monWidth * scale), h = (int)(_monHeight * scale);
        return new Rectangle((Width - w) / 2, (Height - h) / 2, Math.Max(1, w), Math.Max(1, h));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var dest = DestRect();
        // First few paints only: proves the control is actually being asked to draw, and with what.
        if (_paintsLogged < 3)
        {
            _paintsLogged++;
            bool haveCanvas; lock (_canvasLock) haveCanvas = _canvas != null;
            Trace.Write($"view: OnPaint #{_paintsLogged} — dest={dest.X},{dest.Y} {dest.Width}x{dest.Height}, canvas={(haveCanvas ? $"{_monWidth}x{_monHeight}" : "null")}, control={Width}x{Height}, visible={Visible}, parent={(Parent?.GetType().Name ?? "none")}");
        }
        e.Graphics.InterpolationMode = InterpolationMode.Bilinear;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        lock (_canvasLock) { if (_canvas != null) e.Graphics.DrawImage(_canvas, dest); }
        DrawRemoteCursor(e.Graphics, dest);
        // A frozen picture and a live one look identical, and the last frame from a machine that
        // has gone is the most convincing possible lie a remote view can tell. Veiling it is the
        // only way the difference is visible at a glance.
        if (Stale) using (var veil = new SolidBrush(Color.FromArgb(150, 10, 12, 20))) e.Graphics.FillRectangle(veil, ClientRectangle);
    }

    /// <summary>
    /// What this view is showing — their screen and their pointer — drawn into
    /// <paramref name="target"/>, scaled to fit and letterboxed in black. For session recording
    /// (1.6), which records exactly what the technician saw. Thread-safe: the recorder calls it
    /// from its own thread. False when there is no picture yet.
    /// </summary>
    public bool RenderFrame(Bitmap target)
    {
        using var g = Graphics.FromImage(target);
        g.Clear(Color.Black);
        Rectangle dest;
        lock (_canvasLock)
        {
            if (_canvas == null || _monWidth == 0 || _monHeight == 0) return false;
            double scale = Math.Min((double)target.Width / _monWidth, (double)target.Height / _monHeight);
            int w = (int)(_monWidth * scale), h = (int)(_monHeight * scale);
            dest = new Rectangle((target.Width - w) / 2, (target.Height - h) / 2, Math.Max(1, w), Math.Max(1, h));
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_canvas, dest);
        }
        DrawRemoteCursor(g, dest);
        return true;
    }

    /// <summary>The size of the customer's screen as it is being drawn, or empty before the first frame.</summary>
    public Size RemoteSize => new(_monWidth, _monHeight);

    private void DrawRemoteCursor(Graphics graphics, Rectangle dest)
    {
        // The customer's pointer. Stage 2.4: the real bitmap when we have it, the old ring until
        // then. Nothing at all when the customer's own pointer is hidden -- a full-screen video or
        // a game hides it, and drawing one anyway would show the technician something that is not
        // on the customer's screen.
        if (_monWidth > 0 && _remoteCursorVisible)
        {
            double sx = (double)dest.Width / _monWidth, sy = (double)dest.Height / _monHeight;
            int cx = dest.X + (int)(_remoteCursor.X * sx), cy = dest.Y + (int)(_remoteCursor.Y * sy);
            (Bitmap bmp, Point hot)? shape = null;
            lock (_cursorLock) if (_cursorShapes.TryGetValue(_remoteCursorShape, out var s)) shape = s;
            if (shape != null)
            {
                // Scaled with the picture, so the pointer stays the size it is on the customer's
                // screen rather than growing when the view is zoomed out. The hot spot is what is
                // placed at the reported position -- the tip of the arrow, not its corner, which
                // is a one-pixel-looking detail that is the difference between a pointer that
                // lines up with what it is clicking and one that does not.
                var b = shape.Value.bmp;
                int dw = Math.Max(1, (int)Math.Round(b.Width * sx)), dh = Math.Max(1, (int)Math.Round(b.Height * sy));
                int hx = (int)Math.Round(shape.Value.hot.X * sx), hy = (int)Math.Round(shape.Value.hot.Y * sy);
                var was = graphics.InterpolationMode;
                // Nearest neighbour: a 32x32 pointer smoothed up or down turns into a smudge, and
                // a crisp wrong-by-a-pixel pointer reads far better than a soft correct one.
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.DrawImage(b, new Rectangle(cx - hx, cy - hy, dw, dh));
                graphics.InterpolationMode = was;
            }
            else
            {
                using var pen = new Pen(Color.FromArgb(180, Color.Yellow), 2);
                graphics.DrawEllipse(pen, cx - 6, cy - 6, 12, 12);
            }
        }
    }

    /// <summary>
    /// The picture on screen is no longer live — the socket has dropped, or the session is over.
    /// Veils the view and refuses input: sending a click at a frozen picture would be aimed at
    /// wherever the machine's windows were a minute ago.
    /// </summary>
    public bool Stale
    {
        get => _stale;
        set { if (_stale == value) return; _stale = value; Invalidate(); }
    }
    private bool _stale;

    private bool ToRemote(int localX, int localY, out int rx, out int ry)
    {
        rx = ry = 0;
        var dest = DestRect();
        if (!dest.Contains(localX, localY) || _monWidth == 0) return false;
        rx = (int)((localX - dest.X) * (double)_monWidth / dest.Width);
        ry = (int)((localY - dest.Y) * (double)_monHeight / dest.Height);
        rx = Math.Clamp(rx, 0, _monWidth - 1); ry = Math.Clamp(ry, 0, _monHeight - 1);
        return true;
    }

    private static int Btn(MouseButtons b) => b switch { MouseButtons.Right => 1, MouseButtons.Middle => 2, _ => 0 };

    // ---- input diagnostics ----------------------------------------------------------------
    // "Mouse and keyboard stopped working after pop out" has three possible shapes: the control
    // never sees the event, it sees it and suppresses it, or it sends it and the host ignores it.
    // These counters separate the first two; the host side is checked with the remote CursorMsg.
    private long _mouseSent, _mouseOffPicture, _mouseDisabled, _keysSent, _keysDisabled;
    private string _lastSuppressReason = "";

    /// <summary>Maps a local point to the remote monitor, tracing the first few and any change of reason.</summary>
    private bool Allow(int localX, int localY, string what, out int rx, out int ry)
    {
        rx = ry = 0;
        if (Stale)
        {
            _mouseDisabled++;
            Suppressed("the connection to that machine is down");
            return false;
        }
        if (!ControlEnabled)
        {
            _mouseDisabled++;
            Suppressed("the Control checkbox is off");
            return false;
        }
        if (!ToRemote(localX, localY, out rx, out ry))
        {
            _mouseOffPicture++;
            Suppressed($"pointer {localX},{localY} is outside the picture — dest={DestRect()}, canvas={_monWidth}x{_monHeight}, control={Width}x{Height}");
            return false;
        }
        _mouseSent++;
        if (_mouseSent <= 3 || _mouseSent % 500 == 0)
            Trace.Write($"view: mouse {what} -> remote {rx},{ry}  [sent={_mouseSent} offPicture={_mouseOffPicture} disabled={_mouseDisabled}]");
        return true;
    }

    private void Suppressed(string reason)
    {
        if (reason == _lastSuppressReason) return;
        _lastSuppressReason = reason;
        Trace.Write($"view: input SUPPRESSED — {reason}");
    }

    // The focus is also what switches Windows shortcuts over to their machine (ScreenView.Shortcuts.cs).
    protected override void OnGotFocus(EventArgs e) { Trace.Write($"view: got focus (parent={Parent?.GetType().Name ?? "none"})"); InstallShortcutHook(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Trace.Write("view: lost focus"); RemoveShortcutHook(); base.OnLostFocus(e); }

    // ---- dragging OFF the picture (roadmap 3.5.2) --------------------------------------------
    // A left-button drag on the picture is a drag on THEIR screen — moving a file between their
    // folders, a window across their desktop — and it is forwarded as one. Only when the pointer
    // leaves this window with the button still held does it become a question for this machine,
    // and then only if the person there has something selected in a folder window; the session
    // window decides that (OnDragOffPicture) and this control does the mechanics.

    /// <summary>A left-button drag has begun on the picture. The session asks their machine what is selected.</summary>
    public event Action? OnRemoteDragStarted;
    /// <summary>
    /// The pointer left this window with the left button still down. Returns true when the session
    /// took the drag over — this control then forwards nothing more of that gesture.
    /// </summary>
    public Func<bool>? OnDragOffPicture;

    /// <summary>
    /// Which buttons are held right now. Windows' own answer, except under tools/dragoffcheck,
    /// which drives this control with synthetic events and cannot hold a real button down without
    /// taking the mouse from whoever is at this laptop.
    /// </summary>
    public static Func<MouseButtons> ButtonsNow = () => Control.MouseButtons;

    private Point? _leftDownAt;
    private bool _dragStarted, _dragOffAsked, _swallowLeftUp;
    private int _lastRemoteX, _lastRemoteY;

    /// <summary>
    /// Ends the drag on their screen without dropping anything there: Escape cancels a drag in
    /// Explorer and in nearly everything else, and the button is then released where it last was.
    /// </summary>
    public void CancelRemoteDrag()
    {
        OnInput?.Invoke(new KeyMsg { Vk = (int)Keys.Escape, Down = true });
        OnInput?.Invoke(new KeyMsg { Vk = (int)Keys.Escape, Down = false });
        OnInput?.Invoke(new MouseMsg { Monitor = MonitorIndex, X = _lastRemoteX, Y = _lastRemoteY, Action = "up", Button = 0 });   // 0 is the left button (Btn)
        _swallowLeftUp = true;
        Trace.Write($"view: remote drag cancelled at {_lastRemoteX},{_lastRemoteY} — it is leaving the picture");
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_swallowLeftUp)
        {
            // Still the gesture that left: nothing of it goes to their screen.
            if ((ButtonsNow() & MouseButtons.Left) != 0) { base.OnMouseMove(e); return; }
            // The shell's drag loop ate the release, so no MouseUp is coming to clear this. The
            // button is up; the gesture is over, and the pointer is theirs again.
            _swallowLeftUp = false; _leftDownAt = null;
        }
        if (_leftDownAt is Point at && (e.Button & MouseButtons.Left) != 0)
        {
            var drag = SystemInformation.DragSize;
            if (!_dragStarted && (Math.Abs(e.X - at.X) > drag.Width || Math.Abs(e.Y - at.Y) > drag.Height))
            {
                _dragStarted = true;
                try { OnRemoteDragStarted?.Invoke(); } catch (Exception ex) { Trace.Write("view: OnRemoteDragStarted threw " + ex.Message); }
            }
            // Outside the whole control, not just off the picture: the letterbox bars are still
            // this window, and a drag that strays into them has not left anything.
            if (_dragStarted && !_dragOffAsked && !ClientRectangle.Contains(e.Location))
            {
                _dragOffAsked = true;
                bool took = false;
                try { took = OnDragOffPicture?.Invoke() ?? false; } catch (Exception ex) { Trace.Write("view: OnDragOffPicture threw " + ex.Message); }
                if (took) { base.OnMouseMove(e); return; }
            }
        }
        if (Allow(e.X, e.Y, "move", out int rx, out int ry))
        {
            _lastRemoteX = rx; _lastRemoteY = ry;
            OnInput?.Invoke(new MouseMsg { Monitor = MonitorIndex, X = rx, Y = ry, Action = "move" });
        }
        base.OnMouseMove(e);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (e.Button == MouseButtons.Left) { _leftDownAt = e.Location; _dragStarted = _dragOffAsked = _swallowLeftUp = false; }
        if (Allow(e.X, e.Y, "down", out int rx, out int ry))
        {
            _lastRemoteX = rx; _lastRemoteY = ry;
            OnInput?.Invoke(new MouseMsg { Monitor = MonitorIndex, X = rx, Y = ry, Action = "down", Button = Btn(e.Button) });
        }
        base.OnMouseDown(e);
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _leftDownAt = null;
            // Their button was already released by CancelRemoteDrag; a second "up" would be a
            // click on whatever is under their pointer now.
            if (_swallowLeftUp) { _swallowLeftUp = false; base.OnMouseUp(e); return; }
        }
        if (Allow(e.X, e.Y, "up", out int rx, out int ry)) OnInput?.Invoke(new MouseMsg { Monitor = MonitorIndex, X = rx, Y = ry, Action = "up", Button = Btn(e.Button) });
        base.OnMouseUp(e);
    }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (Allow(e.X, e.Y, "wheel", out int rx, out int ry)) OnInput?.Invoke(new MouseMsg { Monitor = MonitorIndex, X = rx, Y = ry, Action = "wheel", Delta = e.Delta });
        base.OnMouseWheel(e);
    }

    protected override bool IsInputKey(Keys keyData) => true; // capture arrows, tab, etc.

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F11) { OnToggleFullScreen?.Invoke(); e.Handled = true; e.SuppressKeyPress = true; return; }
        if (ControlEnabled && !Stale)
        {
            _keysSent++;
            if (_keysSent <= 3 || _keysSent % 200 == 0) Trace.Write($"view: key down vk={(int)e.KeyCode} ({e.KeyCode})  [sent={_keysSent} disabled={_keysDisabled}]");
            _remoteDown.Add((int)e.KeyCode);
            OnInput?.Invoke(new KeyMsg { Vk = (int)e.KeyCode, Down = true, Extended = IsExtended(e.KeyCode) });
        }
        else { _keysDisabled++; Suppressed("the Control checkbox is off"); }
        e.Handled = true; e.SuppressKeyPress = true;
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F11) { e.Handled = true; e.SuppressKeyPress = true; return; }
        _remoteDown.Remove((int)e.KeyCode);
        if (ControlEnabled && !Stale) OnInput?.Invoke(new KeyMsg { Vk = (int)e.KeyCode, Down = false, Extended = IsExtended(e.KeyCode) });
        e.Handled = true; e.SuppressKeyPress = true;
    }

    private static bool IsExtended(Keys k) => k is Keys.Right or Keys.Left or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown or Keys.Insert or Keys.Delete or Keys.RControlKey or Keys.RMenu
        or Keys.LWin or Keys.RWin or Keys.Apps;
}
