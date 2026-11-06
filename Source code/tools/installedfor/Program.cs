using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// installedfor - the installer asks who the computer is for (2026-10-23).
//
// Clint: "during the client install, the client has to put in their name, surname and email address. This will then be
// the default name that shows up in the console and when they then request support, that is also the default name that
// get filled into their user field. And then if one links said user to a client, it automatically registers it as a
// contact under that client in the portal." His answers, all the recommendations: required in the installer window
// (a scripted install skips it); the latest name still wins (§150); Request Support asks the email too, filled in;
// linking to a client adds them as a contact with their email — by email first, then name — with a tick to say no;
// an email never links anything by itself; new installs only; not the free version.
//
// The rules; then pretend agents and tray apps against a real platform (the install's name arriving from both hellos,
// kept ONCE so a later request's name is not put back by a reconnect, a reinstall's new details, the email following
// its name), linking in the console (a new contact with the email, the same person found by email, a blank filled, an
// email never borrowed from another name, the tick left off), and the portal's Devices page and its tick.
//
// Its own platform on port 8221; the live server is never touched. RSP_TEST_DB=postgres runs it on PostgreSQL.
//
//   dotnet run -c Release --project tools/installedfor/installedfor.csproj
// ---------------------------------------------------------------------------------------------

const int Port = 8221;
string http = $"http://127.0.0.1:{Port}", ws = $"ws://127.0.0.1:{Port}";

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}
void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {s}");

Console.WriteLine($"installedfor - who the computer is for, from the installer window ({HarnessDb.Name})");
Console.WriteLine();

// ================================================================ the installer window's rule
Console.WriteLine("== the installer window's rule ==");
Check("all three are needed: the name", GivenPerson.Problem(" ", "Smith", "jane@acme.co.za") == "Type your name.");
Check("...the surname", GivenPerson.Problem("Jane", "", "jane@acme.co.za") == "Type your surname.");
Check("...the email", GivenPerson.Problem("Jane", "Smith", "  ") == "Type your email address.");
Check("...and the email has to look like one", GivenPerson.Problem("Jane", "Smith", "jane at acme") == "That email address does not look right.");
Check("...and then nothing stops it", GivenPerson.Problem("Jane", "Smith", " jane@acme.co.za ") == "");
Check("name and surname become ONE name, tidied, as contacts are", GivenPerson.Join("  Jane ", " van der  Merwe ") == "Jane van der Merwe");
Check("half an address is never kept", GivenPerson.CleanEmail("jane@") == "" && GivenPerson.CleanEmail(" jane@acme.co.za ") == "jane@acme.co.za");
var made = GivenPerson.Make("Jane", "Smith", "jane@acme.co.za", null);
Check("a first install gets an id of its own", made.Id.Length == 32 && made.Name == "Jane Smith" && made.Email == "jane@acme.co.za", made.Id);
var same = GivenPerson.Make(" Jane", "Smith ", "JANE@acme.co.za", made);
Check("an Update that leaves the boxes as they were (spaces and the email's case aside) KEEPS the id — so it does not put the name back over a later request's",
    same.Id == made.Id, $"{made.Id} / {same.Id}");
var changed = GivenPerson.Make("Jane", "Smith", "jane.smith@acme.co.za", made);
Check("...changing anything makes a new one — the email, or the name's case", changed.Id != made.Id && GivenPerson.Make("jane", "Smith", "jane@acme.co.za", made).Id != made.Id);
var folder = Path.Combine(Path.GetTempPath(), "rs-installedfor-file-" + Guid.NewGuid().ToString("N")[..8]);
made.Save(folder);
var back = GivenPerson.Load(folder);
Check("saved beside the installer and read back where the apps run", back?.Id == made.Id && back.Name == "Jane Smith" && back.Email == "jane@acme.co.za"
    && back.First == "Jane" && back.Surname == "Smith");
