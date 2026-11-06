using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CodecBench;

/// <summary>
/// The thing being captured. A borderless window covering the monitor under test, painting one of
/// three reproducible conditions.
///
/// It exists because "measure before and after" is worthless if the before and after were looking
/// at different screens. A real desktop is never twice the same, so the bench brings its own.
///
/// The honest caveat, and it is written into the report as well: <see cref="SceneKind.Video"/> is
/// a STAND-IN. Real video is photographic detail changing across the whole frame; this is a
/// smooth field plus noise. It is deliberately harsher than a scrolling document and deliberately
/// cheaper than a film, and it is identical on every run, which is what makes it useful for
/// comparing two codecs. It is not a substitute for pointing --live at a real film once.
/// </summary>
public enum SceneKind
{
    Idle, Scroll, Video,
    /// <summary>
    /// A still page of saturated colour, for the correctness checks rather than the timings.
    ///
    /// It exists because of a specific way the first colour check lied. The text scene is black
    /// ink on white paper, which is to say it is almost perfectly grey — and on grey, BT.601 and
    /// BT.709 produce IDENTICAL pixels. So the check compared both matrices, got the same answer
    /// for each to one decimal place, and reported a pass without having tested the thing it was
    /// written to test. A harness that cannot distinguish two states will confidently report the
    /// wrong one, and this is what makes them distinguishable.
    /// </summary>
    Colour,
    /// <summary>
    /// A still page that looks like a real Windows desktop at 100%: 12-pixel Segoe UI drawn by GDI
    /// with ClearType, as Explorer and Settings draw it, white on a coloured title bar, blue links,
    /// red and green status text, a grey side panel, small coloured icons. Added 2026-10-15 for
    /// --sharp, because Idle's large Consolas on white flattered the encoder: the complaint was
    /// "never clear" on a 1366x768 screen, and small coloured text is what such a screen is made of.
    /// </summary>
    Desk,
}

public sealed class Scene : Form
{
    private SceneKind _kind;
    private readonly System.Windows.Forms.Timer _tick = new();
    private int _frame;
    private readonly string[] _lines;
    private readonly Font _mono = new("Consolas", 11f);
    private Bitmap? _noise;

    public Scene(SceneKind kind, Rectangle bounds)
    {
        _kind = kind;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.White;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

        _lines = BuildPage();
        // ~60 Hz. A scene that repaints slower than the capturer is measuring the scene, not the
        // capturer, and the whole run would be a study of this file.
        _tick.Interval = 15;
        _tick.Tick += (_, _) => { if (!Still(_kind)) { _frame++; Invalidate(); } };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Being TopMost is not enough when the previous scene is still fading out: the new window
        // can end up under it, leaving a strip of the real desktop on screen. That made an idle
        // pass report 100 KB/s of "changes" that were somebody else's animation, and only showed
        // up when several scenes ran in one process. Ask for the front explicitly.
        BringToFront(); Activate();
        if (!Still(_kind)) _tick.Start();
    }

    /// <summary>
    /// Switches what the scene paints WITHOUT closing the window.
    ///
    /// One window for the whole run, rather than one per condition, and the reason is specific:
    /// a window can only reliably be pushed in front while its process is the foreground one.
    /// The first scene of a run manages it; every later one loses to whatever took the
    /// foreground in between -- on the machine this was written on, an always-on-top editor --
    /// and the bench then measures that instead, which is exactly the fault SceneHost now
    /// refuses to let pass silently.
    /// </summary>
    public void SetKind(SceneKind kind)
    {
        if (InvokeRequired) { Invoke(() => SetKind(kind)); return; }
        _kind = kind;
        _frame = 0;
        if (Still(kind)) _tick.Stop(); else _tick.Start();
        Invalidate();
    }

    /// <summary>
    /// Stops animating and leaves the last painted frame on screen. The comparison in
    /// <see cref="Compare"/> needs a screen that has genuinely changed and then genuinely stopped:
    /// moving, so Desktop Duplication has delivered real pixels rather than the buffer primed from
    /// GDI, and then still, so the two sources are photographing the same moment.
    /// </summary>
    public void Freeze() { if (InvokeRequired) { Invoke(Freeze); return; } _tick.Stop(); }

