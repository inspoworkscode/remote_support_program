using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using RemoteSupport.Host;

// ---------------------------------------------------------------------------------------------
// dragselcheck - roadmap 3.5.2: can the host tell what the person has selected in a folder window?
//
// Dragging off the picture takes "the foreground Explorer selection, and only that" (Clint,
// 2026-09-18). This drives the REAL ExplorerSelection on this laptop, as the person running it,
// against an Explorer window it opens on a throwaway folder and selects items in through Explorer's
// own automation - so what is read back is what Explorer says, not what we told ourselves.
//
// An Explorer window opens on this screen for a few seconds and is closed again. Nothing on the
// desktop is selected or changed; it is only read.
//
//   dotnet run -c Debug --project tools/dragselcheck/dragselcheck.csproj
// ---------------------------------------------------------------------------------------------

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}

Console.WriteLine("dragselcheck - reading the selection in the folder window in front");
Console.WriteLine();

var dir = Path.Combine(Path.GetTempPath(), "rs-dragsel " + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(Path.Combine(dir, "folder one"));
File.WriteAllText(Path.Combine(dir, "folder one", "inside.txt"), "x");
File.WriteAllText(Path.Combine(dir, "a.txt"), "a");
File.WriteAllText(Path.Combine(dir, "café.txt"), "c");
File.WriteAllText(Path.Combine(dir, "not me.txt"), "n");

dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
dynamic? win = null;
try
{
    Process.Start("explorer.exe", $"\"{dir}\"");
    var until = DateTime.UtcNow.AddSeconds(15);
    while (win == null && DateTime.UtcNow < until)
    {
        foreach (dynamic w in shell.Windows())
            try { if (string.Equals((string)w.Document.Folder.Self.Path, dir, StringComparison.OrdinalIgnoreCase)) win = w; } catch { }
        if (win == null) Thread.Sleep(250);
    }
    if (win == null) { Console.WriteLine("the Explorer window never appeared"); return 2; }
    var hwnd = new IntPtr((long)win.HWND);
    Thread.Sleep(800);   // let the view finish enumerating before selecting in it

    // ---- two things selected: a file with a non-ASCII name, and a folder ----
    dynamic doc = win.Document;
    doc.SelectItem(doc.Folder.ParseName("café.txt"), 1 | 4 | 16);   // select, deselect others, focus
    doc.SelectItem(doc.Folder.ParseName("folder one"), 1);
    Thread.Sleep(400);
    var a = ExplorerSelection.ReadWindow(hwnd);
    var names = a.Paths.Select(Path.GetFileName).OrderBy(n => n).ToList();
    Check("an Explorer window is recognised as one", a.Where == "explorer", a.Where);
    Check("...and its folder is named", string.Equals(a.Folder, dir, StringComparison.OrdinalIgnoreCase), a.Folder);
    Check("exactly the selected items come back, a file and a folder",
        names.SequenceEqual(new[] { "café.txt", "folder one" }), string.Join(" | ", names));
    Check("...as full paths that exist", a.Paths.All(p => File.Exists(p) || Directory.Exists(p)));
    Check("...and the one NOT selected is not among them", !names.Contains("not me.txt"));

    // ---- nothing selected ----
    foreach (dynamic it in doc.SelectedItems()) doc.SelectItem(it, 0);
    Thread.Sleep(400);
    var none = ExplorerSelection.ReadWindow(hwnd);
    Check("nothing selected gives no paths", none.Paths.Count == 0, string.Join(" | ", none.Paths));
    Check("...and says so in words", none.Why.Contains("nothing is selected"), none.Why);

    // ---- the desktop is a folder window too ----
    var progman = FindWindow("Progman", null);
    var d = ExplorerSelection.ReadWindow(progman);
    Check("the desktop is read as the desktop", d.Where == "desktop", $"{d.Where} {d.Why}");
    Check("...with its own folder", d.Folder.Length > 0 && Directory.Exists(d.Folder), d.Folder);

    // ---- something that is not a folder ----
    var tray = FindWindow("Shell_TrayWnd", null);
    var t = ExplorerSelection.ReadWindow(tray);
    Check("a window that is not a folder gives no paths, and says why", t.Paths.Count == 0 && t.Where == "" && t.Why.Contains("not a folder"), t.Why);

    // ---- the helper's answer survives the trip through its file ----
    var outFile = Path.Combine(dir, "answer.json");
    int code = ExplorerSelection.RunHelper(outFile);
    var back = JsonSerializer.Deserialize<ExplorerSelection.Answer>(File.ReadAllText(outFile));
    Check("the helper writes an answer the host can read back", code == ExplorerSelection.HelperOk && back != null, $"code {code}");
}
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex); }
finally
{
    try { win?.Quit(); } catch { }
    Thread.Sleep(500);
    try { Directory.Delete(dir, true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
return fail == 0 ? 0 : 1;

[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? title);
