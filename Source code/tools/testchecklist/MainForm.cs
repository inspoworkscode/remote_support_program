using System.Diagnostics;

namespace TestChecklist;

sealed class MainForm : Form
{
    public Store Store { get; }
    public string Me { get; private set; }
    public Board Board { get; private set; }
    public CheckBook Book { get; private set; }
    public Styles Styles { get; }

    DateTime _bookStamp;

    readonly Label _who = new() { AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 0, 4, 0) };
    readonly LinkLabel _change = new() { Text = "change", AutoSize = true, Anchor = AnchorStyles.Left };
    readonly Label _summary = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText, Padding = new Padding(16, 0, 0, 0) };
    readonly Button _refresh = new() { Text = "Refresh", AutoSize = true };
    readonly Button _export = new() { Text = "Export report…", AutoSize = true };
    readonly ToolTip _tip = new();

    readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    readonly TabPage _checksPage = new("Checks");
    readonly TabPage _faultsPage = new("Faults");
    readonly TabPage _upgradesPage = new("Upgrades");

    readonly SplitContainer _outer = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
    readonly SplitContainer _inner = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
    readonly ListBox _sections = new() { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, IntegralHeight = false, BorderStyle = BorderStyle.None };
    readonly ComboBox _show = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    readonly TextBox _search = new() { Width = 240, PlaceholderText = "Search every check…" };
    readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false,
        MultiSelect = true, ShowItemToolTips = true,
    };
    readonly RichTextBox _detail = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, BackColor = SystemColors.Window, BorderStyle = BorderStyle.None,
        DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical,
    };
    readonly TextBox _note = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true,
        PlaceholderText = "What you saw (optional) — type it, then press the result below. It is saved with it.",
    };
    readonly TableLayoutPanel _detailGrid = new() { Dock = DockStyle.Fill, ColumnCount = 1 };
    readonly FlowLayoutPanel _buttons = new() { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
    readonly List<Button> _resultButtons = new();

    readonly ItemsPanel _faults;
    readonly ItemsPanel _upgrades;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 15000 };

    string? _detailSig;
    bool _filling;

    static readonly string[] ShowOptions = { "All", "Not tested", "Works", "Fault or partly", "Can't test yet", "Skipped" };

    sealed class SectionRow
    {
        public Section? S;
        public override string ToString() => S?.Title ?? "All checks";
    }

    public MainForm(string root, string me)
    {
        Store = new Store(root);
        Me = me;
        Store.SetMe(me);
        Book = CheckBook.Load(Store.ChecksPath);
        _bookStamp = File.GetLastWriteTimeUtc(Store.ChecksPath);
        Store.Reload(true);
        Board = new Board(Book, Store.Entries);

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.75f);
        Styles = new Styles(Font);
        Text = "Test Checklist — The Remote Support Program";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1280, 820);
        MinimumSize = new Size(900, 600);

        // ---- top bar -----------------------------------------------------------------------
        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 6, Padding = new Padding(8, 6, 8, 6) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(_who, 0, 0);
        top.Controls.Add(_change, 1, 0);
        top.Controls.Add(_summary, 2, 0);
        top.Controls.Add(_refresh, 3, 0);
        top.Controls.Add(_export, 4, 0);
        _who.Font = Styles.Bold;
        _change.LinkClicked += (_, _) => ChangeMe();
        _refresh.Click += (_, _) => Reload(true);
        _export.Click += (_, _) => DoExport();
        _tip.SetToolTip(_refresh, "Everyone's results are picked up by themselves every 15 seconds; this does it now.");

        // ---- checks tab --------------------------------------------------------------------
        var filterRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(0, 2, 0, 4) };
        filterRow.Controls.Add(new Label { Text = "Show", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        filterRow.Controls.Add(_show);
        filterRow.Controls.Add(new Label { Text = "   ", AutoSize = true });
        filterRow.Controls.Add(_search);
        _show.Items.AddRange(ShowOptions);
        _show.SelectedIndex = 0;

        var listGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        listGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        listGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        listGrid.Controls.Add(filterRow, 0, 0);
        listGrid.Controls.Add(_list, 0, 1);

        _list.Columns.Add("#", 50, HorizontalAlignment.Right);
        _list.Columns.Add("Check", 600);
        _list.Columns.Add("Result", 120);
        _list.Columns.Add("By", 110);
        _list.Columns.Add("When", 120);

        foreach (var r in Results.All)
        {
            var b = new Button { Text = r.Name, AutoSize = true, Tag = r.Code, MinimumSize = new Size(96, 0), Margin = new Padding(0, 4, 6, 0) };
            if (r.Back != Color.Empty) { b.BackColor = r.Back; b.ForeColor = r.Fore; b.UseVisualStyleBackColor = false; b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderColor = r.Fore; }
            b.Text = r.Code == "untested" ? "Clear (not tested)" : r.Name;
            b.Click += (_, _) => Mark(r.Code);
            _resultButtons.Add(b);
        }
        // Results left to right in the order people reach for them; "clear" last.
        foreach (var b in _resultButtons.Skip(1)) _buttons.Controls.Add(b);
        _buttons.Controls.Add(_resultButtons[0]);

        _detailGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _detailGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));   // measured in OnLoad
        _detailGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _detailGrid.RowCount = 3;
        _detailGrid.Controls.Add(_detail, 0, 0);
        _detailGrid.Controls.Add(_note, 0, 1);
        _detailGrid.Controls.Add(_buttons, 0, 2);

        _inner.Panel1.Controls.Add(listGrid);
        _inner.Panel2.Controls.Add(_detailGrid);
        _inner.Panel2.Padding = new Padding(0, 4, 0, 0);
        _outer.Panel1.Controls.Add(_sections);
        _outer.Panel2.Controls.Add(_inner);
        _outer.Panel2.Padding = new Padding(6, 0, 0, 0);
        _checksPage.Controls.Add(_outer);
        _checksPage.Padding = new Padding(6);

        _faults = new ItemsPanel(this, Kinds.Fault) { Dock = DockStyle.Fill };
        _upgrades = new ItemsPanel(this, Kinds.Upgrade) { Dock = DockStyle.Fill };
        _faultsPage.Controls.Add(_faults);
        _upgradesPage.Controls.Add(_upgrades);
        _faultsPage.Padding = _upgradesPage.Padding = new Padding(6);
        _tabs.TabPages.AddRange(new[] { _checksPage, _faultsPage, _upgradesPage });

        Controls.Add(_tabs);
        Controls.Add(top);

        _sections.DrawItem += DrawSection;
        _sections.SelectedIndexChanged += (_, _) => { if (!_filling) { _search.Text = ""; FillList(); ShowDetail(); } };
        _show.SelectedIndexChanged += (_, _) => { FillList(); ShowDetail(); };
        _search.TextChanged += (_, _) => { FillList(); ShowDetail(); };
        _list.SelectedIndexChanged += (_, _) => { if (!_filling) ShowDetail(); };
        _list.Resize += (_, _) => FitColumns();
        _timer.Tick += (_, _) => Reload(false);

        Ui.Tidy(this);
        ResumeLayout(false);
        PerformLayout();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // 1280x820 is 1920x1230 at 150% — taller than a 1080p or 1200p screen, which would put the
        // result buttons below the taskbar. Never bigger than the screen it opens on.
        var work = Screen.FromControl(this).WorkingArea;
        Size = new Size(Math.Min(Width, work.Width * 95 / 100), Math.Min(Height, work.Height * 95 / 100));
        Location = new Point(work.X + (work.Width - Width) / 2, work.Y + (work.Height - Height) / 2);

        Measure();
        _outer.SplitterDistance = LogicalToDeviceUnits(330);
        _inner.SplitterDistance = Math.Max(_inner.Panel1MinSize, (int)(_inner.Height * 0.52));
        FillSections();
        _sections.SelectedIndex = 0;
        RefreshAll();
        _timer.Start();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Measure();
    }

    /// Heights that cannot be AutoSize are measured from the font they show (HANDOVER §30).
    void Measure()
    {
        int line = TextRenderer.MeasureText("Ag", _note.Font).Height;
        _detailGrid.RowStyles[1].Height = line * 3 + LogicalToDeviceUnits(10);
        _sections.ItemHeight = TextRenderer.MeasureText("Ag", _sections.Font).Height + LogicalToDeviceUnits(10);
    }

    // ---- data ----------------------------------------------------------------------------

    public void Append(Entry e)
    {
        if (e.Id.Length == 0) e.Id = Guid.NewGuid().ToString("N");
        e.At = DateTime.UtcNow;
        e.By = Me;
        e.Machine = Environment.MachineName;
        try { Store.Append(e); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "That could not be saved to the shared drive:\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public void Reload(bool force)
    {
        bool changed = false;
        try { changed = Store.Reload(force); } catch (Exception) when (!force) { return; }

        try
        {
            var stamp = File.GetLastWriteTimeUtc(Store.ChecksPath);
            if (stamp != _bookStamp)
            {
                Book = CheckBook.Load(Store.ChecksPath);
                _bookStamp = stamp;
                changed = true;
                FillSections();
            }
        }
        catch (IOException) { }

        if (!changed && !force) return;
        Board = new Board(Book, Store.Entries);
        RefreshAll();
    }

    void RefreshAll()
    {
        UpdateTop();
        _sections.Invalidate();
        FillList();
        ShowDetail();
        _faults.Fill();
        _upgrades.Fill();
        int f = Board.ItemsOf(Kinds.Fault).Count(i => i.IsOpen), u = Board.ItemsOf(Kinds.Upgrade).Count(i => i.IsOpen);
        _faultsPage.Text = f > 0 ? $"Faults ({f} open)" : "Faults";
        _upgradesPage.Text = u > 0 ? $"Upgrades ({u} open)" : "Upgrades";
    }

    void UpdateTop()
    {
        _who.Text = "Testing as " + Me;
        var all = Board.Checks.Values.ToList();
        string Count(string code) => all.Count(c => c.Status == code).ToString();
        _summary.Text = $"{all.Count} checks:  {Count("works")} work · {Count("fault")} fault · {Count("partly")} partly · " +
                        $"{Count("blocked")} can't test yet · {Count("skip")} skipped · {Count("untested")} not tested";
        _tip.SetToolTip(_summary, $"Checklist: {Book.Title}\nchecks.md last updated {File.GetLastWriteTime(Store.ChecksPath):d MMM yyyy HH:mm}\nShared folder: {Store.Root}");
    }

    void ChangeMe()
    {
        var name = Identity.Ask(this, Me);
        if (name == null) return;
        Me = name;
        Store.SetMe(name);
        UpdateTop();
    }

    // ---- sections --------------------------------------------------------------------------

    void FillSections()
    {
        var was = (_sections.SelectedItem as SectionRow)?.S?.Key;
        _filling = true;
        _sections.BeginUpdate();
        _sections.Items.Clear();
        _sections.Items.Add(new SectionRow());
        foreach (var s in Book.Sections) _sections.Items.Add(new SectionRow { S = s });
        _sections.EndUpdate();
        if (was != null)
            for (int i = 0; i < _sections.Items.Count; i++)
                if (((SectionRow)_sections.Items[i]).S?.Key == was) _sections.SelectedIndex = i;
        _filling = false;
    }

    void DrawSection(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        e.DrawBackground();
        var row = (SectionRow)_sections.Items[e.Index];
        var checks = row.S?.Checks ?? Book.All.ToList();
        bool selected = (e.State & DrawItemState.Selected) != 0;
        int pad = LogicalToDeviceUnits(8);

        string count = "";
        Color countColor = selected ? SystemColors.HighlightText : SystemColors.GrayText;
        if (checks.Count > 0)
        {
            var states = checks.Select(c => Board.Checks[c.Number]).ToList();
            int done = states.Count(s => s.Status != "untested");
            count = $"{done}/{checks.Count}";
            if (!selected)
            {
                if (states.Any(s => s.Status is "fault" or "partly")) countColor = Results.Fore("fault");
                else if (done == checks.Count) countColor = Results.Fore("works");
            }
            if (done == checks.Count) count = "✓ " + count;
        }

        var font = row.S == null ? Styles.Bold : checks.Count == 0 ? Styles.Italic : _sections.Font;
        var fore = selected ? SystemColors.HighlightText : checks.Count == 0 ? SystemColors.GrayText : SystemColors.ControlText;
        var r = e.Bounds;
        int cw = count.Length > 0 ? TextRenderer.MeasureText(count, _sections.Font).Width : 0;
        TextRenderer.DrawText(e.Graphics, count, _sections.Font, new Rectangle(r.Right - cw - pad, r.Y, cw, r.Height), countColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, row.ToString(), font, new Rectangle(r.X + pad, r.Y, r.Width - cw - 3 * pad, r.Height), fore,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    // ---- the list of checks ----------------------------------------------------------------

    List<string> SelectedNumbers() => _list.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToList();

    bool Passes(CheckState s) => _show.SelectedIndex switch
    {
        1 => s.Status == "untested",
        2 => s.Status == "works",
        3 => s.Status is "fault" or "partly",
        4 => s.Status == "blocked",
        5 => s.Status == "skip",
        _ => true,
    };

    static bool Matches(Check c, string q) =>
        c.Number == q || string.Join(" ", c.Lines).Contains(q, StringComparison.OrdinalIgnoreCase);

    void FillList()
    {
        var keep = SelectedNumbers();
        string q = _search.Text.Trim();
        var row = _sections.SelectedItem as SectionRow;
        var sections = q.Length > 0 || row?.S == null ? Book.Sections : new List<Section> { row.S };

        _filling = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        _list.Groups.Clear();
        foreach (var sec in sections)
        {
            ListViewGroup? g = null;
            foreach (var c in sec.Checks)
            {
                var st = Board.Checks[c.Number];
                if (!Passes(st) || (q.Length > 0 && !Matches(c, q))) continue;
                if (g == null) { g = new ListViewGroup(sec.Title); _list.Groups.Add(g); }
                string when = st.When is DateTime w ? (w.Date == DateTime.Today ? w.ToString("HH:mm") : w.ToString("d MMM HH:mm")) : "";
                if (st.Latest == null && st.Check.DoneInDoc != null) when = st.Check.DoneInDoc;
                var it = new ListViewItem(new[] { c.Number, c.Short, Results.Name(st.Status), st.By, when }) { Tag = c.Number, Group = g };
                it.ToolTipText = st.Latest?.Note ?? st.DocNote ?? "";
                if (st.Status != "untested") { it.BackColor = Results.Back(st.Status); }
                if (st.Status == "skip") it.ForeColor = Results.Fore("skip");
                if (keep.Contains(c.Number)) it.Selected = true;
                _list.Items.Add(it);
            }
        }
        _list.EndUpdate();
        if (_list.SelectedItems.Count > 0) _list.SelectedItems[0].EnsureVisible();
        _filling = false;
        FitColumns();
    }

    void FitColumns()
    {
        if (_list.Columns.Count < 5) return;
        int others = _list.Columns[0].Width + _list.Columns[2].Width + _list.Columns[3].Width + _list.Columns[4].Width;
        int w = _list.ClientSize.Width - others - SystemInformation.VerticalScrollBarWidth;
        if (w > 150) _list.Columns[1].Width = w;
    }

    // ---- the detail pane -------------------------------------------------------------------

    void ShowDetail()
    {
        var nums = SelectedNumbers();
        var row = _sections.SelectedItem as SectionRow;
        foreach (var b in _resultButtons) b.Enabled = nums.Count > 0;

        // Only redraw when something shown has changed, so a refresh never jumps the scroll.
        string sig;
        if (nums.Count == 1)
        {
            var st = Board.Checks[nums[0]];
            sig = "c" + nums[0] + ":" + st.History.Count + ":" + string.Join(",", Board.ItemsForCheck(nums[0]).Select(i => i.Code + i.Status)) + _bookStamp.Ticks;
        }
        else if (nums.Count > 1) sig = "m" + string.Join(",", nums);
        else sig = "s" + (row?.S?.Key ?? "*") + _bookStamp.Ticks;
        if (sig == _detailSig) return;
        _detailSig = sig;

        var w = new RichWriter(_detail, Styles);
        if (nums.Count == 1) WriteCheck(w, Board.Checks[nums[0]]);
        else if (nums.Count > 1)
        {
            w.Heading($"{nums.Count} checks selected");
            w.Line("Press a result below to give all of them the same one, with the same note.", null, SystemColors.GrayText);
        }
        else if (row?.S != null)
        {
            w.Heading(row.S.Title);
            w.Gap();
            if (row.S.Intro.Count > 0) w.Markdown(row.S.Intro);
            if (row.S.Checks.Count > 0) { w.Gap(); w.Line("Pick a check in the list above.", Styles.Italic, SystemColors.GrayText); }
        }
        else
        {
            w.Heading("How to use this");
            w.Gap();
            w.Markdown(new[]
            {
                "1.  Pick a **section** on the left and a **check** in the list. Its full description shows here.",
                "",
                "2.  Try it. Type what you saw in the box below (optional), then press **Works**, **Partly works**, **Fault**, **Can't test yet** or **Skip**.",
                "",
                "3.  Something wrong? Press **Fault** and say yes to logging it — or use the **Faults** tab. Ideas for improvements go on the **Upgrades** tab.",
                "",
                "4.  Everyone's results appear here within 15 seconds. Several people can have it open at once.",
                "",
                "5.  At the end of the day, **Export report…** writes one file with everything done that day. Give it to Claude.",
            });
        }
        _detail.SelectionStart = 0;
        _detail.ScrollToCaret();
    }

    void WriteCheck(RichWriter w, CheckState st)
    {
        var c = st.Check;
        w.Write($"Check {c.Number}   ", Styles.Heading);
        w.Line(" " + Results.Name(st.Status) + " ", Styles.Bold, st.Status == "untested" ? SystemColors.GrayText : Results.Fore(st.Status));
        w.Line(c.Section.Title, Styles.Small, SystemColors.GrayText);
        w.Gap();
        w.Markdown(c.Lines);

        var items = Board.ItemsForCheck(c.Number).ToList();
        if (items.Count > 0)
        {
            w.Gap();
            w.Line("Logged against this check", Styles.Bold);
            foreach (var i in items)
                w.Line($"   {i.Code}  {i.Title}  —  {i.Status}", null, i.IsOpen ? Results.Fore("fault") : SystemColors.GrayText);
        }

        w.Gap();
        w.Line("What people found", Styles.Bold);
        if (st.History.Count == 0)
            w.Line("   " + (st.DocNote ?? "Nobody has marked this one yet."), Styles.Italic, SystemColors.GrayText);
        foreach (var e in Enumerable.Reverse(st.History))
        {
            var at = e.At.ToLocalTime();
            w.Write($"   {at:ddd d MMM HH:mm}   {e.By}   ", null, SystemColors.GrayText);
            w.Line(Results.Name(e.Status!), Styles.Bold, Results.Fore(e.Status!) is var f && f != Color.Empty ? f : null);
            if (!string.IsNullOrWhiteSpace(e.Note))
                foreach (var l in e.Note.Split('\n'))
                    w.Line("         " + l.TrimEnd('\r'));
        }

        if (c.Section.Intro.Count > 0)
        {
            w.Gap();
            w.Line("About this section", Styles.Bold, SystemColors.GrayText);
            w.Markdown(c.Section.Intro, SystemColors.GrayText);
        }
    }

    // ---- marking a result ------------------------------------------------------------------

    void Mark(string code)
    {
        var nums = SelectedNumbers();
        if (nums.Count == 0) return;
        string note = _note.Text.Trim().Replace("\r\n", "\n");

        foreach (var n in nums)
            Append(new Entry { Type = "result", Check = n, Status = code, Note = note.Length > 0 ? note : null });
        _note.Clear();
        _detailSig = null;
        Reload(true);

        if (code is "fault" or "partly" && nums.Count == 1)
        {
            var c = Book.ByNumber[nums[0]];
            var ask = MessageBox.Show(this,
                $"Log this on the Faults tab as well?\n\nThat is what Claude fixes from — give it a title and, if you can, a screenshot.",
                "Check " + c.Number, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (ask == DialogResult.Yes)
                NewItem(Kinds.Fault, c.Number, $"Check {c.Number}: {c.Short}", note);
        }
    }

    public void NewItem(string kind, string? check = null, string? title = null, string? details = null)
    {
        using var d = new ItemDialog(kind, Book, AreasInUse(), null, check, title, details);
        if (d.ShowDialog(this) != DialogResult.OK) return;

        Reload(true);   // the freshest possible view of the numbers already taken
        var id = Guid.NewGuid().ToString("N");
        List<string> pics;
        try { pics = d.SavePictures(Store.PicturesDir, id); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The pictures could not be saved:\n\n" + ex.Message, Text);
            pics = new List<string>();
        }
        var e = new Entry
        {
            Id = id, Type = "item", Kind = kind, Seq = Board.NextSeq(kind), Title = d.TitleText, Details = d.DetailsText.Replace("\r\n", "\n"),
            Area = d.AreaText, Level = d.LevelText, Check = d.CheckText, Pictures = pics.Count > 0 ? pics : null,
        };
        Append(e);
        Reload(true);
        _tabs.SelectedTab = kind == Kinds.Fault ? _faultsPage : _upgradesPage;
        (kind == Kinds.Fault ? _faults : _upgrades).ShowItem(e.Id);
    }

    public IEnumerable<string> AreasInUse() => Board.Items.Values.Select(i => i.Area).Where(a => a.Length > 0);

    // ---- export ----------------------------------------------------------------------------

    void DoExport()
    {
        Reload(true);
        using var d = new ExportDialog();
        if (d.ShowDialog(this) != DialogResult.OK) return;

        Directory.CreateDirectory(Store.ExportsDir);
        using var save = new SaveFileDialog
        {
            InitialDirectory = Store.ExportsDir,
            FileName = $"Test report {d.Label}.md",
            Filter = "Report (*.md)|*.md",
            OverwritePrompt = true,
        };
        if (save.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var r = Exporter.Write(this, save.FileName, d.From, d.To, d.Label);
            var ask = MessageBox.Show(this,
                $"Saved:\n{save.FileName}\n\n{r}\n\nShow it in Explorer?", "Report exported",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (ask == DialogResult.Yes)
                Process.Start("explorer.exe", $"/select,\"{save.FileName}\"");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The report could not be written:\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