    // ---- the Desk page's two moving parts, for --hostloop (2026-10-15) -----------------------------
    // A text cursor blinking at Windows' 530 ms — the small change that must NOT keep the host's
    // still-screen sharpening running — and a window opening, the large change that must.
    private readonly System.Windows.Forms.Timer _caretTick = new() { Interval = 530 };
    private bool _caretOn, _dialog;
    private Rectangle _caretAt = new(0, 0, 1, 15);   // set where the search box is drawn
    internal static readonly Rectangle DialogAt = new(420, 260, 520, 300);

    public void Blink(bool on)
    {
        if (InvokeRequired) { Invoke(() => Blink(on)); return; }
        _caretTick.Tick -= OnCaret; _caretTick.Tick += OnCaret;
        _caretTick.Enabled = on;
        if (!on && _caretOn) { _caretOn = false; Invalidate(_caretAt); }
    }
    /// <summary>Where the text cursor is, so a score can leave it out: it blinks between taking the screen and decoding.</summary>
    public Rectangle CaretArea => Rectangle.Inflate(_caretAt, 3, 3);
    private void OnCaret(object? s, EventArgs e) { _caretOn = !_caretOn; Invalidate(_caretAt); }

    public void Dialog(bool on)
    {
        if (InvokeRequired) { Invoke(() => Dialog(on)); return; }
        _dialog = on; Invalidate(DialogAt);
    }
    protected override void OnFormClosed(FormClosedEventArgs e) { _tick.Stop(); _tick.Dispose(); _noise?.Dispose(); _mono.Dispose(); base.OnFormClosed(e); }

    /// <summary>
    /// Scenes that must not move. A "still" scene that repaints on a timer is not still: Desktop
    /// Duplication reports every repaint as a change, so the idle row reads as traffic and the
    /// correctness checks compare two different frames.
    /// </summary>
    private static bool Still(SceneKind k) => k is SceneKind.Idle or SceneKind.Colour or SceneKind.Desk;

    protected override void OnPaint(PaintEventArgs e)
    {
        switch (_kind)
        {
            case SceneKind.Idle:   PaintText(e.Graphics, 0); break;
            case SceneKind.Scroll: PaintText(e.Graphics, _frame * 3); break;
            case SceneKind.Video:  PaintVideo(e.Graphics); break;
            case SceneKind.Colour: PaintColour(e.Graphics); break;
            case SceneKind.Desk:   PaintDesk(e.Graphics); break;
        }
    }

    /// <summary>
    /// Saturated patches, skin tones and a grey ramp, still. See <see cref="SceneKind.Colour"/>.
    ///
    /// The three primaries and their complements are where the two YUV matrices disagree most, the
    /// skin tones are where a human eye notices it first, and the grey ramp is the control: greys
    /// must come back grey under either matrix, so a tint in the ramp is a different fault
    /// entirely -- a range mismatch rather than a matrix one.
    /// </summary>
    private void PaintColour(Graphics g)
    {
        g.Clear(Color.FromArgb(24, 24, 28));
        var patches = new[]
        {
            Color.FromArgb(255, 0, 0),    Color.FromArgb(0, 255, 0),    Color.FromArgb(0, 0, 255),
            Color.FromArgb(0, 255, 255),  Color.FromArgb(255, 0, 255),  Color.FromArgb(255, 255, 0),
            Color.FromArgb(210, 160, 130), Color.FromArgb(150, 95, 70),  Color.FromArgb(240, 200, 175),
            Color.FromArgb(20, 90, 160),  Color.FromArgb(30, 120, 60),  Color.FromArgb(180, 40, 40),
        };
        int cols = 4, rows = 3;
        int pw = Width / cols, ph = (int)(Height * 0.6) / rows;
        for (int i = 0; i < patches.Length; i++)
        {
            using var b = new SolidBrush(patches[i]);
            g.FillRectangle(b, (i % cols) * pw, (i / cols) * ph, pw, ph);
        }
        int rampTop = rows * ph;
        for (int x = 0; x < Width; x++)
        {
            int v = 255 * x / Math.Max(1, Width - 1);
            using var b = new SolidBrush(Color.FromArgb(v, v, v));
            g.FillRectangle(b, x, rampTop, 1, Math.Max(1, Height - rampTop));
        }
        using var ink = new SolidBrush(Color.White);
        g.DrawString("colour check — primaries, complements, skin tones, grey ramp", _mono, ink, 8, rampTop + 8);
    }

