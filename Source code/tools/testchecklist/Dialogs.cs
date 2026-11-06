using System.Drawing.Imaging;

namespace TestChecklist;

// Every window here is laid out by TableLayoutPanels and AutoSize. No control height is written
// as a number except where one must be (a multi-line box), and those are measured from the font
// (HANDOVER §30: a height written as a number is right on one machine's scaling only).

class DialogBase : Form
{
    protected readonly TableLayoutPanel Grid;
    protected readonly Button Ok = new() { Text = "OK", AutoSize = true, MinimumSize = new Size(88, 0) };
    protected readonly Button Cancel = new() { Text = "Cancel", AutoSize = true, MinimumSize = new Size(88, 0) };

    protected DialogBase(string title, Size size)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.75f);
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = MaximizeBox = false;
        ClientSize = size;
        MinimumSize = new Size(Math.Min(size.Width, 420), 0);
        Padding = new Padding(12);

        Grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = false };
        Grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false, Padding = new Padding(0, 10, 0, 0),
        };
        buttons.Controls.Add(Cancel);
        buttons.Controls.Add(Ok);
        Cancel.DialogResult = DialogResult.Cancel;
        Ok.Click += (_, _) => { if (IsReady()) { DialogResult = DialogResult.OK; Close(); } };
        AcceptButton = Ok;
        CancelButton = Cancel;

        Controls.Add(Grid);
        Controls.Add(buttons);
    }

    protected void Finish() { Ui.Tidy(this); ResumeLayout(false); PerformLayout(); }

    protected virtual bool IsReady() => true;

    readonly List<(Control Box, int Lines)> _measured = new();

    /// Sizes a box to show <paramref name="lines"/> lines of its own font — in OnLoad, after the
    /// window has been scaled, so it is measured at the scaling it will be shown at.
    protected void Lines(Control box, int lines) => _measured.Add((box, lines));

    protected override void OnLoad(EventArgs e)
    {
        foreach (var (box, lines) in _measured)
            box.Height = TextRenderer.MeasureText("Ag", box.Font).Height * lines + LogicalToDeviceUnits(8);
        base.OnLoad(e);
    }

    protected static Label Caption(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Padding = new Padding(0, 6, 8, 0) };

    protected void AddRow(string caption, Control c, SizeType type = SizeType.AutoSize, float h = 0)
    {
        int row = Grid.RowCount++;
        Grid.RowStyles.Add(new RowStyle(type, h));
        Grid.Controls.Add(Caption(caption), 0, row);
        c.Dock = DockStyle.Fill;
        c.Margin = new Padding(0, 3, 0, 3);
        Grid.Controls.Add(c, 1, row);
    }
}

sealed class NameDialog : DialogBase
{
    readonly TextBox _name = new();
    public string Value => _name.Text.Trim();

    public NameDialog(string? current) : base("Who is testing?", new Size(460, 160))
    {
        var intro = new Label
        {
            Text = "Your name goes beside everything you record, so the report says who found what. It is remembered on this PC.",
            AutoSize = true, MaximumSize = new Size(420, 0), Padding = new Padding(0, 0, 0, 8),
        };
        Grid.RowCount++;
        Grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Grid.Controls.Add(intro, 0, 0);
        Grid.SetColumnSpan(intro, 2);
        _name.Text = current ?? "";
        AddRow("Your name", _name);
        Finish();
    }

    protected override bool IsReady()
    {
        if (Value.Length >= 2) return true;
        MessageBox.Show(this, "Type your name first.", Text);
        return false;
    }
}

/// A picture on a fault or upgrade, either already saved on the share or waiting to be.
sealed class Pic
{
    public string? Saved;
    public Image? Image;
    public string? SourceFile;
    public override string ToString() =>
        Saved != null ? Saved + "  (attached)" : SourceFile != null ? Path.GetFileName(SourceFile) : "Pasted screenshot";
}

sealed class ItemDialog : DialogBase
{
    readonly string _kind;
    readonly CheckBook _book;
    readonly TextBox _title = new();
    readonly ComboBox _area = new() { DropDownStyle = ComboBoxStyle.DropDown };
    readonly ComboBox _level = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly TextBox _check = new() { Width = 80, Dock = DockStyle.None };
    readonly TextBox _details = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
    readonly ListBox _pics = new() { IntegralHeight = false };
    readonly List<Pic> _picList = new();

