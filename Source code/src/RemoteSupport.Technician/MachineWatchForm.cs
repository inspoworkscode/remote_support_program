using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Technician;

/// <summary>
/// Processes and Performance for one machine, WITHOUT connecting to it (2026-10-05). Clint: right-
/// click a machine, "Processes" or "Performance", and see it "before even connecting ... just to give
/// one an idea before deciding on connecting or not".
///
/// <para>Not the customer's own Task Manager — that is a window on THEIR screen, and showing it would
/// be a session. These are the console's own two pages, drawn from figures the machine sends once a
/// second (<see cref="MachineWatcher"/>), so nothing appears on the customer's screen. Clint's rules:
/// straight through on every online machine, no prompt and no password; LOOK ONLY — there is no End
/// task, by decision; nothing logged.</para>
///
/// <para>One window per machine, with both pages: the menu item chosen is just the page it opens on.
/// Closing it is what tells the machine to stop measuring.</para>
/// </summary>
public sealed class MachineWatchForm : Form
{
    public enum Page { Processes, Performance }

    private readonly TechLink _link;
    private readonly Theme _t;
    private readonly RoundButton _tabProcesses, _tabPerformance;
    private readonly Panel _processesPage, _performancePage;
    private readonly ListView _list;
    private readonly Label _status, _processesFooter, _performanceFooter;
    private readonly Graph _cpu, _memory, _disk, _network;
    private readonly Dictionary<int, ListViewItem> _rows = new();
    private readonly System.Windows.Forms.Timer _stale;
    private readonly ToolTip _tips = new();
    private int _sortColumn = ColCpu;
    private bool _sortDescending = true;
    private DateTime _lastSample = DateTime.MinValue, _asked;
    private string _machine;

    private const int ColName = 0, ColPid = 1, ColUser = 2, ColCpu = 3, ColMemory = 4;

    public string DeviceId { get; }
    public Page Showing { get; private set; }
    /// <summary>The machine stopped sending, and why, until it is started again.</summary>
    public string? EndedReason { get; private set; }
    /// <summary>The last reading — for the harness, which checks what the window was given.</summary>
    public MachineSample? Last { get; private set; }

