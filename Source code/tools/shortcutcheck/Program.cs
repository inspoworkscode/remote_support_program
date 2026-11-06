using System.Reflection;
using System.Runtime.InteropServices;
using RemoteSupport.Shared;
using RemoteSupport.Technician;
using RemoteSupport.UI;

// ---------------------------------------------------------------------------------------------
// shortcutcheck - Windows shortcuts go to THEIR machine (2026-10-07).
//
// Clint: "while doing remote support on a machine, using windows shortcuts works within that
// session from the technician machine" — and Ctrl+C / Ctrl+V must work exactly as before.
//
// The REAL SessionForm and ScreenView, with no server (as dragoffcheck). Keys are pressed for
// real with SendInput from another thread, so they pass through Windows — and through the
// console's low-level hook — exactly as a technician's keyboard would. What is asserted is the
// key messages the picture would send to their machine, in order, and that nothing happened on
// THIS machine (the Start menu, Run, Task Manager or the task switcher would take the foreground).
//
// SAFETY: every key is pressed only while this harness's own window is in front with the picture
// focused; the moment it is not, the run stops. Before running, leave the keyboard alone for the
// few seconds it takes.
//
//   dotnet run -c Debug --project tools/shortcutcheck/shortcutcheck.csproj
// ---------------------------------------------------------------------------------------------

