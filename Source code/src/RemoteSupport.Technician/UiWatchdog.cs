namespace RemoteSupport.Technician;

/// <summary>
/// Notices when the console's UI thread stops answering, and says what it was doing when it
/// stopped.
///
/// WHY THIS EXISTS. On 2026-09-18 the session window froze — "I hover over the taskbar and can see
/// the session, but clicking it just makes a noise", which is Windows' way of saying a window is
/// not pumping messages. A freeze leaves nothing behind: no crash, no exception, no last line in
/// the trace, because the thread that would have written one is the thread that is stuck. It was
/// diagnosed three times from the outside, and two of those three were wrong — a walk moved off
/// the UI thread while the same walk stayed on it somewhere else, and then the guess after that.
///
/// Guessing is the wrong tool and this is the right one. A heartbeat on the UI thread, a watcher
/// thread that is never blocked by it, and a note of what the UI thread said it was starting. When
/// the heartbeat stops, the note IS the answer, and it costs one timer and one sleeping thread.
///
/// It is always on, for the same reason HostTrace is: the fault appears on somebody else's machine,
/// in the middle of a support call, and asking them to reproduce it under a debugger is not a plan.
/// </summary>
internal static class UiWatchdog
{
    /// <summary>Bumped on the UI thread. The only thing the watcher reads to decide it is alive.</summary>
    private static long _beat;

    /// <summary>What the UI thread last said it was starting. Written on the UI thread, read on
    /// the watcher's — a torn read of a reference is not possible, and a stale one is still the
    /// answer to the question being asked.</summary>
    private static volatile string _doing = "(nothing in particular)";

    private static System.Windows.Forms.Timer? _tick;
    private static Thread? _watch;
    private static volatile bool _stop;

    /// <summary>
    /// How long the UI thread may be busy before it is called a freeze.
    ///
    /// A transfer, a clipboard another process is holding open, or a large paint all take a
    /// moment, and calling those a freeze would fill the trace with noise nobody reads. Four
    /// seconds is well past anything legitimate and well short of a technician giving up.
    /// </summary>
    private static readonly TimeSpan Stuck = TimeSpan.FromSeconds(4);

    // ---- the third way a window refuses to open, after frozen and modal ------------------------
    // A window that is DISABLED cannot be activated, so the taskbar beeps instead of restoring it
    // — and it looks identical to both of the others from the outside. Windows disables a window
    // while a modal dialog it owns is open: a MessageBox, or an OpenFileDialog that has opened
    // behind the session window or off the edge of the screen. The message loop keeps turning
    // perfectly, so the heartbeat is healthy and nothing here noticed it until 2026-09-18.
    //
    // IsWindowEnabled against a handle captured once on the UI thread is a plain Win32 call and
    // safe from the watcher thread. It is the definitive test for "something modal has it".

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetLastActivePopup(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int count);

    private static IntPtr _window;
    private static DateTime _disabledSince = DateTime.MinValue;
    private static bool _saidDisabled;

    private static void CheckDisabled()
    {
        if (_window == IntPtr.Zero) return;
        bool enabled;
        try { enabled = IsWindowEnabled(_window); } catch { return; }

        if (enabled)
        {
            if (_saidDisabled)
                Trace.Write($"watchdog: the window is usable again after {(DateTime.UtcNow - _disabledSince).TotalSeconds:0.0}s");
            _disabledSince = DateTime.MinValue;
            _saidDisabled = false;
            return;
        }

        if (_disabledSince == DateTime.MinValue) { _disabledSince = DateTime.UtcNow; return; }
        var held = DateTime.UtcNow - _disabledSince;
        if (held < TimeSpan.FromSeconds(4) || _saidDisabled) return;

        _saidDisabled = true;
        // Name the dialog if Windows will tell us which one it is. "Open" or "Restart the
        // machine" is the whole answer; an empty title still says a dialog is the reason.
        var who = "(unnamed)";
        try
        {
            var popup = GetLastActivePopup(_window);
            if (popup != IntPtr.Zero && popup != _window)
            {
                var sb = new System.Text.StringBuilder(256);
                if (GetWindowTextW(popup, sb, sb.Capacity) > 0) who = sb.ToString();
            }
        }
        catch { }

        Trace.Write($"watchdog: THE SESSION WINDOW IS DISABLED and has been for {held.TotalSeconds:0}s — " +
                    $"a modal dialog owns it: '{who}'. This is why the taskbar beeps instead of restoring it.");
        Trace.Write("watchdog: look for a dialog behind the window or off the edge of the screen; Alt+Tab or Escape will clear it");
    }

    /// <summary>
    /// The window to watch for being disabled. Captured on the UI thread once the handle exists,
    /// because a Form's Handle may not be touched from anywhere else — the raw HWND may.
    /// </summary>
    public static void SetWindow(IntPtr window) => _window = window;

    public static void Start(IntPtr window = default)
    {
        if (window != IntPtr.Zero) _window = window;
        if (_tick != null) return;

        // The heartbeat runs on the UI thread by construction: a WinForms timer ticks through the
        // message loop, so if the loop is blocked the beat stops. That is the whole mechanism.
        _tick = new System.Windows.Forms.Timer { Interval = 500 };
        _tick.Tick += (_, _) => Interlocked.Increment(ref _beat);
        _tick.Start();

        _watch = new Thread(Watch) { IsBackground = true, Name = "rs-ui-watchdog" };
        _watch.Start();
        Trace.Write("watchdog: watching the UI thread");
    }

