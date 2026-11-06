namespace RemoteSupport.Host;

/// <summary>
/// Keeps the calling thread attached to the desktop the user is actually looking at.
///
/// Windows does not keep a session on one desktop. "Default" is the normal one; a UAC prompt,
/// the lock screen and Ctrl+Alt+Del all switch to "Winlogon", and a secure screensaver switches
/// to "Screen-saver". Capture and input injection are both per-thread-desktop: a thread pinned
/// to Default keeps returning the last picture it could see and its SendInput goes nowhere, with
/// no error, the instant the user hits a UAC prompt.
///
/// Only a SYSTEM process may open the Winlogon desktop. Running as an elevated user is not
/// enough, which is why the agent launches the host as SYSTEM (see docs/HANDOVER.md 6d).
///
/// Each thread that captures or injects owns one of these and calls <see cref="Sync"/> before
/// doing work. Cheap: it only re-attaches when the input desktop has actually changed.
/// </summary>
internal sealed class DesktopFollower : IDisposable
{
    private IntPtr _attached = IntPtr.Zero;
    private string _attachedName = "";
    private DateTime _nextCheck = DateTime.MinValue;
    private readonly TimeSpan _interval;
    private readonly Action<string>? _onChange;

    public DesktopFollower(Action<string>? onChange = null, int checkMs = 300)
    { _onChange = onChange; _interval = TimeSpan.FromMilliseconds(checkMs); }

    /// <summary>The desktop this thread is currently attached to, for diagnostics.</summary>
    public string CurrentName => _attachedName;

    /// <summary>
    /// Attach this thread to the current input desktop if it has changed. Returns true when the
    /// thread is attached to something usable.
    /// </summary>
    public bool Sync(bool force = false)
    {
        if (!force && DateTime.UtcNow < _nextCheck) return _attached != IntPtr.Zero;
        _nextCheck = DateTime.UtcNow + _interval;

        IntPtr input = Native.OpenInputDesktop(0, false, Native.DESKTOP_ALL);
        if (input == IntPtr.Zero)
        {
            // Typically means we lack rights for the desktop that currently has input — a UAC
            // prompt while running as a plain user. Stay where we are rather than detaching.
            return _attached != IntPtr.Zero;
        }

        var name = Native.DesktopName(input);
        if (name == _attachedName && _attached != IntPtr.Zero)
        {
            Native.CloseDesktop(input);
            return true;
        }

        if (!Native.SetThreadDesktop(input))
        {
            // SetThreadDesktop refuses if this thread owns any window or hook. Nothing to do but
            // keep the old attachment.
            Native.CloseDesktop(input);
            return _attached != IntPtr.Zero;
        }

        var old = _attached;
        _attached = input;
        var previous = _attachedName;
        _attachedName = name;
        if (old != IntPtr.Zero) Native.CloseDesktop(old);
        if (previous != name) _onChange?.Invoke(name);
        return true;
    }

    public void Dispose()
    {
        if (_attached != IntPtr.Zero) { Native.CloseDesktop(_attached); _attached = IntPtr.Zero; }
    }
}
