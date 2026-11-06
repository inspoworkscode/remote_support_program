// =====================================================================================
// websupportcheck - proves support in the browser (stage 3.8.2, 2026-09-30).
//
//   "potential clients to be able to get remote support via a browser. So they click on a link,
//   put in their contact details, and request a support call online and it rings through to the
//   technicians the same way the app works."
//
// His answers: worth building though a browser can only SHOW the screen; switched off and on, with
// hours of the company's own; and a browser caller becomes a Contact and a ticket.
//
// Three parts. The RULES, in process: the hours (overnight, time zones, switched off) and the
// per-address limit. The WIRE, on a platform of its own, with the browser's side spoken as RAW JSON
// exactly as wwwroot/help.js writes it — so what is proved is that the page's own messages are
// understood: the page (off, closed, the form's refusals, the hidden field), the ring, the contact
// and the ticket, chat both ways, answering, the host socket, a picture relayed byte for byte, the
// session marked view only, ending, and a page closed while ringing.
//
// And --browser: the same platform waits for a REAL browser to fill in the form and share a screen
// (tools/websupportcheck prints the link), then decodes what it sends with the console's own H.264
// decoder and looks at the picture. That is the half no C# can stand in for.
//
//   dotnet run -c Debug --project tools/websupportcheck/websupportcheck.csproj [--browser]
// =====================================================================================

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Pages;
using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}
// --browser-voice (3.3): the same wait for a real browser, then talking both ways with it.
bool voiceMode = args.Contains("--browser-voice");
bool browserMode = args.Contains("--browser") || voiceMode;

// =========================================================== the rules
Console.WriteLine("websupportcheck - support in the browser");
Console.WriteLine();
Console.WriteLine("== the hours ==");
var t0 = new Tenant { WebSupportEnabled = true, WebSupportTimeZone = "Africa/Johannesburg" };   // Mon-Fri 08:00-17:00 by default
DateTime Sast(int y, int mo, int d, int h, int mi) => new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Utc).AddHours(-2);
// 2026-09-28 is a Monday.
Check("the default: a Monday at 10:00 in Johannesburg is open", WebSupportHours.IsOpen(t0, Sast(2026, 9, 28, 10, 0)));
Check("...07:59 is not", !WebSupportHours.IsOpen(t0, Sast(2026, 9, 28, 7, 59)));
Check("...17:00 is closed (the closing time is when it stops)", !WebSupportHours.IsOpen(t0, Sast(2026, 10, 2, 17, 0)));
Check("...a Saturday is closed", !WebSupportHours.IsOpen(t0, Sast(2026, 10, 3, 10, 0)));
Check("switched off, it is never open", !WebSupportHours.IsOpen(new Tenant { WebSupportEnabled = false }, Sast(2026, 9, 28, 10, 0)));
var night = new Tenant { WebSupportEnabled = true, WebSupportTimeZone = "Africa/Johannesburg", WebSupportHours = ";18:00-02:00;;;;;" };
Check("longer than office hours, past midnight: Tuesday 23:00 is open", WebSupportHours.IsOpen(night, Sast(2026, 9, 29, 23, 0)));
Check("...and 01:30 on Wednesday belongs to Tuesday, so it is open", WebSupportHours.IsOpen(night, Sast(2026, 9, 30, 1, 30)));
Check("...03:00 on Wednesday is not", !WebSupportHours.IsOpen(night, Sast(2026, 9, 30, 3, 0)));
var london = new Tenant { WebSupportEnabled = true, WebSupportTimeZone = "Europe/London" };
Check("the company's own time zone: 08:30 in London is 09:30 in Johannesburg, and open there",
    WebSupportHours.IsOpen(london, new DateTime(2026, 9, 28, 7, 30, 0, DateTimeKind.Utc)) && !WebSupportHours.IsOpen(london, new DateTime(2026, 9, 28, 6, 30, 0, DateTimeKind.Utc)));
Check("in words: Monday to Friday 08:00–17:00", WebSupportHours.Describe(t0).SequenceEqual(new[] { "Monday to Friday 08:00–17:00" }), string.Join(" | ", WebSupportHours.Describe(t0)));
Check("anything unreadable is closed", WebSupportHours.Parse("rubbish;9-5;;;;;").All(d => d == null));
Check("it round-trips", WebSupportHours.Format(WebSupportHours.Parse(night.WebSupportHours)) == night.WebSupportHours);
Console.WriteLine();
Console.WriteLine("== a busy address ==");
var key = "t|" + Guid.NewGuid();
var at = DateTime.UtcNow;
Check($"{HelpModel.PerAddressPerHour} requests in an hour are let through", Enumerable.Range(0, HelpModel.PerAddressPerHour).All(_ => HelpModel.Allow(key, at)));
Check("...the next is refused", !HelpModel.Allow(key, at.AddMinutes(1)));
Check("...and an hour later it is let through again", HelpModel.Allow(key, at.AddMinutes(61)));