    public string TitleText => _title.Text.Trim();
    public string AreaText => _area.Text.Trim();
    public string LevelText => _level.Text;
    public string CheckText => _check.Text.Trim();
    public string DetailsText => _details.Text.Trim();
    public IEnumerable<Pic> NewPictures => _picList.Where(p => p.Saved == null);

    public ItemDialog(string kind, CheckBook book, IEnumerable<string> areasInUse, ItemState? existing,
        string? check = null, string? title = null, string? details = null)
        : base(existing == null ? (kind == Kinds.Fault ? "Log a fault" : "Suggest an upgrade") : $"Edit {existing.Code}",
               new Size(660, 600))
    {
        _kind = kind;
        _book = book;
        KeyPreview = true;
        MaximizeBox = true;
        MinimumSize = new Size(520, 480);

        _area.Items.AddRange(Kinds.Areas.Concat(areasInUse).Distinct(StringComparer.OrdinalIgnoreCase).ToArray<object>());
        _level.Items.AddRange(Kinds.Levels(kind));

        AddRow("Title", _title);
        AddRow("Area", _area);
        AddRow(Kinds.LevelLabel(kind), _level);

        var checkRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        checkRow.Controls.Add(_check);
        checkRow.Controls.Add(new Label
        {
            Text = "optional — the number on the Checks tab this came from",
            AutoSize = true, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 6, 0, 0),
        });
        AddRow("Check number", checkRow);

        AddRow(kind == Kinds.Fault ? "What happened" : "What it should do", _details, SizeType.Percent, 100);
        _details.PlaceholderText = kind == Kinds.Fault
            ? "What you did, what you expected, what happened instead. Which machine, if it matters."
            : "What you would like it to do, and why it would help.";

        var picBox = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true };
        picBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        picBox.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _pics.Dock = DockStyle.Fill;
        Lines(_pics, 4);
        var picButtons = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        var paste = new Button { Text = "Paste screenshot", AutoSize = true };
        var file = new Button { Text = "Add picture file…", AutoSize = true };
        var remove = new Button { Text = "Remove", AutoSize = true };
        picButtons.Controls.AddRange(new Control[] { paste, file, remove });
        picBox.Controls.Add(_pics, 0, 0);
        picBox.Controls.Add(picButtons, 1, 0);
        AddRow("Pictures", picBox);

        paste.Click += (_, _) => PasteImage(true);
        file.Click += (_, _) => AddFile();
        remove.Click += (_, _) =>
        {
            if (_pics.SelectedItem is Pic p && p.Saved == null) { _picList.Remove(p); RefreshPics(); }
        };
        var tip = new ToolTip();
        tip.SetToolTip(paste, "Take a screenshot (Windows+Shift+S), then press this — or Ctrl+V anywhere in this window.");

        if (existing != null)
        {
            _title.Text = existing.Title;
            _area.Text = existing.Area;
            _level.SelectedItem = existing.Level;
            _check.Text = existing.Check;
            _details.Text = existing.Details.Replace("\n", Environment.NewLine);
            foreach (var p in existing.Pictures) _picList.Add(new Pic { Saved = p });
            RefreshPics();
        }
        else
        {
            _title.Text = title ?? "";
            _check.Text = check ?? "";
            _details.Text = (details ?? "").Replace("\n", Environment.NewLine);
            _level.SelectedIndex = 1;
        }
        Finish();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.V && Clipboard.ContainsImage() && !Clipboard.ContainsText())
        {
            PasteImage(false);
            e.Handled = e.SuppressKeyPress = true;
            return;
        }
        base.OnKeyDown(e);
    }

    void PasteImage(bool say)
    {
        if (!Clipboard.ContainsImage())
        {
            if (say) MessageBox.Show(this, "There is no picture on the clipboard. Take one with Windows+Shift+S, then press Paste screenshot.", Text);
            return;
        }
        var img = Clipboard.GetImage();
        if (img == null) return;
        _picList.Add(new Pic { Image = img });
        RefreshPics();
    }

    void AddFile()
    {
        using var d = new OpenFileDialog
        {
            Filter = "Pictures|*.png;*.jpg;*.jpeg;*.gif;*.bmp|All files|*.*",
            Multiselect = true,
        };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        foreach (var f in d.FileNames) _picList.Add(new Pic { SourceFile = f });
        RefreshPics();
    }

    void RefreshPics()
    {
        _pics.Items.Clear();
        foreach (var p in _picList) _pics.Items.Add(p);
    }

    protected override bool IsReady()
    {
        if (TitleText.Length < 3)
        {
            MessageBox.Show(this, "Give it a title — a few words saying what is wrong or wanted.", Text);
            _title.Focus();
            return false;
        }
        if (CheckText.Length > 0 && !_book.ByNumber.ContainsKey(CheckText))
        {
            MessageBox.Show(this, $"There is no check {CheckText} on the checklist. Leave the box empty if it is not about one check.", Text);
            _check.Focus();
            return false;
        }
        return true;
    }

    /// Saves the new pictures into data\pictures and returns their names.
    public List<string> SavePictures(string dir, string itemId)
    {
        Directory.CreateDirectory(dir);
        var names = new List<string>();
        int n = 1;
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        foreach (var p in NewPictures)
        {
            string ext = p.SourceFile != null ? Path.GetExtension(p.SourceFile).ToLowerInvariant() : ".png";
            if (ext.Length == 0) ext = ".png";
            string name = $"{itemId[..8]}-{stamp}-{n++}{ext}";
            string path = Path.Combine(dir, name);
            if (p.Image != null) p.Image.Save(path, ImageFormat.Png);
            else if (p.SourceFile != null) File.Copy(p.SourceFile, path, overwrite: false);
            names.Add(name);
        }
        return names;
    }
}

