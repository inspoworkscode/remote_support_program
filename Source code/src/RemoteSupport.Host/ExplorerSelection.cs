using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using RemoteSupport.Shared;

namespace RemoteSupport.Host;

/// <summary>
/// What the person at this machine has selected in the folder window in front of them — an
/// Explorer window, or the desktop itself — for dragging OFF the picture (roadmap 3.5.2).
///
/// <para>The picture of their screen is pixels; the console cannot tell a file from a pixel. So
/// when a technician drags off the picture, it asks this, and Clint's decision of 2026-09-18 is the
/// rule: <b>the foreground Explorer selection, and only that.</b> Anything else in front is said
/// plainly — "the window in front is Word, not a folder" — never a drag that silently does
/// nothing.</para>
///
/// <para>It must run AS the person: Explorer's windows and the desktop's folder view belong to
/// their session and account, and from SYSTEM <c>Shell.Application</c> sees no windows at all. So
/// the host asks a short-lived copy of itself (<c>--get-selection</c>), exactly as it does for a
/// copied file list (§40). Explorer is asked through its own automation, on an STA thread.</para>
///
/// <para>Windows 11 puts several TABS in one Explorer window, and every tab answers to the same
/// window handle. The one being looked at is the tab whose own window is visible — found through
/// <c>IShellBrowser.GetWindow</c>, because the automation object has no notion of "active".</para>
/// </summary>
public static class ExplorerSelection
{
    public const int HelperOk = 0, HelperCrashed = 3;

    /// <summary>What was found, and when nothing usable was, why — in words for the technician.</summary>
    public sealed class Answer
    {
        /// <summary>"explorer", "desktop", or "" when the front window is not a folder at all.</summary>
        public string Where { get; set; } = "";
        /// <summary>The folder the selection is in, when there is one.</summary>
        public string Folder { get; set; } = "";
        public List<string> Paths { get; set; } = new();
        /// <summary>Set whenever <see cref="Paths"/> is empty.</summary>
        public string Why { get; set; } = "";
    }

    /// <summary>
    /// From the host. As the user already, asks directly; as SYSTEM, asks a copy of this exe
    /// running as the person at the machine, and reads its answer back from a file.
    /// </summary>
    public static Answer ReadAsUser()
    {
        if (!UserContext.IsSystem) return ReadHere();
        string dir = Path.Combine(UserContext.ProfileDirectory, "AppData", "Local", "Temp", "RemoteSupport", "paste");
        string file = Path.Combine(dir, $"selection-{Guid.NewGuid():N}.json");
        try
        {
            Directory.CreateDirectory(dir);
            var exe = Environment.ProcessPath ?? "";
            if (!UserProcess.RunAsInteractiveUser(exe, $"--get-selection \"{file}\"", 12000, out int code, out var why))
                return new Answer { Why = $"could not ask the person's session ({why})" };
            if (code != HelperOk || !File.Exists(file))
                return new Answer { Why = "the person's session did not answer" };
            return JsonSerializer.Deserialize<Answer>(File.ReadAllText(file)) ?? new Answer { Why = "the answer could not be read" };
        }
        catch (Exception ex) { return new Answer { Why = $"{ex.GetType().Name}: {ex.Message}" }; }
        finally { try { File.Delete(file); } catch { } }
    }

    /// <summary>The <c>--get-selection</c> helper: reads it here and writes the answer as JSON.</summary>
    public static int RunHelper(string outPath)
    {
        try { File.WriteAllText(outPath, JsonSerializer.Serialize(ReadHere())); return HelperOk; }
        catch { return HelperCrashed; }
    }

    /// <summary>Reads the selection of the folder window in front, as whoever is running this.</summary>
    public static Answer ReadHere() => OnSta(() => Read(FrontWindow()));

    /// <summary>
    /// Reads the selection of one particular window. Public for <c>tools/dragselcheck</c>, which
    /// cannot rely on its own Explorer window being the foreground one.
    /// </summary>
    public static Answer ReadWindow(IntPtr hwnd) => OnSta(() => Read(hwnd));

