using System.Drawing;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Technician;

/// <summary>
/// Raise a job by hand. The form changes with the category: call outs ask for an address and
/// appointment, workshop bookings ask for the machine's details and password.
/// </summary>
public sealed class NewTicketDialog : Form
{
    private readonly Theme _t;
    private readonly TechLink _link;

    private readonly ComboBox _category, _priority;
    /// <summary>The job types as coloured buttons (2026-10-09), in CategoryOrder — each sets <see cref="_category"/>.</summary>
    private readonly List<RoundButton> _kinds = new();
    /// <summary>A tick box per person: several people may be on one job from the start (2026-09-28).</summary>
    private readonly FlowLayoutPanel _people;
    private readonly InputBox _subject, _description;
    private readonly InputBox _clientSearch;
    private readonly ListBox _matches;
    private readonly FieldFrame _matchesFrame;
    private readonly Label _clientState;
    private readonly InputBox _clientPhone, _clientEmail;

    private readonly FlowLayoutPanel _calloutPanel, _workshopPanel;
    /// <summary>What the right column says when the chosen category needs nothing extra, so it
    /// reads as "nothing more to fill in" rather than as a blank half of the window.</summary>
    private readonly Label _noExtras;
    private readonly InputBox _siteAddress;
    private readonly DateBox _apptDate;
    private readonly TimeBox _apptTime;
    private readonly CheckBox _hasAppt;
    /// <summary>When the job is due (2026-10-26, HANDOVER §165): ticked, the day below is sent.</summary>
    private readonly CheckBox _hasDue;
    private readonly DateBox _dueDate;
    private readonly InputBox _make, _model, _serial, _accessories, _devicePassword;
    private readonly CheckBox _pwProtected;