// =========================================================== the wire
var root = AppContext.BaseDirectory;
while (root != null && !File.Exists(Path.Combine(root, "CLAUDE.md"))) root = Path.GetDirectoryName(root);
if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }
var dataDir = Path.Combine(Path.GetTempPath(), "rs-websupport-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dataDir);
var port = browserMode ? 8098 : FreePort();
var baseUrl = $"http://127.0.0.1:{port}";
Process? server = null;
var dbOptions = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
try
{
    await using (var db = new PlatformDb(dbOptions)) await HarnessDb.MigrateAsync(db);
    var factory = new Factory(dbOptions);
    var tenants = new TenantService(factory, new ConfigurationBuilder().Build(), dataDir, DataProtectionProvider.Create("websupportcheck"));
    var (open, _) = await tenants.CreateAsync("Open All Hours IT", "open-it", 7);
    var (closedCo, _) = await tenants.CreateAsync("Closed IT", "closed-it", 7);
    var (offCo, _) = await tenants.CreateAsync("Switched Off IT", "off-it", 7);
    await tenants.AddTechnicianAsync(open!.Id, "sue@web.test", "web-check-2026", "Sue Tech", TenantRole.Technician);
    await tenants.UpdateBrandingAsync(open.Id, t => { t.WebSupportEnabled = true; t.WebSupportHours = string.Join(";", Enumerable.Repeat("00:00-24:00", 7)); t.Status = TenantStatus.Active; });
    await tenants.UpdateBrandingAsync(closedCo!.Id, t => { t.WebSupportEnabled = true; t.WebSupportHours = ";;;;;;"; t.SupportPhone = "021 555 0100"; t.Status = TenantStatus.Active; });
    await tenants.UpdateBrandingAsync(offCo!.Id, t => { t.WebSupportEnabled = false; t.Status = TenantStatus.Active; });

    var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in new[] { "run", "-c", "Debug", "--project", Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj"), "--", "run" })
        psi.ArgumentList.Add(a);
    psi.Environment["RSP_Platform__DataDirectory"] = dataDir;
    HarnessDb.Use(psi.Environment, dataDir);
    psi.Environment["RSP_Security__RequireTwoFactor"] = "false";   // a throwaway platform: the password alone (HANDOVER §72)
    psi.Environment["RSP_Urls"] = baseUrl;
    psi.Environment["ASPNETCORE_URLS"] = baseUrl;
    server = Process.Start(psi)!;
    server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    if (!await WaitForPort(port, 90000)) { Console.WriteLine("The platform never started listening."); return 1; }
    Console.WriteLine();
    Console.WriteLine($"== the page (a platform of its own on port {port}) ==");

    var (sue, sueToken) = await Sign(port, "sue@web.test");

    if (browserMode)
    {
        var done = voiceMode ? await BrowserVoiceMode(port, sue, sueToken, Check) : await BrowserMode(port, sue, sueToken, dbOptions, Check);
        Console.WriteLine();
        Console.WriteLine($"{pass} of {pass + fail} checks passed.");
        return done && fail == 0 ? 0 : 1;
    }

    using var anon = new HttpClient { BaseAddress = new Uri(baseUrl) };
    var off = await anon.GetStringAsync("/help/off-it");
    Check("switched off: the page says so, and offers no form", off.Contains("not taking requests") && !off.Contains("name=\"Issue\""));
    var none = WebUtility.HtmlDecode(await anon.GetStringAsync("/help/no-such-company"));
    Check("an unknown company: \"isn't active\"", none.Contains("isn't active"));
    var closedPage = await anon.GetStringAsync("/help/closed-it");
    Check("outside its hours: closed, with its hours and its phone, and no form",
        closedPage.Contains("closed right now") && closedPage.Contains("021 555 0100") && !closedPage.Contains("name=\"Issue\""));

    var jar = new CookieContainer();
    using var browser = new HttpClient(new HttpClientHandler { CookieContainer = jar }) { BaseAddress = new Uri(baseUrl) };
    async Task<string> Post(Dictionary<string, string> fields)
    {
        var page = await browser.GetStringAsync("/help/open-it");
        var tok = Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        fields["__RequestVerificationToken"] = tok;
        var res = await browser.PostAsync("/help/open-it", new FormUrlEncodedContent(fields));
        return await res.Content.ReadAsStringAsync();
    }
    Dictionary<string, string> Form(string name = "Jane Smith", string company = "Acme Ltd", string phone = "082 555 0101",
        string email = "jane@acme.test", string issue = "Outlook keeps asking for my password.", string website = "") => new()
    {
        ["Name"] = name, ["Company"] = company, ["Phone"] = phone, ["Email"] = email, ["Issue"] = issue, ["Website"] = website
    };
    var formPage = await browser.GetStringAsync("/help/open-it");
    Check("open: the form, with the hidden field a person never fills in", formPage.Contains("name=\"Issue\"") && formPage.Contains("name=\"Website\""));
    // This platform has no client download built, so the page must not offer one that 404s.
    Check("...and, with no app built to download, no link to one", !formPage.Contains("/get/open-it"));
    Check("no name: refused", (await Post(Form(name: ""))).Contains("tell us your name"));
    Check("no phone and no email: refused", (await Post(Form(phone: "", email: ""))).Contains("phone number or an email"));
    Check("a bad email: refused", (await Post(Form(email: "jane at acme"))).Contains("does not look right"));
    Check("nothing said about what is wrong: refused", (await Post(Form(issue: "x"))).Contains("what is wrong"));

    sue.Clear();
    var robot = await Post(Form(website: "http://spam.example"));
    Check("the hidden field filled in: told thank you…", robot.Contains("Thank you") && !robot.Contains("data-token"));
    Check("...and NOTHING rings", await sue.Next<RingMsg>(1500) == null);

    var calling = await Post(Form());
    var id = Regex.Match(calling, "data-id=\"([^\"]+)\"").Groups[1].Value;
    var token = Regex.Match(calling, "data-token=\"([^\"]+)\"").Groups[1].Value;
    var tenantId = Regex.Match(calling, "data-tenant=\"([^\"]+)\"").Groups[1].Value;
    Check("a good form: the call page, carrying its own id and token", id.StartsWith("web-") && token.Length > 10 && calling.Contains("help.js"), id);

    Console.WriteLine();
    Console.WriteLine("== it rings ==");
    var wrong = await RawWs.Open($"ws://127.0.0.1:{port}/ws/web?tenant={tenantId}&id={id}&token=not-it");
    var refusedMsg = await wrong.Next(m => m.GetProperty("t").GetString() == "error");
    Check("the page's socket with the wrong token is refused", refusedMsg != null && refusedMsg.Value.GetProperty("message").GetString()!.Contains("expired"));
    Check("...and nothing rang", await sue.Next<RingMsg>(1200) == null);

    var page1 = await RawWs.Open($"ws://127.0.0.1:{port}/ws/web?tenant={tenantId}&id={id}&token={token}");
    var ring = await sue.Next<RingMsg>();
    Check("the page connects and the technician's console RINGS, like the support app",
        ring?.Call is { Status: CallStatus.Ringing } && ring.Call.DeviceId == id, ring?.Call.MachineName ?? "no ring");
    Check("...showing who, from where and what is wrong",
        ring?.Call is { ContactName: "Jane Smith (Acme Ltd)", MachineName: "Jane Smith (web browser)", Issue: "Outlook keeps asking for my password." });
    var ringing = await page1.Next(m => m.GetProperty("t").GetString() == "call_update");
    Check("the page is told it is ringing, in the words help.js reads", ringing?.GetProperty("call").GetProperty("status").GetString() == "ringing");
    Check("a second page with the SAME token is refused", (await (await RawWs.Open($"ws://127.0.0.1:{port}/ws/web?tenant={tenantId}&id={id}&token={token}")).Next(m => m.GetProperty("t").GetString() == "error")) != null);

    await Task.Delay(600);
    await using (var db = new PlatformDb(dbOptions))
    {
        var t = await db.Tickets.Where(x => x.DeviceId == id).FirstOrDefaultAsync();
        var client = t?.ClientId == null ? null : await db.Clients.FirstOrDefaultAsync(c => c.Id == t.ClientId);
        var contact = client == null ? null : await db.Contacts.FirstOrDefaultAsync(c => c.ClientId == client.Id);
        Check("a ticket: Remote support, from a call, their words as the subject",
            t is { Category: TicketCategory.RemoteSupport, Source: TicketSource.Call, Subject: "Outlook keeps asking for my password." }, t?.Subject ?? "none");
        Check("...with their details in it", t != null && t.Description.Contains("Phone: 082 555 0101") && t.Description.Contains("Email: jane@acme.test"));
        Check("...for a NEW client, Acme Ltd, with Jane as its contact",
            client?.Name == "Acme Ltd" && contact is { Name: "Jane Smith", Email: "jane@acme.test" }, $"{client?.Name} / {contact?.Name}");
    }
    Check("the browser caller is NOT in the console's machine list", await sue.Next<DeviceUpdateMsg>(800) is not { Device.DeviceId: var d } || !d.StartsWith("web-"));

    Console.WriteLine();
    Console.WriteLine("== chat, before anybody answers ==");
    sue.Clear();
    await page1.Send("{\"t\":\"chat\",\"text\":\"It started this morning.\"}");
    var fromPage = await sue.Next<ChatMsg>();
    Check("the page's chat reaches the console, under the name from the form",
        fromPage is { From: "Jane Smith", FromTech: false, Text: "It started this morning." } && fromPage.DeviceId == id, fromPage?.From ?? "none");
    await page1.Send("{\"t\":\"chat\",\"from\":\"The Boss\",\"text\":\"trust me\"}");
    Check("...and a name the page CLAIMS is ignored", (await sue.Next<ChatMsg>())?.From == "Jane Smith");
    var echo = await page1.Next(m => m.GetProperty("t").GetString() == "chat" && m.GetProperty("text").GetString() == "It started this morning.");
    Check("...the page gets its own line back, to draw it (help.js draws on the echo)", echo != null);

    Console.WriteLine();
    Console.WriteLine("== answered ==");
    sue.Clear();
    sue.Ws.Send(new AcceptCallMsg { CallId = ring!.Call.CallId });
    var ready = await sue.Next<SessionReadyMsg>();
    Check("Sue answers: a session, marked VIEW ONLY", ready?.Session is { ViewOnly: true } && ready.Session.DeviceId == id, $"viewOnly={ready?.Session.ViewOnly}");
    Check("...named after the caller", ready?.Session.MachineName == "Jane Smith (web browser)");
    var accepted = await page1.Next(m => m.GetProperty("t").GetString() == "call_update" && m.GetProperty("call").GetProperty("status").GetString() == "accepted");
    Check("the page hears who answered", accepted?.GetProperty("call").GetProperty("acceptedBy").GetString() == "Sue Tech");
    var start = await page1.Next(m => m.GetProperty("t").GetString() == "start_session");
    var sessionId = start?.GetProperty("sessionId").GetString() ?? "";
    var hostToken = start?.GetProperty("hostToken").GetString() ?? "";
    Check("...and is handed the session to host", sessionId == ready?.Session.SessionId && hostToken.Length > 10);

    Console.WriteLine();
    Console.WriteLine("== the picture ==");
    var viewer = await Viewer(port, sessionId, sueToken);
    var host = await RawWs.Open($"ws://127.0.0.1:{port}/ws/session?role=host&id={sessionId}&token={hostToken}");
    // Exactly as help.js announces what was shared.
    await host.Send("{\"t\":\"monitors\",\"monitors\":[{\"index\":0,\"x\":0,\"y\":0,\"width\":1280,\"height\":720,\"name\":\"Shared from the browser\",\"primary\":true}]}");
    await host.Send("{\"t\":\"sysinfo\",\"info\":{\"Shared from\":\"A web browser — view only\"}}");
    var mons = await viewer.Next<MonitorsMsg>();
    Check("the page's announcement reaches the technician as a monitor", mons?.Monitors is [{ Width: 1280, Height: 720, Primary: true }], $"{mons?.Monitors.Count}");
    Check("...with what it is", (await viewer.Next<SysInfoMsg>())?.Info.GetValueOrDefault("Shared from") == "A web browser — view only");
    viewer.Ws.Send(new SubscribeMsg { Monitor = 0, On = true, Codecs = new() { VideoCodec.H264, VideoCodec.JpegTiles } });
    var sub = await host.Next(m => m.GetProperty("t").GetString() == "subscribe");
    Check("the console's subscribe reaches the page, in the words help.js reads",
        sub?.GetProperty("on").GetBoolean() == true && sub.Value.GetProperty("codecs").EnumerateArray().Select(e => e.GetString()).Contains("h264"));
    // A frame laid out exactly as help.js lays it out: [4][monitor][flags][w][h][Annex-B].
    var nal = new byte[] { 0, 0, 0, 1, 0x67, 0x42, 0xE0, 0x28, 0, 0, 0, 1, 0x68, 0xCE, 0, 0, 0, 1, 0x65, 0x88, 0x84, 0x21 };
    var frame = new byte[14 + nal.Length];
    frame[0] = 4; BitConverter.TryWriteBytes(frame.AsSpan(1), 0); frame[5] = 1;
    BitConverter.TryWriteBytes(frame.AsSpan(6), 1280); BitConverter.TryWriteBytes(frame.AsSpan(10), 720);
    nal.CopyTo(frame, 14);
    await host.SendBinary(frame);
    var got = await viewer.NextBinary();
    Check("a video frame from the page reaches the console byte for byte", got != null && got.SequenceEqual(frame), $"{got?.Length} B");
    var parsed = got == null ? default : Bin.ParseVideo(got);
    Check("...and reads as a key frame of 1280 × 720 on monitor 0", got != null && parsed is { monitor: 0, keyFrame: true, width: 1280, height: 720 });
    var tile = JpegTile(640, 360);
    await host.SendBinary(tile);
    var gotTile = await viewer.NextBinary();
    Check("a JPEG picture laid out as help.js's fallback reaches it too, as a tile frame", gotTile != null && gotTile[0] == BinKind.Tiles && gotTile.SequenceEqual(tile));

    Console.WriteLine();
    Console.WriteLine("== chat in the session, and the end ==");
    viewer.Ws.Send(new ChatMsg { Text = "I can see your screen now." });
    var techLine = await page1.Next(m => m.GetProperty("t").GetString() == "chat" && m.GetProperty("fromTech").GetBoolean());
    Check("the technician's chat from the session window reaches the page, under their name",
        techLine?.GetProperty("from").GetString() == "Sue Tech" && techLine?.GetProperty("text").GetString() == "I can see your screen now.");
    viewer.Ws.Send(new EndSessionMsg { SessionId = sessionId });
    Check("Sue ends it: the page is told", await page1.Next(m => m.GetProperty("t").GetString() == "session_ended") != null);
    await Task.Delay(4500);
    await using (var db = new PlatformDb(dbOptions))
    {
        var t = await db.Tickets.FirstAsync(x => x.DeviceId == id);
        var notes = await db.TicketNotes.Where(n => n.TicketId == t.Id).OrderBy(n => n.Id).Select(n => n.Body).ToListAsync();
        Check("the ticket's story: the request, the chat, the connection, the end",
            notes.Any(n => n.Contains("web browser page")) && notes.Any(n => n.Contains("It started this morning."))
            && notes.Any(n => n.Contains("Sue Tech connected")) && notes.Any(n => n.StartsWith("Session ended")),
            string.Join(" | ", notes.Select(n => n.Length > 40 ? n[..40] : n)));
        Check("...and Sue is on it", Assignees.Has(t.AssignedTo, "Sue Tech"), t.AssignedTo);
    }

    Console.WriteLine();
    Console.WriteLine("== a page closed while ringing ==");
    var again = await Post(Form(name: "Pat Jones", company: "", phone: "", email: "pat@home.test", issue: "Printer offline"));
    var id2 = Regex.Match(again, "data-id=\"([^\"]+)\"").Groups[1].Value;
    var token2 = Regex.Match(again, "data-token=\"([^\"]+)\"").Groups[1].Value;
    sue.Clear();
    var page2 = await RawWs.Open($"ws://127.0.0.1:{port}/ws/web?tenant={tenantId}&id={id2}&token={token2}");
    var ring2 = await sue.Next<RingMsg>();
    Check("with no company, they are their own client", ring2?.Call.ContactName == "Pat Jones");
    await page2.Close();
    var cancelled = await sue.Next<CallUpdateMsg>(m => m.Call.CallId == ring2?.Call.CallId);
    Check("closing the page CANCELS the ringing on every console", cancelled?.Call.Status == CallStatus.Cancelled);
    await Task.Delay(800);
    await using (var db = new PlatformDb(dbOptions))
    {
        var t = await db.Tickets.FirstOrDefaultAsync(x => x.DeviceId == id2);
        var said = t == null ? new List<string>() : await db.TicketNotes.Where(n => n.TicketId == t.Id).Select(n => n.Body).ToListAsync();
        var client = t?.ClientId == null ? null : await db.Clients.FirstOrDefaultAsync(c => c.Id == t.ClientId);
        Check("...its ticket says so", said.Any(n => n.Contains("before anybody answered")));
        Check("...and Pat is a client of their own name", client?.Name == "Pat Jones");
    }

    Console.WriteLine();
    Console.WriteLine("== the same person again ==");
    var third = await Post(Form(name: "J. Smith", company: "", email: "JANE@acme.test", phone: "", issue: "Still the password thing"));
    var id3 = Regex.Match(third, "data-id=\"([^\"]+)\"").Groups[1].Value;
    var token3 = Regex.Match(third, "data-token=\"([^\"]+)\"").Groups[1].Value;
    var page3 = await RawWs.Open($"ws://127.0.0.1:{port}/ws/web?tenant={tenantId}&id={id3}&token={token3}");
    await sue.Next<RingMsg>();
    await Task.Delay(600);
    await using (var db = new PlatformDb(dbOptions))
    {
        var t = await db.Tickets.FirstOrDefaultAsync(x => x.DeviceId == id3);
        var client = t?.ClientId == null ? null : await db.Clients.FirstOrDefaultAsync(c => c.Id == t.ClientId);
        Check("known by their email (in any case): Acme Ltd again, no new client", client?.Name == "Acme Ltd" && await db.Clients.CountAsync(c => c.Name == "Acme Ltd") == 1);
        Check("...under the name the company already has for them", t?.ContactName == "Jane Smith", t?.ContactName ?? "");
    }
    await page3.Close();

    Console.WriteLine();
    Console.WriteLine("== the address that asks too often ==");
    // Three good requests so far from this address (Jane, Pat, Jane again): two more are let through.
    var fourth = await Post(Form(name: "Four"));
    var fifth = await Post(Form(name: "Five"));
    var sixth = await Post(Form(name: "Six"));
    Check($"the 4th and {HelpModel.PerAddressPerHour}th requests of the hour from one address are let through", fourth.Contains("data-token") && fifth.Contains("data-token"));
    Check("...the next is refused, with a phone number to call instead", sixth.Contains("several requests") && !sixth.Contains("data-token"));

    Console.WriteLine();
    Console.WriteLine("== the download page ==");
    var get = await anon.GetStringAsync("/get/open-it");
    Check("the app's download page offers browser help when it is open", get.Contains("/help/open-it"));
    Check("...and not for a company that has it switched off", !(await anon.GetStringAsync("/get/off-it")).Contains("/help/off-it"));
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

// ---------------------------------------------------------------------------------------------
// --browser: a real browser fills in the form and shares a screen; its H.264 is decoded here with the
// console's own decoder and the picture is looked at.
static async Task<bool> BrowserMode(int port, TechConsole sue, string sueToken, DbContextOptions<PlatformDb> dbOptions, Action<string, bool, string> check)
{
    Console.WriteLine($"   Open http://127.0.0.1:{port}/help/open-it in a browser, fill in the form, and share a screen.");
    Console.WriteLine("   (Waiting up to 5 minutes for it to ring.)");
    var ring = await sue.Next<RingMsg>(300000);
    check("a real browser's request rings the console", ring != null, ring?.Call.ContactName ?? "");
    if (ring == null) return false;
    sue.Ws.Send(new AcceptCallMsg { CallId = ring.Call.CallId });
    var ready = await sue.Next<SessionReadyMsg>();
    check("answered: a view-only session", ready?.Session.ViewOnly == true, "");
    var viewer = await Viewer(port, ready!.Session.SessionId, sueToken);
    Console.WriteLine("   Answered. Now share a screen in the browser. (Waiting up to 5 minutes.)");
    var mons = await viewer.Next<MonitorsMsg>(300000);
    check("the browser announces what it shared", mons?.Monitors.Count == 1, mons == null ? "" : $"{mons.Monitors[0].Width} × {mons.Monitors[0].Height}");
    if (mons == null) return false;
    viewer.Ws.Send(new SubscribeMsg { Monitor = 0, On = true, Codecs = new() { VideoCodec.H264, VideoCodec.JpegTiles } });
    var frames = new List<byte[]>();
    var until = DateTime.UtcNow.AddSeconds(10);
    while (DateTime.UtcNow < until) { var f = await viewer.NextBinary(1000); if (f != null) frames.Add(f); }
    var video = frames.Where(f => f[0] == BinKind.Video).ToList();
    check("H.264 frames arrive from the browser", video.Count > 3, $"{video.Count} in 10 s, {frames.Sum(f => f.Length) / 10 / 1024} KB/s");
    var first = video.Count > 0 ? Bin.ParseVideo(video[0]) : default;
    check("...the first is a key frame", video.Count > 0 && first.keyFrame, "");
    if (video.Count == 0) return false;
    using var dec = new RemoteSupport.Media.H264Decoder(first.width, first.height);
    int decoded = 0;
    foreach (var f in video) { var p = Bin.ParseVideo(f); if (dec.TryDecode(p.bitstream.Span, p.keyFrame)) decoded++; }
    check("the CONSOLE's own decoder decodes them", decoded > 0, $"{decoded} of {video.Count} → {dec.Width} × {dec.Height} ({dec.Name})");
    if (decoded > 0)
    {
        // The middle pixel, as a person would describe it.
        int x = dec.Width / 2, y = dec.Height / 2, o = (y * dec.Width + x) * 4;
        var px = dec.Bgra.Length > o + 2 ? $"R{dec.Bgra[o + 2]} G{dec.Bgra[o + 1]} B{dec.Bgra[o]}" : "?";
        Console.WriteLine($"        the middle of the picture is {px}");
        try
        {
            using var bmp = new System.Drawing.Bitmap(dec.Width, dec.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            var bits = bmp.LockBits(new System.Drawing.Rectangle(0, 0, dec.Width, dec.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
            System.Runtime.InteropServices.Marshal.Copy(dec.Bgra, 0, bits.Scan0, Math.Min(dec.Bgra.Length, bits.Stride * dec.Height));
            bmp.UnlockBits(bits);
            var path = Path.Combine(Path.GetTempPath(), "websupport-decoded.png");
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"        picture: {path}");
        }
        catch (Exception ex) { Console.WriteLine($"        (no picture: {ex.Message})"); }
    }
    viewer.Ws.Send(new ChatMsg { Text = "I can see it. Thanks!" });
    await Task.Delay(1500);
    viewer.Ws.Send(new EndSessionMsg { SessionId = ready.Session.SessionId });
    await Task.Delay(1500);
    return true;
}

// ---------------------------------------------------------------------------------------------
// --browser-voice (3.3, 2026-09-30): a real browser, talking. This end is the technician; the
// person at the page presses its buttons when asked. It proves, with numbers:
//   - the page hears Talk WITHOUT sharing a screen (its host socket opens at the answer), which is
//     what lets a phone talk at all;
//   - its answers reach the technician's window, in the hub's name for the technician;
//   - the technician's voice frames reach the page (read rspHelp.state().voiceIn there);
//   - with a microphone, the page's capture, resampling and framing deliver 16 kHz mono at 50
//     frames a second, and a 1 kHz tone put into it comes out of the frames here (Goertzel).
//     A pretend microphone — an oscillator in place of getUserMedia — carries the tone, so this
//     is a pass or a fail rather than "I heard something"; a real one is a hardware check.
static async Task<bool> BrowserVoiceMode(int port, TechConsole sue, string sueToken, Action<string, bool, string> check)
{
    Console.WriteLine($"   VOICE: open http://127.0.0.1:{port}/help/open-it, fill in the form, and do NOT share a screen.");
    var ring = await sue.Next<RingMsg>(300000);
    check("a real browser's request rings the console", ring != null, ring?.Call.ContactName ?? "");
    if (ring == null) return false;
    sue.Ws.Send(new AcceptCallMsg { CallId = ring.Call.CallId });
    var ready = await sue.Next<SessionReadyMsg>();
    if (ready == null) { check("answered", false, ""); return false; }
    var viewer = await Viewer(port, ready.Session.SessionId, sueToken);
    await Task.Delay(1500);

    viewer.Ws.Send(new VoiceTalkMsg { On = true, Technician = "Pretender" });
    var ask = await viewer.Next<VoiceStateMsg>(v => v.Call && v.Mic == VoiceMic.Asking, 10000);
    check("the page hears Talk with NOTHING shared — its host socket opened at the answer", ask != null, "");
    check("...and names the technician as the hub does", ask?.Technicians == "Sue Tech", ask?.Technicians ?? "");
    Console.WriteLine("   Press \"Not now\" / \"Just listen\" on the page.");
    var declined = await viewer.Next<VoiceStateMsg>(v => v.Mic == VoiceMic.Declined, 300000);
    check("\"not now\" reaches the technician", declined != null, "");

    uint seq = 0;
    var start = DateTime.UtcNow; long sent = 0;
    while (DateTime.UtcNow - start < TimeSpan.FromSeconds(2))
    {
        long due = (long)((DateTime.UtcNow - start).TotalMilliseconds / VoiceWire.FrameMs);
        while (sent <= due)
        {
            var pcm = new byte[VoiceWire.FrameSamples * 2];
            for (int i = 0; i < VoiceWire.FrameSamples; i++)
            {
                short s = (short)(3000 * Math.Sin(2 * Math.PI * 440 * (sent * VoiceWire.FrameSamples + i) / VoiceWire.Rate));
                pcm[i * 2] = (byte)(s & 0xFF); pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
            viewer.Ws.SendBinary(Bin.Voice(seq++, pcm));
            sent++;
        }
        await Task.Delay(5);
    }
    Console.WriteLine($"   Sent {sent} frames of the technician's voice (440 Hz): the page's rspHelp.state().voiceIn should be about {sent}.");
    Console.WriteLine("   Now give the page a microphone and press \"Use my microphone\".");
    var on = await viewer.Next<VoiceStateMsg>(v => v.Mic is VoiceMic.On or VoiceMic.Unavailable, 300000);
    check("the page's microphone is on", on?.Mic == VoiceMic.On, on == null ? "" : on.Mic + " " + on.Detail);
    if (on?.Mic != VoiceMic.On) return false;

    await Task.Delay(500);
    while (await viewer.NextBinary(10) != null) { }   // the first half-second: the page's start-up
    var samples = new List<short>();
    int frames = 0; (int rate, int ch, byte fmt) format = default;
    var until = DateTime.UtcNow.AddSeconds(3);
    while (DateTime.UtcNow < until)
    {
        var f = await viewer.NextBinary(200);
        if (f == null || f.Length <= Bin.AudioHeader || f[0] != BinKind.Voice) continue;
        var (_, r, c, fm, pcm) = Bin.ParseAudio(f);
        format = (r, c, fm); frames++;
        var s = pcm.ToArray();
        for (int i = 0; i + 1 < s.Length; i += 2) samples.Add((short)(s[i] | (s[i + 1] << 8)));
    }
    check("the caller's voice arrives at 50 frames a second", frames >= 120 && frames <= 180, $"{frames} in 3 s");
    check("...as 16 kHz mono 16-bit", format == (VoiceWire.Rate, 1, AudioFormat.Pcm16), format.ToString());
    var x = samples.Select(v => v / 32768.0).ToArray();
    double g1k = Goertzel(x, 1000), g700 = Goertzel(x, 700), g1300 = Goertzel(x, 1300);
    Console.WriteLine($"        goertzel: 1000 Hz={g1k:F5}  700={g700:F5}  1300={g1300:F5}");
    check("...and a 1 kHz tone put into the page's microphone comes out of the frames here", g1k > 0.001 && g1k > 8 * Math.Max(g700, g1300), "");

    Console.WriteLine("   Now press \"Turn my microphone off\".");
    var off = await viewer.Next<VoiceStateMsg>(v => v.Mic == VoiceMic.Declined, 300000);
    check("turning it off reaches the technician", off != null, "");
    await Task.Delay(400);
    while (await viewer.NextBinary(10) != null) { }
    int after = 0;
    var quiet = DateTime.UtcNow.AddSeconds(1);
    while (DateTime.UtcNow < quiet) { var f = await viewer.NextBinary(100); if (f is { Length: > 0 } && f[0] == BinKind.Voice) after++; }
    check("...and the caller's voice stops", after <= 1, $"{after} frames after");

    viewer.Ws.Send(new VoiceTalkMsg { On = false });
    var gone = await viewer.Next<VoiceStateMsg>(v => !v.Call, 8000);
    check("Talk off: the page ends its side of the call", gone != null, "");
    viewer.Ws.Send(new EndSessionMsg { SessionId = ready.Session.SessionId });
    await Task.Delay(1500);
    return true;
}

static double Goertzel(double[] x, double freq)
{
    int n = x.Length; if (n < 256) return 0;
    double k = Math.Round(n * freq / VoiceWire.Rate), w = 2 * Math.PI * k / n, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
    foreach (var v in x) { double s0 = v + c * s1 - s2; s2 = s1; s1 = s0; }
    return Math.Sqrt(Math.Max(0, s1 * s1 + s2 * s2 - c * s1 * s2)) / (n / 2.0);
}

static byte[] JpegTile(int w, int h)
{
    var jpg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 0xFF, 0xD9 };
    var buf = new byte[9 + 20 + jpg.Length];
    buf[0] = BinKind.Tiles; BitConverter.TryWriteBytes(buf.AsSpan(1), 0); BitConverter.TryWriteBytes(buf.AsSpan(5), 1);
    BitConverter.TryWriteBytes(buf.AsSpan(9), 0); BitConverter.TryWriteBytes(buf.AsSpan(13), 0);
    BitConverter.TryWriteBytes(buf.AsSpan(17), w); BitConverter.TryWriteBytes(buf.AsSpan(21), h); BitConverter.TryWriteBytes(buf.AsSpan(25), jpg.Length);
    jpg.CopyTo(buf, 29);
    return buf;
}

static async Task<(TechConsole, string)> Sign(int port, string email)
{
    using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    var lr = await hc.PostAsJsonAsync($"http://127.0.0.1:{port}/api/login", new { email, password = "web-check-2026" });
    lr.EnsureSuccessStatusCode();
    var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
    var cws = new ClientWebSocket();
    await cws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/tech?token={Uri.EscapeDataString(tok)}"), CancellationToken.None);
    var c = new TechConsole(new WsConnection(cws));
    _ = c.Ws.RunAsync(CancellationToken.None);
    await Task.Delay(500);
    c.Clear();
    return (c, tok);
}

static async Task<TechConsole> Viewer(int port, string sessionId, string token)
{
    var cws = new ClientWebSocket();
    await cws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={sessionId}&token={Uri.EscapeDataString(token)}"), CancellationToken.None);
    var c = new TechConsole(new WsConnection(cws));
    _ = c.Ws.RunAsync(CancellationToken.None);
    await Task.Delay(300);
    return c;
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

/// <summary>A console (or a session viewer) and everything it has been sent, in order.</summary>
file sealed class TechConsole
{
    public WsConnection Ws { get; }
    private readonly List<Msg> _got = new();
    private readonly List<byte[]> _bin = new();
    public TechConsole(WsConnection ws)
    {
        Ws = ws;
        ws.OnMessage += m => { lock (_got) _got.Add(m); };
        ws.OnBinary += b => { lock (_bin) _bin.Add(b.ToArray()); };
    }
    public void Clear() { lock (_got) _got.Clear(); }

    public async Task<T?> Next<T>(int ms = 8000) where T : Msg => await Next<T>(_ => true, ms);
    public async Task<T?> Next<T>(Func<T, bool> ok, int ms = 8000) where T : Msg
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
    public async Task<byte[]?> NextBinary(int ms = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            lock (_bin) if (_bin.Count > 0) { var b = _bin[0]; _bin.RemoveAt(0); return b; }
            await Task.Delay(20);
        }
        return null;
    }
}

/// <summary>The browser's side, spoken as raw text exactly as help.js writes and reads it.</summary>
file sealed class RawWs
{
    private readonly ClientWebSocket _ws = new();
    private readonly List<JsonElement> _got = new();
    public static async Task<RawWs> Open(string url)
    {
        var r = new RawWs();
        await r._ws.ConnectAsync(new Uri(url), CancellationToken.None);
        _ = r.Loop();
        return r;
    }
    private async Task Loop()
    {
        var buf = new byte[1 << 20];
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                var ms = new MemoryStream();
                WebSocketReceiveResult res;
                do { res = await _ws.ReceiveAsync(buf, CancellationToken.None); ms.Write(buf, 0, res.Count); } while (!res.EndOfMessage);
                // Answer a close, as a browser does by itself: a server that closes waits for it.
                if (res.MessageType == WebSocketMessageType.Close) { try { await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { } break; }
                if (res.MessageType == WebSocketMessageType.Text)
                    lock (_got) _got.Add(JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray())).RootElement.Clone());
            }
        }
        catch { }
    }
    public Task Send(string json) => _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
    public Task SendBinary(byte[] b) => _ws.SendAsync(b, WebSocketMessageType.Binary, true, CancellationToken.None);
    public async Task Close() { try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { } }
    public async Task<JsonElement?> Next(Func<JsonElement, bool> ok, int ms = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            lock (_got)
            {
                var i = _got.FindIndex(e => { try { return ok(e); } catch { return false; } });
                if (i >= 0) { var e = _got[i]; _got.RemoveAt(i); return e; }
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
