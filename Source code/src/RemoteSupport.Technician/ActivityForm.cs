using System.Drawing;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Technician;

/// <summary>
/// Latest activity in the console (2026-10-26, HANDOVER §164). Clint: "if one clicks on latest activity, it will show any
/// updates on all tickets as they are posted or when stages change ... newest to oldest, but should also be able to be
/// sorted by client, technician, and all tickets." The portal's page of the same name shows the same list — the server
/// (<c>TicketActivityService</c>) makes every line and decides who may see it; this draws them.
///
/// <para>All tickets or My tickets (remembered on the server, and what the header's count counts); a client by part of
/// its name; who posted it; my own posts and automatic lines only when ticked. A click opens the ticket in the console's
/// own ticket window. While the window is on screen, new lines come in by themselves and count as seen; minimised, they
/// wait and stay counted. One window, hidden on close, like Office chat.</para>
/// </summary>
public sealed class ActivityForm : Form
{
    private readonly TechLink _link;
    private readonly Theme _t;
    private readonly Font _bold, _smallBold;
    private readonly RoundButton _allBtn, _mineBtn, _olderBtn;
    private readonly InputBox _client;
    private readonly ThemedCombo _poster;
    private readonly RoundCheck _own, _auto;
    private readonly ListBox _list;
    private readonly Label _status;
    private readonly System.Windows.Forms.Timer _typing = new() { Interval = 450 };
    private readonly List<ActivityEntry> _entries = new();
    private readonly int _line, _small, _pad;
    private bool _mine, _more, _behind;
    /// <summary>"Show older" has been pressed: the list is longer than one page now, and a refresh would throw that away.</summary>
    private bool _older;
    private bool _peopleFilled;

    /// <summary>A line was clicked: the dashboard opens that ticket in the ticket window.</summary>
    public event Action<int>? OpenTicket;

