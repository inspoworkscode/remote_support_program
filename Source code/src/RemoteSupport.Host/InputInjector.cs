using RemoteSupport.Shared;

namespace RemoteSupport.Host;

/// <summary>Injects mouse and keyboard input using absolute virtual-desktop coordinates.</summary>
internal sealed class InputInjector
{
    private readonly Func<List<MonitorInfo>> _monitors;
    public InputInjector(Func<List<MonitorInfo>> monitors) => _monitors = monitors;

    public void Mouse(MouseMsg m)
    {
        var monitors = _monitors();
        var mon = monitors.FirstOrDefault(x => x.Index == m.Monitor);
        if (mon == null) return;
        int screenX = mon.X + m.X, screenY = mon.Y + m.Y;

        // The virtual-desktop bounds MUST come from the same source as the monitor rectangles we
        // captured from. GetSystemMetrics reports in the system-DPI coordinate space, which differs
        // from the per-monitor-aware rectangles returned by EnumDisplayMonitors on a scaled display —
        // mixing the two lands the cursor at the wrong fraction of the screen.
        int left = monitors.Min(x => x.X);
        int top = monitors.Min(x => x.Y);
        int right = monitors.Max(x => x.X + x.Width);
        int bottom = monitors.Max(x => x.Y + x.Height);
        int vx = left, vy = top;
        int vw = Math.Max(1, right - left);
        int vh = Math.Max(1, bottom - top);

        // Absolute coordinates are 0..65535 across the virtual desktop; the +0.5 lands on the pixel
        // centre rather than its top-left corner.
        int ax = (int)Math.Round((screenX - vx + 0.5) * 65535.0 / vw);
        int ay = (int)Math.Round((screenY - vy + 0.5) * 65535.0 / vh);
        ax = Math.Clamp(ax, 0, 65535);
        ay = Math.Clamp(ay, 0, 65535);

        uint flags = Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK;
        uint data = 0;
        switch (m.Action)
        {
            case "move": break;
            case "down": flags |= m.Button switch { 1 => Native.MOUSEEVENTF_RIGHTDOWN, 2 => Native.MOUSEEVENTF_MIDDLEDOWN, _ => Native.MOUSEEVENTF_LEFTDOWN }; break;
            case "up": flags |= m.Button switch { 1 => Native.MOUSEEVENTF_RIGHTUP, 2 => Native.MOUSEEVENTF_MIDDLEUP, _ => Native.MOUSEEVENTF_LEFTUP }; break;
            case "wheel": flags |= Native.MOUSEEVENTF_WHEEL; data = unchecked((uint)m.Delta); break;
        }
        Send(new Native.INPUT { type = Native.INPUT_MOUSE, U = new Native.InputUnion { mi = new Native.MOUSEINPUT { dx = ax, dy = ay, mouseData = data, dwFlags = flags } } });
    }

    public void Key(KeyMsg k)
    {
        uint flags = 0;
        if (!k.Down) flags |= Native.KEYEVENTF_KEYUP;
        if (k.Extended) flags |= Native.KEYEVENTF_EXTENDEDKEY;
        Send(new Native.INPUT { type = Native.INPUT_KEYBOARD, U = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = (ushort)k.Vk, dwFlags = flags } } });
    }

    private static void Send(Native.INPUT input) => Native.SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
}