    /// <summary>See <see cref="SceneKind.Desk"/>. Pixel-sized fonts, so it is the same page at any display scaling.</summary>
    private void PaintDesk(Graphics g)
    {
        g.Clear(Color.White);
        using var ui = new Font("Segoe UI", 12f, GraphicsUnit.Pixel);
        using var bold = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var head = new Font("Segoe UI", 15f, GraphicsUnit.Pixel);
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

        // Title bar: white on the accent colour.
        using (var bar = new SolidBrush(Color.FromArgb(0, 95, 184))) g.FillRectangle(bar, 0, 0, Width, 30);
        TextRenderer.DrawText(g, "Documents - File Explorer", ui, new Point(36, 8), Color.White, Color.FromArgb(0, 95, 184), flags);
        // Side panel.
        using (var side = new SolidBrush(Color.FromArgb(243, 243, 243))) g.FillRectangle(side, 0, 30, 220, Height - 30);
        string[] places = { "Quick access", "Desktop", "Downloads", "Documents", "Pictures", "This PC", "Local Disk (C:)", "Network" };
        for (int i = 0; i < places.Length; i++)
        {
            int y = 46 + i * 24;
            using (var icon = new SolidBrush(i % 3 == 0 ? Color.FromArgb(255, 196, 0) : i % 3 == 1 ? Color.FromArgb(0, 120, 215) : Color.FromArgb(16, 124, 16)))
                g.FillRectangle(icon, 14, y + 1, 14, 12);
            TextRenderer.DrawText(g, places[i], ui, new Point(36, y), Color.FromArgb(27, 27, 27), Color.FromArgb(243, 243, 243), flags);
        }
        // Column headings and rows of files.
        int x0 = 236;
        TextRenderer.DrawText(g, "Name", ui, new Point(x0 + 24, 40), Color.FromArgb(96, 96, 96), Color.White, flags);
        TextRenderer.DrawText(g, "Date modified", ui, new Point(x0 + 360, 40), Color.FromArgb(96, 96, 96), Color.White, flags);
        TextRenderer.DrawText(g, "Type", ui, new Point(x0 + 520, 40), Color.FromArgb(96, 96, 96), Color.White, flags);
        TextRenderer.DrawText(g, "Size", ui, new Point(x0 + 700, 40), Color.FromArgb(96, 96, 96), Color.White, flags);
        string[] names = { "Quarterly figures 2026.xlsx", "Invoice 10432 - Coastline.pdf", "backup-log.txt", "Scan 2026-10-14 0912.pdf",
                           "Network diagram (v3).vsdx", "Letter to landlord.docx", "IMG_20261012_143355.jpg", "setup-printer.exe" };
        for (int r = 0; r < 26; r++)
        {
            int y = 64 + r * 22;
            if (y > Height - 40) break;
            var n = names[r % names.Length];
            using (var icon = new SolidBrush(r % 4 == 0 ? Color.FromArgb(33, 115, 70) : r % 4 == 1 ? Color.FromArgb(200, 30, 30) : r % 4 == 2 ? Color.FromArgb(43, 87, 154) : Color.FromArgb(120, 120, 120)))
                g.FillRectangle(icon, x0 + 4, y + 2, 12, 12);
            TextRenderer.DrawText(g, n, ui, new Point(x0 + 24, y), Color.FromArgb(27, 27, 27), Color.White, flags);
            TextRenderer.DrawText(g, $"2026/10/{(r % 14) + 1:00} {8 + r % 9:00}:{(r * 7) % 60:00}", ui, new Point(x0 + 360, y), Color.FromArgb(96, 96, 96), Color.White, flags);
            TextRenderer.DrawText(g, Path.GetExtension(n).TrimStart('.').ToUpperInvariant() + " File", ui, new Point(x0 + 520, y), Color.FromArgb(96, 96, 96), Color.White, flags);
            TextRenderer.DrawText(g, $"{(r * 137) % 900 + 12:N0} KB", ui, new Point(x0 + 700, y), Color.FromArgb(96, 96, 96), Color.White, flags);
        }
        // A details pane with coloured status text and links.
        int px = Math.Max(x0 + 800, Width - 360);
        TextRenderer.DrawText(g, "Windows Update", head, new Point(px, 46), Color.FromArgb(27, 27, 27), Color.White, flags);
        TextRenderer.DrawText(g, "You're up to date", bold, new Point(px, 76), Color.FromArgb(16, 124, 16), Color.White, flags);
        TextRenderer.DrawText(g, "Last checked: Today, 09:12", ui, new Point(px, 96), Color.FromArgb(96, 96, 96), Color.White, flags);
        TextRenderer.DrawText(g, "2 updates failed to install (0x80070643)", ui, new Point(px, 120), Color.FromArgb(196, 43, 28), Color.White, flags);
        TextRenderer.DrawText(g, "View update history", ui, new Point(px, 144), Color.FromArgb(0, 95, 184), Color.White, flags);
        TextRenderer.DrawText(g, "Advanced options", ui, new Point(px, 166), Color.FromArgb(0, 95, 184), Color.White, flags);
        // A search box with the text cursor in it.
        using (var box = new Pen(Color.FromArgb(138, 138, 138))) g.DrawRectangle(box, px, 196, 220, 24);
        TextRenderer.DrawText(g, "printer", ui, new Point(px + 6, 201), Color.FromArgb(27, 27, 27), Color.White, flags);
        _caretAt = new Rectangle(px + 7 + TextRenderer.MeasureText(g, "printer", ui, Size.Empty, flags).Width, 200, 1, 16);
        if (_caretOn) using (var c = new SolidBrush(Color.Black)) g.FillRectangle(c, _caretAt);
        // A window opening over the list: a title, small text, two buttons.
        if (_dialog)
        {
            var d = DialogAt;
            using (var face = new SolidBrush(Color.FromArgb(249, 249, 249))) g.FillRectangle(face, d);
            using (var edge = new Pen(Color.FromArgb(0, 95, 184))) g.DrawRectangle(edge, d.X, d.Y, d.Width - 1, d.Height - 1);
            TextRenderer.DrawText(g, "Printer properties - HP LaserJet M404", head, new Point(d.X + 16, d.Y + 14), Color.FromArgb(27, 27, 27), Color.FromArgb(249, 249, 249), flags);
            string[] rows = { "Status: Ready", "Location: Front office, ground floor", "Driver: HP Universal Printing PCL 6 (v7.1.0)",
                              "Port: WSD-4f1c2a90-7b3e", "Paper: A4, tray 2", "Error: toner low (12%)" };
            for (int i = 0; i < rows.Length; i++)
                TextRenderer.DrawText(g, rows[i], ui, new Point(d.X + 16, d.Y + 52 + i * 24),
                    i == rows.Length - 1 ? Color.FromArgb(196, 43, 28) : Color.FromArgb(27, 27, 27), Color.FromArgb(249, 249, 249), flags);
            using (var btn = new SolidBrush(Color.FromArgb(0, 95, 184))) g.FillRectangle(btn, d.Right - 196, d.Bottom - 44, 84, 28);
            TextRenderer.DrawText(g, "OK", ui, new Point(d.Right - 162, d.Bottom - 37), Color.White, Color.FromArgb(0, 95, 184), flags);
            using (var btn2 = new Pen(Color.FromArgb(138, 138, 138))) g.DrawRectangle(btn2, d.Right - 100, d.Bottom - 44, 84, 28);
            TextRenderer.DrawText(g, "Cancel", ui, new Point(d.Right - 78, d.Bottom - 37), Color.FromArgb(27, 27, 27), Color.FromArgb(249, 249, 249), flags);
        }
        // Status bar.
        using (var sb = new SolidBrush(Color.FromArgb(32, 32, 32))) g.FillRectangle(sb, 0, Height - 26, Width, 26);
        TextRenderer.DrawText(g, "26 items    1 item selected  48,2 KB", ui, new Point(10, Height - 20), Color.FromArgb(230, 230, 230), Color.FromArgb(32, 32, 32), flags);
    }

