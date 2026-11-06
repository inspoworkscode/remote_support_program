using System.Drawing;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Technician;

/// <summary>
/// "Who does this machine belong to?" — the console's half of what the portal's Devices page has
/// always been able to do.
///
/// Clint, 2026-09-20: *"devices should be an option that everybody has, otherwise it's only the
/// owner/supervisor that can link them to a client or contact. This should also be a console
/// feature."* Both halves of that are the point. Until a machine is linked its tickets never reach
/// a client page, so an unlinked machine is a hole in the records — and the person who notices it
/// is whoever is looking at the list, which is a technician in the console, not the owner in a
/// browser.
///
/// **One list, not two.** A company and a person at that company are the same decision, so they
/// are rows in one list rather than a client dropdown that then enables a contact dropdown.
/// Picking "Bob at Zenith" has said everything; being made to confirm Zenith afterwards is the
/// second control that "one control beats two" exists to avoid. It is the same
/// reasoning the portal's single owner picker was built on, so the two surfaces behave alike.
/// </summary>
public sealed class LinkDeviceDialog : Form
{
    /// <summary>
    /// How many customers to fetch. The type-ahead's default of 12 is right while somebody types
    /// a name they already know; it is wrong here, because somebody looking at a stray machine
    /// often does not know whose it is and needs to be able to read down the list.
    /// </summary>
    private const int FetchLimit = 500;

    private readonly Theme _t;
    private readonly TechLink _link;
    private readonly ListView _list;
    private readonly InputBox _filter;
    private readonly Label _note;
    private List<ClientInfo> _clients = new();

    /// <summary>The client chosen, or 0. Set only when the dialog returns OK.</summary>
    public int ClientId { get; private set; }
    /// <summary>The person chosen, or 0. A person implies their company.</summary>
    public int ContactId { get; private set; }
    /// <summary>The person chosen, by name, or empty — a ticket keeps its contact as a name.</summary>
    public string ContactName { get; private set; } = "";

    /// <summary>
    /// The name typed into Request Support on this machine (2026-10-18), while nobody has linked it: people on file it
    /// fits are offered at the top, and so is adding it as a new contact. Empty for anything else.
    /// </summary>
    private readonly string _given = "";
    /// <summary>"Add {name} as a new contact…" was chosen: the list is companies only, asking which one they are at.</summary>
    private bool _adding;
    private Label _head = null!, _sub = null!;
    private string _headText = "", _subText = "";

    /// <summary>The name to add as a new contact at <see cref="ClientId"/>, or empty. Set only when the dialog returns OK.</summary>
    public string NewContactName { get; private set; } = "";

    private readonly string _givenEmail = "";
    /// <summary>
    /// "Add {name} as a contact when I choose a customer" (2026-10-23), ticked: choosing a COMPANY row adds the person who gave
    /// the name there, with their email, and links the machine to them. Unticked for a shared PC, which belongs to the company.
    /// </summary>
    private RoundCheck? _addGiven;

    public LinkDeviceDialog(Theme t, TechLink link, DeviceInfo device)
        : this(t, link, "Who does this machine belong to?", device.MachineName,
            device.ContactId > 0 && device.ContactName.Length > 0
                ? $"Currently {device.ContactName}" + (device.ClientName.Length > 0 ? $" at {device.ClientName}." : ".")
                : device.ClientId > 0 && device.ClientName.Length > 0
                    ? $"Currently {device.ClientName}."
                    : device.NamedByRequest
                        ? $"Not linked to anybody yet. They gave the name {device.RequestedBy}" + (device.RequestedByEmail.Length > 0 ? $" ({device.RequestedByEmail})." : ".")
                        : "Not linked to anybody yet — its tickets will not reach a customer's page until it is.",
            device.ClientId, device.ContactId,
            // The name given at the install or a request, while no PERSON is linked (2026-10-23) — linked to a client alone,
            // the tick still adds them there.
            device.ContactId <= 0 ? device.RequestedBy : "", device.ContactId <= 0 ? device.RequestedByEmail : "")
    { }

