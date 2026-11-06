using System.Drawing;
using System.Windows.Forms;
using RemoteSupport.UI;

namespace RemoteSupport.Technician;

/// <summary>
/// One tool — Chat, Files, Scripts, Sound or Info — in a window of its own.
///
/// Stage 3.5.4, and the reason it is a window rather than a tab is a measurement rather than a
/// taste: the side panel these came out of was 400 px wide and was 400 px wide whether or not
/// anybody was using it, in every viewing mode, so popping the screen out or going full screen —
/// the two things that exist to give the picture more room — gave it less. A window costs nothing
/// until it is opened, can be moved, can be resized, and can be put on a second monitor.
///
/// It is owned by the session window (<c>Show(owner)</c>), which buys three things that a docked
/// panel could not have: it stays above the picture including in full screen, it is a sibling of
/// the popped-out screen window rather than something buried under it, and it goes away when the
/// session does. It is deliberately NOT in the taskbar: five of these per session, times three
/// sessions, is a taskbar nobody can read.
///
/// The page inside it is the same <see cref="Panel"/> the side panel used to hold, unchanged.
/// Closing this window hides it and keeps the page — see the FormClosing handler in
/// <c>SessionForm.AddTool</c>, and the reason: what is in these pages is the session's history.
/// </summary>
internal sealed class ToolWindow : Form
{
    /// <summary>
    /// What this window's position is remembered under, across runs of the console.
    ///
    /// Per TOOL and not per customer machine: a technician who puts Chat on their second monitor
    /// means Chat, not Chat-when-helping-this-person. Remembering it per machine would put the
    /// window somewhere different on the second call of the morning, which is the surprise this
    /// is meant to remove rather than add.
    /// </summary>
    private readonly string _placementKey;

    public ToolWindow(Theme t, string title, Panel page, Size size, string placementKey)
    {
        _placementKey = placementKey;
        Text = title;
        ClientSize = size;
        // Small enough to tuck into a corner, large enough that the page inside is still usable.
        // Below this the Files list header and the chat input row start clipping each other.
        MinimumSize = new Size(340, 280);
        StartPosition = FormStartPosition.Manual;   // SessionForm.PlaceTool decides, once
        ShowInTaskbar = false;
        BackColor = t.Mist;
        Font = t.Body;
        KeyPreview = false;

        var host = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(12) };
        var card = new Card(t, 12) { Dock = DockStyle.Fill };
        card.Controls.Add(page);
        host.Controls.Add(card);
        Controls.Add(host);

        // Dark title bar and scrollbars, the same as every other window in this app. On Shown
        // rather than in the constructor because it needs a handle to talk to.
        Shown += (_, _) => NativeDark.Apply(this, t);

        // Stage 3.5.5: it stays where it is put, between sessions and between runs. ResizeEnd is
        // raised at the end of a move AND at the end of a resize, which is both of the things
        // worth recording and neither of the hundred intermediate positions a drag passes
        // through. Size is kept as well as position: somebody who makes the Files window tall
        // enough to read a folder in wants it that tall next time too.
        ResizeEnd += (_, _) =>
        {
            if (WindowState != FormWindowState.Normal) return;   // a maximised size is not a chosen one
            WindowPlacement.Remember(_placementKey, Bounds);
        };
    }

    /// <summary>
    /// Restores where this window was last left, and says whether it managed to.
    ///
    /// False means the caller should place it itself — either it has never been opened, or where
    /// it was left is on a monitor that is not here any more. The second is not hypothetical for
    /// a technician with a laptop: restoring that position faithfully would open a window they
    /// cannot see, click or close, and it would present as the tool being broken.
    /// </summary>
    public bool RestorePlacement()
    {
        if (WindowPlacement.Recall(_placementKey) is not { } r) return false;
        // Never smaller than the page inside it can survive, whatever is in the file.
        var size = new Size(Math.Max(r.Width, MinimumSize.Width), Math.Max(r.Height, MinimumSize.Height));
        Bounds = WindowPlacement.ClampToScreen(new Rectangle(r.Location, size));
        return true;
    }

    /// <summary>
    /// Opening a tool must never take the keyboard.
    ///
    /// The session brings windows up by itself — the chat window when the customer says
    /// something, the Scripts window when a file could not be written — and the technician may be
    /// typing on the customer's machine at that moment. A window that activates itself swallows
    /// the next keystrokes, which is a worse fault than the one it was reporting. Choosing a tool
    /// from the menu calls Activate() explicitly afterwards, so an asked-for window still comes
    /// to the front with the focus.
    /// </summary>
    protected override bool ShowWithoutActivation => true;
}
