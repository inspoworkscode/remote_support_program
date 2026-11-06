// =====================================================================================
// ticketwire - the console's ticket window, over a real socket (stage 3.7, 2026-09-24).
//
// ticketwindowprobe proves the window draws what it is sent. This proves what it is SENT, and
// what happens when it presses things: a platform of its own, consoles signed in the way the
// console signs in, and a workshop job driven through the messages the window uses.
//
//   the detail carries the stage, the buttons and the fields - and only the ones this person has
//   a button press is checked by the service: refused with words, or done and re-sent
//   "1:15" and "R 1 250,00" typed in the console mean what they mean in the portal
//   Work done, item suggestions, "did you mean", adding and removing quote lines
//   a colleague's console cannot touch the job, and cannot remove a line by naming another ticket
//   every console hears that the job moved
//   "Open in browser" may land on one ticket, and only on a ticket number
//
//   dotnet run -c Debug --project tools/ticketwire/ticketwire.csproj
// =====================================================================================

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
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
var dataDir = Path.Combine(Path.GetTempPath(), "rs-ticketwire-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dataDir);
var port = FreePort();
Console.WriteLine($"ticketwire - a platform of its own on port {port} (the live server is not touched)");
Console.WriteLine();

Process? server = null;
try
{
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
    await using (var db = new PlatformDb(options)) await HarnessDb.MigrateAsync(db);
    var factory = new Factory(options);
    var tenants = new TenantService(factory, new ConfigurationBuilder().Build(), dataDir, DataProtectionProvider.Create("ticketwire"));
    var (tenant, _) = await tenants.CreateAsync("Ticket Wire", "ticket-wire", 5);
    await tenants.AddTechnicianAsync(tenant!.Id, "sue@wire.test", "ticket-wire-2026", "Sue Tech", TenantRole.Technician);
    await tenants.AddTechnicianAsync(tenant.Id, "bob@wire.test", "ticket-wire-2026", "Bob Tech", TenantRole.Technician);
    var tickets = new TicketService(factory);
    var quotes = new QuoteService(factory);
    var job = await tickets.CreateAsync(tenant.Id, "Slow laptop", "", "Office", TicketSource.Manual, assignedTo: "Sue Tech", category: TicketCategory.Workshop);
    var bobsJob = await tickets.CreateAsync(tenant.Id, "Bob's laptop", "", "Office", TicketSource.Manual, assignedTo: "Bob Tech", category: TicketCategory.Workshop);
    await tickets.ActAsync(tenant.Id, bobsJob.Id, "Bob Tech", TenantRole.Technician, "inspection", new StageInput { Details = "x" });
    await tickets.ActAsync(tenant.Id, bobsJob.Id, "Bob Tech", TenantRole.Technician, "repairable");
    await quotes.AddItemAsync(tenant.Id, "DDR4 memory 8GB SO-DIMM", "Sue Tech");
    var (bobsLine, _) = await quotes.AddLineAsync(tenant.Id, bobsJob.Id, 1, "X-1", 1, null, "Bob Tech", TenantRole.Technician);

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

    var sue = await Sign(port, "sue@wire.test");
    var bob = await Sign(port, "bob@wire.test");

    Console.WriteLine("== what the window is sent ==");
    sue.Ws.Send(new TicketOpenMsg { TicketId = job.Id });
    var d = await sue.Next<TicketDetailMsg>();
    Check("the detail carries the stage and whose it is", d?.Detail.Stage == "awaiting-diagnosis" && d.Detail.WithWhom == "With Sue Tech", $"{d?.Detail.Stage} / {d?.Detail.WithWhom}");
    // Add another machine since 2026-09-30: at Booked in, whoever is on the job may add one beside it.
    Check("...and Sue's buttons: Inspection findings, Waiting on customer, Add another machine — and not Abandoned, which is the office's",
        d != null && d.Detail.Actions.Select(a => a.Key).SequenceEqual(new[] { "inspection", "wait-customer", "add-machine" }),
        string.Join(",", d?.Detail.Actions.Select(a => a.Key) ?? Array.Empty<string>()));
    Check("the photo counts come with it", d?.Detail.PhotosRequired == 3);

    bob.Ws.Send(new TicketOpenMsg { TicketId = job.Id });
    Check("Bob cannot even open Sue's job", (await bob.Next<ErrorMsg>())?.Message == "Ticket not found.");
    bob.Ws.Send(new TicketActMsg { TicketId = job.Id, Action = "inspection", Details = "x" });
    Check("...nor press its buttons", (await bob.Next<ErrorMsg>()) != null);

    Console.WriteLine();
    Console.WriteLine("== pressing things ==");
    sue.Clear(); bob.Clear();
    sue.Ws.Send(new TicketActMsg { TicketId = job.Id, Action = "inspection" });
    var n0 = await sue.Next<TicketNoticeMsg>();
    await sue.Next<TicketDetailMsg>();   // a refusal is answered with the ticket too; read it, or it answers the NEXT press
    Check("Inspection findings with nothing written is refused, in words", n0?.Error.Contains("inspection found") == true, n0?.Error ?? "");
    sue.Clear(); bob.Clear();
    sue.Ws.Send(new TicketActMsg { TicketId = job.Id, Action = "inspection", Details = "Fan full of dust; runs hot." });
    var n1 = await sue.Next<TicketNoticeMsg>();
    d = await sue.Next<TicketDetailMsg>();
    Check("Inspection findings: done, and the fresh ticket is sent back", n1?.Error == "" && d?.Detail.Stage == "inspected", $"{n1?.Error} / {d?.Detail.Stage}");
    Check("...its form is in the history, titled, and the crumb knows where it came from",
        d?.Detail.Notes.Any(x => x.FormTitle == "Inspection findings" && x.Body.StartsWith("Fan full of dust") && x.MovedToLabel == "Inspected") == true
        && d.Detail.PreviousStageLabel == "Booked in", d?.Detail.PreviousStageLabel ?? "");
    Check("every console hears the job moved — Bob's too", (await bob.Next<TicketChangedMsg>())?.Ticket.Id == job.Id);
    Check("Inspected offers Repairable and Not repairable, and no ending yet",
        d != null && d.Detail.Actions.Any(a => a.Key == "repairable") && d.Detail.Actions.Any(a => a.Key == "not-repairable") && !d.Detail.Actions.Any(a => a.IsDecision));

    sue.Clear();
    sue.Ws.Send(new TicketActMsg { TicketId = job.Id, Action = "solved-invoice", Minutes = "1:15", Amount = "R 1 250,00" });
    var n2 = await sue.Next<TicketNoticeMsg>();
    await sue.Next<TicketDetailMsg>();
    Check("an ending before Repairable is refused, in words", n2?.Error.Length > 0, n2?.Error ?? "");

    sue.Clear();
    sue.Ws.Send(new TicketActMsg { TicketId = job.Id, Action = "repairable" });
    Check("Repairable is recorded", (await sue.Next<TicketNoticeMsg>())?.Error == "");
    d = await sue.Next<TicketDetailMsg>();
    // No Quote tab yet: that waits for a quote (2026-09-27); the Quote needed step has its own item box.
    Check("at Repairable, Sue may edit the quote, and the three endings come at once — Clint, 2026-09-27",
        d != null && d.Detail.MayEditLines && !d.Detail.ShowQuote
        && new[] { "solved-invoice", "quote-needed", "do-not-invoice" }.All(k => d.Detail.Actions.Any(a => a.Key == k)));

    Console.WriteLine();
    Console.WriteLine("== the quote ==");
    sue.Clear();
    sue.Ws.Send(new ItemSuggestMsg { Term = "memory ddr" });
    var items = await sue.Next<ItemListMsg>();
    Check("\"memory ddr\" suggests the DDR4 item", items?.Items.Any(i => i.Name == "DDR4 memory 8GB SO-DIMM") == true);
    sue.Ws.Send(new QuoteLineAddMsg { TicketId = job.Id, ItemName = "SO-DIMM memory DDR4 8GB", PartNumber = "KVR26S19S8/8", SerialNumber = "SN-8GB-001", Quantity = 2 });
    var same = await sue.Next<TicketNoticeMsg>();
    d = await sue.Next<TicketDetailMsg>();
    Check("the same words in another order are quoted as the EXISTING item", same?.Error == "" && d?.Detail.QuoteLines.Count == 1
        && d.Detail.QuoteLines[0].ItemName == "DDR4 memory 8GB SO-DIMM" && d.Detail.QuoteLines[0].Quantity == 2 && d.Detail.QuoteLines[0].SerialNumber == "SN-8GB-001");
    sue.Clear();
    sue.Ws.Send(new QuoteLineAddMsg { TicketId = job.Id, ItemName = "DDR4 memory", SerialNumber = "SN-4GB-002", Quantity = 1 });
    var similar = await sue.Next<QuoteSimilarMsg>();
    Check("a near-miss comes back as \"did you mean\", holding the serial number typed", similar?.Similar.Any(i => i.Name == "DDR4 memory 8GB SO-DIMM") == true && similar.SerialNumber == "SN-4GB-002");
    sue.Ws.Send(new QuoteLineAddMsg { TicketId = job.Id, ItemName = "DDR4 memory", ConfirmNew = true, Quantity = 1 });
    await sue.Next<TicketNoticeMsg>();
    d = await sue.Next<TicketDetailMsg>();
    Check("...and is added once it is confirmed different", d?.Detail.QuoteLines.Count == 2);

    sue.Clear();
    sue.Ws.Send(new QuoteLineRemoveMsg { TicketId = job.Id, LineId = bobsLine!.Id });
    var wrong = await sue.Next<TicketNoticeMsg>();
    Check("a line cannot be removed by naming a ticket it is not on", wrong?.Error.Length > 0, wrong?.Error ?? "");
    sue.Ws.Send(new QuoteLineRemoveMsg { TicketId = job.Id, LineId = d!.Detail.QuoteLines[1].Id });
    await sue.Next<TicketNoticeMsg>();
    d = await sue.Next<TicketDetailMsg>();
    Check("her own line is removed", d?.Detail.QuoteLines.Count == 1);

    // 2026-09-29: the Item box is a dropdown, and the quote says which machine it is for.
    sue.Clear();
    sue.Ws.Send(new ItemSuggestMsg { Term = "" });
    var whole = await sue.Next<ItemListMsg>();
    Check("nothing typed brings the whole list, by name — the Item box as a dropdown",
        whole != null && whole.Term == "" && whole.Items.Count >= 2
        && whole.Items.Select(i => i.Name).SequenceEqual(whole.Items.Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal)),
        whole == null ? "nothing came" : string.Join(", ", whole.Items.Select(i => i.Name)));
    bob.Clear();
    bob.Ws.Send(new QuoteMachineMsg { TicketId = job.Id, Type = "Laptop", Make = "Not his" });
    Check("Bob cannot say which machine Sue's quote is for", (await bob.Next<ErrorMsg>())?.Message == "Ticket not found.");
    sue.Ws.Send(new QuoteMachineMsg { TicketId = job.Id, Type = "laptop", Make = "Asus", Model = "VivoBook 123", Serial = "SN123" });
    var mn = await sue.Next<TicketNoticeMsg>();
    d = await sue.Next<TicketDetailMsg>();
    Check("Sue says which machine the quote is for, and the fresh ticket carries it back",
        mn?.Error == "" && d?.Detail.QuoteMachine is { Type: "Laptop", Make: "Asus", Model: "VivoBook 123", Serial: "SN123", FromBooking: false }
        && d.Detail.MachineTypes.SequenceEqual(new[] { "Desktop", "Laptop", "All-in-one" }),
        $"{mn?.Error} / {d?.Detail.QuoteMachine.Type} {d?.Detail.QuoteMachine.Make} {d?.Detail.QuoteMachine.Model} {d?.Detail.QuoteMachine.Serial}");

    Console.WriteLine();
    Console.WriteLine("== numbers typed in the console ==");
    sue.Clear();
    sue.Ws.Send(new TicketActMsg { TicketId = job.Id, Action = "solved-invoice", Minutes = "1:15", Amount = "R 1 250,00" });
    var noWork = await sue.Next<TicketNoticeMsg>();
    await sue.Next<TicketDetailMsg>();
    Check("Solved without saying what was done is refused, in words", noWork?.Error.Contains("was done") == true, noWork?.Error ?? "");
    sue.Clear();
    const string work = "Replaced the thermal paste. @Bob Tech is the fan noise normal?";
    sue.Ws.Send(new TicketActMsg { TicketId = job.Id, Action = "solved-invoice", Details = work, Minutes = "1:15", Amount = "R 1 250,00" });
    var n3 = await sue.Next<TicketNoticeMsg>();
    d = await sue.Next<TicketDetailMsg>();
    Check("Solved, to be invoiced goes through", n3?.Error == "" && d?.Detail.Stage == "to-be-invoiced", n3?.Error ?? "");
    Check("...and the history has it as the Solved form, marked as the work done, written under Repairable",
        d?.Detail.Notes.Any(x => x.IsWorkDone && x.FormTitle == "Solved — to be invoiced" && x.StageLabel == "Repairable") == true);
    Check("...with what goes on the invoice sent as the work alone, no time or amount",
        d?.Detail.Notes.Any(x => x.IsWorkDone && x.WorkDoneText == work) == true,
        d?.Detail.Notes.FirstOrDefault(x => x.IsWorkDone)?.WorkDoneText ?? "");
    Check("\"1:15\" is 1 h 15 min and \"R 1 250,00\" is 1,250.00",
        d?.Detail.Facts.Any(f => f.Label == "Time spent" && f.Value == "1 h 15 min") == true
        && d.Detail.Facts.Any(f => f.Label == "To invoice" && f.Value == "1,250.00") == true,
        string.Join("; ", d?.Detail.Facts.Select(f => $"{f.Label}={f.Value}") ?? Array.Empty<string>()));
    // Machine collected stays hers: whoever is at the counter when the customer comes may hand it over.
    // Since 2026-09-30 it can also go to the customer instead, with a technician: Out for delivery. And since
    // 2026-10-13 she may go back over the step she made (pressed by mistake): Back to Repairable, drawn last.
    Check("in the billing queue, Sue's only buttons are Machine collected, Out for delivery and Back to Repairable, and she cannot edit the quote",
        d?.Detail.Actions.Select(a => a.Key).SequenceEqual(new[] { "collected", "send-for-delivery", "go-back" }) == true
        && d.Detail.Actions.Last().Label == "Back to Repairable" && d.Detail.MayEditLines == false && d.Detail.WithWhom == "In the billing queue",
        string.Join(",", d?.Detail.Actions.Select(a => a.Key + ":" + a.Label) ?? Array.Empty<string>()));

    sue.Clear();
    sue.Ws.Send(new TicketListRequestMsg { Filter = "mine" });
    var list = await sue.Next<TicketListMsg>();
    sue.Ws.Send(new TicketListRequestMsg { Filter = "all" });
    var all = await sue.Next<TicketListMsg>();
    Check("the list carries the stage in words", all?.Tickets.Any(t => t.Id == job.Id && t.StageLabel == "To be invoiced") == true);

    // ---- recurring tasks (2026-10-07): set up from the console's New ticket, cancelled from a ticket ----
    Console.WriteLine();
    Console.WriteLine("== recurring tasks, from the console ==");
    sue.Clear();
    sue.Ws.Send(new RecurringKindsRequestMsg());
    var kinds = await sue.Next<RecurringKindsMsg>();
    Check("New ticket is sent the company's types and everybody a task may go to",
        kinds != null && kinds.Kinds.SequenceEqual(new[] { "Maintenance", "Billing", "Readings" }) && kinds.People.SequenceEqual(new[] { "Bob Tech", "Sue Tech" }),
        kinds == null ? "nothing" : string.Join(",", kinds.Kinds) + " / " + string.Join(",", kinds.People));
    sue.Ws.Send(new TicketCreateMsg { Category = 5, Subject = "Daily backup check", RecurringKind = "Maintenance", Repeats = 0, AssignTo = "" });
    Check("a schedule with nobody to give it to is refused, with words", (await sue.Next<ErrorMsg>())?.Message.Contains("who this task goes to") == true);
    sue.Ws.Send(new TicketCreateMsg { Category = 5, Subject = "Daily backup check", RecurringKind = "Server room", Repeats = 0, AssignTo = "Sue Tech" });
    var set = await sue.Next<RecurringSetMsg>();
    Check("a daily one is set up and the console is told", set != null && set.TaskId > 0 && set.Message.StartsWith("\"Daily backup check\" is set up: every day."), set?.Message ?? "nothing");
    // The platform's company is in South Africa; before 06:00 there, today's waits for the morning.
    var saNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, CompanyClock.Find(CompanyClock.SouthAfrica));
    var opened = saNow.Hour >= RecurringTaskService.MorningHour ? await sue.Next<TicketDetailMsg>() : null;
    if (saNow.Hour < RecurringTaskService.MorningHour)
        Check("(before 06:00 in South Africa: today's opens in the morning, and the console is told when)", set?.Message.Contains("opens on the morning of") == true);
    else
    {
        Check("after 06:00, today's ticket opens at once and the console opens it",
            opened?.Detail is { Ticket.Category: 5, Stage: "recurring-due" } od && od.Recurring is { Kind: "Server room", Repeats: "Every day", MayChange: true },
            $"{opened?.Detail.Ticket.Category} {opened?.Detail.Stage} {opened?.Detail.Recurring?.Kind}");
        Check("...with Done and To be invoiced as its buttons", opened?.Detail.Actions.Select(a => a.Key).SequenceEqual(new[] { "recurring-done", "recurring-invoice" }) == true);
        var tid = opened!.Detail.Ticket.Id;
        bob.Clear();
        bob.Ws.Send(new RecurringCancelMsg { TaskId = set!.TaskId, TicketId = tid, Reason = "not mine" });
        Check("Bob cannot cancel it from Sue's ticket — he cannot touch the ticket", (await bob.Next<ErrorMsg>())?.Message == "Ticket not found.");
        bob.Ws.Send(new RecurringCancelMsg { TaskId = set.TaskId, Reason = "not mine" });
        Check("...nor by naming the task alone", (await bob.Next<ErrorMsg>())?.Message.Contains("Only the people on this task") == true);
        sue.Clear();
        sue.Ws.Send(new RecurringCancelMsg { TaskId = set.TaskId, TicketId = tid, Reason = " " });
        Check("Sue's cancel with no reason is refused, with words", (await sue.Next<TicketNoticeMsg>())?.Error == "Say why it is being cancelled.");
        // The refusal comes back as a notice AND the ticket again (AfterWorkflowChangeAsync), the ticket a moment later.
        // Taken here, so it is never mistaken below for the ticket after the real cancel — it was, one run in two on
        // PostgreSQL, once opening a ticket also asked whether Back is offered (2026-10-13).
        await sue.Next<TicketDetailMsg>();
        sue.Clear();
        sue.Ws.Send(new RecurringCancelMsg { TaskId = set.TaskId, TicketId = tid, Reason = "Moved to cloud backup" });
        var cn = await sue.Next<TicketNoticeMsg>();
        var after = await sue.Next<TicketDetailMsg>();
        Check("with a reason it is cancelled, and the ticket comes back saying so",
            cn?.Message.StartsWith("Recurring task cancelled") == true && after?.Detail.Recurring is { Cancelled: true, MayChange: false } && after.Detail.Recurring.Next.Contains("Moved to cloud backup"),
            $"{cn?.Message}{cn?.Error} / {after?.Detail.Recurring?.Next}");
    }
    bob.Ws.Send(new RecurringKindsRequestMsg());
    Check("the type Sue typed is now the company's", (await bob.Next<RecurringKindsMsg>())?.Kinds.Contains("Server room") == true);

    Console.WriteLine();
    Console.WriteLine("== Open in browser ==");
    Check("a recurring task by number, and the list, are allowed (Change in the browser)",
        PortalHandoffService.SafePath("/portal/recurring/12") == "/portal/recurring/12" && PortalHandoffService.SafePath("/portal/recurring") == "/portal/recurring");
    Check("...and nothing else under it", PortalHandoffService.SafePath("/portal/recurring/12x") == "/portal/tickets"
        && PortalHandoffService.SafePath("/portal/recurring/new") == "/portal/tickets" && PortalHandoffService.SafePath("/portal/recurring/1/../../x") == "/portal/tickets");
    Check("one ticket by number is allowed", PortalHandoffService.SafePath("/portal/tickets/42") == "/portal/tickets/42");
    Check("anything else after the slash is not", PortalHandoffService.SafePath("/portal/tickets/42x") == "/portal/tickets"
        && PortalHandoffService.SafePath("/portal/tickets/42/../../evil") == "/portal/tickets"
        && PortalHandoffService.SafePath("/portal/tickets/new") == "/portal/tickets");
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

static async Task<TechConsole> Sign(int port, string email)
{
    using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    var lr = await hc.PostAsJsonAsync($"http://127.0.0.1:{port}/api/login", new { email, password = "ticket-wire-2026" });
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

    /// <summary>The oldest message of this kind not yet taken, waiting up to a few seconds for one.</summary>
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

file sealed class Factory : IDbContextFactory<PlatformDb>
{
    private readonly DbContextOptions<PlatformDb> _o;
    public Factory(DbContextOptions<PlatformDb> o) => _o = o;
    public PlatformDb CreateDbContext() => new(_o);
}
