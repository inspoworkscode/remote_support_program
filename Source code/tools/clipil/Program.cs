using System.Runtime.InteropServices;
using System.Text;
using RemoteSupport.Host;

// ---------------------------------------------------------------------------------------------
// Does a file list cross an INTEGRITY LEVEL boundary on the clipboard?
//
// The host writes the customer's clipboard as SYSTEM. Explorer reads it as the user, at Medium.
// Text crosses that boundary — proven on hardware. Files do not, and six real faults later
// nobody has measured whether a CF_HDROP written above Medium is even visible from Medium.
// Every check so far read the clipboard back from the same side it was written on: the host's
// own GetFiles, and a PowerShell that the host itself spawned, as SYSTEM.
//
// This measures the boundary itself, on this laptop, with no elevation: write at Medium (the
// level this process runs at), read from a child of this process lowered to LOW integrity. The
// relation "reader below writer" is the same one as SYSTEM -> Medium. Then the reverse, because
// the other direction of the mirror (the customer copies as Medium, the host reads as SYSTEM)
// has only ever been proven with a writer the host spawned — also SYSTEM.
//
//   clipil            run the measurement
//   clipil --child    (internal) the low-integrity half
// ---------------------------------------------------------------------------------------------

if (args.Length > 0 && args[0] == "--child") return Child.Run(args[1], args.Skip(2).ToArray());
// clipil --setfiles <path>...   put a file list on this clipboard with the host's own code and exit,
// so a script can then ask the shell what it makes of it. --settext <text> likewise.
if (args.Length > 1 && args[0] is "--setfiles" or "--settext")
{
    using var c = new ClipboardAccess();
    Thread.Sleep(300);
    bool ok = args[0] == "--setfiles" ? c.SetFiles(args.Skip(1).ToArray(), out var w) : c.SetText(args[1], out w);
    Console.WriteLine(ok ? "ok" : "FAILED: " + w);
    return ok ? 0 : 1;
}

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail)
{
    if (ok) pass++; else fail++;
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}" + (detail.Length > 0 ? $"  — {detail}" : ""));
}

Console.WriteLine($"this process: {Low.Label()}");
var exe = Environment.ProcessPath!;
var lowDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "clipil");
Directory.CreateDirectory(lowDir);

