using System.Drawing;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Host;

/// <summary>
/// Branded "a technician wants to connect" dialog shown on the client machine before any capture.
/// Runs on its own STA thread so it works regardless of the host process's threading model, and
/// auto-declines after a timeout so an unattended machine never grants access by being left alone.
/// </summary>
/// <summary>How the question was answered. "Declined" and "nobody answered" are different
/// conversations for the technician to have, so they are told apart (2026-10-02).</summary>
internal enum ConsentAnswer { Granted, Declined, TimedOut, Withdrawn }

internal static class ConsentPrompt
{
    /// <summary>The prompt's real timeout for a configured one: never under five seconds. The
    /// technician's countdown is told this number, so the two cannot disagree.</summary>
    public static int Seconds(int configured) => Math.Max(5, configured <= 0 ? 60 : configured);

    /// <param name="withdrawn">Cancelled when the session ends while the question is up — the
    /// technician gave up waiting — which takes it off the person's screen.</param>
    /// <param name="visitorCompany">Somebody from ANOTHER company, let in with a code the person made (3.9):
    /// their company. The prompt then says they are not this IT company, and what they can and cannot do.</param>
    /// <returns>The answer, and — for somebody from outside only — whether they also ticked "let them copy files".</returns>
    public static (ConsentAnswer Answer, bool Files) Ask(Branding brand, string technicianName, int timeoutSeconds, CancellationToken withdrawn = default, string visitorCompany = "")
    {
        var answer = ConsentAnswer.Declined;
        bool files = false;
        var t = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var form = new ConsentForm(brand, technicianName, timeoutSeconds, visitorCompany);
                using var reg = withdrawn.Register(() => { try { form.BeginInvoke(form.Withdraw); } catch { } });
                // Withdrawn before the window had a handle to invoke on: caught when it appears.
                form.Shown += (_, _) => { if (withdrawn.IsCancellationRequested) form.Withdraw(); };
                var r = form.ShowDialog();
                answer = r == DialogResult.OK ? ConsentAnswer.Granted
                       : form.WasWithdrawn || withdrawn.IsCancellationRequested ? ConsentAnswer.Withdrawn
                       : form.TimedOut ? ConsentAnswer.TimedOut
                       : ConsentAnswer.Declined;
                files = answer == ConsentAnswer.Granted && form.FilesAllowed;
            }
            catch { answer = ConsentAnswer.Declined; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        t.Join();
        return (answer, files);
    }

    private sealed class ConsentForm : Form
    {
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
        private int _remaining;
        private readonly Label _countdown;

        /// <summary>Closed by its own countdown rather than by a person.</summary>
        public bool TimedOut { get; private set; }
        /// <summary>Closed because the technician stopped waiting.</summary>
        public bool WasWithdrawn { get; private set; }
        /// <summary>Somebody from outside (3.9): "Also let them copy files…" — unticked unless the person ticks it.</summary>
        private readonly CheckBox? _files;
        public bool FilesAllowed => _files?.Checked == true;

        public void Withdraw()
        {
            if (IsDisposed) return;
            WasWithdrawn = true; _timer.Stop();
            DialogResult = DialogResult.Abort; Close();
        }

        public ConsentForm(Branding brand, string tech, int timeoutSeconds, string visitorCompany = "")
        {
            _remaining = Seconds(timeoutSeconds);
            var t = Theme.FromBranding(brand, AppSurface.Customer);
            Image? logo = null;
            try { if (brand.HasLogo) logo = Image.FromFile(brand.LogoPath); } catch { }

            Text = $"{brand.CompanyName} — {brand.ProductName}";
            bool outsider = visitorCompany.Length > 0;
            // Taller for somebody from outside: it has more to say, and all of it must be read — and the
            // files box, measured from its own font (§30), on top of that.
            int filesH = outsider ? TextRenderer.MeasureText("Ag\nAg", t.Body, new Size(460, int.MaxValue), TextFormatFlags.WordBreak).Height + 12 : 0;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = true;
            TopMost = true; Font = t.Body; BackColor = t.Mist;
            Shown += (_, _) => NativeDark.Apply(this, t);   // dark title bar and scrollbars — see NativeDark

            var canvas = new CanvasPanel(t) { Dock = DockStyle.Fill, Padding = new Padding(18, 14, 18, 16) };
            Controls.Add(canvas);
            Controls.Add(new BrandHeader(t, logo, brand.CompanyName, brand.ProductName, 88));

            var card = new Card(t, 20) { Dock = DockStyle.Fill };
            canvas.Controls.Add(card);

            var buttons = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Color.Transparent };
            var allow = new RoundButton(t, BtnStyle.Success) { Text = "Allow", Dock = DockStyle.Right, Width = 150, Height = 46, DialogResult = DialogResult.OK };
            var deny = new RoundButton(t, BtnStyle.Danger) { Text = "Deny", Dock = DockStyle.Right, Width = 110, Height = 46, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(allow);
            buttons.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 10, BackColor = Color.Transparent });
            buttons.Controls.Add(deny);

