// =====================================================================================
// billingwire - proves the office is told when a job lands in the billing queue, and the call out's
// Reschedule over the console's socket (2026-10-05).
//
//   "Two things need amending. Call outs need the option to reschedule and when something is
//   assigned to billing, all office administrators should be notified."
//
// His answers: a badge AND a pop-up; Reschedule asks a new date and time and why, at Call out booked.
//
// The RULE: who is told — every office administrator, or the supervisors and owner of a company with
// none (answer 16), never both. The WIRE, on a platform of its own: a technician's console moving a
// job into the queue; the office administrator's PORTAL page (they have no console) and an office
// administrator's console hearing it at once — the count and the job — and nobody else hearing it;
// a step within the queue moving the count with no pop-up; the person who put it there never popped
// at; one company never hearing another's. calloutcheck proves Reschedule's rules; this proves the
// date and time cross the console's socket.
//
//   dotnet run -c Debug --project tools/billingwire/billingwire.csproj
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
var dataDir = Path.Combine(Path.GetTempPath(), "rs-billingwire-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dataDir);
var port = FreePort();
Console.WriteLine($"billingwire - the billing queue's badge and pop-up; a platform of its own on port {port}");
Console.WriteLine();

const string Pw = "billing-wire-2026";
const string Sue = "Sue Tech", Olive = "Olive Office", Oscar = "Oscar Office", Sam = "Sam Supervisor";
const string Nat = "Nat Supervisor", Tess = "Tess Tech";
Process? server = null;
try
{
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
    await using (var db = new PlatformDb(options)) await HarnessDb.MigrateAsync(db);
    var factory = new Factory(options);
    var tenants = new TenantService(factory, new ConfigurationBuilder().Build(), dataDir, DataProtectionProvider.Create("billingwire"));
    // A: an office administrator (portal only), one who is also a technician (a console), a supervisor.
    var (a, _) = await tenants.CreateAsync("Billing Wire", "billing-wire", 7);
    await tenants.AddTechnicianAsync(a!.Id, "sue@bw.test", Pw, Sue, TenantRole.Technician);
    await tenants.AddTechnicianAsync(a.Id, "olive@bw.test", Pw, Olive, TenantRole.Billing);
    await tenants.AddTechnicianAsync(a.Id, "oscar@bw.test", Pw, Oscar, TenantRole.Billing | TenantRole.Technician);
    await tenants.AddTechnicianAsync(a.Id, "sam@bw.test", Pw, Sam, TenantRole.Admin);
    // B: no office administrator at all — the queue falls to the supervisor.
    var (b, _) = await tenants.CreateAsync("No Office", "no-office", 7);
    await tenants.AddTechnicianAsync(b!.Id, "nat@bw.test", Pw, Nat, TenantRole.Admin);
    await tenants.AddTechnicianAsync(b.Id, "tess@bw.test", Pw, Tess, TenantRole.Technician);

    var tickets = new TicketService(factory);
    async Task<Ticket> CallOut(int tenantId, string subject, string tech) =>
        await tickets.CreateAsync(tenantId, subject, "", tech, TicketSource.Manual, assignedTo: tech, category: TicketCategory.CallOut,
            extra: x => { x.SiteAddress = "1 Main Road"; x.AppointmentUtc = DateTime.UtcNow.Date.AddDays(1).AddHours(7); });
    var job = await CallOut(a.Id, "Printer jams", Sue);
    var bJob = await CallOut(b.Id, "Server room", Tess);
    var bJob2 = await CallOut(b.Id, "Second site", Tess);
    async Task<Ticket> Get(int id) { await using var db = new PlatformDb(options); return await db.Tickets.FirstAsync(t => t.Id == id); }

    // =========================================================== the rule
    Console.WriteLine("== who is told ==");
    Check("an office administrator is told", Permissions.HearsOfBilling(TenantRole.Billing, true));
    Check("a supervisor is NOT, beside an office administrator — it is not their work", !Permissions.HearsOfBilling(TenantRole.Admin, true));
    Check("a supervisor IS, in a company with none (answer 16)", Permissions.HearsOfBilling(TenantRole.Admin, false));
    Check("...and so is an owner who also supervises", Permissions.HearsOfBilling(TenantRole.OwnerSupervisor, false));
    Check("...but never an owner-only login, which does not work the queue (2026-10-15)", !Permissions.HearsOfBilling(TenantRole.Owner, false));
    Check("a technician never is", !Permissions.HearsOfBilling(TenantRole.Technician, true) && !Permissions.HearsOfBilling(TenantRole.Technician, false));
    var told = (await tickets.BillingNotifiedAsync(a.Id)).OrderBy(x => x).ToList();
    Check("company A tells both its office administrators, and nobody else", told.SequenceEqual(new[] { Olive, Oscar }), string.Join(", ", told));
    var toldB = await tickets.BillingNotifiedAsync(b.Id);
    Check("company B, with none, tells its supervisor", toldB.SequenceEqual(new[] { Nat }), string.Join(", ", toldB));
    Check("the badge is a number for Olive, and nothing at all for Sue or Sam",
        await tickets.BillingBadgeForAsync(a.Id, Olive) == 0 && await tickets.BillingBadgeForAsync(a.Id, Sue) == null && await tickets.BillingBadgeForAsync(a.Id, Sam) == null);

    // =========================================================== the platform
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

    Console.WriteLine();
    Console.WriteLine("== signing in ==");
    var sue = await Sign(port, "sue@bw.test");
    var oscar = await Sign(port, "oscar@bw.test", keep: true);
    var sam = await Sign(port, "sam@bw.test");
    var first = await oscar.Next<BillingQueueMsg>();
    Check("Oscar's console is told the count when he signs in, and it is 0, with nothing popping", first is { Count: 0, Arrived: null });
    Check("Sam's console is told nothing — there are office administrators", await sam.Next<BillingQueueMsg>(1500) == null);
    Check("...nor Sue's", await sue.Next<BillingQueueMsg>(500) == null);

    // Olive has no console: her portal page, signed in through its own form.
    var olive = await Portal(port, "olive@bw.test");
    var olivePage = await olive.GetStringAsync("/portal/tickets");
    Check("Olive is signed in to the portal", olivePage.Contains("Billing queue"));
    Check("...and her page carries the badge, hidden at 0", olivePage.Contains("id=\"billingBadge\"") && olivePage.Contains(">0 to do<"));
    var suePortal = await Portal(port, "sue@bw.test");
    Check("Sue's portal page has no badge", !(await suePortal.GetStringAsync("/portal/tickets")).Contains("billingBadge"));
    var stream = new EventReader(olive, "/api/portal/chat/stream");
    await stream.Connected(8000);
    Check("Olive's live stream opens", stream.IsOpen);
    await Task.Delay(300);

    Console.WriteLine();
    Console.WriteLine("== reschedule, from the console ==");
    var newAt = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(5).AddHours(11).AddMinutes(30), DateTimeKind.Utc);
    sue.Ws.Send(new TicketActMsg { TicketId = job.Id, Action = Workflow.Reschedule, AppointmentUtc = newAt, Reason = "Customer is out on Tuesday." });
    var moved = await sue.Next<TicketNoticeMsg>();
    Check("Sue's Reschedule is accepted", moved is { Error: "" }, moved?.Error ?? "no answer");
    var afterMove = await Get(job.Id);
    Check("...the new date and time crossed the socket exactly, and the job is still at Call out booked",
        afterMove.AppointmentUtc == newAt && afterMove.Stage == Workflow.CallOutFirst, $"{afterMove.AppointmentUtc:u} / {afterMove.Stage}");
    sue.Ws.Send(new TicketActMsg { TicketId = job.Id, Action = Workflow.Reschedule, AppointmentUtc = DateTime.UtcNow.AddDays(-1), Reason = "x" });
    var refused = await sue.Next<TicketNoticeMsg>();
    Check("...one in the past comes back refused, in words", refused?.Error.Contains("passed") == true, refused?.Error ?? "no answer");
    Check("moving an appointment tells the office nothing", await oscar.Next<BillingQueueMsg>(1500) == null && await stream.Next("billing", 300) == null);

    Console.WriteLine();
    Console.WriteLine("== a job comes into the billing queue ==");
    sue.Ws.Send(new TicketActMsg { TicketId = job.Id, Action = "callout-work-done", Details = "Cleared the paper path.", Minutes = "45" });
    Check("Work done is accepted", (await sue.Next<TicketNoticeMsg>()) is { Error: "" });
    Check("...and a step on the bench tells the office nothing", await oscar.Next<BillingQueueMsg>(1500) == null && await stream.Next("billing", 300) == null);
    var number = (await Get(job.Id)).Number;

    sue.Ws.Send(new TicketActMsg { TicketId = job.Id, Action = "callout-invoice", Amount = "650" });
    Check("Sue sends it to be invoiced", (await sue.Next<TicketNoticeMsg>()) is { Error: "" });
    var pop = await oscar.Next<BillingQueueMsg>();
    Check("Oscar's console hears at once: 1 waiting, and WHICH job — it pops up",
        pop is { Count: 1, Arrived: { } arr } && arr.TicketId == job.Id && arr.Number == number && arr.Subject == "Printer jams"
        && arr.Stage == "To be invoiced" && arr.By == Sue,
        pop == null ? "nothing" : $"{pop.Count} / #{pop.Arrived?.Number} {pop.Arrived?.Stage} by {pop.Arrived?.By}");
    var ev = await stream.Next("billing");
    Check("Olive's portal page hears it too: the count and the job",
        ev is { } e && e.GetProperty("count").GetInt32() == 1 && e.GetProperty("arrived").ValueKind == JsonValueKind.Object
        && e.GetProperty("arrived").GetProperty("number").GetInt32() == number && e.GetProperty("arrived").GetProperty("by").GetString() == Sue
        && e.GetProperty("arrived").GetProperty("stage").GetString() == "To be invoiced",
        ev?.ToString() ?? "nothing");
    Check("Sam hears nothing", await sam.Next<BillingQueueMsg>(1500) == null);
    Check("Sue, who sent it, hears nothing", await sue.Next<BillingQueueMsg>(300) == null);
    Check("a page Olive opens now says 1 to do", (await olive.GetStringAsync("/portal/tickets")).Contains(">1 to do<"));

    Console.WriteLine();
    Console.WriteLine("== the office works it ==");
    oscar.Ws.Send(new TicketActMsg { TicketId = job.Id, Action = "client-invoiced", Amount = "650", Reference = "INV-77" });
    Check("Oscar invoices it from his console", (await oscar.Next<TicketNoticeMsg>()) is { Error: "" });
    var down = await oscar.Next<BillingQueueMsg>();
    Check("the count goes back to 0 — invoiced and awaiting payment is waiting on the customer, not the office",
        down is { Count: 0 }, down == null ? "nothing" : down.Count.ToString());
    Check("...and nothing pops up for him: he did it, and it moved WITHIN the queue", down is { Arrived: null });
    var ev2 = await stream.Next("billing");
    Check("Olive's badge goes to 0 with no pop-up",
        ev2 is { } e2 && e2.GetProperty("count").GetInt32() == 0 && e2.GetProperty("arrived").ValueKind == JsonValueKind.Null, ev2?.ToString() ?? "nothing");

    Console.WriteLine();
    Console.WriteLine("== a company with no office administrator ==");
    var nat = await Sign(port, "nat@bw.test", keep: true);
    var tess = await Sign(port, "tess@bw.test");
    Check("Nat, the supervisor, is told the count when he signs in", (await nat.Next<BillingQueueMsg>()) is { Count: 0 });
    tess.Ws.Send(new TicketActMsg { TicketId = bJob.Id, Action = "callout-work-done", Details = "Patched the switch.", Minutes = "30" });
    await tess.Next<TicketNoticeMsg>();
    tess.Ws.Send(new TicketActMsg { TicketId = bJob.Id, Action = "callout-invoice", Amount = "300" });
    Check("Tess sends hers to be invoiced", (await tess.Next<TicketNoticeMsg>()) is { Error: "" });
    var natPop = await nat.Next<BillingQueueMsg>();
    Check("Nat's console pops it up", natPop is { Count: 1, Arrived: { By: Tess } }, natPop == null ? "nothing" : $"{natPop.Count} by {natPop.Arrived?.By}");
    Check("company A hears NOTHING of company B's", await oscar.Next<BillingQueueMsg>(1500) == null && await stream.Next("billing", 300) == null);
    tess.Ws.Send(new TicketActMsg { TicketId = bJob2.Id, Action = "callout-work-done", Details = "Ran a cable.", Minutes = "60" });
    await tess.Next<TicketNoticeMsg>();
    nat.Ws.Send(new TicketActMsg { TicketId = bJob2.Id, Action = "callout-invoice", Amount = "500" });
    Check("Nat steps in and sends the second one himself", (await nat.Next<TicketNoticeMsg>()) is { Error: "" });
    var natOwn = await nat.Next<BillingQueueMsg>();
    Check("...his count goes to 2, with no pop-up of his own doing", natOwn is { Count: 2, Arrived: null }, natOwn == null ? "nothing" : $"{natOwn.Count} / {natOwn.Arrived?.Number}");
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

/// <summary>A console signed in. <paramref name="keep"/> keeps what arrived with the sign-in (the count).</summary>
static async Task<TechConsole> Sign(int port, string email, bool keep = false)
{
    using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    var lr = await hc.PostAsJsonAsync($"http://127.0.0.1:{port}/api/login", new { email, password = "billing-wire-2026" });
    lr.EnsureSuccessStatusCode();
    var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
    var cws = new ClientWebSocket();
    await cws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/tech?token={Uri.EscapeDataString(tok)}"), CancellationToken.None);
    var c = new TechConsole(new WsConnection(cws));
    _ = c.Ws.RunAsync(CancellationToken.None);
    await Task.Delay(500);
    if (!keep) c.Clear();
    return c;
}

/// <summary>Signed in through the portal's own form, antiforgery token and all.</summary>
static async Task<HttpClient> Portal(int port, string email)
{
    var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true })
        { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = Timeout.InfiniteTimeSpan };
    var page = await c.GetStringAsync("/login");
    await c.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Email"] = email, ["Password"] = "billing-wire-2026", ["__RequestVerificationToken"] = Token(page) }));
    return c;
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
