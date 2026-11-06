using System.Diagnostics;

namespace TestChecklist;

/// The Faults tab and the Upgrades tab: one list, one detail pane, the same three buttons.
sealed class ItemsPanel : UserControl
{
    readonly MainForm _host;
    readonly string _kind;
    readonly ComboBox _filter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    readonly TextBox _search = new() { Width = 220, PlaceholderText = "Search…" };
    readonly Button _new = new() { AutoSize = true };
    readonly Button _edit = new() { Text = "Edit…", AutoSize = true, Enabled = false };
    readonly Button _update = new() { Text = "Change status / comment…", AutoSize = true, Enabled = false };
    readonly Button _pictures = new() { Text = "Open pictures", AutoSize = true, Enabled = false };
    readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
    readonly RichTextBox _detail = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, BackColor = SystemColors.Window, BorderStyle = BorderStyle.None,
        DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical,
    };
    readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
    string? _detailSig;

    public ItemsPanel(MainForm host, string kind)
    {
        _host = host;
        _kind = kind;
        _new.Text = kind == Kinds.Fault ? "Log a fault…" : "Suggest an upgrade…";

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 4) };
        bar.Controls.AddRange(new Control[]
        {
            _new, _edit, _update, _pictures,
            new Label { Text = "      Show", AutoSize = true, Padding = new Padding(0, 7, 0, 0) },
            _filter, _search,
        });
        _filter.Items.Add("Open");
        _filter.Items.Add("All");
        _filter.Items.AddRange(Kinds.Statuses(kind));
        _filter.SelectedIndex = 0;

        _list.Columns.Add("#", 60);
        _list.Columns.Add("Title", 420);
        _list.Columns.Add("Area", 130);
        _list.Columns.Add(Kinds.LevelLabel(kind), 100);
        _list.Columns.Add("Check", 70, HorizontalAlignment.Right);
        _list.Columns.Add("Status", 150);
        _list.Columns.Add("Raised by", 110);
        _list.Columns.Add("When", 110);

        _split.Panel1.Controls.Add(_list);
        _split.Panel2.Controls.Add(_detail);
        _split.Panel2.Padding = new Padding(0, 6, 0, 0);
        Controls.Add(_split);
        Controls.Add(bar);

        _new.Click += (_, _) => _host.NewItem(_kind);
        _edit.Click += (_, _) => Edit();
        _update.Click += (_, _) => ChangeStatus();
        _pictures.Click += (_, _) => OpenPictures();
        _filter.SelectedIndexChanged += (_, _) => Fill();
        _search.TextChanged += (_, _) => Fill();
        _list.SelectedIndexChanged += (_, _) => ShowDetail();
        _list.DoubleClick += (_, _) => ChangeStatus();
        _list.Resize += (_, _) => FitColumns();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _split.SplitterDistance = Math.Max(_split.Panel1MinSize, (int)(_split.Height * 0.5));
    }

    ItemState? Selected =>
        _list.SelectedItems.Count > 0 && _host.Board.Items.TryGetValue((string)_list.SelectedItems[0].Tag!, out var i) ? i : null;

    public void ShowItem(string id)
    {
        _filter.SelectedIndex = 1;
        _search.Text = "";
        Fill();
        foreach (ListViewItem it in _list.Items)
            if ((string)it.Tag! == id) { it.Selected = true; it.EnsureVisible(); }
    }

    public void Fill()
    {
        var keep = Selected?.Id;
        string q = _search.Text.Trim();
        var items = _host.Board.ItemsOf(_kind)
            .Where(i => _filter.SelectedIndex switch { 0 => i.IsOpen, 1 => true, _ => i.Status == _filter.Text })
            .Where(i => q.Length == 0 || i.Code.Equals(q, StringComparison.OrdinalIgnoreCase)
                        || (i.Title + " " + i.Details + " " + i.Area).Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.IsOpen).ThenBy(i => Array.IndexOf(Kinds.Levels(_kind), i.Level)).ThenBy(i => i.Created.At);

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var i in items)
        {
            var at = i.Created.At.ToLocalTime();
            var it = new ListViewItem(new[]
            {
                i.Code, i.Title, i.Area, i.Level, i.Check, i.Status, i.Created.By,
                at.Date == DateTime.Today ? at.ToString("HH:mm") : at.ToString("d MMM HH:mm"),
            }) { Tag = i.Id };
            if (!i.IsOpen) it.ForeColor = SystemColors.GrayText;
            else if (i.Status.StartsWith("Fixed")) it.BackColor = Results.Back("blocked");
            else if (_kind == Kinds.Fault && i.Level is "Stops work" or "Serious") it.BackColor = Results.Back("fault");
            if (i.Id == keep) it.Selected = true;
            _list.Items.Add(it);
        }
        _list.EndUpdate();
        FitColumns();
        ShowDetail();
    }

    void FitColumns()
    {
        int others = 0;
        for (int c = 0; c < _list.Columns.Count; c++) if (c != 1) others += _list.Columns[c].Width;
        int w = _list.ClientSize.Width - others - SystemInformation.VerticalScrollBarWidth;
        if (w > 150) _list.Columns[1].Width = w;
    }

    void ShowDetail()
    {
        var i = Selected;
        _edit.Enabled = _update.Enabled = i != null;
        _pictures.Enabled = i != null && i.Pictures.Count > 0;

        string sig = i == null ? "none" : i.Id + i.Changes.Count + i.Status;
        if (sig == _detailSig) return;
        _detailSig = sig;

        var st = _host.Styles;
        var w = new RichWriter(_detail, st);
        if (i == null)
        {
            w.Line(_kind == Kinds.Fault
                ? "Something not working as it should? Press Log a fault. A screenshot helps more than anything else."
                : "An idea that would make it better? Press Suggest an upgrade.", st.Italic, SystemColors.GrayText);
            return;
        }

        w.Heading($"{i.Code}  {i.Title}");
        var raised = i.Created.At.ToLocalTime();
        w.Line($"{i.Status}   ·   {Kinds.LevelLabel(_kind)}: {i.Level}   ·   Area: {(i.Area.Length > 0 ? i.Area : "—")}" +
               (i.Check.Length > 0 ? $"   ·   Check {i.Check}" : ""), st.Bold, i.IsOpen ? null : SystemColors.GrayText);
        w.Line($"Raised by {i.Created.By} on {i.Created.Machine}, {raised:ddd d MMM yyyy HH:mm}", st.Small, SystemColors.GrayText);
        w.Gap();
        if (i.Details.Length > 0) foreach (var l in i.Details.Split('\n')) w.Line(l);
        else w.Line("No details written.", st.Italic, SystemColors.GrayText);

        if (i.Pictures.Count > 0)
        {
            w.Gap();
            w.Line($"{i.Pictures.Count} picture{(i.Pictures.Count == 1 ? "" : "s")} — press Open pictures.", st.Italic, SystemColors.GrayText);
        }

        if (i.Check.Length > 0 && _host.Book.ByNumber.TryGetValue(i.Check, out var c))
        {
            w.Gap();
            w.Line($"Check {c.Number}: {c.Short}", st.Small, SystemColors.GrayText);
        }

        if (i.Changes.Count > 0)
        {
            w.Gap();
            w.Line("History", st.Bold);
            foreach (var e in i.Changes)
            {
                var at = e.At.ToLocalTime();
                string what = e.Type == "edit" ? "edited it"
                    : !string.IsNullOrEmpty(e.Status) ? "→ " + e.Status : "commented";
                w.Write($"   {at:ddd d MMM HH:mm}   {e.By}   ", null, SystemColors.GrayText);
                w.Line(what, st.Bold);
                if (!string.IsNullOrWhiteSpace(e.Comment))
                    foreach (var l in e.Comment.Split('\n')) w.Line("         " + l);
            }
        }
        _detail.SelectionStart = 0;
        _detail.ScrollToCaret();
    }

    void Edit()
    {
        var i = Selected;
        if (i == null) return;
        using var d = new ItemDialog(_kind, _host.Book, _host.AreasInUse(), i);
        if (d.ShowDialog(this) != DialogResult.OK) return;
        List<string> pics;
        try { pics = d.SavePictures(_host.Store.PicturesDir, i.Id); }
        catch (Exception ex) { MessageBox.Show(this, "The pictures could not be saved:\n\n" + ex.Message); pics = new(); }
        _host.Append(new Entry
        {
            Type = "edit", Target = i.Id, Title = d.TitleText, Details = d.DetailsText.Replace("\r\n", "\n"),
            Area = d.AreaText, Level = d.LevelText, Check = d.CheckText, Pictures = pics.Count > 0 ? pics : null,
        });
        _detailSig = null;
        _host.Reload(true);
    }

    void ChangeStatus()
    {
        var i = Selected;
        if (i == null) return;
        using var d = new UpdateDialog(i);
        if (d.ShowDialog(this) != DialogResult.OK) return;
        _host.Append(new Entry
        {
            Type = "update", Target = i.Id,
            Status = d.StatusChanged ? d.StatusText : null,
            Comment = d.CommentText.Length > 0 ? d.CommentText.Replace("\r\n", "\n") : null,
        });
        _detailSig = null;
        _host.Reload(true);
    }

    void OpenPictures()
    {
        var i = Selected;
        if (i == null) return;
        foreach (var p in i.Pictures.Take(10))
        {
            var path = Path.Combine(_host.Store.PicturesDir, p);
            if (!File.Exists(path)) { MessageBox.Show(this, "Missing from the shared drive: " + path); continue; }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }
}