    private static Answer OnSta(Func<Answer> f)
    {
        Answer? a = null;
        var t = new Thread(() =>
        {
            try { a = f(); }
            catch (Exception ex) { a = new Answer { Why = $"Explorer could not be asked ({ex.GetType().Name}: {ex.Message})" }; }
        }) { IsBackground = true, Name = "rs-selection" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return t.Join(8000) && a != null ? a : new Answer { Why = "Explorer did not answer in time" };
    }

    private static Answer Read(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return new Answer { Why = "no window is in front on their screen" };
        string cls = ClassOf(hwnd);
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        try
        {
            if (cls is "Progman" or "WorkerW")
            {
                dynamic? desk = DesktopView(shell);
                if (desk == null) return new Answer { Where = "desktop", Why = "their desktop could not be read" };
                return FromView(desk, "desktop", "on their desktop");
            }
            if (cls is "CabinetWClass" or "ExploreWClass")
            {
                // Every tab of a Windows 11 Explorer window answers with the same HWND; the one on
                // screen is the one whose own tab window is visible.
                dynamic? best = null;
                foreach (dynamic w in shell.Windows())
                {
                    try
                    {
                        if ((long)w.HWND != hwnd.ToInt64()) continue;
                        if (best == null) best = w;
                        if (TabIsShowing(w)) { best = w; break; }
                    }
                    catch { }
                }
                if (best == null) return new Answer { Where = "explorer", Why = "that Explorer window did not answer" };
                return FromView(best, "explorer", $"in \"{Title(hwnd)}\"");
            }
            var title = Title(hwnd);
            return new Answer
            {
                Why = title.Length > 0
                    ? $"the window in front over there is \"{title}\", not a folder — select the files in an Explorer window, then drag"
                    : "the window in front over there is not a folder — select the files in an Explorer window, then drag"
            };
        }
        finally { try { Marshal.FinalReleaseComObject(shell); } catch { } }
    }

    /// <summary>Paths of the selected items in one folder view (an Explorer tab, or the desktop).</summary>
    private static Answer FromView(dynamic view, string where, string placeWords)
    {
        var a = new Answer { Where = where };
        dynamic doc = view.Document;
        try { a.Folder = (string)doc.Folder.Self.Path; } catch { }
        foreach (dynamic item in doc.SelectedItems())
        {
            try
            {
                string p = item.Path;
                // Real file-system things only. A selected "This PC" or a Control Panel item has
                // a ::{CLSID} path that nothing could fetch.
                if (!string.IsNullOrWhiteSpace(p) && !p.StartsWith("::") && (File.Exists(p) || Directory.Exists(p))) a.Paths.Add(p);
            }
            catch { }
        }
        if (a.Paths.Count == 0) a.Why = $"nothing is selected {placeWords} — click the files there first, then drag";
        return a;
    }

    /// <summary>The desktop's own folder view, through IShellWindows.FindWindowSW(SWC_DESKTOP).</summary>
    private static dynamic? DesktopView(dynamic shell)
    {
        try
        {
            dynamic windows = shell.Windows();
            object loc = 0x0000;          // CSIDL_DESKTOP
            object root = Type.Missing;
            int hwnd = 0;
            // SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1.
            return windows.FindWindowSW(ref loc, ref root, 8, ref hwnd, 1);
        }
        catch { return null; }
    }

    // ---- which tab is on screen -------------------------------------------------------------

    [ComImport, Guid("6d5140c1-7436-11ce-8034-00aa006009fa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider { [PreserveSig] int QueryService(ref Guid sid, ref Guid iid, out IntPtr ppv); }

    /// <summary>Only the first method of IShellBrowser (from IOleWindow) is needed.</summary>
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowserWindow { [PreserveSig] int GetWindow(out IntPtr hwnd); }

    private static bool TabIsShowing(object w)
    {
        try
        {
            if (w is not IServiceProvider sp) return false;
            var sid = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");   // SID_STopLevelBrowser
            var iid = new Guid("000214E2-0000-0000-C000-000000000046");   // IID_IShellBrowser
            if (sp.QueryService(ref sid, ref iid, out var p) != 0 || p == IntPtr.Zero) return false;
            try
            {
                var browser = (IShellBrowserWindow)Marshal.GetObjectForIUnknown(p);
                return browser.GetWindow(out var tab) == 0 && tab != IntPtr.Zero && IsWindowVisible(tab);
            }
            finally { Marshal.Release(p); }
        }
        catch { return false; }
    }

    // ---- the window in front -----------------------------------------------------------------

    /// <summary>
    /// The foreground window, or when that is something of ours or the taskbar, the first real
    /// window below it in z-order — the one a person would say is "in front".
    /// </summary>
    public static IntPtr FrontWindow()
    {
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && !Ignored(fg)) return fg;
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || Ignored(h)) return true;
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }

    private static bool Ignored(IntPtr h)
    {
        GetWindowThreadProcessId(h, out uint pid);
        if (pid == (uint)Environment.ProcessId) return true;
        return ClassOf(h) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "ConsoleWindowClass" or "PseudoConsoleWindow";
    }

    private static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); return GetClassName(h, sb, sb.Capacity) > 0 ? sb.ToString() : ""; }
    private static string Title(IntPtr h) { var sb = new StringBuilder(512); return GetWindowText(h, sb, sb.Capacity) > 0 ? sb.ToString() : ""; }

    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
}
