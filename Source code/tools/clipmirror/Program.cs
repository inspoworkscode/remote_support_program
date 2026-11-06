using System.Diagnostics;
using RemoteSupport.Host;
using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// The clipboard watcher and its echo guard, on THIS laptop, with no server, no session and no
// customer machine (stage 3.5.1).
//
// tools/clipcheck proves the same feature over a real session, and it is the one that matters —
// but it needs a machine, a server and two minutes, and it cannot be run on the change that broke
// something at the moment it breaks. This can. It drives the real ClipboardAccess and asserts the
// two properties that are invisible from outside and expensive to get wrong:
//
//   IT FIRES when somebody else changes the clipboard. Without this the mirror never happens and
//   copy and paste is back to being a button.
//
//   IT DOES NOT FIRE for a change it made itself. Without this the two ends trade one string
//   between them until somebody gives up, and the customer's clipboard is overwritten repeatedly
//   with what was already on it.
//
// A separate PROCESS does the copying, not this one. A watcher that only ever sees its own
// process change the clipboard would pass while watching a clipboard nobody else can see, which
// is the window-station trap that clipcheck's header describes and the reason it exists.
//
//   clipmirror
// ---------------------------------------------------------------------------------------------

// One clipboard per machine, so one clipboard harness at a time.
//
// pastecheck and this both take the real clipboard, and on 2026-09-18 running them back to back
// produced two sets of confident failures that had nothing to do with the code: a screenshot
// landed on the clipboard mid-run, and later the two harnesses simply overwrote each other. A
// harness that cries wolf costs more than one that does not exist, because its failures get
// investigated.
using var clipboardTurn = new Mutex(false, @"Global\RemoteSupport-clipboard-harness");
bool gotTurn = false;
try { gotTurn = clipboardTurn.WaitOne(TimeSpan.FromMinutes(3)); }
catch (AbandonedMutexException) { gotTurn = true; }   // the last holder died; the clipboard is ours
if (!gotTurn)
{
    Console.WriteLine("another clipboard harness has been running for three minutes; not starting a second one.");
    return 2;
}

int passed = 0, failed = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) passed++; else failed++;
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? " — " + detail : "")}");
}

Console.WriteLine("clipmirror — the watcher and its echo guard, locally");
Console.WriteLine();

using var clip = new ClipboardAccess();

// What the watcher has seen, with the time, so a fire that arrives late is told from one that
// never arrives at all.
var fired = new List<DateTime>();
var gate = new object();
clip.Changed += () => { lock (gate) fired.Add(DateTime.UtcNow); };

int FiredSince(DateTime t) { lock (gate) return fired.Count(f => f > t); }

// Long enough for four polls at 250 ms, so a pass is a pass rather than a race won.
const int Settle = 1400;

/// Sets the clipboard from ANOTHER PROCESS. PowerShell is what a customer's machine has, needs
/// no build step, and is unmistakably not us.
bool SetFromAnotherProcess(string text)
{
    var psi = new ProcessStartInfo("powershell.exe",
        $"-NoProfile -NonInteractive -STA -Command \"Set-Clipboard -Value '{text.Replace("'", "''")}'\"")
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    try
    {
        using var p = Process.Start(psi)!;
        p.WaitForExit(20000);
        return p.ExitCode == 0;
    }
    catch (Exception ex) { Console.WriteLine("        could not start PowerShell: " + ex.Message); return false; }
}

/// Runs a fragment in a separate PowerShell and gives back what it printed. STA, because the
/// clipboard cmdlets need an apartment and a plain -Command does not get one.
string RunPs(string script)
{
    var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -STA -Command \"{script.Replace("\"", "\\\"")}\"")
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    try
    {
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(20000);
        return o;
    }
    catch (Exception ex) { return "could not run PowerShell: " + ex.Message; }
}

// Give the owner window and the first sequence read time to happen before anything is asserted.
Thread.Sleep(600);

// ---- 1. it sees somebody else copy -----------------------------------------------------------
Console.WriteLine("SOMEBODY ELSE COPIES");
var mark = $"clipmirror {Guid.NewGuid():N}";
var t0 = DateTime.UtcNow;
bool set = SetFromAnotherProcess(mark);
Check("a separate process could set the clipboard", set, set ? "" : "PowerShell returned non-zero");
Thread.Sleep(Settle);
int sawIt = FiredSince(t0);
Check("the watcher noticed", sawIt > 0, sawIt > 0 ? $"fired {sawIt} time(s) within {Settle} ms" : $"nothing in {Settle} ms");

