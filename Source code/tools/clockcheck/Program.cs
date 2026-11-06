// =====================================================================================
// clockcheck — every time in the company's own time zone, never the server's (2026-10-05, HANDOVER §85).
//
// Clint: "I see the time zone for Anywhere I.T has changed again." Measured: the live server runs on
// UTC (timedatectl), and every portal time was the SERVER's local time — right on the office PC,
// two hours behind live. A typed appointment was read the same way. And on Windows, with invariant
// globalisation, no IANA zone could be found at all, so web chat hours were checked in UTC there.
// Then: "Time zone should be selected at the start of creating the tenant, because selecting the us
// as location still could have different time zones."
//
//   THE ZONES      every zone offered is found on THIS (Windows) machine, with its real offset.
//   THE SUMS       to the company's time and back; the hour clocks skip is read, not refused; web
//                  chat hours open at 08:00 South African time, not 08:00 UTC.
//   THE MIGRATION  every company gets a zone from its region (Anywhere IT: South Africa).
//   SIGNUP         the US, Canada and Europe must choose one of their own; South Africa is filled in.
//   THE PAGES      on a server whose own clock is SOUTH AFRICAN, a company in NEW YORK sees New York
//                  times, and a typed appointment is New York time; change it on Branding and the
//                  pages follow at once.
//
//   dotnet run -c Debug --project tools/clockcheck/clockcheck.csproj
// =====================================================================================

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;
using RemoteSupport.Tools;

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}
void Head(string s) { Console.WriteLine(); Console.WriteLine($"== {s} =="); }

