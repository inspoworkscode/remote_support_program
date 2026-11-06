// =====================================================================================
// helpcheck - proves outside help asked for by a TECHNICIAN (2026-10-22, HANDOVER §155).
//
//   "What would be nice is for a technician to also be able to ask for outside help. In the console for
//   example under tools when connected to a machine, there is a get outside help option. This generates a
//   code, plus links to the browser and guest account download ... Sometimes it happens that a tech needs
//   the outside assistance rather than the client."  Then: "the customer should be told."
//
// Two companies and a guest on a platform of their own: A's technician Sue, in a session on ANN-PC, makes
// a code; B's technician Victor, or a guest, types it; Sue lets them in. This stands in for ANN-PC's tray
// app and host, so every rule is asked of the REAL hub: only a technician IN the session may make one; it
// works for that session only; whoever types it waits for SUE — not for the person at the machine, who is
// TOLD and never asked; only the maker may answer; the same allow-lists as §89; files only on Sue's tick;
// their leaving does NOT end the session; Remove takes them out; nobody of A left takes them out; Sue's
// Leave with only them left ends it; the code stops when the session ends or its maker leaves; the
// company's switch, from the portal, stops and removes; the customer's own code is untouched by all of it.
//
//   dotnet run -c Debug --project tools/helpcheck/helpcheck.csproj
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

// ---- the words and links, in-process ----
Console.WriteLine("== the message a technician sends ==");
{
    var text = OutsideHelpMessage.Text("https://example.test/", "123456789", "Pieter", "Sue Tech", "Anywhere Test IT");
    Check("it says who is asking, and greets who it is for", text.StartsWith("Hello Pieter,") && text.Contains("Sue Tech from Anywhere Test IT"));
    Check("the code, read in threes, and how long it lasts", text.Contains("Code: 123 456 789") && text.Contains("10 minutes"));
    Check("the browser link carries the code after a # — never sent to a server", text.Contains("https://example.test/guest/web#123456789"));
    Check("the guest program's page", text.Contains("https://example.test/guest\r\n"));
    Check("and the way in for a company already on the platform", text.Contains("Connect with a code"));
    var wa = OutsideHelpMessage.WhatsAppUrl(text);
    Check("WhatsApp: wa.me with the whole message encoded", wa.StartsWith("https://wa.me/?text=Hello%20Pieter%2C") && !wa.Contains(' ') && wa.Contains("%23123456789"));
    var mail = OutsideHelpMessage.MailUrl(text);
    Check("email: a mailto with a subject and the message", mail.StartsWith("mailto:?subject=") && mail.Contains("&body=Hello%20Pieter") && !mail.Contains('\n'));
    // The customer's own code sends the same message (2026-10-22): signed with their name, from the tray app's socket address.
    Check("the tray app's server address becomes the web address: wss → https, ws → http, any path dropped",
        OutsideHelpMessage.WebBase("wss://theremotesupportprogram.com/ws") == "https://theremotesupportprogram.com"
        && OutsideHelpMessage.WebBase("ws://192.168.1.10:8090") == "http://192.168.1.10:8090" && OutsideHelpMessage.WebBase("nonsense") == "");
    var fromCustomer = OutsideHelpMessage.Text(OutsideHelpMessage.WebBase("wss://example.test"), "987654321", "Smith & Co Accountants", "Annabel", "");
    Check("the customer's message: their name, the code, the browser link", fromCustomer.Contains("Annabel is asking for your help")
        && fromCustomer.Contains("Code: 987 654 321") && fromCustomer.Contains("https://example.test/guest/web#987654321"));
    var joined = new OutsiderJoinedMsg { Visitor = "Victor (Smith)", By = "Sue Tech" };
    Check("what the customer is told names who and at whose request", joined.Describe().Contains("Victor (Smith) has joined at Sue Tech's request") && joined.Describe().Contains("not from your IT company"));
}