    /// <summary>
    /// The same choice for anything else that belongs to a customer — a ticket, since 2026-09-27
    /// ("there should always be the option to link to clients at any stage").
    /// </summary>
    public LinkDeviceDialog(Theme t, TechLink link, string title, string heading, string now, int clientId, int contactId,
        string given = "", string givenEmail = "")
    {
        _t = t; _link = link;
        _currentClient = clientId; _currentContact = contactId;
        _given = OwnerNames.Clean(given);
        _givenEmail = (givenEmail ?? "").Trim();
        _headText = heading; _subText = now;

        Text = title;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;
        Font = t.Body; BackColor = t.Mist;
        Shown += (_, _) => NativeDark.Apply(this, t);

        var canvas = new CanvasPanel(t) { Dock = DockStyle.Fill, Padding = new Padding(18) };
        Controls.Add(canvas);
        var card = new Card(t, 18) { Dock = DockStyle.Fill };
        canvas.Controls.Add(card);

        // Every height is measured from the font that will draw it. §30: three faults of this one
        // shape, two of them invisible on this machine, all from a pixel constant written against
        // a 100% display.
        int headLine = TextRenderer.MeasureText("Ag", t.H3).Height;
        int smallLine = TextRenderer.MeasureText("Ag", t.Small).Height;
        int btnH = TextRenderer.MeasureText("Ag", t.ButtonFont).Height + 16;

        var head = _head = new Label
        {
            Dock = DockStyle.Top, Text = heading, Font = t.H3, ForeColor = t.Ink,
            BackColor = Color.Transparent, Height = headLine + 6, UseMnemonic = false
        };

        // Says where it stands now rather than leaving the technician to work it out from which
        // row happens to be selected. A machine nobody has claimed says so in those words, because
        // that is the case this dialog exists for.
        var sub = _sub = new Label
        {
            Dock = DockStyle.Top, Text = now, Font = t.Small, ForeColor = t.Muted,
            BackColor = Color.Transparent, Height = smallLine * 2 + 6, UseMnemonic = false
        };

        _note = new Label
        {
            Dock = DockStyle.Bottom, Text = "", Font = t.Small, ForeColor = Color.FromArgb(0xC0, 0x39, 0x2B),
            BackColor = Color.Transparent, Height = smallLine + 4, Visible = false
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            BackColor = Color.Transparent, Height = btnH + 10, Padding = new Padding(0, 8, 0, 0)
        };
        var save = new RoundButton(t, BtnStyle.Accent) { Text = "Save", Size = new Size(130, btnH), Margin = new Padding(0) };
        var cancel = new RoundButton(t, BtnStyle.Ghost)
        { Text = "Cancel", Size = new Size(100, btnH), DialogResult = DialogResult.Cancel, Margin = new Padding(10, 0, 0, 0) };
        save.Click += (_, _) => Submit();
        buttons.Controls.Add(save); buttons.Controls.Add(cancel);

        _list = new ListView().Themed(t);
        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.HeaderStyle = ColumnHeaderStyle.None;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.GridLines = false;
        _list.Columns.Add("", 460);
        _list.DoubleClick += (_, _) => Submit();

        _filter = new InputBox(t) { Dock = DockStyle.Top };
        _filter.Box.TextChanged += (_, _) => Fill();
        // Typing narrows the list; it is not a second way of choosing. Enter still means Save,
        // which is why the filter box does not swallow it.
        var filterHint = new Label
        {
            Dock = DockStyle.Top, Text = "Start typing to narrow the list.", Font = t.Small,
            ForeColor = t.Muted, BackColor = Color.Transparent, Height = smallLine + 8
        };

        card.Controls.Add(_list);
        card.Controls.Add(filterHint);
        card.Controls.Add(_filter);
        card.Controls.Add(sub);
        card.Controls.Add(head);
        // Above the complaint and the buttons (the last added docks lowest). Wrapped to the window's width, measured (§30).
        if (_given.Length > 0)
        {
            var words = $"Add {_given}{(_givenEmail.Length > 0 ? $" ({_givenEmail})" : "")} as a contact when I choose a customer";
            _addGiven = new RoundCheck(t)
            {
                Text = words, Checked = true, AutoSize = false, Dock = DockStyle.Bottom, Font = t.Small, ForeColor = t.Ink,
                CheckAlign = ContentAlignment.TopLeft, TextAlign = ContentAlignment.TopLeft
            };
            void Fit()
            {
                int boxW = SystemInformation.MenuCheckSize.Width + 8;
                _addGiven.Height = TextRenderer.MeasureText(words, t.Small, new Size(Math.Max(60, card.ClientSize.Width - card.Padding.Horizontal - boxW), 0),
                    TextFormatFlags.WordBreak).Height + 10;
            }
            card.Resize += (_, _) => Fit();
            Fit();
            card.Controls.Add(_addGiven);
        }
        card.Controls.Add(_note);
        card.Controls.Add(buttons);

        AcceptButton = save; CancelButton = cancel;
        ClientSize = new Size(560, 520);
        MinimumSize = new Size(420, 380);

        _link.OnClientList += OnClients;
        FormClosed += (_, _) => _link.OnClientList -= OnClients;
        // Everything, not a type-ahead page: see FetchLimit.
        _link.SearchClients("", FetchLimit);

        Fill();
        Shown += (_, _) => { try { _filter.Box.Focus(); } catch { } };
    }

