// =====================================================================================
// webchatcheck - proves website chat (stage 3.8.3, 2026-09-30).
//
//   "a chat windows for a tenants website. This would allow them to install it on their own website
//    and when one chats in it is goes through to every logged in technician including supervisors and
//    office administrators. this would be a great way for someone to answer questions immediately
//    and guide them to the support page"
//
// His answers: the FIRST REPLY TAKES IT; a visitor gives a NAME and an EMAIL OR PHONE and becomes a
// Contact; nobody signed in or outside the hours → they LEAVE A MESSAGE, which becomes a TICKET.
//
// Two halves. The SERVICE, in-process: the rules for a visitor's details, and the idle sweep. The
// WIRE, on a platform of its own: the visitor spoken as RAW JSON exactly as wwwroot/webchat.js
// writes it, two consoles, an office administrator's portal page on its event stream, and a second
// company that must hear nothing.
//
//   dotnet run -c Debug --project tools/webchatcheck/webchatcheck.csproj
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
var dataDir = Path.Combine(Path.GetTempPath(), "rs-webchatcheck-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dataDir);
var port = FreePort();
var baseUrl = $"http://127.0.0.1:{port}";
Console.WriteLine($"webchatcheck - website chat; the wire half on a platform of its own on port {port}");
Console.WriteLine();

const string Pw = "web-chat-2026";
const string Sue = "Sue Tech", Tom = "Tom Tech", Olive = "Olive Office", Sam = "Sam Supervisor", Other = "Otto Other", Abe = "Abe Absent";
const string Slug = "web-chat-co", OtherSlug = "other-co";
Process? server = null;
try
{
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
    await using (var db = new PlatformDb(options)) await HarnessDb.MigrateAsync(db);
    var factory = new Factory(options);
    var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Platform:PublicUrl"] = baseUrl, ["Testing:WebChatHandOverGapMs"] = "300" }).Build();
    var tenants = new TenantService(factory, cfg, dataDir, DataProtectionProvider.Create("webchatcheck"));
    var (tenant, _) = await tenants.CreateAsync("Web Chat Co", Slug, 7);
    var (other, _) = await tenants.CreateAsync("Other Co", OtherSlug, 7);
    int tid = tenant!.Id, oid = other!.Id;
    await tenants.AddTechnicianAsync(tid, "sue@web.test", Pw, Sue, TenantRole.Technician);
    await tenants.AddTechnicianAsync(tid, "tom@web.test", Pw, Tom, TenantRole.Technician);
    await tenants.AddTechnicianAsync(tid, "olive@web.test", Pw, Olive, TenantRole.Billing);
    await tenants.AddTechnicianAsync(tid, "sam@web.test", Pw, Sam, TenantRole.Admin);
    await tenants.AddTechnicianAsync(tid, "abe@web.test", Pw, Abe, TenantRole.Technician);   // never signs in
    await tenants.AddTechnicianAsync(oid, "otto@other.test", Pw, Other, TenantRole.Technician);
    var always = WebSupportHours.Format(Enumerable.Range(0, 7).Select(_ => (WebSupportHours.Day?)new WebSupportHours.Day(TimeSpan.Zero, TimeSpan.FromHours(24))).ToArray());

    // =========================================================== the service
    Console.WriteLine("== a visitor's details ==");
    Check("a name is needed", WebChatService.Check("", "a@b.co", "", "hi").Contains("name"));
    Check("an email OR a phone is needed — neither is refused", WebChatService.Check("Ann", "", "", "hi").Contains("email address or a phone"));
    Check("an email alone will do", WebChatService.Check("Ann", "ann@example.com", "", "hi") == "");
    Check("a phone alone will do", WebChatService.Check("Ann", "", "082 555 1234", "hi") == "");
    Check("an email that is not one is refused", WebChatService.Check("Ann", "ann", "", "hi").Length > 0);
    Check("a phone with too few digits is refused", WebChatService.Check("Ann", "", "12", "hi").Length > 0);
    Check("a question is needed", WebChatService.Check("Ann", "ann@example.com", "", "  ").Contains("question"));
    Check("one over the length is refused", WebChatService.Check("Ann", "ann@example.com", "", new string('x', WebChatService.TextMax + 1)).Length > 0);

    Console.WriteLine();
    Console.WriteLine("== the bubble's three states, and the idle sweep (in-process) ==");
    var presence = new StaffPresence();
    var svc = new WebChatService(factory, new Visitors(factory, new ClientService(factory, DataProtectionProvider.Create("webchatcheck")), new ContactService(factory)),
        new TicketService(factory), presence, cfg);
    var t0 = (await tenants.ByIdAsync(tid))!;
    Check("switched off: no bubble", svc.ModeFor(t0, DateTime.UtcNow) == WebChatService.Mode.Off);
    await tenants.UpdateBrandingAsync(tid, t => { t.WebChatEnabled = true; t.WebChatHours = always; });
    t0 = (await tenants.ByIdAsync(tid))!;
    Check("on, but nobody signed in: it takes messages", svc.ModeFor(t0, DateTime.UtcNow) == WebChatService.Mode.Message);
    using (presence.Arrive(tid, Sue))
    {
        Check("somebody signed in: it takes chats", svc.ModeFor(t0, DateTime.UtcNow) == WebChatService.Mode.Open);
        Check("...somebody at ANOTHER company does not count", svc.ModeFor((await tenants.ByIdAsync(oid))!, DateTime.UtcNow) == WebChatService.Mode.Off);
        await tenants.UpdateBrandingAsync(tid, t => t.WebChatHours = ";;;;;;");
        Check("outside its hours: messages, even with somebody here", svc.ModeFor((await tenants.ByIdAsync(tid))!, DateTime.UtcNow) == WebChatService.Mode.Message);
        await tenants.UpdateBrandingAsync(tid, t => t.WebChatHours = always);
        t0 = (await tenants.ByIdAsync(tid))!;
        var (quiet, _, e1) = await svc.StartAsync(t0, "Quiet Quentin", "quentin@example.com", "", "Hello?", "", "127.0.0.1");
        Check("a chat starts", quiet != null, e1);
        await using (var db = new PlatformDb(options))
            await db.WebChats.Where(c => c.Id == quiet!.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.LastUtc, DateTime.UtcNow.AddHours(-1)));
        var ended = new List<WebChatInfo>();
        svc.Changed += (_, c) => { if (c.Ended) lock (ended) ended.Add(c); };
        await svc.VisitorHereAsync(tid, quiet!.Id, +1);
        await svc.SweepAsync();
        Check("the sweep leaves a quiet chat alone while its visitor's page is open", ended.Count == 0);
        await svc.VisitorHereAsync(tid, quiet.Id, -1);
        await svc.SweepAsync();
        Check("...and ends it as idle once they have gone", ended.Count == 1 && ended[0].EndedBy == "idle");
    }
    Check("everybody gone: messages again", svc.ModeFor(t0, DateTime.UtcNow) == WebChatService.Mode.Message);

    // ---- hand over (2026-10-15): Clint asked whether a chat can be transferred. Who may, to whom, and the race.
    Console.WriteLine();
    Console.WriteLine("== handing a chat over (in-process): who may, to whom, the race ==");
    using (presence.Arrive(tid, Sue)) using (presence.Arrive(tid, Tom)) using (presence.Arrive(tid, Olive)) using (presence.Arrive(tid, Sam))
    using (presence.Arrive(oid, Other))
    {
        var moves = new List<WebChatInfo>(); var notes = new List<WebChatLineInfo>();
        svc.Changed += (_, c) => { if (c.HandedOverBy.Length > 0) lock (moves) moves.Add(c); };
        svc.Line += (_, l) => { if (l.Note) lock (notes) notes.Add(l); };
        var (h, _, he) = await svc.StartAsync(t0, "Hana Handover", "hana@example.com", "", "Can somebody help?", "", "127.0.0.1");
        Check("a chat to hand over starts", h != null, he);
        int hid = h!.Id;
        var e0 = await svc.HandOverAsync(tid, hid, Olive, TenantRole.Billing, Tom);
        Check("nobody has answered: ANYBODY (the office administrator) may give it to a named person", e0 == "", e0);
        Check("...everybody is told who has it now, and who gave it", moves.LastOrDefault() is { AnsweredBy: Tom, HandedOverBy: Olive } m0 && m0.Id == hid);
        Check("...and the visitor is told, in a line from nobody", notes.LastOrDefault() is { Note: true, Name: "", FromVisitor: false, Text: "Tom Tech will be chatting with you." });
        var eOlive = await svc.HandOverAsync(tid, hid, Olive, TenantRole.Billing, Sue);
        Check("once it is Tom's, a colleague who is not answering it is REFUSED", eOlive.Contains("Tom Tech is answering"), eOlive);
        var eTech = await svc.HandOverAsync(tid, hid, Sue, TenantRole.Technician, Sue);
        Check("...a technician too, even to themselves", eTech.Contains("Tom Tech is answering"), eTech);
        Check("to the person who has it already: refused, saying so", (await svc.HandOverAsync(tid, hid, Tom, TenantRole.Technician, Tom)).Contains("already answering"));
        var eStranger = await svc.HandOverAsync(tid, hid, Tom, TenantRole.Technician, "Nobody Here");
        Check("to a STRANGER (nobody by that name signed in): refused", eStranger.Contains("not signed in"), eStranger);
        Check("to a real person of the company who is NOT signed in: refused",
            (await svc.HandOverAsync(tid, hid, Tom, TenantRole.Technician, Abe)).Contains("not signed in"));
        var eOther = await svc.HandOverAsync(tid, hid, Tom, TenantRole.Technician, Other);
        Check("to somebody of ANOTHER company, signed in there: refused", eOther.Contains("not signed in"), eOther);
        Check("to nobody: refused", (await svc.HandOverAsync(tid, hid, Tom, TenantRole.Technician, " ")).Length > 0);
        await using (var db = new PlatformDb(options))
            Check("...and none of those moved it", (await db.WebChats.FirstAsync(c => c.Id == hid)).AnsweredBy == Tom);
        var e1b = await svc.HandOverAsync(tid, hid, Tom, TenantRole.Technician, Sue);
        Check("the person answering hands it on", e1b == "" && moves.Last().AnsweredBy == Sue, e1b);
        Check("...and the visitor is told who has taken over", notes.Last().Text == "Sue Tech has taken over this chat.");
        var eSam = await svc.HandOverAsync(tid, hid, Sam, TenantRole.Admin, Olive);
        Check("a SUPERVISOR may hand over a chat somebody else is answering", eSam == "" && moves.Last() is { AnsweredBy: Olive, HandedOverBy: Sam }, eSam);
        Check("the note lines are kept, in order, with the conversation", (await svc.LinesAsync(tid, hid)).Count(l => l.Note) == 3);
        await svc.EndAsync(tid, hid, Sam);
        Check("an ended chat cannot be handed over", (await svc.HandOverAsync(tid, hid, Sam, TenantRole.Admin, Tom)) == "This chat has ended.");

        // The race: four hand-overs of the same chat at the same instant. Exactly one may win, every round —
        // the move is conditional on who had it when each one looked.
        int rounds = 10, clean = 0, extraNotes = 0;
        for (int r = 0; r < rounds; r++)
        {
            var (rc, _, _) = await svc.StartAsync(t0, $"Racer {r}", "racer@example.com", "", "Hello", "", "127.0.0.1");
            await svc.HandOverAsync(tid, rc!.Id, Sam, TenantRole.Admin, Sam);
            var results = await Task.WhenAll(new[] { Sue, Tom, Olive, Sam }.Select(to => Task.Run(() => svc.HandOverAsync(tid, rc.Id, Sam, TenantRole.Admin, to))));
            var winners = results.Count(x => x == "");
            await using (var db = new PlatformDb(options))
            {
                var row = await db.WebChats.FirstAsync(c => c.Id == rc.Id);
                var won = new[] { Sue, Tom, Olive, Sam }.Where((to, i) => results[i] == "").ToList();
                if (winners == 1 && row.AnsweredBy == won[0]) clean++;
                extraNotes += Math.Max(0, await db.WebChatLines.CountAsync(l => l.WebChatId == rc.Id && l.Name == "") - 2);
            }
            await svc.EndAsync(tid, rc.Id, Sam);
        }
        Check("four hand-overs at once: exactly ONE wins, and the record says that one — every round", clean == rounds, $"{clean} of {rounds} clean");
        Check("...and the visitor is told only once per move", extraNotes == 0, $"{extraNotes} extra notes");

        // A hand-over looked while nobody had it, and Tom's first reply took it before the move: Tom keeps it,
        // and Olive — who may not move Tom's chat — is told it changed, never silently overriding him.
        int agree = 0; string lastGive = "";
        for (int r = 0; r < 4; r++)
        {
            var (rc, _, _) = await svc.StartAsync(t0, $"Reply race {r}", "rr@example.com", "", "Hi", "", "127.0.0.1");
            var give = Task.Run(() => svc.HandOverAsync(tid, rc!.Id, Olive, TenantRole.Billing, Sue));
            await Task.Delay(60);   // inside the hand-over's gap: it has looked, and not yet moved
            await svc.StaffSaysAsync(tid, rc!.Id, Tom, "Hello, Tom here.");
            lastGive = await give;
            await using (var db = new PlatformDb(options))
                if (lastGive.Contains("just changed") && (await db.WebChats.FirstAsync(c => c.Id == rc.Id)).AnsweredBy == Tom) agree++;
            await svc.EndAsync(tid, rc.Id, Sam);
        }
        Check("a hand-over racing a first reply: the reply keeps it, the hand-over is refused saying it changed", agree == 4, $"{agree} of 4 — \"{lastGive}\"");
    }
    await tenants.UpdateBrandingAsync(oid, t => { t.WebChatEnabled = true; t.WebChatHours = always; });

    // =========================================================== the wire
    Console.WriteLine();
    Console.WriteLine("== the bubble, with nobody signed in ==");
    var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in new[] { "run", "-c", "Debug", "--project", Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj"), "--", "run" })
        psi.ArgumentList.Add(a);
    psi.Environment["RSP_Platform__DataDirectory"] = dataDir;
    HarnessDb.Use(psi.Environment, dataDir);
    psi.Environment["RSP_Security__RequireTwoFactor"] = "false";   // a throwaway platform: the password alone (HANDOVER §72)
    psi.Environment["RSP_Platform__PublicUrl"] = baseUrl;
    psi.Environment["RSP_Testing__WebChatHandOverGapMs"] = "300";   // hand-overs at once really meet (WebChatService)
    psi.Environment["RSP_Platform__SiteChatSlug"] = Slug;            // OUR site's bubble is Web Chat Co's chat (SiteChat)
    psi.Environment["RSP_Urls"] = baseUrl;
    psi.Environment["ASPNETCORE_URLS"] = baseUrl;
    server = Process.Start(psi)!;
    server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    if (!await WaitForPort(port, 90000)) { Console.WriteLine("The platform never started listening."); return 1; }
    using var web = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };

    var js = await web.GetAsync($"/webchat/{Slug}.js");
    var jsText = await js.Content.ReadAsStringAsync();
    Check("the line a company pastes serves the bubble's script, with its company written in",
        js.IsSuccessStatusCode && js.Content.Headers.ContentType?.MediaType == "text/javascript" && jsText.Contains($"\"{Slug}\"") && !jsText.Contains("__SLUG__"));
    var st = await web.GetAsync($"/api/webchat/{Slug}");
    Check("its state answers ANY website (the bubble asks from the company's own site)",
        st.Headers.TryGetValues("Access-Control-Allow-Origin", out var acao) && acao.Contains("*"));
    Check("...and says: take messages", await Mode(web, Slug) == "message");
    Check("a company that does not exist has no bubble", await Mode(web, "nobody-at-all") == "off");

    // Our own site (2026-10-27, Services/SiteChat.cs): Platform:SiteChatSlug's chat, wearing OUR name and colour there only.
    var home = await web.GetStringAsync("/");
    Check("our site's pages carry the site-chat company's bubble, asking for our name (?site=1)",
        home.Contains($"/webchat/{Slug}.js?site=1"));
    var ourState = await web.GetFromJsonAsync<JsonElement>($"/api/webchat/{Slug}?site=1");
    Check("on our site its bubble says The Remote Support Program, in our colour",
        ourState.GetProperty("company").GetString() == "The Remote Support Program" && ourState.GetProperty("accent").GetString() == "#6d28d9",
        ourState.ToString());
    var theirState = await web.GetFromJsonAsync<JsonElement>($"/api/webchat/{Slug}");
    Check("...and on the company's own site it is still the company's", theirState.GetProperty("company").GetString() == "Web Chat Co");
    var otherState = await web.GetFromJsonAsync<JsonElement>($"/api/webchat/{OtherSlug}?site=1");
    Check("no other company's bubble can wear our name by asking", otherState.GetProperty("company").GetString() == "Other Co");
    var ourFrame = await web.GetStringAsync($"/webchat/{Slug}/frame?from=https%3A%2F%2Ftheremotesupportprogram.com%2F&site=1");
    var theirFrame = await web.GetStringAsync($"/webchat/{Slug}/frame?from=https%3A%2F%2Fwebchat.example%2F");
    var otherFrame = await web.GetStringAsync($"/webchat/{OtherSlug}/frame?from=https%3A%2F%2Fother.example%2F&site=1");
    Check("the chat window on our site is headed The Remote Support Program, in our colour",
        ourFrame.Contains("<strong>The Remote Support Program</strong>") && ourFrame.Contains("--fill: #6d28d9") && !ourFrame.Contains("Web Chat Co"));
    Check("...the company's own site's window keeps its name", theirFrame.Contains("<strong>Web Chat Co</strong>"));
    Check("...and another company's window ignores the ask", otherFrame.Contains("<strong>Other Co</strong>") && !otherFrame.Contains("The Remote Support Program"));
    Check("the bubble's script passes the ask on to its state and its window",
        jsText.Contains("searchParams.get('site')") && jsText.Contains("'?' + SITE") && jsText.Contains("'&' + SITE"));

    var early = await web.PostAsJsonAsync($"/api/webchat/{Slug}/start", new { name = "Early Eve", email = "eve@example.com", text = "Hi", page = "https://webchat.example/" });
    var earlyBody = await early.Content.ReadAsStringAsync();
    Check("a chat cannot start with nobody to answer — it says to leave a message", early.StatusCode == HttpStatusCode.BadRequest
        && earlyBody.Contains("\"mode\":\"message\""), earlyBody);
    var leave = await web.PostAsJsonAsync($"/api/webchat/{Slug}/leave", new { name = "Mandy Message", phone = "082 555 1234", text = "Please call me about a new laptop.", page = "https://webchat.example/laptops" });
    Check("a message is left", leave.IsSuccessStatusCode, await leave.Content.ReadAsStringAsync());
    await using (var db = new PlatformDb(options))
    {
        var tk = await db.Tickets.OrderByDescending(x => x.Id).FirstOrDefaultAsync(x => x.TenantId == tid);
        Check("...it is a GENERAL ticket, from the chat, in their words", tk is { Category: TicketCategory.General, Source: TicketSource.Chat }
            && tk.Subject == "Please call me about a new laptop." && tk.Description.Contains("082 555 1234") && tk.Description.Contains("/laptops"),
            tk == null ? "no ticket" : $"{tk.Category} {tk.Source} \"{tk.Subject}\"");
        var contact = await db.Contacts.FirstOrDefaultAsync(c => c.TenantId == tid && c.Name == "Mandy Message");
        Check("...and they are a Contact, under a client", contact != null && tk?.ClientId == contact.ClientId);
    }
    await web.PostAsJsonAsync($"/api/webchat/{Slug}/leave", new { name = "Mandy M", phone = "0825551234", text = "Me again." });
    await using (var db = new PlatformDb(options))
        Check("the same phone again is the SAME contact, not a second one", await db.Contacts.CountAsync(c => c.TenantId == tid && c.Phone.Contains("555")) == 1);

    Console.WriteLine();
    Console.WriteLine("== somebody signs in ==");
    var sue = await Sign(port, "sue@web.test");
    var sueList = await sue.Next<WebChatListMsg>();
    Check("a console is told at sign-in: on, and the links to hand a visitor",
        sueList is { Enabled: true } && sueList.AppLink == $"{baseUrl}/get/{Slug}" && sueList.HelpLink == "",
        $"{sueList?.AppLink} / help \"{sueList?.HelpLink}\"");
    Check("...no browser support link while browser support is off", sueList?.HelpLink == "");
    Check("the bubble now takes chats", await Mode(web, Slug) == "open");
    var tom = await Sign(port, "tom@web.test");
    await tom.Next<WebChatListMsg>();
    var otto = await Sign(port, "otto@other.test");
    // The office administrator has no console: a portal page, signed in with the form.
    var olive = await Portal(port, "olive@web.test");
    var stream = new EventReader(olive, "/api/portal/chat/stream");
    await stream.Connected(8000);
    Check("the office administrator's portal page is listening", stream.IsOpen);

    Console.WriteLine();
    Console.WriteLine("== a visitor starts a chat ==");
    var bad = await web.PostAsJsonAsync($"/api/webchat/{Slug}/start", new { name = "Vic", text = "Hi" });
    Check("no email and no phone is refused, saying why", bad.StatusCode == HttpStatusCode.BadRequest && (await bad.Content.ReadAsStringAsync()).Contains("email address or a phone"));
    sue.Clear(); tom.Clear();
    var robot = await web.PostAsJsonAsync($"/api/webchat/{Slug}/start", new { name = "Robot", email = "r@r.co", text = "buy", website = "http://spam" });
    Check("a robot (the hidden field) is answered politely and nothing rings",
        robot.IsSuccessStatusCode && (await robot.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32() == 0 && await sue.Next<WebChatUpdateMsg>(1500) == null);

    var start = await web.PostAsJsonAsync($"/api/webchat/{Slug}/start",
        new { name = "Vic Visitor", email = "vic@example.com", text = "Do you fix Macs?", page = "https://webchat.example/services" });
    var started = await start.Content.ReadFromJsonAsync<JsonElement>();
    int chatId = started.GetProperty("id").GetInt32(); var token = started.GetProperty("token").GetString()!;
    Check("a real visitor starts one, and gets a secret to reconnect with", start.IsSuccessStatusCode && chatId > 0 && token.Length >= 20);
    var up = await sue.Next<WebChatUpdateMsg>();
    Check("Sue's console hears it: who, how to reach them, where on the site, nobody answering yet",
        up?.Chat is { Name: "Vic Visitor", Email: "vic@example.com", AnsweredBy: "", Ended: false } && up.Chat.Page.EndsWith("/services"));
    var firstLine = await sue.Next<WebChatLineMsg>();
    Check("...and their question", firstLine?.Line is { FromVisitor: true, Text: "Do you fix Macs?", Name: "Vic Visitor" });
    Check("Tom's console hears it too — EVERYBODY signed in does", (await tom.Next<WebChatUpdateMsg>())?.Chat?.Id == chatId);
    var oliveEv = await stream.Next("webchat");
    Check("...and the office administrator's portal page", oliveEv?.GetProperty("name").GetString() == "Vic Visitor");
    Check("...with the question", (await stream.Next("webline"))?.GetProperty("text").GetString() == "Do you fix Macs?");
    Check("ANOTHER company's console hears nothing", await otto.Next<WebChatUpdateMsg>(1500) == null && await otto.Next<WebChatLineMsg>(500) == null);
    await using (var db = new PlatformDb(options))
    {
        var row = await db.WebChats.FirstAsync(c => c.Id == chatId);
        Check("the secret is kept only hashed", row.TokenHash.Length == 64 && row.TokenHash != token);
        Check("...and the visitor is a Contact", row.ContactId != null && await db.Contacts.AnyAsync(c => c.Id == row.ContactId && c.Email == "vic@example.com"));
    }

    Console.WriteLine();
    Console.WriteLine("== the visitor's own connection ==");
    var vic = await Visitor.Connect(port, chatId, token);
    var hello = await vic.Next("hello");
    Check("with its secret it hears its chat so far", hello?.GetProperty("lines").GetArrayLength() == 1
        && hello?.GetProperty("chat").GetProperty("answeredBy").GetString() == "");
    var hereUp = await sue.Next<WebChatUpdateMsg>();
    Check("the consoles see that the visitor's page is open", hereUp?.Chat is { VisitorHere: true } && hereUp.Chat.Id == chatId);
    var wrong = await Visitor.Connect(port, chatId, "not-the-secret");
    Check("a wrong secret is turned away, and shown nothing", (await wrong.Next("gone")) != null && (await wrong.Next("hello", 800)) == null);
    Check("...and so is the right secret against another chat's number", (await (await Visitor.Connect(port, chatId + 1000, token)).Next("gone")) != null);
    sue.Clear(); tom.Clear();
    await vic.Send(new { type = "say", text = "It's a MacBook Air.", name = "Somebody Else" });
    var vl = await sue.Next<WebChatLineMsg>();
    Check("what the visitor says reaches the consoles, as the name they GAVE — never one the page claims",
        vl?.Line is { FromVisitor: true, Name: "Vic Visitor", Text: "It's a MacBook Air." });

    Console.WriteLine();
    Console.WriteLine("== the first reply takes it ==");
    sue.Clear(); tom.Clear();
    tom.Ws.Send(new WebChatSayMsg { ChatId = chatId, Text = "Yes, we do. What's wrong with it?" });
    var claimed = await sue.Next<WebChatUpdateMsg>();
    Check("Tom replies first: everybody is told Tom is answering", claimed?.Chat?.AnsweredBy == Tom);
    Check("...Tom's own console too", (await tom.Next<WebChatUpdateMsg>())?.Chat?.AnsweredBy == Tom);
    var vstate = await vic.Next("state", e => e.GetProperty("chat").GetProperty("answeredBy").GetString() == Tom);
    Check("...and the visitor sees who", vstate?.GetProperty("chat").GetProperty("answeredBy").GetString() == Tom);
    var vline = await vic.Next("line", e => !e.GetProperty("line").GetProperty("fromVisitor").GetBoolean());
    Check("...and his reply, under his name", vline?.GetProperty("line").GetProperty("name").GetString() == Tom
        && vline?.GetProperty("line").GetProperty("fromVisitor").GetBoolean() == false);
    Check("the portal page is told Tom is answering", await stream.Next("webchat", e => e.GetProperty("answeredBy").GetString() == Tom) != null);
    sue.Clear();
    sue.Ws.Send(new WebChatSayMsg { ChatId = chatId, Text = "Sue here too — bring it in any time." });
    var sueLine = await sue.Next<WebChatLineMsg>();
    Check("Sue may still join in", sueLine?.Line?.Name == Sue);
    Check("...but it stays Tom's", await sue.Next<WebChatUpdateMsg>(1200) == null);
    await using (var db = new PlatformDb(options))
        Check("...on the record too", (await db.WebChats.FirstAsync(c => c.Id == chatId)).AnsweredBy == Tom);

    Console.WriteLine();
    Console.WriteLine("== the office administrator, from the portal ==");
    var noHeader = await olive.PostAsJsonAsync($"/api/portal/webchats/{chatId}/say", new { text = "no header" });
    Check("a portal write without its X-Chat header is REFUSED", noHeader.StatusCode == HttpStatusCode.Unauthorized);
    var list = await olive.GetFromJsonAsync<JsonElement>("/api/portal/webchats");
    Check("the portal lists the chat going on, and the app link", list.GetProperty("chats").EnumerateArray().Any(c => c.GetProperty("id").GetInt32() == chatId && c.GetProperty("answeredBy").GetString() == Tom)
        && list.GetProperty("appLink").GetString() == $"{baseUrl}/get/{Slug}");
    vic.Clear();
    var os = await Post(olive, $"/api/portal/webchats/{chatId}/say", new { text = $"You can download our support app here: {baseUrl}/get/{Slug}" });
    Check("Olive sends the app link from the portal", os.IsSuccessStatusCode, await os.Content.ReadAsStringAsync());
    var ol = await vic.Next("line");
    Check("...the visitor gets it, under her name", ol?.GetProperty("line").GetProperty("name").GetString() == Olive
        && ol?.GetProperty("line").GetProperty("text").GetString()!.Contains("/get/" + Slug) == true);
    var lines = await olive.GetFromJsonAsync<JsonElement>($"/api/portal/webchats/{chatId}");
    Check("the portal can read the whole conversation, in order", lines.GetArrayLength() == 5
        && lines[0].GetProperty("text").GetString() == "Do you fix Macs?", $"{lines.GetArrayLength()} lines");

    Console.WriteLine();
    Console.WriteLine("== leaving and ending ==");
    sue.Clear();
    await vic.Close();
    var goneUp = await sue.Next<WebChatUpdateMsg>();
    Check("the visitor closing the page: the consoles see they have left", goneUp?.Chat is { VisitorHere: false, Ended: false });
    var vic2 = await Visitor.Connect(port, chatId, token);
    var hello2 = await vic2.Next("hello");
    Check("moving to another page of the site: the same chat carries on, all of it", hello2?.GetProperty("lines").GetArrayLength() == 5);
    sue.Clear();
    sue.Ws.Send(new WebChatEndMsg { ChatId = chatId });
    var endUp = await sue.Next<WebChatUpdateMsg>();
    Check("Sue ends it: everybody is told, and by whom", endUp?.Chat is { Ended: true, EndedBy: Sue });
    var vEnd = await vic2.Next("state", e => e.GetProperty("chat").GetProperty("ended").GetBoolean());
    Check("...and the visitor is told it has ended", vEnd?.GetProperty("chat").GetProperty("ended").GetBoolean() == true);
    tom.Clear();
    tom.Ws.Send(new WebChatSayMsg { ChatId = chatId, Text = "One more thing" });
    var late = await tom.Next<WebChatLineMsg>();
    Check("saying anything after the end is refused, saying so", late?.Error == "This chat has ended.");
    await vic2.Send(new { type = "say", text = "Hello?" });
    Check("...from the visitor too", (await vic2.Next("error"))?.GetProperty("error").GetString() == "This chat has ended.");
    Check("an ended chat is gone from the portal's list", !(await olive.GetFromJsonAsync<JsonElement>("/api/portal/webchats")).GetProperty("chats").EnumerateArray().Any(c => c.GetProperty("id").GetInt32() == chatId));

    var s2 = await (await web.PostAsJsonAsync($"/api/webchat/{Slug}/start", new { name = "Wendy", phone = "0115550000", text = "Quick question" })).Content.ReadFromJsonAsync<JsonElement>();
    var wendy = await Visitor.Connect(port, s2.GetProperty("id").GetInt32(), s2.GetProperty("token").GetString()!);
    await wendy.Next("hello");
    sue.Clear();
    await wendy.Send(new { type = "end" });
    var wEnd = await sue.Next<WebChatUpdateMsg>();
    Check("a visitor can end it themselves", wEnd?.Chat is { Ended: true, EndedBy: "visitor" });

    Console.WriteLine();
    Console.WriteLine("== handing a chat over, on the wire ==");
    var samC = await Sign(port, "sam@web.test");
    var samList = await samC.Next<WebChatListMsg>();
    Check("a supervisor's console is told it may hand over anybody's chat", samList?.MayHandOverAny == true);
    Check("...a technician's is not", sueList?.MayHandOverAny == false);
    Check("...nor the office administrator's portal page", (await olive.GetFromJsonAsync<JsonElement>("/api/portal/webchats")).GetProperty("mayHandOverAny").GetBoolean() == false);
    samC.Ws.Send(new WebChatPeopleRequestMsg { ChatId = 1 });
    var ppl = (await samC.Next<WebChatPeopleMsg>())?.Names ?? new();
    Check("who could take it: everybody signed in here, console or portal page — not another company's, not somebody absent",
        new[] { Sue, Tom, Olive, Sam }.All(ppl.Contains) && !ppl.Contains(Other) && !ppl.Contains(Abe), string.Join(", ", ppl));
    var pplPortal = await olive.GetFromJsonAsync<JsonElement>("/api/portal/webchats/people");
    Check("...the portal is told the same", pplPortal.GetProperty("names").EnumerateArray().Select(n => n.GetString()).OrderBy(n => n).SequenceEqual(ppl.OrderBy(n => n)));

    var s3 = await (await web.PostAsJsonAsync($"/api/webchat/{Slug}/start", new { name = "Hank Handover", email = "hank@example.com", text = "Who can help with a printer?" })).Content.ReadFromJsonAsync<JsonElement>();
    int hid2 = s3.GetProperty("id").GetInt32();
    var hank = await Visitor.Connect(port, hid2, s3.GetProperty("token").GetString()!);
    await hank.Next("hello");
    tom.Ws.Send(new WebChatSayMsg { ChatId = hid2, Text = "Tom here — let me see." });
    await sue.Next<WebChatUpdateMsg>(m => m.Chat?.AnsweredBy == Tom);
    sue.Clear(); tom.Clear(); hank.Clear();
    tom.Ws.Send(new WebChatHandOverMsg { ChatId = hid2, To = Sue });
    var hu = await sue.Next<WebChatUpdateMsg>(m => m.Chat?.HandedOverBy.Length > 0);
    Check("Tom hands it to Sue: every console is told it is Sue's, and that Tom gave it", hu?.Chat is { AnsweredBy: Sue, HandedOverBy: Tom } && hu.Chat.Id == hid2);
    Check("...Tom's own console too", (await tom.Next<WebChatUpdateMsg>(m => m.Chat?.HandedOverBy.Length > 0))?.Chat?.AnsweredBy == Sue);
    var hn = await sue.Next<WebChatLineMsg>(m => m.Line?.ChatId == hid2);
    Check("...with a line from nobody, marked as a note", hn?.Line is { Note: true, Name: "", Text: "Sue Tech has taken over this chat." });
    Check("the visitor sees Sue is chatting with them now", await hank.Next("state", e => e.GetProperty("chat").GetProperty("answeredBy").GetString() == Sue) != null);
    var hv = await hank.Next("line");
    Check("...and is TOLD, in a line: \"Sue Tech has taken over this chat.\"", hv?.GetProperty("line").GetProperty("note").GetBoolean() == true
        && hv?.GetProperty("line").GetProperty("text").GetString() == "Sue Tech has taken over this chat.");
    Check("the portal page is told too, with who gave it", await stream.Next("webchat", e => e.GetProperty("id").GetInt32() == hid2
        && e.GetProperty("answeredBy").GetString() == Sue && e.GetProperty("handedOverBy").GetString() == Tom) != null);
    Check("...and gets the note marked as one", await stream.Next("webline", e => e.GetProperty("chatId").GetInt32() == hid2 && e.GetProperty("note").GetBoolean()) != null);

    tom.Clear();
    tom.Ws.Send(new WebChatHandOverMsg { ChatId = hid2, To = Tom });
    Check("Tom, no longer answering it, cannot take it back — refused, saying who has it", (await tom.Next<WebChatLineMsg>())?.Error.Contains("Sue Tech is answering") == true);
    var oliveTry = await Post(olive, $"/api/portal/webchats/{hid2}/handover", new { to = Olive });
    Check("...nor can the office administrator, from the portal", oliveTry.StatusCode == HttpStatusCode.BadRequest
        && (await oliveTry.Content.ReadAsStringAsync()).Contains("Sue Tech is answering"));
    var noHdr = await olive.PostAsJsonAsync($"/api/portal/webchats/{hid2}/handover", new { to = Olive });
    Check("a portal hand-over without its X-Chat header is REFUSED", noHdr.StatusCode == HttpStatusCode.Unauthorized);
    sue.Clear();
    sue.Ws.Send(new WebChatHandOverMsg { ChatId = hid2, To = Other });
    Check("Sue cannot hand it to ANOTHER company's technician, signed in though he is", (await sue.Next<WebChatLineMsg>(m => m.Error.Length > 0))?.Error.Contains("not signed in") == true);
    sue.Ws.Send(new WebChatHandOverMsg { ChatId = hid2, To = Abe });
    Check("...nor to a colleague who is not signed in", (await sue.Next<WebChatLineMsg>(m => m.Error.Length > 0))?.Error.Contains("not signed in") == true);
    sue.Ws.Send(new WebChatHandOverMsg { ChatId = hid2 + 1000, To = Tom });
    Check("...nor a chat that is not there", (await sue.Next<WebChatLineMsg>(m => m.Error.Length > 0))?.Error == "That chat is not there any more.");
    tom.Clear();
    samC.Ws.Send(new WebChatHandOverMsg { ChatId = hid2, To = Olive });
    Check("a supervisor moves it from Sue to Olive", (await tom.Next<WebChatUpdateMsg>(m => m.Chat?.HandedOverBy.Length > 0))?.Chat is { AnsweredBy: Olive, HandedOverBy: Sam });
    tom.Clear();
    var oliveGives = await Post(olive, $"/api/portal/webchats/{hid2}/handover", new { to = Tom });
    Check("Olive, answering it now, hands it on from the portal", oliveGives.IsSuccessStatusCode, await oliveGives.Content.ReadAsStringAsync());
    Check("...to Tom, who is told it is his", (await tom.Next<WebChatUpdateMsg>(m => m.Chat?.HandedOverBy.Length > 0))?.Chat is { AnsweredBy: Tom, HandedOverBy: Olive });

    // The race, on the wire: Sam's console and Sam's portal page move it at the same instant.
    var samP = await Portal(port, "sam@web.test");
    sue.Clear(); samC.Clear();
    var viaPortal = Post(samP, $"/api/portal/webchats/{hid2}/handover", new { to = Sue });
    samC.Ws.Send(new WebChatHandOverMsg { ChatId = hid2, To = Olive });
    var portalAnswer = await viaPortal;
    var consoleRefusal = await samC.Next<WebChatLineMsg>(m => m.Error.Length > 0, 3000);
    await Task.Delay(800);
    int racedMoves = 0; while (await sue.Next<WebChatUpdateMsg>(m => m.Chat?.HandedOverBy.Length > 0, 200) != null) racedMoves++;
    string raceOwner;
    await using (var db = new PlatformDb(options)) raceOwner = (await db.WebChats.FirstAsync(c => c.Id == hid2)).AnsweredBy;
    Check("two hand-overs at once: exactly ONE moves it, the other is told it changed",
        racedMoves == 1 && portalAnswer.IsSuccessStatusCode == (consoleRefusal != null)
        && (portalAnswer.IsSuccessStatusCode ? raceOwner == Sue && consoleRefusal!.Error.Contains("just changed") : raceOwner == Olive),
        $"{racedMoves} moves, portal {(int)portalAnswer.StatusCode}, console \"{consoleRefusal?.Error}\", now {raceOwner}");

    // Nobody has answered: anybody may give it to a named person, and the waiting stops for everybody.
    var s4 = await (await web.PostAsJsonAsync($"/api/webchat/{Slug}/start", new { name = "Una Unanswered", phone = "0215550000", text = "Hello?" })).Content.ReadFromJsonAsync<JsonElement>();
    int uid = s4.GetProperty("id").GetInt32();
    var una = await Visitor.Connect(port, uid, s4.GetProperty("token").GetString()!);
    await una.Next("hello");
    tom.Clear(); una.Clear();
    var giveUna = await Post(olive, $"/api/portal/webchats/{uid}/handover", new { to = Tom });
    Check("a chat NOBODY has answered: the office administrator gives it to Tom", giveUna.IsSuccessStatusCode, await giveUna.Content.ReadAsStringAsync());
    Check("...Tom is told it is his, and by whom", (await tom.Next<WebChatUpdateMsg>(m => m.Chat?.Id == uid && m.Chat.HandedOverBy.Length > 0))?.Chat is { AnsweredBy: Tom, HandedOverBy: Olive });
    Check("...and the visitor: \"Tom Tech will be chatting with you.\"", (await una.Next("line"))?.GetProperty("line").GetProperty("text").GetString() == "Tom Tech will be chatting with you.");
    Check("...and nobody is waiting any more (the ring stops)", !(await olive.GetFromJsonAsync<JsonElement>("/api/portal/webchats")).GetProperty("chats").EnumerateArray()
        .Any(c => c.GetProperty("answeredBy").GetString() == ""));
    await using (var db = new PlatformDb(options))
        Check("the notes are on the record with the conversation, for the history page", await db.WebChatLines.CountAsync(l => (l.WebChatId == hid2 || l.WebChatId == uid) && l.Name == "" && !l.FromVisitor) == 5);
    await una.Send(new { type = "end" }); await hank.Send(new { type = "end" });
    await Task.Delay(300);

    Console.WriteLine();
    Console.WriteLine("== the settings, and who may change them ==");
    var oliveSettings = await PostForm(olive, "/portal/webchat", new() { ["enabled"] = "false" });
    Check("the office administrator cannot change them", oliveSettings.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect || (await Mode(web, Slug)) == "open",
        $"{(int)oliveSettings.StatusCode}");
    Check("...and the bubble is still on", await Mode(web, Slug) == "open");
    var sam = await Portal(port, "sam@web.test");
    var page = await sam.GetStringAsync("/portal/webchat");
    Check("a supervisor's page gives the line to paste, with the address of this server",
        page.Contains($"&lt;script src=&quot;{baseUrl}/webchat/{Slug}.js&quot; async&gt;&lt;/script&gt;") || page.Contains($"{baseUrl}/webchat/{Slug}.js"));
    Check("...and lists the chats, the left message among them", page.Contains("Vic Visitor") && page.Contains("Mandy Message"));
    sue.Clear();
    var ws = await PostForm(sam, "/portal/websupport", new() { ["enabled"] = "true", ["zone"] = "Africa/Johannesburg" });
    var wsList = await sue.Next<WebChatListMsg>();
    Check("switching browser support on gives every console its link at once", ws.IsSuccessStatusCode
        && wsList?.HelpLink == $"{baseUrl}/help/{Slug}", $"\"{wsList?.HelpLink}\"");
    sue.Clear();
    var off = await PostForm(sam, "/portal/webchat", new() { ["enabled"] = "false", ["zone"] = "Africa/Johannesburg" });
    Check("a supervisor switches it off", off.IsSuccessStatusCode && await Mode(web, Slug) == "off");
    var offList = await sue.Next<WebChatListMsg>();
    Check("...and every console is told at once (the button goes)", offList is { Enabled: false });
    var refusedStart = await web.PostAsJsonAsync($"/api/webchat/{Slug}/start", new { name = "Late Larry", email = "l@example.com", text = "hi" });
    Check("...and no chat can start", !refusedStart.IsSuccessStatusCode);

    Console.WriteLine();
    Console.WriteLine("== the pages ==");
    var frame = await web.GetStringAsync($"/webchat/{OtherSlug}/frame?from=https%3A%2F%2Fother.example%2F");
    Check("the bubble's frame draws the company's name, both forms and its script", frame.Contains("Other Co") && frame.Contains("id=\"start\"")
        && frame.Contains("id=\"leave\"") && frame.Contains("/webchat.js") && frame.Contains("data-page=\"https://other.example/\""));
    var tryPage = await web.GetStringAsync($"/webchat/{OtherSlug}/try");
    Check("the Try it page puts the bubble on a sample site the same way a company's site does", tryPage.Contains($"<script src=\"/webchat/{OtherSlug}.js\" async>"));

    Console.WriteLine();
    Console.WriteLine("== a flood ==");
    // Otto's company: ten posts from one address in an hour, and the eleventh is turned away.
    int limited = 0;
    for (int i = 0; i < WebChatService.PerAddressPerHour + 1; i++)
    {
        var r = await web.PostAsJsonAsync($"/api/webchat/{OtherSlug}/start", new { name = "", text = "x" });
        if (r.StatusCode == HttpStatusCode.TooManyRequests) limited++;
    }
    Check("one post over the hour's limit from one address is turned away — and only that one", limited == 1, $"{limited} refused");

    Console.WriteLine();
    Console.WriteLine("== nobody left ==");
    await sue.Ws.CloseAsync(); await tom.Ws.CloseAsync(); stream.Stop();
    await Task.Delay(800);
    Check("with every console and portal page gone, Other Co's bubble still has Otto", await Mode(web, OtherSlug) == "open");
    await otto.Ws.CloseAsync();
    await Task.Delay(800);
    Check("...and once he goes too, it takes messages", await Mode(web, OtherSlug) == "message");
}
finally
{
    try { if (server is { HasExited: false }) server.Kill(true); } catch { }
    await Task.Delay(500);
    try { Directory.Delete(dataDir, true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
return fail == 0 ? 0 : 1;

static async Task<string> Mode(HttpClient c, string slug) =>
    (await c.GetFromJsonAsync<JsonElement>($"/api/webchat/{slug}")).GetProperty("mode").GetString() ?? "";

static Task<HttpResponseMessage> Post(HttpClient c, string path, object body)
{
    var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
    req.Headers.Add("X-Chat", "1");
    return c.SendAsync(req);
}

/// <summary>A portal form, antiforgery token and all, as the browser posts it.</summary>
static async Task<HttpResponseMessage> PostForm(HttpClient c, string path, Dictionary<string, string> fields)
{
    var page = await c.GetStringAsync(path);
    fields["__RequestVerificationToken"] = Between(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"", "\"")
        ?? Between(page, "name=\"__RequestVerificationToken\" value=\"", "\"") ?? "";
    return await c.PostAsync(path, new FormUrlEncodedContent(fields));
}

static async Task<HttpClient> Portal(int port, string email)
{
    var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true })
    { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = Timeout.InfiniteTimeSpan };
    await PostForm(c, "/login", new() { ["Email"] = email, ["Password"] = "web-chat-2026" });
    return c;
}

static string? Between(string s, string a, string b)
{
    var i = s.IndexOf(a, StringComparison.Ordinal);
    if (i < 0) return null;
    i += a.Length;
    var j = s.IndexOf(b, i, StringComparison.Ordinal);
    return j < 0 ? null : s[i..j];
}

static async Task<TechConsole> Sign(int port, string email)
{
    using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    var lr = await hc.PostAsJsonAsync($"http://127.0.0.1:{port}/api/login", new { email, password = "web-chat-2026" });
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

/// <summary>One signed-in console and everything it has been sent, in order.</summary>
file sealed class TechConsole
{
    public WsConnection Ws { get; }
    private readonly List<Msg> _got = new();
    public TechConsole(WsConnection ws) { Ws = ws; ws.OnMessage += m => { lock (_got) _got.Add(m); }; }
    public void Clear() { lock (_got) _got.Clear(); }

    public Task<T?> Next<T>(int ms = 8000) where T : Msg => Next<T>(_ => true, ms);

    /// <summary>The first message of this type that ALSO says what is being waited for; earlier ones stay.</summary>
    public async Task<T?> Next<T>(Func<T, bool> what, int ms = 8000) where T : Msg
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            lock (_got)
            {
                var m = _got.OfType<T>().FirstOrDefault(what);
                if (m != null) { _got.Remove(m); return m; }
            }
            await Task.Delay(40);
        }
        return null;
    }
}

/// <summary>The visitor's page, spoken as wwwroot/webchat.js speaks it: raw JSON on /ws/webchat.</summary>
file sealed class Visitor
{
    private readonly ClientWebSocket _ws = new();
    private readonly List<JsonElement> _got = new();

    public static async Task<Visitor> Connect(int port, int id, string token)
    {
        var v = new Visitor();
        await v._ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/webchat?id={id}&token={Uri.EscapeDataString(token)}"), CancellationToken.None);
        _ = Task.Run(async () =>
        {
            var buf = new byte[64 * 1024];
            try
            {
                while (v._ws.State == WebSocketState.Open)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult r;
                    do { r = await v._ws.ReceiveAsync(buf, CancellationToken.None); if (r.MessageType == WebSocketMessageType.Close) return; ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage);
                    var e = JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
                    lock (v._got) v._got.Add(e);
                }
            }
            catch { }
        });
        return v;
    }

    public Task Send(object o) => _ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(o), WebSocketMessageType.Text, true, CancellationToken.None);
    public void Clear() { lock (_got) _got.Clear(); }
    public async Task Close() { try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { } }

    public Task<JsonElement?> Next(string type, int ms = 8000) => Next(type, _ => true, ms);

    /// <summary>The first message of this type that ALSO says what is being waited for — earlier ones (its own
    /// presence, say) are left where they are.</summary>
    public async Task<JsonElement?> Next(string type, Func<JsonElement, bool> what, int ms = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            lock (_got)
            {
                var i = _got.FindIndex(e => e.TryGetProperty("type", out var t) && t.GetString() == type && what(e));
                if (i >= 0) { var e = _got[i]; _got.RemoveAt(i); return e; }
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

    public Task<JsonElement?> Next(string name, int ms = 8000) => Next(name, _ => true, ms);

    public async Task<JsonElement?> Next(string name, Func<JsonElement, bool> what, int ms = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            lock (_got)
            {
                var i = _got.FindIndex(e => e.name == name && what(JsonDocument.Parse(e.data).RootElement));
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