Console.WriteLine("clockcheck — the company's clock, not the server's");
Console.WriteLine($"(this machine's own zone: {TimeZoneInfo.Local.Id})");
var dir = Path.Combine(Path.GetTempPath(), "clockcheck-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dir);
const string Pw = "ClockCheck!2026";
Process? server = null;

try
{
    // ---- the zones -----------------------------------------------------------------------------
    Head("every zone offered is found here, with its real offset");
    var jan = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
    var expected = new Dictionary<string, double> { ["Africa/Johannesburg"] = 2, ["Europe/London"] = 0, ["America/New_York"] = -5, ["America/St_Johns"] = -3.5,
        ["America/Regina"] = -6, ["Europe/Helsinki"] = 2, ["Pacific/Honolulu"] = -10, ["UTC"] = 0 };
    var missing = CompanyClock.Zones.Where(z => z.Id != "UTC" && CompanyClock.Find(z.Id).BaseUtcOffset == TimeSpan.Zero && !z.Id.StartsWith("Europe/")).Select(z => z.Id).ToList();
    Check("no zone falls back to UTC on this machine", missing.Count == 0, string.Join(", ", missing));
    foreach (var (id, offset) in expected)
        Check($"{id}: {offset:+0.#;-0.#;0} in January", CompanyClock.Find(id).GetUtcOffset(jan) == TimeSpan.FromHours(offset), CompanyClock.Find(id).GetUtcOffset(jan).ToString());
    Check("an unknown name is South Africa, never a silent UTC", CompanyClock.Find("Mars/Olympus").BaseUtcOffset == TimeSpan.FromHours(2));

    // ---- the sums ------------------------------------------------------------------------------
    Head("the sums");
    var sa = CompanyClock.Find("Africa/Johannesburg"); var ny = CompanyClock.Find("America/New_York");
    var noon = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    Check("12:00 UTC is 14:00 in South Africa and 08:00 in New York (summer time)", noon.Local(sa).Hour == 14 && noon.Local(ny).Hour == 8);
    Check("09:00 typed in New York is stored as 13:00 UTC", CompanyClock.ToUtc(new DateTime(2026, 10, 20, 9, 0, 0), ny) == new DateTime(2026, 10, 20, 13, 0, 0));
    Check("...and shows as 09:00 again", CompanyClock.ToUtc(new DateTime(2026, 10, 20, 9, 0, 0), ny).Local(ny).Hour == 9);
    Check("02:30 on the night New York's clocks skip is read as 03:30, not refused",
        CompanyClock.ToUtc(new DateTime(2026, 3, 8, 2, 30, 0), ny).Local(ny) == new DateTime(2026, 3, 8, 3, 30, 0));
    // Monday 5 Oct 2026, 06:30 UTC = 08:30 in South Africa: open for 08:00–17:00 hours.
    var hours = WebSupportHours.Format(Enumerable.Range(0, 7).Select(i => i < 5 ? new WebSupportHours.Day(TimeSpan.FromHours(8), TimeSpan.FromHours(17)) : (WebSupportHours.Day?)null).ToArray());
    Check("web chat hours: 08:30 in South Africa (06:30 UTC) is OPEN — on Windows it was checked in UTC",
        WebSupportHours.IsOpen(true, hours, "Africa/Johannesburg", new DateTime(2026, 10, 5, 6, 30, 0, DateTimeKind.Utc)));
    Check("...and 17:30 there (15:30 UTC) is closed", !WebSupportHours.IsOpen(true, hours, "Africa/Johannesburg", new DateTime(2026, 10, 5, 15, 30, 0, DateTimeKind.Utc)));

    // ---- the migration -------------------------------------------------------------------------
    Head("the migration: a zone for every company, from its region");
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dir, "old.db")};Pooling=False").Options;
    if (HarnessDb.SqliteUpgradePart("a zone for every company"))
    {
    await using (var db = new PlatformDb(options))
    {
        await db.GetService<IMigrator>().MigrateAsync("20261005115155_SeatsAndPrice");
        foreach (var (id, slug, region, country) in new[] { (1, "anywhereit", "ZA", ""), (2, "uk", "UK", "United Kingdom"), (3, "us", "US", "United States"),
                     (4, "ca", "CA", "Canada"), (5, "ie", "EU", "Ireland"), (6, "de", "EU", "Germany") })
        {
            await Seed.OldTenant(db, id, slug, slug.ToUpperInvariant(), "k" + id);
            await db.Database.ExecuteSqlRawAsync(HarnessDb.Portable("UPDATE Tenants SET Region = {0}, Country = {1} WHERE Id = {2}"), region, country, id);
        }
        await db.GetService<IMigrator>().MigrateAsync();
    }
    await using (var db = new PlatformDb(options))
    {
        var z = await db.Tenants.OrderBy(t => t.Id).Select(t => t.TimeZone).ToListAsync();
        Check("Anywhere IT South Africa, UK London, US New York, Canada Toronto, Ireland Dublin, Germany Central Europe",
            z.SequenceEqual(new[] { "Africa/Johannesburg", "Europe/London", "America/New_York", "America/Toronto", "Europe/Dublin", "Europe/Amsterdam" }), string.Join(" ", z));
    }
    }

    // ---- signup --------------------------------------------------------------------------------
    Head("signup: chosen at the start");
    foreach (var (region, choose) in new[] { ("ZA", false), ("UK", false), ("US", true), ("CA", true), ("EU", true) })
        Check($"{Pricing.For(region).Name}: {(choose ? "must choose" : "filled in")}", CompanyClock.MustChoose(region) == choose);
    var usOpt = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dir, "us.db")};Pooling=False").Options;
    await using (var db = new PlatformDb(usOpt)) await HarnessDb.MigrateAsync(db);
    var usTenants = new TenantService(new Factory(usOpt), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Platform:Region"] = "US" }).Build(),
        dir, DataProtectionProvider.Create("clockcheck"));
    var usSha = Terms.For("US").Sha256;
    TenantService.SignupRequest Us(string? zone, string email) => new("US", null, "Lone Star IT", "Ann Ray", "+1 512 555 0100", email, Pw, null, null, true, usSha, "203.0.113.5", zone);
    var (none, e1) = await usTenants.SignupAsync(Us(null, "a@star.test"));
    Check("the United States with no time zone is refused", none == null && e1.Contains("time zone"), e1);
    var (wrong, e2) = await usTenants.SignupAsync(Us("Africa/Johannesburg", "b@star.test"));
    Check("...and with one that is not American", wrong == null, e2);
    var (tx, e3) = await usTenants.SignupAsync(Us("America/Chicago", "c@star.test"));
    Check("US Central is accepted and kept", tx?.TimeZone == "America/Chicago", e3);

    // ---- the pages -----------------------------------------------------------------------------
    Head("the pages: a New York company on a South African server");
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d != null && !File.Exists(Path.Combine(d.FullName, "RemoteSupport.sln"))) d = d.Parent;
    var proj = Path.Combine(d!.FullName, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
    var data = Path.Combine(dir, "data"); Directory.CreateDirectory(data);
    var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
    var baseUrl = $"http://127.0.0.1:{port}";
    ProcessStartInfo Psi(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "run", "--no-build", "-c", "Debug", "--project", proj, "--" }.Concat(args)) psi.ArgumentList.Add(a);
        psi.Environment["RSP_Platform__DataDirectory"] = data;
        HarnessDb.Use(psi.Environment, data);
        psi.Environment["RSP_Platform__AdminKey"] = "clockcheck-admin-key-0123456789abcdef0123456789abcdef";
        psi.Environment["RSP_Security__RequireTwoFactor"] = "false";
        psi.Environment["RSP_Urls"] = baseUrl; psi.Environment["ASPNETCORE_URLS"] = baseUrl;
        return psi;
    }
    async Task<string> Cli(params string[] args) { var p = Process.Start(Psi(args))!; var o = await p.StandardOutput.ReadToEndAsync(); await p.WaitForExitAsync(); return o; }
    var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{proj}\" -c Debug -v q -nologo") { RedirectStandardOutput = true, UseShellExecute = false })!;
    await build.StandardOutput.ReadToEndAsync(); await build.WaitForExitAsync();
    if (build.ExitCode != 0) { Check("the platform builds", false); return 1; }
    await Cli("create-tenant", "Big Apple IT", "bigapple", "7");
    await Cli("add-tech", "bigapple", "boss@apple.test", Pw, "Bea Boss", "--owner", "--admin");
    var set = await Cli("set-brand", "bigapple", "time-zone", "America/New_York");
    Check("the command line sets the company's zone", !set.Contains("Unknown") && !set.Contains("Not a time zone"), set.Trim());
    server = Process.Start(Psi("run"))!;
    var serverLog = Path.Combine(Path.GetTempPath(), "clockcheck-server.log"); File.WriteAllText(serverLog, "");
    // The server's own words, kept after the run (the temp folder is not): a 500 is read here.
    server.OutputDataReceived += (_, e) => { if (e.Data != null) lock (serverLog) File.AppendAllText(serverLog, e.Data + Environment.NewLine); };
    server.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (serverLog) File.AppendAllText(serverLog, e.Data + Environment.NewLine); };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    var until = DateTime.UtcNow.AddSeconds(90);
    while (DateTime.UtcNow < until) { try { using var c = new TcpClient(); await c.ConnectAsync("127.0.0.1", port); break; } catch { await Task.Delay(400); } }

    var http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
    string Token(string page) => Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
    var login = await http.GetStringAsync("/login");
    await http.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = "boss@apple.test", ["Password"] = Pw, ["__RequestVerificationToken"] = Token(login) }));
    var signedIn = DateTime.UtcNow;

    // "Last sign-in" on the People page was printed in raw UTC; now it is the company's time.
    string Shown(TimeZoneInfo z) => signedIn.Local(z).ToString("yyyy-MM-dd HH:mm");
    string ShownBefore(TimeZoneInfo z) => signedIn.AddMinutes(-1).Local(z).ToString("yyyy-MM-dd HH:mm");
    var people = await http.GetStringAsync("/portal/technicians");
    Check("the People page shows the last sign-in in NEW YORK time", people.Contains(Shown(ny)) || people.Contains(ShownBefore(ny)), Shown(ny));
    Check("...not the server's South African time, nor UTC", !people.Contains(Shown(sa)) && !people.Contains(Shown(TimeZoneInfo.Utc)));

    var form = await http.GetStringAsync("/portal/tickets/new");
    var made = await http.PostAsync("/portal/tickets/new", new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["Subject"] = "Clock check", ["Category"] = "4", ["AppointmentDate"] = "2026-10-20", ["AppointmentTime"] = "09:00",
        ["__RequestVerificationToken"] = Token(form)
    }));
    await using (var db = new PlatformDb(new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(data, "platform.db")};Pooling=False").Options))
    {
        var t = await db.Tickets.Where(x => x.Subject == "Clock check").FirstOrDefaultAsync();
        Check("an appointment typed as 09:00 is New York's 09:00 — stored 13:00 UTC", t?.AppointmentUtc == new DateTime(2026, 10, 20, 13, 0, 0),
            t == null ? $"no ticket ({(int)made.StatusCode})" : $"{t.AppointmentUtc:yyyy-MM-dd HH:mm} UTC");
        var page = WebUtility.HtmlDecode(await http.GetStringAsync($"/portal/tickets/{t?.Id}"));
        var i = page.IndexOf("Appointment", StringComparison.Ordinal);
        Check("...and the ticket shows it as 09:00", page.Contains("Appointment 2026-10-20 09:00"),
            i < 0 ? "no Appointment on the page" : Regex.Replace(page.Substring(i, Math.Min(200, page.Length - i)), @"\s+", " "));
    }

    var branding = await http.GetStringAsync("/portal/branding");
    Check("Branding offers the time zone, New York chosen", Regex.IsMatch(branding, "<option value=\"America/New_York\" selected"));
    // Every input of the Branding form as the page has it (attributes in any order), with the zone changed.
    var form2 = branding[branding.LastIndexOf("<form", branding.IndexOf("name=\"CompanyName\"", StringComparison.Ordinal), StringComparison.Ordinal)..];
    form2 = form2[..form2.IndexOf("</form>", StringComparison.Ordinal)];
    var fields = Regex.Matches(form2, "<input[^>]*>").Select(m => m.Value)
        .Where(tag => !tag.Contains("type=\"file\"") && (!tag.Contains("type=\"checkbox\"") || tag.Contains(" checked")))
        .Select(tag => new KeyValuePair<string, string>(Regex.Match(tag, "name=\"([^\"]+)\"").Groups[1].Value, WebUtility.HtmlDecode(Regex.Match(tag, "value=\"([^\"]*)\"").Groups[1].Value)))
        .Where(kv => kv.Key.Length > 0 && kv.Key != "__RequestVerificationToken").ToList();
    fields.Add(new("TimeZone", "Europe/London")); fields.Add(new("__RequestVerificationToken", Token(branding)));
    var posted = await http.PostAsync("/portal/branding", new FormUrlEncodedContent(fields));
    Check("Branding saves", posted.IsSuccessStatusCode && (await posted.Content.ReadAsStringAsync()).Contains("Branding saved"), $"HTTP {(int)posted.StatusCode}");
    var london = CompanyClock.Find("Europe/London");
    people = await http.GetStringAsync("/portal/technicians");
    Check("changed to London on Branding, the People page follows at once", people.Contains(Shown(london)) || people.Contains(ShownBefore(london)), Shown(london));
}
finally
{
    try { if (server is { HasExited: false }) server.Kill(true); } catch { }
    try { await Task.Delay(300); Directory.Delete(dir, true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
return fail == 0 ? 0 : 1;

file sealed class Factory : IDbContextFactory<PlatformDb>
{
    private readonly DbContextOptions<PlatformDb> _o;
    public Factory(DbContextOptions<PlatformDb> o) => _o = o;
    public PlatformDb CreateDbContext() => new(_o);
}