Check("a folder with none (a scripted install) says nothing", GivenPerson.Load(Path.Combine(folder, "nowhere")) == null);
File.WriteAllText(Path.Combine(folder, GivenPerson.FileName), "{ not json");
Check("...and a damaged one is no person, never an error", GivenPerson.Load(folder) == null);
try { Directory.Delete(folder, true); } catch { }

// ================================================================ the server's rule
Console.WriteLine();
Console.WriteLine("== the server's rule: the email belongs to the name beside it ==");
var d0 = new Device();
ContactService.Given(d0, "Jane Smith", "jane@acme.co.za", DateTime.UtcNow);
Check("a name with an email: both kept", d0.RequestedBy == "Jane Smith" && d0.RequestedByEmail == "jane@acme.co.za");
ContactService.Given(d0, "jane smith", "", DateTime.UtcNow);
Check("the SAME name with none (an older tray app sends none): the email stays", d0.RequestedByEmail == "jane@acme.co.za");
ContactService.Given(d0, "Mary Jones", null, DateTime.UtcNow);
Check("ANOTHER name with none: the old email goes — it was somebody else's", d0.RequestedBy == "Mary Jones" && d0.RequestedByEmail == "");
ContactService.Given(d0, "Mary Jones", "not an address", DateTime.UtcNow);
Check("half an address is not kept by the server either", d0.RequestedByEmail == "");
var d1 = new Device();
Check("the install's details, the first time: kept", ContactService.KeepInstalledFor(d1, "id-1", " Jane  Smith", "jane@acme.co.za", DateTime.UtcNow)
    && d1.RequestedBy == "Jane Smith" && d1.InstalledForId == "id-1");
ContactService.Given(d1, "Mary Jones", "", DateTime.UtcNow);
Check("...the same id again (every reconnect): NOT put back over a later request's name",
    !ContactService.KeepInstalledFor(d1, "id-1", "Jane Smith", "jane@acme.co.za", DateTime.UtcNow) && d1.RequestedBy == "Mary Jones");
Check("...a new id (a reinstall typed again): kept", ContactService.KeepInstalledFor(d1, "id-2", "Jane Smith", "jane@acme.co.za", DateTime.UtcNow)
    && d1.RequestedBy == "Jane Smith" && d1.RequestedByEmail == "jane@acme.co.za");
Check("no id or no name (a scripted install, an older build): nothing", !ContactService.KeepInstalledFor(d1, "", "X", "", DateTime.UtcNow)
    && !ContactService.KeepInstalledFor(d1, "id-3", "  ", "", DateTime.UtcNow) && d1.RequestedBy == "Jane Smith");

// ================================================================ a real platform
var repo = FindRepoRoot();
var work = Path.Combine(Path.GetTempPath(), "rs-installedfor-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
var dataDir = Path.Combine(work, "data");
Directory.CreateDirectory(dataDir);
var serverDir = Path.Combine(work, "server");
Log($"building the platform into {serverDir}");
var b = Run("dotnet", $"build -c Release \"{Path.Combine(repo, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj")}\" -v q --nologo -p:OutDir=\"{serverDir}\\\"", repo, 300000);
if (b.code != 0) { Console.WriteLine(b.output); Console.WriteLine("the platform would not build"); return 2; }
var exe = Path.Combine(serverDir, "RemoteSupport.Platform.exe");
var env = new Dictionary<string, string>
{
    ["RSP_Platform__DataDirectory"] = dataDir, ["RSP_Security__RequireTwoFactor"] = "false", ["RSP_Platform__PublicUrl"] = http,
    ["ASPNETCORE_URLS"] = http, ["ASPNETCORE_ENVIRONMENT"] = "Development",
    // Holds a machine's hello between looking its row up and saving it, so a first install's agent and tray app meet
    // in it on SQLite too (tools/firsthello's switch) — the case the RecordGate exists for.
    ["RSP_Testing__PersistDeviceGapMs"] = "600",
};
HarnessDb.Use(env, dataDir);
string tenantKey = "";
foreach (var cmd in new[] { "create-tenant \"Installed For\" installed-for 5", "approve installed-for", "add-tech installed-for sue@local.test localtest \"Sue Tech\" --admin" })
{
    var r = Run(exe, cmd, serverDir, 60000, env);
    if (r.code != 0) { Console.WriteLine(r.output); Console.WriteLine("CLI step failed: " + cmd); return 2; }
    var line = r.output.Split('\n').FirstOrDefault(l => l.StartsWith("Tenant key:"));
    if (line != null) tenantKey = line["Tenant key:".Length..].Trim();
}
if (tenantKey.Length == 0) { Console.WriteLine("could not read the tenant key"); return 2; }

