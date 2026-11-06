using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Technician;

/// <summary>
/// "A job has come into the billing queue" (Clint, 2026-10-05: "when something is assigned to
/// billing, all office administrators should be notified" — a badge and a pop-up). Bottom right of
/// the screen, on top, and it does NOT take the keyboard, like the incoming call alert. It stays until
/// it is opened or dismissed: it is there so that it is not missed. Several arriving at once show the
/// newest and say how many more — the queue itself is one click away.
///
/// <para>The server decides who sees it (Permissions.HearsOfBilling) and never sends one to the person
/// who put the job there; this only draws what arrives.</para>
/// </summary>
public sealed class BillingPopup : Form
{
    private readonly Theme _t;
    private readonly Label _title, _line, _more;
    private readonly List<BillingArrivalInfo> _waiting = new();
    private readonly int _headH;

    /// <summary>Open was pressed: the ticket to show. The dashboard opens the Tickets window on it.</summary>
    public event Action<int>? OpenTicket;

    public BillingPopup(Theme t)
    {
        _t = t;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        Text = "In the billing queue";
        BackColor = _t.Mist;
        Font = _t.Body;
        Shown += (_, _) => NativeDark.Apply(this, _t);

        // Every height from its font, never a number (§30): at 150% one line of 9.75 pt is not 20 px.
        const int width = 380, pad = 16;
        int inner = width - pad * 2;
        int Lines(Font f, int n) => TextRenderer.MeasureText(string.Join("\n", Enumerable.Repeat("Ag", n)), f,
            new Size(inner, int.MaxValue), TextFormatFlags.WordBreak).Height + 2;
        _headH = Lines(_t.BodyBold, 1) + 14;

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(pad, _headH + 8, pad, pad) };
        Controls.Add(body);
        _title = new Label { Dock = DockStyle.Top, Height = Lines(_t.BodyBold, 3), Font = _t.BodyBold, ForeColor = _t.Ink, BackColor = Color.Transparent, UseMnemonic = false };
        _line = new Label { Dock = DockStyle.Top, Height = Lines(_t.Body, 2), Font = _t.Body, ForeColor = _t.Muted, BackColor = Color.Transparent, UseMnemonic = false };
        _more = new Label { Dock = DockStyle.Top, Height = Lines(_t.Small, 1), Font = _t.Small, ForeColor = _t.Muted, BackColor = Color.Transparent, UseMnemonic = false };

        int buttonH = TextRenderer.MeasureText("Ag", _t.ButtonFont).Height + 14;
        var buttons = new Panel { Dock = DockStyle.Bottom, Height = buttonH, BackColor = Color.Transparent };
        var open = new RoundButton(_t, BtnStyle.Accent) { Text = "Open the ticket", Dock = DockStyle.Fill, Height = buttonH };
        open.Click += (_, _) =>
        {
            var newest = _waiting.LastOrDefault();
            Dismiss();
            if (newest != null) OpenTicket?.Invoke(newest.TicketId);
        };
        var dismiss = new RoundButton(_t, BtnStyle.Ghost) { Text = "Dismiss", Dock = DockStyle.Right, Height = buttonH,
            Width = TextRenderer.MeasureText("Dismiss", _t.ButtonFont).Width + 40 };
        dismiss.Click += (_, _) => Dismiss();
        buttons.Controls.Add(open);
        buttons.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 10, BackColor = Color.Transparent });
        buttons.Controls.Add(dismiss);

        // Top-docked: the LAST added sits highest, so add them bottom-up.
        body.Controls.Add(buttons);
        body.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Color.Transparent });
        body.Controls.Add(_more);
        body.Controls.Add(_line);
        body.Controls.Add(_title);

        ClientSize = new Size(width, body.Padding.Top + _title.Height + _line.Height + _more.Height + 10 + buttonH + body.Padding.Bottom);
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left && e.Y < _headH) DragFrom(); };
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>A job has just arrived. Shown at once, without taking the keyboard.</summary>
    public void Arrived(BillingArrivalInfo a, IWin32Window? near)
    {
        _waiting.RemoveAll(x => x.TicketId == a.TicketId);
        _waiting.Add(a);
        // Three lines hold about 120 characters; a longer subject is cut at a word and SAYS so, never clipped in silence.
        int cut = a.Subject.Length <= 110 ? -1 : a.Subject.LastIndexOf(' ', 110);
        var subject = a.Subject.Length <= 110 ? a.Subject : a.Subject[..(cut > 40 ? cut : 110)] + "…";
        _title.Text = $"#{a.Number} — {subject}";
        _line.Text = $"{a.Stage} · from {a.By}";
        int others = _waiting.Count - 1;
        _more.Text = others > 0 ? $"+ {others} more since — they are all in the billing queue" : "";
        if (!Visible)
        {
            var screen = near is Control c && c.IsHandleCreated ? Screen.FromControl(c) : Screen.PrimaryScreen;
            var wa = (screen ?? Screen.PrimaryScreen)!.WorkingArea;
            Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
            Show();
        }
        try { SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); } catch { }
        Invalidate();
    }

    private void Dismiss() { _waiting.Clear(); Hide(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        // Amber, as the portal's: somebody's work is waiting, which is not the same as an error.
        var amber = Color.FromArgb(0xB4, 0x53, 0x09);
        using (var head = new SolidBrush(amber)) g.FillRectangle(head, 0, 0, Width, _headH);
        TextRenderer.DrawText(g, "In the billing queue", _t.BodyBold, new Rectangle(16, 0, Width - 32, _headH), Color.White,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        using var pen = new Pen(amber, 2f);
        g.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Never really closed — the dashboard owns it.
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Dismiss(); }
        base.OnFormClosing(e);
    }

    private void DragFrom()
    {
        try { ReleaseCapture(); SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0); } catch { }
    }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    private const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 0x2;
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
}
