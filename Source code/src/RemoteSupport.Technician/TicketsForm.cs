using System.Drawing;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Technician;

/// <summary>
/// The technician's tickets, in the console — rebuilt 2026-09-24 for the workflows (stage 3.7), and
/// again 2026-09-27 as a STORY, the same shape as the portal's ticket page.
///
/// <para>Clint, 2026-09-27, with screenshots of Anywhere's current system: "the stage transitions at
/// the top with always a place to post comments through every stage, and then one can scroll through
/// it, reads like a story." So a workshop job is: its stage and the next steps at the top, a comment
/// box that is always there, and every step as a numbered entry, newest first. Pressing a step opens
/// its form at the top of the story, with Submit; nothing else asks for anything until then. Photos,
/// the quote and the details are tabs, out of the way.</para>
///
/// <para>The queue is on the left; on the right is NOTHING the server did not send. The stage, the
/// buttons, the fields a step needs, whether the quote may be edited — every one of them is an answer
/// in the <see cref="TicketDetail"/>, computed by the same <c>TicketService.ActionsFor</c> the portal
/// draws. A route written in two places drifts, and the symptom is a job a technician cannot move
/// and nobody can explain.</para>
///
/// <para>Laid out the §37 way: every size measured from its font, flow layout rather than
/// coordinates, and nothing positioned by hand. The right-hand side is rebuilt from scratch each
/// time a ticket arrives; what is typed into a step survives that, because it is kept as it is typed.</para>
///
/// <para>What stays in the browser: Request quote (the office's, and it needs the mail-client choice)
/// and looking at the photographs. Those buttons open the portal, signed in, at that place.</para>
/// </summary>
public sealed class TicketsForm : Form
{
    private readonly TechLink _link;
    private readonly Theme _t;
    private readonly Branding _brand;

    private ListView _list = null!;
    private InputBox _search = null!;
    private ComboBox _filter = null!;

    private Label _count = null!;

    private Panel _scroll = null!;
    private FlowLayoutPanel _body = null!;
    private Label _placeholder = null!;

    private readonly List<TicketSummary> _tickets = new();
    private TicketDetail? _open;
    private bool _loading;
    /// <summary>The last thing a button press came back with, shown at the top of the ticket.</summary>
    private TicketNoticeMsg? _notice;
    /// <summary>Which tab of a workflow ticket is showing: activity, photos, quote or details.</summary>
    private string _tab = "activity";
    /// <summary>The step whose form is open at the top of the story, if any.</summary>
    private string? _step;
    /// <summary>Add another machine: where its photos are staged, and how many have gone up. Kept
    /// across redraws (and a refusal) so a photo picked is never stranded; cleared once it is booked.</summary>
    private string? _machineStage;
    private int _machinePhotos;
    /// <summary>What has been typed, by box, so a redraw — somebody else moving the ticket, an item
    /// added to the quote — does not throw it away.</summary>
    private readonly Dictionary<string, string> _drafts = new();
    private readonly System.Windows.Forms.Timer _suggestDebounce = new() { Interval = 220 };
    private readonly System.Windows.Forms.Timer _resizeDebounce = new() { Interval = 150 };

    // Measured once, from the fonts (§30): nothing below is a pixel constant.
    private readonly int _line, _fieldH, _btnH, _gap;

    /// <summary>The filters, in the order offered, with the key the server understands.</summary>
    public static readonly (string label, string key)[] Filters =
    {
        ("My work", "mine"),
        ("Waiting on my reply", "asked"),
        ("Unassigned", "unassigned"),
        ("All open", "open"),
        ("Billing queue", "billing"),
        ("Closed", "closed"),
        ("Everything", "all"),
    };

    public TicketsForm(TechLink link, Theme theme, Branding brand, string startFilter = "mine")
    {
        _link = link; _t = theme; _brand = brand;
        _line = TextRenderer.MeasureText("Ag", _t.Body).Height;
        _fieldH = _line + 16;
        _btnH = TextRenderer.MeasureText("Ag", _t.ButtonFont).Height + 18;
        _gap = _line / 2;
        BuildUi(startFilter);

        _link.OnTicketList += OnList;
        _link.OnTicketDetail += OnDetail;
        _link.OnTicketChanged += OnChanged;
        _link.OnTicketNotice += OnNotice;
        _link.OnItemList += OnItems;
        _link.OnQuoteSimilar += OnSimilar;
        // Focus starts in the queue, not the filter: a focused dropdown paints its text in the
        // system highlight colour, which in dark is a white bar across the top of the window.
        Shown += (_, _) => { Refresh_(); ActiveControl = _list; };
        FormClosed += (_, _) =>
        {
            _link.OnTicketList -= OnList;
            _link.OnTicketDetail -= OnDetail;
            _link.OnTicketChanged -= OnChanged;
            _link.OnTicketNotice -= OnNotice;
            _link.OnItemList -= OnItems;
            _link.OnQuoteSimilar -= OnSimilar;
            _suggestDebounce.Stop(); _resizeDebounce.Stop();
            try { WindowPlacement.Remember("tickets", Bounds); } catch { }
        };
    }

    private void Inv(Action a) { try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { } }

    /// <summary>Changes the queue's filter — the Tickets button uses it to open on "Waiting on my reply".</summary>
    public void ShowFilter(string key)
    {
        int i = Array.FindIndex(Filters, f => f.key == key);
        if (i >= 0 && _filter.SelectedIndex != i) _filter.SelectedIndex = i; else Refresh_();
    }

    // ================================================================== the frame