    public ActivityForm(TechLink link, Theme t, bool mine)
    {
        _link = link; _t = t; _mine = mine;
        _bold = new Font(t.Body, FontStyle.Bold);
        _smallBold = new Font(t.Small, FontStyle.Bold);
        Text = "Latest activity";
        StartPosition = FormStartPosition.Manual;
        BackColor = t.Mist;
        Font = t.Body;
        ShowInTaskbar = true;
        Shown += (_, _) => NativeDark.Apply(this, t);

        // Every height from its font (§30): the fields, the buttons, each line of a row.
        _line = TextRenderer.MeasureText("Ag", t.Body).Height;
        _small = TextRenderer.MeasureText("Ag", t.Small).Height;
        _pad = Math.Max(8, _line / 2);
        int btnH = TextRenderer.MeasureText("Ag", t.ButtonFont).Height + 14;
        int fieldH = TextRenderer.MeasureText("Ag", new Font("Segoe UI", 10.5f)).Height + 16;
        int gap = 8;
        ClientSize = new Size(TextRenderer.MeasureText(new string('M', 60), t.Body).Width, (_line * 3 + _pad * 3) * 7 + btnH * 3);
        MinimumSize = new Size(TextRenderer.MeasureText(new string('M', 34), t.Body).Width, (_line * 3 + _pad * 3) * 3 + btnH * 4);

        // ---- whose tickets ----
        var scope = new Panel { Dock = DockStyle.Top, Height = btnH + gap, BackColor = Color.Transparent, Padding = new Padding(0, 0, 0, gap) };
        RoundButton Scope(string text)
        {
            var b = new RoundButton(t, BtnStyle.Neutral) { Text = text, Dock = DockStyle.Left, Height = btnH,
                Width = TextRenderer.MeasureText(text, t.ButtonFont).Width + 36, Tint = t.Grape, TintInk = t.Grape };
            return b;
        }
        _allBtn = Scope("All tickets");
        _mineBtn = Scope("My tickets");
        _allBtn.Click += (_, _) => { if (_mine) { _mine = false; DrawScope(); Ask(); } };
        _mineBtn.Click += (_, _) => { if (!_mine) { _mine = true; DrawScope(); Ask(); } };
        scope.Controls.Add(_mineBtn);
        scope.Controls.Add(new Panel { Dock = DockStyle.Left, Width = gap, BackColor = Color.Transparent });
        scope.Controls.Add(_allBtn);

        // ---- narrowing it: a flow, so a narrow window wraps the boxes rather than hiding one ----
        var filters = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = Color.Transparent, Padding = new Padding(0, 0, 0, gap) };
        Label Caption(string s) => new() { Text = s, AutoSize = true, Font = _smallBold, ForeColor = t.Muted, BackColor = Color.Transparent, UseMnemonic = false,
            Margin = new Padding(0, (fieldH - _small) / 2, 6, 0) };
        _client = new InputBox(t, height: fieldH) { Width = TextRenderer.MeasureText(new string('M', 16), t.Body).Width, Margin = new Padding(0, 0, 14, 6) };
        _client.Box.TextChanged += (_, _) => { _typing.Stop(); _typing.Start(); };
        _client.Box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _typing.Stop(); Ask(); } };
        _typing.Tick += (_, _) => { _typing.Stop(); Ask(); };
        _poster = new ThemedCombo(t) { Font = t.Body, FlatStyle = FlatStyle.Flat, Width = TextRenderer.MeasureText(new string('M', 13), t.Body).Width, Margin = new Padding(0, (fieldH - (_line + 8)) / 2, 14, 6) };
        _poster.Items.Add("Everybody");
        _poster.SelectedIndex = 0;
        _poster.SelectedIndexChanged += (_, _) => { if (_peopleFilled) Ask(); };
        _own = new RoundCheck(t) { Text = "Include my own", Margin = new Padding(0, (fieldH - _line) / 2 - 2, 14, 6) };
        _own.CheckedChanged += (_, _) => Ask();
        _auto = new RoundCheck(t) { Text = "Include automatic entries", Margin = new Padding(0, (fieldH - _line) / 2 - 2, 0, 6) };
        _auto.CheckedChanged += (_, _) => Ask();
        filters.Controls.AddRange(new Control[] { Caption("Client"), _client, Caption("Posted by"), _poster, _own, _auto });
        var tips = new ToolTip();
        tips.SetToolTip(_client, "Part of a client's name, contact, phone or email — the Clients page's own search");
        tips.SetToolTip(_own, "What you posted yourself is left out unless this is ticked — you know what you wrote");
        tips.SetToolTip(_auto, "Lines a ticket writes by itself during a session — connected, ended, clipboard, scripts");

        // ---- the list ----
        _list = new ListBox
        {
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = t.Surface, ForeColor = t.Ink, Font = t.Body,
            DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Math.Min(255, _pad * 2 + _line * 2 + _small + 6), IntegralHeight = false, Cursor = Cursors.Hand
        };
        _list.DrawItem += DrawRow;
        _list.Click += (_, _) => { if (_list.SelectedIndex >= 0 && _list.SelectedIndex < _entries.Count) OpenTicket?.Invoke(_entries[_list.SelectedIndex].TicketId); };
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && _list.SelectedIndex >= 0 && _list.SelectedIndex < _entries.Count) OpenTicket?.Invoke(_entries[_list.SelectedIndex].TicketId); };
        var frame = new Card(t, 12) { Dock = DockStyle.Fill, Padding = new Padding(6) };
        frame.Controls.Add(_list);

        // ---- below: Show older, and what the list is ----
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = btnH + gap, BackColor = Color.Transparent, Padding = new Padding(0, gap, 0, 0) };
        _olderBtn = new RoundButton(t, BtnStyle.Neutral) { Text = "Show older", Dock = DockStyle.Right, Height = btnH, Width = TextRenderer.MeasureText("Show older", t.ButtonFont).Width + 36, Visible = false };
        _olderBtn.Click += (_, _) => { if (_entries.Count > 0) Ask(_entries[^1].AtUtc); };
        _status = new Label { Dock = DockStyle.Fill, Font = t.Small, ForeColor = t.Muted, BackColor = Color.Transparent, UseMnemonic = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        bottom.Controls.Add(_status);
        bottom.Controls.Add(_olderBtn);

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(12) };
        body.Controls.Add(frame);
        body.Controls.Add(bottom);
        body.Controls.Add(filters);
        body.Controls.Add(scope);
        Controls.Add(body);
        DrawScope();

        _link.OnActivityList += m => Inv(() => Take(m));

        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        ResizeEnd += (_, _) => { if (WindowState == FormWindowState.Normal) WindowPlacement.Remember("activity", Bounds); };
        Resize += (_, _) => { if (WindowState != FormWindowState.Minimized && _behind) Ask(); };
        Activated += (_, _) => { if (_behind) Ask(); };
        Load += (_, _) =>
        {
            if (WindowPlacement.Recall("activity") is { } r) Bounds = WindowPlacement.ClampToScreen(r);
            else { var wa = Screen.PrimaryScreen!.WorkingArea; Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2); }
        };
    }

    /// <summary>Brings it up with the newest lines, and counts them as seen.</summary>
    public void ShowFor()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        Ask();
    }

    /// <summary>Is somebody looking at it: shown, and not minimised. Only then is what it shows "seen".</summary>
    private bool OnScreen => Visible && WindowState != FormWindowState.Minimized;

    /// <summary>
    /// The count moved. With something new and the window on screen, it fetches the newest again — which counts it seen,
    /// so the count goes back to nought and nothing more is asked. Off screen, it waits for the person to look.
    /// Never asked again on a count of nought: that is the answer to this window's own look.
    /// </summary>
    public void CountChanged(ActivityCountMsg m)
    {
        // All or Mine chosen somewhere else (the portal): followed while nobody is looking at this window.
        if (m.Mine != _mine && !OnScreen) { _mine = m.Mine; DrawScope(); _behind = true; }
        if (m.Count <= 0) return;
        if (OnScreen && !_older) Ask();
        else _behind = true;
    }


    private void Ask(DateTime? before = null)
    {
        _behind = false;

        if (before == null) _status.Text = "Looking…";
        _link.RequestActivity(new ActivityRequestMsg
        {
            Mine = _mine, Client = _client.Value.Trim(), Poster = PosterChosen, Automatic = _auto.Checked, Own = _own.Checked,
            BeforeUtc = before, MarkSeen = before == null && OnScreen
        });
    }

    private string PosterChosen => _poster.SelectedIndex > 0 ? _poster.SelectedItem as string ?? "" : "";

    private void Take(ActivityListMsg m)
    {
        // An answer to choices since changed is thrown away: the newer question's answer is on its way.
        if (m.Mine != _mine || m.Client != _client.Value.Trim() || m.Poster != PosterChosen || m.Automatic != _auto.Checked || m.Own != _own.Checked) return;
        FillPeople(m.People);
        int keep = _list.TopIndex;
        if (m.BeforeUtc == null) _entries.Clear();
        _older = m.BeforeUtc != null;
        _entries.AddRange(m.Entries);
        _more = m.More;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var e in _entries) _list.Items.Add(e.Key);
        _list.EndUpdate();
        if (m.BeforeUtc != null && keep < _list.Items.Count) _list.TopIndex = keep;
        _olderBtn.Visible = _more;
        int fresh = _entries.Count(e => e.IsNew);
        _status.Text = _entries.Count == 0
            ? (m.Client.Length > 0 || m.Poster.Length > 0 || m.Own || m.Automatic ? "Nothing matches these choices." : _mine ? "Nothing on your tickets yet. Updates appear here as they are posted." : "Nothing yet. Updates appear here as they are posted.")
            : $"{_entries.Count} update{(_entries.Count == 1 ? "" : "s")}, newest first" + (fresh > 0 ? $" — {fresh} new since you last looked" : "") + ". Click one to open its ticket.";
    }

    private void FillPeople(List<string> people)
    {
        var want = people.Where(p => p != _link.DisplayName).ToList();
        if (_peopleFilled && _poster.Items.Count == want.Count + 2) return;
        var chosen = PosterChosen;
        _peopleFilled = false;
        _poster.BeginUpdate();
        _poster.Items.Clear();
        _poster.Items.Add("Everybody");
        _poster.Items.Add(_link.DisplayName);   // "what did I post" is a fair question too
        foreach (var p in want) _poster.Items.Add(p);
        int at = chosen.Length == 0 ? 0 : _poster.Items.IndexOf(chosen);
        _poster.SelectedIndex = Math.Max(0, at);
        _poster.EndUpdate();
        _peopleFilled = true;
    }

    private void DrawScope()
    {
        _allBtn.Chosen = !_mine; _mineBtn.Chosen = _mine;
        _allBtn.Invalidate(); _mineBtn.Invalidate();
    }

    /// <summary>The mark down a row's left edge, by what it is — the portal's colours.</summary>
    private Color KindColour(ActivityEntry e) => e.Kind switch
    {
        ActivityKinds.Comment => _t.Grape, ActivityKinds.Customer => _t.Orange, ActivityKinds.Step => _t.Green,
        ActivityKinds.Back => _t.Pink, ActivityKinds.Opened => _t.Blue, _ => _t.Muted
    };

    private void DrawRow(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _entries.Count) return;
        var a = _entries[e.Index];
        var g = e.Graphics;
        var r = e.Bounds;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        var back = selected ? _t.SurfaceAlt : a.IsNew ? Theme.Blend(_t.Surface, _t.Grape, .09) : _t.Surface;
        using (var b = new SolidBrush(back)) g.FillRectangle(b, r);
        using (var b = new SolidBrush(KindColour(a))) g.FillRectangle(b, r.Left, r.Top + 4, 4, r.Height - 8);
        using (var p = new Pen(_t.CardBorder)) g.DrawLine(p, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);

        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
        // Measured WITHOUT the ellipsis: with it and no room given, Windows measures next to nothing (lookprobe's picture showed
        // the time cut to "2…" and "assigned it to you" drawn over the line's own words).
        const TextFormatFlags Measure = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        int x = r.Left + 14, y = r.Top + _pad, right = r.Right - 10;
        var ink = selected ? _t.SurfaceAltText : _t.Ink;

        // Line 1: the ticket, and when — "5 min ago", with "new" beside it.
        var when = ActivityWords.Ago(a.AtUtc, DateTime.UtcNow, u => u.ToLocalTime()) + (a.IsNew ? "  · new" : "");
        int whenW = TextRenderer.MeasureText(g, when, a.IsNew ? _smallBold : _t.Small, Size.Empty, Measure).Width;
        TextRenderer.DrawText(g, when, a.IsNew ? _smallBold : _t.Small, new Rectangle(right - whenW, y, whenW, _line), a.IsNew ? _t.Grape : _t.Muted, flags);
        var title = $"#{a.TicketNumber} {a.Subject}" + (a.Client.Length > 0 ? $"  ·  {a.Client}" : "");
        TextRenderer.DrawText(g, title, _bold, new Rectangle(x, y, Math.Max(10, right - whenW - 12 - x), _line), ink, flags);
        y += _line + 2;

        // Line 2: who, what — and why it is about me, in red: "assigned it to you", "asked you".
        var what = $"{a.Who}  ·  {a.Headline}" + (a.Undone ? " (gone back over)" : "") + (a.Files > 0 ? $"  ·  {a.Files} file{(a.Files == 1 ? "" : "s")}" : "");
        int whatW = TextRenderer.MeasureText(g, what, _t.Body, Size.Empty, Measure).Width;
        TextRenderer.DrawText(g, what, _t.Body, new Rectangle(x, y, right - x, _line), ink, flags);
        if (a.ForYou.Length > 0 && x + whatW + 10 < right)
            TextRenderer.DrawText(g, a.ForYou, _bold, new Rectangle(x + whatW + 10, y, right - x - whatW - 10, _line), _t.BadText, flags);
        y += _line + 2;

        // Line 3: the words, shortened.
        if (a.Text.Length > 0) TextRenderer.DrawText(g, a.Text, _t.Small, new Rectangle(x, y, right - x, _small), _t.Muted, flags);
    }

    private void Inv(Action a) { try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { } }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _typing.Dispose(); _bold.Dispose(); _smallBold.Dispose(); }
        base.Dispose(disposing);
    }
}