var root = Path.Combine(Path.GetTempPath(), "clipil-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
File.WriteAllText(Path.Combine(root, "one.txt"), "one");
File.WriteAllText(Path.Combine(root, "two.txt"), "two");
var files = new[] { Path.Combine(root, "one.txt"), Path.Combine(root, "two.txt") };

using var clip = new ClipboardAccess();
Thread.Sleep(300);

// ---- 1. written ABOVE the reader, the host's way ------------------------------------------------
Console.WriteLine();
Console.WriteLine("WRITTEN AT MEDIUM BY ClipboardAccess.SetFiles, READ FROM LOW (the SYSTEM -> Explorer relation)");
bool w1 = clip.SetFiles(files, out var why1);
Check("SetFiles at Medium", w1, w1 ? "2 files" : why1);
var r1 = Low.RunChild(exe, lowDir, "read");
Report(r1);
Check("LOW child sees the file list at all (IsClipboardFormatAvailable)", r1.GetValueOrDefault("hdrop-available") == "True", "");
Check("LOW child can GetClipboardData(CF_HDROP)", r1.GetValueOrDefault("hdrop-handle") == "True", "win32 " + r1.GetValueOrDefault("hdrop-lasterror", "?"));
Check("LOW child reads 2 paths through DragQueryFile (ClipboardAccess.GetFiles)", r1.GetValueOrDefault("getfiles") == "2", r1.GetValueOrDefault("getfiles-detail", ""));
Check("LOW child reads 2 paths the way Explorer does (OLE GetFileDropList)", r1.GetValueOrDefault("ole-files") == "2", r1.GetValueOrDefault("ole-detail", ""));
Check("LOW child sees Preferred DropEffect", r1.GetValueOrDefault("dropeffect") == "1", r1.GetValueOrDefault("dropeffect", "(none)"));
// The question Explorer actually asks. Reading the list back is necessary and not sufficient:
// on DESKTOP-OPHQIDH on 2026-09-21 the user's own process read both paths and the shell still
// offered no Paste, with a SYSTEM host as the clipboard owner.
var vtarget = Path.Combine(Path.GetTempPath(), "clipil-target-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(vtarget);
var rv = Low.RunChild(exe, lowDir, "verbs", new[] { vtarget });
Report(rv);
Check("the shell, asked from LOW, offers Paste for a list a MEDIUM owner wrote", rv.GetValueOrDefault("paste-offered") == "True",
    rv.GetValueOrDefault("paste-offered") == "True" ? "" : "verbs: " + rv.GetValueOrDefault("verbs", rv.GetValueOrDefault("verbs-error", "?")));
try { Directory.Delete(vtarget, true); } catch { }

// ---- 2. same boundary, text ------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("WRITTEN AT MEDIUM AS TEXT, READ FROM LOW (the case that is known to work on hardware)");
var text = "clipil " + Guid.NewGuid().ToString("N");
bool w2 = clip.SetText(text, out var why2);
Check("SetText at Medium", w2, w2 ? "" : why2);
var r2 = Low.RunChild(exe, lowDir, "read");
Report(r2);
Check("LOW child reads the text", r2.GetValueOrDefault("text") == text, r2.GetValueOrDefault("text", "(none)"));

// ---- 3. written by OLE (what Explorer's own Ctrl+C does), read from LOW ----------------------
Console.WriteLine();
Console.WriteLine("WRITTEN AT MEDIUM BY OLE (Clipboard.SetFileDropList), READ FROM LOW");
var sta = new Thread(() =>
{
    var col = new System.Collections.Specialized.StringCollection();
    col.AddRange(files);
    System.Windows.Forms.Clipboard.SetFileDropList(col);
});
sta.SetApartmentState(ApartmentState.STA); sta.Start(); sta.Join();
var r3 = Low.RunChild(exe, lowDir, "read");
Report(r3);
Check("LOW child reads 2 paths through DragQueryFile", r3.GetValueOrDefault("getfiles") == "2", r3.GetValueOrDefault("getfiles-detail", ""));
Check("LOW child reads 2 paths the way Explorer does (OLE)", r3.GetValueOrDefault("ole-files") == "2", r3.GetValueOrDefault("ole-detail", ""));

// ---- 4. the reverse: written BELOW the reader ----------------------------------------------
Console.WriteLine();
Console.WriteLine("WRITTEN AT LOW BY ClipboardAccess.SetFiles, READ AT MEDIUM (the Explorer -> SYSTEM relation)");
var r4 = Low.RunChild(exe, lowDir, "write", files);
Report(r4);
Check("LOW child could SetFiles", r4.GetValueOrDefault("setfiles") == "True", r4.GetValueOrDefault("setfiles-detail", ""));
var back = clip.GetFiles();
Check("this process (Medium) reads 2 paths through DragQueryFile", back is { Count: 2 }, back == null ? "no file list" : string.Join(", ", back));
int oleBack = -1; string oleDetail = "";
var sta2 = new Thread(() =>
{
    try { var c = System.Windows.Forms.Clipboard.GetFileDropList(); oleBack = c.Count; oleDetail = string.Join(", ", c.Cast<string>()); }
    catch (Exception ex) { oleDetail = ex.GetType().Name + ": " + ex.Message; }
});
sta2.SetApartmentState(ApartmentState.STA); sta2.Start(); sta2.Join();
Check("this process (Medium) reads 2 paths the way Explorer does (OLE)", oleBack == 2, oleDetail);

// ---- 5. the delay-rendered list, which is what a real person's Ctrl+C produces ----------------
// Measured on DESKTOP-OPHQIDH 2026-09-21: when the logged-on user copies files, the raw Win32
// clipboard carries NO CF_HDROP — only a `DataObject` marker, with the list rendered on demand
// through OLE. The SYSTEM host's GetClipboardData(CF_HDROP) came back NULL, win32 203, and
// "copy from the customer" did nothing at all. ClipboardAccess.GetFiles falls back to OLE for
// exactly this. The owner must still be ALIVE: an OLE owner that exits gets flushed to real
// formats by Windows, and then the raw read would have worked and this would prove nothing.
Console.WriteLine();
Console.WriteLine("A LIVE OLE OWNER, DELAY-RENDERING ITS FILE LIST (what a real Ctrl+C looks like)");
var readyFile = Path.Combine(lowDir, "ready-" + Guid.NewGuid().ToString("N") + ".txt");
var holder = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe,
    $"--child \"{Path.Combine(lowDir, "hold.txt")}\" holdole \"{readyFile}\" \"{files[0]}\" \"{files[1]}\"")
{ UseShellExecute = false, CreateNoWindow = true });
for (int i = 0; i < 100 && !File.Exists(readyFile); i++) Thread.Sleep(100);
Thread.Sleep(400);
bool rawBlind = false;
try
{
    // The raw route on its own, to show WHY the fallback has to exist. Reflection rather than a
    // public method: GetFilesRaw is private on purpose and this harness should not widen it.
    var mi = typeof(ClipboardAccess).GetMethod("GetFilesRaw", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    var rawList = mi?.Invoke(clip, null) as List<string>;
    rawBlind = rawList is null or { Count: 0 };
    Console.WriteLine($"      the raw Win32 read sees: {(rawList == null ? "nothing" : rawList.Count + " path(s)")}");
}
catch (Exception ex) { Console.WriteLine("      (could not call the raw read: " + ex.GetType().Name + ")"); }
var viaOle = clip.GetFiles();
Check("GetFiles reads a list that is only delay-rendered through OLE", viaOle is { Count: 2 },
    viaOle == null ? "nothing came back — copy from the customer would do nothing"
                   : string.Join(", ", viaOle.Select(Path.GetFileName)));
Console.WriteLine(rawBlind
    ? "      (and the raw route alone was blind to it — the fallback is what carried this)"
    : "      NOTE: this machine did NOT reproduce the blindness. One user, one integrity level, and\n"
    + "      OLE materialised the list for the raw read anyway. The failure needs the reader to be\n"
    + "      a DIFFERENT account from the copier — SYSTEM host, logged-on user — which only the real\n"
    + "      machine has. tools/usercopy is the proof for that; this check only shows GetFiles is\n"
    + "      not broken by the fallback.");
try { holder?.Kill(); } catch { }
try { File.Delete(readyFile); } catch { }
Thread.Sleep(300);

Console.WriteLine();
Console.WriteLine("WRITTEN AT LOW AS TEXT, READ AT MEDIUM");
var text2 = "clipil low " + Guid.NewGuid().ToString("N");
var r5 = Low.RunChild(exe, lowDir, "writetext", new[] { text2 });
Report(r5);
var t2 = clip.GetText();
Check("this process (Medium) reads the text", t2 == text2, t2 ?? "(none)");

try { Directory.Delete(root, true); } catch { }
Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} passed");
return fail == 0 ? 0 : 1;