    private void BuildUi(string startFilter)
    {
        Text = $"{_brand.CompanyName} — Tickets";
        StartPosition = FormStartPosition.Manual;
        Font = _t.Body;
        BackColor = _t.Mist;
        Shown += (_, _) => NativeDark.Apply(this, _t);

        // Big, because a workshop job is a lot to read: most of the screen, clamped to it.
        var work = Screen.FromPoint(Cursor.Position).WorkingArea;
        var want = new Size(Math.Min(work.Width, Math.Max(1100, _line * 62)), Math.Min(work.Height, Math.Max(760, _line * 38)));
        var saved = WindowPlacement.Recall("tickets");
        Bounds = saved is { } r && WindowPlacement.IsReachable(r)
            ? WindowPlacement.ClampToScreen(r)
            : new Rectangle(work.Left + (work.Width - want.Width) / 2, work.Top + (work.Height - want.Height) / 2, want.Width, want.Height);
        // A floor measured from what the two columns need (§30), clamped to the screen: the queue,
        // and a ticket side wide enough for its widest row of buttons.
        int minW = Math.Max(420, _line * 22) + _line * 30 + 60;
        MinimumSize = new Size(Math.Min(minW, work.Width), Math.Min(Math.Max(560, _line * 26), work.Height));

        Image? logo = null;
        try { if (_brand.HasLogo) logo = Image.FromFile(_brand.LogoPath); } catch { }
        var header = new BrandHeader(_t, logo, _brand.CompanyName, "Tickets", 76);
        Controls.Add(header);

        var canvas = new CanvasPanel(_t) { Dock = DockStyle.Fill, Padding = new Padding(14) };
        Controls.Add(canvas);
        canvas.BringToFront();

        // ---------- left: the queue ----------
        // The queue has more columns since they sort (§165): client, who, priority, updated and due beside the subject. It
        // takes what the window can spare and NO MORE — the ticket side keeps the room it always had first (its rows of
        // buttons and long names: ticketwindowprobe), and the queue grows into the rest, up to wanting no sideways scroll.
        int queueMin = Math.Max(420, _line * 22), queueWant = Math.Max(640, _line * 34), ticketRoom = _line * 74;
        int leftW = queueWant;
        var leftCard = new Card(_t, 12) { Dock = DockStyle.Left, Width = queueMin };

        // Painted in the card's colour rather than left transparent: one layer fewer for the buttons
        // in it to see through, and nothing behind the card can show at their corners.
        var bar = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 2, AutoSize = true, BackColor = _t.CardBg, Padding = new Padding(0, 4, 0, 6) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _filter = new ThemedCombo(_t) { Dock = DockStyle.Fill, Font = _t.Body, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 6, 6) };
        _filter.Items.AddRange(Filters.Select(f => (object)f.label).ToArray());
        _filter.SelectedIndex = Math.Max(0, Array.FindIndex(Filters, f => f.key == startFilter));
        _filter.SelectedIndexChanged += (_, _) => Refresh_();
        var browser = new RoundButton(_t, BtnStyle.Neutral)
        {
            Text = "Open in browser", Margin = new Padding(0, 0, 0, 6),
            Size = new Size(TextRenderer.MeasureText("Open in browser", _t.ButtonFont).Width + 36, _btnH)
        };
        browser.Click += (_, _) => _link.RequestPortalLink(_open != null ? Portal.Ticket(_open.Ticket) : "/portal/tickets");
        _search = new InputBox(_t) { Dock = DockStyle.Fill, Height = _fieldH, Margin = new Padding(0, 0, 6, 0) };
        _search.Box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Refresh_(); } };
        var refresh = new RoundButton(_t, BtnStyle.Neutral)
        {
            Text = "Search", Margin = new Padding(0),
            Size = new Size(TextRenderer.MeasureText("Open in browser", _t.ButtonFont).Width + 36, _btnH)
        };
        refresh.Click += (_, _) => Refresh_();
        bar.Controls.Add(_filter, 0, 0); bar.Controls.Add(browser, 1, 0);
        bar.Controls.Add(_search, 0, 1); bar.Controls.Add(refresh, 1, 1);

        // Every column sorts by clicking its heading (2026-10-26, HANDOVER §165 — the Sort box of 2026-10-13 is gone): the
        // server sorts, before it takes the first few hundred; ▲/▼ on the one in use; remembered on this PC.
        _sortKey = TicketSortChoice.Load();
        _list = new ListView().Themed(_t);
        _list.Dock = DockStyle.Fill;
        int w(string s) => TextRenderer.MeasureText(s, _t.Body).Width + 18;
        int fixedW = 0;
        foreach (var (key, _, widest) in ListColumns)
            if (key != TicketSort.Subject) fixedW += w(widest + " ▼");
        foreach (var (key, heading, widest) in ListColumns)
            _list.Columns.Add(heading, key == TicketSort.Subject ? Math.Max(160, leftW - fixedW - 40) : w(widest + " ▼"));
        _list.ColumnClick += (_, e) =>
        {
            _sortKey = TicketSort.Toggle(_sortKey, ListColumns[e.Column].Key);
            TicketSortChoice.Remember(_sortKey);
            DrawHeadings();
            Refresh_();
        };
        DrawHeadings();
        _list.SelectedIndexChanged += (_, _) => OpenSelected();
        // As wide as the window can spare (above), the Subject column taking what the others leave.
        void FitQueue()
        {
            int width = Math.Clamp(canvas.ClientSize.Width - canvas.Padding.Horizontal - 14 - ticketRoom, queueMin, queueWant);
            if (leftCard.Width != width) leftCard.Width = width;
            // Subject never squeezed below two fifths of the queue: in a narrow one, the later columns scroll instead.
            int subject = Math.Max(Math.Max(160, width * 2 / 5), width - fixedW - 40);
            int at = Array.FindIndex(ListColumns, c => c.Key == TicketSort.Subject);
            if (_list.Columns.Count > at && _list.Columns[at].Width != subject) _list.Columns[at].Width = subject;
        }
        canvas.Resize += (_, _) => FitQueue();
        FitQueue();

        _count = new Label { Dock = DockStyle.Bottom, Height = TextRenderer.MeasureText("Ag", _t.Small).Height + 8, ForeColor = _t.Muted, Font = _t.Small, BackColor = Color.Transparent };

        leftCard.Controls.Add(_list);
        leftCard.Controls.Add(_count);
        leftCard.Controls.Add(bar);
        leftCard.Controls.Add(new SectionTitle(_t, "Queue", _t.Green) { Dock = DockStyle.Top });

        // ---------- right: the open ticket ----------
        var rightCard = new Card(_t, 14) { Dock = DockStyle.Fill };
        _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Transparent };
        _body = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Location = new Point(0, 0), Margin = new Padding(0)
        };
        _scroll.Controls.Add(_body);
        _placeholder = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = _t.Muted, Font = _t.Body,
            Text = "Choose a ticket on the left.", BackColor = Color.Transparent
        };
        rightCard.Controls.Add(_scroll);
        rightCard.Controls.Add(_placeholder);
        _scroll.Visible = false;

        // The ticket is drawn to the width it has; a resize redraws it once the dragging stops.
        // Only a change of the panel's OUTER width counts. Its client width also changes whenever the
        // scrollbar comes or goes — and a redraw can make it come or go — so reacting to that rebuilt
        // the whole side several times over, ping-ponging, and threw away whatever had just been put
        // on it: ticketwindowprobe found a "Choose the ticket" message vanishing the moment it
        // appeared, because it was the line that made the scrollbar arrive (2026-09-27).
        _resizeDebounce.Tick += (_, _) => { _resizeDebounce.Stop(); if (_open != null) Draw(_open, keepScroll: true); };
        _scroll.Resize += (_, _) => { if (_scroll.Width == _drawnWidth) return; _resizeDebounce.Stop(); _resizeDebounce.Start(); };

        canvas.Controls.Add(rightCard);
        canvas.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 14, BackColor = Color.Transparent });
        canvas.Controls.Add(leftCard);

        // Emptying the box brings the whole list back; a pick just made is not asked about again.
        _suggestDebounce.Tick += (_, _) => { _suggestDebounce.Stop(); if (_itemBox != null && _pickedItem == null) _link.SuggestItems(_itemBox.Value.Trim()); };
    }

    private string FilterKey => Filters[Math.Clamp(_filter.SelectedIndex, 0, Filters.Length - 1)].key;

    /// <summary>The queue's columns, left to right: the sort key, the heading, and the widest thing it holds (measured, §30).</summary>
    private static readonly (string Key, string Heading, string Widest)[] ListColumns =
    {
        // Most needed first, so a narrow queue still shows them; the rest scroll in from the right.
        (TicketSort.Number, "#", "#9999"), (TicketSort.Subject, "Subject", ""), (TicketSort.Status, "Where it is", "Awaiting diagnosis"),
        (TicketSort.Due, "Due", "Tue 30 Sep"), (TicketSort.Priority, "Priority", "Urgent"), (TicketSort.Client, "Client", "Zenith Accounting"),
        (TicketSort.Assigned, "Assigned to", "Sue Tech"), (TicketSort.Updated, "Updated", "59 min ago"),
    };

    /// <summary>The order the queue is in: a column and its direction (<see cref="TicketSort.Of"/>), as sent to the server.</summary>
    private string _sortKey = TicketSort.Of(null);
    private string SortKey => _sortKey;

    /// <summary>The headings, the one in use with ▲ or ▼.</summary>
    private void DrawHeadings()
    {
        for (int i = 0; i < ListColumns.Length && i < _list.Columns.Count; i++)
        {
            var arrow = TicketSort.Arrow(_sortKey, ListColumns[i].Key);
            _list.Columns[i].Text = ListColumns[i].Heading + (arrow.Length > 0 ? " " + arrow : "");
        }
    }

    private void Refresh_() => _link.RequestTickets(FilterKey, _search.Value.Trim(), SortKey);

    /// <summary>"2 min ago", "3 h ago", else the day — as the portal says Last updated.</summary>
    private static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        if (d < TimeSpan.FromMinutes(1)) return "just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} min ago";
        if (d < TimeSpan.FromHours(24)) return $"{(int)d.TotalHours} h ago";
        return DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("d MMM");
    }

    /// <summary>The Due column: its due day (a day, shown as it is), else a call out's appointment on this PC's clock.</summary>
    private static string DueFor(TicketSummary t) =>
        t.DueDate is DateTime d ? d.ToString("ddd d MMM") + (t.Overdue ? " — overdue" : "")
        : t.AppointmentUtc is DateTime a ? "📅 " + DateTime.SpecifyKind(a, DateTimeKind.Utc).ToLocalTime().ToString("ddd d MMM HH:mm")
        : "—";

    // ================================================================== the queue

    private void OnList(TicketListMsg m) => Inv(() =>
    {
        if (m.Filter != FilterKey) return;   // an answer to a filter that is no longer chosen
        // ...or to an order no longer chosen. An older server sends none, and its order is the one it always had.
        if (m.Sort.Length > 0 && m.Sort != SortKey) return;
        _tickets.Clear(); _tickets.AddRange(m.Tickets);
        _loading = true;
        _list.BeginUpdate(); _list.Items.Clear();
        foreach (var t in _tickets)
        {
            var where = t.StageLabel.Length > 0 ? t.StageLabel : t.StatusName;
            // The customer's emailed reply is the newest entry (§165): said first, as the portal's orange pill says it.
            if (t.CustomerReplied) where = "Customer replied · " + where;
            var it = new ListViewItem(ListColumns.Select(c => c.Key switch
            {
                TicketSort.Number => "#" + t.Number, TicketSort.Subject => t.Subject, TicketSort.Status => where, TicketSort.Due => DueFor(t),
                TicketSort.Priority => t.PriorityName, TicketSort.Client => t.ClientName.Length > 0 ? t.ClientName : "—",
                TicketSort.Assigned => t.AssignedTo.Length > 0 ? t.AssignedTo : "—", _ => Ago(t.UpdatedUtc)
            }).ToArray())
                { Tag = t, ToolTipText = $"{t.CategoryName} · {t.ClientName}" };
            if (t.Priority >= 3 || t.Overdue) it.ForeColor = _t.BadText;
            else if (t.CustomerReplied) it.ForeColor = _t.WarnText;
            else if (t.Status >= 3) it.ForeColor = _t.Muted;
            _list.Items.Add(it);
            if (_open != null && t.Id == _open.Ticket.Id) it.Selected = true;
        }
        _list.EndUpdate();
        _loading = false;
        _count.Text = _tickets.Count == 0 ? "Nothing here." : $"{_tickets.Count} ticket{(_tickets.Count == 1 ? "" : "s")}";
    });

    private void OnChanged(TicketSummary s) => Inv(() =>
    {
        // Somebody moved a ticket — maybe this one, maybe from the browser. The list is redrawn so
        // its stages are true; the open ticket is fetched again ONLY if it is the one that moved.
        Refresh_();
        if (_open != null && _open.Ticket.Id == s.Id) _link.OpenTicket(s.Id);
    });

    private void OpenSelected()
    {
        if (_loading || _list.SelectedItems.Count == 0) return;
        if (_list.SelectedItems[0].Tag is TicketSummary s)
        {
            if (_open?.Ticket.Id != s.Id) Forget();
            _link.OpenTicket(s.Id);
        }
    }

    /// <summary>Everything that belonged to the ticket that was open: another one is coming.</summary>
    private void Forget()
    {
        _notice = null; _tab = "activity"; _step = null; _drafts.Clear(); _partPhotoPaths.Clear();
        _machineStage = null; _machinePhotos = 0; _cancellingRecurring = false; _emailNote = 0; _posted = null;
        _picked.Clear(); _postedFiles = null;
    }

    private void OnNotice(TicketNoticeMsg n) => Inv(() =>
    {
        if (_open != null && n.TicketId != _open.Ticket.Id) return;
        _notice = n;
        // The edit card's save went through: what was typed in it is the ticket now. Refused, it stays to be put right.
        if (_editPending) { _editPending = false; if (n.Error.Length == 0) foreach (var k in _drafts.Keys.Where(k => k.StartsWith("edit:")).ToList()) _drafts.Remove(k); }
        // The server refused the address and posted nothing (2026-10-09): the words go back in the box, ticked.
        if (_posted != null && n.Error.EndsWith("has not been posted."))
        {
            _drafts["comment"] = _posted; _drafts["comment.email"] = "1";
            if (_postedFiles != null) _picked["comment"] = _postedFiles;
            if (_open != null) Draw(_open, keepScroll: true);
        }
        _posted = null; _postedFiles = null;
    });

    private void OnDetail(TicketDetail d) => Inv(() => Take(d, redraw: true));

    /// <summary>
    /// Draws one ticket. Public so tools/ticketwindowprobe can hand the real window a ticket and
    /// measure what it does with it, with no server in the loop.
    /// </summary>
    public void ShowTicket(TicketDetail d) => Take(d, redraw: false);

    private void Take(TicketDetail d, bool redraw)
    {
        bool same = _open?.Ticket.Id == d.Ticket.Id;
        if (!same) Forget();
        // The stage moved — the step just submitted went through, or somebody else moved it. The form
        // that was open belonged to the stage that has gone, and so does what was typed into it.
        else if (_open!.Stage != d.Stage)
        {
            _step = null;
            foreach (var k in _drafts.Keys.Where(k => k != "comment").ToList()) _drafts.Remove(k);
            // A step's picked files went with it (or stay loose on the server, cleared after a day).
            foreach (var k in _picked.Keys.Where(k => k != "comment").ToList()) _picked.Remove(k);
        }
        // Add another machine leaves the stage where it is: the new machine appearing beside this one
        // is what says it went through. Its form, what was typed and its photos are then done with.
        // On the same job (2026-10-02) it makes no ticket, so the machine appearing ON this one says it.
        else if (d.BookedWith.Count > _open.BookedWith.Count || d.Machines.Count > _open.Machines.Count)
        {
            if (_step == "add-machine") _step = null;
            foreach (var k in _drafts.Keys.Where(k => k.StartsWith("add-machine:")).ToList()) _drafts.Remove(k);
            _machineStage = null; _machinePhotos = 0;
        }
        _open = d;
        Draw(d, keepScroll: same && redraw);
    }

    // ================================================================== the open ticket

    // From the OUTER width, with room for a scrollbar always kept: the same width whether or not the
    // scrollbar is showing, so its coming and going never changes the layout (see the Resize handler).
    private int ContentWidth => Math.Max(300, _scroll.Width - SystemInformation.VerticalScrollBarWidth - 4);
    private int _drawnWidth;

    // Kept so the suggestion list and the did-you-mean can find them after a redraw.
    private InputBox? _itemBox, _partBox, _serialBox;
    private NumberBox? _qtyBox;
    private ListBox? _suggest;
    private FieldFrame? _suggestFrame;
    private readonly List<string> _partPhotoPaths = new();
    private Label? _partPhotoLabel;

    private void Draw(TicketDetail d, bool keepScroll)
    {
        var scrollY = keepScroll ? -_scroll.AutoScrollPosition.Y : 0;
        _scroll.Visible = true; _placeholder.Visible = false;
        // NativeDark.Apply ran when the window was SHOWN, while this side was hidden and had no handle
        // — so it skipped it, and the scrollbar stayed light on a dark console. Seen in a real screen
        // capture; DrawToBitmap draws every scrollbar light anyway, so no bitmap could have shown it.
        NativeDark.Scrollbars(_scroll, _t.IsDark);
        _scroll.SuspendLayout(); _body.SuspendLayout();
        foreach (Control c in _body.Controls.Cast<Control>().ToList()) c.Dispose();
        _body.Controls.Clear();
        _itemBox = _partBox = _serialBox = null; _qtyBox = null; _suggest = null; _suggestFrame = null; _partPhotoLabel = null;

        int w = ContentWidth;
        _drawnWidth = _scroll.Width;
        if (d.Stage.Length > 0) DrawStory(w, d); else DrawPlain(w, d);

        _body.ResumeLayout(); _scroll.ResumeLayout();
        _scroll.AutoScrollPosition = new Point(0, scrollY);
    }

    private void Add(Control c) => _body.Controls.Add(c);

    /// <summary>The notice from the last press, and anybody waiting on this person's reply.</summary>
    private void Banners(int w, TicketDetail d)
    {
        if (_notice is { } n && (n.Error.Length > 0 || n.Message.Length > 0))
            Add(Banner(w, n.Error.Length > 0 ? n.Error : n.Message, n.Error.Length > 0 ? _t.BadBg : _t.OkBg, n.Error.Length > 0 ? _t.BadText : _t.OkText));
        foreach (var who in d.AskedBy)
            Add(Banner(w, $"{who} asked you something on this ticket. Reply below and the flag clears.", _t.WarnBg, _t.WarnText));
        foreach (var who in d.RepliedBy)
            Add(Banner(w, $"{who} replied by email — it is below.", _t.WarnBg, _t.WarnText));
        if (d.CanClaim)
        {
            var take = Button_("Take this ticket", BtnStyle.Accent);
            take.Click += (_, _) => _link.UpdateTicket(new TicketUpdateMsg { TicketId = d.Ticket.Id, AssignedTo = _link.DisplayName });
            Add(take);
        }
    }

    /// <summary>
    /// The knowledge base on this ticket (3.8.5), as the server sent it: the client's articles, the
    /// ones written up from this ticket, and "Add to knowledge base" when this person is offered it.
    /// Each opens the portal, where articles are written with pictures. Nothing at all when none applies.
    /// </summary>
    private Control? KnowledgeRow(int w, TicketDetail d)
    {
        var k = d.Knowledge;
        if (k == null || (k.ClientCount == 0 && k.FromTicket.Count == 0 && !k.CanAdd)) return null;
        static string Short(string s) => s.Length <= 60 ? s : s[..57].TrimEnd() + "…";
        var row = Row(w);
        row.Margin = new Padding(0, _gap / 2, 0, 0);
        if (k.ClientCount > 0)
        {
            var b = Button_(Short($"📘 Knowledge base for {k.ClientName} ({k.ClientCount})"), BtnStyle.Ghost);
            var to = k.ClientNumber > 0 ? $"/portal/kb?c={k.ClientNumber}" : $"/portal/kb?client={k.ClientId}";
            b.Click += (_, _) => _link.RequestPortalLink(to);
            row.Controls.Add(b);
        }
        foreach (var a in k.FromTicket)
        {
            var b = Button_(Short("Written up: " + a.Title), BtnStyle.Ghost);
            var to = a.Number > 0 ? $"/portal/a/{a.Number}" : $"/portal/kb/{a.Id}";
            b.Click += (_, _) => _link.RequestPortalLink(to);
            row.Controls.Add(b);
        }
        if (k.CanAdd)
        {
            var b = Button_(k.FromTicket.Count > 0 ? "Add another to the knowledge base" : "Add to knowledge base", BtnStyle.Neutral);
            var to = $"/portal/kb/edit?t={d.Ticket.Number}";
            b.Click += (_, _) => _link.RequestPortalLink(to);
            row.Controls.Add(b);
        }
        return row;
    }

    // ================================================================== a ticket with no workflow

    /// <summary>General, remote support and call outs: a status box, a comment box and the history.</summary>
    private void DrawPlain(int w, TicketDetail d)
    {
        var t = d.Ticket;
        Add(Text_(w, $"#{t.Number}  {t.Subject}", _t.H2, _t.Ink));
        var meta = new List<string> { t.CategoryName };
        if (t.ClientName.Length > 0) meta.Add(t.ClientName);
        if (t.MachineName.Length > 0) meta.Add(t.MachineName);
        meta.Add($"opened {t.CreatedUtc.ToLocalTime():d MMM yyyy HH:mm} by {d.OpenedBy}");
        Add(Text_(w, string.Join("  ·  ", meta), _t.Small, _t.Muted));
        if (KnowledgeRow(w, d) is { } kb) Add(kb);
        JobLinks(w, d);
        Banners(w, d);
        if (RecurringCard(w, d) is { } rc) Add(rc);
        if (t.Category == (int)TicketCategoryKind.CallOut) Add(CallOutCard(w, d));
        if (!d.IsClosed)
        {
            var (card, col, inner) = Section(w, "Comment", _t.Pink);
            CommentBox(col, inner, d);
            col.Controls.Add(Text_(inner, "People on it, status and priority", _t.BodyBold, _t.Ink));
            AssignRow(col, inner, d, withStatus: true);
            Add(card);
        }
        Add(EditCard(w, d));
        Add(HistoryCard(w, d));
    }

    // ================================================================== a workflow ticket: the story

    private void DrawStory(int w, TicketDetail d)
    {
        var t = d.Ticket;
        Add(Text_(w, $"#{t.Number} – {t.Subject}", _t.H2, _t.Ink));
        // Straight to the edit card, on the Details tab (2026-10-13).
        if (_tab != "details")
        {
            var edit = Button_("✎ Edit the ticket", BtnStyle.Ghost);
            edit.Click += (_, _) => { _tab = "details"; Draw(d, keepScroll: false); };
            Add(edit);
        }
        Add(Crumb(w, d));
        var meta = new List<string> { $"Opened by {d.OpenedBy} on {t.CreatedUtc.ToLocalTime():dd/MM/yyyy}" };
        if (d.WithWhom.Length > 0) meta.Add(d.WithWhom);
        if (t.ClientName.Length > 0) meta.Add(t.ClientName);
        Add(Text_(w, string.Join("  ·  ", meta), _t.Small, _t.Muted));
        if (KnowledgeRow(w, d) is { } kb) Add(kb);
        if (d.BookedWith.Count > 0)
        {
            // Machines the client brought in together (2026-09-30): equals, each on its own route.
            Add(Text_(w, "Booked in with:", _t.Small, _t.Muted));
            var row = Row(w);
            foreach (var b in d.BookedWith)
            {
                var go = Button_($"#{b.Number} {b.Subject}  ({b.StageLabel})", BtnStyle.Ghost);
                int id = b.Id;
                go.Click += (_, _) => { _step = null; _link.OpenTicket(id); };
                row.Controls.Add(go);
            }
            Add(row);
        }
        JobLinks(w, d);

        if (d.Actions.Count > 0) Add(StepButtons(w, d));
        else if (d.IsClosed) Add(Text_(w, "This job is closed.", _t.Body, _t.Muted));
        else Add(Text_(w, "Nothing here for you to do at this stage.", _t.Small, _t.Muted));
        Banners(w, d);
        if (RecurringCard(w, d) is { } rc) Add(rc);

        bool quoteTab = QuoteTab(d);
        if (_tab == "quote" && !quoteTab) _tab = "activity";
        Add(Tabs(w, d, quoteTab));
        switch (_tab)
        {
            case "photos": Add(PhotosPane(w, d)); break;
            case "quote": Add(QuotePane(w, d)); break;
            case "details": DetailsPane(w, d); break;
            default: ActivityPane(w, d); break;
        }
    }

    /// <summary>
    /// The job this ticket is part of, with "Not part of it", and the tickets that are part of THIS one, each with where
    /// it has got to (2026-10-09). Neither showed anywhere in the console before, so a ticket could not be taken out of
    /// a job, and a job's open parts could not be seen from it. Each opens that ticket.
    /// </summary>
    private void JobLinks(int w, TicketDetail d)
    {
        static string Short(string s) => s.Length <= 60 ? s : s[..57].TrimEnd() + "…";
        if (d.Parent is { } p)
        {
            Add(Text_(w, "Part of:", _t.Small, _t.Muted));
            var row = Row(w);
            var go = Button_(Short($"#{p.Number} {p.Subject}  ({p.StageLabel})"), BtnStyle.Ghost);
            int pid = p.Id;
            go.Click += (_, _) => { _step = null; _link.OpenTicket(pid); };
            row.Controls.Add(go);
            var off = Button_("Not part of it", BtnStyle.Neutral);
            off.Click += (_, _) =>
            {
                if (MessageBox.Show(this, $"Take #{d.Ticket.Number} out of #{p.Number} {p.Subject}?\n\nIt stays open as a ticket of its own.",
                        "Not part of it", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                _notice = null;
                _link.SetTicketParent(d.Ticket.Id, 0);
            };
            row.Controls.Add(off);
            Add(row);
        }
        if (d.Children.Count > 0)
        {
            int open = d.Children.Count(c => c.Open);
            Add(Text_(w, open == 0 ? "Part of this job:"
                : $"Part of this job — {open} still open, and this job cannot be closed until {(open == 1 ? "it is" : "they are")}:",
                _t.Small, open == 0 ? _t.Muted : _t.WarnText));
            var row = Row(w);
            foreach (var c in d.Children)
            {
                var go = Button_(Short($"#{c.Number} {c.Subject}  ({c.StageLabel})"), BtnStyle.Ghost);
                int cid = c.Id;
                go.Click += (_, _) => { _step = null; _link.OpenTicket(cid); };
                row.Controls.Add(go);
            }
            Add(row);
        }
    }

    /// <summary>
    /// What a job still waiting on its parts says when somebody tries to close it — the server's own words (see
    /// TicketService.CheckChildrenClosedAsync), shown as a pop-up BEFORE anything is sent (Clint, 2026-10-09). The
    /// server refuses it anyway; this is so the person is told at once, in a window, which tickets to close.
    /// </summary>
    public static string? OpenPartsRefusal(TicketDetail d) =>
        JobParts.Refusal(d.Children.Where(c => c.Open).Select(c => (c.Number, c.Subject)));

    // Once there is a quote — the Quote needed step has its own item box before that.
    private static bool QuoteTab(TicketDetail d) => d.QuoteLines.Count > 0 || d.ShowQuote;

    /// <summary>"Workshop › where it was › WHERE IT IS" — the last step, and the stage as a badge.</summary>
    private Control Crumb(int w, TicketDetail d)
    {
        var row = Row(w);
        row.Margin = new Padding(0, 0, 0, _gap / 2);
        var words = d.Ticket.CategoryName + "  ›" + (d.PreviousStageLabel.Length > 0 ? $"  {d.PreviousStageLabel}  ›" : "");
        var pill = new Pill(_t, d.StageLabel, d.IsClosed ? _t.NeutralBg : _t.OkBg, d.IsClosed ? _t.Muted : _t.OkText);
        var ph = TextRenderer.MeasureText("Ag", pill.Font).Height + 8;
        pill.Size = new Size(Math.Min(w - 20, TextRenderer.MeasureText(d.StageLabel, pill.Font).Width + ph), ph);
        var lab = new Label
        {
            Text = words, Font = _t.Body, ForeColor = _t.Muted, BackColor = Color.Transparent, AutoSize = true,
            MaximumSize = new Size(w, 0), Margin = new Padding(0, (ph - _line) / 2, _gap / 2, 0), UseMnemonic = false
        };
        pill.Margin = new Padding(0);
        row.Controls.Add(lab); row.Controls.Add(pill);
        return row;
    }

    /// <summary>What a step button does: open its form, hand over to the browser, or just do it.</summary>
    private static bool HasForm(StageActionInfo a) => a.Fields.Count > 0 || a.Key is "quote-needed" or "collected" or "send-for-delivery";
    private static bool InBrowser(StageActionInfo a) => a.Fields.Contains("SupplierEmail");

    /// <summary>The next steps, as the server sent them. The pointing hand is the current system's.</summary>
    private Control StepButtons(int w, TicketDetail d)
    {
        var row = Row(w);
        row.Margin = new Padding(0, _gap, 0, 0);
        foreach (var a in d.Actions)
        {
            // Back (2026-10-13) is a way out, not the way on: plain beside the steps forward, as in the portal.
            bool back = a.Key == "go-back";
            var b = Button_((back ? "↶ " : "☞ ") + (InBrowser(a) ? "Request quote" : a.Label), a.IsDanger ? BtnStyle.Danger : back ? BtnStyle.Neutral : BtnStyle.Accent);
            if (InBrowser(a))
                b.Click += (_, _) => _link.RequestPortalLink(Portal.Ticket(d.Ticket) + "?open=quote-requested");
            else if (HasForm(a))
                b.Click += (_, _) => { _step = a.Key; _tab = "activity"; Draw(d, keepScroll: false); };
            else
                b.Click += (_, _) => Press(d, a, new TicketActMsg { TicketId = d.Ticket.Id, Action = a.Key });
            row.Controls.Add(b);
        }
        return row;
    }

    private Control Tabs(int w, TicketDetail d, bool quoteTab)
    {
        var row = Row(w);
        row.Margin = new Padding(0, _gap, 0, 0);
        void tab(string key, string text)
        {
            var b = Button_(text, _tab == key ? BtnStyle.Accent : BtnStyle.Neutral);
            b.Click += (_, _) => { if (_tab == key) return; _tab = key; Draw(d, keepScroll: false); };
            row.Controls.Add(b);
        }
        tab("activity", "Activity");
        // Photos are of a workshop machine; a call out has none until it goes to the workshop.
        if (IsWorkshop(d)) tab("photos", $"Photos ({d.PhotosIn} in · {d.PhotosOut} out)");
        if (quoteTab) tab("quote", d.QuoteLines.Count > 0 ? $"Quote ({d.QuoteLines.Count})" : "Quote");
        tab("details", "Details");
        return row;
    }

    /// <summary>A step. Nothing is sent that the server has not been asked for; it checks it all again.</summary>
    private void Press(TicketDetail d, StageActionInfo a, TicketActMsg msg)
    {
        // The server says which steps finish the job (Abandoned does not: walking away is not claiming it is done).
        if (a.ClosesJob && OpenPartsRefusal(d) is { } parts)
        {
            MessageBox.Show(this, parts, $"#{d.Ticket.Number} — not closed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (a.Closes && !HasForm(a) && MessageBox.Show(this, $"{a.Label}?", $"#{d.Ticket.Number}", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _notice = null;
        _link.ActOnTicket(msg);
    }

    // ---- Activity: the comment box, the open step, then the story newest first ----

    private void ActivityPane(int w, TicketDetail d)
    {
        if (!d.IsClosed)
        {
            var (card, col, inner) = Box(w);
            CommentBox(col, inner, d);
            Add(card);
        }
        if (_step != null && d.Actions.FirstOrDefault(a => a.Key == _step) is { } open && !InBrowser(open))
            Add(Numbered(w, d.Notes.Count + 1, inner => StepForm(inner, d, open), highlight: true));

        for (int i = d.Notes.Count - 1; i >= 0; i--)
        {
            var n = d.Notes[i];
            Add(Numbered(w, i + 1, inner => Entry(inner, n)));
        }
        Add(Numbered(w, 0, inner => Booking(inner, d)));
    }

    /// <summary>One entry of the story: its number in a circle, and a card beside it.</summary>
    private Control Numbered(int w, int number, Func<int, IEnumerable<Control>> content, bool highlight = false)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Margin = new Padding(0, _gap, 0, 0)
        };
        var dot = new Pill(_t, number.ToString(), highlight ? _t.Accent : _t.NeutralBg, highlight ? Theme.On(_t.Accent) : _t.Ink);
        int s = Math.Max(TextRenderer.MeasureText("99", dot.Font).Width + 8, TextRenderer.MeasureText("Ag", dot.Font).Height + 8);
        dot.Size = new Size(s, s);
        dot.Margin = new Padding(0, _gap, _gap, 0);
        var (card, col, inner) = Box(w - s - _gap - 2);
        card.Margin = new Padding(0);
        foreach (var c in content(inner)) col.Controls.Add(c);
        row.Controls.Add(dot); row.Controls.Add(card);
        return row;
    }

    private IEnumerable<Control> Entry(int inner, TicketNoteInfo n)
    {
        var when = n.CreatedUtc.ToLocalTime().ToString("d MMM yyyy, HH:mm");
        // A step gone back over (2026-10-13): still in the story, its words greyed, and said plainly.
        bool undone = n.Undone.Length > 0;
        var ink = undone ? _t.Muted : _t.Ink;
        if (n.FormTitle.Length > 0)
        {
            // A step: what was filled in, under the step's name, and where it moved the job. A "Back to …" says so.
            var title = n.WentBack ? "↶ " + n.FormTitle : n.FormTitle + (n.MovedToLabel.Length > 0 ? "   →  " + n.MovedToLabel : "");
            yield return new SectionTitle(_t, title, n.WentBack ? _t.WarnText : undone ? _t.Muted : _t.Accent)
                { Width = inner, Margin = new Padding(0, 0, 0, _gap / 2) };
            if (n.Body.Trim().Length > 0) yield return Text_(inner, n.Body, _t.Body, ink);
            foreach (var c in NoteFiles(inner, n)) yield return c;
            yield return Text_(inner, $"{n.Author}  ·  {when}", _t.Small, _t.Muted);
            // The damage report goes to the client from its step, and can be sent again.
            if (n.FormTitle == "Damage report needed") foreach (var c in NoteEmail(inner, n)) yield return c;
        }
        else if (n.IsSystem)
        {
            yield return Text_(inner, $"{n.Author} — {n.Body}  ·  {when}", _t.Small, _t.Muted);
        }
        else
        {
            yield return Text_(inner, $"{n.Author}  ·  {when}", _t.BodyBold, _t.Ink);
            if (n.Body.Trim().Length > 0) yield return Text_(inner, n.Body, _t.Body, ink);
            foreach (var c in NoteFiles(inner, n)) yield return c;
            foreach (var c in NoteEmail(inner, n)) yield return c;
        }
        if (undone) yield return Text_(inner, "↶ " + n.Undone, _t.BodyBold, _t.WarnText);
    }

    /// <summary>A comment's files and photos (2026-10-13): a button each, opening it in the browser — a picture or a PDF
    /// shown there, anything else downloaded.</summary>
    private IEnumerable<Control> NoteFiles(int inner, TicketNoteInfo n)
    {
        if (n.Attachments.Count == 0) yield break;
        var row = Row(inner);
        foreach (var f in n.Attachments)
        {
            var to = f.Token.Length > 0 ? $"/portal/f/{f.Token}" : $"/portal/attachment/{f.Id}";
            var b = Button_((f.IsImage ? "🖼 " : "📎 ") + f.Name + (f.Size.Length > 0 ? $" ({f.Size})" : ""), BtnStyle.Ghost);
            b.Click += (_, _) => _link.RequestPortalLink(to);
            row.Controls.Add(b);
        }
        yield return row;
    }

    // ---- files picked for a comment or a step, before it is posted (2026-10-13) ----

    /// <summary>What has been picked and uploaded, by form: "comment", or a step's key. Kept across redraws, and put back
    /// when the server refuses the comment.</summary>
    private readonly Dictionary<string, List<AttachmentInfo>> _picked = new();
    private List<long> PickedIds(string key) => _picked.TryGetValue(key, out var l) ? l.Select(f => f.Id).ToList() : new();

    /// <summary>"Attach files or photos…", and each picked file with × to take it off again.</summary>
    private Control FilePicker(int inner, TicketDetail d, string key, string label)
    {
        var col = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, _gap / 2)
        };
        var pick = Button_(label, BtnStyle.Neutral);
        pick.Click += async (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Title = label.TrimEnd('…'), Multiselect = true,
                Filter = "Photos, PDFs, documents|*.jpg;*.jpeg;*.png;*.webp;*.heic;*.gif;*.pdf;*.doc;*.docx;*.xls;*.xlsx;*.ppt;*.pptx;*.odt;*.ods;*.rtf;*.txt;*.csv;*.log;*.eml;*.msg;*.zip;*.mp4;*.mov|All files|*.*"
            };
            if (dlg.ShowDialog(this) != DialogResult.OK || dlg.FileNames.Length == 0) return;
            pick.Enabled = false; var was = pick.Text; pick.Text = "Uploading…";
            try
            {
                var (files, error) = await _link.UploadAttachmentsAsync(d.Ticket.Id, dlg.FileNames,
                    progress: (i, n) => { try { BeginInvoke(() => pick.Text = n > 1 ? $"Uploading {i} of {n}…" : "Uploading…"); } catch { } });
                if (!_picked.TryGetValue(key, out var list)) _picked[key] = list = new();
                list.AddRange(files);
                if (error.Length > 0) MessageBox.Show(this, error, "Files", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { pick.Text = was; pick.Enabled = true; }
            // What was typed is in the drafts, so the redraw keeps it.
            if (_open != null) Draw(_open, keepScroll: true);
        };
        col.Controls.Add(pick);
        foreach (var f in _picked.TryGetValue(key, out var picked) ? picked.ToList() : new())
        {
            var row = Row(inner);
            row.Margin = new Padding(0, _gap / 3, 0, 0);
            var name = Text_(inner - _line * 4, (f.IsImage ? "🖼 " : "📎 ") + f.Name + (f.Size.Length > 0 ? $" ({f.Size})" : ""), _t.Body, _t.Ink);
            name.Margin = new Padding(0, 6, _gap / 2, 0);
            var off = Button_("×", BtnStyle.Neutral);
            off.Margin = new Padding(0);
            var file = f;
            off.Click += async (_, _) =>
            {
                _picked[key].Remove(file);
                if (_open != null) Draw(_open, keepScroll: true);
                await _link.RemoveAttachmentAsync(file.Id);
            };
            row.Controls.Add(name); row.Controls.Add(off);
            col.Controls.Add(row);
        }
        return col;
    }

    private static bool IsWorkshop(TicketDetail d) => d.Ticket.Category == (int)TicketCategoryKind.Workshop;
    /// <summary>What it STARTED as: a workshop job may have been a call out or a walk in, whose
    /// "Needs workshop work" / "Needs to be booked in" entry says so.</summary>
    private static TicketCategoryKind Origin(TicketDetail d) =>
        !IsWorkshop(d) ? (TicketCategoryKind)d.Ticket.Category
        : d.Notes.Any(n => n.FormTitle == "Needs workshop work") ? TicketCategoryKind.CallOut
        : d.Notes.Any(n => n.FormTitle == "Needs to be booked in") ? TicketCategoryKind.WalkIn
        : TicketCategoryKind.Workshop;
    /// <summary>A call out — or a workshop job that started as one and still has its site.</summary>
    private static bool StartedAsCallOut(TicketDetail d) => Origin(d) == TicketCategoryKind.CallOut;

    /// <summary>How it started: the booking, at the bottom — the current system's "created" card — and what their screen
    /// looked like when they asked for help (2026-10-09), a button per picture, opening it full size in the browser.</summary>
    private IEnumerable<Control> Booking(int inner, TicketDetail d)
    {
        foreach (var c in BookingFacts(inner, d)) yield return c;
        if (d.Screenshots.Count == 0) yield break;
        var row = Row(inner);
        for (int i = 0; i < d.Screenshots.Count; i++)
        {
            var to = i < d.ScreenshotTokens.Count ? $"/portal/p/{d.ScreenshotTokens[i]}" : $"/portal/photo/{d.Screenshots[i]}";
            var b = Button_(d.Screenshots.Count == 1 ? "🖼 See their screen" : $"🖼 Their screen ({i + 1})", BtnStyle.Ghost);
            b.Click += (_, _) => _link.RequestPortalLink(to);
            row.Controls.Add(b);
        }
        yield return row;
    }

    private IEnumerable<Control> BookingFacts(int inner, TicketDetail d)
    {
        var origin = Origin(d);
        if (origin is not (TicketCategoryKind.CallOut or TicketCategoryKind.Workshop))
        {
            // A walk in, a remote session or a General ticket: who opened it, and what it said.
            var how = origin switch
            {
                TicketCategoryKind.WalkIn => "booked the walk in",
                TicketCategoryKind.RemoteSupport => "opened the remote support ticket",
                _ => "opened the ticket"
            };
            yield return Text_(inner, $"{d.OpenedBy} {how}  ·  {d.Ticket.CreatedUtc.ToLocalTime():d MMM yyyy, HH:mm}", _t.BodyBold, _t.Ink);
            var rows = new List<(string, string)>();
            if (d.Ticket.MachineName.Length > 0) rows.Add(("Machine", d.Ticket.MachineName));
            rows.Add(("Client", d.Ticket.ClientName.Length > 0 ? d.Ticket.ClientName : "none"));
            if (d.Description.Trim().Length > 0) rows.Add((origin == TicketCategoryKind.General ? "Details" : "Fault reported", d.Description.Trim()));
            foreach (var row in FactRows(inner, rows)) yield return row;
            yield break;
        }
        if (StartedAsCallOut(d))
        {
            yield return Text_(inner, $"{d.OpenedBy} booked the call out  ·  {d.Ticket.CreatedUtc.ToLocalTime():d MMM yyyy, HH:mm}", _t.BodyBold, _t.Ink);
            var site = new List<(string, string)>
            {
                ("Site address", d.SiteAddress.Length > 0 ? d.SiteAddress : "—"),
                ("Appointment", d.Ticket.AppointmentUtc?.ToLocalTime().ToString("dddd d MMMM 'at' HH:mm") ?? "not scheduled"),
            };
            if (d.Description.Trim().Length > 0) site.Add(("Fault reported", d.Description.Trim()));
            foreach (var row in FactRows(inner, site)) yield return row;
            yield break;
        }
        yield return Text_(inner, $"{d.OpenedBy} booked the machine in  ·  {d.Ticket.CreatedUtc.ToLocalTime():d MMM yyyy, HH:mm}", _t.BodyBold, _t.Ink);
        var facts = new List<(string, string)>
        {
            (d.Machines.Count > 0 ? "Machine 1" : "Machine", ($"{d.AssetMake} {d.AssetModel}".Trim() is { Length: > 0 } mm ? mm : "—") + (d.AssetSerial.Length > 0 ? $" · serial {d.AssetSerial}" : "")),
            ("Accessories", d.Accessories.Length > 0 ? d.Accessories : "none recorded"),
        };
        if (d.Description.Trim().Length > 0) facts.Add(("Fault reported", d.Description.Trim()));
        // Its OWN photos in: a further machine's are counted on its own card (2026-10-02).
        facts.Add(("Photos in", (d.PhotosIn - d.Machines.Sum(m => m.PhotosIn)).ToString()));
        foreach (var row in FactRows(inner, facts)) yield return row;
    }

    // ---- the open step's form ----

    private string FieldLabel(StageActionInfo a, string f) => f switch
    {
        "Details" => a.Key == "inspection" ? "What did the inspection find?"
            : a.Key == "add-machine" ? "What is wrong with it?"
            : a.Key == "delivered" ? "What was done there? (setting up a printer, for example)"
            : a.Key is "remote-invoice" or "general-invoice" ? "What was done?" : "What was done to the machine?",
        "ParentTicket" => "Which of this client's open tickets is it part of?",
        "Minutes" => "Time spent — minutes, or 1:30",
        "Amount" => a.Key == "quote-sent" ? "Quote amount" : a.Key is "paid-cash" or "paid-card" ? "Amount paid"
            : a.Key == "delivery-invoice" ? "Amount to invoice for the delivery's work"
            : a.IsDecision ? "Amount to invoice" : "Invoice amount",
        "Reference" => a.Key == "quote-sent" ? "Quote reference, from your invoicing system"
            : a.Key == "paid-card" ? "Card machine reference number" : "Invoice number, from your accounting package",
        "Reason" => a.Key == "go-back" ? "Why is it going back? (something changed, pressed by mistake…)"
            : a.Key == "decline-dni" ? "Why it should be invoiced after all" : a.Key == "abandon" ? "Why it is being abandoned" : a.Key == "not-delivered" ? "Why it was not delivered"
            : a.Key == "callout-reschedule" ? "Why is it being moved? (the customer asked, nobody free…)" : "Why it should not be invoiced",
        "Appointment" => "New date and time",
        "AssetMake" => "Make",
        "AssetModel" => "Model",
        "AssetSerial" => "Serial number",
        "Accessories" => "Accessories left with it",
        "DevicePassword" => "Password to sign in (leave empty if none)",
        "Comments" => "Comments (optional) — the client gets these with the report if it is emailed",
        "Files" => "The damage report — a PDF, a Word document or photos of it",
        _ => f
    };

    /// <summary>"of the machine", or how many of each when the job has several (2026-10-02).</summary>
    private static string OfEach(TicketDetail d) => d.Machines.Count == 0 ? " of the machine"
        : $" ({OutEach(d)} of each of the {d.Machines.Count + 1} machines on this job)";

    /// <summary>Photos out of EACH machine this job asks for: 3, or 0 when its Job types did not ask (§168).</summary>
    private static int OutEach(TicketDetail d) => d.PhotosOutRequired / (d.Machines.Count + 1);

    /// <summary>The line over Add another machine's photos — none asked for when this job's Job types said so (§168).</summary>
    private static string MachinePhotosLine(TicketDetail d, int added) => (d.PhotosRequired == 0
        ? "Photos of THAT machine are not asked for, but you can add some."
        : $"Take at least {d.PhotosRequired} photos of THAT machine as it arrived, and as many more as the damage needs.") + $" {added} added so far.";

    /// <summary>The Details box of Add another machine, by the choice above it — the portal's words.</summary>
    private static string MachineDetailsLabel(bool sameJob) => sameJob
        ? "What is this machine on the job? (optional — for example: the new laptop, the data goes onto this one)"
        : "What is wrong with it?";

    /// <summary>What a step may leave empty — the booking's machine details, as at the counter. The
    /// server holds the same list (Workflow.Optional) and is the one that decides.</summary>
    private static bool IsOptional(string f) => f is "AssetMake" or "AssetModel" or "AssetSerial" or "Accessories" or "DevicePassword" or "Comments" or "EmailClient";

    private IEnumerable<Control> StepForm(int inner, TicketDetail d, StageActionInfo a)
    {
        // The step's name, and Cancel on the same line.
        var head = Row(inner);
        var cancel = Button_("Cancel", BtnStyle.Neutral);
        cancel.Margin = new Padding(0);
        cancel.Click += (_, _) => { _step = null; Draw(d, keepScroll: true); };
        var title = a.Label;
        head.Controls.Add(new SectionTitle(_t, title, _t.Accent) { Width = inner - cancel.Width - _gap - 2, Margin = new Padding(0, 0, _gap, 0) });
        head.Controls.Add(cancel);
        yield return head;
        // What going back cannot take back (2026-10-13): an email sent, an invoice issued, a machine gone.
        if (a.Warning.Length > 0) yield return Banner(inner, "⚠ " + a.Warning, _t.WarnBg, _t.WarnText);

        if (a.Key == "quote-needed")
        {
            yield return Text_(inner, "What parts does the supplier need to quote for? Click the Item box to choose from the list, or type to narrow it. Not on the list? Type it and press Add.", _t.Small, _t.Muted);
            foreach (var c in QuoteEditor(inner, d)) yield return c;
            var send = Button_("Submit — send to the office for a quote", BtnStyle.Accent);
            send.Margin = new Padding(0, _gap, 0, 0);
            send.Click += (_, _) =>
            {
                if (d.QuoteLines.Count == 0) { _notice = new TicketNoticeMsg { TicketId = d.Ticket.Id, Error = "Add at least one item for the supplier to quote." }; Draw(d, keepScroll: true); return; }
                Press(d, a, new TicketActMsg { TicketId = d.Ticket.Id, Action = a.Key });
            };
            yield return send;
            yield break;
        }

        if (a.Key is "collected" or "send-for-delivery")
        {
            // A delivery's photos are taken at the office, before it goes (Clint, 2026-09-30).
            bool deliver = a.Key == "send-for-delivery";
            yield return Text_(inner, (d.PhotosOutRequired == 0
                ? (deliver ? "A technician takes it to the customer. " : "") + $"Photos {(d.Machines.Count > 0 ? "of the machines as they leave" : "of it as it leaves")} are not asked for, but you can add some."
                : deliver
                ? $"A technician takes it to the customer. Take at least {d.PhotosOutRequired} photos{OfEach(d)} here at the office, before {(d.Machines.Count > 0 ? "they leave" : "it leaves")}, and as many more as you like."
                : $"Take at least {d.PhotosOutRequired} photos{OfEach(d)} {(d.Machines.Count > 0 ? "as they leave" : "as it leaves")} — ideally in front of the customer — and as many more as you like.")
                + $" {d.PhotosOut} added so far.", _t.Body, _t.Ink);
            var row = Row(inner);
            var photos = Button_("Add photos as it leaves…", BtnStyle.Neutral);
            photos.Click += async (_, _) => await AddOutPhotosAsync(d.Ticket.Id, photos);
            var done = Button_(deliver ? "Submit — it is going out for delivery" : "Submit — the machine has been collected", BtnStyle.Accent);
            done.Click += (_, _) => Press(d, a, new TicketActMsg { TicketId = d.Ticket.Id, Action = a.Key });
            row.Controls.Add(photos); row.Controls.Add(done);
            yield return row;
            yield break;
        }

        if (a.Key is "needs-workshop" or "walkin-book-in")
        {
            // Booking it in to the workshop: the photos a counter booking needs, first.
            yield return Text_(inner, "It becomes a workshop job at Booked in, keeping this number and everything below. " + (d.PhotosRequired == 0
                ? "Photos of the machine are not asked for, but you can add some."
                : $"Take at least {d.PhotosRequired} photos of the machine as it goes — the lid, the base and the screen — and as many more as the damage needs.") + $" {d.PhotosIn} added so far.", _t.Body, _t.Ink);
            var pics = Button_("Add photos as it arrives…", BtnStyle.Neutral);
            pics.Click += async (_, _) => await AddPhotosAsync(d.Ticket.Id, pics, "before");
            yield return pics;
        }
        if (a.Key == "add-machine")
        {
            // Another machine the client brought in with this one: a ticket of its own, whose photos
            // are staged as they go up and attached when it is created — as the booking dialog does.
            _machineStage ??= Guid.NewGuid().ToString("N");
            var count = Text_(inner, MachinePhotosLine(d, _machinePhotos), _t.Body, _t.Ink);
            yield return count;
            var pics = Button_("Add photos of this machine…", BtnStyle.Neutral);
            pics.Click += async (_, _) =>
            {
                using var pick = new OpenFileDialog
                {
                    Title = "Photos of the other machine as it arrived",
                    Filter = "Photos|*.jpg;*.jpeg;*.png;*.webp;*.heic|All files|*.*", Multiselect = true
                };
                if (pick.ShowDialog(this) != DialogResult.OK || pick.FileNames.Length == 0) return;
                pics.Enabled = false; var was = pics.Text; pics.Text = "Uploading…";
                try
                {
                    var (uploaded, error) = await _link.UploadPhotosAsync(pick.FileNames, "before", _machineStage!,
                        progress: (i, n) => { try { BeginInvoke(() => pics.Text = n > 1 ? $"Uploading {i} of {n}…" : "Uploading…"); } catch { } });
                    _machinePhotos += uploaded;
                    // Only the count changes; the form is not redrawn, so nothing typed is lost.
                    count.Text = MachinePhotosLine(d, _machinePhotos);
                    if (error.Length > 0) MessageBox.Show(this, error, "Photos", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally { pics.Text = was; pics.Enabled = true; }
            };
            yield return pics;
        }

        var boxes = new Dictionary<string, InputBox>();
        ComboBox? parentPick = null;
        DateBox? apptDate = null;
        TimeBox? apptTime = null;
        // Add another machine: same job or separate (2026-10-02). It decides what the Details box asks.
        ComboBox? sameJobPick = null;
        Label? detailsCaption = null;
        // The damage report's email (2026-10-13): ticked by default, to the ticket's contact, changeable.
        RoundCheck? emailTick = null;
        Func<string>? emailTo = null;
        foreach (var f in a.Fields)
        {
            if (f == "Files")
            {
                yield return FieldCaption(inner, FieldLabel(a, f));
                yield return FilePicker(inner, d, a.Key, "📎 Attach the damage report…");
                continue;
            }
            if (f == "EmailClient")
            {
                if (d.ClientUpdate is not { Ready: true } cu)
                {
                    if (d.ClientUpdate is { WhyNot.Length: > 0 } no)
                        yield return Text_(inner, no.WhyNot + " Until then, record it here and send the report to the client yourself.", _t.Small, _t.Muted);
                    continue;
                }
                var tickKey = $"{a.Key}:email";
                emailTick = new RoundCheck(_t)
                {
                    Text = "Email the damage report to the client", Checked = !_drafts.TryGetValue(tickKey, out var t0) || t0 == "1", AutoSize = true,
                    Font = _t.BodyBold, ForeColor = _t.Ink, BackColor = Color.Transparent, Margin = new Padding(0, _gap / 2, 0, _gap / 2), UseMnemonic = false
                };
                var (chooser, address) = AddressChooser(inner, cu, $"{a.Key}:to",
                    $"They get the report attached, your comments above, and the fault the ticket was opened for, from {cu.From} — not the time or the amount. Their reply comes back here.");
                emailTo = address;
                chooser.Visible = emailTick.Checked;
                var tick = emailTick;
                tick.CheckedChanged += (_, _) => { chooser.Visible = tick.Checked; _drafts[tickKey] = tick.Checked ? "1" : "0"; };
                yield return tick;
                yield return chooser;
                continue;
            }
            if (f == "SameJob")
            {
                var client = d.Ticket.ClientName.Length > 0 ? d.Ticket.ClientName : "the same client";
                yield return FieldCaption(inner, "Is it part of this job?");
                sameJobPick = new ThemedCombo(_t) { Font = _t.Body, FlatStyle = FlatStyle.Flat, Width = inner, Margin = new Padding(0, 0, 0, _gap / 2), DropDownStyle = ComboBoxStyle.DropDownList };
                sameJobPick.Items.Add($"No — a separate job: a ticket of its own for {client}, linked to this one");
                sameJobPick.Items.Add("Yes — the same job, on this ticket (a data transfer's old and new machine)");
                // Kept with what was typed, so a refusal redrawing the form never turns "same job" back
                // into a separate ticket nobody chose.
                var sjKey = $"{a.Key}:SameJob";
                sameJobPick.SelectedIndex = _drafts.TryGetValue(sjKey, out var sj) && sj == "yes" ? 1 : 0;
                sameJobPick.SelectedIndexChanged += (_, _) =>
                {
                    _drafts[sjKey] = sameJobPick.SelectedIndex == 1 ? "yes" : "no";
                    if (detailsCaption != null) detailsCaption.Text = MachineDetailsLabel(sameJobPick.SelectedIndex == 1);
                };
                yield return sameJobPick;
                continue;
            }
            var caption = FieldCaption(inner, a.Key == "add-machine" && f == "Details" ? MachineDetailsLabel(sameJobPick?.SelectedIndex == 1) : FieldLabel(a, f));
            if (a.Key == "add-machine" && f == "Details") detailsCaption = caption;
            yield return caption;
            if (f == "ParentTicket")
            {
                // One of this client's open tickets — the server sent the list, and checks the choice again.
                if (d.ClientId == 0)
                    yield return Text_(inner, "This ticket is not linked to a client yet. Link it on the Details tab first — it can only be part of another job for the same client.", _t.Small, _t.Muted);
                else if (d.ParentChoices.Count == 0)
                    yield return Text_(inner, $"{(d.Ticket.ClientName.Length > 0 ? d.Ticket.ClientName : "This client")} has no other open tickets to file this under.", _t.Small, _t.Muted);
                else
                {
                    parentPick = new ThemedCombo(_t) { Font = _t.Body, FlatStyle = FlatStyle.Flat, Width = inner, Margin = new Padding(0, 0, 0, _gap / 2) };
                    parentPick.Items.Add("— choose —");
                    foreach (var c in d.ParentChoices) parentPick.Items.Add(c);
                    parentPick.SelectedIndex = 0;
                    yield return parentPick;
                }
                continue;
            }
            if (f == "Appointment")
            {
                // Reschedule (2026-10-05). Starts at the current appointment, so moving it a day is one
                // change; this PC's clock, as New ticket's appointment is. The server refuses the past.
                var booked = d.Ticket.AppointmentUtc?.ToLocalTime();
                yield return Text_(inner, booked.HasValue ? $"Booked for {booked.Value:dddd d MMMM yyyy 'at' HH:mm}." : "Not scheduled yet.", _t.Small, _t.Muted);
                var apptKey = $"{a.Key}:Appointment";
                var start = _drafts.TryGetValue(apptKey, out var draft) && DateTime.TryParse(draft, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var kept)
                    ? kept : booked ?? DateTime.Today.AddDays(1).AddHours(9);
                var when = Row(inner);
                apptDate = new DateBox(_t) { Width = inner * 3 / 5, Height = _fieldH, Value = start.Date, Margin = new Padding(0, 0, _gap, _gap / 2) };
                // Our own date box and a quarter-hour dropdown (2026-10-06): Windows' date pickers are
                // square and stay white on the dark console.
                apptTime = new TimeBox(_t) { Width = inner - inner * 3 / 5 - _gap - 2, Value = DateTime.Today.Add(start.TimeOfDay), Margin = new Padding(0, 0, 0, _gap / 2) };
                var (dp, tp) = (apptDate, apptTime);
                void Keep(object? _, EventArgs __) => _drafts[apptKey] = dp.Value.Date.Add(tp.Value.TimeOfDay).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
                dp.ValueChanged += Keep; tp.ValueChanged += Keep;
                when.Controls.Add(dp); when.Controls.Add(tp);
                yield return when;
                continue;
            }
            bool tall = f is "Details" or "Reason" or "Comments";
            var box = tall ? new InputBox(_t, multiline: true, height: _line * 4) { Width = inner }.GrowWithText() : new InputBox(_t) { Width = inner, Height = _fieldH };
            box.Margin = new Padding(0, 0, 0, _gap / 2);
            if (f == "Minutes") box.Box.PlaceholderText = "minutes, or 1:30";
            if (f == "Amount") box.Box.PlaceholderText = "e.g. 650.00";
            var key = $"{a.Key}:{f}";
            if (_drafts.TryGetValue(key, out var was)) box.Value = was;
            box.Box.TextChanged += (_, _) => _drafts[key] = box.Value;
            boxes[f] = box;
            yield return box;
        }
        var missing = Text_(inner, "", _t.BodyBold, _t.BadText);
        missing.Visible = false;
        yield return missing;
        var submit = Button_(a.Key == "go-back" ? a.Label : "Submit", a.IsDanger ? BtnStyle.Danger : BtnStyle.Accent);
        submit.Margin = new Padding(0, _gap / 2, 0, 0);
        submit.Click += (_, _) =>
        {
            string v(string f) => boxes.TryGetValue(f, out var b) ? b.Value.Trim() : "";
            var chosenParent = parentPick?.SelectedItem as TicketChoiceInfo;
            bool sameJob = sameJobPick?.SelectedIndex == 1;
            // On the same job the fault is already written: what this machine is for may be left empty.
            var empty = a.Fields.FirstOrDefault(f => f == "ParentTicket" ? chosenParent == null
                : f == "Files" ? PickedIds(a.Key).Count == 0
                : f is "SameJob" or "Appointment" || (sameJob && f == "Details") ? false : !IsOptional(f) && v(f).Length == 0);
            var to = emailTick?.Checked == true ? emailTo!() : "";
            if (empty == null && emailTick?.Checked == true && to.Length == 0) { missing.Text = "Choose who to email the report to, or type their address."; missing.Visible = true; return; }
            if (empty != null)
            {
                missing.Text = empty == "ParentTicket" ? "Choose the ticket this is part of." : empty == "Files" ? "Attach the damage report." : "Fill in: " + FieldLabel(a, empty);
                missing.Visible = true;
                if (boxes.TryGetValue(empty, out var box)) box.Box.Focus(); else parentPick?.Focus();
                return;
            }
            Press(d, a, new TicketActMsg
            {
                TicketId = d.Ticket.Id, Action = a.Key, Details = v("Details"),
                Minutes = v("Minutes"), Amount = v("Amount"), Reference = v("Reference"), Reason = v("Reason"),
                AssetMake = v("AssetMake"), AssetModel = v("AssetModel"), AssetSerial = v("AssetSerial"),
                Accessories = v("Accessories"), DevicePassword = v("DevicePassword"),
                ParentTicketId = chosenParent?.Id ?? 0,
                PhotoStageId = a.Key == "add-machine" ? _machineStage ?? "" : "",
                SameJob = sameJob,
                AppointmentUtc = apptDate != null && apptTime != null
                    ? DateTime.SpecifyKind(apptDate.Value.Date.Add(apptTime.Value.TimeOfDay), DateTimeKind.Local).ToUniversalTime() : null,
                Comments = v("Comments"), AttachmentIds = PickedIds(a.Key), EmailTo = to
            });
        };
        yield return submit;
    }

    private Label FieldCaption(int inner, string text) => new()
    {
        Text = text, Font = _t.BodyBold, ForeColor = _t.Ink, BackColor = Color.Transparent, AutoSize = true,
        MaximumSize = new Size(inner, 0), Margin = new Padding(0, _gap / 2, 0, 2), UseMnemonic = false
    };

    // ---- the comment box: always there, first ----

    private void CommentBox(FlowLayoutPanel col, int inner, TicketDetail d)
    {
        var box = new InputBox(_t, multiline: true, height: _line * 3) { Width = inner, Margin = new Padding(0, 0, 0, _gap / 2) }.GrowWithText();
        box.Box.PlaceholderText = "Post a comment… type @ to ask someone. They are flagged until they reply.";
        if (_drafts.TryGetValue("comment", out var was)) box.Value = was;
        box.Box.TextChanged += (_, _) => _drafts["comment"] = box.Value;
        col.Controls.Add(box);

        // Typing @ lists the people on the ticket first, then everybody else (2026-10-21). The server still
        // reads who is mentioned out of the posted words; the list only saves typing the name.
        _mentions = MentionPopup.Attach(_t, box.Box, d.OnTicket, d.People, _link.DisplayName);
        var row = Row(inner);
        // Also email it to the client (2026-10-09): unticked by default — most comments are internal.
        var cu = d.ClientUpdate;
        RoundCheck? email = null;
        Func<string>? emailTo = null;
        Control? chooser = null;
        if (cu is { Ready: true })
        {
            email = new RoundCheck(_t)
            {
                Text = "Also email this to the client", Checked = _drafts.ContainsKey("comment.email"), AutoSize = true,
                Font = _t.BodyBold, ForeColor = _t.Ink, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, _gap / 2), UseMnemonic = false
            };
            (chooser, emailTo) = AddressChooser(inner, cu, "comment.to",
                $"They get the fault the ticket was opened for and this comment, with your name on it, from {cu.From} — nothing else on the ticket. Their reply comes back here.");
            chooser.Visible = email.Checked;
            email.CheckedChanged += (_, _) =>
            {
                chooser.Visible = email.Checked;
                if (email.Checked) _drafts["comment.email"] = "1"; else _drafts.Remove("comment.email");
            };
        }

        var send = Button_("Post", BtnStyle.Accent);
        send.Click += (_, _) =>
        {
            var text = box.Value.Trim();
            // Files alone are a comment too (2026-10-13): progress photos need no caption.
            var files = PickedIds("comment");
            if (text.Length == 0 && files.Count == 0) return;
            var to = email?.Checked == true ? emailTo!() : "";
            if (email?.Checked == true && to.Length == 0)
            {
                _notice = new TicketNoticeMsg { TicketId = d.Ticket.Id, Error = "Choose who to email it to, or type their address." };
                Draw(d, keepScroll: true);
                return;
            }
            _notice = null;
            // Kept until the server answers: a refused address sends the words back into the box (OnNotice).
            _posted = text;
            _postedFiles = _picked.TryGetValue("comment", out var was) ? was : null;
            _drafts.Remove("comment"); _drafts.Remove("comment.email"); _picked.Remove("comment");
            _link.AddTicketNote(d.Ticket.Id, text, to, files);
        };
        row.Controls.Add(send);
        col.Controls.Add(row);
        // Files and photos on the comment (2026-10-13).
        col.Controls.Add(FilePicker(inner, d, "comment", "📎 Attach files or photos…"));
        if (email != null) { col.Controls.Add(email); col.Controls.Add(chooser!); }
        else if (cu is { Ready: false, WhyNot.Length: > 0 }) col.Controls.Add(Text_(inner, cu.WhyNot, _t.Small, _t.Muted));
    }

    /// <summary>The @ list on the comment box now drawn — for tools/ticketwindowprobe.</summary>
    private MentionPopup? _mentions;

    /// <summary>The comment last posted, until the server answers — put back in the box if it refused the address.</summary>
    private string? _posted;
    /// <summary>Its files, put back with it.</summary>
    private List<AttachmentInfo>? _postedFiles;
    /// <summary>The comment whose "Email to the client…" is open, or 0.</summary>
    private long _emailNote;

    /// <summary>
    /// Who an update goes to (2026-10-09): the client's people the server offered, or "Another address…" and a box
    /// to type it in — the portal's chooser. What was chosen is kept in the drafts under <paramref name="key"/>.
    /// </summary>
    private (Control panel, Func<string> address) AddressChooser(int inner, ClientUpdateInfo cu, string key, string hint)
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Margin = new Padding(0)
        };
        var pick = new ThemedCombo(_t) { Font = _t.Body, FlatStyle = FlatStyle.Flat, DropDownStyle = ComboBoxStyle.DropDownList,
            Width = Math.Min(inner, _line * 26), Margin = new Padding(0, 0, 0, _gap / 2) };
        foreach (var c in cu.Choices) pick.Items.Add(c);
        pick.Items.Add("Another address…");
        var other = new InputBox(_t) { Width = Math.Min(inner, _line * 26), Height = _fieldH, Margin = new Padding(0, 0, 0, _gap / 2) };
        other.Box.PlaceholderText = "name@theircompany.co.za";
        var was = _drafts.TryGetValue(key, out var w) ? w : cu.Default;
        int at = cu.Choices.FindIndex(c => c.Address.Equals(was, StringComparison.OrdinalIgnoreCase));
        pick.SelectedIndex = at >= 0 ? at : pick.Items.Count - 1;
        if (at < 0) other.Value = was;
        other.Visible = at < 0;
        pick.SelectedIndexChanged += (_, _) =>
        {
            bool typed = pick.SelectedItem is not ClientAddressInfo;
            other.Visible = typed;
            _drafts[key] = typed ? other.Value : ((ClientAddressInfo)pick.SelectedItem!).Address;
            if (typed) other.Box.Focus();
        };
        other.Box.TextChanged += (_, _) => { if (pick.SelectedItem is not ClientAddressInfo) _drafts[key] = other.Value; };
        panel.Controls.Add(pick); panel.Controls.Add(other);
        panel.Controls.Add(Text_(inner, hint, _t.Small, _t.Muted));
        return (panel, () => pick.SelectedItem is ClientAddressInfo a ? a.Address : other.Value.Trim());
    }

    /// <summary>
    /// Under a comment (2026-10-09): each time it was emailed to the client, or why it did not go, and "Email to the
    /// client…" for a comment that may be sent — opening the chooser in place. The customer's own reply says so.
    /// </summary>
    private IEnumerable<Control> NoteEmail(int inner, TicketNoteInfo n)
    {
        if (n.FromCustomer) yield return Text_(inner, "✉ Their reply, by email", _t.Small, _t.Muted);
        foreach (var line in n.Emailed)
            yield return Text_(inner, (line.StartsWith("Emailed") ? "✉ " : "⚠ ") + line, _t.Small, line.StartsWith("Emailed") ? _t.Muted : _t.BadText);
        if (_open is not { } d || !n.CanEmail || n.Id == 0 || d.ClientUpdate is not { Ready: true } cu) yield break;
        if (_emailNote != n.Id)
        {
            var open = Button_(n.Emailed.Any(l => l.StartsWith("Emailed")) ? "Email it again…" : "Email to the client…", BtnStyle.Ghost);
            open.Click += (_, _) => { _emailNote = n.Id; _notice = null; Draw(d, keepScroll: true); };
            yield return open;
            yield break;
        }
        var key = $"email.{n.Id}";
        var (chooser, address) = AddressChooser(inner, cu, key, $"They get the fault the ticket was opened for and this comment, with {n.Author}'s name on it — nothing else.");
        yield return chooser;
        var row = Row(inner);
        var go = Button_("Send", BtnStyle.Accent);
        go.Click += (_, _) =>
        {
            var to = address();
            if (to.Length == 0)
            {
                _notice = new TicketNoticeMsg { TicketId = d.Ticket.Id, Error = "Choose who to email it to, or type their address." };
                Draw(d, keepScroll: true);
                return;
            }
            _notice = null; _emailNote = 0; _drafts.Remove(key);
            _link.EmailTicketNote(d.Ticket.Id, n.Id, to);
        };
        var cancel = Button_("Cancel", BtnStyle.Neutral);
        cancel.Click += (_, _) => { _emailNote = 0; _drafts.Remove(key); Draw(d, keepScroll: true); };
        row.Controls.Add(go); row.Controls.Add(cancel);
        yield return row;
    }

    // ---- Photos ----

    private Card PhotosPane(int w, TicketDetail d)
    {
        var (card, col, inner) = Section(w, "Photos", _t.Blue);
        col.Controls.Add(Text_(inner, $"As it arrived: {d.PhotosIn}.   As it leaves: {d.PhotosOut}.", _t.Body, _t.Ink));
        if (!d.IsClosed && d.PhotosOut < d.PhotosOutRequired)
            col.Controls.Add(Text_(inner, d.Machines.Count == 0
                ? $"At least {d.PhotosOutRequired} are needed before the machine can be collected or go out for delivery — ideally taken in front of the customer."
                : $"At least {d.PhotosOutRequired} ({OutEach(d)} of each of the {d.Machines.Count + 1} machines) are needed before they can be collected or go out for delivery — ideally taken in front of the customer.", _t.Small, _t.Muted));
        // As many as the damage needs, at BOTH ends, for as long as the job is open (Clint,
        // 2026-09-29: fourteen photos of a dropped laptop). Three is the least, never the most.
        var row = Row(inner);
        if (!d.IsClosed)
        {
            var addIn = Button_("Add photos as it arrived…", BtnStyle.Neutral);
            addIn.Click += async (_, _) => await AddPhotosAsync(d.Ticket.Id, addIn, "before");
            row.Controls.Add(addIn);
            var add = Button_("Add photos as it leaves…", BtnStyle.Neutral);
            add.Click += async (_, _) => await AddOutPhotosAsync(d.Ticket.Id, add);
            row.Controls.Add(add);
        }
        var see = Button_("See them in the browser", BtnStyle.Ghost);
        see.Click += (_, _) => _link.RequestPortalLink(Portal.Ticket(d.Ticket) + "?tab=photos");
        row.Controls.Add(see);
        col.Controls.Add(row);
        return card;
    }

    private Task AddOutPhotosAsync(int ticketId, RoundButton btn) => AddPhotosAsync(ticketId, btn, "after");

    /// <summary>Photos straight onto the ticket: "after" as it leaves, "before" as it arrives (a call
    /// out going to the workshop books its machine in with them).</summary>
    private async Task AddPhotosAsync(int ticketId, RoundButton btn, string kind)
    {
        using var pick = new OpenFileDialog
        {
            Title = kind == "before" ? "Photos of the machine as it arrives" : "Photos of the machine as it leaves",
            Filter = "Photos|*.jpg;*.jpeg;*.png;*.webp;*.heic|All files|*.*", Multiselect = true
        };
        if (pick.ShowDialog(this) != DialogResult.OK || pick.FileNames.Length == 0) return;
        btn.Enabled = false; btn.Text = "Uploading…";
        var (uploaded, error) = await _link.UploadPhotosAsync(pick.FileNames, kind, "", ticketId,
            progress: (i, n) => { try { BeginInvoke(() => btn.Text = n > 1 ? $"Uploading {i} of {n}…" : "Uploading…"); } catch { } });
        _notice = new TicketNoticeMsg { TicketId = ticketId, Error = error, Message = error.Length == 0 ? $"{uploaded} photo{(uploaded == 1 ? "" : "s")} added." : "" };
        _link.OpenTicket(ticketId);
    }

    // ---- Quote ----

    private Card QuotePane(int w, TicketDetail d)
    {
        var (card, col, inner) = Section(w, d.QuoteLines.Count > 0 ? $"Quote ({d.QuoteLines.Count} item{(d.QuoteLines.Count == 1 ? "" : "s")})" : "Quote", _t.Orange);
        foreach (var c in QuoteEditor(inner, d)) col.Controls.Add(c);
        return card;
    }

    /// <summary>
    /// The quote's items and, while they may be changed, the Item box with Add BESIDE it (Clint,
    /// 2026-09-27: "There should be the field for the item and next to it an add button"). Typing
    /// offers matching items; Add with a new name adds it to the list, after a "Did you mean".
    /// Drawn in the Quote needed step and on the Quote tab — never both at once.
    /// </summary>
    private IEnumerable<Control> QuoteEditor(int inner, TicketDetail d)
    {
        foreach (var c in MachineEditor(inner, d)) yield return c;
        if (d.QuoteLines.Count == 0) yield return Text_(inner, "No items yet.", _t.Small, _t.Muted);
        foreach (var l in d.QuoteLines)
        {
            var row = Row(inner);
            // An older server sends HasPhoto alone.
            int photos = l.PhotoCount > 0 ? l.PhotoCount : l.HasPhoto ? 1 : 0;
            var text = $"{(l.Quantity > 1 ? $"{l.Quantity} × " : "")}{l.ItemName}{(l.PartNumber.Length > 0 ? $"  ·  {l.PartNumber}" : "")}{(l.SerialNumber.Length > 0 ? $"  ·  serial {l.SerialNumber}" : "")}{(photos == 1 ? "  ·  photo" : photos > 1 ? $"  ·  {photos} photos" : "")}";
            // "Add photos" is sized for its progress too ("12 of 12…"), so it never grows under the words.
            int addW = Math.Max(TextRenderer.MeasureText("Add photos", _t.ButtonFont).Width, TextRenderer.MeasureText("12 of 12…", _t.ButtonFont).Width) + 40;
            int btnW = d.MayEditLines ? TextRenderer.MeasureText("Remove", _t.ButtonFont).Width + 40 + _gap + addW + _gap : 0;
            row.Controls.Add(new Label { Text = text, Font = _t.Body, ForeColor = _t.Ink, BackColor = Color.Transparent, AutoSize = true, MaximumSize = new Size(Math.Max(_line * 6, inner - btnW - 4), 0), Margin = new Padding(0, 6, _gap, 0), UseMnemonic = false });
            if (d.MayEditLines)
            {
                var more = Button_("Add photos", BtnStyle.Ghost);
                more.Width = Math.Max(more.Width, addW);
                more.Click += async (_, _) => await AddLinePhotosAsync(d.Ticket.Id, l.Id, more);
                row.Controls.Add(more);
                var rm = Button_("Remove", BtnStyle.Ghost);
                rm.Click += (_, _) => { _notice = null; _link.RemoveQuoteLine(d.Ticket.Id, l.Id); };
                row.Controls.Add(rm);
            }
            yield return row;
        }
        if (!d.MayEditLines) yield break;

        // ---- Item, with Add beside it ----
        yield return FieldCaption(inner, "Item");
        var itemRow = Row(inner);
        var add = Button_("Add", BtnStyle.Accent);
        add.Margin = new Padding(0);
        _itemBox = new InputBox(_t) { Width = inner - add.Width - _gap - 2, Height = _fieldH, Margin = new Padding(0, 0, _gap, 0) };
        _itemBox.Box.PlaceholderText = "Choose from the list, or type — e.g. DDR, SSD, screen";
        _itemBox.Box.TextChanged += (_, _) => { _pickedItem = null; _suggestDebounce.Stop(); _suggestDebounce.Start(); };
        // A dropdown (2026-09-29): clicking into the box, or the down arrow, lists what is on the
        // company's list — all of it when nothing is typed.
        _itemBox.Box.MouseDown += (_, _) => { if (_suggestFrame is { Visible: false }) _link.SuggestItems(_itemBox.Value.Trim()); };
        _itemBox.Box.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await AddLineAsync(d.Ticket.Id, add); }
            else if (e.KeyCode == Keys.Down && _suggest != null)
            {
                e.SuppressKeyPress = true;
                if (!_suggestFrame!.Visible) _link.SuggestItems(_itemBox.Value.Trim());
                else { _suggest.Focus(); if (_suggest.Items.Count > 0 && _suggest.SelectedIndex < 0) _suggest.SelectedIndex = 0; }
            }
            else if (e.KeyCode == Keys.Escape && _suggestFrame is { Visible: true }) { e.SuppressKeyPress = true; _suggestFrame!.Visible = false; }
        };
        _itemBox.Box.Leave += (_, _) => BeginInvoke((Action)(() => { if (_suggest != null && !_suggest.Focused) _suggestFrame!.Visible = false; }));
        add.Click += async (_, _) => await AddLineAsync(d.Ticket.Id, add);
        itemRow.Controls.Add(_itemBox); itemRow.Controls.Add(add);
        yield return itemRow;
        // In a rounded frame, its rows in the palette (2026-10-06); the frame is what shows and hides.
        _suggest = new ListBox { Font = _t.Body, Margin = new Padding(0, 2, 0, 0) }.Themed(_t);
        _suggest.Size = new Size(inner, _line * 8);
        _suggestFrame = new FieldFrame(_t, _suggest) { Width = inner, Visible = false };
        _suggest.Click += (_, _) => PickSuggestion();
        _suggest.Leave += (_, _) => BeginInvoke((Action)(() => { if (_suggest != null && _itemBox is { Box.Focused: false }) _suggestFrame!.Visible = false; }));
        _suggest.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; PickSuggestion(); } };
        yield return _suggestFrame;

        // ---- part number, serial number and quantity, then the photo — all optional ----
        var row2 = Row(inner);
        row2.Margin = new Padding(0, _gap / 2, 0, 0);
        int qtyW = Math.Max(_line * 4, TextRenderer.MeasureText("999", _t.Body).Width + 40);
        int halfW = (inner - qtyW - _gap * 2 - 4) / 2;
        _partBox = new InputBox(_t) { Width = halfW, Height = _fieldH, Margin = new Padding(0, 0, _gap, 0) };
        _partBox.Box.PlaceholderText = "Part number (optional)";
        _serialBox = new InputBox(_t) { Width = halfW, Height = _fieldH, Margin = new Padding(0, 0, _gap, 0) };
        _serialBox.Box.PlaceholderText = "Serial number (optional)";
        _qtyBox = new NumberBox(_t, 1, 999, 1, _fieldH) { Width = qtyW, Margin = new Padding(0) };
        row2.Controls.Add(_partBox); row2.Controls.Add(_serialBox); row2.Controls.Add(_qtyBox);
        yield return row2;

        var row3 = Row(inner);
        row3.Margin = new Padding(0, _gap / 2, 0, 0);
        // As many photos of the part as it needs (2026-10-06), picked together.
        var photo = Button_("Photos of the part…", BtnStyle.Neutral);
        photo.Click += (_, _) =>
        {
            using var pick = new OpenFileDialog { Title = "Photos of the part", Filter = PhotoFilter, Multiselect = true };
            if (pick.ShowDialog(this) != DialogResult.OK || pick.FileNames.Length == 0) return;
            _partPhotoPaths.Clear(); _partPhotoPaths.AddRange(pick.FileNames);
            if (_partPhotoLabel != null) _partPhotoLabel.Text = PartPhotosChosen();
        };
        row3.Controls.Add(photo);
        int labelW = inner - photo.Width - _gap - 4;
        _partPhotoLabel = new Label { Text = PartPhotosChosen(), Font = _t.Small, ForeColor = _t.Muted, BackColor = Color.Transparent, AutoSize = true, MaximumSize = new Size(Math.Max(_line * 4, labelW), 0), AutoEllipsis = true, Margin = new Padding(0, 10, 0, 0), UseMnemonic = false };
        row3.Controls.Add(_partPhotoLabel);
        yield return row3;
    }

    private const string PhotoFilter = "Photos|*.jpg;*.jpeg;*.png;*.webp;*.heic|All files|*.*";

    /// <summary>"optional", "battery.jpg", or "3 photos chosen".</summary>
    private string PartPhotosChosen() => _partPhotoPaths.Count switch
    {
        0 => "optional — as many as you like",
        1 => Path.GetFileName(_partPhotoPaths[0]),
        var n => $"{n} photos chosen"
    };

    /// <summary>More photos for an item already on the quote: uploaded one at a time, then named to the line.</summary>
    private async Task AddLinePhotosAsync(int ticketId, long lineId, RoundButton btn)
    {
        using var pick = new OpenFileDialog { Title = "More photos of the part", Filter = PhotoFilter, Multiselect = true };
        if (pick.ShowDialog(this) != DialogResult.OK || pick.FileNames.Length == 0) return;
        btn.Enabled = false;
        var (ids, error) = await _link.UploadPhotoIdsAsync(pick.FileNames, "part", "", ticketId,
            progress: (i, n) => { try { BeginInvoke(() => btn.Text = n > 1 ? $"{i} of {n}…" : "…"); } catch { } });
        if (error.Length > 0) MessageBox.Show(this, error, "Photos", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        if (ids.Count == 0) { btn.Enabled = true; btn.Text = "Add photos"; return; }
        _notice = null;
        _link.AddQuoteLinePhotos(ticketId, lineId, ids);
    }

    /// <summary>
    /// The machine the quote is for — one per quote, every part optional (Clint, 2026-09-29: "a
    /// battery replacement for an Asus vivo book 123, with SN123"). A workshop job arrives with the
    /// machine it was booked in with. What is typed survives a redraw until it is saved.
    /// </summary>
    private IEnumerable<Control> MachineEditor(int inner, TicketDetail d)
    {
        var m = d.QuoteMachine;
        if (!d.MayEditLines)
        {
            var said = string.Join(" — ", new[] { m.Type, $"{m.Make} {m.Model}".Trim(), m.Serial.Length > 0 ? $"serial number {m.Serial}" : "" }.Where(s => s.Length > 0));
            yield return Text_(inner, said.Length > 0 ? "For: " + said : "Not for a particular machine.", _t.Small, _t.Muted);
            yield break;
        }
        yield return FieldCaption(inner, "The machine (optional — leave it empty when the parts are not for one machine)");
        string Draft(string key, string saved) => _drafts.TryGetValue(key, out var was) ? was : saved;

        var row1 = Row(inner);
        int typeW = Math.Max(_line * 7, TextRenderer.MeasureText("All-in-one", _t.Body).Width + 48);
        var type = new ThemedCombo(_t) { Font = _t.Body, FlatStyle = FlatStyle.Flat, DropDownStyle = ComboBoxStyle.DropDownList, Width = typeW, Margin = new Padding(0, 4, _gap, 0) };
        type.Items.Add("Type");
        foreach (var k in d.MachineTypes) type.Items.Add(k);
        var wantType = Draft("m.type", m.Type);
        type.SelectedIndex = Math.Max(0, d.MachineTypes.IndexOf(wantType) + 1);
        type.SelectedIndexChanged += (_, _) => _drafts["m.type"] = type.SelectedIndex > 0 ? (string)type.SelectedItem! : "";
        int halfW = (inner - typeW - _gap * 2 - 4) / 2;
        InputBox Box(string key, string saved, string hint, int width, int right)
        {
            var b = new InputBox(_t) { Width = width, Height = _fieldH, Margin = new Padding(0, 0, right, 0) };
            b.Box.PlaceholderText = hint;
            b.Value = Draft(key, saved);
            b.Box.TextChanged += (_, _) => _drafts[key] = b.Value;
            return b;
        }
        var make = Box("m.make", m.Make, "Make — e.g. Asus", halfW, _gap);
        var model = Box("m.model", m.Model, "Model — e.g. VivoBook 15", halfW, 0);
        row1.Controls.Add(type); row1.Controls.Add(make); row1.Controls.Add(model);
        yield return row1;

        var row2 = Row(inner);
        row2.Margin = new Padding(0, _gap / 2, 0, 0);
        var save = Button_("Save", BtnStyle.Neutral);
        save.Margin = new Padding(0);
        var serial = Box("m.serial", m.Serial, "Serial number", inner - save.Width - _gap - 2, _gap);
        save.Click += (_, _) =>
        {
            _notice = null;
            _link.SetQuoteMachine(new QuoteMachineMsg
            {
                TicketId = d.Ticket.Id, Type = type.SelectedIndex > 0 ? (string)type.SelectedItem! : "",
                Make = make.Value.Trim(), Model = model.Value.Trim(), Serial = serial.Value.Trim()
            });
            // What comes back is what was saved; the drafts have done their job.
            foreach (var k in new[] { "m.type", "m.make", "m.model", "m.serial" }) _drafts.Remove(k);
        };
        row2.Controls.Add(serial); row2.Controls.Add(save);
        yield return row2;
        if (m.FromBooking)
            yield return Text_(inner, "Filled in from the booking. Change it, or empty it, and Save.", _t.Small, _t.Muted);
    }

    private CatalogItemInfo? _pickedItem;

    private void OnItems(ItemListMsg m) => Inv(() =>
    {
        if (_suggest == null || _itemBox == null || m.Term != _itemBox.Value.Trim()) return;
        _suggest.BeginUpdate(); _suggest.Items.Clear();
        foreach (var i in m.Items) _suggest.Items.Add(i);
        _suggest.EndUpdate();
        _suggestFrame!.Visible = m.Items.Count > 0;
        NativeDark.Scrollbars(_suggest, _t.IsDark);
    });

    private void PickSuggestion()
    {
        if (_suggest?.SelectedItem is not CatalogItemInfo i || _itemBox == null) return;
        _itemBox.Value = i.Name;          // raises TextChanged, which forgets the pick — so pick after
        _pickedItem = i;
        if (_partBox != null && _partBox.Value.Trim().Length == 0) _partBox.Value = i.LastPartNumber;
        _suggestFrame!.Visible = false;
    }

    private async Task AddLineAsync(int ticketId, RoundButton btn)
    {
        if (_itemBox == null || _partBox == null || _qtyBox == null) return;
        var name = _itemBox.Value.Trim();
        if (name.Length == 0 && _pickedItem == null) { _itemBox.Box.Focus(); return; }
        var photoIds = new List<long>();
        if (_partPhotoPaths.Count > 0)
        {
            // One at a time and made smaller, as every other photo goes (§62).
            btn.Enabled = false; btn.Text = "…";
            var (ids, error) = await _link.UploadPhotoIdsAsync(_partPhotoPaths.ToList(), "part", "", ticketId,
                progress: (i, n) => { try { BeginInvoke(() => btn.Text = n > 1 ? $"{i} of {n}…" : "…"); } catch { } });
            btn.Enabled = true; btn.Text = "Add";
            if (error.Length > 0)
            {
                // The ones that went up are kept; the person decides whether to add the item without the rest.
                var go = ids.Count > 0 && MessageBox.Show(this, $"{error}\n\n{ids.Count} of {_partPhotoPaths.Count} went up. Add the item with those?",
                    "Photos", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
                if (ids.Count == 0) MessageBox.Show(this, error, "Photos", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                if (!go) return;
            }
            photoIds = ids;
        }
        _partPhotoPaths.Clear();
        _notice = null;
        _link.AddQuoteLine(new QuoteLineAddMsg
        {
            TicketId = ticketId, ItemId = _pickedItem?.Id ?? 0, ItemName = name,
            PartNumber = _partBox.Value.Trim(), SerialNumber = _serialBox?.Value.Trim() ?? "", Quantity = (int)_qtyBox.Value,
            PhotoIds = photoIds, PhotoId = photoIds.Count > 0 ? photoIds[0] : null
        });
    }

    /// <summary>"Did you mean" — the list stays one list. Choosing one quotes that item instead.</summary>
    private void OnSimilar(QuoteSimilarMsg m) => Inv(() =>
    {
        using var dlg = new Form
        {
            Text = "Did you mean…", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent, BackColor = _t.Mist, Font = _t.Body
        };
        if (BrandIcon.Current is { } icon) dlg.Icon = icon;   // a fixed dialog takes no default icon
        dlg.Shown += (_, _) => NativeDark.Apply(dlg, _t);
        int colW = Math.Max(380, _line * 16);
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(18), BackColor = Color.Transparent };
        dlg.Controls.Add(flow);
        flow.Controls.Add(Text_(colW, $"\"{m.Typed}\" looks like something already on the list. Did you mean:", _t.BodyBold, _t.Ink));
        CatalogItemInfo? chosen = null; bool asNew = false;
        foreach (var s in m.Similar)
        {
            var b = new RoundButton(_t, BtnStyle.Neutral) { Text = s.Name, Size = new Size(colW, _btnH), Margin = new Padding(0, 0, 0, _gap / 2) };
            b.Click += (_, _) => { chosen = s; dlg.DialogResult = DialogResult.OK; };
            flow.Controls.Add(b);
        }
        var fresh = new RoundButton(_t, BtnStyle.Accent) { Text = $"No — it is different. Add \"{m.Typed}\"", Size = new Size(colW, _btnH), Margin = new Padding(0, _gap, 0, 0) };
        fresh.Click += (_, _) => { asNew = true; dlg.DialogResult = DialogResult.OK; };
        flow.Controls.Add(fresh);
        dlg.ClientSize = flow.PreferredSize;
        if (dlg.ShowDialog(this) != DialogResult.OK || (chosen == null && !asNew)) return;
        _link.AddQuoteLine(new QuoteLineAddMsg
        {
            TicketId = m.TicketId, ItemId = chosen?.Id ?? 0, ItemName = chosen == null ? m.Typed : "", ConfirmNew = asNew,
            PartNumber = m.PartNumber, SerialNumber = m.SerialNumber, Quantity = m.Quantity, PhotoId = m.PhotoId, PhotoIds = m.PhotoIds
        });
    });

    // ---- Details: the machine, the job, who has it ----

    /// <summary>A save from the edit card is on its way: its answer, if it went through, clears what was typed.</summary>
    private bool _editPending;

    /// <summary>
    /// Edit the ticket (Clint, 2026-10-13): subject, type of job, appointment, description — anybody who can see it, as
    /// in the portal. What is typed is kept in <see cref="_drafts"/> under "edit:…", so a redraw (the type changed, a
    /// photo added, the window resized) loses nothing, and a refused save still has it. The server decides
    /// (TicketService.EditAsync); this offers the same choices.
    /// </summary>
    private Card EditCard(int w, TicketDetail d)
    {
        var t = d.Ticket;
        bool closed = d.IsClosed || t.Status >= 3;
        var (card, col, inner) = Section(w, "Edit the ticket", _t.Orange);
        string Draft(string k, string was) => _drafts.TryGetValue("edit:" + k, out var v) ? v : was;

        col.Controls.Add(Text_(inner, "Subject", _t.BodyBold, _t.Ink));
        var subject = new InputBox(_t) { Width = inner, Height = _fieldH, Margin = new Padding(0, 0, 0, _gap / 2) };
        subject.Box.MaxLength = 200;
        subject.Value = Draft("subject", t.Subject);
        subject.Box.TextChanged += (_, _) => _drafts["edit:subject"] = subject.Value;
        col.Controls.Add(subject);

        int cat = int.TryParse(Draft("cat", t.Category.ToString()), out var c) ? c : t.Category;
        DateBox? date = null; TimeBox? time = null; RoundCheck? none = null;
        if (closed) col.Controls.Add(Text_(inner, $"Type of job: {t.CategoryName} — the job is closed, so its type and appointment stay as they are.", _t.Small, _t.Muted));
        else
        {
            col.Controls.Add(Text_(inner, "Type of job", _t.BodyBold, _t.Ink));
            var kinds = Row(inner);
            // As on New ticket (§124): each in its colour, the chosen one filled; the VALUE is what is sent. A recurring
            // task is set up on its own page, so it is offered only to a ticket that already is one.
            // And only the categories the company uses now (Job types, §168) — the ticket's own stays whatever is switched off.
            foreach (var value in TicketSummary.CategoryOrder.Where(v => (v != (int)TicketCategoryKind.Recurring && _link.CategoryOn(v)) || t.Category == v))
            {
                var (fill, onDark) = TicketSummary.CategoryColors[value];
                var name = TicketSummary.CategoryNames[value];
                var b = new RoundButton(_t, BtnStyle.Ghost)
                {
                    Text = name, Tint = ColorTranslator.FromHtml(fill), TintInk = ColorTranslator.FromHtml(_t.IsDark ? onDark : fill),
                    Size = new Size(TextRenderer.MeasureText(name, _t.ButtonFont).Width + 32, _btnH),
                    Margin = new Padding(0, 0, _gap / 2, _gap / 2), Chosen = value == cat, AccessibleName = name + (value == cat ? ", chosen" : "")
                };
                b.Click += (_, _) => { _drafts["edit:cat"] = value.ToString(); Draw(d, keepScroll: true); };
                kinds.Controls.Add(b);
            }
            col.Controls.Add(kinds);
            if (cat != t.Category)
                col.Controls.Add(Banner(inner, "⚠ It starts again at the first stage of that type of job. Everything done so far stays in the story.", _t.WarnBg, _t.WarnText));
            if (cat == (int)TicketCategoryKind.Workshop && t.Category != cat)
            {
                // What a workshop booking needs before it can become one: somebody on it, and the photos as it arrived.
                col.Controls.Add(Text_(inner, d.PhotosRequired == 0
                    ? $"A workshop job needs a technician on it (People on it). Photos of the machine are not asked for, but you can add some — {d.PhotosIn} added so far."
                    : $"A workshop job needs a technician on it (People on it) and at least {d.PhotosRequired} photos of the machine as it arrived — {d.PhotosIn} added so far.", _t.Small, _t.Ink));
                var addIn = Button_("Add photos as it arrived…", BtnStyle.Neutral);
                addIn.Click += async (_, _) => await AddPhotosAsync(t.Id, addIn, "before");
                col.Controls.Add(addIn);
            }
            // A call out's appointment; a ticket that has one keeps the boxes whatever its type.
            if (cat == (int)TicketCategoryKind.CallOut || t.AppointmentUtc.HasValue)
            {
                col.Controls.Add(Text_(inner, "Appointment", _t.BodyBold, _t.Ink));
                var booked = t.AppointmentUtc is DateTime a ? DateTime.SpecifyKind(a, DateTimeKind.Utc).ToLocalTime() : (DateTime?)null;
                var start = DateTime.TryParse(Draft("when", ""), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var kept)
                    ? kept : booked ?? DateTime.Today.AddDays(1).AddHours(9);
                var when = Row(inner);
                date = new DateBox(_t) { Width = inner * 3 / 5, Height = _fieldH, Value = start.Date, Margin = new Padding(0, 0, _gap, _gap / 2) };
                time = new TimeBox(_t) { Width = inner - inner * 3 / 5 - _gap - 2, Value = DateTime.Today.Add(start.TimeOfDay), Margin = new Padding(0, 0, 0, _gap / 2) };
                var (dp, tp) = (date, time);
                void Keep(object? _, EventArgs __) => _drafts["edit:when"] = dp.Value.Date.Add(tp.Value.TimeOfDay).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
                dp.ValueChanged += Keep; tp.ValueChanged += Keep;
                when.Controls.Add(dp); when.Controls.Add(tp);
                // The tick ABOVE the boxes it hides, so ticking it never moves it out from under the pointer.
                none = new RoundCheck(_t)
                {
                    Text = "No appointment", AutoSize = true, Font = _t.Body, ForeColor = _t.Ink, BackColor = Color.Transparent,
                    Checked = Draft("none", booked.HasValue ? "0" : "1") == "1", Margin = new Padding(0, 0, 0, _gap / 2), UseMnemonic = false
                };
                var n = none;
                // Ticked, the boxes go: greyed, the time box still looked live (ticketwindowprobe's picture, 2026-10-13).
                // Set, never read back — Visible reads false until the window is shown (§109).
                void Show() => when.Visible = !n.Checked;
                n.CheckedChanged += (_, _) => { _drafts["edit:none"] = n.Checked ? "1" : "0"; Show(); };
                Show();
                col.Controls.Add(none);
                col.Controls.Add(when);
                col.Controls.Add(Text_(inner, booked.HasValue ? $"Booked for {booked.Value:dddd d MMMM yyyy 'at' HH:mm}." : "Not scheduled.", _t.Small, _t.Muted));
            }
        }

        // When it is due (2026-10-26, HANDOVER §165) — any type of job: a day, or none. As the appointment's: the tick above
        // the box it hides. A day is a day — no clock — so it is shown and sent as it is.
        DateBox? dueBox = null; RoundCheck? noDue = null;
        if (closed) col.Controls.Add(Text_(inner, t.DueDate is DateTime wasDue ? $"Was due {wasDue:dddd d MMMM yyyy}." : "No due date.", _t.Small, _t.Muted));
        else
        {
            col.Controls.Add(Text_(inner, "Due date", _t.BodyBold, _t.Ink));
            var dueStart = DateTime.TryParse(Draft("due", ""), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var keptDue)
                ? keptDue : t.DueDate?.Date ?? DateTime.Today.AddDays(1);
            noDue = new RoundCheck(_t)
            {
                Text = "No due date", AutoSize = true, Font = _t.Body, ForeColor = _t.Ink, BackColor = Color.Transparent,
                Checked = Draft("nodue", t.DueDate.HasValue ? "0" : "1") == "1", Margin = new Padding(0, 0, 0, _gap / 2), UseMnemonic = false
            };
            dueBox = new DateBox(_t) { Width = inner * 3 / 5, Height = _fieldH, Value = dueStart, Margin = new Padding(0, 0, 0, _gap / 2), AccessibleName = "Due date" };
            var (nd, db) = (noDue, dueBox);
            db.ValueChanged += (_, _) => _drafts["edit:due"] = db.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            void ShowDue() => db.Visible = !nd.Checked;   // set, never read back (§109)
            nd.CheckedChanged += (_, _) => { _drafts["edit:nodue"] = nd.Checked ? "1" : "0"; ShowDue(); };
            ShowDue();
            col.Controls.Add(noDue);
            col.Controls.Add(dueBox);
            if (t.DueDate is DateTime isDue)
                col.Controls.Add(Text_(inner, $"Due {isDue:dddd d MMMM yyyy}{(t.Overdue ? " — overdue" : "")}.", _t.Small, t.Overdue ? _t.BadText : _t.Muted));
        }

        col.Controls.Add(Text_(inner, "Description — the fault as it was booked", _t.BodyBold, _t.Ink));
        var desc = new InputBox(_t, multiline: true, height: _line * 3) { Width = inner, Margin = new Padding(0, 0, 0, _gap / 2) }.GrowWithText();
        desc.Value = Draft("desc", d.Description);
        desc.Box.TextChanged += (_, _) => _drafts["edit:desc"] = desc.Value;
        col.Controls.Add(desc);

        var save = Button_("Save changes", BtnStyle.Accent);
        var (dd, tt, nn) = (date, time, none);
        var (dueB, noD) = (dueBox, noDue);
        save.Click += (_, _) =>
        {
            _notice = null;
            _editPending = true;
            bool apptShown = dd != null && tt != null && nn != null;
            _link.EditTicket(new TicketEditMsg
            {
                TicketId = t.Id, Subject = subject.Value.Trim(), Description = desc.Value,
                Category = closed ? null : cat,
                AppointmentGiven = apptShown,
                // This PC's clock, as New ticket's and Reschedule's are; the server refuses the past.
                AppointmentUtc = apptShown && !nn!.Checked
                    ? DateTime.SpecifyKind(dd!.Value.Date.Add(tt!.Value.TimeOfDay), DateTimeKind.Local).ToUniversalTime() : null,
                // The DAY at midnight marked UTC (§165) — never this PC's local midnight.
                DueGiven = dueB != null && noD != null,
                DueDate = dueB != null && noD != null && !noD.Checked ? DateTime.SpecifyKind(dueB.Value.Date, DateTimeKind.Utc) : null
            });
        };
        col.Controls.Add(save);
        col.Controls.Add(Text_(inner, "Every change goes into the ticket's story, with what it was before.", _t.Small, _t.Muted));
        return card;
    }

    private void DetailsPane(int w, TicketDetail d)
    {
        var t = d.Ticket;
        // Its subject, type of job, appointment and description (2026-10-13) — first: it is what this tab is opened for.
        Add(EditCard(w, d));
        // Which client — on every ticket, at any stage (Clint, 2026-09-27). The same window the
        // machine list uses to say whose a machine is.
        {
            var (card, col, inner) = Section(w, "Client", _t.Green);
            col.Controls.Add(Text_(inner, t.ClientName.Length > 0 ? t.ClientName : "Not linked to a client.", _t.Body, t.ClientName.Length > 0 ? _t.Ink : _t.Muted));
            var link = Button_(d.ClientId > 0 ? "Change the client…" : "Link to a client…", BtnStyle.Neutral);
            link.Click += (_, _) =>
            {
                using var dlg = new LinkDeviceDialog(_t, _link, "Which client is this ticket for?", $"#{t.Number} – {t.Subject}",
                    d.ClientId > 0 ? $"Currently {t.ClientName}." : "Not linked to a client yet.", d.ClientId, 0);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _notice = null;
                _link.SetTicketClient(t.Id, dlg.ClientId, dlg.ContactId > 0 ? dlg.ContactName : null);
            };
            col.Controls.Add(link);
            Add(card);
        }
        // A call out's site — and still there once the job has gone to the workshop.
        if (StartedAsCallOut(d)) Add(CallOutCard(w, d));
        if (t.Category == (int)TicketCategoryKind.Workshop)
        {
            var (card, col, inner) = Section(w, d.Machines.Count > 0 ? "Machine 1" : "The machine", _t.Blue);
            var facts = new List<(string, string)>
            {
                ("Make / model", $"{d.AssetMake} {d.AssetModel}".Trim() is { Length: > 0 } mm ? mm : "—"),
                ("Serial", d.AssetSerial.Length > 0 ? d.AssetSerial : "—"),
                ("Accessories", d.Accessories.Length > 0 ? d.Accessories : "none recorded"),
                ("Client", t.ClientName.Length > 0 ? t.ClientName : "—"),
            };
            if (!d.DevicePasswordProtected) facts.Add(("Password", "not password protected"));
            foreach (var r in FactRows(inner, facts)) col.Controls.Add(r);
            if (d.DevicePasswordProtected)
            {
                var show = Button_("Show the machine's password", BtnStyle.Ghost);
                show.Click += (_, _) => { show.Text = d.DevicePassword; show.Width = TextRenderer.MeasureText(d.DevicePassword, _t.ButtonFont).Width + 40; };
                col.Controls.Add(show);
            }
            Add(card);
            // The further machines on this job (2026-10-02): a card each, as the portal draws them.
            for (int i = 0; i < d.Machines.Count; i++)
            {
                var m = d.Machines[i];
                var (mc, mcol, minner) = Section(w, $"Machine {i + 2}", _t.Blue);
                var mf = new List<(string, string)>();
                if (m.Purpose.Length > 0) mf.Add(("On this job", m.Purpose));
                mf.Add(("Make / model", $"{m.Make} {m.Model}".Trim() is { Length: > 0 } n ? n : "—"));
                mf.Add(("Serial", m.Serial.Length > 0 ? m.Serial : "—"));
                mf.Add(("Accessories", m.Accessories.Length > 0 ? m.Accessories : "none recorded"));
                mf.Add(("Photos in", m.PhotosIn.ToString()));
                if (!m.PasswordProtected) mf.Add(("Password", "not password protected"));
                foreach (var r in FactRows(minner, mf)) mcol.Controls.Add(r);
                if (m.PasswordProtected)
                {
                    var show = Button_("Show this machine's password", BtnStyle.Ghost);
                    show.Click += (_, _) => { show.Text = m.Password; show.Width = TextRenderer.MeasureText(m.Password, _t.ButtonFont).Width + 40; };
                    mcol.Controls.Add(show);
                }
                Add(mc);
            }
        }

        var (job, jcol, jinner) = Section(w, "The job", _t.Purple);
        var jf = d.Facts.Select(f => (f.Label, f.Value)).ToList();
        jf.Add(("Opened", $"{t.CreatedUtc.ToLocalTime():d MMM yyyy, HH:mm} by {d.OpenedBy}"));
        foreach (var r in FactRows(jinner, jf)) jcol.Controls.Add(r);
        var work = d.Notes.Where(x => x.IsWorkDone).ToList();
        if (work.Count > 0)
        {
            // What it is for: pasting into the customer's invoice.
            var copy = Button_("Copy the work done for the invoice", BtnStyle.Ghost);
            copy.Click += (_, _) => { try { Clipboard.SetText(string.Join(Environment.NewLine, work.Select(e => "- " + (e.WorkDoneText.Length > 0 ? e.WorkDoneText : e.Body.Trim())))); copy.Text = "Copied"; } catch { } };
            jcol.Controls.Add(copy);
        }
        if (!d.IsClosed)
        {
            jcol.Controls.Add(Text_(jinner, "People on it and priority", _t.BodyBold, _t.Ink));
            AssignRow(jcol, jinner, d, withStatus: false);
        }
        Add(job);
    }

    // ================================================================== builders. One place decides spacing, and it measures.

    private Label Text_(int w, string text, Font f, Color c) => new()
    {
        Text = text, Font = f, ForeColor = c, BackColor = Color.Transparent, AutoSize = true,
        MaximumSize = new Size(w, 0), Margin = new Padding(0, 0, 0, _gap / 2), UseMnemonic = false
    };

    private Label Banner(int w, string text, Color bg, Color fg) => new()
    {
        Text = text, Font = _t.BodyBold, ForeColor = fg, BackColor = bg, AutoSize = true,
        MaximumSize = new Size(w, 0), MinimumSize = new Size(w, 0), Padding = new Padding(10, 8, 10, 8),
        Margin = new Padding(0, _gap / 2, 0, _gap / 2), UseMnemonic = false
    };

    private RoundButton Button_(string text, BtnStyle style) => new(_t, style)
    {
        Text = text, Margin = new Padding(0, 0, _gap, _gap),
        Size = new Size(TextRenderer.MeasureText(text, _t.ButtonFont).Width + 40, _btnH)
    };

    /// <summary>A card holding a column. Everything in it flows; nothing is placed by hand.</summary>
    private (Card card, FlowLayoutPanel col, int inner) Box(int w)
    {
        var card = new Card(_t, 14) { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = w, MinimumSize = new Size(w, 0), MaximumSize = new Size(w, 0), Margin = new Padding(0, _gap, 0, 0) };
        int inner = w - card.Padding.Horizontal - 2;
        var col = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Dock = DockStyle.Top, Margin = new Padding(0)
        };
        card.Controls.Add(col);
        return (card, col, inner);
    }

    /// <summary>A card with a title.</summary>
    private (Card card, FlowLayoutPanel col, int inner) Section(int w, string title, Color dot)
    {
        var (card, col, inner) = Box(w);
        col.Controls.Add(new SectionTitle(_t, title, dot) { Width = inner, Margin = new Padding(0, 0, 0, _gap / 2) });
        return (card, col, inner);
    }

    private FlowLayoutPanel Row(int w) => new()
    {
        FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink, MaximumSize = new Size(w, 0), BackColor = Color.Transparent, Margin = new Padding(0)
    };

    private IEnumerable<Control> FactRows(int inner, IEnumerable<(string label, string value)> facts)
    {
        int labelW = Math.Min(inner / 3, TextRenderer.MeasureText("Not invoiced because", _t.BodyBold).Width + 12);
        foreach (var (l, v) in facts)
        {
            var row = Row(inner);
            row.Controls.Add(new Label { Text = l, Font = _t.BodyBold, ForeColor = _t.Muted, BackColor = Color.Transparent, AutoSize = false, Width = labelW, Height = _line + 4, Margin = new Padding(0), UseMnemonic = false });
            row.Controls.Add(new Label { Text = v, Font = _t.Body, ForeColor = _t.Ink, BackColor = Color.Transparent, AutoSize = true, MaximumSize = new Size(inner - labelW - 4, 0), Margin = new Padding(0, 0, 0, 2), UseMnemonic = false });
            yield return row;
        }
    }

    private Card CallOutCard(int w, TicketDetail d)
    {
        var (card, col, inner) = Section(w, "Call out", _t.Blue);
        foreach (var r in FactRows(inner, new[]
        {
            ("Site address", d.SiteAddress.Length > 0 ? d.SiteAddress : "—"),
            ("Appointment", d.Ticket.AppointmentUtc?.ToLocalTime().ToString("dddd d MMMM 'at' HH:mm") ?? "not scheduled")
        })) col.Controls.Add(r);
        return card;
    }

    /// <summary>Whether the open ticket's "Cancel the task…" has been pressed, showing the box for why.</summary>
    private bool _cancellingRecurring;

    /// <summary>
    /// A recurring task's ticket (2026-10-07): the schedule it came from, as the server sent it, and —
    /// for the people on it, a supervisor or the owner — Cancel, asking why in the window itself.
    /// Changing the days or the people is the portal's form, opened signed in.
    /// </summary>
    private Card? RecurringCard(int w, TicketDetail d)
    {
        var r = d.Recurring;
        if (r == null) return null;
        var (card, col, inner) = Section(w, "Recurring task", _t.Blue);
        var due = d.Ticket.DueOn is DateTime on ? on.ToString("dddd d MMMM yyyy") + (d.Ticket.Overdue ? " — OVERDUE" : "") : "—";
        foreach (var row in FactRows(inner, new[] { ("Type", r.Kind), ("Repeats", r.Repeats), ("Due", due), (r.Cancelled ? "Cancelled" : "Next", r.Next.Replace("Next: ", "")) }))
            col.Controls.Add(row);
        if (!r.MayChange) return card;

        int taskId = r.TaskId, ticketId = d.Ticket.Id;
        var taskAt = r.TaskNumber > 0 ? $"/portal/r/{r.TaskNumber}" : $"/portal/recurring/{taskId}";
        if (_cancellingRecurring)
        {
            col.Controls.Add(Text_(inner, "Why is it no longer needed? No more tickets will be opened; this one stays open until it is done.", _t.Body, _t.Ink));
            var why = new InputBox(_t, multiline: true, height: _line * 2) { Width = inner, Margin = new Padding(0, 0, 0, _gap / 2) }.GrowWithText();
            if (_drafts.TryGetValue("recurring-cancel", out var was)) why.Value = was;
            why.Box.TextChanged += (_, _) => _drafts["recurring-cancel"] = why.Value;
            col.Controls.Add(why);
            var buttons = Row(inner);
            var stop = Button_("Cancel the task", BtnStyle.Accent);
            stop.Click += (_, _) =>
            {
                _notice = null;
                _link.CancelRecurring(new RecurringCancelMsg { TaskId = taskId, TicketId = ticketId, Reason = why.Value.Trim() });
            };
            var back = Button_("Keep it", BtnStyle.Ghost);
            back.Click += (_, _) => { _cancellingRecurring = false; _drafts.Remove("recurring-cancel"); Draw(d, keepScroll: true); };
            buttons.Controls.Add(stop); buttons.Controls.Add(back);
            col.Controls.Add(buttons);
        }
        else
        {
            var buttons = Row(inner);
            var change = Button_("Change in the browser", BtnStyle.Neutral);
            change.Click += (_, _) => _link.RequestPortalLink(taskAt);
            var cancel = Button_("Cancel the task…", BtnStyle.Ghost);
            cancel.Click += (_, _) => { _cancellingRecurring = true; Draw(d, keepScroll: true); };
            buttons.Controls.Add(change); buttons.Controls.Add(cancel);
            col.Controls.Add(buttons);
        }
        return card;
    }

    /// <summary>Who has it and how urgent — and, for a ticket with no workflow, its status.</summary>
    private void AssignRow(FlowLayoutPanel col, int inner, TicketDetail d, bool withStatus)
    {
        var row2 = Row(inner);
        ComboBox? status = null;
        if (withStatus)
        {
            status = new ThemedCombo(_t) { Font = _t.Body, FlatStyle = FlatStyle.Flat, Width = _line * 7, Margin = new Padding(0, 4, _gap, _gap) };
            status.Items.AddRange(TicketSummary.StatusNames.Cast<object>().ToArray());
            status.SelectedIndex = Math.Clamp(d.Ticket.Status, 0, status.Items.Count - 1);
            row2.Controls.Add(status);
        }
        var prio = new ThemedCombo(_t) { Font = _t.Body, FlatStyle = FlatStyle.Flat, Width = _line * 6, Margin = new Padding(0, 4, _gap, _gap) };
        prio.Items.AddRange(TicketSummary.PriorityNames.Cast<object>().ToArray());
        prio.SelectedIndex = Math.Clamp(d.Ticket.Priority, 0, prio.Items.Count - 1);

        // The people on it: a tick box each, because several people may work one job at once
        // (2026-09-28). What may be changed is the server's rule drawn — it decides whatever is sent —
        // and a box that may not be changed is greyed and sent as it stands.
        var on = SplitPeople(d.Ticket.AssignedTo);
        var me = _link.DisplayName;
        bool meOn = on.Contains(me);
        bool MayChange(string name) =>
            d.CanAssignToAnyone
            || (name == me ? (meOn ? d.CanPassOn : d.CanJoin) : meOn && d.CanPassOn && !on.Contains(name));
        var people = Row(inner);
        people.Name = "people";
        var boxes = new List<CheckBox>();
        foreach (var name in _link.Roster.Concat(on.Where(n => !_link.Roster.Contains(n))))
        {
            var box = new RoundCheck(_t)
            {
                Text = _link.Roster.Contains(name) ? name : name + " (no longer a technician)", Tag = name,
                Checked = on.Contains(name), Enabled = MayChange(name), AutoSize = true, Font = _t.Body,
                ForeColor = _t.Ink, BackColor = Color.Transparent, Margin = new Padding(0, 2, _gap * 2, 2), UseMnemonic = false
            };
            boxes.Add(box); people.Controls.Add(box);
        }
        col.Controls.Add(people);

        var save = Button_("Save", BtnStyle.Neutral);
        save.Click += (_, _) =>
        {
            _notice = null;
            // The server decides what of this the person may change: a technician adds colleagues only
            // to a job they are on and never takes one off (Permissions.CanPassOn), and a workflow
            // job's status is refused whatever is sent.
            _link.UpdateTicket(new TicketUpdateMsg
            {
                TicketId = d.Ticket.Id, Status = status?.SelectedIndex, Priority = prio.SelectedIndex,
                AssignedTo = string.Join(", ", boxes.Where(b => b.Checked).Select(b => (string)b.Tag!))
            });
        };
        row2.Controls.Add(prio); row2.Controls.Add(save);
        col.Controls.Add(row2);
    }

    /// <summary>"Jason, Ryan" into its names — the server's Assignees.Parse, which the console cannot reference.</summary>
    internal static List<string> SplitPeople(string stored) =>
        stored.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0).Distinct().ToList();

    /// <summary>A ticket with no workflow: its description, then what was written, oldest first.</summary>
    private Card HistoryCard(int w, TicketDetail d)
    {
        var (card, col, inner) = Section(w, "History", _t.Muted);
        if (d.Description.Trim().Length > 0)
        {
            col.Controls.Add(Text_(inner, "Description", _t.BodyBold, _t.Ink));
            col.Controls.Add(Text_(inner, d.Description, _t.Body, _t.Ink));
        }
        foreach (var n in d.Notes)
        {
            col.Controls.Add(Text_(inner, $"{n.CreatedUtc.ToLocalTime():d MMM HH:mm} · {n.Author}{(n.IsSystem ? " (automatic)" : "")}", _t.Small, _t.Muted));
            var body = Text_(inner, n.Body, n.IsSystem ? _t.Small : _t.Body, n.IsSystem ? _t.Muted : _t.Ink);
            body.Margin = new Padding(0, 0, 0, _gap);
            col.Controls.Add(body);
            foreach (var c in NoteEmail(inner, n)) col.Controls.Add(c);
        }
        if (d.Notes.Count == 0) col.Controls.Add(Text_(inner, "Nothing recorded yet.", _t.Small, _t.Muted));
        return card;
    }
}
