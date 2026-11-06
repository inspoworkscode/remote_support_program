using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodecBench;

/// <summary>
/// Runs a <see cref="Scene"/> on its own STA thread with its own message loop, so the scene keeps
/// repainting while the capture thread measures it. The bench's main thread does neither: it just
/// waits, which is the only arrangement where the numbers belong to the capturer.
///
/// It also checks that the scene is genuinely the thing on screen, and that check is not
/// defensive decoration. Without it the bench reported an idle screen sending 100 KB/s and a
/// capture path that looked broken; the scene had simply failed to come to the front, and the
/// capturer was faithfully recording a live application behind it. A benchmark that does not know
/// what it is photographing produces numbers that look precise and mean nothing.
/// </summary>
public sealed class SceneHost : IDisposable
{
    private readonly Thread _thread;
    private Scene? _form;
    private readonly ManualResetEventSlim _up = new();

    /// <summary>True when the scene was verified to be the window occupying the monitor.</summary>
    public bool Covering { get; private set; }

    private SceneHost(SceneKind kind, Rectangle bounds)
    {
        _thread = new Thread(() =>
        {
            _form = new Scene(kind, bounds);
            _form.Shown += (_, _) => _up.Set();
            Application.Run(_form);
        });
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.IsBackground = true;
        _thread.Start();
        if (!_up.Wait(5000)) { Console.WriteLine("  WARNING: the scene window never appeared."); return; }
        Covering = TakeTheScreen(bounds);
        if (!Covering)
            Console.WriteLine($"  WARNING: the scene is not the window on screen - {_lastWinner} is. This pass would measure the real desktop.");
    }

    public static SceneHost Show(SceneKind kind, Rectangle bounds) => new(kind, bounds);

    /// <summary>Stops the animation, leaving the last frame on screen. See <see cref="Scene.Freeze"/>.</summary>
    public void Freeze() { try { _form?.Freeze(); } catch { } }

    /// <summary>The Desk page's blinking text cursor and its window opening. See <see cref="Scene.Blink"/>.</summary>
    public void Blink(bool on) { try { _form?.Blink(on); } catch { } }
    public void Dialog(bool on) { try { _form?.Dialog(on); } catch { } }
    public Rectangle CaretArea => _form?.CaretArea ?? Rectangle.Empty;

    /// <summary>Changes what this one window paints, and re-checks it is still the one on screen.</summary>
    public void SetKind(SceneKind kind, Rectangle bounds)
    {
        try { _form?.SetKind(kind); } catch { }
        Covering = TakeTheScreen(bounds);
        if (!Covering)
            Console.WriteLine($"  WARNING: the scene is no longer the window on screen - {_lastWinner} is. This pass would measure the real desktop.");
    }

    /// <summary>
    /// Pushes the scene to the front and then PROVES it is there, by asking Windows which window
    /// owns the pixel at the middle of the monitor. Being TopMost, being shown and being activated
    /// are all things a window can be while something else is still in front of it.
    /// </summary>
    private bool TakeTheScreen(Rectangle bounds)
    {
        var mid = new POINT { X = bounds.X + bounds.Width / 2, Y = bounds.Y + bounds.Height / 2 };
        uint mine = (uint)Environment.ProcessId;
        for (int attempt = 0; attempt < 12; attempt++)
        {
            IntPtr h = _form?.Handle ?? IntPtr.Zero;
            if (h != IntPtr.Zero)
            {
                SetWindowPos(h, HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_SHOWWINDOW);
                SetForegroundWindow(h);
            }
            Thread.Sleep(150);
            IntPtr at = WindowFromPoint(mid);
            if (at != IntPtr.Zero) { GetWindowThreadProcessId(at, out uint pid); if (pid == mine) return true; }
            _lastWinner = Describe(at);
        }
        return false;
    }

    private string _lastWinner = "";
    /// <summary>What is in front instead, so the warning names it rather than shrugging.</summary>
    public string Winner => _lastWinner;

    private static string Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "nothing";
        var cls = new System.Text.StringBuilder(256);
        GetClassName(hwnd, cls, cls.Capacity);
        GetWindowThreadProcessId(hwnd, out uint pid);
        string proc;
        try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { proc = $"pid {pid}"; }
        return $"{proc} (class {cls})";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder buf, int max);

    public void Dispose()
    {
        try { _form?.Invoke(() => _form.Close()); } catch { }
        _thread.Join(3000);
        _up.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_SHOWWINDOW = 0x0040;
}
