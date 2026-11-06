using System.Drawing;
using System.Windows.Forms;
using RemoteSupport.UI;

namespace RemoteSupport.Client;

/// <summary>
/// While the customer gets the screen ready for a new picture: one line at the top of the screen, and the button that
/// takes it (2026-10-13, see <see cref="ScreenSnap"/>).
///
/// The request window and the tray window are out of the way while this is up, so the person can click their taskbar
/// and bring back what they want us to see — as long as they like, no countdown to beat. TopMost, so the window they
/// bring back does not cover it; not in the taskbar, which is where they are about to click. It is closed before the
/// picture is taken, so it is never in it.
/// </summary>
public sealed class PictureBar : Form
{
    private readonly Theme _t;

    public PictureBar(Theme t, Screen on)
    {
        _t = t;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Font = t.Body;
        BackColor = t.CardBg;
        Text = "Take a new picture";
        Padding = new Padding(16, 12, 12, 12);

        var words = new Label
        {
            Text = "Bring up what you want us to see, then press Take the picture.",
            AutoSize = false, Font = t.BodyBold, ForeColor = t.Ink, BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false
        };
        // Every size from the font it draws with (§30 of the handover).
        int btnH = TextRenderer.MeasureText("Ag", t.ButtonFont).Height + 20;
        var take = new RoundButton(t, BtnStyle.Accent) { Text = "Take the picture", Size = new Size(TextRenderer.MeasureText("Take the picture", t.ButtonFont).Width + 48, btnH) };
        var cancel = new RoundButton(t, BtnStyle.Ghost) { Text = "Cancel", Size = new Size(TextRenderer.MeasureText("Cancel", t.ButtonFont).Width + 48, btnH), DialogResult = DialogResult.Cancel };
        take.Click += (_, _) => DialogResult = DialogResult.OK;
        Controls.Add(words); Controls.Add(take); Controls.Add(cancel);
        AcceptButton = take; CancelButton = cancel;

        var area = on.WorkingArea;
        int buttonsW = take.Width + 10 + cancel.Width;
        int roomForWords = Math.Max(120, area.Width - 40 - Padding.Horizontal - 16 - buttonsW);
        var need = TextRenderer.MeasureText(words.Text, words.Font, new Size(roomForWords, int.MaxValue), TextFormatFlags.WordBreak);
        int wordsW = Math.Min(need.Width + 4, roomForWords);
        int innerH = Math.Max(btnH, need.Height + 4);
        ClientSize = new Size(Padding.Horizontal + wordsW + 16 + buttonsW, Padding.Vertical + innerH);
        words.SetBounds(Padding.Left, Padding.Top, wordsW, innerH);
        take.Location = new Point(words.Right + 16, Padding.Top + (innerH - btnH) / 2);
        cancel.Location = new Point(take.Right + 10, take.Top);
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + 12);
    }

    /// <summary>A border in the company's colour: a borderless window on a white Explorer window is otherwise just more
    /// white.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(_t.Accent, 2);
        e.Graphics.DrawRectangle(pen, 1, 1, ClientSize.Width - 2, ClientSize.Height - 2);
    }
}
