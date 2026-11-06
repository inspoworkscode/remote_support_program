using System.Runtime.InteropServices;
using RemoteSupport.Host;

// RemoteSupport.Host.exe --server wss://host:port --session <id> --token <hostToken>
// Runs one screen-sharing session as the host (capture + input + files + scripts).

[DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
[DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

string? Get(string name)
{
    for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
    return null;
}

// RemoteSupport.Host.exe --put-files <list.txt>
// The one thing the host must NOT do as SYSTEM: put a file list on the clipboard. Explorer will
// not paste from a list owned by a process above it, so the SYSTEM host starts this copy of
// itself as the logged-on user to do that write, waits for it, and reads its exit code. See
// ClipboardAccess.SetFilesAsUser and UserProcess for the measurement behind it.
var putFiles = Get("--put-files");
if (putFiles != null)
{
    try { ShowWindow(GetConsoleWindow(), 0); } catch { }
    return ClipboardAccess.RunPutFilesHelper(putFiles);
}

// RemoteSupport.Host.exe --get-files <list.txt>
// The mirror of --put-files, and needed for the same reason. When the person at the machine
// copies files, Explorer puts a data object on the clipboard that renders the list on demand in
// its own process, and that rendering does not cross to another account: from SYSTEM the list is
// simply not there. So the host asks a copy of itself running as that person.
var getFiles = Get("--get-files");
if (getFiles != null)
{
    try { ShowWindow(GetConsoleWindow(), 0); } catch { }
    return ClipboardAccess.RunGetFilesHelper(getFiles);
}

// RemoteSupport.Host.exe --get-selection <answer.json>
// What the person has selected in the folder window in front of them, for a technician dragging
// files OFF the picture (roadmap 3.5.2). As that person, for the same reason as --get-files:
// from SYSTEM, Explorer's windows are not there at all.
var getSelection = Get("--get-selection");
if (getSelection != null)
{
    try { ShowWindow(GetConsoleWindow(), 0); } catch { }
    return ExplorerSelection.RunHelper(getSelection);
}

// RemoteSupport.Host.exe --print-file <file> <answer.json>
// A technician's file on the person's printer (remote printing, 2026-10-06). As that person: the
// default printer is theirs, per user, and as SYSTEM there is often none at all. The answer — which
// printer, how many pages, or why not — is written for the host to read back.
var printFile = Get("--print-file");
if (printFile != null)
{
    try { ShowWindow(GetConsoleWindow(), 0); } catch { }
    var answerAt = args.Length > Array.IndexOf(args, "--print-file") + 2 ? args[Array.IndexOf(args, "--print-file") + 2] : "";
    var r = RemoteSupport.Media.DocumentPrinter.Print(printFile, null, Path.GetFileName(printFile));
    try { if (answerAt.Length > 0) File.WriteAllText(answerAt, RemoteSupport.Shared.Json.Serialize(r)); } catch { }
    return r.Ok ? 0 : 1;
}

var server = Get("--server");
var session = Get("--session");
var token = Get("--token");
if (server == null || session == null || token == null)
{
    Console.Error.WriteLine("Usage: RemoteSupport.Host --server <ws-url> --session <id> --token <hostToken> [--consent ask|auto] [--tech \"Name\"]");
    return 2;
}
bool askConsent = string.Equals(Get("--consent"), "ask", StringComparison.OrdinalIgnoreCase);
var techName = Get("--tech") ?? "A technician";
// Somebody from ANOTHER company, let in with a code (3.9): their company, for the prompt.
var visitorCompany = Get("--visitor") ?? "";
// "Being controlled remotely" on screen for the whole session (the free version, 2026-10-06).
bool showNotice = args.Contains("--notice");

try { ShowWindow(GetConsoleWindow(), 0); } catch { } // hide console

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var engine = new HostEngine(server, session, token, askConsent, techName, visitorCompany, showNotice);
try { await engine.RunAsync(cts.Token); return 0; }
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