static void Report(Dictionary<string, string> r)
{
    foreach (var kv in r) if (kv.Key.StartsWith("child-")) Console.WriteLine($"      {kv.Key}: {kv.Value}");
}

static class Child
{
    public static int Run(string outFile, string[] a)
    {
        var sb = new StringBuilder();
        void Put(string k, string v) => sb.AppendLine(k + "=" + v.Replace("\r", " ").Replace("\n", " "));
        Put("child-label", Low.Label());
        try
        {
            using var clip = new ClipboardAccess();
            Thread.Sleep(300);
            switch (a[0])
            {
                case "read":
                {
                    Put("hdrop-available", Low.IsClipboardFormatAvailable(15).ToString());
                    bool handle = false;
                    if (Low.OpenClipboard(IntPtr.Zero))
                    {
                        try { handle = Low.GetClipboardData(15) != IntPtr.Zero; Put("hdrop-lasterror", Marshal.GetLastWin32Error().ToString()); }
                        finally { Low.CloseClipboard(); }
                    }
                    else Put("openclipboard-lasterror", Marshal.GetLastWin32Error().ToString());
                    Put("hdrop-handle", handle.ToString());
                    var got = clip.GetFiles();
                    Put("getfiles", (got?.Count ?? 0).ToString());
                    Put("getfiles-detail", got == null ? "no file list" : string.Join(", ", got));
                    Put("text", clip.GetText() ?? "");
                    var effFmt = Low.RegisterClipboardFormatW("Preferred DropEffect");
                    if (Low.OpenClipboard(IntPtr.Zero))
                    {
                        try
                        {
                            var h = Low.GetClipboardData(effFmt);
                            if (h != IntPtr.Zero) { var p = Low.GlobalLock(h); Put("dropeffect", Marshal.ReadInt32(p).ToString()); Low.GlobalUnlock(h); }
                            else Put("dropeffect", "handle null, win32 " + Marshal.GetLastWin32Error());
                        }
                        finally { Low.CloseClipboard(); }
                    }
                    int ole = -1; string oleDetail = "";
                    var t = new Thread(() =>
                    {
                        try { var c = System.Windows.Forms.Clipboard.GetFileDropList(); ole = c.Count; oleDetail = string.Join(", ", c.Cast<string>()); }
                        catch (Exception ex) { oleDetail = ex.GetType().Name + ": " + ex.Message; }
                    });
                    t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
                    Put("ole-files", ole.ToString());
                    Put("ole-detail", oleDetail);
                    break;
                }
                case "verbs":
                {
                    // Ask the shell, from LOW, whether it would offer Paste into a folder for
                    // whatever is on the clipboard now. This is the question Explorer asks, and
                    // it is a different question from "can I read the file list".
                    var target = a[1];
                    try
                    {
                        dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                        dynamic self = sh.NameSpace(target).Self;
                        var names = new List<string>();
                        foreach (dynamic vb in self.Verbs()) names.Add(((string)vb.Name).Replace("&", ""));
                        Put("verbs", string.Join(", ", names));
                        Put("paste-offered", names.Contains("Paste").ToString());
                    }
                    catch (Exception ex) { Put("verbs-error", ex.GetType().Name + ": " + ex.Message); }
                    break;
                }
                case "holdole":
                {
                    // Put a file list on the clipboard THROUGH OLE and stay alive holding it.
                    // Staying alive is the whole point: an OLE owner that exits has its data
                    // flushed to real clipboard formats by Windows, and the delay-rendered case
                    // — the one a live Explorer produces, and the one the raw Win32 read cannot
                    // see — only exists while the owner is still there.
                    var toSet = a.Skip(2).ToArray();
                    var ready = a[1];
                    var th = new Thread(() =>
                    {
                        var col = new System.Collections.Specialized.StringCollection();
                        col.AddRange(toSet);
                        System.Windows.Forms.Clipboard.SetFileDropList(col);
                    });
                    th.SetApartmentState(ApartmentState.STA); th.Start(); th.Join();
                    Put("held", toSet.Length.ToString());
                    File.WriteAllText(outFile, sb.ToString());
                    try { File.WriteAllText(ready, "ready"); } catch { }
                    Thread.Sleep(20000);   // hold it, so the parent reads a LIVE delay-rendered list
                    return 0;
                }
                case "write":
                {
                    bool ok = clip.SetFiles(a.Skip(1).ToArray(), out var why);
                    Put("setfiles", ok.ToString());
                    Put("setfiles-detail", ok ? "" : why);
                    break;
                }
                case "writetext":
                {
                    bool ok = clip.SetText(a[1], out var why);
                    Put("settext", ok.ToString());
                    Put("settext-detail", ok ? "" : why);
                    break;
                }
            }
        }
        catch (Exception ex) { Put("child-error", ex.ToString()); }
        File.WriteAllText(outFile, sb.ToString());
        return 0;
    }
}

