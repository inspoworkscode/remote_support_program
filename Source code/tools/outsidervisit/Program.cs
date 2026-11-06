// =====================================================================================
// outsidervisit - proves outside help, step 2 (roadmap 3.9, 2026-10-06): a technician of ANOTHER
// company on the platform connects to a customer's machine with the code that customer made.
//
//   "Two signed up tenants may compliment each other in the work they do, so cross tenant option would
//   be good using the same principle. This would still need to be a multiple connection option, so
//   that the local tech can watch what is going on and terminate the connection if necessary."
//
// Three companies on a platform of their own: A owns the machine, B's technician visits, C is a
// stranger. This stands in for A's customer's tray app and for the host, so every rule is asked of the
// REAL hub: a code refused (own company, offline, too-old app, busy machine) stays UNSPENT; the person
// is ALWAYS asked, by name and company; ONLY that visitor may open the session from outside — not a
// colleague of theirs, not a stranger; what the visitor may send and see is an allow-list both ways
// (screen and control; no clipboard, files, scripts, chat or sound); A's consoles are told and may
// watch and end it; leaving ends it; switching outside help off ends it; both companies keep a record.
//
//   dotnet run -c Debug --project tools/outsidervisit/outsidervisit.csproj
// =====================================================================================

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}

var root = AppContext.BaseDirectory;
while (root != null && !File.Exists(Path.Combine(root, "CLAUDE.md"))) root = Path.GetDirectoryName(root);
if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }
var dataDir = Path.Combine(Path.GetTempPath(), "rs-outsidervisit-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dataDir);
const string Pw = "outsider-visit-2026";
const string Sue = "Sue Tech", Sam = "Sam Supervisor", Vic = "Victor Visitor", Bea = "Bea Colleague", Carl = "Carl Stranger";
const string ACo = "Anywhere Test IT", BCo = "Smith & Co IT", CCo = "Stranger IT";
string VicLabel = $"{Vic} ({BCo})";
Process? server = null;
try
{
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
    await using (var db = new PlatformDb(options)) await HarnessDb.MigrateAsync(db);
    var tenants = new TenantService(new Factory(options), new ConfigurationBuilder().Build(), dataDir, DataProtectionProvider.Create("outsidervisit"));
    var (a, _) = await tenants.CreateAsync(ACo, "a-co", 7);
    await tenants.AddTechnicianAsync(a!.Id, "sue@a.test", Pw, Sue, TenantRole.Technician);
    await tenants.AddTechnicianAsync(a.Id, "sam@a.test", Pw, Sam, TenantRole.Admin);
    var (b, _) = await tenants.CreateAsync(BCo, "b-co", 7);
    await tenants.AddTechnicianAsync(b!.Id, "vic@b.test", Pw, Vic, TenantRole.Technician);
    await tenants.AddTechnicianAsync(b.Id, "bea@b.test", Pw, Bea, TenantRole.Technician);
    var (c, _) = await tenants.CreateAsync(CCo, "c-co", 7);
    await tenants.AddTechnicianAsync(c!.Id, "carl@c.test", Pw, Carl, TenantRole.Technician);
    // Both A and B switch outside help on for the whole company.
    await tenants.UpdateBrandingAsync(a.Id, t => t.OutsidersAllowed = true);
    await tenants.UpdateBrandingAsync(b.Id, t => t.OutsidersAllowed = true);
    async Task<OutsiderCode?> CodeRow(string deviceId) { await using var db = new PlatformDb(options); return await db.OutsiderCodes.Where(x => x.DeviceId == deviceId).OrderBy(x => x.Id).LastOrDefaultAsync(); }

    var port = FreePort();
    var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var arg in new[] { "run", "-c", "Debug", "--project", Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj"), "--", "run" })
        psi.ArgumentList.Add(arg);
    psi.Environment["RSP_Platform__DataDirectory"] = dataDir;
    HarnessDb.Use(psi.Environment, dataDir);
    psi.Environment["RSP_Security__RequireTwoFactor"] = "false";   // a throwaway platform: the password alone (HANDOVER §72)
    psi.Environment["RSP_Urls"] = $"http://127.0.0.1:{port}";
    psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
    server = Process.Start(psi)!;
    server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    if (!await WaitForPort(port, 90000)) { Console.WriteLine("The platform never started listening."); return 1; }
    Console.WriteLine($"outsidervisit - another company connects with a code; a platform of its own on port {port}");

    var (sue, sueTok) = await Console_(port, "sue@a.test");
    var (sam, _) = await Console_(port, "sam@a.test");
    var (vic, vicTok) = await Console_(port, "vic@b.test");
    var (_, beaTok) = await Console_(port, "bea@b.test");
    var (carl, carlTok) = await Console_(port, "carl@c.test");
    var ann = Id("ann"); var tray = await Device(port, ann, DeviceRole.Client, a.TenantKey, "ANN-PC", DeviceCan.AlwaysAsk);
    var bob = Id("bob"); var bTray = await Device(port, bob, DeviceRole.Client, b.TenantKey, "BOB-PC", DeviceCan.AlwaysAsk);
    var old = Id("old"); var oldTray = await Device(port, old, DeviceRole.Client, a.TenantKey, "OLD-PC");   // an older tray app: says nothing
    var gone = Id("gone"); var goneTray = await Device(port, gone, DeviceRole.Client, a.TenantKey, "GONE-PC", DeviceCan.AlwaysAsk);
    await Task.Delay(800);

    async Task<string> Code(Peer t, string forWhom)
    {
        t.Ws.Send(new OutsiderCodeRequestMsg { For = forWhom });
        return (await t.NextWhere<OutsiderCodeMsg>(m => m.Code.Length > 0))?.Code ?? "";
    }

    // =========================================================== refused, and the code kept
    Console.WriteLine();
    Console.WriteLine("== a code refused is a code kept ==");
    vic.Ws.Send(new OutsiderConnectMsg { Code = "000000001" });
    Check("a code that does not exist: refused, in words", (await vic.Next<ErrorMsg>())?.Message.Contains("does not work") == true);

    var own = await Code(bTray, "Bea");
    vic.Ws.Send(new OutsiderConnectMsg { Code = own });
    var ownErr = await vic.Next<ErrorMsg>();
    Check("a code for one of his OWN company's machines: refused — that is what Connect is for", ownErr?.Message.Contains("own company") == true, ownErr?.Message ?? "nothing");
    Check("...and that code is NOT spent", (await CodeRow(bob.DeviceId))?.UsedUtc == null);

    var oldCode = await Code(oldTray, "Smith & Co");
    Check("(an older tray app could still make a code here — it cannot in the real world, but the server must not trust that)", oldCode.Length == 9);
    vic.Ws.Send(new OutsiderConnectMsg { Code = oldCode });
    var oldErr = await vic.Next<ErrorMsg>();
    Check("a machine whose app might not ASK (no always-ask): refused, \"too old\"", oldErr?.Message.Contains("too old") == true, oldErr?.Message ?? "nothing");
    Check("...and the code is not spent", (await CodeRow(old.DeviceId))?.UsedUtc == null);

    var goneCode = await Code(goneTray, "Smith & Co");
    await goneTray.Ws.CloseAsync("probe: laptop asleep");
    await Task.Delay(800);
    vic.Ws.Send(new OutsiderConnectMsg { Code = goneCode });
    var goneErr = await vic.Next<ErrorMsg>();
    Check("a machine that has gone offline: refused, \"not connected\"", goneErr?.Message.Contains("not connected") == true, goneErr?.Message ?? "nothing");
    Check("...and the code is not spent — the customer need not make another", (await CodeRow(gone.DeviceId))?.UsedUtc == null);

    // =========================================================== the visit
    Console.WriteLine();
    Console.WriteLine("== Victor of Smith & Co connects to ANN-PC ==");
    var code1 = await Code(tray, "Smith & Co Accountants");
    sue.Clear(); sam.Clear(); vic.Clear(); tray.Clear();
    vic.Ws.Send(new OutsiderConnectMsg { Code = OutsiderAccess.Format(code1) });
    var start = await tray.NextWhere<StartSessionMsg>(m => m.HostToken.Length > 0);
    Check("ANN-PC's support app is told to host — and to ASK, whatever the branding says",
        start is { AskConsent: true } && start.TechnicianName == VicLabel && start.VisitorCompany == BCo,
        start == null ? "nothing" : $"ask={start.AskConsent}, '{start.TechnicianName}', company '{start.VisitorCompany}'");
    var mine = await vic.NextWhere<SessionReadyMsg>(m => m.Session.SessionId == start!.SessionId);
    Check("Victor's console hears of it: started by HIM, by his own name, marked as visiting",
        mine is { Session: { StartedBy: Vic, Visiting: true, Visitor: "" } }, mine == null ? "nothing" : $"{mine.Session.StartedBy} / visiting {mine.Session.Visiting}");
    var aSees = await sue.NextWhere<SessionReadyMsg>(m => m.Session.SessionId == start!.SessionId);
    Check("A's consoles see it labelled with his company — never a bare name one of theirs might share",
        aSees is { Session: { Visiting: false } } && aSees.Session.StartedBy == VicLabel && aSees.Session.Visitor == VicLabel);
    var pop = await sam.Next<OutsiderVisitMsg>();
    Check("A's consoles are told at once: who, which machine, whose code",
        pop is { MachineName: "ANN-PC", ForWhom: "Smith & Co Accountants" } && pop.Visitor == VicLabel && pop.MadeBy.Contains("ANN-PC"),
        pop == null ? "nothing" : $"{pop.Visitor} / {pop.MachineName} / {pop.ForWhom} / {pop.MadeBy}");
    Check("...and Victor's console is NOT sent A's pop-up", await vic.Next<OutsiderVisitMsg>(800) == null);
    Check("Carl's console (another company) hears nothing at all", await carl.Next<SessionReadyMsg>(500) == null);
    var spent = await CodeRow(ann.DeviceId);
    Check("the code is spent, and says by whom", spent is { UsedBy: var ub } && ub == VicLabel && spent.UsedUtc != null);
    vic.Ws.Send(new OutsiderConnectMsg { Code = code1 });
    Check("...it does not work twice", (await vic.Next<ErrorMsg>())?.Message.Contains("does not work") == true);

    var sid = start!.SessionId;
    var host = await Peer.Open($"ws://127.0.0.1:{port}/ws/session?role=host&id={sid}&token={start.HostToken}&consent=asking&secs=60");
    var waiting = await vic.NextWhere<SessionReadyMsg>(m => m.Session.AwaitingConsent);
    Check("the host is asking the person: Victor's console is told he is waiting", waiting != null);
    // A viewer that tries to get in BEFORE the yes sees nothing and gets nothing through.
    host.Ws.Send(new ConsentGrantedMsg());
    var allowed = await vic.NextWhere<SessionReadyMsg>(m => m.Session.HostConnected && !m.Session.AwaitingConsent);
    Check("the person allows it: Victor's console is told he is in", allowed != null);
    host.Ws.Send(new MonitorsMsg { Monitors = new List<MonitorInfo> { new() { Index = 0, Width = 1920, Height = 1080, Primary = true } } });
    await Task.Delay(300);

    // ---- who may open the session ----
    var vicView = await Peer.TryOpen($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={sid}&token={vicTok}");
    Check("VICTOR opens the session — from another company, let in", vicView != null);
    Check("...and is shown the screen's layout", vicView != null && await vicView.Next<MonitorsMsg>() != null);
    var beaView = await Peer.TryOpen($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={sid}&token={beaTok}");
    Check("his COLLEAGUE Bea, same company, cannot — the code let in one person", beaView == null);
    var carlView = await Peer.TryOpen($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={sid}&token={carlTok}");
    Check("Carl, a stranger company, cannot", carlView == null);
    var sueView = await Peer.TryOpen($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={sid}&token={sueTok}");
    Check("Sue, A's technician, CAN — Watch", sueView != null);
    var parts = await host.NextWhere<ParticipantsMsg>(p => p.Technicians.Count >= 2);
    Check("the host sees both, Victor by his company", parts != null && parts.Technicians.Contains(VicLabel) && parts.Technicians.Contains(Sue),
        parts == null ? "nothing" : string.Join(", ", parts.Technicians));
    if (vicView == null || sueView == null) throw new Exception("cannot go on without both viewers");
    vicView.Clear(); sueView.Clear(); host.Clear();

    // ---- what Victor may send ----
    Console.WriteLine();
    Console.WriteLine("== what the visitor may send: screen and control ==");
    vicView.Ws.Send(new MouseMsg { Monitor = 0 });
    vicView.Ws.Send(new KeyMsg { Vk = 65, Down = true });
    vicView.Ws.Send(new SubscribeMsg());
    Check("his mouse reaches the machine", await host.Next<MouseMsg>() != null);
    Check("...and his keyboard", await host.Next<KeyMsg>() != null);
    Check("...and his request for the picture", await host.Next<SubscribeMsg>() != null);
    vicView.Ws.Send(new ClipboardMsg { Text = "pasted by the visitor" });
    vicView.Ws.Send(new FileListRequestMsg { Path = "C:\\" });
    vicView.Ws.Send(new ScriptRunMsg { RunId = "x", Script = "Get-ChildItem" });
    vicView.Ws.Send(new SysActionMsg { Action = "reboot" });
    vicView.Ws.Send(new VoiceTalkMsg { On = true });
    vicView.Ws.Send(new ChatMsg { Text = "hello from the visitor" });
    vicView.Ws.Send(new RemotePrintMsg { Technician = "Victor", On = true });
    vicView.Ws.Send(new PrintFileMsg { JobId = "v", Path = "C:/x.pdf" });
    vicView.Ws.SendBinary(Bin.FileChunk("f1", 0, new byte[] { 1, 2, 3 }));
    await Task.Delay(1200);
    Check("his clipboard does NOT reach the machine", host.Count<ClipboardMsg>() == 0);
    Check("...nor a request for its files", host.Count<FileListRequestMsg>() == 0);
    Check("...nor a script", host.Count<ScriptRunMsg>() == 0);
    Check("...nor a restart", host.Count<SysActionMsg>() == 0);
    Check("...nor his voice", host.Count<VoiceTalkMsg>() == 0);
    Check("...nor a file chunk", host.BinaryCount == 0);
    Check("...and his chat does not reach the customer's tray app", tray.Count<ChatMsg>() == 0);
    Check("...nor a printer of his on their machine, nor a print of his file (remote printing)", host.Count<RemotePrintMsg>() == 0 && host.Count<PrintFileMsg>() == 0);
    sueView.Ws.Send(new ClipboardMsg { Text = "Sue's" });
    Check("Sue (A) still can — the limits are the visitor's alone", await host.Next<ClipboardMsg>() != null);

    // ---- what Victor is shown ----
    Console.WriteLine();
    Console.WriteLine("== what the visitor is shown: the screen and the pointer ==");
    vicView.Clear(); sueView.Clear();
    host.Ws.SendBinary(new byte[] { BinKind.Video, 0, 0, 0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0 });
    host.Ws.SendBinary(new byte[] { BinKind.Cursor, 1, 2, 3 });
    host.Ws.SendBinary(Bin.FileChunk("pulled-by-sue", 0, new byte[] { 9, 9, 9 }));
    host.Ws.SendBinary(new byte[] { BinKind.Audio, 1, 2, 3 });
    host.Ws.Send(new ClipboardMsg { Text = "the customer's clipboard" });
    host.Ws.Send(new FileListMsg { Path = "C:\\" });
    host.Ws.Send(new CursorMsg());
    host.Ws.Send(new RemotePrintJobMsg { JobId = "j", Technician = "Sue", Document = "payslip", Path = "C:/job-1.pdf" });
    host.Ws.Send(new RemotePrintStateMsg { Technician = "Sue", On = true, Printer = "Sue's printer" });
    tray.Ws.Send(new ChatMsg { Text = "the customer says hello to their IT company" });
    await Task.Delay(1500);
    Check("Victor gets the picture and the pointer", vicView.BinaryKinds.Contains(BinKind.Video) && vicView.BinaryKinds.Contains(BinKind.Cursor),
        string.Join(",", vicView.BinaryKinds));
    Check("...but NOT the file Sue pulled, nor the machine's sound", !vicView.BinaryKinds.Contains(BinKind.FileChunk) && !vicView.BinaryKinds.Contains(BinKind.Audio));
    Check("...nor the customer's clipboard, nor a file list", vicView.Count<ClipboardMsg>() == 0 && vicView.Count<FileListMsg>() == 0);
    Check("...nor the customer's chat with their own IT company", vicView.Count<ChatMsg>() == 0);
    Check("...nor what the customer printed for Sue, nor her printer (remote printing)", vicView.Count<RemotePrintJobMsg>() == 0 && vicView.Count<RemotePrintStateMsg>() == 0);
    Check("...but the pointer's shape, yes", vicView.Count<CursorMsg>() == 1);
    Check("Sue gets all of it", sueView.BinaryKinds.Contains(BinKind.FileChunk) && sueView.Count<ClipboardMsg>() == 1 && sueView.Count<ChatMsg>() == 1);

    // ---- the records ----
    Console.WriteLine();
    Console.WriteLine("== the records ==");
    await using (var db = new PlatformDb(options))
    {
        var t = await db.Tickets.Where(x => x.TenantId == a.Id && x.DeviceId == ann.DeviceId).OrderBy(x => x.Id).LastOrDefaultAsync();
        // Sue watched, and whoever of A's joins a session goes on its ticket (§49). Victor never does.
        Check("A has a ticket for it with Victor NOT on it — he is not one of A's people; Sue, who watched, is",
            t != null && t.Subject.Contains(VicLabel) && !t.AssignedTo.Contains(Vic) && t.AssignedTo == Sue, t == null ? "none" : $"'{t.Subject}' / '{t.AssignedTo}'");
        var notes = t == null ? new List<TicketNote>() : await db.TicketNotes.Where(n => n.TicketId == t.Id).Select(n => n).ToListAsync();
        Check("...saying who, with whose code, for whom", notes.Any(n => n.Body.Contains("with a code") && n.Body.Contains("Smith & Co Accountants")));
        Check("...and that the person allowed it — without files, as they did not tick it", notes.Any(n => n.Body.Contains("allowed the connection, without files")));
        var bRow = await db.Sessions.FirstOrDefaultAsync(x => x.TenantId == b.Id && x.SessionId == sid);
        Check("B has its own line on its Sessions page", bRow != null && bRow.StartedBy == Vic && bRow.MachineName.Contains(ACo), bRow?.MachineName ?? "none");
    }

    // ---- A ends it ----
    Console.WriteLine();
    Console.WriteLine("== the local technician ends it ==");
    sam.Ws.Send(new EndSessionMsg { SessionId = sid });
    var endedV = await vicView.Next<SessionEndedMsg>();
    Check("Sam (A) presses End: Victor's window is told it ended, and by whom", endedV?.Reason.Contains(Sam) == true, endedV?.Reason ?? "nothing");
    Check("...Victor's console hears it is over", await vic.NextWhere<SessionReadyMsg>(m => m.Session.SessionId == sid && m.Session.Ended) != null);
    await Task.Delay(800);
    await using (var db = new PlatformDb(options))
        Check("...and B's record has the end", (await db.Sessions.FirstOrDefaultAsync(x => x.TenantId == b.Id && x.SessionId == sid))?.EndedUtc != null);
    var gone2 = await Peer.TryOpen($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={sid}&token={vicTok}");
    Check("he cannot get back into the ended session", gone2 == null || await gone2.Next<SessionEndedMsg>() != null);

    // =========================================================== his leaving ends it
    Console.WriteLine();
    Console.WriteLine("== when the visitor leaves, the session goes — watchers or not ==");
    var (sid2, host2) = await Visit(tray, vic, port, "Smith & Co Accountants");
    var v2 = await Peer.TryOpen($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={sid2}&token={vicTok}");
    var s2 = await Peer.TryOpen($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={sid2}&token={sueTok}");
    await Task.Delay(400);
    v2!.Ws.Send(new EndSessionMsg { SessionId = sid2 });
    var endS = await s2!.Next<SessionEndedMsg>();
    Check("Victor ends it with Sue still watching: it ENDS — Sue is not left holding his session", endS?.Reason.Contains(VicLabel) == true, endS?.Reason ?? "nothing");

    // =========================================================== switching it off ends it
    Console.WriteLine();
    Console.WriteLine("== switching outside help off ends a visit ==");
    var (sid3, host3) = await Visit(tray, vic, port, "Smith & Co Accountants");
    var v3 = await Peer.TryOpen($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={sid3}&token={vicTok}");
    await Task.Delay(300);
    sam.Ws.Send(new DeviceOutsiderModeMsg { DeviceId = ann.DeviceId, Mode = OutsiderAccess.Off });
    var off = await v3!.Next<SessionEndedMsg>();
    Check("Sam switches ANN-PC Off: Victor's session ends at once, saying so", off?.Reason.Contains("switched off") == true, off?.Reason ?? "nothing");
    sam.Ws.Send(new DeviceOutsiderModeMsg { DeviceId = ann.DeviceId, Mode = OutsiderAccess.AsCompany });
    await tray.NextWhere<OutsiderStateMsg>(s => s.Allowed);

    // =========================================================== files, when the person ticks it
    Console.WriteLine();
    Console.WriteLine("== files: only when the person ticks it ==");
    var code6 = await Code(tray, "Smith & Co Accountants");
    tray.Clear(); vic.Clear(); sam.Clear();
    vic.Ws.Send(new OutsiderConnectMsg { Code = code6 });
    var start6 = (await tray.NextWhere<StartSessionMsg>(m => m.HostToken.Length > 0))!;
    var sid6 = start6.SessionId;
    var host6 = await Peer.Open($"ws://127.0.0.1:{port}/ws/session?role=host&id={sid6}&token={start6.HostToken}&consent=asking&secs=60");
    await Task.Delay(300);
    host6.Ws.Send(new ConsentGrantedMsg { SessionId = sid6, Files = true });
    var ticked = await vic.NextWhere<SessionReadyMsg>(m => m.Session.SessionId == sid6 && m.Session.HostConnected && !m.Session.AwaitingConsent);
    Check("the person allows it AND ticks files: Victor's console is told files are his", ticked?.Session.VisitorFiles == true);
    Check("...and so are A's consoles", (await sam.NextWhere<SessionReadyMsg>(m => m.Session.SessionId == sid6 && m.Session.HostConnected))?.Session.VisitorFiles == true);
    var v6 = await Peer.TryOpen($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={sid6}&token={vicTok}");
    await Task.Delay(400);
    host6.Clear();
    v6!.Ws.Send(new FileListRequestMsg { Path = "C:\\" });
    v6.Ws.Send(new FileSetPullMsg());
    v6.Ws.SendBinary(Bin.FileChunk("up", 0, new byte[] { 1, 2, 3 }));
    v6.Ws.Send(new ScriptRunMsg { RunId = "x", Script = "whoami" });
    v6.Ws.Send(new ClipboardMsg { Text = "still not" });
    v6.Ws.Send(new RemotePrintMsg { Technician = "Victor", On = true });
    v6.Ws.SendBinary(new byte[] { BinKind.Voice, 0, 0, 0, 0 });
    await Task.Delay(1200);
    Check("now his folder request and his pull reach the machine", host6.Count<FileListRequestMsg>() == 1 && host6.Count<FileSetPullMsg>() == 1);
    Check("...and his file chunk", host6.BinaryKinds.SequenceEqual(new[] { BinKind.FileChunk }), string.Join(",", host6.BinaryKinds));
    Check("...but still NOT a script, the clipboard or his voice — files are files", host6.Count<ScriptRunMsg>() == 0 && host6.Count<ClipboardMsg>() == 0);
    Check("...nor a printer of his — printing is not files", host6.Count<RemotePrintMsg>() == 0);
    v6.Clear();
    host6.Ws.Send(new FileListMsg { Path = "C:\\" });
    host6.Ws.SendBinary(Bin.FileChunk("down", 0, new byte[] { 4, 5, 6 }));
    host6.Ws.Send(new ClipboardMsg { Text = "the customer's clipboard" });
    host6.Ws.SendBinary(new byte[] { BinKind.Audio, 1, 2, 3 });
    await Task.Delay(1200);
    Check("the machine's file list and its file chunks reach him", v6.Count<FileListMsg>() == 1 && v6.BinaryKinds.Contains(BinKind.FileChunk));
    Check("...but still not the customer's clipboard or the machine's sound", v6.Count<ClipboardMsg>() == 0 && !v6.BinaryKinds.Contains(BinKind.Audio));
    await using (var db = new PlatformDb(options))
    {
        var t6 = await db.TicketNotes.Where(n => n.Body.Contains("ticked that they may copy files")).CountAsync();
        Check("the ticket says the person ticked files", t6 >= 1);
    }
    v6.Ws.Send(new EndSessionMsg { SessionId = sid6 });
    await vic.NextWhere<SessionReadyMsg>(m => m.Session.SessionId == sid6 && m.Session.Ended);

    // =========================================================== declined, and a busy machine
    Console.WriteLine();
    Console.WriteLine("== the person says no; a busy machine ==");
    var code4 = await Code(tray, "Smith & Co Accountants");
    tray.Clear(); vic.Clear();
    vic.Ws.Send(new OutsiderConnectMsg { Code = code4 });
    var start4 = await tray.NextWhere<StartSessionMsg>(m => m.HostToken.Length > 0);
    var host4 = await Peer.Open($"ws://127.0.0.1:{port}/ws/session?role=host&id={start4!.SessionId}&token={start4.HostToken}&consent=asking&secs=60");
    var code5 = await Code(tray, "Somebody else");   // made while the first visit is waiting
    carl.Ws.Send(new OutsiderConnectMsg { Code = code5 });
    var busy = await carl.Next<ErrorMsg>();
    Check("while somebody is connected, a second code is refused — joining would skip the Allow", busy?.Message.Contains("already connected") == true, busy?.Message ?? "nothing");
    Check("...and that code is not spent", (await CodeRow(ann.DeviceId))?.UsedUtc == null);
    host4.Ws.Send(new DeclineSessionMsg { SessionId = start4.SessionId, Reason = "The person at ANN-PC declined." });
    var declined = await vic.NextWhere<SessionReadyMsg>(m => m.Session.SessionId == start4.SessionId && m.Session.Ended);
    Check("the person presses Deny: Victor's console hears it ended, and why", declined?.Session.EndReason.Contains("declined") == true, declined?.Session.EndReason ?? "nothing");
}
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex); }
finally
{
    try { if (server is { HasExited: false }) server.Kill(true); } catch { }
    await Task.Delay(500);
    try { Directory.Delete(dataDir, true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
return fail == 0 ? 0 : 1;

// A visit started, the person asked and allowed, and the host announcing its screen.
static async Task<(string Sid, Peer Host)> Visit(Peer tray, Peer visitor, int port, string forWhom)
{
    tray.Ws.Send(new OutsiderCodeRequestMsg { For = forWhom });
    var code = (await tray.NextWhere<OutsiderCodeMsg>(m => m.Code.Length > 0))!.Code;
    tray.Clear();
    visitor.Ws.Send(new OutsiderConnectMsg { Code = code });
    var start = (await tray.NextWhere<StartSessionMsg>(m => m.HostToken.Length > 0))!;
    var host = await Peer.Open($"ws://127.0.0.1:{port}/ws/session?role=host&id={start.SessionId}&token={start.HostToken}&consent=asking&secs=60");
    await Task.Delay(300);
    host.Ws.Send(new ConsentGrantedMsg());
    await Task.Delay(300);
    return (start.SessionId, host);
}

static DeviceIdentity Id(string name) => new() { DeviceId = name + "-" + Guid.NewGuid().ToString("N")[..6], DeviceKey = Guid.NewGuid().ToString("N") };

static async Task<(Peer, string)> Console_(int port, string email)
{
    using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    var lr = await hc.PostAsJsonAsync($"http://127.0.0.1:{port}/api/login", new { email, password = "outsider-visit-2026" });
    lr.EnsureSuccessStatusCode();
    var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
    var p = await Peer.Open($"ws://127.0.0.1:{port}/ws/tech?token={Uri.EscapeDataString(tok)}");
    await Task.Delay(400); p.Clear();
    return (p, Uri.EscapeDataString(tok));
}

static async Task<Peer> Device(int port, DeviceIdentity id, DeviceRole role, string tenantKey, string machine, params string[] can)
{
    var p = await Peer.Open($"ws://127.0.0.1:{port}/ws/device");
    p.Ws.Send(new HelloMsg
    {
        Role = role, DeviceId = id.DeviceId, DeviceKey = id.DeviceKey, MachineName = machine,
        UserName = "probe", Os = "probe", Version = "1.0", TenantKey = tenantKey, Can = can.ToList()
    });
    return p;
}

static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

static async Task<bool> WaitForPort(int port, int ms)
{
    var until = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < until)
    {
        try { using var c = new TcpClient(); await c.ConnectAsync("127.0.0.1", port); return true; }
        catch { await Task.Delay(400); }
    }
    return false;
}

/// <summary>One socket and everything it has been sent; each Next takes the oldest match.</summary>
file sealed class Peer
{
    public WsConnection Ws { get; private set; } = null!;
    private readonly List<Msg> _got = new();
    private readonly List<byte> _kinds = new();
    public void Clear() { lock (_got) { _got.Clear(); _kinds.Clear(); } }
    public int Count<T>() where T : Msg { lock (_got) return _got.OfType<T>().Count(); }
    public int BinaryCount { get { lock (_got) return _kinds.Count; } }
    public List<byte> BinaryKinds { get { lock (_got) return _kinds.ToList(); } }

    public static async Task<Peer> Open(string url) => await TryOpen(url) ?? throw new InvalidOperationException("could not open " + url);

    /// <summary>Null when the server refuses the socket — the 401 that keeps an outsider out.</summary>
    public static async Task<Peer?> TryOpen(string url)
    {
        try
        {
            var cws = new ClientWebSocket();
            await cws.ConnectAsync(new Uri(url), CancellationToken.None);
            var p = new Peer { Ws = new WsConnection(cws) };
            p.Ws.OnMessage += m => { lock (p._got) p._got.Add(m); };
            p.Ws.OnBinary += b => { lock (p._got) p._kinds.Add(b.Length > 0 ? b.Span[0] : (byte)0); };
            _ = p.Ws.RunAsync(CancellationToken.None);
            return p;
        }
        catch { return null; }
    }

    public Task<T?> Next<T>(int ms = 8000) where T : Msg => NextWhere<T>(_ => true, ms);

    public async Task<T?> NextWhere<T>(Func<T, bool> ok, int ms = 8000) where T : Msg
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            lock (_got)
            {
                var m = _got.OfType<T>().FirstOrDefault(ok);
                if (m != null) { _got.Remove(m); return m; }
            }
            await Task.Delay(40);
        }
        return null;
    }
}

file sealed class Factory : IDbContextFactory<PlatformDb>
{
    private readonly DbContextOptions<PlatformDb> _o;
    public Factory(DbContextOptions<PlatformDb> o) => _o = o;
    public PlatformDb CreateDbContext() => new(_o);
}
