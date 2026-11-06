using System.Runtime.InteropServices;
using System.Windows.Forms;
using RemoteSupport.Shared;

namespace RemoteSupport.Technician;

// ---- Windows shortcuts go to THEIR machine (2026-10-07) -------------------------------------
// Clint: "while doing remote support on a machine, using windows shortcuts works within that
// session from the technician machine" — without changing how Ctrl+C and Ctrl+V work.
//
// Most keys reach the picture as ordinary key messages and go over in OnKeyDown/OnKeyUp. A few
// never do: Windows acts on them before any program is told — the Windows key and everything
// pressed with it, Alt+Tab, Alt+Esc, Ctrl+Esc, Ctrl+Shift+Esc, Print Screen — so they opened the
// Start menu, the task switcher or Task Manager on the TECHNICIAN's machine. A low-level keyboard
// hook sees them first. It exists only while the picture has the keyboard focus (installed in
// OnGotFocus, removed in OnLostFocus): click anywhere else and the shortcuts are the
// technician's own again. There is deliberately no on/off switch (Clint's decision).
//
// The hook takes ONLY those keys and lets everything else through untouched, so Ctrl+C, Ctrl+V
// and every other key still arrive by the ordinary path, exactly as before — clipboard mirroring
// never depended on the keys anyway; it watches the clipboards. `shortcutcheck` asserts both.
//
// Two Windows will not let any program take: Ctrl+Alt+Del (the toolbar's button) and Win+L,
// which locks the technician's own PC (the toolbar's Lock locks theirs).
public sealed partial class ScreenView
{
    /// <summary>Keys the hook took down and has not yet seen come up; their release is the hook's too.</summary>
    private readonly HashSet<int> _taken = new();
    /// <summary>Keys sent down by the ordinary path and not yet released — the modifiers a caught shortcut leans on.</summary>
    private readonly HashSet<int> _remoteDown = new();
    private IntPtr _hook;
    private HookProc? _hookProc;   // held, or the collector takes the delegate Windows is calling
    private long _shortcutsSent;

    /// <summary>How many key events the hook has sent across. For the harness: Ctrl+C must not move it.</summary>
    internal long ShortcutKeysSent => _shortcutsSent;
    internal bool ShortcutHookInstalled => _hook != IntPtr.Zero;

    private void InstallShortcutHook()
    {
        if (_hook != IntPtr.Zero) return;
        _hookProc ??= HookCallback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
        Trace.Write(_hook != IntPtr.Zero ? "view: shortcut hook on — Windows shortcuts go to their machine"
                                         : $"view: shortcut hook FAILED (error {Marshal.GetLastWin32Error()}) — Windows shortcuts stay on this machine");
    }

    /// <summary>
    /// Removes the hook and lets go of everything still held down over there. The key-up of a key
    /// held while the focus left goes to whatever has the focus now, never to the picture, so
    /// without this their machine would go on holding Ctrl or the Windows key.
    /// </summary>
    private void RemoveShortcutHook()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; Trace.Write("view: shortcut hook off"); }
        if (_taken.Count == 0 && _remoteDown.Count == 0) return;
        bool live = ControlEnabled && !Stale;
        foreach (var vk in _taken.Concat(_remoteDown).Distinct().ToList())
            if (live) OnInput?.Invoke(new KeyMsg { Vk = vk, Down = false, Extended = IsExtended((Keys)vk) });
        Trace.Write($"view: released {_taken.Count + _remoteDown.Count} held key(s) on their machine as the focus left");
        _taken.Clear(); _remoteDown.Clear();
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool altFlag = (k.flags & LLKHF_ALTDOWN) != 0;
            try { if (Catch((int)k.vkCode, down, altFlag)) return (IntPtr)1; }
            catch (Exception ex) { Trace.Write("view: shortcut hook threw " + ex.Message); }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>True when the key was sent to their machine and must not reach this one.</summary>
    private bool Catch(int vk, bool down, bool altFlag)
    {
        if (!down)
        {
            if (!_taken.Remove(vk)) return false;   // its down went the ordinary way; so does its up
            Send(vk, false);
            return true;
        }

        if (!ControlEnabled || Stale || !Focused || !IsForeground()) return false;
        bool ctrl = Held(VK_CONTROL), alt = altFlag || Held(VK_MENU);
        bool winHeld = _taken.Contains(VK_LWIN) || _taken.Contains(VK_RWIN);
        bool take = vk is VK_LWIN or VK_RWIN           // the Start menu, and every Win+ shortcut
                 || winHeld                            // Win+E, Win+R, Win+D, Win+Shift+S, Win+arrows…
                 || vk == VK_SNAPSHOT                  // Print Screen: Windows never sends its key-down
                 || (vk == VK_TAB && alt)              // Alt+Tab, Alt+Shift+Tab
                 || (vk == VK_ESCAPE && (ctrl || alt)); // Ctrl+Esc, Ctrl+Shift+Esc, Alt+Esc
        if (!take) return false;

        // Ctrl, Shift and Alt came the ordinary way. Their messages may still be waiting in this
        // window's queue when the hook runs — it is called ahead of them — so a modifier that is
        // held but not yet sent goes first, or Alt+Tab would arrive as Tab then Alt. The ordinary
        // message then arrives as a second key-down, which their machine takes as auto-repeat.
        foreach (var mod in new[] { VK_CONTROL, VK_SHIFT, VK_MENU })
            if (Held(mod) && _remoteDown.Add(mod)) OnInput?.Invoke(new KeyMsg { Vk = mod, Down = true });

        _taken.Add(vk);
        Send(vk, true);
        return true;
    }

    private void Send(int vk, bool down)
    {
        _shortcutsSent++;
        if (_shortcutsSent <= 6 || _shortcutsSent % 200 == 0) Trace.Write($"view: shortcut key {(down ? "down" : "up")} vk={vk} ({(Keys)vk}) to their machine  [sent={_shortcutsSent}]");
        OnInput?.Invoke(new KeyMsg { Vk = vk, Down = down, Extended = IsExtended((Keys)vk) });
    }

    private bool IsForeground()
    {
        var fg = GetForegroundWindow();
        return fg != IntPtr.Zero && fg == GetAncestor(Handle, GA_ROOT);
    }

    private static bool Held(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104, LLKHF_ALTDOWN = 0x20, GA_ROOT = 2;
    private const int VK_TAB = 0x09, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_ESCAPE = 0x1B,
                      VK_SNAPSHOT = 0x2C, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, int flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