static class Low
{
    [DllImport("user32.dll", SetLastError = true)] public static extern bool OpenClipboard(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr GetClipboardData(uint f);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool IsClipboardFormatAvailable(uint f);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterClipboardFormatW(string n);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr h);

    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, uint access, out IntPtr tok);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr tok, uint access, IntPtr attrs, int level, int type, out IntPtr newTok);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool ConvertStringSidToSidW(string s, out IntPtr sid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetTokenInformation(IntPtr tok, int cls, ref TOKEN_MANDATORY_LABEL info, int len);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr tok, int cls, IntPtr info, int len, out int ret);
    [DllImport("advapi32.dll", SetLastError = true)] static extern IntPtr GetSidSubAuthority(IntPtr sid, int i);
    [DllImport("advapi32.dll", SetLastError = true)] static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessAsUserW(IntPtr tok, string? app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);

    [StructLayout(LayoutKind.Sequential)] struct SID_AND_ATTRIBUTES { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] struct TOKEN_MANDATORY_LABEL { public SID_AND_ATTRIBUTES Label; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO { public int cb; public string? r1, desktop, title; public int x, y, cx, cy, xc, yc, fill, flags; public short show, r2; public IntPtr r3, in_, out_, err; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr p, t; public int pid, tid; }

    const int TokenIntegrityLevel = 25;
    const uint SE_GROUP_INTEGRITY = 0x20;

    public static string Label()
    {
        try
        {
            OpenProcessToken(GetCurrentProcess(), 0x0008, out var tok);
            GetTokenInformation(tok, TokenIntegrityLevel, IntPtr.Zero, 0, out int need);
            var buf = Marshal.AllocHGlobal(need);
            GetTokenInformation(tok, TokenIntegrityLevel, buf, need, out _);
            var lbl = Marshal.PtrToStructure<TOKEN_MANDATORY_LABEL>(buf);
            int count = Marshal.ReadByte(GetSidSubAuthorityCount(lbl.Label.Sid));
            int rid = Marshal.ReadInt32(GetSidSubAuthority(lbl.Label.Sid, count - 1));
            Marshal.FreeHGlobal(buf); CloseHandle(tok);
            return rid switch { 0x1000 => "LOW", 0x2000 => "MEDIUM", 0x3000 => "HIGH", 0x4000 => "SYSTEM", _ => "0x" + rid.ToString("x") } + $" integrity (pid {Environment.ProcessId})";
        }
        catch (Exception ex) { return "unknown: " + ex.Message; }
    }

    /// <summary>Runs this exe again as a LOW-integrity child, waits for it, returns what it wrote.</summary>
    public static Dictionary<string, string> RunChild(string exe, string lowDir, string mode, string[]? extra = null)
    {
        var outFile = Path.Combine(lowDir, $"{mode}-{Guid.NewGuid():N}.txt");
        if (!OpenProcessToken(GetCurrentProcess(), 0x0002 | 0x0008 | 0x0080 | 0x0001, out var tok))
            throw new InvalidOperationException("OpenProcessToken " + Marshal.GetLastWin32Error());
        if (!DuplicateTokenEx(tok, 0x02000000 /*MAXIMUM_ALLOWED*/, IntPtr.Zero, 2 /*SecurityImpersonation*/, 1 /*TokenPrimary*/, out var low))
            throw new InvalidOperationException("DuplicateTokenEx " + Marshal.GetLastWin32Error());
        ConvertStringSidToSidW("S-1-16-4096", out var sid);
        var tml = new TOKEN_MANDATORY_LABEL { Label = new SID_AND_ATTRIBUTES { Sid = sid, Attributes = SE_GROUP_INTEGRITY } };
        if (!SetTokenInformation(low, TokenIntegrityLevel, ref tml, Marshal.SizeOf<TOKEN_MANDATORY_LABEL>() + 12))
            throw new InvalidOperationException("SetTokenInformation " + Marshal.GetLastWin32Error());

        var cmd = new StringBuilder($"\"{exe}\" --child \"{outFile}\" {mode}");
        foreach (var e in extra ?? Array.Empty<string>()) cmd.Append($" \"{e}\"");
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        if (!CreateProcessAsUserW(low, null, cmd.ToString(), IntPtr.Zero, IntPtr.Zero, false, 0x08000000 /*CREATE_NO_WINDOW*/, IntPtr.Zero, null, ref si, out var pi))
            throw new InvalidOperationException("CreateProcessAsUser " + Marshal.GetLastWin32Error());
        WaitForSingleObject(pi.p, 30000);
        CloseHandle(pi.p); CloseHandle(pi.t); CloseHandle(low); CloseHandle(tok);

        var d = new Dictionary<string, string>();
        if (!File.Exists(outFile)) { d["child-error"] = "the child wrote nothing"; return d; }
        foreach (var line in File.ReadAllLines(outFile))
        {
            int i = line.IndexOf('=');
            if (i > 0) d[line[..i]] = line[(i + 1)..];
        }
        try { File.Delete(outFile); } catch { }
        return d;
    }
}