Process? server = null;
async Task<bool> StartAsync()
{
    var psi = new ProcessStartInfo(exe, $"run --urls {http}") { WorkingDirectory = serverDir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
    server = Process.Start(psi)!;
    var log = Path.Combine(work, "server.log");
    void Keep(string? line) { if (line != null) lock (work) File.AppendAllText(log, line + Environment.NewLine); }
    server.OutputDataReceived += (_, e) => Keep(e.Data); server.ErrorDataReceived += (_, e) => Keep(e.Data);
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    for (int i = 0; i < 60; i++) { try { if ((await probe.GetAsync($"{http}/health")).IsSuccessStatusCode) return true; } catch { } await Task.Delay(500); }
    return false;
}
void Kill() { try { if (server != null && !server.HasExited) server.Kill(true); server?.WaitForExit(5000); } catch { } }
if (!await StartAsync()) { Console.WriteLine("the platform did not come up"); Kill(); return 2; }

try { await ChecksAsync(); }
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex); }
finally { Kill(); }

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
Console.WriteLine($"server and database kept at {work}");
return fail == 0 ? 0 : 1;

async Task ChecksAsync()
{
    int tenantId;
    await using (var db = Db()) tenantId = (await db.Tenants.FirstAsync(t => t.Slug == "installed-for")).Id;
    var sue = await Tech.OpenAsync(http, ws, "sue@local.test");
    var jane = GivenPerson.Make("Jane", "Smith", "jane@acme.co.za", null);

    // ================================================================ the install's name arrives
    Console.WriteLine();
    Console.WriteLine("== a new install says who it is for ==");
    // A first install's agent and tray app both say hello at once, both carrying the details.
    var agent = new FakeMachine(ws, "pc-1", "DESKTOP-A1", tenantKey, DeviceRole.Agent, jane);
    var tray = new FakeMachine(ws, "pc-1", "DESKTOP-A1", tenantKey, DeviceRole.Client, jane);
    await Task.WhenAll(agent.ConnectAsync(settle: false), tray.ConnectAsync(settle: false));
    var named = await sue.WaitForDevice("pc-1", d => d.RequestedBy == "Jane Smith", 6000);
    Check("every open console shows the name it was installed for", named?.OwnerName == "Jane Smith", named?.OwnerName ?? "no update");
    Check("...marked as only what was typed, until somebody links it", named?.NamedByRequest == true);
    Check("...with the email beside it", named?.RequestedByEmail == "jane@acme.co.za", named?.RequestedByEmail ?? "");
    await Task.Delay(1500);
    var row = await DeviceAsync("pc-1");
    Check("kept on the machine's record, with the install's id", row?.RequestedBy == "Jane Smith" && row.RequestedByEmail == "jane@acme.co.za"
        && row.InstalledForId == jane.Id, row == null ? "no row" : $"{row.RequestedBy} / {row.RequestedByEmail} / {row.InstalledForId}");

    // ================================================================ the latest name still wins
    Console.WriteLine();
    Console.WriteLine("== a support request with another name still wins ==");
    tray.Send(new RequestCallMsg { ContactName = "Mary Jones", Issue = "Outlook" });
    var mary = await sue.WaitForDevice("pc-1", d => d.RequestedBy == "Mary Jones", 5000);
    Check("Mary asks for help from Jane's PC: the console says Mary", mary?.OwnerName == "Mary Jones", mary?.OwnerName ?? "no update");
    Check("...and Jane's email is not left beside Mary's name", mary?.RequestedByEmail == "", mary?.RequestedByEmail ?? "");
    agent.Drop(); tray.Drop();
    await Task.Delay(500);
    agent = new FakeMachine(ws, "pc-1", "DESKTOP-A1", tenantKey, DeviceRole.Agent, jane);
    tray = new FakeMachine(ws, "pc-1", "DESKTOP-A1", tenantKey, DeviceRole.Client, jane);
    await agent.ConnectAsync(); await tray.ConnectAsync();
    await Task.Delay(2000);
    row = await DeviceAsync("pc-1");
    Check("both reconnect, still saying Jane: the install's name is NOT put back over Mary's", row?.RequestedBy == "Mary Jones"
        && sue.Latest("pc-1")?.OwnerName == "Mary Jones", $"{row?.RequestedBy} / console {sue.Latest("pc-1")?.OwnerName}");
    tray.Send(new RequestCallMsg { ContactName = "Mary Jones", ContactEmail = "mary@acme.co.za", Issue = "Outlook again" });
    var maryMail = await sue.WaitForDevice("pc-1", d => d.RequestedByEmail == "mary@acme.co.za", 5000);
    Check("a request that gives an email: kept with the name, and the consoles told", maryMail?.RequestedBy == "Mary Jones", maryMail?.RequestedByEmail ?? "no update");
    tray.Send(new RequestCallMsg { ContactName = "Mary Jones", Issue = "From an older tray app" });
    await Task.Delay(1500);
    Check("the same name with no email (an older tray app): her email stays", (await DeviceAsync("pc-1"))?.RequestedByEmail == "mary@acme.co.za");
    tray.Send(new RequestCallMsg { ContactName = "Mary Jones", ContactEmail = "mary at acme", Issue = "Typo" });
    await Task.Delay(1500);
    Check("half an address from an older or odd tray app is not kept", (await DeviceAsync("pc-1"))?.RequestedByEmail == "mary@acme.co.za");

    // A reinstall: the installer window typed again — new details, a new id.
    agent.Drop(); tray.Drop();
    var again = GivenPerson.Make("Jane", "Smith", "jane.smith@acme.co.za", jane);
    agent = new FakeMachine(ws, "pc-1", "DESKTOP-A1", tenantKey, DeviceRole.Agent, again);
    await agent.ConnectAsync();
    var reinstalled = await sue.WaitForDevice("pc-1", d => d.RequestedByEmail == "jane.smith@acme.co.za", 5000);
    Check("installed again with details typed again: they are the latest, so they win", reinstalled?.OwnerName == "Jane Smith",
        reinstalled == null ? "no update" : $"{reinstalled.OwnerName} {reinstalled.RequestedByEmail}");

    // A scripted install: no details at all.
    var scripted = new FakeMachine(ws, "pc-2", "DESKTOP-B2", tenantKey, DeviceRole.Client, null);
    await scripted.ConnectAsync();
    await Task.Delay(1200);
    var plain = sue.Latest("pc-2");
    Check("a scripted install (no installer window) behaves as before: Windows' sign-in, unmarked", plain?.OwnerName == "FakeUser" && !plain.NamedByRequest,
        plain == null ? "no device" : $"{plain.OwnerName} marked={plain.NamedByRequest}");

    // ================================================================ the restart
    Console.WriteLine();
    Console.WriteLine("== the server restarts ==");
    sue.Dispose(); agent.Drop(); tray.Drop(); scripted.Drop();
    Kill();
    if (!await StartAsync()) { Check("the platform comes back", false); return; }
    sue = await Tech.OpenAsync(http, ws, "sue@local.test");
    var state = await sue.WaitFor<StateMsg>(_ => true, 5000);
    var off = state?.Devices.FirstOrDefault(d => d.DeviceId == "pc-1");
    Check("a machine that is OFF still shows the name and its email", off?.OwnerName == "Jane Smith" && off.RequestedByEmail == "jane.smith@acme.co.za",
        off == null ? "not in the list" : $"{off.OwnerName} {off.RequestedByEmail}");

    // ================================================================ linking in the console
    Console.WriteLine();
    Console.WriteLine("== linking: the person goes on file with their email ==");
    int acme, zenith, vdm;
    await using (var db = Db())
    {
        var a = new Client { TenantId = tenantId, Name = "Acme Ltd" }; var z = new Client { TenantId = tenantId, Name = "Zenith Motors" };
        db.Clients.AddRange(a, z);
        await db.SaveChangesAsync();
        acme = a.Id; zenith = z.Id;
        // Jane married: on file under her new name, with the address she still uses.
        var c = new Contact { TenantId = tenantId, ClientId = zenith, Name = "Jane van der Merwe", Email = "jvdm@zenith.co.za" };
        db.Contacts.Add(c);
        db.Contacts.Add(new Contact { TenantId = tenantId, ClientId = zenith, Name = "Peter Pan" });
        await db.SaveChangesAsync();
        vdm = c.Id;
    }
    // What the console's Link window sends with its tick on and Acme chosen: the given name, as a new contact there.
    sue.Send(new DeviceLinkMsg { DeviceId = "pc-1", ClientId = acme, NewContactName = "Jane Smith" });
    var linked = await sue.WaitForDevice("pc-1", d => d.ContactId > 0, 5000);
    Check("Acme chosen with the tick on: Jane Smith is a contact there, and the machine is hers", linked?.ContactName == "Jane Smith" && linked.ClientName == "Acme Ltd",
        linked == null ? "no update" : $"{linked.ContactName} at {linked.ClientName}");
    var janeRow = await ContactAsync(linked?.ContactId ?? 0);
    Check("...WITH her email, from the machine's record", janeRow?.Email == "jane.smith@acme.co.za", janeRow?.Email ?? "no contact");

    var married = GivenPerson.Make("Jane", "Smith", "jvdm@zenith.co.za", null);
    var pc3 = new FakeMachine(ws, "pc-3", "DESKTOP-C3", tenantKey, DeviceRole.Client, married);
    await pc3.ConnectAsync();
    await sue.WaitForDevice("pc-3", d => d.RequestedBy == "Jane Smith", 5000);
    sue.Send(new DeviceLinkMsg { DeviceId = "pc-3", ClientId = zenith, NewContactName = "Jane Smith" });
    var byEmail = await sue.WaitForDevice("pc-3", d => d.ContactId > 0, 5000);
    int zenithCount; await using (var db = Db()) zenithCount = await db.Contacts.CountAsync(c => c.TenantId == tenantId && c.ClientId == zenith);
    Check("the same email already on file at that client: THAT person, by email, not a second one", byEmail?.ContactId == vdm && zenithCount == 2,
        $"linked to {byEmail?.ContactName} ({byEmail?.ContactId}), {zenithCount} contacts at Zenith");

    var pc4 = new FakeMachine(ws, "pc-4", "DESKTOP-D4", tenantKey, DeviceRole.Client, GivenPerson.Make("Peter", "Pan", "peter@zenith.co.za", null));
    await pc4.ConnectAsync();
    await sue.WaitForDevice("pc-4", d => d.RequestedBy == "Peter Pan", 5000);
    sue.Send(new DeviceLinkMsg { DeviceId = "pc-4", ClientId = zenith, NewContactName = "Peter Pan" });
    var byName = await sue.WaitForDevice("pc-4", d => d.ContactId > 0, 5000);
    var peter = await ContactAsync(byName?.ContactId ?? 0);
    int peters; await using (var db = Db()) peters = await db.Contacts.CountAsync(c => c.TenantId == tenantId && c.Name == "Peter Pan");
    Check("the same NAME on file with no email: that person, and their blank email filled in", peters == 1 && peter?.Email == "peter@zenith.co.za",
        $"{peters} Peter Pans, email '{peter?.Email}'");

    var pc5 = new FakeMachine(ws, "pc-5", "DESKTOP-E5", tenantKey, DeviceRole.Client, GivenPerson.Make("Sam", "Brown", "sam@acme.co.za", null));
    await pc5.ConnectAsync();
    await sue.WaitForDevice("pc-5", d => d.RequestedBy == "Sam Brown", 5000);
    sue.Send(new DeviceLinkMsg { DeviceId = "pc-5", ClientId = acme, NewContactName = "Somebody Else" });
    var other = await sue.WaitForDevice("pc-5", d => d.ContactId > 0, 5000);
    var otherRow = await ContactAsync(other?.ContactId ?? 0);
    Check("a name that is NOT the one given never borrows its email", otherRow?.Name == "Somebody Else" && otherRow.Email == "",
        otherRow == null ? "no contact" : $"{otherRow.Name} '{otherRow.Email}'");

    var pc6 = new FakeMachine(ws, "pc-6", "RECEPTION-PC", tenantKey, DeviceRole.Client, GivenPerson.Make("Front", "Desk", "desk@acme.co.za", null));
    await pc6.ConnectAsync();
    await sue.WaitForDevice("pc-6", d => d.RequestedBy == "Front Desk", 5000);
    int before; await using (var db = Db()) before = await db.Contacts.CountAsync(c => c.TenantId == tenantId);
    // The tick left off: the console sends the client alone, as it always has.
    sue.Send(new DeviceLinkMsg { DeviceId = "pc-6", ClientId = acme });
    var shared = await sue.WaitForDevice("pc-6", d => d.ClientId == acme, 5000);
    int after; await using (var db = Db()) after = await db.Contacts.CountAsync(c => c.TenantId == tenantId);
    Check("the tick left off (a shared PC): the company only, and nobody added", shared?.ContactId == 0 && shared.OwnerName == "Acme Ltd" && after == before,
        $"{shared?.OwnerName}, contacts {before} → {after}");

    // ================================================================ the portal
    Console.WriteLine();
    Console.WriteLine("== the portal's Devices page ==");
    var pc7 = new FakeMachine(ws, "pc-7", "DESKTOP-F7", tenantKey, DeviceRole.Client, GivenPerson.Make("Lindiwe", "Dube", "lindiwe@zenith.co.za", null));
    await pc7.ConnectAsync();
    await sue.WaitForDevice("pc-7", d => d.RequestedBy == "Lindiwe Dube", 5000);
    var portal = await PortalAsync("sue@local.test");
    var page = await portal.GetStringAsync("/portal/devices");
    var setForm = AssignFormFor(page, "pc-7");
    Check("the Set form for a machine with a given name has the tick, ticked, with the name and email",
        setForm != null && setForm.Contains("name=\"addGiven\" value=\"true\" checked") && setForm.Contains("Add Lindiwe Dube (lindiwe@zenith.co.za) as a contact when I choose a client"),
        setForm == null ? "no form" : "form found");
    Check("...and on a machine linked to a client but no person — the reception PC — too", AssignFormFor(page, "pc-6")?.Contains("addGiven") == true);
    Check("...but not on one that is a person's already", AssignFormFor(page, "pc-1")?.Contains("addGiven") == false);
    Check("the name's tooltip carries the email", page.Contains("with lindiwe@zenith.co.za"));
    var token = Between(setForm ?? "", "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"", "\"") ?? "";
    Check("...whose form carries its own antiforgery token (§80)", token.Length > 0);
    var post = await portal.PostAsync("/portal/devices?handler=Assign", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["deviceId"] = "pc-7", ["owner"] = $"c:{zenith}", ["addGiven"] = "true", ["__RequestVerificationToken"] = token }));
    var answer = await post.Content.ReadAsStringAsync();
    Check("Set with Zenith chosen and the tick on: linked to her, in words", answer.Contains("DESKTOP-F7 linked to Lindiwe Dube at Zenith Motors"),
        Between(answer, "id=\"devicesMessage\">", "<") ?? Between(answer, "id=\"devicesError\">", "<") ?? "no message");
    var viaPortal = await sue.WaitForDevice("pc-7", d => d.ContactName == "Lindiwe Dube", 5000);
    var lindiwe = await ContactAsync(viaPortal?.ContactId ?? 0);
    Check("...a contact at Zenith with her email, and the consoles told", lindiwe?.ClientId == zenith && lindiwe.Email == "lindiwe@zenith.co.za",
        lindiwe == null ? "no contact" : $"{lindiwe.Name} '{lindiwe.Email}'");

    var pc8 = new FakeMachine(ws, "pc-8", "SERVER-01", tenantKey, DeviceRole.Client, GivenPerson.Make("Admin", "Person", "it@zenith.co.za", null));
    await pc8.ConnectAsync();
    await sue.WaitForDevice("pc-8", d => d.RequestedBy == "Admin Person", 5000);
    page = await portal.GetStringAsync("/portal/devices");
    token = Between(AssignFormFor(page, "pc-8") ?? "", "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"", "\"") ?? "";
    int beforePortal; await using (var db = Db()) beforePortal = await db.Contacts.CountAsync(c => c.TenantId == tenantId);
    post = await portal.PostAsync("/portal/devices?handler=Assign", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["deviceId"] = "pc-8", ["owner"] = $"c:{zenith}", ["__RequestVerificationToken"] = token }));
    answer = await post.Content.ReadAsStringAsync();
    int afterPortal; await using (var db = Db()) afterPortal = await db.Contacts.CountAsync(c => c.TenantId == tenantId);
    Check("Set with the tick taken off: the company only, nobody added", answer.Contains("SERVER-01 linked to Zenith Motors.") && afterPortal == beforePortal,
        $"{Between(answer, "id=\"devicesMessage\">", "<") ?? "no message"}; contacts {beforePortal} → {afterPortal}");

    sue.Dispose(); pc3.Drop(); pc4.Drop(); pc5.Drop(); pc6.Drop(); pc7.Drop(); pc8.Drop(); portal.Dispose();
}