    public MachineWatchForm(TechLink link, Theme t, DeviceInfo d, Page page)
    {
        _link = link; _t = t; DeviceId = d.DeviceId; _machine = d.MachineName;
        Text = $"{d.MachineName} — Processes and performance";
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(860, 660);
        MinimumSize = new Size(600, 440);
        BackColor = t.Mist;
        Font = t.Body;
        ShowInTaskbar = true;
        Shown += (_, _) => NativeDark.Apply(this, t);

        int small = TextRenderer.MeasureText("Ag", t.Small).Height;
        int tabH = TextRenderer.MeasureText("Ag", t.ButtonFont).Height + 14;

        // ---- top: who, and whether the figures are live ----
        var top = new Panel { Dock = DockStyle.Top, BackColor = t.Mist, Padding = new Padding(14, 12, 14, 0) };
        var who = new Label
        {
            Dock = DockStyle.Top, Height = TextRenderer.MeasureText("Ag", t.H3).Height + 4, Font = t.H3, ForeColor = t.Ink,
            BackColor = Color.Transparent, UseMnemonic = false, AutoEllipsis = true,
            Text = d.OwnerName == "—" ? d.MachineName : $"{d.MachineName}  ·  {d.OwnerName}"   // whose, as the list says it (2026-10-18)
        };
        _status = new Label { Dock = DockStyle.Top, Height = small + 8, Font = t.Small, ForeColor = t.Muted, BackColor = Color.Transparent, UseMnemonic = false, AutoEllipsis = true };
        var tabs = new Panel { Dock = DockStyle.Top, Height = tabH + 10, BackColor = t.Mist, Padding = new Padding(0, 4, 0, 6) };
        int tabW = new[] { "Processes", "Performance" }.Max(s => TextRenderer.MeasureText(s, t.ButtonFont).Width) + 40;
        _tabProcesses = new TabButton(t, BtnStyle.Accent) { Text = "Processes", Dock = DockStyle.Left, Width = tabW, Height = tabH };
        _tabPerformance = new TabButton(t, BtnStyle.Neutral) { Text = "Performance", Dock = DockStyle.Left, Width = tabW, Height = tabH };
        _tabProcesses.Click += (_, _) => ShowPage(Page.Processes);
        _tabPerformance.Click += (_, _) => ShowPage(Page.Performance);
        tabs.Controls.Add(_tabPerformance);
        tabs.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8, BackColor = Color.Transparent });
        tabs.Controls.Add(_tabProcesses);
        top.Controls.Add(tabs);
        top.Controls.Add(_status);
        top.Controls.Add(who);
        top.Height = top.Padding.Vertical + who.Height + _status.Height + tabs.Height;

        // ---- Processes ----
        _processesPage = new Panel { Dock = DockStyle.Fill, BackColor = t.Mist, Padding = new Padding(14, 4, 14, 12) };
        var listCard = new Card(t, 10) { Dock = DockStyle.Fill };
        _list = new SmoothList().Themed(t);
        _list.Dock = DockStyle.Fill;
        _list.MultiSelect = false;
        // Every width measured from the widest thing it must hold, in the font it is drawn in, heading
        // and sort arrow included (§30): written as numbers, PID and CPU were cut to "117…" at 150%.
        // Name takes whatever is left, so a wider window gives the room to the names.
        using (var headFont = new Font("Segoe UI", 9f, FontStyle.Bold))
        {
            int W(string cell, string head) => Math.Max(TextRenderer.MeasureText(cell, t.Body).Width, TextRenderer.MeasureText(head, headFont).Width) + 24;
            _list.Columns.Add("Name", 200);
            _list.Columns.Add("PID", W("8888888", "PID  ▼"), HorizontalAlignment.Right);
            _list.Columns.Add("User", W("NETWORK SERVICE", "User  ▼"));
            _list.Columns.Add("CPU", W("100.0%", "CPU  ▼"), HorizontalAlignment.Right);
            _list.Columns.Add("Memory", W("8888.8 MB", "Memory  ▼"), HorizontalAlignment.Right);
        }
        _list.Resize += (_, _) => FitNameColumn();
        _list.ListViewItemSorter = new RowOrder(this);
        _list.ColumnClick += (_, e) =>
        {
            // Numbers start biggest first, names A to Z — what anybody clicking CPU or Memory is after.
            if (e.Column == _sortColumn) _sortDescending = !_sortDescending;
            else { _sortColumn = e.Column; _sortDescending = e.Column is ColCpu or ColMemory; }
            _list.Sort();
            ShowSortInHeadings();
        };
        listCard.Controls.Add(_list);
        _processesFooter = new Label { Dock = DockStyle.Bottom, Height = small + 10, Font = t.Small, ForeColor = t.Muted, BackColor = Color.Transparent, UseMnemonic = false, Padding = new Padding(2, 6, 0, 0) };
        _processesPage.Controls.Add(listCard);
        _processesPage.Controls.Add(_processesFooter);

        // ---- Performance ----
        _performancePage = new Panel { Dock = DockStyle.Fill, BackColor = t.Mist, Padding = new Padding(14, 4, 14, 12) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = t.Mist };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        _cpu = new Graph(t, "Processor", t.Blue, percent: true) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 6, 6) };
        _memory = new Graph(t, "Memory", t.Purple, percent: true) { Dock = DockStyle.Fill, Margin = new Padding(6, 0, 0, 6) };
        _disk = new Graph(t, "Disk", t.Green, percent: true) { Dock = DockStyle.Fill, Margin = new Padding(0, 6, 6, 0) };
        _network = new Graph(t, "Network", t.Orange, percent: false) { Dock = DockStyle.Fill, Margin = new Padding(6, 6, 0, 0) };
        grid.Controls.Add(_cpu, 0, 0); grid.Controls.Add(_memory, 1, 0);
        grid.Controls.Add(_disk, 0, 1); grid.Controls.Add(_network, 1, 1);
        _performanceFooter = new Label { Dock = DockStyle.Bottom, Height = small + 10, Font = t.Small, ForeColor = t.Muted, BackColor = Color.Transparent, UseMnemonic = false, Padding = new Padding(2, 6, 0, 0) };
        _performancePage.Controls.Add(grid);
        _performancePage.Controls.Add(_performanceFooter);

        Controls.Add(_processesPage);
        Controls.Add(_performancePage);
        Controls.Add(top);

        // Says so when the figures stop arriving without the server saying why — a slow link, or a
        // machine too busy to answer. A frozen graph that looks live is the one thing this must not be.
        _stale = new System.Windows.Forms.Timer { Interval = 1000 };
        _stale.Tick += (_, _) => ShowLiveness();
        _stale.Start();

        ResizeEnd += (_, _) => { if (WindowState == FormWindowState.Normal) WindowPlacement.Remember("machinewatch", Bounds); };
        Load += (_, _) =>
        {
            if (WindowPlacement.Recall("machinewatch") is { } r) Bounds = WindowPlacement.ClampToScreen(new Rectangle(r.Location, r.Size));
            else CenterToScreen();
        };

        ShowPage(page);
        ShowSortInHeadings();
        Start();
    }

    /// <summary>Asks the machine for its figures — on opening, and again after the console reconnects
    /// or the machine comes back, which is why it is not just the constructor's.</summary>
    public void Start()
    {
        EndedReason = null;
        _asked = DateTime.UtcNow;
        _lastSample = DateTime.MinValue;
        _link.WatchMachine(DeviceId, true);
        ShowLiveness();
    }

    public void ShowPage(Page page)
    {
        Showing = page;
        _processesPage.Visible = page == Page.Processes;
        _performancePage.Visible = page == Page.Performance;
        _tabProcesses.Style = page == Page.Processes ? BtnStyle.Accent : BtnStyle.Neutral;
        _tabPerformance.Style = page == Page.Performance ? BtnStyle.Accent : BtnStyle.Neutral;
        _tabProcesses.Invalidate(); _tabPerformance.Invalidate();
    }

    /// <summary>The machine's name changed in the list (or arrived): keep the title true.</summary>
    public void Rename(string machine) { if (machine.Length > 0 && machine != _machine) { _machine = machine; Text = $"{machine} — Processes and performance"; } }

    /// <summary>The server says the figures have stopped, and why.</summary>
    public void EndWith(string reason)
    {
        EndedReason = reason;
        ShowLiveness();
    }

    public void Apply(MachineSample s)
    {
        if (EndedReason != null) return;
        Last = s;
        _lastSample = DateTime.UtcNow;

        _cpu.Push(s.CpuPercent, $"{s.CpuPercent:0}%", $"{s.LogicalProcessors} logical processor{(s.LogicalProcessors == 1 ? "" : "s")}");
        double memPct = s.MemoryTotalMb > 0 ? 100.0 * s.MemoryUsedMb / s.MemoryTotalMb : 0;
        _memory.Push(memPct, $"{memPct:0}%", $"{Gb(s.MemoryUsedMb)} of {Gb(s.MemoryTotalMb)} in use");
        if (s.DiskActivePercent >= 0)
            _disk.Push(s.DiskActivePercent, $"{s.DiskActivePercent:0}%", $"Active time  ·  read {Rate(s.DiskReadBytesPerSec)}, write {Rate(s.DiskWriteBytesPerSec)}");
        else
            _disk.Push(0, "—", "Windows on that machine gave no disk figures");
        long net = s.NetReceiveBytesPerSec + s.NetSendBytesPerSec;
        _network.Push(net, Rate(net), $"Receiving {Rate(s.NetReceiveBytesPerSec)}  ·  sending {Rate(s.NetSendBytesPerSec)}");
        if (s.Networks != null)
        {
            var lines = NetworkLines(s.Networks, _t);
            _network.SetNotes(lines);
            // A narrow card cuts the network's name short; hovering shows every line whole.
            var full = string.Join("\n", lines.Select(l => l.Text));
            if (_tips.GetToolTip(_network) != full) _tips.SetToolTip(_network, full);
        }
        _performanceFooter.Text = $"Up {Uptime(s.UptimeSeconds)}   ·   {s.ProcessCount:N0} processes   ·   {s.ThreadCount:N0} threads";

        if (s.Processes != null) ApplyProcesses(s.Processes);
        _processesFooter.Text = $"{s.ProcessCount:N0} processes   ·   {s.ThreadCount:N0} threads   ·   click a heading to sort";
        ShowLiveness();
    }

    /// <summary>Rows are kept and updated in place, so the selection and the scroll position survive
    /// every refresh — a list rebuilt each second would jump under the technician's eye.</summary>
    private void ApplyProcesses(List<ProcessSample> procs)
    {
        _list.BeginUpdate();
        try
        {
            var seen = new HashSet<int>();
            foreach (var p in procs)
            {
                seen.Add(p.Pid);
                if (!_rows.TryGetValue(p.Pid, out var row))
                {
                    row = new ListViewItem(new[] { "", "", "", "", "" });
                    _rows[p.Pid] = row;
                    _list.Items.Add(row);
                }
                row.Tag = p;
                Set(row, ColName, p.Name);
                Set(row, ColPid, p.Pid.ToString());
                Set(row, ColUser, p.User);
                Set(row, ColCpu, p.CpuPercent >= 0.05 ? $"{p.CpuPercent:0.0}%" : "0%");
                Set(row, ColMemory, Mb(p.MemoryKb));
            }
            foreach (var gone in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _list.Items.Remove(_rows[gone]);
                _rows.Remove(gone);
            }
            _list.Sort();
        }
        finally { _list.EndUpdate(); }

        static void Set(ListViewItem row, int col, string text) { if (row.SubItems[col].Text != text) row.SubItems[col].Text = text; }
    }

    private void ShowLiveness()
    {
        string text; Color colour = _t.Muted;
        if (EndedReason != null) { text = EndedReason + "  The figures shown are the last ones received."; colour = _t.WarnText; }
        else if (_lastSample == DateTime.MinValue)
        {
            int waited = (int)(DateTime.UtcNow - _asked).TotalSeconds;
            text = waited < 6 ? $"Asking {_machine} for its figures…" : $"{_machine} has not answered for {waited} seconds — it may be very busy, or its connection slow.";
            if (waited >= 6) colour = _t.WarnText;
        }
        else
        {
            int age = (int)(DateTime.UtcNow - _lastSample).TotalSeconds;
            if (age <= 4) text = "Live, every second.  Look only — nothing on the machine can be changed from here.";
            else { text = $"No figures for {age} seconds — the machine may be very busy, or its connection slow."; colour = _t.WarnText; }
        }
        if (_status.Text != text) _status.Text = text;
        if (_status.ForeColor != colour) _status.ForeColor = colour;
    }

    private void FitNameColumn()
    {
        int others = 0;
        for (int i = 1; i < _list.Columns.Count; i++) others += _list.Columns[i].Width;
        int room = _list.ClientSize.Width - others - 4;
        int least = TextRenderer.MeasureText("svchost.exe", _t.Body).Width + 24;
        _list.Columns[ColName].Width = Math.Max(least, room);
    }

    private void ShowSortInHeadings()
    {
        string[] names = { "Name", "PID", "User", "CPU", "Memory" };
        for (int i = 0; i < names.Length; i++)
            _list.Columns[i].Text = i == _sortColumn ? names[i] + (_sortDescending ? "  ▼" : "  ▲") : names[i];
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _stale.Stop(); _stale.Dispose(); _tips.Dispose();
        // The machine measures only while somebody is looking; this is the "nobody is" for this console.
        _link.WatchMachine(DeviceId, false);
        base.OnFormClosed(e);
    }

    // ------------------------------------------------------------------ words

    /// <summary>
    /// One line per connected network: adapter, name, Private / Public / Domain, address. Clint:
    /// "this can solve quite a few problems at a glance" — Public is amber, with why it matters,
    /// because a network Windows calls Public has file sharing and discovery off by default and
    /// nothing else on the screen would say so.
    /// </summary>
    internal static List<(string Text, Color Colour)> NetworkLines(List<NetworkProfile> nets, Theme t)
    {
        if (nets.Count == 0) return new() { ("Not connected to any network", t.WarnText) };
        return nets.Select(n =>
        {
            // What matters first — Private or Public, then the address — and the adapter and name last,
            // where a narrow card cuts them short: at the default width the address used to be the
            // part lost to the "…" (watchprobe's picture).
            var parts = new List<string> { n.Category == "Public" ? "Public network, sharing off" : $"{n.Category} network" };
            if (n.Address.Length > 0) parts.Add(n.Address);
            var where = string.Join(" ", new[] { n.Adapter, n.Name.Length > 0 ? $"“{n.Name}”" : "" }.Where(x => x.Length > 0));
            if (where.Length > 0) parts.Add(where);
            return (string.Join("  ·  ", parts), n.Category == "Public" ? t.WarnText : t.Ink);
        }).ToList();
    }

    private static string Gb(long mb) => mb >= 1024 ? $"{mb / 1024.0:0.0} GB" : $"{mb} MB";
    private static string Mb(long kb) => kb >= 1024 * 1024 ? $"{kb / (1024.0 * 1024):0.00} GB" : $"{kb / 1024.0:0.0} MB";
    private static string Rate(long bytesPerSec) =>
        bytesPerSec >= 1024L * 1024 * 1024 ? $"{bytesPerSec / (1024.0 * 1024 * 1024):0.0} GB/s"
        : bytesPerSec >= 1024 * 1024 ? $"{bytesPerSec / (1024.0 * 1024):0.0} MB/s"
        : $"{bytesPerSec / 1024.0:0} KB/s";
    private static string Uptime(long secs)
    {
        var t = TimeSpan.FromSeconds(secs);
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays} day{((int)t.TotalDays == 1 ? "" : "s")} {t.Hours} h";
        if (t.TotalHours >= 1) return $"{t.Hours} h {t.Minutes} min";
        return $"{t.Minutes} min";
    }

    // ------------------------------------------------------------------ parts

    /// <summary>The list's order, by the heading last clicked; ties broken by name, then PID, so rows
    /// with equal figures do not swap places every second.</summary>
    private sealed class RowOrder : System.Collections.IComparer
    {
        private readonly MachineWatchForm _f;
        public RowOrder(MachineWatchForm f) { _f = f; }
        public int Compare(object? x, object? y)
        {
            if ((x as ListViewItem)?.Tag is not ProcessSample a || (y as ListViewItem)?.Tag is not ProcessSample b) return 0;
            int c = _f._sortColumn switch
            {
                ColPid => a.Pid.CompareTo(b.Pid),
                ColUser => string.Compare(a.User, b.User, StringComparison.OrdinalIgnoreCase),
                ColCpu => a.CpuPercent.CompareTo(b.CpuPercent),
                ColMemory => a.MemoryKb.CompareTo(b.MemoryKb),
                _ => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            };
            if (_f._sortDescending) c = -c;
            if (c == 0) c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.Pid.CompareTo(b.Pid);
        }
    }

    /// <summary>
    /// The console's rounded button, with the square around its rounding filled in the colour of what
    /// it sits on first. On this window the corners outside the rounding came out BLACK — the button
    /// draws into an off-screen buffer and nothing painted those pixels — measured by watchprobe,
    /// which reads the corner pixel off the screen. The dashboard's buttons are not affected.
    /// </summary>
    private sealed class TabButton : RoundButton
    {
        public TabButton(Theme t, BtnStyle style) : base(t, style) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            var under = Parent?.BackColor is { } c && c != Color.Transparent ? c : FindForm()?.BackColor ?? Color.Transparent;
            using (var b = new SolidBrush(under)) e.Graphics.FillRectangle(b, ClientRectangle);
            base.OnPaint(e);
        }
    }

    /// <summary>A ListView that paints off-screen first, so updating 300 rows a second does not flicker.</summary>
    private sealed class SmoothList : ListView { public SmoothList() { DoubleBuffered = true; } }

    /// <summary>
    /// One of the four Performance cards: its name, the figure now, a line under it, and the last
    /// minute as a filled line. Percentages are drawn against 0-100; the network against the biggest
    /// figure in the minute, as Task Manager does.
    /// </summary>
    internal sealed class Graph : Control
    {
        private const int Points = 60;
        private readonly Theme _t;
        private readonly string _title;
        private readonly Color _colour;
        private readonly bool _percent;
        private readonly double[] _values = new double[Points];
        private int _count;
        public string Value { get; private set; } = "—";
        public string Detail { get; private set; } = "";
        /// <summary>Lines under the detail, each in its own colour — the Network card's networks.</summary>
        public IReadOnlyList<(string Text, Color Colour)> Notes { get; private set; } = Array.Empty<(string, Color)>();

        public void SetNotes(List<(string Text, Color Colour)> notes)
        {
            if (notes.SequenceEqual(Notes)) return;
            Notes = notes;
            Invalidate();
        }

        public Graph(Theme t, string title, Color colour, bool percent)
        {
            _t = t; _title = title; _colour = colour; _percent = percent;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        public void Push(double v, string value, string detail)
        {
            Array.Copy(_values, 1, _values, 0, Points - 1);
            _values[Points - 1] = v;
            _count = Math.Min(Points, _count + 1);
            Value = value; Detail = detail;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.Smooth(g);
            var card = new Rectangle(0, 0, Width - 1, Height - 1);
            if (card.Width <= 20 || card.Height <= 20) return;
            using (var path = Theme.RoundedPath(card, _t.RadiusCard))
            using (var fill = new SolidBrush(_t.CardBg))
            using (var pen = new Pen(_t.CardBorder))
            { g.FillPath(fill, path); g.DrawPath(pen, path); }

            // Every height from its font (§30).
            int pad = 12;
            int titleH = TextRenderer.MeasureText("Ag", _t.H3).Height;
            int valueH = TextRenderer.MeasureText("Ag", _t.H2).Height;
            int detailH = TextRenderer.MeasureText("Ag", _t.Small).Height;
            var valueSize = TextRenderer.MeasureText(Value, _t.H2);
            var valueRect = new Rectangle(card.Right - pad - valueSize.Width, pad, valueSize.Width, valueH);
            TextRenderer.DrawText(g, _title, _t.H3, new Rectangle(pad, pad + (valueH - titleH) / 2, Math.Max(0, valueRect.Left - pad - 6), titleH), _t.Ink, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Value, _t.H2, valueRect, _t.Ink, TextFormatFlags.Right);
            int detailTop = pad + valueH + 2;
            TextRenderer.DrawText(g, Detail, _t.Small, new Rectangle(pad, detailTop, card.Width - pad * 2, detailH), _t.Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            int below = detailTop + detailH;
            foreach (var (text, colour) in Notes)
            {
                TextRenderer.DrawText(g, text, _t.Small, new Rectangle(pad, below + 2, card.Width - pad * 2, detailH), colour, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                below += detailH + 2;
            }

            var plot = new Rectangle(pad, below + 8, card.Width - pad * 2, card.Bottom - pad - (below + 8));
            if (plot.Width < 10 || plot.Height < 10) return;
            using (var bg = new SolidBrush(_t.Surface)) g.FillRectangle(bg, plot);
            using (var grid = new Pen(_t.CardBorder))
            {
                for (int k = 1; k < 4; k++) { int y = plot.Top + plot.Height * k / 4; g.DrawLine(grid, plot.Left, y, plot.Right, y); }
                g.DrawRectangle(grid, plot);
            }
            if (_count < 2) return;

            double top = _percent ? 100 : Math.Max(1, _values.Skip(Points - _count).Max() * 1.15);
            var pts = new List<PointF>();
            for (int k = Points - _count; k < Points; k++)
            {
                float x = plot.Left + (float)plot.Width * k / (Points - 1);
                float y = plot.Bottom - (float)(Math.Clamp(_values[k] / top, 0, 1) * plot.Height);
                pts.Add(new PointF(x, y));
            }
            var area = new List<PointF>(pts) { new(pts[^1].X, plot.Bottom), new(pts[0].X, plot.Bottom) };
            using (var fill = new SolidBrush(Color.FromArgb(60, _colour))) g.FillPolygon(fill, area.ToArray());
            using (var line = new Pen(_colour, 2f) { LineJoin = LineJoin.Round }) g.DrawLines(line, pts.ToArray());
        }
    }
}