    // A page of text: the case desktop codecs are built for, and the one JPEG tiles are worst at.
    private void PaintText(Graphics g, int scrollPx)
    {
        g.Clear(Color.White);
        int lh = _mono.Height;
        int first = scrollPx / lh, off = -(scrollPx % lh);
        using var ink = new SolidBrush(Color.FromArgb(30, 30, 34));
        using var rule = new SolidBrush(Color.FromArgb(120, 130, 145));
        for (int row = 0; off + row * lh < Height; row++)
        {
            int i = (first + row) % _lines.Length;
            g.DrawString($"{first + row + 1,5}  ", _mono, rule, 4, off + row * lh);
            g.DrawString(_lines[i], _mono, ink, 60, off + row * lh);
        }
    }

    // A stand-in for video: a smooth moving field, plus per-pixel noise so the encoder cannot
    // have it for nothing. See the class comment — this is NOT a film.
    private void PaintVideo(Graphics g)
    {
        int w = Math.Max(1, Width / 4), h = Math.Max(1, Height / 4);   // quarter res, then upscale
        if (_noise == null || _noise.Width != w || _noise.Height != h)
        {
            _noise?.Dispose();
            _noise = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        }
        var d = _noise.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly,
                                System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        unsafe
        {
            // Deterministic: seeded from the frame number, so run N of the bench paints exactly
            // what run N-1 painted.
            uint rng = (uint)(_frame * 2654435761u) | 1u;
            double t = _frame * 0.08;
            for (int y = 0; y < h; y++)
            {
                byte* row = (byte*)d.Scan0 + y * d.Stride;
                for (int x = 0; x < w; x++)
                {
                    double v = Math.Sin(x * 0.05 + t) + Math.Sin(y * 0.06 - t * 0.7)
                             + Math.Sin((x + y) * 0.03 + t * 1.3);
                    rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                    int n = (int)(rng & 0x1F) - 16;
                    row[x * 3 + 0] = Clamp((v + 1.5) * 70 + n);
                    row[x * 3 + 1] = Clamp((v + 2.0) * 55 + n);
                    row[x * 3 + 2] = Clamp((v + 2.5) * 45 + n);
                }
            }
        }
        _noise.UnlockBits(d);
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.DrawImage(_noise, 0, 0, Width, Height);
    }

