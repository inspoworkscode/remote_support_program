using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;

namespace RemoteSupport.UI;

/// <summary>
/// Where a window was last put, remembered between runs.
///
/// Stage 3.5.5. Both sides of the product now have windows that float rather than dock — the
/// technician's tools (3.5.4) and the customer's chat popup — and a floating window that forgets
/// where it was put is worse than a docked one: the person moves it out of their way, and it
/// comes back to exactly the place they moved it away from. The customer's popup had the sharper
/// version of this, because it was pinned to the bottom right and could sit on top of a desktop
/// icon they had just been asked to double-click.
///
/// It lives in RemoteSupport.UI because both the console and the client app need it and neither
/// should own it. It is per user (`%LOCALAPPDATA%`), which is right for both: a technician's
/// layout is theirs, and the customer's machine may have several people logging into it.
///
/// **Nothing here ever throws.** A remembered position is a convenience; a roaming profile, a
/// read-only disk or a file somebody edited by hand must cost the person a window in the default
/// place, never a window that will not open.
/// </summary>
public static class WindowPlacement
{
    private static readonly object Lock = new();
    private static Dictionary<string, int[]>? _cache;

    private static string Path_ => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RemoteSupport", "windows.json");

    /// <summary>
    /// Where this window was last left — or null if it has never been put anywhere, or if where
    /// it was put is no longer somewhere it can be reached.
    ///
    /// The second half is the one that matters and is easy to leave out. A technician who docks a
    /// laptop, puts the chat window on the second monitor and then works from a coffee shop has a
    /// remembered position on a screen that does not exist; restoring it faithfully would open a
    /// window nobody can see, click or close, and the fault would present as "chat has stopped
    /// working". <see cref="IsReachable"/> is what refuses that.
    /// </summary>
    public static Rectangle? Recall(string name)
    {
        lock (Lock)
        {
            try
            {
                Load();
                if (_cache == null || !_cache.TryGetValue(name, out var v) || v.Length != 4) return null;
                var r = new Rectangle(v[0], v[1], v[2], v[3]);
                if (r.Width < 40 || r.Height < 30) return null;
                return IsReachable(r) ? r : null;
            }
            catch { return null; }
        }
    }

    /// <summary>Records where a window has been put. Called when a move or resize FINISHES, not
    /// while it is happening — a drag raises a move for every pixel, and writing a file for each
    /// of them would make dragging the window stutter.</summary>
    public static void Remember(string name, Rectangle bounds)
    {
        lock (Lock)
        {
            try
            {
                Load();
                _cache ??= new Dictionary<string, int[]>();
                _cache[name] = new[] { bounds.X, bounds.Y, bounds.Width, bounds.Height };
                var dir = System.IO.Path.GetDirectoryName(Path_)!;
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path_, JsonSerializer.Serialize(_cache));
            }
            catch { }
        }
    }

    /// <summary>
    /// Is enough of this window on a screen for somebody to use it?
    ///
    /// "Enough" is a grabbable slice of its TOP edge, not merely one pixel of overlap. A window
    /// showing its bottom corner cannot be dragged back into view, so by this test it is not
    /// reachable — which is the honest answer, because the person cannot reach it.
    /// </summary>
    public static bool IsReachable(Rectangle bounds)
    {
        var grab = new Rectangle(bounds.X, bounds.Y, Math.Min(bounds.Width, 120), Math.Min(bounds.Height, 32));
        foreach (var s in Screen.AllScreens)
            if (s.WorkingArea.IntersectsWith(grab)) return true;
        return false;
    }

    /// <summary>
    /// Nudges a window wholly onto the screen it is mostly on. Used after the screens change —
    /// a monitor unplugged, a resolution changed, a laptop undocked — where the right answer is
    /// to keep the window where the person put it as nearly as possible rather than to throw
    /// their choice away and go back to the corner.
    /// </summary>
    public static Rectangle ClampToScreen(Rectangle bounds)
    {
        var screen = Screen.FromRectangle(bounds).WorkingArea;
        int x = Math.Max(screen.Left, Math.Min(bounds.X, screen.Right - bounds.Width));
        int y = Math.Max(screen.Top, Math.Min(bounds.Y, screen.Bottom - bounds.Height));
        return new Rectangle(x, y, bounds.Width, bounds.Height);
    }

    /// <summary>Forgets one window's position. Nothing in the product calls it yet; it is here so
    /// that "put it back where it started" has somewhere obvious to live when it is asked for.</summary>
    public static void Forget(string name)
    {
        lock (Lock)
        {
            try
            {
                Load();
                if (_cache == null || !_cache.Remove(name)) return;
                File.WriteAllText(Path_, JsonSerializer.Serialize(_cache));
            }
            catch { }
        }
    }

    private static void Load()
    {
        if (_cache != null) return;
        try
        {
            _cache = File.Exists(Path_)
                ? JsonSerializer.Deserialize<Dictionary<string, int[]>>(File.ReadAllText(Path_)) ?? new()
                : new();
        }
        catch { _cache = new(); }
    }
}