sealed class UpdateDialog : DialogBase
{
    readonly ComboBox _status = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly TextBox _comment = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
    readonly string _was;

    public string StatusText => _status.Text;
    public string CommentText => _comment.Text.Trim();
    public bool StatusChanged => StatusText != _was;

    public UpdateDialog(ItemState item) : base($"Update {item.Code} — {item.Title}", new Size(520, 300))
    {
        _was = item.Status;
        MaximizeBox = true;
        _status.Items.AddRange(Kinds.Statuses(item.Kind));
        _status.SelectedItem = item.Status;
        if (_status.SelectedIndex < 0) { _status.Items.Add(item.Status); _status.SelectedItem = item.Status; }
        AddRow("Status", _status);
        AddRow("Comment", _comment, SizeType.Percent, 100);
        _comment.PlaceholderText = "What changed — e.g. retested on DESKTOP-OPHQIDH and it now works.";
        Finish();
    }

    protected override bool IsReady()
    {
        if (StatusChanged || CommentText.Length > 0) return true;
        MessageBox.Show(this, "Change the status or write a comment.", Text);
        return false;
    }
}

sealed class ExportDialog : DialogBase
{
    readonly ComboBox _range = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly DateTimePicker _day = new() { Format = DateTimePickerFormat.Long, Enabled = false };

    public DateTime? From { get; private set; }
    public DateTime? To { get; private set; }
    public string Label { get; private set; } = "";

    public ExportDialog() : base("Export a report", new Size(480, 190))
    {
        _range.Items.AddRange(new object[] { "Today", "Yesterday", "A day I choose", "Everything so far" });
        _range.SelectedIndex = 0;
        _range.SelectedIndexChanged += (_, _) => _day.Enabled = _range.SelectedIndex == 2;
        AddRow("What to include", _range);
        AddRow("Day", _day);
        var note = new Label
        {
            Text = "The report is a single file (plus a folder of any pictures) — give it to Claude at the end of the day.",
            AutoSize = true, MaximumSize = new Size(440, 0), ForeColor = SystemColors.GrayText, Padding = new Padding(0, 8, 0, 0),
        };
        int row = Grid.RowCount++;
        Grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Grid.Controls.Add(note, 0, row);
        Grid.SetColumnSpan(note, 2);
        Ok.Text = "Export…";
        Finish();
    }

    protected override bool IsReady()
    {
        var today = DateTime.Today;
        switch (_range.SelectedIndex)
        {
            case 0: From = today; To = today.AddDays(1); Label = today.ToString("yyyy-MM-dd"); break;
            case 1: From = today.AddDays(-1); To = today; Label = today.AddDays(-1).ToString("yyyy-MM-dd"); break;
            case 2: From = _day.Value.Date; To = _day.Value.Date.AddDays(1); Label = _day.Value.ToString("yyyy-MM-dd"); break;
            default: From = To = null; Label = "everything to " + today.ToString("yyyy-MM-dd"); break;
        }
        return true;
    }
}