PlatformDb Db() => new(new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options);

async Task<Device?> DeviceAsync(string deviceId) { await using var db = Db(); return await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == deviceId); }
async Task<Contact?> ContactAsync(int id) { await using var db = Db(); return await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id); }

async Task<HttpClient> PortalAsync(string email)
{
    var c = new HttpClient(new HttpClientHandler { CookieContainer = new System.Net.CookieContainer(), AllowAutoRedirect = true }) { BaseAddress = new Uri(http) };
    var login = await c.GetStringAsync("/login");
    var token = Between(login, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"", "\"") ?? "";
    await c.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = email, ["Password"] = "localtest", ["__RequestVerificationToken"] = token }));
    return c;
}

/// <summary>The owner picker's Set form for this machine — the one posting to Assign with the cpick box in it.</summary>
static string? AssignFormFor(string page, string deviceId)
{
    int at = 0;
    while ((at = page.IndexOf("class=\"cpick-form\"", at, StringComparison.Ordinal)) >= 0)
    {
        var start = page.LastIndexOf("<form", at, StringComparison.Ordinal);
        var end = page.IndexOf("</form>", at, StringComparison.Ordinal);
        if (start < 0 || end < 0) return null;
        var f = page[start..end];
        if (f.Contains($"name=\"deviceId\" value=\"{deviceId}\"") && f.Contains("handler=Assign")) return f;
        at = end;
    }
    return null;
}