var root = AppContext.BaseDirectory;
while (root != null && !File.Exists(Path.Combine(root, "CLAUDE.md"))) root = Path.GetDirectoryName(root);
if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }
var dataDir = Path.Combine(Path.GetTempPath(), "rs-helpcheck-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dataDir);
const string Pw = "invite-check-2026";
const string Sue = "Sue Tech", Tom = "Tom Tech", Sam = "Sam Supervisor", Vic = "Victor Visitor";
const string ACo = "Anywhere Test IT", BCo = "Smith & Co IT";
const string Jane = "Jane Accountant", JaneCo = "Smith & Co Accountants";
string VicLabel = $"{Vic} ({BCo})", JaneLabel = $"{Jane} ({JaneCo}, a guest — not checked)";
Process? server = null;
try
{
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
    await using (var db = new PlatformDb(options)) await HarnessDb.MigrateAsync(db);
    var tenants = new TenantService(new Factory(options), new ConfigurationBuilder().Build(), dataDir, DataProtectionProvider.Create("helpcheck"));
    var (a, _) = await tenants.CreateAsync(ACo, "a-co", 7);
    await tenants.AddTechnicianAsync(a!.Id, "sue@a.test", Pw, Sue, TenantRole.Technician);
    await tenants.AddTechnicianAsync(a.Id, "tom@a.test", Pw, Tom, TenantRole.Technician);
    await tenants.AddTechnicianAsync(a.Id, "sam@a.test", Pw, Sam, TenantRole.Admin);
    var (b, _) = await tenants.CreateAsync(BCo, "b-co", 7);
    await tenants.AddTechnicianAsync(b!.Id, "vic@b.test", Pw, Vic, TenantRole.Technician);
    int sueId;
    await using (var db = new PlatformDb(options))
    {
        sueId = (await db.Technicians.FirstAsync(t => t.Email == "sue@a.test")).Id;
        Check("a company starts with technicians allowed to bring somebody in (on by default)", (await db.Tenants.FirstAsync(t => t.Id == a.Id)).TechnicianInvitesAllowed);
        // A's CUSTOMERS may not make codes: the customer's switch is OFF, and this must not depend on it.
        Check("...and its clients' own codes are off — the switch this does NOT depend on", !(await db.Tenants.FirstAsync(t => t.Id == a.Id)).OutsidersAllowed);
    }
    async Task<OutsiderCode?> LastCode(string sessionOrDevice, bool session = true)
    {
        await using var db = new PlatformDb(options);
        return await db.OutsiderCodes.Where(x => session ? x.SessionId == sessionOrDevice : x.DeviceId == sessionOrDevice && x.SessionId == "").OrderBy(x => x.Id).LastOrDefaultAsync();
    }
    async Task<List<string>> Notes(string deviceId)
    {
        await using var db = new PlatformDb(options);
        var t = await db.Tickets.Where(x => x.TenantId == a.Id && x.DeviceId == deviceId).OrderBy(x => x.Id).LastOrDefaultAsync();
        return t == null ? new() : await db.TicketNotes.Where(n => n.TicketId == t.Id).OrderBy(n => n.Id).Select(n => n.Body).ToListAsync();
    }

    var port = FreePort();
    var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var arg in new[] { "run", "-c", "Debug", "--project", Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj"), "--", "run" })
        psi.ArgumentList.Add(arg);
    psi.Environment["RSP_Platform__DataDirectory"] = dataDir;
    HarnessDb.Use(psi.Environment, dataDir);
    psi.Environment["RSP_Security__RequireTwoFactor"] = "false";   // a throwaway platform: the password alone (HANDOVER §72)
    psi.Environment["RSP_Testing__OutsiderJoinAskSeconds"] = "6";   // the two-minute question, shortened
    psi.Environment["RSP_Urls"] = $"http://127.0.0.1:{port}";
    psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
    server = Process.Start(psi)!;
    server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    if (!await WaitForPort(port, 90000)) { Console.WriteLine("The platform never started listening."); return 1; }
    Console.WriteLine($"helpcheck - a technician brings somebody in; a platform of its own on port {port}");

    var (sue, sueTok) = await Console_(port, "sue@a.test");
    var (tom, tomTok) = await Console_(port, "tom@a.test");
    var (sam, _) = await Console_(port, "sam@a.test");
    var (vic, vicTok) = await Console_(port, "vic@b.test");
    var ann = Id("ann"); var tray = await Device(port, ann, DeviceRole.Client, a.TenantKey, "ANN-PC", DeviceCan.AlwaysAsk);
    await Task.Delay(800);
    Check("ANN-PC's tray app is told it may NOT make a code of its own (its company has that off)", (await tray.NextWhere<OutsiderStateMsg>(_ => true))?.Allowed == false);

    // ---- a session of Sue's on ANN-PC, under way ----
    async Task<(string Sid, Peer Host, Peer View)> SueConnects()
    {
        tray.Clear();
        sue.Ws.Send(new ConnectDeviceMsg { DeviceId = ann.DeviceId, Reason = "helpcheck: a printer that will not print" });
        var start = (await tray.NextWhere<StartSessionMsg>(m => m.HostToken.Length > 0))!;
        var host = await Peer.Open($"ws://127.0.0.1:{port}/ws/session?role=host&id={start.SessionId}&token={start.HostToken}" + (start.AskConsent ? "&consent=asking&secs=60" : ""));
        await Task.Delay(300);
        if (start.AskConsent) host.Ws.Send(new ConsentGrantedMsg { SessionId = start.SessionId });
        await Task.Delay(300);
        var view = await Peer.Open($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={start.SessionId}&token={sueTok}");
        await Task.Delay(400);
        return (start.SessionId, host, view);
    }
    async Task<OutsiderInviteMsg?> Invite(Peer who, string sid, string forWhom)
    {
        who.Ws.Send(new OutsiderInviteRequestMsg { SessionId = sid, For = forWhom });
        return await who.NextWhere<OutsiderInviteMsg>(m => m.SessionId == sid && (m.Code.Length > 0 || m.Error.Length > 0));
    }

    // =========================================================== who may make one
    Console.WriteLine();
    Console.WriteLine("== who may make a code ==");
    var bogus = await Invite(sue, "no-such-session", "Pieter");
    Check("for a session that does not exist: refused, in words", bogus?.Error.Contains("ended") == true, bogus?.Error ?? "nothing");
    var (sid, host, sueView) = await SueConnects();
    var tomTry = await Invite(tom, sid, "Pieter");
    Check("Tom, of the same company but NOT in the session: refused", tomTry?.Error.Contains("in this session") == true, tomTry?.Error ?? "nothing");
    var blank = await Invite(sue, sid, "  ");
    Check("Sue, with nobody named: refused — who it is for is shown to the customer and on the ticket", blank?.Error.Contains("who the code is for") == true, blank?.Error ?? "nothing");
    var made = await Invite(sue, sid, "Smith & Co IT");
    Check("Sue, in the session: a nine-digit code", made is { Code.Length: 9, Error: "" } && made.ExpiresUtc > DateTime.UtcNow.AddMinutes(9), made?.Error ?? "nothing");
    var row = await LastCode(sid);
    Check("stored as hers, for THIS session, as a hash only", row is { InvitedById: var iid, MadeBy: Sue } && iid == sueId && row.CodeHash != made!.Code && !row.CodeHash.Contains(made.Code));
    Check("ANN-PC's tray app is told nothing about it — its own code line is the customer's", await tray.Next<OutsiderStateMsg>(800) == null);
    Check("the ticket says she made one, and for whom", (await Notes(ann.DeviceId)).Any(n => n.Contains("Sue Tech made a code to bring \"Smith & Co IT\"")));

    // =========================================================== Victor types it; Sue answers
    Console.WriteLine();
    Console.WriteLine("== Victor of Smith & Co types it: SUE is asked, the customer is not ==");
    sue.Clear(); tom.Clear(); sam.Clear(); vic.Clear(); tray.Clear(); host.Clear();
    vic.Ws.Send(new OutsiderConnectMsg { Code = OutsiderAccess.Format(made!.Code) });
    var ask = await sue.Next<OutsiderJoinAskMsg>();
    Check("Sue is asked to let him in: who, which machine, the code's \"for\"",
        ask is { MachineName: "ANN-PC", ForWhom: "Smith & Co IT" } && ask.Visitor == VicLabel && ask.Seconds == 6, ask == null ? "nothing" : $"{ask.Visitor} / {ask.ForWhom} / {ask.Seconds}s");
    Check("...and her window is told he is waiting", (await sue.NextWhere<OutsiderInviteMsg>(m => m.State.Contains("waiting for you")))!= null);
    Check("Tom is NOT asked — it was not his code", await tom.Next<OutsiderJoinAskMsg>(600) == null);
    var waiting = await vic.NextWhere<SessionReadyMsg>(m => m.Session.SessionId == sid);
    Check("Victor's console: waiting for SUE to let him in, as his own visit",
        waiting is { Session: { AwaitingConsent: true, HostConnected: false, AwaitingTechnician: Sue, StartedBy: Vic, Visiting: true } } && waiting.Session.Technicians.Count == 0,
        waiting == null ? "nothing" : $"awaiting {waiting.Session.AwaitingConsent} by '{waiting.Session.AwaitingTechnician}'");
    Check("the person at ANN-PC is not asked anything — no new session, no consent prompt", tray.Count<StartSessionMsg>() == 0 && host.Count<StartSessionMsg>() == 0);
    Check("the code is spent, by him", (await LastCode(sid)) is { UsedUtc: not null, UsedBy: var ub } && ub == VicLabel);
    vic.Ws.Send(new OutsiderConnectMsg { Code = made.Code });
    Check("...and does not work twice", (await vic.Next<ErrorMsg>())?.Message.Contains("does not work") == true);
    var early = await Peer.TryOpen($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={sid}&token={vicTok}");
    Check("before Sue answers, Victor cannot open the session", early == null);
    tom.Ws.Send(new OutsiderJoinAnswerMsg { SessionId = sid, Allow = true, Files = true });
    Check("Tom answering for Sue's code does nothing", await vic.NextWhere<SessionReadyMsg>(m => m.Session.SessionId == sid && m.Session.HostConnected, 1200) == null);

    sue.Ws.Send(new OutsiderJoinAnswerMsg { SessionId = sid, Allow = true });
    var allowed = await vic.NextWhere<SessionReadyMsg>(m => m.Session.SessionId == sid && m.Session.HostConnected);
    Check("Sue lets him in: Victor's console is told he is in, without files", allowed is { Session: { AwaitingConsent: false, Visiting: true, VisitorFiles: false, StartedBy: Vic } });
    var told = await tray.Next<OutsiderJoinedMsg>();
    Check("the person at ANN-PC is TOLD — who, at whose request — not asked", told is { Left: false, By: Sue, Files: false } && told.Visitor == VicLabel, told?.Describe() ?? "nothing");
    var pop = await sam.Next<OutsiderVisitMsg>();
    Check("A's consoles get the red pop-up, saying Sue brought him in", pop is { InvitedBy: Sue, MachineName: "ANN-PC" } && pop.Visitor == VicLabel, pop == null ? "nothing" : $"{pop.Visitor} by '{pop.InvitedBy}'");
    Check("Sue's window says he is connected, and whom it may Remove", (await sue.NextWhere<OutsiderInviteMsg>(m => m.Connected.Length > 0))?.Connected == VicLabel);
    var aSees = await tom.NextWhere<SessionReadyMsg>(m => m.Session.SessionId == sid && m.Session.Visitor.Length > 0);
    Check("the session says who is in it from outside, and who brought him", aSees is { Session: { InvitedBy: Sue } } && aSees.Session.Visitor == VicLabel);
    var notes = await Notes(ann.DeviceId);
    Check("the ticket: he typed it, Sue let him in without files, the person was told",
        notes.Any(n => n.Contains("typed the code Sue Tech made")) && notes.Any(n => n.Contains("Sue Tech let Victor Visitor (Smith & Co IT) into the session") && n.Contains("without files") && n.Contains("was told")));

    var vicView = await Peer.TryOpen($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={sid}&token={vicTok}");
    Check("now Victor opens the session", vicView != null);
    var parts = await host.NextWhere<ParticipantsMsg>(p => p.Technicians.Contains(VicLabel));
    Check("the host sees Sue and Victor, Victor by his company", parts != null && parts.Technicians.Contains(Sue));
    if (vicView == null) throw new Exception("cannot go on without Victor's window");
    await Task.Delay(300);
    vicView.Clear(); host.Clear(); sueView.Clear();
    vicView.Ws.Send(new MouseMsg { Monitor = 0 });
    vicView.Ws.Send(new ClipboardMsg { Text = "from the visitor" });
    vicView.Ws.Send(new FileListRequestMsg { Path = "C:\\" });
    vicView.Ws.Send(new ScriptRunMsg { RunId = "x", Script = "whoami" });
    vicView.Ws.SendBinary(Bin.FileChunk("f", 0, new byte[] { 1, 2, 3 }));
    await Task.Delay(1200);
    Check("§89's allow-list holds: his mouse reaches the machine", host.Count<MouseMsg>() == 1);
    Check("...his clipboard, a file request, a script and a file chunk do not (files not ticked)",
        host.Count<ClipboardMsg>() == 0 && host.Count<FileListRequestMsg>() == 0 && host.Count<ScriptRunMsg>() == 0 && host.BinaryCount == 0);
    host.Ws.Send(new ClipboardMsg { Text = "the customer's clipboard" });
    await Task.Delay(600);
    Check("...and he is not shown the customer's clipboard; Sue is", vicView.Count<ClipboardMsg>() == 0 && sueView.Count<ClipboardMsg>() == 1);
    var second = await Invite(sue, sid, "Somebody else");
    Check("a second code while he is in: refused — one person from outside at a time", second?.Error.Contains("already in this session") == true, second?.Error ?? "nothing");

    // ---- his leaving does not end it ----
    Console.WriteLine();
    Console.WriteLine("== he leaves: the session goes on ==");
    tray.Clear(); sueView.Clear(); sam.Clear();
    vicView.Ws.Send(new EndSessionMsg { SessionId = sid });
    var vEnded = await vicView.Next<SessionEndedMsg>();
    Check("Victor presses Leave: his window is told it is over for him", vEnded?.Reason.Contains("left") == true, vEnded?.Reason ?? "nothing");
    Check("...Sue's is NOT — the session is hers and goes on", await sueView.Next<SessionEndedMsg>(1200) == null);
    Check("...the person at ANN-PC is told he has left", (await tray.Next<OutsiderJoinedMsg>()) is { Left: true });
    Check("...A's consoles see the session with nobody from outside in it", (await sam.NextWhere<SessionReadyMsg>(m => m.Session.SessionId == sid && m.Session.Visitor == "" && !m.Session.Ended)) != null);
    var back = await Peer.TryOpen($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={sid}&token={vicTok}");
    Check("...and he cannot come back in with the spent code's session", back == null || await back.Next<SessionEndedMsg>() != null);
    sueView.Clear(); host.Clear();
    sueView.Ws.Send(new ClipboardMsg { Text = "Sue still working" });
    Check("Sue carries on working", await host.Next<ClipboardMsg>() != null);

    // =========================================================== a guest: turned away, then let in with files, then removed
    Console.WriteLine();
    Console.WriteLine("== a guest: Sue says no; then yes, with files; then Sam removes her ==");
    var guest = await Peer.Open($"ws://127.0.0.1:{port}/ws/guest");
    var g1 = await Invite(sue, sid, JaneCo);
    sue.Clear();
    guest.Ws.Send(new GuestConnectMsg { Code = g1!.Code, Name = Jane, Company = JaneCo });
    var gAsk = await sue.Next<OutsiderJoinAskMsg>();
    Check("a guest types it: Sue is asked, the guest labelled NOT CHECKED", gAsk?.Visitor == JaneLabel, gAsk?.Visitor ?? "nothing");
    var gWait = await guest.NextWhere<SessionReadyMsg>(m => m.Session.SessionId == sid);
    Check("the guest's window: waiting for Sue", gWait?.Session.AwaitingTechnician == Sue);
    sue.Ws.Send(new OutsiderJoinAnswerMsg { SessionId = sid, Allow = false });
    var gNo = await guest.NextWhere<SessionReadyMsg>(m => m.Session.SessionId == sid && m.Session.Ended);
    Check("Sue turns her away: the guest is told why", gNo?.Session.EndReason.Contains("did not let you in") == true, gNo?.Session.EndReason ?? "nothing");
    Check("...and Sue's session goes on", await sueView.Next<SessionEndedMsg>(800) == null);

    var g2 = await Invite(sue, sid, JaneCo);
    sue.Clear(); guest.Clear(); tray.Clear();
    guest.Ws.Send(new GuestConnectMsg { Code = g2!.Code, Name = Jane, Company = JaneCo });
    var gTicket = await guest.Next<GuestTicketMsg>();
    await sue.Next<OutsiderJoinAskMsg>();
    sue.Ws.Send(new OutsiderJoinAnswerMsg { SessionId = sid, Allow = true, Files = true });
    var gIn = await guest.NextWhere<SessionReadyMsg>(m => m.Session.SessionId == sid && m.Session.HostConnected);
    Check("Sue lets her in AND ticks files: the guest is told files are hers", gIn?.Session.VisitorFiles == true);
    Check("...and the person at ANN-PC is told she may copy files", (await tray.Next<OutsiderJoinedMsg>())?.Files == true);
    var gView = await Peer.TryOpen($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={sid}&token={gTicket!.Token}");
    Check("the guest's token opens the session", gView != null);
    await Task.Delay(400); host.Clear();
    gView!.Ws.Send(new FileListRequestMsg { Path = "C:\\" });
    gView.Ws.Send(new ScriptRunMsg { RunId = "x", Script = "whoami" });
    await Task.Delay(800);
    Check("files ticked: her folder request reaches the machine; a script still does not", host.Count<FileListRequestMsg>() == 1 && host.Count<ScriptRunMsg>() == 0);
    sam.Ws.Send(new OutsiderRemoveMsg { SessionId = sid });
    var gOut = await gView.Next<SessionEndedMsg>();
    Check("Sam presses Remove: she is out, told by whom", gOut?.Reason.Contains($"Removed by {Sam}") == true, gOut?.Reason ?? "nothing");
    Check("...Sue's session goes on", await sueView.Next<SessionEndedMsg>(800) == null);
    host.Clear();
    gView.Ws.Send(new MouseMsg { Monitor = 0 });
    gView.Ws.Send(new FileListRequestMsg { Path = "C:\\" });
    await Task.Delay(800);
    Check("...and anything her socket still sends after Remove goes nowhere", host.Count<MouseMsg>() == 0 && host.Count<FileListRequestMsg>() == 0);

    // =========================================================== nobody answers
    Console.WriteLine();
    Console.WriteLine("== Sue does not answer ==");
    var g3 = await Invite(sue, sid, JaneCo);
    var guest2 = await Peer.Open($"ws://127.0.0.1:{port}/ws/guest");
    guest2.Ws.Send(new GuestConnectMsg { Code = g3!.Code, Name = Jane, Company = JaneCo });
    var timedOut = await guest2.NextWhere<SessionReadyMsg>(m => m.Session.SessionId == sid && m.Session.Ended, 12000);
    Check("after the time is up, the guest is turned away — \"did not answer\"", timedOut?.Session.EndReason.Contains("did not answer") == true, timedOut?.Session.EndReason ?? "nothing");

    // =========================================================== nobody of ours left / Sue leaves
    Console.WriteLine();
    Console.WriteLine("== nobody from A left: the outsider goes too ==");
    async Task<Peer> VictorIn()
    {
        var c = await Invite(sue, sid, BCo);
        sue.Clear();
        vic.Ws.Send(new OutsiderConnectMsg { Code = c!.Code });
        await sue.Next<OutsiderJoinAskMsg>();
        sue.Ws.Send(new OutsiderJoinAnswerMsg { SessionId = sid, Allow = true });
        await vic.NextWhere<SessionReadyMsg>(m => m.Session.SessionId == sid && m.Session.HostConnected);
        var v = await Peer.Open($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={sid}&token={vicTok}");
        await Task.Delay(400);
        return v;
    }
    var vic2 = await VictorIn();
    tray.Clear();
    await sueView.Ws.CloseAsync("probe: Sue's Wi-Fi drops");
    var lonely = await vic2.Next<SessionEndedMsg>();
    Check("Sue's connection drops with Victor in: he is taken out at once — nobody from A is left",
        lonely?.Reason.Contains($"Nobody from {ACo}") == true, lonely?.Reason ?? "nothing");
    Check("...and the person at ANN-PC is told he has left", (await tray.Next<OutsiderJoinedMsg>())?.Left == true);
    sueView = await Peer.Open($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={sid}&token={sueTok}");
    Check("...while Sue's session waits for her, and she comes back to it", await sueView.Next<SessionEndedMsg>(1200) == null);
    await Task.Delay(300);

    Console.WriteLine();
    Console.WriteLine("== Sue presses Leave with only Victor left: it ends ==");
    var vic3 = await VictorIn();
    host.Clear();
    sueView.Ws.Send(new EndSessionMsg { SessionId = sid });
    var all = await vic3.Next<SessionEndedMsg>();
    Check("Sue leaves deliberately: the session ENDS — Victor is never left on ANN-PC alone", all != null, all?.Reason ?? "nothing");
    Check("...and the host is told it is over", await host.Next<SessionEndedMsg>() != null);

    // =========================================================== the code stops with the session, or its maker
    Console.WriteLine();
    Console.WriteLine("== a code stops when it can no longer be answered ==");
    (sid, host, sueView) = await SueConnects();
    var orphan = await Invite(sue, sid, BCo);
    sue.Ws.Send(new EndSessionMsg { SessionId = sid });
    await Task.Delay(1000);
    Check("the session ends: its live code is stopped, saying so", (await LastCode(sid))?.EndedWhy.Contains("session ended") == true, (await LastCode(sid))?.EndedWhy ?? "none");
    vic.Ws.Send(new OutsiderConnectMsg { Code = orphan!.Code });
    Check("...and typing it does nothing", (await vic.Next<ErrorMsg>())?.Message.Contains("does not work") == true);

    (sid, host, sueView) = await SueConnects();
    var tomView = await Peer.Open($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={sid}&token={tomTok}");
    await Task.Delay(400);
    var hers = await Invite(sue, sid, BCo);
    sueView.Ws.Send(new EndSessionMsg { SessionId = sid });   // Tom stays, so only Sue leaves
    await Task.Delay(300);
    await sueView.Ws.CloseAsync("Leave — her window closes");
    await Task.Delay(1000);
    Check("Sue leaves with Tom still in: HER code stops — she is the one it would ask", (await LastCode(sid))?.EndedWhy.Contains("left the session") == true, (await LastCode(sid))?.EndedWhy ?? "none");
    Check("...and the session goes on with Tom", await tomView.Next<SessionEndedMsg>(800) == null);

    // =========================================================== the customer's own code is untouched
    Console.WriteLine();
    Console.WriteLine("== the customer's own code is another door ==");
    sam.Ws.Send(new DeviceOutsiderModeMsg { DeviceId = ann.DeviceId, Mode = OutsiderAccess.On });
    await tray.NextWhere<OutsiderStateMsg>(s => s.Allowed);
    tray.Ws.Send(new OutsiderCodeRequestMsg { For = "The accountant" });
    var custCode = await tray.NextWhere<OutsiderCodeMsg>(m => m.Code.Length > 0);
    var tomCode = await Invite(tom, sid, BCo);
    Check("Tom makes a code in the session: the customer's own code is NOT stopped by it",
        tomCode?.Code.Length == 9 && (await LastCode(ann.DeviceId, session: false)) is { EndedUtc: null, UsedUtc: null });
    tray.Ws.Send(new OutsiderCodeCancelMsg());
    await Task.Delay(600);
    Check("the customer stopping theirs does not stop Tom's", (await LastCode(sid)) is { EndedUtc: null, UsedUtc: null, MadeBy: Tom });
    sam.Ws.Send(new DeviceOutsiderModeMsg { DeviceId = ann.DeviceId, Mode = OutsiderAccess.Off });
    await Task.Delay(800);
    Check("ANN-PC switched OFF for its customers: Tom's code still works — that switch is the customer's", (await LastCode(sid)) is { EndedUtc: null });

    // =========================================================== the company's switch, from the portal
    Console.WriteLine();
    Console.WriteLine("== the company's switch ==");
    tom.Clear();
    vic.Ws.Send(new OutsiderConnectMsg { Code = tomCode!.Code });
    await tom.Next<OutsiderJoinAskMsg>();
    tom.Ws.Send(new OutsiderJoinAnswerMsg { SessionId = sid, Allow = true });
    await vic.NextWhere<SessionReadyMsg>(m => m.Session.SessionId == sid && m.Session.HostConnected);
    var vic4 = await Peer.Open($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={sid}&token={vicTok}");
    await Task.Delay(400);
    var samWeb = await Portal(port, "sam@a.test");
    var page = await samWeb.GetStringAsync("/portal/outsiders");
    Check("the portal's Outside help page has the technicians' switch, ticked", page.Contains("Let our technicians bring somebody in") && page.Contains("handler=Invites"));
    var offPage = await PostForm(samWeb, "/portal/outsiders", "handler=Invites");   // unticked: nothing posted for it
    Check("Sam switches it off: the page says so", offPage.Contains("Switched off. No technician can bring anybody in"));
    var swOff = await vic4.Next<SessionEndedMsg>();
    Check("...Victor, let in by Tom, is removed at once, saying why", swOff?.Reason.Contains("switched off") == true, swOff?.Reason ?? "nothing");
    Check("...and Tom's session goes on", await tomView.Next<SessionEndedMsg>(800) == null);
    var refused = await Invite(tom, sid, BCo);
    Check("...and no new code can be made, saying where to switch it on", refused?.Error.Contains("switched off for your company") == true, refused?.Error ?? "nothing");
    await using (var db = new PlatformDb(options))
        Check("...and the switch is stored", !(await db.Tenants.FirstAsync(t => t.Id == a.Id)).TechnicianInvitesAllowed);
    var sueWeb = await Portal(port, "sue@a.test");
    var sueOn = await PostForm(sueWeb, "/portal/outsiders", "handler=Invites", ("enabled", "true"));
    await using (var db = new PlatformDb(options))
        Check("a technician cannot switch it back on from the portal", !(await db.Tenants.FirstAsync(t => t.Id == a.Id)).TechnicianInvitesAllowed);
    await PostForm(samWeb, "/portal/outsiders", "handler=Invites", ("enabled", "true"));
    Check("Sam switches it on again: Tom can make a code", (await Invite(tom, sid, BCo))?.Code.Length == 9);
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

static DeviceIdentity Id(string name) => new() { DeviceId = name + "-" + Guid.NewGuid().ToString("N")[..6], DeviceKey = Guid.NewGuid().ToString("N") };

static async Task<(Peer, string)> Console_(int port, string email)
{
    using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    var lr = await hc.PostAsJsonAsync($"http://127.0.0.1:{port}/api/login", new { email, password = "invite-check-2026" });
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

/// <summary>Signed in through the portal's own form, antiforgery token and all.</summary>
static async Task<HttpClient> Portal(int port, string email)
{
    var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true })
        { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(30) };
    var page = await c.GetStringAsync("/login");
    await c.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Email"] = email, ["Password"] = "invite-check-2026", ["__RequestVerificationToken"] = Token(page) }));
    return c;
}

/// <summary>A form posted the way the page posts it: the token from THAT form (§80's lesson).</summary>
static async Task<string> PostForm(HttpClient c, string url, string formMarker, params (string k, string v)[] fields)
{
    var page = await c.GetStringAsync(url);
    var at = page.IndexOf(formMarker, StringComparison.Ordinal);
    var start = at < 0 ? -1 : page.LastIndexOf("<form", at, StringComparison.Ordinal);
    var end = at < 0 ? -1 : page.IndexOf("</form>", at, StringComparison.Ordinal);
    var form = start < 0 || end < 0 ? "" : page[start..end];
    var all = fields.Select(f => new KeyValuePair<string, string>(f.k, f.v)).Append(new("__RequestVerificationToken", Token(form)));
    var r = await c.PostAsync(url + "?" + formMarker, new FormUrlEncodedContent(all));
    return WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());
}

static string Token(string page)
{
    foreach (var a in new[] { "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"", "name=\"__RequestVerificationToken\" value=\"" })
    {
        var i = page.IndexOf(a, StringComparison.Ordinal);
        if (i < 0) continue;
        i += a.Length;
        var j = page.IndexOf('"', i);
        if (j > i) return page[i..j];
    }
    return "";
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
