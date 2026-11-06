// =====================================================================================
// mentionwire - proves the console's @mention badge over a real socket (stage 3.7, 2026-09-24).
//
// mentioncheck proves the rule in the service. This proves the WIRE: a platform of its own, two
// technicians signed in as the console signs in, and the MentionCountMsg that moves the badge on
// the Tickets button. It exists because the badge is the whole of what the console shows, and a
// rule that is right in the database and never reaches the socket is a badge that never lights.
//
//   the count arrives when a console signs in
//   it arrives at the RIGHT console the moment somebody is asked, and nobody else's
//   it survives a reconnect, and clears the moment they reply on that ticket
//   the Tickets button may open "waiting on my reply", and nothing else with a query on it
//
//   dotnet run -c Debug --project tools/mentionwire/mentionwire.csproj
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
var dataDir = Path.Combine(Path.GetTempPath(), "rs-mentionwire-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dataDir);
var port = FreePort();
Console.WriteLine($"mentionwire - a platform of its own on port {port} (the live server is not touched)");
Console.WriteLine();

Process? server = null;
const string Pw = "mention-wire-2026", Sue = "Sue Tech", Bob = "Bob Tech";
try
{
    // ---- seed, through the services, before the platform opens the database ----
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
    await using (var db = new PlatformDb(options)) await HarnessDb.MigrateAsync(db);
    var factory = new Factory(options);
    var tenants = new TenantService(factory, new ConfigurationBuilder().Build(), dataDir, DataProtectionProvider.Create("mentionwire"));
    var (tenant, _) = await tenants.CreateAsync("Mention Wire", "mention-wire", 5);
    await tenants.AddTechnicianAsync(tenant!.Id, "sue@wire.test", Pw, Sue, TenantRole.Technician);
    await tenants.AddTechnicianAsync(tenant.Id, "bob@wire.test", Pw, Bob, TenantRole.Technician);
    var tickets = new TicketService(factory);
    var job = await tickets.CreateAsync(tenant.Id, "Bob's job", "", Bob, TicketSource.Manual, assignedTo: Bob);

    // ---- the platform ----
    var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in new[] { "run", "-c", "Debug", "--project", Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj"), "--", "run" })
        psi.ArgumentList.Add(a);
    psi.Environment["RSP_Platform__DataDirectory"] = dataDir;
    HarnessDb.Use(psi.Environment, dataDir);
    psi.Environment["RSP_Security__RequireTwoFactor"] = "false";   // a throwaway platform: the password alone (HANDOVER §72)
    psi.Environment["RSP_Urls"] = $"http://127.0.0.1:{port}";
    psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
    server = Process.Start(psi)!;
    server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    if (!await WaitForPort(port, 90000)) { Console.WriteLine("The platform never started listening."); return 1; }

    Console.WriteLine("== the badge ==");
    var sue = await Console_(port, "sue@wire.test");
    var bob = await Console_(port, "bob@wire.test");
    Check("Sue's console is told her count when she signs in, and it is 0", await sue.NextCount() == 0);
    Check("so is Bob's", await bob.NextCount() == 0);

    bob.Ws.Send(new TicketNoteAddMsg { TicketId = job.Id, Body = "@Sue Tech have you seen this model before?" });
    Check("Bob asks Sue on his ticket: Sue's badge goes to 1 at once", await sue.NextCount() == 1);
    Check("...and Bob's console is told nothing", await bob.NextCount(1500) == null);

    await sue.Ws.DisposeAsync();
    sue = await Console_(port, "sue@wire.test");
    Check("the flag survives Sue closing the console and opening it again", await sue.NextCount() == 1);

    sue.Ws.Send(new TicketNoteAddMsg { TicketId = job.Id, Body = "Yes — it's the hinge, swap the left one." });
    Check("Sue replies on the ticket: her badge goes back to 0", await sue.NextCount() == 0);

    // The @ list (2026-10-21): the console offers the people on the ticket first, so the server must send them.
    Console.WriteLine();
    Console.WriteLine("== the @ list ==");
    bob.Ws.Send(new TicketOpenMsg { TicketId = job.Id });
    var opened = await bob.NextDetail();
    Check("an opened ticket says who is on it, for the @ list to offer first",
        opened != null && string.Join(",", opened.OnTicket) == Bob, opened == null ? "no ticket came" : string.Join(",", opened.OnTicket));
    Check("...and everybody who can be asked, for below them",
        opened != null && opened.People.Contains(Sue) && opened.People.Contains(Bob), opened == null ? "" : string.Join(",", opened.People));

    Console.WriteLine();
    Console.WriteLine("== where the Tickets button may land ==");
    Check("\"waiting on my reply\" is allowed, query and all", PortalHandoffService.SafePath("/portal/tickets?filter=asked") == "/portal/tickets?filter=asked");
    Check("any OTHER query is dropped, as before", PortalHandoffService.SafePath("/portal/tickets?filter=all") == "/portal/tickets");
    Check("a look-alike that only starts the same is not let through",
        PortalHandoffService.SafePath("/portal/tickets?filter=asked&next=//evil.example.com") == "/portal/tickets");
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

static async Task<TechConsole> Console_(int port, string email)
{
    using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    var lr = await hc.PostAsJsonAsync($"http://127.0.0.1:{port}/api/login", new { email, password = "mention-wire-2026" });
    lr.EnsureSuccessStatusCode();
    var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
    var cws = new ClientWebSocket();
    await cws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/tech?token={Uri.EscapeDataString(tok)}"), CancellationToken.None);
    var c = new TechConsole(new WsConnection(cws));
    _ = c.Ws.RunAsync(CancellationToken.None);
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

/// <summary>One signed-in console, and the counts it has been sent, oldest first.</summary>
file sealed class TechConsole
{
    public WsConnection Ws { get; }
    private readonly System.Threading.Channels.Channel<int> _counts = System.Threading.Channels.Channel.CreateUnbounded<int>();
    private readonly System.Threading.Channels.Channel<TicketDetail> _details = System.Threading.Channels.Channel.CreateUnbounded<TicketDetail>();
    public TechConsole(WsConnection ws)
    {
        Ws = ws;
        ws.OnMessage += m =>
        {
            if (m is MentionCountMsg mc) _counts.Writer.TryWrite(mc.Count);
            if (m is TicketDetailMsg td) _details.Writer.TryWrite(td.Detail);
        };
    }

    /// <summary>The next ticket this console is sent, or null if none arrives in time.</summary>
    public async Task<TicketDetail?> NextDetail(int ms = 8000)
    {
        using var cts = new CancellationTokenSource(ms);
        try { return await _details.Reader.ReadAsync(cts.Token); } catch (OperationCanceledException) { return null; }
    }

    /// <summary>The next count this console is sent, or null if none arrives in time.</summary>
    public async Task<int?> NextCount(int ms = 8000)
    {
        using var cts = new CancellationTokenSource(ms);
        try { return await _counts.Reader.ReadAsync(cts.Token); } catch (OperationCanceledException) { return null; }
    }
}

file sealed class Factory : IDbContextFactory<PlatformDb>
{
    private readonly DbContextOptions<PlatformDb> _o;
    public Factory(DbContextOptions<PlatformDb> o) => _o = o;
    public PlatformDb CreateDbContext() => new(_o);
}