internal static class ShortcutCheck
{
    private static int pass, fail;
    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    }

    private const ushort SHIFT = 0x10, CTRL = 0x11, ALT = 0x12, ESC = 0x1B, TAB = 0x09, LWIN = 0x5B, PRTSC = 0x2C;

    [STAThread]
    private static int Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Console.WriteLine("shortcutcheck - Windows shortcuts go to their machine");
        Console.WriteLine();

        var brand = Branding.Load();
        var theme = Theme.FromBranding(brand, AppSurface.Technician);
        var session = new SessionInfo { SessionId = "probe", DeviceId = "probe", MachineName = "PROBE-PC", StartedBy = "Probe", Unattended = true };

        using var f = new SessionForm("ws://127.0.0.1:59999", "probe-token", session, theme, brand);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(40, 40);
        f.Size = new Size(1100, 760);
        var elsewhere = new TextBox { Location = new Point(4, 4), Width = 80 };
        f.Show();
        f.Controls.Add(elsewhere);
        Pump(600);

        var view = Field<ScreenView>(f, "_view")!;
        view.Reset(1920, 1080);
        var sent = new List<KeyMsg>();
        view.OnInput += m => { if (m is KeyMsg k) sent.Add(k); };
        long Taken() => (long)Prop(view, "ShortcutKeysSent")!;
        bool Hooked() => (bool)Prop(view, "ShortcutHookInstalled")!;

        void Ready() { view.Stale = false; view.ControlEnabled = true; sent.Clear(); }
        bool InFront() => GetForegroundWindow() == f.Handle && view.Focused;

        BringToFront(f);
        view.Focus();
        Pump(300);
        if (!InFront())
        {
            Console.WriteLine("  ABORT  could not bring the harness window to the front with the picture focused — no key was pressed");
            return 2;
        }

        // ---- the hook follows the focus ----
        Console.WriteLine("== the hook follows the focus ==");
        Check("focusing the picture installs the hook", Hooked());

        // Presses keys for real and waits for every message they cause to be handled. Stops the
        // whole run if the picture is not in front: a Ctrl+V or Win+R into whatever IS in front
        // would act on this machine.
        bool Press(string what, bool burst, params (ushort vk, bool up)[] keys)
        {
            Ready();
            if (!InFront()) { Console.WriteLine($"  ABORT  before \"{what}\": the picture is no longer in front — stopping"); return false; }
            var t = Task.Run(() =>
            {
                if (burst) Inject(keys);
                else foreach (var k in keys) { Inject(k); Thread.Sleep(40); }
            });
            while (!t.IsCompleted) Pump(10);
            Pump(400);
            return true;
        }

        // ---- ordinary keys, and the clipboard keys, are untouched ----
        Console.WriteLine("== ordinary keys and Ctrl+C / Ctrl+V go the way they always did ==");
        long before = Taken();
        if (!Press("A", false, ('A', false), ('A', true))) return Done();
        Check("A arrives as A down, A up", Seq() == "A↓ A↑", Seq());
        Check("A did not pass through the shortcut hook", Taken() == before);

        before = Taken();
        if (!Press("Ctrl+C", false, (CTRL, false), ('C', false), ('C', true), (CTRL, true))) return Done();
        Check("Ctrl+C arrives as Ctrl down, C down, C up, Ctrl up", Seq() == "Ctrl↓ C↓ C↑ Ctrl↑", Seq());
        Check("Ctrl+C did not pass through the shortcut hook", Taken() == before);

        before = Taken();
        if (!Press("Ctrl+V", false, (CTRL, false), ('V', false), ('V', true), (CTRL, true))) return Done();
        Check("Ctrl+V arrives as Ctrl down, V down, V up, Ctrl up", Seq() == "Ctrl↓ V↓ V↑ Ctrl↑", Seq());
        Check("Ctrl+V did not pass through the shortcut hook", Taken() == before);

        // ---- the shortcuts Windows keeps for itself ----
        Console.WriteLine("== the Windows key and its shortcuts ==");
        if (!Press("Win", false, (LWIN, false), (LWIN, true))) return Done();
        Check("Win alone goes over as Win down, Win up", Seq() == "Win↓ Win↑", Seq());
        Check("…marked as an extended key, as a real Windows key is", sent.Count == 2 && sent.All(k => k.Extended));
        Check("…and the Start menu did not open here", InFront());

        if (!Press("Win+R", false, (LWIN, false), ('R', false), ('R', true), (LWIN, true))) return Done();
        Check("Win+R goes over whole", Seq() == "Win↓ R↓ R↑ Win↑", Seq());
        Check("…and Run did not open here", InFront());

        if (!Press("Win+Shift+S", false, (LWIN, false), (SHIFT, false), ('S', false), ('S', true), (SHIFT, true), (LWIN, true))) return Done();
        Check("Win+Shift+S goes over whole, in order", Seq() == "Win↓ Shift↓ S↓ S↑ Shift↑ Win↑", Seq());
        Check("…and the snipping bar did not open here", InFront());

        if (!Press("Win+E, Win released first", false, (LWIN, false), ('E', false), (LWIN, true), ('E', true))) return Done();
        Check("a key held past the Windows key still has its release sent", Seq() == "Win↓ E↓ Win↑ E↑", Seq());
        Check("…and Explorer did not open here", InFront());

        Console.WriteLine("== Alt+Tab, Ctrl+Esc, Ctrl+Shift+Esc, Alt+Esc, Print Screen ==");
        if (!Press("Alt+Tab, typed", false, (ALT, false), (TAB, false), (TAB, true), (ALT, true))) return Done();
        Check("Alt+Tab goes over: Alt, Tab, Tab, Alt", Ordered("Alt↓", "Tab↓", "Tab↑", "Alt↑") && Balanced(), Seq());
        Check("…and the task switcher did not open here", InFront());

        // All four in one go: the hook is called for Tab before this window has handled the Alt
        // message ahead of it, so this is the case that needs the held modifier sent first.
        if (!Press("Alt+Tab, in one burst", true, (ALT, false), (TAB, false), (TAB, true), (ALT, true))) return Done();
        Check("Alt+Tab in one burst still has Alt down before Tab", Ordered("Alt↓", "Tab↓", "Tab↑", "Alt↑") && Balanced(), Seq());
        Check("…and the task switcher did not open here", InFront());

        if (!Press("Alt+Shift+Tab", true, (ALT, false), (SHIFT, false), (TAB, false), (TAB, true), (SHIFT, true), (ALT, true))) return Done();
        Check("Alt+Shift+Tab goes over with both held first", Ordered("Alt↓", "Tab↓", "Tab↑") && Ordered("Shift↓", "Tab↓") && Balanced(), Seq());

        if (!Press("Ctrl+Shift+Esc", true, (CTRL, false), (SHIFT, false), (ESC, false), (ESC, true), (SHIFT, true), (CTRL, true))) return Done();
        Check("Ctrl+Shift+Esc goes over with Ctrl and Shift held first", Ordered("Ctrl↓", "Esc↓", "Esc↑", "Ctrl↑") && Ordered("Shift↓", "Esc↓") && Balanced(), Seq());
        Check("…and Task Manager did not open here", InFront());

        if (!Press("Ctrl+Esc", false, (CTRL, false), (ESC, false), (ESC, true), (CTRL, true))) return Done();
        Check("Ctrl+Esc goes over", Ordered("Ctrl↓", "Esc↓", "Esc↑", "Ctrl↑") && Balanced(), Seq());
        Check("…and the Start menu did not open here", InFront());

        if (!Press("Alt+Esc", false, (ALT, false), (ESC, false), (ESC, true), (ALT, true))) return Done();
        Check("Alt+Esc goes over", Ordered("Alt↓", "Esc↓", "Esc↑", "Alt↑") && Balanced(), Seq());
        Check("…and this window was not sent to the back", InFront());

        if (!Press("Print Screen", false, (PRTSC, false), (PRTSC, true))) return Done();
        Check("Print Screen goes over, down and up", Seq() == "PrtSc↓ PrtSc↑", Seq());
        Check("…and the Snipping Tool did not open here", InFront());

        before = Taken();
        if (!Press("plain Escape", false, (ESC, false), (ESC, true))) return Done();
        Check("Escape on its own is an ordinary key", Seq() == "Esc↓ Esc↑" && Taken() == before, Seq());
        before = Taken();
        if (!Press("plain Tab", false, (TAB, false), (TAB, true))) return Done();
        Check("Tab on its own is an ordinary key", Seq() == "Tab↓ Tab↑" && Taken() == before, Seq());

        // ---- when not to take them ----
        // Asked of the hook's own decision rather than pressed: a Windows key it rightly declines
        // would open the Start menu on this machine.
        Console.WriteLine("== when the shortcuts stay here ==");
        Ready(); view.ControlEnabled = false;
        Check("with Control unticked the Windows key is left to this machine", !Catch(view, 0x5B, true));
        Ready(); view.Stale = true;
        Check("with the connection down the Windows key is left to this machine", !Catch(view, 0x5B, true));
        Ready();

        // ---- the focus leaving lets go of everything held ----
        Console.WriteLine("== the focus leaving ==");
        // Ctrl first, the ordinary way; then the Windows key, by the hook's decision. (The other
        // way round, Ctrl would be part of a Win shortcut and go over by the hook as Left Ctrl.)
        if (!Press("Ctrl held", false, (CTRL, false))) return Done();
        Check("the hook's decision takes the Windows key while the picture is in front", Catch(view, 0x5B, true));
        sent.Clear();
        elsewhere.Focus();
        Pump(200);
        Check("clicking away removes the hook", !Hooked());
        Check("…and releases the Windows key AND Ctrl on their machine", sent.Count == 2 && sent.All(k => !k.Down)
              && sent.Any(k => k.Vk == 0x5B) && sent.Any(k => k.Vk == CTRL), Seq());
        Inject((CTRL, true));   // this machine's Ctrl, now going to the text box
        Pump(200);
        sent.Clear();
        Check("with the focus elsewhere the hook declines everything", !Catch(view, 0x5B, true));
        view.Focus();
        Pump(200);
        Check("focusing the picture again puts the hook back", Hooked());
        Check("…and the next session's keys start clean", !Press("A again", false, ('A', false), ('A', true)) ? false : Seq() == "A↓ A↑", Seq());

        string Seq() => string.Join(" ", sent.Select(k => Name(k.Vk) + (k.Down ? "↓" : "↑")));
        bool Ordered(params string[] marks)
        {
            var seq = sent.Select(k => Name(k.Vk) + (k.Down ? "↓" : "↑")).ToList();
            int at = -1;
            foreach (var m in marks) { int i = seq.FindIndex(at + 1, s => s == m); if (i < 0) return false; at = i; }
            return true;
        }
        bool Balanced()
        {
            var held = new HashSet<string>();
            foreach (var k in sent) { if (k.Down) held.Add(Name(k.Vk)); else held.Remove(Name(k.Vk)); }
            return held.Count == 0;
        }
        int Done()
        {
            Console.WriteLine();
            Console.WriteLine($"shortcutcheck: {pass} passed, {fail} failed");
            return fail == 0 ? 0 : 1;
        }
        return Done();
    }

    private static string Name(int vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => "Shift",
        0x11 or 0xA2 or 0xA3 => "Ctrl",
        0x12 or 0xA4 or 0xA5 => "Alt",
        0x5B or 0x5C => "Win",
        0x1B => "Esc",
        0x09 => "Tab",
        0x2C => "PrtSc",
        _ => ((Keys)vk).ToString()
    };

    private static bool Catch(ScreenView v, int vk, bool down) =>
        (bool)typeof(ScreenView).GetMethod("Catch", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(v, new object[] { vk, down, false })!;

    private static void BringToFront(Form f)
    {
        f.Activate();
        if (GetForegroundWindow() == f.Handle) return;
        // Windows refuses a background process the foreground; joining the input of the window
        // in front for a moment is the accepted way round it.
        var fg = GetForegroundWindow();
        uint theirs = GetWindowThreadProcessId(fg, out _), ours = GetCurrentThreadId();
        AttachThreadInput(ours, theirs, true);
        SetForegroundWindow(f.Handle);
        f.Activate();
        AttachThreadInput(ours, theirs, false);
    }

    private static void Inject(params (ushort vk, bool up)[] keys)
    {
        var inputs = keys.Select(k => new INPUT
        {
            type = 1,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = k.vk,
                    wScan = (ushort)MapVirtualKey(k.vk, 0),
                    dwFlags = (k.up ? 2u : 0u) | (k.vk is LWIN or PRTSC ? 1u : 0u)
                }
            }
        }).ToArray();
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void Pump(int ms) { var until = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(10); } }
    private static T? Field<T>(object o, string name) where T : class =>
        o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(o) as T;
    private static object? Prop(object o, string name) =>
        o.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(o);

    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