var read = clip.GetText();
Check("and what it reads back is what that process set", read == mark,
    read == mark ? $"{mark.Length} characters" : $"read '{Trim(read)}'");

// ---- 2. it does NOT see its own write --------------------------------------------------------
// The whole of the loop prevention. This is the check that fails the day somebody moves the write
// guard, and nothing else in the suite would notice.
Console.WriteLine();
Console.WriteLine("WE COPY (this must be invisible to the watcher)");
var ours = $"clipmirror ours {Guid.NewGuid():N}";
var t1 = DateTime.UtcNow;
bool wrote = clip.SetText(ours, out var why);
Check("the host wrote the clipboard", wrote, wrote ? $"{ours.Length} characters" : why);
Thread.Sleep(Settle);
int echoes = FiredSince(t1);
Check("the watcher did NOT fire for our own write", echoes == 0,
    echoes == 0 ? $"silent for {Settle} ms, so nothing would bounce back to the technician"
                : $"fired {echoes} time(s) — this is the loop the guard exists to stop");

Check("our write really did land", clip.GetText() == ours, "");

// ---- 3. it still works afterwards ------------------------------------------------------------
// A guard that suppresses one write and then stays suppressed is worse than no guard: the mirror
// would silently stop the first time the technician pasted anything.
Console.WriteLine();
Console.WriteLine("AND IT IS STILL WATCHING AFTERWARDS");
var after = $"clipmirror after {Guid.NewGuid():N}";
var t2 = DateTime.UtcNow;
SetFromAnotherProcess(after);
Thread.Sleep(Settle);
int sawAgain = FiredSince(t2);
Check("a copy made after our own write is still noticed", sawAgain > 0,
    sawAgain > 0 ? $"fired {sawAgain} time(s)" : "the guard latched on and the mirror is dead");