    private static byte Clamp(double v) => (byte)Math.Clamp(v, 0, 255);

    private static string[] BuildPage()
    {
        // Prose, not lorem ipsum: real text has real word lengths and real spacing, and the tile
        // hasher is sensitive to both.
        var src = new[]
        {
            "The capture loop follows the user to whatever desktop currently has input, because a",
            "thread pinned to Default keeps returning the last picture it could see the moment a",
            "UAC prompt appears. Only SYSTEM may open the Winlogon desktop; elevated is not enough.",
            "",
            "    int cols = (mon.Width + Tile - 1) / Tile, rows = (mon.Height + Tile - 1) / Tile;",
            "    if (!forceFull && st.Hashes![cell] == hash) continue;",
            "",
            "A frozen picture and a live one look identical, and the last frame from a machine that",
            "has gone is the most convincing possible lie a remote view can tell. Veiling it is the",
            "only way the difference is visible at a glance.",
            "",
            "Measure before changing code. When something is reported broken, prove where it breaks",
            "first -- the harnesses exist because \"the screen is black\" turned out to be a rendering",
            "bug, and guessing would have sent the fix to the wrong file.",
            "",
        };
        var page = new List<string>();
        for (int i = 0; i < 12; i++) foreach (var s in src) page.Add(s);
        return page.ToArray();
    }
}