static string? Between(string s, string a, string b)
{
    var i = s.IndexOf(a, StringComparison.Ordinal);
    if (i < 0) return null;
    i += a.Length;
    var j = s.IndexOf(b, i, StringComparison.Ordinal);
    return j < 0 ? null : s[i..j];
}

static (int code, string output) Run(string file, string argLine, string cwd, int ms, Dictionary<string, string>? env = null)
{
    var psi = new ProcessStartInfo(file, argLine) { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
    using var p = Process.Start(psi)!;
    var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    p.WaitForExit(ms);
    return (p.HasExited ? p.ExitCode : -1, o);
}

static string FindRepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        if (Directory.Exists(Path.Combine(d.FullName, "src", "RemoteSupport.Platform"))) return d.FullName;
    throw new InvalidOperationException("could not find the repository root");
}

/// <summary>A technician's console connection.</summary>
sealed class Tech : IDisposable
{
    private readonly ClientWebSocket _raw = new();
    private WsConnection _c = null!;
    public readonly ConcurrentQueue<Msg> Seen = new();

    public static async Task<Tech> OpenAsync(string http, string ws, string email)
    {
        using var hc = new HttpClient();
        var lr = await hc.PostAsJsonAsync($"{http}/api/login", new { email, password = "localtest" });
        var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
        var t = new Tech();
        await t._raw.ConnectAsync(new Uri($"{ws}/ws/tech?token={tok}"), default);
        t._c = new WsConnection(t._raw);
        t._c.OnMessage += m => t.Seen.Enqueue(m);
        _ = t._c.RunAsync();
        await Task.Delay(600);
        return t;
    }

