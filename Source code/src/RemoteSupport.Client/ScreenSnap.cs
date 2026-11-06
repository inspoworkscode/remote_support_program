using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace RemoteSupport.Client;

/// <summary>
/// A picture of the customer's screen for a support request (Clint, 2026-10-09: "a screenshot option when a client
/// requests support would be a great feature").
///
/// Taken BEFORE the request window opens, so it shows the problem and not our form; the tray window, when it is up,
/// is made invisible for the moment of the capture for the same reason. Every monitor, as one picture, made no wider
/// than <see cref="MaxWidth"/> and saved as a JPEG — a few hundred KB, sent with the request and kept on the ticket.
/// The tray app runs as the person, in their session, so it sees exactly what they see.
///
/// 2026-10-13, Clint: "When doing a screenshot, it does it of the desktop only. If a client has explorer open with a
/// folder list for example, it does not screenshot that." Measured first: the capture itself takes an open Explorer
/// window whole. What it cannot take is a window that is not on the screen — and the app's shortcut is ON THE
/// DESKTOP, so the usual way in is to clear the screen (Show desktop, Win+D) and double-click it, which minimises
/// exactly the window the customer wants help with. So the picture says so (<see cref="OnlyDesktop"/>), and the
/// request window can take it again once they have brought that window back (<see cref="PictureBar"/>).
/// </summary>
public sealed class ScreenSnap : IDisposable
{
    /// <summary>Wide enough to read an error message on, small enough to travel with the request.</summary>
    public const int MaxWidth = 1920;
    private const long JpegQuality = 80;

    public byte[] Jpeg { get; }
    /// <summary>The same picture, for the request window to show what will be sent.</summary>
    public Image Preview { get; }
    /// <summary>
    /// Nothing but the desktop was on the screen when it was taken — every program minimised or none open. Worth
    /// saying beside the preview: it is almost never what the person meant to send.
    /// </summary>
    public bool OnlyDesktop { get; }

    private ScreenSnap(byte[] jpeg, Image preview, bool onlyDesktop) { Jpeg = jpeg; Preview = preview; OnlyDesktop = onlyDesktop; }

    /// <summary>
    /// Null when there is nothing to take — a locked or secure desktop, or anything else that refuses a capture. The
    /// request goes without one; it never stops anybody asking for help.
    /// </summary>
    /// <param name="hide">Our own windows that are on screen: invisible for the capture, then back as they were.</param>
    public static ScreenSnap? Take(params Form?[] hide)
    {
        // Take(null) is a null ARRAY, not an array of one null: both mean "nothing of ours to hide".
        var hidden = (hide ?? Array.Empty<Form?>()).Where(f => f is { IsDisposed: false, Visible: true }).Select(f => (Form: f!, Was: f!.Opacity)).ToList();
        try
        {
            if (hidden.Count > 0)
            {
                foreach (var (f, _) in hidden) { f.Opacity = 0; f.Update(); }
                // One composed frame for the windows to actually leave the screen.
                Thread.Sleep(150);
            }
            bool onlyDesktop = !AnyProgramShowing();
            var area = SystemInformation.VirtualScreen;
            if (area.Width <= 0 || area.Height <= 0) return null;
            using var full = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(full))
                g.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size, CopyPixelOperation.SourceCopy);

            double scale = Math.Min(1.0, (double)MaxWidth / area.Width);
            var size = new Size(Math.Max(1, (int)(area.Width * scale)), Math.Max(1, (int)(area.Height * scale)));
            var small = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(full, new Rectangle(Point.Empty, size));
            }
            return new ScreenSnap(Encode(small), small, onlyDesktop);
        }
        catch { return null; }
        finally { foreach (var (f, was) in hidden) try { f.Opacity = was; } catch { } }
    }

    private static byte[] Encode(Image img)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var p = new EncoderParameters(1);
        p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, JpegQuality);
        using var ms = new MemoryStream();
        img.Save(ms, codec, p);
        return ms.ToArray();
    }

    public void Dispose() => Preview.Dispose();

    // ---- is anything but the desktop on the screen? -------------------------------------------

    /// <summary>
    /// The windows a person would call "open": the ones the taskbar shows, not minimised, not cloaked (Windows keeps
    /// suspended Store apps and other virtual desktops' windows "visible" but cloaked), not ours. A wrong answer here
    /// only changes a sentence beside the preview, so it errs towards "something is showing".
    /// </summary>
    public static bool AnyProgramShowing() => ProgramsShowing().Any();

    /// <summary>The titles behind <see cref="AnyProgramShowing"/>, for a harness to print.</summary>
    public static List<string> ProgramsShowing()
    {
        var found = new List<string>();
        try
        {
            uint me = (uint)Environment.ProcessId;
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h) || IsIconic(h)) return true;
                GetWindowThreadProcessId(h, out uint pid);
                if (pid == me) return true;
                long ex = (long)GetWindowLongPtr(h, GWL_EXSTYLE);
                bool appWindow = (ex & WS_EX_APPWINDOW) != 0;
                if ((ex & WS_EX_TOOLWINDOW) != 0 && !appWindow) return true;
                if (GetWindow(h, GW_OWNER) != IntPtr.Zero && !appWindow) return true;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                var cls = new StringBuilder(64);
                GetClassName(h, cls, cls.Capacity);
                if (ShellClasses.Contains(cls.ToString())) return true;
                int len = GetWindowTextLength(h);
                if (len == 0) return true;
                if (!GetWindowRect(h, out var r) || r.Right - r.Left < 50 || r.Bottom - r.Top < 50) return true;
                var title = new StringBuilder(len + 1);
                GetWindowText(h, title, title.Capacity);
                found.Add($"{title} [{cls}]");
                return true;
            }, IntPtr.Zero);
        }
        catch { found.Add("(could not tell)"); }   // unknown counts as showing: say nothing rather than something wrong
        return found;
    }

    /// <summary>The desktop and the taskbar themselves.</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
        { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
    private const uint GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
}