    private readonly int _currentClient, _currentContact;

    private void OnClients(ClientListMsg m)
    {
        if (IsDisposed) return;
        try { BeginInvoke(new Action(() => { _clients = m.Clients; Fill(); })); } catch { }
    }

    /// <summary>
    /// One row per choice: unlink first, then each company with its people beneath it.
    ///
    /// The people are indented under their company rather than written "Bob (Zenith)" in a flat
    /// list, because the question being answered is "whose is this" and the company is the coarse
    /// answer somebody reaches for first.
    /// </summary>
    private void Fill()
    {
        var term = _filter.Value.Trim();
        _list.BeginUpdate();
        _list.Items.Clear();

        // Which client is the new contact at (2026-10-18): companies only, and a way back.
        if (_adding)
        {
            _list.Items.Add(new ListViewItem("← Back to everybody") { Tag = Back, ForeColor = _t.Muted });
            foreach (var c in _clients.Where(c => Matches(c.Name, term)).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                _list.Items.Add(new ListViewItem(c.Name) { Tag = new Choice(c.Id, 0) });
            if (_list.Items.Count == 1)
                _list.Items.Add(new ListViewItem(_clients.Count == 0
                    ? "No customers on file yet — add one in the portal first."
                    : "Nothing matches that.") { Tag = null, ForeColor = _t.Muted });
            var first = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag is Choice);
            if (first != null) first.Selected = true;
            _list.EndUpdate();
            return;
        }

        // The name they gave at a support request (2026-10-18): the people on file it fits, best first, then adding it
        // as somebody new — offered at the top, never chosen for anybody. Save untouched still changes nothing.
        if (_given.Length > 0 && term.Length == 0)
        {
            var fits = _clients.SelectMany(c => c.Contacts.Select(p => (c, p, score: OwnerNames.Likeness(_given, p.Name))))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score).ThenBy(x => x.p.Name, StringComparer.OrdinalIgnoreCase)
                .Take(5);
            foreach (var (c, p, _) in fits)
                _list.Items.Add(new ListViewItem($"{p.Name} — {c.Name}") { Tag = new Choice(c.Id, p.Id) });
            _list.Items.Add(new ListViewItem($"+ Add {_given} as a new contact…") { Tag = AddNew });
        }

        // Unlinking is a real answer — a machine sold on, or linked to the wrong customer by
        // mistake — so it is a row rather than a separate button.
        var none = new ListViewItem("Not linked to anybody") { Tag = new Choice(0, 0) };
        if (term.Length == 0) _list.Items.Add(none);