    public void Send(Msg m) => _c.Send(m);

    public async Task<T?> WaitFor<T>(Func<T, bool> f, int ms) where T : Msg
    { var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { foreach (var m in Seen) if (m is T t && f(t)) return t; await Task.Delay(50); } return null; }

    public DeviceInfo? Latest(string id) => Seen.Reverse().Select(m => m switch
    {
        DeviceUpdateMsg u when u.Device.DeviceId == id => u.Device,
        StateMsg s => s.Devices.FirstOrDefault(d => d.DeviceId == id),
        _ => null
    }).FirstOrDefault(d => d != null);

    public async Task<DeviceInfo?> WaitForDevice(string id, Func<DeviceInfo, bool> f, int ms)
    {
        var e = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < e)
        {
            foreach (var m in Seen) if (m is DeviceUpdateMsg u && u.Device.DeviceId == id && f(u.Device)) return u.Device;
            await Task.Delay(50);
        }
        return null;
    }

    public void Dispose() { try { _raw.Abort(); } catch { } }
}

/// <summary>A machine's agent or tray app, saying in its hello who it was installed for — as DeviceLink does.</summary>
sealed class FakeMachine
{
    private readonly string _ws, _tenantKey;
    private readonly DeviceRole _role;
    private readonly GivenPerson? _person;
    public readonly string Id, Name;
    private ClientWebSocket? _raw;
    private WsConnection? _c;

