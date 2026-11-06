using System.Drawing;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Client;

/// <summary>
/// The customer asking for help: their name, a subject, what is wrong in their own words, and a picture of their screen.
///
/// 2026-10-09, Clint: "change the request support to include a subject line and then the description as currently the
/// description, if long creates a mess of the tickets" — the one box used to become the ticket's subject whole. Now the
/// SUBJECT is a line (required, <see cref="SubjectMax"/> at most) and becomes the ticket's subject and the alert's big
/// text; the DESCRIPTION is optional and becomes the ticket's description. And "a screenshot option ... would be a
/// great feature": <see cref="ScreenSnap"/>, taken before this window opened, offered ticked with a preview so they
/// can see what will be sent and untick it.
///
/// 2026-10-13: "Take a new picture" beside the preview, for when it is not what they meant (see <see cref="ScreenSnap"/>
/// for why it so often shows only the desktop); and the name starts as the last one given (<see cref="LastContact"/>).
/// </summary>
public sealed class RequestDialog : Form
{
    /// <summary>Long enough for "Outlook keeps asking for my password", short enough to stay one line in the queue.</summary>
    public const int SubjectMax = SupportRequest.SubjectMax;

    private readonly InputBox _name;
    private readonly InputBox _email;
    private readonly InputBox _subject;
    private readonly InputBox _details;
    private readonly RoundCheck? _withShot;
    private readonly Label _hint;
    private readonly Theme _t;
    /// <summary>The picture that will be sent. The caller's at first; a new one if they took it again.</summary>
    private ScreenSnap? _shot;
    /// <summary>The caller's picture, which is theirs to dispose — any taken here are this window's.</summary>
    private readonly ScreenSnap? _given;
    private readonly PictureBox? _pic;
    private readonly Label? _note;
    private readonly RoundButton? _retake;
    private readonly Action? _placeShot;
    private readonly Font _noteBold;
    public string ContactName => _name.Value.Trim();
    /// <summary>Their email, optional (2026-10-23) — filled in from the last request or the install. Empty when blank.</summary>
    public string ContactEmail => GivenPerson.CleanEmail(_email.Value);
    public string Subject => _subject.Value.Trim();
    public string Details => _details.Value.Trim();
    /// <summary>The picture goes with the request: there is one, and they left it ticked.</summary>
    public bool IncludeScreenshot => _withShot?.Checked == true && _shot != null;
    /// <summary>The JPEG to send with the request, or null when there is none or it was unticked.</summary>
    public byte[]? Screenshot => IncludeScreenshot ? _shot!.Jpeg : null;

    private const string NoteTaken = "Taken just now — this is what will be sent.";
    private const string NoteDesktop = "Only your desktop is showing. Open what you need help with, then take a new picture.";
    private const string NoteNone = "No picture will be sent.";