        foreach (var c in _clients.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var people = c.Contacts.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var companyHit = Matches(c.Name, term) || Matches(c.ContactName, term)
                             || Matches(c.Phone, term) || Matches(c.Email, term);
            var hits = people.Where(p => companyHit || Matches(p.Name, term) || Matches(p.Phone, term)
                                         || Matches(p.Email, term)).ToList();
            // A company is shown when it matches itself, or when one of its people does — a person
            // with no company above them reads as an orphan.
            if (!companyHit && hits.Count == 0) continue;

            _list.Items.Add(new ListViewItem(c.Name) { Tag = new Choice(c.Id, 0) });
            foreach (var p in hits)
                _list.Items.Add(new ListViewItem("      " + p.Name) { Tag = new Choice(c.Id, p.Id) });
        }

        // Never a blank list with no explanation: say which of the two reasons it is.
        if (_list.Items.Count == 0)
            _list.Items.Add(new ListViewItem(_clients.Count == 0
                ? "No customers on file yet — add one in the portal first."
                : "Nothing matches that.")
            { Tag = null, ForeColor = _t.Muted });

        Reselect();
        _list.EndUpdate();
    }

    /// <summary>Puts the cursor on the current owner, so Save without touching anything
    /// changes nothing.</summary>
    private void Reselect()
    {
        var want = new Choice(_currentClient, _currentContact);
        foreach (ListViewItem it in _list.Items)
            if (it.Tag is Choice c && c.Equals(want)) { it.Selected = true; it.EnsureVisible(); return; }
        if (_list.Items.Count > 0 && _list.Items[0].Tag is Choice) _list.Items[0].Selected = true;
    }

    private static bool Matches(string value, string term) =>
        term.Length == 0 || (value ?? "").Contains(term, StringComparison.OrdinalIgnoreCase);

    private void Submit()
    {
        var tag = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag : null;
        // "Add them as a new contact" and "back" change what the list asks rather than answering it.
        if (tag == AddNew || tag == Back) { SetAdding(tag == AddNew); return; }
        if (tag is not Choice c || (_adding && c.ClientId <= 0))
        {
            // Inside the window, not a message box on top of it — ReasonDialog's reasoning: a
            // message box to say "you did not choose" is two dismissals for one mistake.
            _note.Text = _adding ? $"Choose the customer {_given} is at." : "Choose a customer, a person, or “Not linked to anybody”.";
            _note.Visible = true;
            return;
        }
        ClientId = c.ClientId; ContactId = c.ContactId;
        ContactName = _adding ? _given
            : _clients.FirstOrDefault(x => x.Id == c.ClientId)?.Contacts.FirstOrDefault(p => p.Id == c.ContactId)?.Name ?? "";
        NewContactName = _adding ? _given : "";
        // A customer chosen with the tick on (2026-10-23): the person who gave the name is added there and linked. The
        // server takes their email from the machine's record, never from here.
        if (!_adding && c.ClientId > 0 && c.ContactId <= 0 && _addGiven is { Checked: true })
            ContactName = NewContactName = _given;
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>Into, or out of, asking which customer the new contact is at.</summary>
    private void SetAdding(bool adding)
    {
        _adding = adding;
        // Adding them is what this step does already: the tick would only be a second way of saying it.
        if (_addGiven != null) _addGiven.Visible = !adding;
        _head.Text = adding ? $"Which customer is {_given} at?" : _headText;
        _sub.Text = adding ? $"{_given} is added as a contact there, and this machine linked to them." : _subText;
        _note.Visible = false;
        _filter.Value = "";
        Fill();
        try { _filter.Box.Focus(); } catch { }
    }

    /// <summary>One row's answer. A contact carries its client, so the two can never disagree.</summary>
    private readonly record struct Choice(int ClientId, int ContactId);
    /// <summary>The rows that are not answers: adding the given name as somebody new, and going back from it.</summary>
    private static readonly object AddNew = new(), Back = new();
}