    public static void Stop()
    {
        _stop = true;
        try { _tick?.Stop(); } catch { }
        _tick = null;
    }

    private static void Watch()
    {
        long last = Interlocked.Read(ref _beat);
        var lastMoved = DateTime.UtcNow;
        bool reported = false;
        var frozenSince = DateTime.UtcNow;

        while (!_stop)
        {
            Thread.Sleep(1000);
            // Checked whatever the heartbeat says, because a nested loop keeps the heartbeat
            // perfectly healthy while the window cannot be used.
            CheckModal();
            CheckDisabled();
            long now = Interlocked.Read(ref _beat);

            if (now != last)
            {
                if (reported)
                {
                    // Worth its own line. A freeze that ends is a slow operation, and a freeze
                    // that never ends is a deadlock; the two need different fixes and this is the
                    // only place the difference is visible.
                    Trace.Write($"watchdog: the UI thread came back after {(DateTime.UtcNow - frozenSince).TotalSeconds:0.0}s — it had been in: {_doing}");
                    reported = false;
                }
                last = now;
                lastMoved = DateTime.UtcNow;
                continue;
            }

            var stuckFor = DateTime.UtcNow - lastMoved;
            if (stuckFor < Stuck) continue;

            if (!reported)
            {
                frozenSince = lastMoved;
                reported = true;
                Trace.Write($"watchdog: THE UI THREAD HAS NOT ANSWERED FOR {stuckFor.TotalSeconds:0}s — it is in: {_doing}");
                Trace.Write("watchdog: the window will not restore from the taskbar and clicking it will beep until this returns");
            }
            else if ((int)stuckFor.TotalSeconds % 10 == 0)
            {
                Trace.Write($"watchdog: still stuck after {stuckFor.TotalSeconds:0}s in: {_doing}");
            }
        }
    }

    /// <summary>
    /// Marks a stretch of UI-thread work, so a freeze inside it has a name.
    ///
    /// Wrap anything that touches the clipboard, the file system or the shell. Those are the three
    /// that block for reasons outside this process's control, and they are what every freeze in
    /// this window has turned out to be.
    /// </summary>
    public static IDisposable Work(string what) => new Scope(what);

    // ---- the freeze the heartbeat cannot see ---------------------------------------------------
    // A window can be unusable while still pumping messages, and then everything above this line
    // reports it as healthy. Two things in this console do exactly that: the nested
    // Application.DoEvents loop that holds a drag open while its files are fetched, and
    // DoDragDrop, which runs a modal loop of its own with the mouse captured.
    //
    // From the outside that state is indistinguishable from a freeze — the taskbar beeps instead
    // of restoring the window, because a modal loop swallows the activation — but Responding
    // stays True and the heartbeat keeps ticking, so on 2026-09-18 the watchdog said nothing
    // while Clint reported the window as dead for the fourth time.
    //
    // So a nested loop declares itself, with a clock. Nothing else can tell the difference.

    private static volatile string? _modalWhat;
    private static long _modalSince;

    /// <summary>How long a nested loop may run before it is worth saying so. A drag that is
    /// waiting for a fetch is legitimate; one still waiting a minute later is not.</summary>
    private static readonly TimeSpan ModalTooLong = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Marks a nested message loop or a modal shell call — anything that keeps pumping while the
    /// window cannot actually be used.
    /// </summary>
    public static IDisposable Modal(string what)
    {
        _modalWhat = what;
        Interlocked.Exchange(ref _modalSince, DateTime.UtcNow.Ticks);
        Trace.Write($"watchdog: entering {what} — the window will pump but will not respond to clicks until it ends");
        return new ModalScope(what);
    }

    private sealed class ModalScope : IDisposable
    {
        private readonly string _what;
        private readonly DateTime _started = DateTime.UtcNow;
        public ModalScope(string what) => _what = what;
        public void Dispose()
        {
            _modalWhat = null;
            var took = DateTime.UtcNow - _started;
            Trace.Write(took > TimeSpan.FromSeconds(2)
                ? $"watchdog: left {_what} after {took.TotalSeconds:0.0}s"
                : $"watchdog: left {_what}");
        }
    }

    private static void CheckModal()
    {
        var what = _modalWhat;
        if (what == null) return;
        var since = new DateTime(Interlocked.Read(ref _modalSince), DateTimeKind.Utc);
        var held = DateTime.UtcNow - since;
        if (held < ModalTooLong) return;
        // Once every ten seconds, not every second: this is a report about something that is
        // still happening, and a trace nobody can read is a trace nobody reads.
        if ((int)held.TotalSeconds % 10 != 0) return;
        Trace.Write($"watchdog: STILL INSIDE {what} after {held.TotalSeconds:0}s — " +
                    "the window is pumping but clicking it will beep, and this is why");
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _previous;
        public Scope(string what) { _previous = _doing; _doing = what; }
        public void Dispose() => _doing = _previous;
    }
}