    public FakeMachine(string ws, string id, string name, string tenantKey, DeviceRole role, GivenPerson? person)
    { _ws = ws; Id = id; Name = name; _tenantKey = tenantKey; _role = role; _person = person; }

    public async Task ConnectAsync(bool settle = true)
    {
        _raw = new ClientWebSocket();
        await _raw.ConnectAsync(new Uri($"{_ws}/ws/device"), default);
        _c = new WsConnection(_raw);
        _ = _c.RunAsync();
        _c.Send(new HelloMsg
        {
            Role = _role, DeviceId = Id, DeviceKey = Id + "-key", MachineName = Name, UserName = "FakeUser",
            Os = "Windows 11 (pretend)", Version = "0.0-test", TenantKey = _tenantKey,
            Spec = new SystemSpec { Cpu = "Pretend CPU", Cores = 1, RamMb = 1024, DiskTotalGb = 10, DiskFreeGb = 5, Os = "Windows 11 (pretend)" },
            InstalledForId = _person?.Id ?? "", InstalledForName = _person?.Name ?? "", InstalledForEmail = _person?.Email ?? ""
        });
        if (settle) await Task.Delay(500);
    }

    public void Send(Msg m) => _c?.Send(m);
    public void Drop() { try { _raw?.Abort(); } catch { } }
}
