// =====================================================================================
// freeaccount — a free-version account made by us from Owner → Free version (2026-10-23, HANDOVER §157).
//
// Clint: "Can we rather have a button that i can create the client in the portal from and send the links directly from
// there?", then "I need an option to cc the tech in when sending the links so that he can help her set it up."
//
// A real platform, signups OFF (as on the live server), a fake SMTP server, driven over HTTP:
//
//   REFUSED     no name, a copy address that is not one, an email already registered — nothing made.
//   MADE        free plan, Active at once, no trial; one person, Owner + Technician, no password yet; logged by name.
//   THE EMAIL   to them, the technician in Cc (visible) and Reply-To; from us; the set-up link, the installer and the console.
//   THE TERMS   the set-up page shows the FREE terms; no tick or another text's fingerprint is refused and spends nothing;
//               ticked: the password is set and the terms kept word for word. A full company's invitation shows no terms.
//   AGAIN       not set up: a new link, the old one dead; set up: the download links alone. A full company is refused.
//   NO MAIL     the company is still made, and the set-up link is shown once to pass on.
//
//   dotnet run -c Release --project tools/freeaccount/freeaccount.csproj
// =====================================================================================

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
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

Console.WriteLine("freeaccount — Owner → Free version: make the account, send the links, copy the technician");
var dir = Path.Combine(Path.GetTempPath(), "freeaccount-" + Guid.NewGuid().ToString("N")[..8]);
var data = Path.Combine(dir, "data"); Directory.CreateDirectory(data);
var d = new DirectoryInfo(AppContext.BaseDirectory);
while (d != null && !File.Exists(Path.Combine(d.FullName, "RemoteSupport.sln"))) d = d.Parent;
var proj = Path.Combine(d!.FullName, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
int port = FreePort();
var baseUrl = $"http://127.0.0.1:{port}";
const string AdminKey = "freeaccount-admin-key-0123456789abcdef0123456789abcdef";
const string Jane = "jane@free.test", Tech = "sipho@anywhereit.test";
using var smtp = new FakeSmtp(); smtp.Start();

ProcessStartInfo Psi(params string[] args)
{
    var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in new[] { "run", "--no-build", "-c", "Debug", "--project", proj, "--" }.Concat(args)) psi.ArgumentList.Add(a);
    psi.Environment["RSP_Platform__DataDirectory"] = data;
    HarnessDb.Use(psi.Environment, data);
    psi.Environment["RSP_Platform__AdminKey"] = AdminKey;
    psi.Environment["RSP_Platform__SignupsEnabled"] = "false";
    psi.Environment["RSP_Platform__Region"] = "ZA";
    psi.Environment["RSP_Platform__PublicUrl"] = baseUrl;
    psi.Environment["RSP_Urls"] = baseUrl; psi.Environment["ASPNETCORE_URLS"] = baseUrl;
    return psi;
}
async Task<string> Cli(params string[] args) { var p = Process.Start(Psi(args))!; var o = await p.StandardOutput.ReadToEndAsync(); await p.WaitForExitAsync(); return o; }