// ---- 4. files and folders, measured -----------------------------------------------------------
// Clint, 2026-09-18: all of this must work for text, files AND folders. The size is what the
// threshold reads, and a wrong size is a session stalled by somebody pressing Ctrl+C.
Console.WriteLine();
Console.WriteLine("FILES AND FOLDERS");
var root = Path.Combine(Path.GetTempPath(), "clipmirror-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(Path.Combine(root, "empty"));          // the failure only a folder has
File.WriteAllText(Path.Combine(root, "one.txt"), new string('a', 1000));
File.WriteAllText(Path.Combine(root, "two.txt"), new string('b', 2000));

var t3 = DateTime.UtcNow;
bool wroteFiles = clip.SetFiles(new[] { root }, out var fileWhy);
Check("a folder can be put on the clipboard", wroteFiles, wroteFiles ? root : fileWhy);
Thread.Sleep(Settle);
Check("that write was invisible to the watcher too", FiredSince(t3) == 0, "");

var got = clip.GetFiles();
bool oneFolder = got is { Count: 1 } && string.Equals(got[0].TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
Check("and it reads back as that folder", oneFolder,
    oneFolder ? Path.GetFileName(root) : got == null ? "no file list on the clipboard" : string.Join(", ", got));

// ---- the check that matters, and the one that was missing -----------------------------------
// Reading our own CF_HDROP back with DragQueryFile proves only that we can read what we wrote.
// EXPLORER does not look at it that way: right-click Paste asks OLE for a data object, and
// "Paste is greyed out" means that question came back with nothing. So ask the same question a
// different way, from a separate process, through the shell's own route.
//
// This is the files version of the independent check clipcheck has always done for text, and
// its absence is why a file list that Explorer will not paste could pass everything above.
var seen = RunPs("$f = Get-Clipboard -Format FileDropList; if ($f) { $f | ForEach-Object { $_.FullName } } else { 'NONE' }");
bool explorerSees = seen.Contains(Path.GetFileName(root), StringComparison.OrdinalIgnoreCase);
Check("a separate process sees it the way Explorer does (Get-Clipboard -Format FileDropList)", explorerSees,
    explorerSees ? "the shell would offer Paste"
                 : $"the shell sees no file list — this is exactly 'Paste greyed out'. It reported: {seen.Trim().Replace("\r\n", " ")}");

if (oneFolder)
{
    var plan = FileSetSender.Plan(got!);
    Check("the tree measures to what is actually in it", plan.TotalBytes == 3000,
        $"{plan.TotalBytes:N0} bytes in {plan.FileCount} file(s)");
    Check("the empty folder is in the plan, not quietly dropped",
        plan.Entries.Any(e => e.IsDir && e.Path.Replace('\\', '/').EndsWith("empty")),
        "an empty directory that does not arrive is the failure only a folder can have");
    Check("3 KB is under the threshold, so it would move without being asked for",
        plan.TotalBytes <= FileSetLimits.AutoFetchBytes,
        $"{FileSetLimits.Human(plan.TotalBytes)} against {FileSetLimits.Human(FileSetLimits.AutoFetchBytes)}");
}

// ---- 5. MORE THAN ONE of them ----------------------------------------------------------------
// Reported on 2026-09-18: selecting several files and copying them put only one on the far
// machine. Everything above this line used a single path, so a CF_HDROP that carries only its
// first entry would have passed the whole suite. Three, with a space in one name and a non-ASCII
// character in another, because those are what a real desktop has on it.
Console.WriteLine();
Console.WriteLine("SEVERAL FILES AT ONCE");
var many = Path.Combine(Path.GetTempPath(), "clipmirror-many-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(many);
var three = new[]
{
    Path.Combine(many, "first.txt"),
    Path.Combine(many, "second file with spaces.txt"),
    Path.Combine(many, "thìrd é.txt"),
};
foreach (var p in three) File.WriteAllText(p, "x");

bool wroteThree = clip.SetFiles(three, out var threeWhy);
Check("three files can be put on the clipboard at once", wroteThree, wroteThree ? "" : threeWhy);

var backThree = clip.GetFiles();
Check("all three read back", backThree is { Count: 3 },
    backThree == null ? "nothing on the clipboard" : $"{backThree.Count} of 3: {string.Join(" | ", backThree.Select(Path.GetFileName))}");

// Copy, not Move. The files sit in a staging folder under the customer's temp that they never
// open, so a paste that MOVES them empties it: the first paste works, a second finds nothing,
// and there is no error and nothing to see. The drag path has set this since 2026-09-15 for the
// weaker version of the same reason; the paste path did not set it at all until 2026-09-18.
var effect = RunPs(
    "Add-Type -AssemblyName System.Windows.Forms; " +
    "$d = [Windows.Forms.Clipboard]::GetDataObject(); " +
    "$s = $d.GetData('Preferred DropEffect'); " +
    "if ($s) { $b = New-Object byte[] 4; $null = $s.Read($b,0,4); [BitConverter]::ToInt32($b,0) } else { 'ABSENT' }");
bool copyEffect = effect.Trim().StartsWith("1") || effect.Trim().StartsWith("5");
Check("and it is marked Copy, so a paste cannot empty the staging folder", copyEffect,
    copyEffect ? "Preferred DropEffect = Copy"
               : $"the shell read back '{effect.Trim()}' — a paste may offer to MOVE, and these files live in temp");

var shellSaw = RunPs("$f = Get-Clipboard -Format FileDropList; if ($f) { $f.Count } else { 0 }");
bool shellSawThree = shellSaw.Trim().StartsWith("3");
Check("and a separate process sees all three the way Explorer does", shellSawThree,
    shellSawThree ? "the shell would paste three files"
                  : $"the shell counted {shellSaw.Trim()} — a multi-file copy would arrive incomplete");

try { Directory.Delete(many, true); } catch { }
try { Directory.Delete(root, true); } catch { }

// Leave the clipboard roughly as found rather than with a marker on it.
clip.SetText("", out _);

Console.WriteLine();
Console.WriteLine(new string('-', 70));
Console.WriteLine($"{passed} passed, {failed} failed");
Console.WriteLine(failed == 0
    ? "the watcher fires for other people and stays silent for itself, for text, files and folders."
    : "the mirror is NOT proven — see the failures above.");
return failed == 0 ? 0 : 1;

static string Trim(string? s) => s == null ? "(nothing)" : s.Length <= 40 ? s : s[..40] + "…";
