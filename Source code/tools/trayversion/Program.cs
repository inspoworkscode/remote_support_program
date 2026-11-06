using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// Does a running program see a newer copy of itself put in its place? (2026-10-19)
//
// Clint: two test machines on all the time, on different versions. The server's history said
// both AGENTS had taken every release by themselves; the console shows the OLDER of agent and
// tray app, and the tray app on DESKTOP-2N6FVGT had been running since 16 October through four
// updates. Reproduced on this PC with a real tray app: it read "RemoteSupport.Client.exe" — the
// new file, swapped in under it by the agent's rename — as its OWN old version, every minute, for
// ever. Windows answers GetFileVersionInfo from a module already loaded at that path, and a
// running program renamed aside still goes by its original path (which is what lets the swap
// work at all). UpdateTrust.FileVersionOnDisk reads the file's bytes instead.
//
// This does to ITSELF what the agent does to the tray app: renames its own running .exe aside,
// copies a stamped release program into its place, and asks both reads about its own path.
// Then it checks the byte reader agrees with Windows on files that are NOT running — ours, 64-
// and 32-bit Windows files — and gives up cleanly on things that are not programs. It puts its
// own .exe back whatever happens.
//
//   dotnet run -c Release --project tools/trayversion/trayversion.csproj
//   dotnet run -c Release --project tools/trayversion/trayversion.csproj -- --old    (the fault: must FAIL)
// ---------------------------------------------------------------------------------------------

bool oldRead = args.Contains("--old");
Func<string, string> read = oldRead ? UpdateTrust.FileVersion : UpdateTrust.FileVersionOnDisk;
int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) pass++; else fail++;
    Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
    Console.Write(ok ? "  PASS  " : "  FAIL  ");
    Console.ResetColor();
    Console.WriteLine(what + (detail.Length > 0 ? $"  [{detail}]" : ""));
}

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var stamped = new[] { @"dist\client\RemoteSupport.Host.exe", @"dist\client\RemoteSupport.Agent.exe", @"dist\technician\RemoteSupport.Technician.exe" }
    .Select(p => Path.Combine(root, p)).FirstOrDefault(p => File.Exists(p) && AppVersion.IsStamped(UpdateTrust.FileVersion(p)));
if (stamped == null) { Console.WriteLine("No stamped build in dist\\ to swap in. Run .\\build.ps1 first."); return 2; }
var newer = UpdateTrust.FileVersion(stamped);
Console.WriteLine($"Reading with {(oldRead ? "UpdateTrust.FileVersion (the OLD read)" : "UpdateTrust.FileVersionOnDisk")}; swapping in {Path.GetFileName(stamped)} {newer}");
Console.WriteLine();

// ---- 1. the swap, done to this running program
var self = Environment.ProcessPath!;
var aside = self + ".old";
var mine = UpdateTrust.FileVersion(self);
Console.WriteLine($"This program: {self}  ({mine})");
try
{
    if (File.Exists(aside)) File.Delete(aside);
    File.Move(self, aside);                // what UpdateSwap does to the running tray app
    File.Copy(stamped, self);
    Check("a running program can be renamed aside and a newer one copied into its place", File.Exists(aside) && File.Exists(self));
    Check("Windows' own read of that path answers from the RUNNING copy (the trap is real here)",
        UpdateTrust.FileVersion(self) == mine, $"FileVersion says {UpdateTrust.FileVersion(self)}");
    string got;
    try { got = read(self); } catch (Exception ex) { got = "threw " + ex.Message; }
    Check("its own path, after the swap, reads as the NEW file's version — so the tray app restarts into it", got == newer,
        $"read {got}, the file is {newer}");
}
finally
{
    try { if (File.Exists(aside)) { if (File.Exists(self)) File.Delete(self); File.Move(aside, self); } }
    catch (Exception ex) { Console.WriteLine($"COULD NOT PUT THIS PROGRAM BACK: {ex.Message} — rebuild tools/trayversion."); }
}
Console.WriteLine();

// ---- 2. the same answer as Windows for programs that are not running
var others = new List<string>();
foreach (var dir in new[] { @"dist\client", @"dist\technician" })
    if (Directory.Exists(Path.Combine(root, dir))) others.AddRange(Directory.GetFiles(Path.Combine(root, dir), "*.exe"));
var sys = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
others.AddRange(new[] { @"System32\notepad.exe", @"System32\kernel32.dll", @"System32\shell32.dll", @"SysWOW64\notepad.exe", @"SysWOW64\kernel32.dll" }
    .Select(p => Path.Combine(sys, p)).Where(File.Exists));
foreach (var f in others)
{
    string a = UpdateTrust.FileVersion(f), b;
    try { b = read(f); } catch (Exception ex) { b = "threw " + ex.Message; }
    Check($"{Path.GetFileName(Path.GetDirectoryName(f))}\\{Path.GetFileName(f)} reads the same as Windows' answer", a == b, $"{a} / {b}");
}
Console.WriteLine();

// ---- 3. not a program: nothing, never a wrong version
var tmp = Directory.CreateTempSubdirectory("trayversion-");
try
{
    var text = Path.Combine(tmp.FullName, "brand.json");
    File.WriteAllText(text, "{\"serverUrl\":\"wss://example\"}");
    var cut = Path.Combine(tmp.FullName, "cut.exe");
    using (var src = File.OpenRead(stamped)) { var buf = new byte[1024]; int n = src.Read(buf); File.WriteAllBytes(cut, buf[..n]); }
    foreach (var (f, what) in new[] { (text, "a text file"), (cut, "a program cut off after 1 KB") })
    {
        string got;
        try { got = read(f); } catch { got = ""; }   // the tray app's tick catches and waits a minute
        Check($"{what} gives no version", got == "", got);
    }
}
finally { try { tmp.Delete(true); } catch { } }

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} passed");
return fail == 0 ? 0 : 1;
