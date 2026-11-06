// =====================================================================================
// chatcheck - proves office chat (stage 3.8.1, 2026-09-30).
//
//   "everybody at the office is able to chat with each other so that when you chat and do either
//   the @ or select the person it pops up a chat window in the browser or something similar"
//
// His answers: one to one, and one group (Everyone); @mentions in ticket comments stay ticket
// comments; the browser shows a notification.
//
// Two halves. The SERVICE: who may be written to, what a conversation is, what is unread, what an
// @ means. The WIRE, on a platform of its own: a console sending through the hub and a signed-in
// portal page hearing it on its event stream, and back; that a message reaches exactly the people
// it concerns — never a third person, on either path; that a write from the portal needs its
// header; and that reading in one place moves the count in the other.
//
//   dotnet run -c Debug --project tools/chatcheck/chatcheck.csproj
// =====================================================================================

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
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
var dataDir = Path.Combine(Path.GetTempPath(), "rs-chatcheck-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dataDir);
var port = FreePort();
Console.WriteLine($"chatcheck - office chat; the wire half on a platform of its own on port {port}");
Console.WriteLine();

const string Sue = "Sue Tech", Bob = "Bob Tech", Tom = "Tom Tech", Olive = "Olive Office", Gone = "Gary Gone";
Process? server = null;
try
{
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
    await using (var db = new PlatformDb(options)) await HarnessDb.MigrateAsync(db);
    var factory = new Factory(options);
    var tenants = new TenantService(factory, new ConfigurationBuilder().Build(), dataDir, DataProtectionProvider.Create("chatcheck"));
    var (tenant, _) = await tenants.CreateAsync("Chat Check", "chat-check", 7);
    int tid = tenant!.Id;
    await tenants.AddTechnicianAsync(tid, "sue@chat.test", "chat-check-2026", Sue, TenantRole.Technician);
    await tenants.AddTechnicianAsync(tid, "bob@chat.test", "chat-check-2026", Bob, TenantRole.Technician);
    await tenants.AddTechnicianAsync(tid, "tom@chat.test", "chat-check-2026", Tom, TenantRole.Technician);
    await tenants.AddTechnicianAsync(tid, "olive@chat.test", "chat-check-2026", Olive, TenantRole.Billing);
    await tenants.AddTechnicianAsync(tid, "gary@chat.test", "chat-check-2026", Gone, TenantRole.Technician);
    await using (var db = new PlatformDb(options))
    {
        var g = await db.Technicians.FirstAsync(t => t.DisplayName == Gone);
        g.Disabled = true;
        await db.SaveChangesAsync();
    }

    // =========================================================== the service
    var chat = new StaffChatService(factory);
    var sent = new List<StaffMessage>(); var reads = new List<string>();
    chat.Sent += (t, m) => { lock (sent) sent.Add(m); };
    chat.ReadChanged += (t, p) => { lock (reads) reads.Add(p); };

    Console.WriteLine("== who may be written to ==");
    var people = await chat.PeopleAsync(tid);
    Check("everybody enabled is a person to chat with — the office administrator too, not the disabled",
        people.SequenceEqual(new[] { Bob, Olive, Sue, Tom }), string.Join(", ", people));
    Check("an empty message is REFUSED", (await chat.SendAsync(tid, Sue, Bob, "  ")).error.Length > 0);
    Check("one over the length is REFUSED", (await chat.SendAsync(tid, Sue, Bob, new string('x', StaffChatService.MaxLength + 1))).error.Length > 0);
    Check("to yourself is REFUSED", (await chat.SendAsync(tid, Sue, Sue, "hi")).error.Length > 0);
    Check("to somebody who does not work here is REFUSED", (await chat.SendAsync(tid, Sue, "Nobody", "hi")).error.Contains("Nobody"));
    Check("to somebody disabled is REFUSED", (await chat.SendAsync(tid, Sue, Gone, "hi")).error.Length > 0);
    Check("FROM somebody who is not one of the company's people is REFUSED", (await chat.SendAsync(tid, "Stranger", Bob, "hi")).error.Length > 0);
    var (cased, err) = await chat.SendAsync(tid, Sue, "bob tech", "  Printer on the second floor?  ");
    Check("a name in any case reaches them, stored as the company spells it, the text trimmed",
        cased is { To: Bob, From: Sue, Text: "Printer on the second floor?" }, err);
    Check("...and Sent was raised with it", sent.Count == 1 && sent[0].Id == cased!.Id);

    Console.WriteLine();
    Console.WriteLine("== conversations ==");
    await chat.SendAsync(tid, Bob, Sue, "Yes, jammed again.");
    await chat.SendAsync(tid, Tom, Bob, "Lunch?");
    await chat.SendAsync(tid, Olive, "", "Invoices go out at 3.");
    var sueBob = await chat.HistoryAsync(tid, Sue, Bob);
    Check("Sue and Bob's conversation is both directions, oldest first, and nothing else",
        sueBob.Select(m => m.Text).SequenceEqual(new[] { "Printer on the second floor?", "Yes, jammed again." }), string.Join(" | ", sueBob.Select(m => m.Text)));
    Check("...and Bob sees the same from his side", (await chat.HistoryAsync(tid, Bob, Sue)).Select(m => m.Id).SequenceEqual(sueBob.Select(m => m.Id)));
    Check("Tom's message to Bob is not in Sue's view of anything", !(await chat.HistoryAsync(tid, Sue, Tom)).Any() && !sueBob.Any(m => m.From == Tom));
    Check("Everyone is its own conversation", (await chat.HistoryAsync(tid, Tom, "")).Select(m => m.Text).SequenceEqual(new[] { "Invoices go out at 3." }));

    Console.WriteLine();
    Console.WriteLine("== what is unread ==");
    var ob = await chat.OverviewAsync(tid, Bob);
    int U(StaffChatOverviewMsg o, string w) => o.Threads.FirstOrDefault(t => t.With == w)?.Unread ?? -1;
    // Bob REPLIED to Sue, and a reply is written below what it answers: he has read it.
    Check("Bob: 1 from Tom, 1 on Everyone — and Sue's is read, because he replied to it", U(ob, Sue) == 0 && U(ob, Tom) == 1 && U(ob, "") == 1 && ob.TotalUnread == 2,
        string.Join(", ", ob.Threads.Select(t => $"{(t.With.Length == 0 ? "Everyone" : t.With)}={t.Unread}")));
    Check("...Sue has Bob's reply unread", U(await chat.OverviewAsync(tid, Sue), Bob) == 1);
    Check("...his own messages are never unread to him", U(await chat.OverviewAsync(tid, Olive), "") == 0);
    Check("...Everyone is listed first, even with nothing said", (await chat.OverviewAsync(tid, Tom)).Threads[0].With == "");
    Check("...the people he could chat with leave him out", !ob.People.Contains(Bob) && ob.People.Contains(Olive));
    reads.Clear();
    var lastSue = sueBob.Last().Id;
    await chat.MarkReadAsync(tid, Sue, Bob, lastSue);
    Check("Sue reading it clears it, and says so", U(await chat.OverviewAsync(tid, Sue), Bob) == 0 && reads.SequenceEqual(new[] { Sue }));
    reads.Clear();
    await chat.MarkReadAsync(tid, Sue, Bob, lastSue - 1);
    Check("reading BACKWARDS changes nothing, and says nothing", U(await chat.OverviewAsync(tid, Sue), Bob) == 0 && reads.Count == 0);
    await chat.SendAsync(tid, Bob, Sue, "Thanks.");
    Check("a new message after that is unread again", U(await chat.OverviewAsync(tid, Sue), Bob) == 1);

    Console.WriteLine();
    Console.WriteLine("== the @ ==");
    Check("\"@Sue Tech\" names Sue", StaffChatService.Mentions("Can @Sue Tech look?", Sue));
    Check("...in any case, before a comma", StaffChatService.Mentions("@sue tech, can you look", Sue));
    Check("...at the very end", StaffChatService.Mentions("over to @Sue Tech", Sue));
    Check("\"@Sue Technician\" does NOT name Sue Tech", !StaffChatService.Mentions("@Sue Technician", Sue));
    Check("a name without the @ does not", !StaffChatService.Mentions("Sue Tech will look", Sue));

    // =========================================================== the wire
    Console.WriteLine();
    Console.WriteLine("== over the wire: a console and a portal page ==");
    var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in new[] { "run", "-c", "Debug", "--project", Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj"), "--", "run" })
        psi.ArgumentList.Add(a);
    psi.Environment["RSP_Platform__DataDirectory"] = dataDir;
    HarnessDb.Use(psi.Environment, dataDir);
    psi.Environment["RSP_Security__RequireTwoFactor"] = "false";   // a throwaway platform: the password alone (HANDOVER §72)
    // Both: the platform's own settings file names a port, and RSP_Urls is what wins over it.
    psi.Environment["RSP_Urls"] = $"http://127.0.0.1:{port}";
    psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
    server = Process.Start(psi)!;
    server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    if (!await WaitForPort(port, 90000)) { Console.WriteLine("The platform never started listening."); return 1; }

    var sue = await Sign(port, "sue@chat.test");
    var tom = await Sign(port, "tom@chat.test");
    // Bob in the BROWSER: a portal cookie the way the console's Tickets button gets one.
    var bobConsole = await Sign(port, "bob@chat.test");
    bobConsole.Ws.Send(new PortalLinkRequestMsg { Path = "/portal/tickets" });
    var link = await bobConsole.Next<PortalLinkMsg>();
    var cookies = new CookieContainer();
    using var bob = new HttpClient(new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = true }) { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = Timeout.InfiniteTimeSpan };
    var enter = await bob.GetAsync(link!.Url);
    Check("Bob is signed in to the portal", enter.IsSuccessStatusCode && cookies.GetCookies(bob.BaseAddress!).Count > 0, $"{(int)enter.StatusCode}");
    await bobConsole.Ws.CloseAsync();
    await Task.Delay(300);

    var stream = new EventReader(bob, "/api/portal/chat/stream");
    await stream.Connected(8000);
    Check("the portal's event stream opens", stream.IsOpen);

    sue.Clear(); tom.Clear();
    sue.Ws.Send(new StaffChatSendMsg { To = Bob, Text = "Are you at your desk?" });
    var ev = await stream.Next("message");
    Check("Sue's console → Bob's portal page, live", ev?.GetProperty("message").GetProperty("text").GetString() == "Are you at your desk?"
        && ev?.GetProperty("message").GetProperty("from").GetString() == Sue);
    var sueOwn = await sue.Next<StaffChatMsg>();
    Check("...and back to Sue's own console as sent", sueOwn?.Message?.Text == "Are you at your desk?" && sueOwn.Error == "");
    Check("...but NOT to Tom's console", await tom.Next<StaffChatMsg>(1500) == null);

    var noHeader = await bob.PostAsJsonAsync("/api/portal/chat/send", new { to = Sue, text = "no header" });
    Check("a portal write without its X-Chat header is REFUSED (a page on another site cannot add one)", noHeader.StatusCode == HttpStatusCode.Unauthorized);
    Check("...and did not send", await sue.Next<StaffChatMsg>(1200) == null);

    sue.Clear();
    var reply = await Post(bob, "/api/portal/chat/send", new { to = Sue, text = "Yes — come over." });
    Check("Bob replies from the portal", reply.IsSuccessStatusCode);
    var got = await sue.Next<StaffChatMsg>();
    Check("...Sue's console hears it", got?.Message is { From: Bob, To: Sue, Text: "Yes — come over." } && !got.MentionsMe);
    var ov = await sue.Next<StaffChatOverviewMsg>();
    Check("...and her unread count comes with it", ov?.Threads.FirstOrDefault(t => t.With == Bob)?.Unread >= 1, $"{ov?.TotalUnread}");
    Check("the portal is told of its own message too (other tabs)", (await stream.Next("message"))?.GetProperty("message").GetProperty("from").GetString() == Bob);

    var refused = await Post(bob, "/api/portal/chat/send", new { to = "Nobody", text = "hi" });
    var refusedBody = await refused.Content.ReadAsStringAsync();
    Check("a refused send from the portal says why", refused.StatusCode == HttpStatusCode.BadRequest && refusedBody.Contains("Nobody"), refusedBody);

    Console.WriteLine();
    Console.WriteLine("== Everyone, and the @ ==");
    sue.Clear(); tom.Clear();
    sue.Ws.Send(new StaffChatSendMsg { To = "", Text = "Who has the spare charger? @Tom Tech" });
    var tomGot = await tom.Next<StaffChatMsg>();
    Check("Everyone reaches Tom, and says it names him", tomGot?.Message?.To == "" && tomGot.MentionsMe);
    var bobEv = await stream.Next("message");
    Check("...reaches Bob's page, NOT naming him", bobEv?.GetProperty("message").GetProperty("to").GetString() == "" && bobEv?.GetProperty("mentionsMe").GetBoolean() == false);
    var sueGot = await sue.Next<StaffChatMsg>();
    Check("...and Sue's own copy never says it names her", sueGot != null && !sueGot.MentionsMe);

    Console.WriteLine();
    Console.WriteLine("== reading in one place moves the count in the other ==");
    var overview = JsonDocument.Parse(await bob.GetStringAsync("/api/portal/chat")).RootElement;
    Check("the portal's list: who Bob is, and his unread",
        overview.GetProperty("me").GetString() == Bob && overview.GetProperty("totalUnread").GetInt32() >= 1, overview.GetProperty("totalUnread").ToString());
    int Unread(JsonElement o, string w) => o.GetProperty("threads").EnumerateArray().First(t => t.GetProperty("with").GetString() == w).GetProperty("unread").GetInt32();
    var hist = JsonDocument.Parse(await bob.GetStringAsync($"/api/portal/chat/history?with={Uri.EscapeDataString(Sue)}")).RootElement;
    Check("the portal's history of Bob and Sue", hist.EnumerateArray().Any(m => m.GetProperty("text").GetString() == "Are you at your desk?"));
    // He replied to Sue, so that one is read already; Everyone is not.
    Check("replying from the portal counted as reading Sue's", Unread(overview, Sue) == 0);
    Check("...Everyone is still unread for him", Unread(overview, "") >= 1, Unread(overview, "").ToString());
    var everyone = JsonDocument.Parse(await bob.GetStringAsync("/api/portal/chat/history?with=")).RootElement;
    var lastId = everyone.EnumerateArray().Last().GetProperty("id").GetInt64();
    var rd = await Post(bob, "/api/portal/chat/read", new { with = "", upTo = lastId });
    Check("Bob reads Everyone in the portal", rd.IsSuccessStatusCode);
    Check("...his page is told what he has read changed (another tab, the console)", await stream.Next("read") != null);
    var after = JsonDocument.Parse(await bob.GetStringAsync("/api/portal/chat")).RootElement;
    Check("...and Everyone is no longer unread for him", Unread(after, "") == 0);
    tom.Clear();
    tom.Ws.Send(new StaffChatReadMsg { With = "", UpToId = long.MaxValue / 2 });
    var tomOv = await tom.Next<StaffChatOverviewMsg>();
    Check("Tom reads Everyone in the console: his count is sent back to him", tomOv != null && tomOv.Threads.First(t => t.With == "").Unread == 0);
    tom.Clear();
    tom.Ws.Send(new StaffChatHistoryRequestMsg { With = Bob });
    var tomHist = await tom.Next<StaffChatHistoryMsg>();
    Check("Tom's console history with Bob holds only theirs", tomHist?.Messages.Select(m => m.Text).SequenceEqual(new[] { "Lunch?" }) == true,
        string.Join(" | ", tomHist?.Messages.Select(m => m.Text) ?? Array.Empty<string>()));

    Console.WriteLine();
    Console.WriteLine("== signed out ==");
    using var nobody = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    Check("the list, the history and the stream refuse a browser that is not signed in",
        (await nobody.GetAsync("/api/portal/chat")).StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Redirect or HttpStatusCode.Found
        && !(await nobody.GetAsync("/api/portal/chat/history?with=x")).IsSuccessStatusCode
        && !(await nobody.GetAsync("/api/portal/chat/stream")).IsSuccessStatusCode);
    stream.Stop();
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

static Task<HttpResponseMessage> Post(HttpClient c, string path, object body)
{
    var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
    req.Headers.Add("X-Chat", "1");
    return c.SendAsync(req);
}

static async Task<TechConsole> Sign(int port, string email)
{
    using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    var lr = await hc.PostAsJsonAsync($"http://127.0.0.1:{port}/api/login", new { email, password = "chat-check-2026" });
    lr.EnsureSuccessStatusCode();
    var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
    var cws = new ClientWebSocket();
    await cws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/tech?token={Uri.EscapeDataString(tok)}"), CancellationToken.None);
    var c = new TechConsole(new WsConnection(cws));
    _ = c.Ws.RunAsync(CancellationToken.None);
    await Task.Delay(500);
    c.Clear();
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

/// <summary>One signed-in console and everything it has been sent, in order.</summary>
file sealed class TechConsole
{
    public WsConnection Ws { get; }
    private readonly List<Msg> _got = new();
    public TechConsole(WsConnection ws) { Ws = ws; ws.OnMessage += m => { lock (_got) _got.Add(m); }; }
    public void Clear() { lock (_got) _got.Clear(); }

    public async Task<T?> Next<T>(int ms = 8000) where T : Msg
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            lock (_got)
            {
                var m = _got.OfType<T>().FirstOrDefault();
                if (m != null) { _got.Remove(m); return m; }
            }
            await Task.Delay(40);
        }
        return null;
    }
}

/// <summary>A portal page's server-sent event stream, read as the browser's EventSource would.</summary>
file sealed class EventReader
{
    private readonly List<(string name, string data)> _got = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _open = new();
    public bool IsOpen { get; private set; }

    public EventReader(HttpClient c, string path)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var res = await c.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
                IsOpen = res.IsSuccessStatusCode && res.Content.Headers.ContentType?.MediaType == "text/event-stream";
                _open.TrySetResult();
                if (!IsOpen) return;
                using var r = new StreamReader(await res.Content.ReadAsStreamAsync(_cts.Token), Encoding.UTF8);
                string name = "message"; var data = new StringBuilder();
                while (!_cts.IsCancellationRequested)
                {
                    var line = await r.ReadLineAsync(_cts.Token);
                    if (line == null) break;
                    if (line.Length == 0)
                    {
                        if (data.Length > 0) lock (_got) _got.Add((name, data.ToString()));
                        name = "message"; data.Clear();
                    }
                    else if (line.StartsWith("event: ")) name = line[7..];
                    else if (line.StartsWith("data: ")) data.Append(line[6..]);
                }
            }
            catch { _open.TrySetResult(); }
        });
    }

    public async Task Connected(int ms) => await Task.WhenAny(_open.Task, Task.Delay(ms));

    public async Task<JsonElement?> Next(string name, int ms = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            lock (_got)
            {
                var i = _got.FindIndex(e => e.name == name);
                if (i >= 0) { var e = _got[i]; _got.RemoveAt(i); return JsonDocument.Parse(e.data).RootElement; }
            }
            await Task.Delay(40);
        }
        return null;
    }

    public void Stop() => _cts.Cancel();
}

file sealed class Factory : IDbContextFactory<PlatformDb>
{
    private readonly DbContextOptions<PlatformDb> _o;
    public Factory(DbContextOptions<PlatformDb> o) => _o = o;
    public PlatformDb CreateDbContext() => new(_o);
}