var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(data, "platform.db")};Pooling=False").Options;
Process? server = null;
try
{
    var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{proj}\" -c Debug -v q -nologo") { RedirectStandardOutput = true, UseShellExecute = false })!;
    await build.StandardOutput.ReadToEndAsync(); await build.WaitForExitAsync();
    if (build.ExitCode != 0) { Console.WriteLine("The platform did not build."); return 1; }
    var mailSet = await Cli("platform-mail", "set", "--host", "127.0.0.1", "--port", smtp.Port.ToString(), "--nossl",
        "--from", "noreply@free.test", "--from-name", "The Remote Support Program", "--copy-to", "copies@free.test");
    if (!mailSet.Contains("Saved")) { Console.WriteLine("Could not set the platform mailbox:\n" + mailSet); return 1; }
    // A full-version company beside the free ones, for the two guards that must leave it alone.
    await Cli("create-tenant", "Full Company", "fullco");
    server = Process.Start(Psi("run"))!;
    server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    var until = DateTime.UtcNow.AddSeconds(90);
    while (DateTime.UtcNow < until) { try { using var c = new TcpClient(); await c.ConnectAsync("127.0.0.1", port); break; } catch { await Task.Delay(400); } }

    HttpClient Browser() => new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(60) };
    async Task<(HttpStatusCode code, string html)> Get(HttpClient h, string path)
    {
        var r = await h.GetAsync(path);
        return (r.StatusCode, WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync()));
    }
    string TokenIn(string html) => Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
    async Task<(HttpStatusCode code, string html, string? to)> Post(HttpClient h, string path, string fromHtml, params (string k, string v)[] f)
    {
        var all = f.Select(x => new KeyValuePair<string, string>(x.k, x.v)).Append(new("__RequestVerificationToken", TokenIn(fromHtml)));
        var r = await h.PostAsync(path, new FormUrlEncodedContent(all));
        return (r.StatusCode, WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync()), r.Headers.Location?.ToString());
    }
    string Field(string html, string name) => Regex.Match(html, $"name=\"{name}\" value=\"([^\"]*)\"").Groups[1].Value;
    string Said(string html) => Regex.Match(html, "id=\"freeMessage\">([^<]*)<").Groups[1].Value.Trim();

    // ---- an admin --------------------------------------------------------------------------------
    var admin = Browser();
    var (_, loginForm) = await Get(admin, "/owner/login");
    var keyStep = await Post(admin, "/owner/login", loginForm, ("Key", AdminKey));
    var keySecret = Regex.Match(keyStep.html, "user-select:all\">([A-Z2-7 ]+)<").Groups[1].Value.Replace(" ", "");
    await Post(admin, "/owner/login", keyStep.html, ("UseKey", "true"), ("Pending", Field(keyStep.html, "Pending")), ("Code", RemoteSupport.Shared.Totp.Now(keySecret)));
    var (fcode, page) = await Get(admin, "/owner/free");
    Head("the page");
    Check("Owner → Free version opens with the form and the copy box", fcode == HttpStatusCode.OK && page.Contains("id=\"makeFree\"") && page.Contains("id=\"freeCc\""), fcode.ToString());
    Check("...it is in the owner menu", page.Contains("href=\"/owner/free\""));
    var (anon, _) = await Get(Browser(), "/owner/free");
    Check("...and nobody signed out reaches it", anon != HttpStatusCode.OK, anon.ToString());

    async Task<int> Tenants() { await using var db = new PlatformDb(options); return await db.Tenants.CountAsync(); }
    // The Basic version, chosen (§163: the Free version is what the form makes when nothing is chosen — basiccheck makes one).
    (string, string)[] Form(string name, string email, string cc = "", string company = "") =>
        new[] { ("Name", name), ("Email", email), ("Company", company), ("Phone", ""), ("Cc", cc), ("Plan", "basic") };

    // ---- refused ---------------------------------------------------------------------------------
    Head("refused, and nothing made");
    int before = await Tenants();
    var noName = await Post(admin, "/owner/free?handler=Make", page, Form("", Jane));
    Check("no name is refused", Said(noName.html).Contains("Enter their name"), Said(noName.html));
    var badCc = await Post(admin, "/owner/free?handler=Make", page, Form("Jane Smith", Jane, "sipho at anywhere"));
    Check("a copy address that is not one is refused, and what was typed stays", Said(badCc.html).Contains("does not look like an email address")
        && badCc.html.Contains("value=\"Jane Smith\""), Said(badCc.html));
    Check("...nothing was made", await Tenants() == before);

    // ---- made ------------------------------------------------------------------------------------
    Head("made, Active, one person with no password");
    var sentBefore = smtp.Count;
    var made = await Post(admin, "/owner/free?handler=Make", page, Form("Jane Smith", Jane, Tech));
    Check("made, and the page says so and who the links went to", Said(made.html).Contains("is ready — Basic version, 14-day trial")
        && Said(made.html).Contains($"went to {Jane} (copy to {Tech})"), Said(made.html));
    Tenant jane; Technician person;
    await using (var db = new PlatformDb(options))
    {
        jane = await db.Tenants.Include(x => x.Technicians).SingleAsync(x => x.SupportEmail == Jane);
        person = jane.Technicians.Single();
        Check("on the Basic plan (once free), a 14-day trial at once — nothing to approve; R50 a month after it", RemoteSupport.Shared.FreePlan.Is(jane.Plan) && jane.Status == TenantStatus.Trial
            && jane.ApprovedUtc != null && jane.TrialEndsUtc is { } te && Math.Abs((te - DateTime.UtcNow.AddDays(14)).TotalHours) < 1, $"{jane.Plan} {jane.Status} {jane.TrialEndsUtc:yyyy-MM-dd}");
        Check("...no company typed: her own name stands in", jane.CompanyName == "Jane Smith" && jane.Slug == "jane-smith", $"{jane.CompanyName} / {jane.Slug}");
        Check("...South Africa, Johannesburg time", jane.Region == "ZA" && jane.Country == "South Africa" && jane.TimeZone == CompanyClock.SouthAfrica);
        Check("one person, Owner + Technician, no password yet", person.Role == TenantService.FreeRole && person.AwaitingSetup && person.DisplayName == "Jane Smith");
        Check("no terms accepted yet", !await db.TermsAcceptances.AnyAsync(a => a.TenantId == jane.Id));
        Check("the admin log names who made it", await db.AdminLog.AnyAsync(e => e.Company == "Jane Smith" && e.What.Contains("Made a Basic account") && e.Who.Length > 0)
            && await db.AdminLog.AnyAsync(e => e.Company == "Jane Smith" && e.What.Contains($"copy to {Tech}")));
    }
    var mail = await smtp.WaitFor(Jane, "set up and download");
    Check("one email reaches her", mail != null && smtp.Count == sentBefore + 1, $"{smtp.Count - sentBefore} sent");
    Check("...the technician in Cc, visibly, and Reply-To", mail != null && mail.Cc.Contains(Tech) && mail.ReplyTo.Contains(Tech) && mail.To.Contains(Tech), mail == null ? "" : $"Cc '{mail.Cc}' Reply-To '{mail.ReplyTo}'");
    Check("...from us, not a company she does not have", mail?.FromName == "The Remote Support Program", mail?.FromName ?? "");
    Check("...never a hidden copy to our own copy-to address (a set-up link is not for us)", mail != null && !mail.To.Contains("copies@free.test"));
    Check("...the set-up link, the installer and the console, three computers, R50 after 14 days and the card", mail != null && mail.SetupLink.Length > 0 && mail.Body.Contains($"{baseUrl}/download/jane-smith")
        && mail.Body.Contains($"{baseUrl}/tech/jane-smith") && mail.Body.Contains("3 in the price, more on Billing") && mail.Body.Contains("R50 a month") && mail.Body.Contains("Add your card on Billing"));
    Check("...and says the technician has it, to help", mail != null && mail.Body.Contains($"A copy of this email has gone to {Tech}"));
    var again = await Post(admin, "/owner/free?handler=Make", page, Form("Jane Again", Jane));
    Check("the same email again is refused — one login, one address", Said(again.html).Contains("already registered") && await Tenants() == before + 1, Said(again.html));
    var (_, listed) = await Get(admin, "/owner/free");
    Check("listed: not set up yet, terms not accepted, 0 of 3 machines, no card yet", listed.Contains($"data-free=\"{jane.Id}\"") && listed.Contains("Not set up yet")
        && listed.Contains("Terms not accepted yet") && listed.Contains("0 of 3") && listed.Contains($"data-pay-by=\"{jane.Id}\""));

    // ---- the terms, at set-up --------------------------------------------------------------------
    Head("the free terms, before the password");
    var freeTerms = Terms.For("ZA", "basic");
    var setupPath = new Uri(mail!.SetupLink).PathAndQuery;
    var secret = Regex.Match(setupPath, "t=([0-9A-F]+)").Groups[1].Value;
    var her = Browser();
    var (_, setup) = await Get(her, setupPath);
    Check("the set-up page shows the BASIC terms with a tick", setup.Contains("id=\"termsText\"") && setup.Contains("id=\"acceptTerms\"")
        && setup.Contains($"value=\"{freeTerms.Sha256}\"") && setup.Contains("Our terms for the Basic version"));
    // Found by walking it: the company is her, so "Jane Smith has added you" read as nonsense.
    Check("...and welcomes her to her own account, not \"Jane Smith has added you\"", setup.Contains("Your remote support account is ready") && setup.Contains("add your card on Billing")
        && !setup.Contains("Jane Smith has added you"));
    Check("...and does not jump past the terms to the password box", !setup.Contains("autofocus"));
    const string Pw = "FreeAccount!2026";
    async Task<bool> Spent() { await using var db = new PlatformDb(options); return (await db.AccountTokens.SingleAsync(x => x.TechnicianId == person.Id)).UsedUtc != null; }
    async Task<bool> HasPassword() { await using var db = new PlatformDb(options); return !(await db.Technicians.SingleAsync(x => x.Id == person.Id)).AwaitingSetup; }
    var noTick = await Post(her, "/account/setup", setup, ("T", secret), ("TermsSha", freeTerms.Sha256), ("Password", Pw), ("Again", Pw));
    Check("no tick: refused, the link not spent, no password", noTick.html.Contains("tick the box") && !await Spent() && !await HasPassword());
    var fullSha = await Post(her, "/account/setup", setup, ("T", secret), ("TermsSha", Terms.For("ZA").Sha256), ("Accept", "true"), ("Password", Pw), ("Again", Pw));
    Check("the FULL terms' fingerprint: refused the same way", fullSha.html.Contains("Our terms were updated") && !await Spent() && !await HasPassword());
    var ok = await Post(her, "/account/setup", setup, ("T", secret), ("TermsSha", freeTerms.Sha256), ("Accept", "true"), ("Password", Pw), ("Again", Pw));
    Check("ticked: the password is set and she goes on to the authenticator", await Spent() && await HasPassword()
        && (ok.html.Contains("One more step") || ok.to != null), ok.code.ToString());
    await using (var db = new PlatformDb(options))
    {
        var a = await db.TermsAcceptances.SingleOrDefaultAsync(x => x.TenantId == jane.Id);
        Check("the free terms kept word for word, accepted by her", a != null && a.Sha256 == freeTerms.Sha256 && a.Text == freeTerms.Source
            && a.AcceptedByEmail == Jane && a.AcceptedByName == "Jane Smith" && a.Version == freeTerms.Version, a?.Sha256 ?? "none");
    }
    var (_, reused) = await Get(Browser(), setupPath);
    Check("the link does not work twice", reused.Contains("already been used"));

    // ---- again -----------------------------------------------------------------------------------
    Head("the links again");
    (_, page) = await Get(admin, "/owner/free");
    Check("listed: set up, terms accepted", page.Contains("Set up") && !Regex.IsMatch(page, $"data-free=\"{jane.Id}\"[\\s\\S]*?Terms not accepted yet[\\s\\S]*?</tr>"));
    var mark = smtp.All.LastOrDefault();
    var resent = await Post(admin, "/owner/free?handler=Send", page, ("id", jane.Id.ToString()), ("cc", "thabo@anywhereit.test"));
    var downloads = await smtp.WaitFor(Jane, "the download links", mark);
    Check("set up: only the download links go, with the copy chosen NOW", Said(resent.html).Contains("only the download links") && downloads != null
        && downloads.Cc.Contains("thabo@anywhereit.test") && !downloads.Cc.Contains(Tech), Said(resent.html));
    Check("...no set-up link in it", downloads != null && downloads.SetupLink.Length == 0 && downloads.Body.Contains("/download/jane-smith") && downloads.Body.Contains("/tech/jane-smith"));

    var bob = await Post(admin, "/owner/free?handler=Make", page, Form("Bob Botha", "bob@free.test", "", "Botha Farms"));
    var bobMail = await smtp.WaitFor("bob@free.test", "set up and download");
    int bobId; await using (var db = new PlatformDb(options)) bobId = (await db.Tenants.SingleAsync(x => x.SupportEmail == "bob@free.test")).Id;
    Check("no copy: Bob's email has no Cc, and his company name is the one typed", bobMail != null && bobMail.Cc.Length == 0 && Said(bob.html).StartsWith("Botha Farms is ready"), Said(bob.html));
    mark = smtp.All.LastOrDefault();
    var bobAgain = await Post(admin, "/owner/free?handler=Send", bob.html, ("id", bobId.ToString()), ("cc", Tech));
    var bobMail2 = await smtp.WaitFor("bob@free.test", "set up and download", mark);
    Check("not set up: a NEW set-up link, with the technician copied", bobMail2 != null && bobMail2.SetupLink.Length > 0 && bobMail2.SetupLink != bobMail!.SetupLink
        && bobMail2.Cc.Contains(Tech), Said(bobAgain.html));
    var (_, oldBob) = await Get(Browser(), new Uri(bobMail!.SetupLink).PathAndQuery);
    Check("...and the old one no longer works", oldBob.Contains("This link cannot be used") && !oldBob.Contains("id=\"termsText\""));

    int fullId; await using (var db = new PlatformDb(options)) fullId = (await db.Tenants.SingleAsync(x => x.Slug == "fullco")).Id;
    var full = await Post(admin, "/owner/free?handler=Send", page, ("id", fullId.ToString()), ("cc", ""));
    Check("a full-version company is refused — its people are invited from its own People page", Said(full.html).Contains("full version"), Said(full.html));
    var fullInvite = await Cli("invite", "fullco", "owner@fullco.test", "Fiona Full", "--owner", "--admin");
    var fullMail = await smtp.WaitFor("owner@fullco.test", "set up your account");
    var (_, fullSetup) = await Get(Browser(), new Uri(fullMail?.SetupLink ?? baseUrl).PathAndQuery);
    Check("a full company's invitation: no terms on the set-up page, and the company's own email", fullMail != null && fullSetup.Contains("Set up your account")
        && !fullSetup.Contains("id=\"termsText\"") && fullSetup.Contains("autofocus") && fullMail.FromName == "Full Company", fullInvite.Trim());

    // ---- no mail ---------------------------------------------------------------------------------
    Head("when the email cannot go");
    await Cli("platform-mail", "set", "--port", FreePort().ToString());
    var noMail = await Post(admin, "/owner/free?handler=Make", page, Form("Nomsa Dube", "nomsa@free.test", Tech));
    Check("the account is still made, and says the email did not go", Said(noMail.html).Contains("Nomsa Dube is ready") && Said(noMail.html).Contains("did not go"), Said(noMail.html));
    Check("...the set-up link is shown once, to pass on", noMail.html.Contains("id=\"freeLink\"") && Regex.IsMatch(noMail.html, "/account/setup\\?t=[0-9A-F]+"));
    var (_, after) = await Get(admin, "/owner/free");
    Check("...and not again on the next visit", !after.Contains("id=\"freeLink\""));
}
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex); }
finally
{
    try { server?.Kill(true); } catch { }
    await Task.Delay(500);
    try { Directory.Delete(dir, true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} passed");
return fail == 0 ? 0 : 1;

/// <summary>Just enough SMTP for the platform's SmtpClient to deliver to (as invitecheck's).</summary>
sealed class FakeSmtp : IDisposable
{
    private TcpListener? _l;
    private readonly List<Message> _mails = new();
    public int Port { get; private set; }
    public int Count { get { lock (_mails) return _mails.Count; } }
    public List<Message> All { get { lock (_mails) return _mails.ToList(); } }

    public void Start()
    {
        _l = new TcpListener(IPAddress.Loopback, 0);
        _l.Start();
        Port = ((IPEndPoint)_l.LocalEndpoint).Port;
        _ = Task.Run(async () => { while (true) { TcpClient c; try { c = await _l.AcceptTcpClientAsync(); } catch { return; } _ = Handle(c); } });
    }

    private async Task Handle(TcpClient c)
    {
        using (c)
        {
            var s = c.GetStream();
            var r = new StreamReader(s, Encoding.UTF8);
            var w = new StreamWriter(s, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
            await w.WriteLineAsync("220 freeaccount ESMTP");
            var to = new List<string>();
            while (await r.ReadLineAsync() is { } line)
            {
                var cmd = line.ToUpperInvariant();
                if (cmd.StartsWith("EHLO") || cmd.StartsWith("HELO")) await w.WriteLineAsync("250 freeaccount");
                else if (cmd.StartsWith("RCPT TO:")) { to.Add(line[8..].Trim().Trim('<', '>').ToLowerInvariant()); await w.WriteLineAsync("250 OK"); }
                else if (cmd == "DATA")
                {
                    await w.WriteLineAsync("354 go ahead");
                    var sb = new StringBuilder();
                    while (await r.ReadLineAsync() is { } d && d != ".") sb.Append(d.StartsWith("..") ? d[1..] : d).Append("\r\n");
                    lock (_mails) _mails.Add(new Message(to.ToList(), sb.ToString()));
                    to.Clear();
                    await w.WriteLineAsync("250 queued");
                }
                else if (cmd == "QUIT") { await w.WriteLineAsync("221 bye"); break; }
                else await w.WriteLineAsync("250 OK");
            }
        }
    }

    public async Task<Message?> WaitFor(string to, string subjectPart, Message? after = null)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until)
        {
            List<Message> all; lock (_mails) all = _mails.ToList();
            var start = after == null ? 0 : all.IndexOf(after) + 1;
            var m = all.Skip(start).LastOrDefault(x => x.To.Contains(to) && x.Subject.Contains(subjectPart));
            if (m != null) return m;
            await Task.Delay(100);
        }
        return null;
    }

    public void Dispose() { try { _l?.Stop(); } catch { } }
}

/// <summary>One message, read as a mail program would: unfolded headers, decoded body.</summary>
sealed class Message
{
    public List<string> To { get; }
    public string From = "", FromName = "", ReplyTo = "", Cc = "", Subject = "", Body = "";
    public Message(List<string> to, string raw)
    {
        To = to;
        var split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = Regex.Replace(split < 0 ? raw : raw[..split], "\r\n[ \t]+", " ");
        var body = split < 0 ? "" : raw[(split + 4)..];
        string H(string n) => Regex.Match(head, $"^{n}:\\s*(.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value.Trim();
        From = Words(H("From")); ReplyTo = Words(H("Reply-To")).ToLowerInvariant(); Cc = Words(H("Cc")).ToLowerInvariant(); Subject = Words(H("Subject"));
        FromName = Regex.Match(From, "^\"?([^\"<]+?)\"?\\s*<").Groups[1].Value.Trim();
        var cte = H("Content-Transfer-Encoding").ToLowerInvariant();
        Body = cte == "base64" ? Encoding.UTF8.GetString(Convert.FromBase64String(Regex.Replace(body, "\\s", "")))
             : cte == "quoted-printable" ? QuotedPrintable(body) : body;
        Body = Body.Replace("\r\n", "\n");
    }

    public string SetupLink => Regex.Match(Body, @"http://127\.0\.0\.1:\d+/account/setup\?t=[0-9A-F]+").Value;

    private static string QuotedPrintable(string s)
    {
        s = s.Replace("=\r\n", "");
        var bytes = new List<byte>();
        for (int i = 0; i < s.Length; i++)
            if (s[i] == '=' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2])) { bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16)); i += 2; }
            else bytes.AddRange(Encoding.UTF8.GetBytes(s[i].ToString()));
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static string Words(string v) => Regex.Replace(v, @"=\?([^?]+)\?([BbQq])\?([^?]*)\?=", m =>
        m.Groups[2].Value.ToUpperInvariant() == "B"
            ? Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[3].Value))
            : QuotedPrintable(m.Groups[3].Value.Replace('_', ' ')));
}