    /// <param name="shot">The picture taken before this opened, or null when none could be.</param>
    public RequestDialog(string defaultName, Theme t, Branding brand, ScreenSnap? shot = null, string defaultEmail = "")
    {
        _t = t;
        _shot = _given = shot;
        _noteBold = new Font(t.Small, FontStyle.Bold);
        Text = "Request Support";
        ClientSize = new Size(520, 540);   // and taller by the picture row, once it is measured
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false;
        Font = t.Body;
        BackColor = t.Mist;
        Shown += (_, _) => NativeDark.Apply(this, t);   // dark title bar, scrollbars and list header — see NativeDark

        var canvas = new CanvasPanel(t) { Dock = DockStyle.Fill, Padding = new Padding(18) };
        Controls.Add(canvas);

        var card = new Card(t, 22) { Dock = DockStyle.Fill };
        canvas.Controls.Add(card);

        // Every height from the font it draws with (§30 of the handover: pixel constants written at 100% cut words off
        // at 150%).
        int labelH = TextRenderer.MeasureText("Ag", t.BodyBold).Height + 10;
        int smallH = TextRenderer.MeasureText("Ag", t.Small, new Size(1000, int.MaxValue), TextFormatFlags.NoPadding).Height + 6;
        Label FieldLabel(string text) => new()
        {
            Text = text, Dock = DockStyle.Top, Height = labelH, Font = t.BodyBold, ForeColor = t.Ink, BackColor = Color.Transparent,
            TextAlign = ContentAlignment.BottomLeft, Padding = new Padding(0, 0, 0, 4), UseMnemonic = false
        };

        // buttons, right-aligned in a deterministic order
        int btnH = TextRenderer.MeasureText("Ag", t.ButtonFont).Height + 20;
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = btnH + 14, BackColor = Color.Transparent,
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 10, 0, 0)
        };
        // No DialogResult on the button. With one set, Windows closes the dialog on the click
        // before anything can object, and the subject has to be able to object (stage 3.5.7).
        var ok = new RoundButton(t, BtnStyle.Accent) { Text = "Request Now", Size = new Size(TextRenderer.MeasureText("Request Now", t.ButtonFont).Width + 48, btnH), Margin = new Padding(0) };
        var cancel = new RoundButton(t, BtnStyle.Ghost) { Text = "Cancel", Size = new Size(TextRenderer.MeasureText("Cancel", t.ButtonFont).Width + 48, btnH), DialogResult = DialogResult.Cancel, Margin = new Padding(10, 0, 0, 0) };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        _name = new InputBox(t) { Dock = DockStyle.Top };
        _name.Height = _name.Box.PreferredHeight + 16;
        _name.Value = defaultName;

        // Optional (2026-10-23): kept with the name, so linking the machine adds the person with it.
        _email = new InputBox(t) { Dock = DockStyle.Top };
        _email.Height = _email.Box.PreferredHeight + 16;
        _email.Box.MaxLength = GivenPerson.EmailMax;
        _email.Value = defaultEmail;
        ClientSize = new Size(ClientSize.Width, ClientSize.Height + labelH + _email.Height);

        // Required (stage 3.5.7): a call that rings with nothing to say is a call answered blind.
        _subject = new InputBox(t) { Dock = DockStyle.Top };
        _subject.Height = _subject.Box.PreferredHeight + 16;
        _subject.Box.MaxLength = SubjectMax;

        _details = new InputBox(t, multiline: true, height: TextRenderer.MeasureText("Ag", t.Body).Height * 4 + 14) { Dock = DockStyle.Fill };

        // Said only when it is needed, and said in the dialog rather than in a message box on top of it: a modal
        // complaint about a modal dialog is two windows to dismiss before the person can do what they were doing.
        _hint = new Label
        {
            Text = "", Dock = DockStyle.Bottom, Height = smallH, Font = t.Small, ForeColor = t.BadText,
            BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false
        };

        // The picture: a tick, ticked, and the picture itself beside it, so nobody sends what they have not seen.
        // The tick on a line of its own, the picture under it and the words beside the picture: side by side with the
        // picture, the tick's own words were cut off (callprobe, 2026-10-09).
        // 2026-10-13: and a button beside it to take it again, under the words — the customer who came in through the
        // desktop shortcut has a picture of their desktop, with the window they wanted help with minimised.
        Panel? shotRow = null;
        if (shot != null)
        {
            var screen = shot.Preview;
            const int thumbW = 176;
            int thumbH = Math.Max(60, thumbW * screen.Height / Math.Max(1, screen.Width));
            var check = new RoundCheck(t) { Text = "Include a picture of my screen", Checked = true };
            int checkH = check.PreferredSize.Height;
            var pic = new PictureBox
            {
                Size = new Size(thumbW, thumbH), Image = screen, SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent, BorderStyle = BorderStyle.FixedSingle
            };
            var note = new Label
            {
                Text = NoteTaken, AutoSize = false, Font = t.Small, ForeColor = t.Muted,
                BackColor = Color.Transparent, UseMnemonic = false
            };
            var retake = new RoundButton(t, BtnStyle.Ghost)
            {
                Text = "Take a new picture", Size = new Size(TextRenderer.MeasureText("Take a new picture", t.ButtonFont).Width + 32, btnH)
            };
            // The words and the button beside the picture, measured for the longest thing the words can say, so the
            // row is tall enough before it is ever laid out (the card's padding and the canvas's, then the picture).
            int besideW = Math.Max(60, ClientSize.Width - 2 * canvas.Padding.Left - 2 * card.Padding.Left - (thumbW + 2) - 12);
            // In the font each is drawn in: the desktop note is bold, and measured in the plain font it pushed the
            // button off the bottom of the row (callprobe, 2026-10-13).
            int noteMaxH = new[] { (NoteTaken, t.Small), (NoteDesktop, _noteBold), (NoteNone, t.Small) }
                .Max(n => TextRenderer.MeasureText(n.Item1, n.Item2, new Size(besideW, int.MaxValue), TextFormatFlags.WordBreak).Height + 4);
            int bodyH = Math.Max(thumbH, noteMaxH + 8 + btnH);
            shotRow = new Panel { Dock = DockStyle.Bottom, Height = 6 + checkH + 6 + bodyH + 6, BackColor = Color.Transparent };
            var row = shotRow;
            void Place()
            {
                check.Location = new Point(0, 6);
                pic.Location = new Point(2, 6 + checkH + 6);
                int left = check.Checked ? pic.Right + 12 : 0;
                int noteW = Math.Max(10, row.ClientSize.Width - left);
                int noteH = TextRenderer.MeasureText(note.Text, note.Font, new Size(noteW, int.MaxValue), TextFormatFlags.WordBreak).Height + 4;
                // The tick, not retake.Visible: Visible reads false until the window is shown (§109 of the handover).
                int groupH = noteH + (check.Checked ? 8 + btnH : 0);
                int top = pic.Top + Math.Max(0, (bodyH - groupH) / 2);
                note.SetBounds(left, top, noteW, noteH);
                retake.Width = Math.Min(TextRenderer.MeasureText(retake.Text, t.ButtonFont).Width + 32, noteW);
                retake.Location = new Point(left, note.Bottom + 8);
            }
            shotRow.Resize += (_, _) => Place();
            shotRow.Controls.Add(check); shotRow.Controls.Add(pic); shotRow.Controls.Add(note); shotRow.Controls.Add(retake);
            check.CheckedChanged += (_, _) => ShowShotState();
            retake.Click += (_, _) => Retake();
            _withShot = check; _pic = pic; _note = note; _retake = retake; _placeShot = Place;
            ShowShotState();
            ClientSize = new Size(ClientSize.Width, ClientSize.Height + shotRow.Height);
        }

        // add in reverse visual order: last added sits at the top; the Fill box first, so it takes what is left
        card.Controls.Add(_details);
        card.Controls.Add(FieldLabel("Tell us more (optional)"));
        card.Controls.Add(_subject);
        card.Controls.Add(FieldLabel("What do you need help with? A few words"));
        card.Controls.Add(_email);
        card.Controls.Add(FieldLabel("Your email (optional)"));
        card.Controls.Add(_name);
        card.Controls.Add(FieldLabel("Your name"));
        card.Controls.Add(new Label { Text = "Request support", Dock = DockStyle.Top, Height = TextRenderer.MeasureText("Ag", t.H2).Height + 12, Font = t.H2, ForeColor = t.Ink, BackColor = Color.Transparent });
        // At the bottom, the LAST added docks lowest: the complaint under the buttons (where it always was), and the
        // picture above the buttons — added first, so the buttons stay the last thing above the complaint.
        if (shotRow != null) card.Controls.Add(shotRow);
        card.Controls.Add(buttons);
        card.Controls.Add(_hint);

        ok.Click += (_, _) => Submit();
        AcceptButton = ok; CancelButton = cancel;
        // Typing is the answer to the complaint, so the complaint goes as soon as they do.
        _subject.Box.TextChanged += (_, _) => { if (_hint.Text.Length > 0 && Describes(Subject)) _hint.Text = ""; };
        _name.NextField(_email.Box);
        _email.NextField(_subject.Box);
        _subject.NextField(_details.Box);
        Shown += (_, _) => { try { _name.Box.Focus(); _name.Box.SelectAll(); } catch { } };
    }

    /// <summary>
    /// Is this a subject, or is it somebody getting past a required field?
    ///
    /// Emptiness is the whole test, plus a floor of three characters to catch "." and "x". It is
    /// deliberately not cleverer than that: any rule strong enough to reject "asdf" would also
    /// reject somebody in a hurry writing something true and short, and the person it would
    /// obstruct is by definition already having a bad day. The floor exists to stop a keystroke
    /// dismissing the question, not to grade the answer.
    /// </summary>
    private static bool Describes(string s) => s.Trim().Length >= 3;

    /// <summary>The words beside the picture, and what is shown, for the tick and the picture as they are now.</summary>
    private void ShowShotState()
    {
        if (_withShot == null || _pic == null || _note == null || _retake == null) return;
        bool on = _withShot.Checked;
        bool desktop = on && _shot is { OnlyDesktop: true };
        _pic.Visible = on;
        _retake.Visible = on;
        _note.Text = !on ? NoteNone : desktop ? NoteDesktop : NoteTaken;
        // The desktop-only note is advice to act on, so it reads as one; the others are just information.
        _note.ForeColor = desktop ? _t.WarnText : _t.Muted;
        _note.Font = desktop ? _noteBold : _t.Small;
        _placeShot?.Invoke();
    }

    /// <summary>
    /// Out of the way while the customer brings up what they want us to see, then a new picture (2026-10-13).
    ///
    /// This window and the tray window behind it go invisible rather than hidden: hiding a modal window ends it, and an
    /// invisible (alpha 0) window lets the clicks through to whatever is under it. The bar is closed before the picture,
    /// and <see cref="ScreenSnap.Take"/> waits the frame it takes to leave the screen.
    /// </summary>
    private void Retake()
    {
        var mine = new[] { (Form)this, Owner }.Where(f => f is { Visible: true }).Select(f => (Form: f!, Was: f!.Opacity)).ToList();
        try
        {
            foreach (var (f, _) in mine) { f.Opacity = 0; f.Update(); }
            using (var bar = new PictureBar(_t, Screen.FromControl(this)))
                if (bar.ShowDialog(this) != DialogResult.OK) return;
            var fresh = ScreenSnap.Take(mine.Select(m => m.Form).ToArray());
            if (fresh == null) return;   // the screen could not be read: keep the picture there was
            var old = _shot;
            _shot = fresh;
            if (_pic != null) _pic.Image = fresh.Preview;
            if (old != null && old != _given) old.Dispose();
            if (_withShot != null) _withShot.Checked = true;
            ShowShotState();
        }
        finally
        {
            foreach (var (f, was) in mine) try { f.Opacity = was; } catch { }
            try { Activate(); } catch { }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _shot != null && _shot != _given) _shot.Dispose();
        base.Dispose(disposing);
    }

    private void Submit()
    {
        if (!Describes(Subject))
        {
            // Short enough to fit the card on one line at any sensible display scale.
            _hint.Text = "Please say in a few words what is wrong.";
            try { _subject.Box.Focus(); } catch { }
            return;
        }
        // Blank is fine; half an address is not — it would be kept beside their name.
        if (_email.Value.Trim().Length > 0 && !GivenPerson.LooksLikeEmail(_email.Value))
        {
            _hint.Text = "That email address does not look right.";
            try { _email.Box.Focus(); } catch { }
            return;
        }
        DialogResult = DialogResult.OK;
    }
}