            _countdown = new Label { Dock = DockStyle.Bottom, Height = 32, ForeColor = t.Muted, Font = t.Small, BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };

            var who = string.IsNullOrWhiteSpace(tech) ? "A technician" : tech;
            var detail = new Label
            {
                Dock = DockStyle.Fill, BackColor = Color.Transparent, ForeColor = t.Ink, Font = new Font("Segoe UI", 10f),
                UseMnemonic = false, AutoEllipsis = false,
                // Somebody from outside (3.9): who they are NOT is the first thing said, because the scam
                // this invites is somebody posing as the IT company whose logo is at the top of this window.
                Text = outsider
                    ? $"{who} is asking to connect to this computer with the code you made. They are NOT from {brand.CompanyName}.\n\n" +
                      "If you allow it, they can see your screen and use your mouse and keyboard until the session ends. " +
                      $"They cannot run anything, and cannot copy files unless you tick the box below. {brand.CompanyName} is told and can end it.\n\n" +
                      "If you did not give them a code, press Deny."
                    : $"{who} is requesting to connect to this computer.\n\n" +
                      "If you allow this, they will be able to see your screen, control your mouse and keyboard, " +
                      "transfer files and run maintenance tasks until the session ends."
            };
            var head = new Label
            {
                Dock = DockStyle.Top, Height = 82, BackColor = Color.Transparent, ForeColor = t.Ink, UseMnemonic = false,
                Font = new Font("Segoe UI", 15f, FontStyle.Bold), Text = outsider ? "Allow somebody from outside?" : "Allow a remote support connection?"
            };

            // The window is as tall as what it has to SAY (§30; fitprobe, 2026-10-06): a long guest's name cut
            // the outsider text 60 px short, and what went was its last line — "If you did not give them a
            // code, press Deny", the one there for the scam. Measured at the card's inner width, with the same
            // flags the labels draw with; never shorter than the 500 px it always was.
            const int inner = 540 - 36 - 40;
            int Need(string text, Font f) => TextRenderer.MeasureText(text, f, new Size(inner, int.MaxValue), TextFormatFlags.WordBreak).Height;
            head.Height = Math.Max(82, Need(head.Text, head.Font) + 16);
            int detailH = Need(detail.Text, detail.Font) + 12;
            ClientSize = new Size(540, Math.Max(500, 88 + 30 + 40 + head.Height + detailH + filesH + _countdown.Height + buttons.Height + 8));

            card.Controls.Add(detail);
            card.Controls.Add(head);
            if (outsider)
            {
                // Clint, 2026-10-06: "File transfer only if the customer ticks it". Off unless they tick it,
                // for this session only. Bottom-docked before the countdown, so it sits just above it.
                _files = new RoundCheck(t)
                {
                    Text = "Also let them copy files to and from this computer", Dock = DockStyle.Bottom, Height = filesH,
                    AutoSize = false, Checked = false, Font = t.Body, ForeColor = t.Ink, BackColor = Color.Transparent, UseMnemonic = false
                };
                card.Controls.Add(_files);
            }
            card.Controls.Add(_countdown);
            card.Controls.Add(buttons);

            AcceptButton = allow; CancelButton = deny;

            void tick()
            {
                _countdown.Text = $"Declining automatically in {_remaining} second{(_remaining == 1 ? "" : "s")}.";
                if (_remaining-- <= 0) { _timer.Stop(); TimedOut = true; DialogResult = DialogResult.Cancel; Close(); }
            }
            tick();
            _timer.Tick += (_, _) => { tick(); ComeForward.StayOnTop(this); };
            // In front and with the keyboard, even over the program they are working in (2026-10-16): TopMost
            // alone is taken off by Windows when a background program shows it — see ComeForward.
            Shown += (_, _) => { _timer.Start(); ComeForward.Now(this); try { allow.Focus(); } catch { } };
            FormClosed += (_, _) => _timer.Stop();
        }

        protected override void OnFormClosing(FormClosingEventArgs e) { _timer.Stop(); base.OnFormClosing(e); }
    }
}