    // ---- recurring task (2026-10-07): a schedule set up once, not one ticket ----
    private readonly FlowLayoutPanel _recurringPanel, _weekPanel;
    private readonly ThemedCombo _kind, _repeats, _monthDay;
    private readonly InputBox _newKind;
    private readonly CheckBox _weekdaysOnly;
    /// <summary>People a recurring task may go to who are not on the console's roster — the office.
    /// Shown only while Recurring task is chosen: the other categories keep the roster.</summary>
    private readonly List<CheckBox> _officePeople = new();
    private const string NewKindItem = "New type…";
    /// <summary>The left column and the right's tallest panel, kept so the window can grow once for the office's names.</summary>
    private readonly FlowLayoutPanel _left;
    private int _rightNeeds;

    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 250 };
    private List<ClientInfo> _found = new();
    private int _pickedClientId;

    private readonly Label _photoLabel;
    private readonly RoundButton _photoBtn;
    /// <summary>
    /// Identifies this booking so photos uploaded before the ticket exists can be attached to it
    /// when it is created. One per dialog: two people booking machines in at one counter never
    /// share a pile of photographs.
    /// </summary>
    private readonly string _stageId = Guid.NewGuid().ToString("N");
    private int _photosUploaded;

    /// <summary>The number the server will insist on, as the company has it NOW (Job types, §168: 3, or 0 when it does not
    /// ask) — told by the server, so the two cannot quietly differ. It used to be a constant 3 here.</summary>
    private readonly int PhotosRequired;

    /// <summary>The categories offered, in <c>TicketSummary.CategoryOrder</c> less the ones the company has switched off
    /// (Job types, §168). The hidden combo, the buttons and <see cref="Chosen"/> are all in THIS order.</summary>
    private readonly int[] _offered;

    private string PhotoPrompt(int have) =>
        PhotosRequired == 0
            ? $"Photos of the machine as it arrived: {have}. Your company doesn't ask for them - add some if they help."
            : have >= PhotosRequired
            ? $"Photos of the machine as it arrived: {have}. That is enough to book it in - add more if the damage needs them."
            : $"Photos of the machine as it arrived: {have} of at least {PhotosRequired}.\r\nThis is what settles an argument about a scratch six weeks later.";

    public TicketCreateMsg? Result { get; private set; }

    public NewTicketDialog(Theme t, TechLink link)
    {
        _t = t; _link = link;
        PhotosRequired = link.BookingPhotos;
        _offered = TicketSummary.CategoryOrder.Where(link.CategoryOn).ToArray();

        // ------------------------------------------------------------------ layout
        //
        // Rebuilt 2026-09-20. It was one 592 px column of absolutely positioned controls stepped
        // along with `y += 40` constants, inside a 1180 px card in a scroller — so on a 150%
        // display, where one line of the body font is 28 px and not the ~18 those constants
        // assumed, everything crowded and the CATEGORY PANEL FELL OFF THE BOTTOM. Choosing
        // Workshop appeared to do nothing at all, which is how "it did not ask for photos" was
        // reported. §30 of the handover, a fourth time.
        //
        // Two things fix it and both are structural rather than a bigger number:
        //
        //   TWO COLUMNS. The job on the left, what the CATEGORY needs on the right. The right
        //   column is the part that changes, and putting it beside the form rather than below it
        //   means it is on screen the moment it appears.
        //
        //   FLOW LAYOUT, not coordinates. A hidden control takes no space in a FlowLayoutPanel, so
        //   the client-matches list stops leaving an 84 px hole when there is nothing to show —
        //   the empty gap under the search box in Clint's screenshot — and the panels reflow
        //   themselves when anything is shown or hidden. Nothing here is positioned by hand.

        int line = TextRenderer.MeasureText("Ag", t.Body).Height;
        int fieldH = line + 16;                         // a single-line box, measured
        int colW = Math.Max(420, line * 17);            // ~17 lines of text wide; grows with the font
        int gap = line / 2;

        Text = "New ticket";
        FormBorderStyle = FormBorderStyle.Sizable;      // a fixed dialog cannot cope with a long list
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = true; MinimizeBox = false;
        Font = t.Body; BackColor = t.Mist;
        Shown += (_, _) => NativeDark.Apply(this, t);   // dark title bar, scrollbars and list header — see NativeDark

        var canvas = new CanvasPanel(t) { Dock = DockStyle.Fill, Padding = new Padding(16) };
        Controls.Add(canvas);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, BackColor = Color.Transparent, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false, Padding = new Padding(0, 10, 0, 0),
            Height = TextRenderer.MeasureText("Ag", t.ButtonFont).Height + 34
        };
        int btnH = TextRenderer.MeasureText("Ag", t.ButtonFont).Height + 20;
        var ok = new RoundButton(t, BtnStyle.Accent) { Text = "Create ticket", Size = new Size(160, btnH), Margin = new Padding(0) };
        var cancel = new RoundButton(t, BtnStyle.Ghost) { Text = "Cancel", Size = new Size(110, btnH), DialogResult = DialogResult.Cancel, Margin = new Padding(10, 0, 0, 0) };
        ok.Click += (_, _) => Submit();
        buttons.Controls.Add(ok); buttons.Controls.Add(cancel);
        canvas.Controls.Add(buttons);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Transparent };
        canvas.Controls.Add(scroll);
        scroll.BringToFront();

        // The two columns. AutoSize so each is exactly as tall as what it holds, which is what
        // lets the window size itself to its contents at the end of this method.
        FlowLayoutPanel Column() => new()
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent,
            Width = colW, Margin = new Padding(0)
        };

        var left = Column();
        _left = left;
        var right = Column();

        var card = new Card(t, 18)
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Width = colW * 2 + gap * 4 + 36
        };
        scroll.Controls.Add(card);

        var columns = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Dock = DockStyle.Top
        };
        columns.Controls.Add(left);
        columns.Controls.Add(new Panel { Width = gap * 3, Height = 1, BackColor = Color.Transparent, Margin = new Padding(0) });
        columns.Controls.Add(right);
        card.Controls.Add(columns);

        // ---- the little builders every field goes through ----
        // One place that decides spacing, so the dialog cannot drift into six different gaps, and
        // one place that measures rather than assumes.
        Label Head(FlowLayoutPanel col, string text)
        {
            var l = new Label
            {
                Text = text, Font = t.H3, ForeColor = t.Ink, BackColor = Color.Transparent,
                AutoSize = false, Width = colW, Height = TextRenderer.MeasureText("Ag", t.H3).Height + 4,
                Margin = new Padding(0, gap, 0, gap / 2), UseMnemonic = false
            };
            col.Controls.Add(l); return l;
        }
        Label Lab(FlowLayoutPanel col, string text)
        {
            var l = new Label
            {
                Text = text, Font = t.BodyBold, ForeColor = t.Ink, BackColor = Color.Transparent,
                AutoSize = false, Width = colW, Height = TextRenderer.MeasureText("Ag", t.BodyBold).Height + 2,
                Margin = new Padding(0, gap / 2, 0, 2), UseMnemonic = false
            };
            col.Controls.Add(l); return l;
        }
        T Add<T>(FlowLayoutPanel col, T c) where T : Control
        { c.Margin = new Padding(0, 0, 0, 2); col.Controls.Add(c); return c; }

        /// A row holding two controls side by side, each half the column.
        Panel Pair(FlowLayoutPanel col, Control a, Control b, int height)
        {
            int half = (colW - gap) / 2;
            a.SetBounds(0, 0, half, height);
            b.SetBounds(half + gap, 0, half, height);
            var row = new Panel { Width = colW, Height = height, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 2) };
            row.Controls.Add(a); row.Controls.Add(b);
            col.Controls.Add(row); return row;
        }

        // ================================================================== left: the job
        // "Type of job" and its buttons are a row across the TOP of the dialog, over both columns (built below).
        _category = Add(left, new ThemedCombo(t) { Width = colW, Font = t.Body });
        // Offered in CategoryOrder, which is NOT value order — General leads and is stored as 4.
        // Everything downstream reads the value back through Chosen(), never the combo's index.
        _category.Items.AddRange(_offered
            .Select(v => (object)TicketSummary.CategoryNames[v]).ToArray());
        _category.SelectedIndex = Array.IndexOf(_offered, (int)TicketCategoryKind.General);
        _category.SelectedIndexChanged += (_, _) => ShowCategoryFields();

        // The job type as a row of buttons, each in its category's colour, the chosen one filled (Clint,
        // 2026-10-09: the wrong category had been chosen "because it is not pronounced enough" — it was this dropdown).
        // The dropdown stays, hidden, as the one record of the choice: Chosen() and everything after it read it, and
        // a button only sets it. Colours: TicketSummary.CategoryColors, shared with the portal.
        _category.Visible = false;
        var kinds = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, MaximumSize = new Size(colW * 2 + gap * 4, 0), BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, gap / 2)
        };
        for (int i = 0; i < _offered.Length; i++)
        {
            int index = i, value = _offered[i];
            var (fill, onDark) = TicketSummary.CategoryColors[value];
            // No "…" after Recurring task here: in the portal it opens another page, in the console its form is beside this one.
            var name = TicketSummary.CategoryNames[value];
            var b = new RoundButton(t, BtnStyle.Ghost)
            {
                Text = name, Tag = name, Tint = ColorTranslator.FromHtml(fill), TintInk = ColorTranslator.FromHtml(t.IsDark ? onDark : fill),
                // Sized to the words alone: a tick's room reserved on all six pushed the row onto a second line at
                // 150% (ticketformprobe), so the chosen one is shown by its fill — white words on its colour.
                Size = new Size(TextRenderer.MeasureText(name, t.ButtonFont).Width + 32, btnH),
                Margin = new Padding(0, 0, gap / 2, gap / 2)
            };
            b.Click += (_, _) => _category.SelectedIndex = index;
            _kinds.Add(b);
            kinds.Controls.Add(b);
        }
        void ShowChosen()
        {
            for (int i = 0; i < _kinds.Count; i++)
            {
                var b = _kinds[i];
                b.Chosen = i == _category.SelectedIndex;
                b.Text = (string)b.Tag!;
                b.AccessibleName = (string)b.Tag! + (b.Chosen ? ", chosen" : "");
                b.Invalidate();
            }
        }
        _category.SelectedIndexChanged += (_, _) => ShowChosen();
        ShowChosen();
        // Across the top of the card, over both columns: the first thing seen, and one row rather than three
        // stacked in the left column, which pushed the full-size window past the screen (ticketformprobe).
        var typeRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top, BackColor = Color.Transparent, Margin = new Padding(0), Padding = new Padding(0, 0, 0, gap / 2)
        };
        Head(typeRow, "Type of job");
        typeRow.Controls.Add(kinds);
        card.Controls.Add(typeRow);
        typeRow.SendToBack();   // docked first, so above the columns

        Head(left, "Client");
        Lab(left, "Search or type a new client name");
        _clientSearch = Add(left, new InputBox(t) { Width = colW, Height = fieldH });
        _clientSearch.Box.TextChanged += (_, _) => { _pickedClientId = 0; _debounce.Stop(); _debounce.Start(); };

        // Hidden until there is something to show, and in a flow layout that means it takes no
        // room at all rather than leaving a hole.
        // In a rounded frame, its rows drawn in the palette (2026-10-06) — it had Windows' square
        // black border and its blue selection bar.
        _matches = new ListBox { Font = t.Body }.Themed(t);
        _matches.Size = new Size(colW, _matches.ItemHeight * 4);
        _matchesFrame = Add(left, new FieldFrame(t, _matches) { Width = colW, Visible = false });
        _matches.DoubleClick += (_, _) => PickMatch();
        _matches.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; PickMatch(); } };

        _clientState = Add(left, new Label
        {
            Text = "No client selected — a new one will be created.", Font = t.Small, ForeColor = t.Muted,
            BackColor = Color.Transparent, AutoSize = false, Width = colW,
            // Two lines: the "3 existing clients match…" sentence is longer than one.
            Height = TextRenderer.MeasureText("Ag", t.Small).Height * 2 + 2
        });

        // Short enough to fit the column at any scaling. The long version ran off the end at 150%
        // and read "…when creating a new" — a label that is cut is worse than a terse one.
        Lab(left, "Phone / email (new clients only)");
        _clientPhone = new InputBox(t);
        _clientEmail = new InputBox(t);
        Pair(left, _clientPhone, _clientEmail, fieldH);

        Head(left, "The job");
        Lab(left, "Subject");
        _subject = Add(left, new InputBox(t) { Width = colW, Height = fieldH });
        Lab(left, "Description / fault reported");
        _description = Add(left, new InputBox(t, multiline: true, height: line * 4) { Width = colW }.GrowWithText());
        // Priority, and beside it when the job is due (2026-10-26, §165) — optional, for any type of job, a day never before
        // today (the server says so if it is). On the SAME two rows Priority always had: the tick is the due date's label,
        // so the window grows by nothing (ticketformprobe: the full-size window needs no scrollbar).
        int labH = TextRenderer.MeasureText("Ag", t.BodyBold).Height + 2;
        var prioLab = new Label { Text = "Priority", Font = t.BodyBold, ForeColor = t.Ink, BackColor = Color.Transparent, UseMnemonic = false };
        _hasDue = new RoundCheck(t) { Text = "Due date", Checked = false, Font = t.BodyBold, ForeColor = t.Ink, BackColor = Color.Transparent, AutoSize = false };
        Pair(left, prioLab, _hasDue, Math.Max(labH, line + 6)).Margin = new Padding(0, gap / 2, 0, 2);
        _priority = new ThemedCombo(t) { Font = t.Body };
        _priority.Items.AddRange(TicketSummary.PriorityNames.Cast<object>().ToArray());
        _priority.SelectedIndex = 1;
        _dueDate = new DateBox(t) { Format = "ddd d MMM yyyy", Value = DateTime.Today, Enabled = false };
        Pair(left, _priority, _dueDate, Math.Max(fieldH, _priority.Height));
        _hasDue.CheckedChanged += (_, _) => _dueDate.Enabled = _hasDue.Checked;
        Lab(left, "People on this job");
        // Wraps onto as many lines as the team needs; each box is as wide as its own name.
        _people = Add(left, new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, MaximumSize = new Size(colW, 0), Width = colW,
            BackColor = Color.Transparent
        });
        foreach (var name in link.Roster)
            _people.Controls.Add(new RoundCheck(t)
            {
                Text = name, Tag = name, Checked = name == link.DisplayName, AutoSize = true, Font = t.Body,
                ForeColor = t.Ink, BackColor = Color.Transparent, Margin = new Padding(0, 2, gap * 2, 2), UseMnemonic = false
            });

        // ================================================================== right: the category
        // What a remote support or walk-in job needs is nothing extra, so the column says so
        // rather than sitting empty and looking broken.
        _noExtras = Add(right, new Label
        {
            Text = "", Font = t.Body, ForeColor = t.Muted, BackColor = Color.Transparent,
            AutoSize = false, Width = colW, Height = TextRenderer.MeasureText("Ag", t.Body).Height * 3,
            Margin = new Padding(0, gap * 3, 0, 0)
        });

        // ---- call out ----
        _calloutPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = colW, BackColor = Color.Transparent,
            Visible = false, Margin = new Padding(0)
        };
        right.Controls.Add(_calloutPanel);
        Head(_calloutPanel, "Call out");
        Lab(_calloutPanel, "Site address");
        _siteAddress = Add(_calloutPanel, new InputBox(t, multiline: true, height: line * 3) { Width = colW });
        _hasAppt = Add(_calloutPanel, new RoundCheck(t)
        {
            Text = "Book an appointment", Checked = true, AutoSize = false, Width = colW,
            Height = line + 8, Font = t.BodyBold, ForeColor = t.Ink, BackColor = Color.Transparent
        });
        _hasAppt.CheckedChanged += (_, _) => { _apptDate.Enabled = _apptTime.Enabled = _hasAppt.Checked; };
        _apptDate = new DateBox(t) { Format = "ddd d MMM yyyy", Value = DateTime.Today };
        _apptTime = new TimeBox(t) { Value = DateTime.Today.AddHours(9) };
        Pair(_calloutPanel, _apptDate, _apptTime, fieldH);

        // ---- workshop ----
        _workshopPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = colW, BackColor = Color.Transparent,
            Visible = false, Margin = new Padding(0)
        };
        right.Controls.Add(_workshopPanel);
        Head(_workshopPanel, "Workshop booking");
        Lab(_workshopPanel, "Make / model");
        _make = new InputBox(t);
        _model = new InputBox(t);
        Pair(_workshopPanel, _make, _model, fieldH);
        Lab(_workshopPanel, "Serial number");
        _serial = Add(_workshopPanel, new InputBox(t) { Width = colW, Height = fieldH });
        Lab(_workshopPanel, "Accessories supplied (charger, bag, cables…)");
        _accessories = Add(_workshopPanel, new InputBox(t, multiline: true, height: line * 3) { Width = colW });
        _pwProtected = Add(_workshopPanel, new RoundCheck(t)
        {
            Text = "Machine is password protected", AutoSize = false, Width = colW, Height = line + 8,
            Font = t.BodyBold, ForeColor = t.Ink, BackColor = Color.Transparent
        });
        _devicePassword = Add(_workshopPanel, new InputBox(t) { Width = colW, Height = fieldH, Visible = false });
        // Hiding it in a flow layout closes the gap, so the photo prompt moves up rather than
        // leaving a blank strip where the password box would have been.
        _pwProtected.CheckedChanged += (_, _) => { _devicePassword.Visible = _pwProtected.Checked; };

        // ---- photos of the machine as it arrived ----
        // Three of them, or it is not booked in. The count here is a courtesy; the rule is on the
        // server, in TicketPhotoService, so the browser form and this dialog meet the same one.
        _photoLabel = Add(_workshopPanel, new Label
        {
            Text = PhotoPrompt(0), Font = t.BodyBold, ForeColor = t.Ink, BackColor = Color.Transparent,
            AutoSize = false, Width = colW,
            // Three lines of its own font: it explains itself until the three photos are there.
            Height = TextRenderer.MeasureText("Ag", t.BodyBold).Height * 3 + 4
        });
        _photoLabel.Margin = new Padding(0, gap, 0, 4);
        _photoBtn = Add(_workshopPanel, new RoundButton(t, BtnStyle.Neutral)
        {
            Text = "Add photos…", Size = new Size(180, btnH)
        });
        _photoBtn.Click += async (_, _) => await PickPhotosAsync();

        // ---- recurring task ----
        // Set up once; a ticket then opens for its people on the morning of every day it is due.
        // The rules are the server's (RecurringTaskService) — this only asks.
        _recurringPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = colW, BackColor = Color.Transparent,
            Visible = false, Margin = new Padding(0)
        };
        right.Controls.Add(_recurringPanel);
        Head(_recurringPanel, "Recurring task");
        Add(_recurringPanel, new Label
        {
            Text = "Set up once: a ticket opens for the people ticked on the morning of every day it is due. The client is optional.",
            Font = t.Small, ForeColor = t.Muted, BackColor = Color.Transparent, AutoSize = true, MaximumSize = new Size(colW, 0), UseMnemonic = false
        });
        Lab(_recurringPanel, "Type of task");
        _kind = Add(_recurringPanel, new ThemedCombo(t) { Width = colW, Font = t.Body });
        foreach (var k in new[] { "Maintenance", "Billing", "Readings" }) _kind.Items.Add(k);   // until the server's list arrives
        _kind.Items.Add(NewKindItem);
        _kind.SelectedIndex = 0;
        _newKind = Add(_recurringPanel, new InputBox(t) { Width = colW, Height = fieldH, Visible = false });
        _newKind.Box.PlaceholderText = "Name the new type";
        _kind.SelectedIndexChanged += (_, _) => { _newKind.Visible = (_kind.SelectedItem as string) == NewKindItem; if (_newKind.Visible) _newKind.Box.Focus(); };

        Lab(_recurringPanel, "How often");
        _repeats = Add(_recurringPanel, new ThemedCombo(t) { Width = (colW - gap) / 2, Font = t.Body });
        _repeats.Items.AddRange(new object[] { "Daily", "Weekly", "Monthly" });
        _repeats.SelectedIndex = 1;
        _weekdaysOnly = Add(_recurringPanel, new RoundCheck(t)
        {
            Text = "Weekdays only (Monday to Friday)", AutoSize = false, Width = colW, Height = line + 8,
            Font = t.Body, ForeColor = t.Ink, BackColor = Color.Transparent
        });
        _weekPanel = Add(_recurringPanel, new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, MaximumSize = new Size(colW, 0), Width = colW, BackColor = Color.Transparent
        });
        foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday })
            _weekPanel.Controls.Add(new RoundCheck(t)
            {
                Text = day.ToString(), Tag = 1 << (int)day, Checked = day == DateTime.Today.DayOfWeek, AutoSize = true, Font = t.Body,
                ForeColor = t.Ink, BackColor = Color.Transparent, Margin = new Padding(0, 2, gap * 2, 2), UseMnemonic = false
            });
        _monthDay = Add(_recurringPanel, new ThemedCombo(t) { Width = (colW - gap) / 2, Font = t.Body });
        for (int d = 1; d <= 31; d++)
            _monthDay.Items.Add($"The {d}{(d % 100 is 11 or 12 or 13 ? "th" : (d % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" })}{(d >= 29 ? " (or the last day)" : "")}");
        _monthDay.Items.Add("The last day of the month");
        _monthDay.SelectedIndex = DateTime.Today.Day - 1;
        _repeats.SelectedIndexChanged += (_, _) => ShowRepeats();
        ShowRepeats();

        // The company's own types, and the office as people a task may go to.
        _link.OnRecurringKinds += OnRecurringKinds;
        _link.RequestRecurringKinds();

        _debounce.Tick += (_, _) => { _debounce.Stop(); var term = _clientSearch.Value.Trim(); if (term.Length >= 2) _link.SearchClients(term); else { _matchesFrame.Visible = false; UpdateClientState(); } };
        _link.OnClientList += OnClients;
        FormClosed += (_, _) => { _link.OnClientList -= OnClients; _link.OnRecurringKinds -= OnRecurringKinds; _debounce.Stop(); };

        AcceptButton = ok; CancelButton = cancel;
        Shown += (_, _) => { try { _clientSearch.Box.Focus(); } catch { } };
        ShowCategoryFields();

        // ---- size it to what it holds ----
        //
        // The width is COMPUTED from the columns, not read back from the card. A card that is
        // Dock = Top with AutoSize reports its PARENT's width, and at this point the parent is an
        // empty panel a few hundred pixels wide — so reading card.Width produced a 560 px window
        // with a 1030 px card in it and the whole right-hand column off the right edge.
        // `ticketformprobe` caught that on its first run; nothing else would have.
        int cardW = colW * 2 + gap * 3 + 36;                  // two columns, the gap, the card's padding
        int frameW = Width - ClientSize.Width;                // the border, whatever the OS makes it
        int frameH = Height - ClientSize.Height;

        // Tall enough for the TALLEST category, measured by showing each panel and asking. Sizing
        // to whatever happens to be showing means the window jumps about as somebody changes the
        // category, and a form that resizes under the cursor is worse than one that is too big.
        int rightNeeds = 0;
        foreach (var panel in new[] { _calloutPanel, _workshopPanel, _recurringPanel })
        {
            bool was = panel.Visible;
            panel.Visible = true;
            rightNeeds = Math.Max(rightNeeds, panel.PreferredSize.Height);
            panel.Visible = was;
        }
        // Plus the job types' row across the top (2026-10-09), which sits over both columns.
        int contentH = typeRow.PreferredSize.Height + Math.Max(left.PreferredSize.Height, rightNeeds) + 36;
        _rightNeeds = rightNeeds;

        var work = Screen.FromPoint(Cursor.Position).WorkingArea;
        ClientSize = new Size(
            Math.Min(cardW + canvas.Padding.Horizontal + 8, work.Width - frameW),
            Math.Min(contentH + canvas.Padding.Vertical + buttons.Height + 12, work.Height - frameH));

        // A floor that cannot exceed the screen: on a small laptop the clamp above wins and a
        // MinimumSize larger than it would fight it for ever.
        MinimumSize = new Size(
            Math.Min(cardW / 2, work.Width - frameW),
            Math.Min(contentH / 2, work.Height - frameH));
    }

    private void Inv(Action a) { try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { } }

    private void OnClients(ClientListMsg m) => Inv(() =>
    {
        _found = m.Clients;
        _matches.BeginUpdate();
        _matches.Items.Clear();
        foreach (var c in _found) _matches.Items.Add(c.Display);
        _matchesFrame.Visible = _found.Count > 0;
        _matches.EndUpdate();
        UpdateClientState();
    });

    private void PickMatch()
    {
        if (_matches.SelectedIndex < 0 || _matches.SelectedIndex >= _found.Count) return;
        var c = _found[_matches.SelectedIndex];
        _pickedClientId = c.Id;
        _clientSearch.Box.TextChanged -= null!;
        _clientSearch.Value = c.Name;
        _pickedClientId = c.Id;       // set again: assigning Value raises TextChanged which clears it
        _matchesFrame.Visible = false;
        UpdateClientState();
    }

    private void UpdateClientState()
    {
        if (_pickedClientId > 0)
        {
            _clientState.Text = "Linked to existing client — no duplicate will be created.";
            _clientState.ForeColor = _t.OkText;
        }
        else if (_found.Count > 0)
        {
            _clientState.Text = $"{_found.Count} existing client(s) match — double-click one to link, or keep typing to create a new client.";
            _clientState.ForeColor = _t.WarnText;
        }
        else
        {
            _clientState.Text = "No client selected — a new one will be created.";
            _clientState.ForeColor = _t.Muted;
        }
    }

    /// <summary>
    /// The category the picker is on. The combo is drawn in <c>TicketSummary.CategoryOrder</c>,
    /// which is not value order, so its SelectedIndex is a position on screen and nothing else.
    /// Casting that index straight to a category would file every job under the wrong heading.
    /// </summary>
    private TicketCategoryKind Chosen()
    {
        int i = _category.SelectedIndex;
        if (i < 0 || i >= _offered.Length) return TicketCategoryKind.General;
        return (TicketCategoryKind)_offered[i];
    }

    /// <summary>The company's types of recurring task, and everybody a task may go to.</summary>
    private void OnRecurringKinds(RecurringKindsMsg m) => Inv(() =>
    {
        if (m.Kinds.Count > 0)
        {
            var was = _kind.SelectedItem as string;
            _kind.Items.Clear();
            foreach (var k in m.Kinds) _kind.Items.Add(k);
            _kind.Items.Add(NewKindItem);
            int i = was == null ? -1 : _kind.Items.IndexOf(was);
            _kind.SelectedIndex = i >= 0 ? i : 0;
        }
        var shown = _people.Controls.OfType<CheckBox>().Select(b => (string)b.Tag!).ToHashSet();
        foreach (var name in m.People.Where(p => !shown.Contains(p)))
        {
            var box = new RoundCheck(_t)
            {
                Text = name, Tag = name, AutoSize = true, Font = _t.Body, ForeColor = _t.Ink, BackColor = Color.Transparent,
                Margin = new Padding(0, 2, _people.Controls.Count > 0 ? _people.Controls[0].Margin.Right : 12, 2), UseMnemonic = false,
                Visible = Chosen() == TicketCategoryKind.Recurring
            };
            _officePeople.Add(box);
            _people.Controls.Add(box);
        }
        FitOfficePeople();
    });

    /// <summary>
    /// The office's names arrive a moment after the window is sized, and on a recurring task they add a
    /// line to the people — enough to bring in a scrollbar that squeezed the right column (ticketformprobe's
    /// screenshot showed the Type box cut off). So the window grows ONCE, now, by what that line needs,
    /// measured with the names shown — the tallest the left column gets — rather than jumping when the
    /// category changes.
    /// </summary>
    private void FitOfficePeople()
    {
        if (_officePeople.Count == 0) return;
        var was = _officePeople.Select(b => b.Visible).ToList();
        foreach (var b in _officePeople) b.Visible = true;
        int need = Math.Max(_left.PreferredSize.Height, _rightNeeds) + 36;
        for (int i = 0; i < _officePeople.Count; i++) _officePeople[i].Visible = was[i];
        int have = Math.Max(_left.PreferredSize.Height, _rightNeeds) + 36;
        if (need <= have) return;
        var work = Screen.FromControl(this).WorkingArea;
        int frameH = Height - ClientSize.Height;
        ClientSize = new Size(ClientSize.Width, Math.Min(ClientSize.Height + need - have, work.Height - frameH));
    }

    private void ShowRepeats()
    {
        _weekdaysOnly.Visible = _repeats.SelectedIndex == 0;
        _weekPanel.Visible = _repeats.SelectedIndex == 1;
        _monthDay.Visible = _repeats.SelectedIndex == 2;
    }

    private void ShowCategoryFields()
    {
        var cat = Chosen();
        _calloutPanel.Visible = cat == TicketCategoryKind.CallOut;
        _workshopPanel.Visible = cat == TicketCategoryKind.Workshop;
        _recurringPanel.Visible = cat == TicketCategoryKind.Recurring;
        // The office may be given a recurring task (a billing run, a reading); never anything else here.
        foreach (var b in _officePeople) { b.Visible = _recurringPanel.Visible; if (!b.Visible) b.Checked = false; }

        // The right column always says something. An empty half-window reads as a fault, and a
        // technician who has just chosen Workshop and sees nothing appear assumes the same.
        _noExtras.Visible = !_calloutPanel.Visible && !_workshopPanel.Visible && !_recurringPanel.Visible;
        _noExtras.Text = cat switch
        {
            TicketCategoryKind.WalkIn => "A walk in needs nothing else — fill in the job on the left and create it.",
            TicketCategoryKind.RemoteSupport => "Remote support needs nothing else — fill in the job on the left and create it.",
            _ => "A general job needs nothing else — fill in the job on the left and create it."
        };
    }

    /// <summary>
    /// Picks photos and sends them straight up, staged against this booking.
    ///
    /// Uploaded now rather than when OK is pressed, so a slow set of pictures does not look like a
    /// frozen dialog at the moment somebody is trying to finish a job at the counter.
    /// </summary>
    private async Task PickPhotosAsync()
    {
        using var pick = new OpenFileDialog
        {
            Title = "Photos of the machine as it arrived",
            Filter = "Photos|*.jpg;*.jpeg;*.png;*.webp;*.heic|All files|*.*",
            Multiselect = true
        };
        if (pick.ShowDialog(this) != DialogResult.OK || pick.FileNames.Length == 0) return;

        _photoBtn.Enabled = false;
        var was = _photoBtn.Text;
        _photoBtn.Text = "Uploading…";
        try
        {
            var (uploaded, error) = await _link.UploadPhotosAsync(pick.FileNames, "before", _stageId,
                progress: (i, n) => { try { BeginInvoke(() => _photoBtn.Text = n > 1 ? $"Uploading {i} of {n}…" : "Uploading…"); } catch { } });
            _photosUploaded += uploaded;
            _photoLabel.Text = PhotoPrompt(_photosUploaded);
            if (error.Length > 0)
                MessageBox.Show(this, error, "Photos", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { _photoBtn.Text = was; _photoBtn.Enabled = true; }
    }

    /// <summary>Everybody ticked, as the server stores them: "Jason, Ryan", or empty for nobody.</summary>
    private string ChosenPeople() =>
        string.Join(", ", _people.Controls.OfType<CheckBox>().Where(b => b.Checked).Select(b => (string)b.Tag!));

    private void Submit()
    {
        if (_subject.Value.Trim().Length == 0) { MessageBox.Show(this, "Please enter a subject.", "New ticket"); return; }
        var cat = Chosen();
        // A General ticket may be an internal instruction with no client (Clint, 2026-09-27); it can
        // be linked to one later, from the ticket.
        if (_clientSearch.Value.Trim().Length == 0 && cat is not (TicketCategoryKind.General or TicketCategoryKind.Recurring))
        { MessageBox.Show(this, "Please choose or name a client.", "New ticket"); return; }

        // A recurring task: said here so nobody is refused at the end; the server checks it all again.
        int days = _weekPanel.Controls.OfType<CheckBox>().Where(b => b.Checked).Aggregate(0, (a, b) => a | (int)b.Tag!);
        string kind = (_kind.SelectedItem as string) == NewKindItem ? _newKind.Value.Trim() : _kind.SelectedItem as string ?? "";
        if (cat == TicketCategoryKind.Recurring)
        {
            string problem = kind.Length == 0 ? "Name the new type of task."
                : ChosenPeople().Length == 0 ? "Tick who this task goes to — at least one person."
                : _repeats.SelectedIndex == 1 && days == 0 ? "Tick at least one day of the week." : "";
            if (problem.Length > 0) { MessageBox.Show(this, problem, "Recurring task", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        }

        // Said here so nobody fills the whole form and is refused by the server at the end. The
        // server checks it again regardless — that is the rule, this is the manners.
        if (cat == TicketCategoryKind.Workshop && _photosUploaded < PhotosRequired)
        {
            MessageBox.Show(this,
                $"A workshop booking needs {PhotosRequired} photos of the machine as it arrived.\r\n\r\n" +
                $"{_photosUploaded} added so far.",
                "New ticket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // A workshop machine is booked in to a named technician (Clint, 2026-09-24). The server
        // refuses it otherwise; saying so here saves filling the form in twice.
        if (cat == TicketCategoryKind.Workshop && ChosenPeople().Length == 0)
        {
            MessageBox.Show(this, "Tick the technician this machine is booked in to.", "New ticket",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        DateTime? appt = null;
        if (cat == TicketCategoryKind.CallOut && _hasAppt.Checked)
            appt = _apptDate.Value.Date.Add(_apptTime.Value.TimeOfDay).ToUniversalTime();

        Result = new TicketCreateMsg
        {
            Subject = _subject.Value.Trim(),
            Description = _description.Value.Trim(),
            Priority = Math.Max(0, _priority.SelectedIndex),
            Category = (int)cat,
            ClientId = _pickedClientId,
            NewClientName = _pickedClientId > 0 ? "" : _clientSearch.Value.Trim(),
            NewClientPhone = _clientPhone.Value.Trim(),
            NewClientEmail = _clientEmail.Value.Trim(),
            AssignToMe = false,
            AssignTo = ChosenPeople(),
            SiteAddress = cat == TicketCategoryKind.CallOut ? _siteAddress.Value.Trim() : "",
            AppointmentUtc = appt,
            // The DAY, at midnight marked UTC — never this PC's local midnight, which the server would read in its own zone.
            DueDate = _hasDue.Checked ? DateTime.SpecifyKind(_dueDate.Value.Date, DateTimeKind.Utc) : null,
            AssetMake = cat == TicketCategoryKind.Workshop ? _make.Value.Trim() : "",
            AssetModel = cat == TicketCategoryKind.Workshop ? _model.Value.Trim() : "",
            AssetSerial = cat == TicketCategoryKind.Workshop ? _serial.Value.Trim() : "",
            Accessories = cat == TicketCategoryKind.Workshop ? _accessories.Value.Trim() : "",
            DevicePasswordProtected = cat == TicketCategoryKind.Workshop && _pwProtected.Checked,
            DevicePassword = cat == TicketCategoryKind.Workshop && _pwProtected.Checked ? _devicePassword.Value : "",
            // Tells the server where this booking's photos are waiting, so it can attach them to
            // the ticket it is about to create.
            PhotoStageId = cat == TicketCategoryKind.Workshop ? _stageId : "",
            // A recurring task: the schedule the server sets up instead of one ticket.
            RecurringKind = cat == TicketCategoryKind.Recurring ? kind : "",
            Repeats = Math.Max(0, _repeats.SelectedIndex),
            WeekdaysOnly = _weekdaysOnly.Checked,
            Days = days,
            // The list is the 1st to the 31st, then the last day (0).
            MonthDay = _monthDay.SelectedIndex is >= 0 and < 31 ? _monthDay.SelectedIndex + 1 : 0
        };
        DialogResult = DialogResult.OK;
        Close();
    }
}
