// =====================================================================================
// m365check - proves "Sign in with Microsoft" for Tickets by email (2026-10-19, HANDOVER §151).
//
// Microsoft 365 no longer takes a mailbox password from a program like ours, so a company on 365 had only forwarding,
// which its administrator must allow. Now: one multi-tenant Entra app of ours, delegated IMAP.AccessAsUser.All and
// SMTP.Send (never Mail.Read), the refresh token kept encrypted, mail read by IMAP with XOAUTH2 and replies sent by SMTP
// with XOAUTH2 — keeping every rule Tickets by email already had (read-only by UID, the starting point, a refusal tried
// ONCE). When Microsoft refuses the sign-in, checking stops, the page says so in plain words and offers Sign in again.
//
// Two halves:
//   THE RULES, in-process — the scopes asked for, a token refusal in words (theirs, ours, later), Microsoft's answers
//     at the way back, a mailbox that says no to a good token, the id_token read.
//   THE WIRE, on a platform of its own, with a FAKE MICROSOFT (its token endpoint), a FAKE IMAP SERVER and a FAKE SMTP
//     SERVER inside this harness that take only XOAUTH2 with a token the fake Microsoft gave out: the app put on by the
//     command line, the portal's button, the way back checked (tampered, another browser, another start), the code
//     redeemed with PKCE, the token kept encrypted and rotated, real mail made a ticket by the real timer and the real
//     reply caught, a shared mailbox, IMAP switched off, OUR app refused, THEIR sign-in refused — stopped, said, not
//     asked again, and signing in again carrying on — and Disconnect.
//
//   dotnet run -c Debug --project tools/m365check/m365check.csproj           (RSP_TEST_DB=postgres for PostgreSQL)
// =====================================================================================

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;
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

var root = AppContext.BaseDirectory;
while (root != null && !File.Exists(Path.Combine(root, "CLAUDE.md"))) root = Path.GetDirectoryName(root);
if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }
var dataDir = Path.Combine(Path.GetTempPath(), "rs-m365check-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dataDir);
var port = FreePort();
var baseUrl = $"http://127.0.0.1:{port}";
var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");

const string Pw = "m365-check-2026";
const string Slug = "m365-co", OtherSlug = "other-co";
const string Support = "support@m365co.test", Admin = "admin@m365co.test", Tid = "9f0e8d7c-1111-2222-3333-444455556666";
const string ClientId = "1b2c3d4e-5f60-4718-9a0b-c1d2e3f40516", Secret = "our-app-secret-NEVER-PRINTED";
const string PlatformFrom = "noreply@platform.test";

using var ms = new FakeMicrosoft(ClientId, Secret);
using var imap = new FakeImap(ms);
using var smtp = new FakeSmtp(ms);
ms.Start(); imap.Start(); smtp.Start();
imap.AddMailbox(Support); imap.AddMailbox(Admin);
Console.WriteLine($"m365check — Sign in with Microsoft; a platform on port {port}, Microsoft on {ms.Port}, IMAP on {imap.Port}, SMTP on {smtp.Port}");

Process? server = null;
try
{
    // =========================================================== the rules
    Head("what we ask Microsoft for");
    var scopes = MicrosoftSignIn.SignInScopes.Split(' ');
    Check("delegated IMAP and SMTP for the signed-in mailbox, and a refresh token",
        scopes.Contains("https://outlook.office.com/IMAP.AccessAsUser.All") && scopes.Contains("https://outlook.office.com/SMTP.Send") && scopes.Contains("offline_access"),
        MicrosoftSignIn.SignInScopes);
    Check("never an application-wide Mail.Read, Mail.ReadWrite, full_access_as_app or .default",
        !scopes.Any(x => x.Contains("Mail.Read", StringComparison.OrdinalIgnoreCase) || x.Contains("full_access", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".default")));

    Head("Microsoft refusing a token, in words — theirs, ours, or later");
    var (f1, w1) = MicrosoftSignIn.Explain("invalid_grant", "AADSTS50173: The provided grant has expired due to it being revoked, a fresh auth token is needed. The user might have changed or reset their password.");
    Check("a changed password ends THEIR sign-in, says so, and says sign in again", f1 == MsFailure.Refused && w1.Contains("password was changed") && w1.Contains("Sign in with Microsoft"), w1);
    var (f2, w2) = MicrosoftSignIn.Explain("invalid_grant", "AADSTS65001: The user or administrator has not consented to use the application.");
    Check("a permission withdrawn ends their sign-in", f2 == MsFailure.Refused && w2.Contains("permission") && w2.Contains("withdrawn"), w2);
    var (f3, _) = MicrosoftSignIn.Explain("interaction_required", "AADSTS50076: Due to a configuration change made by your administrator ... you must use multi-factor authentication");
    Check("a new sign-in step their organisation asks for ends it too", f3 == MsFailure.Refused);
    var (f4, w4) = MicrosoftSignIn.Explain("invalid_client", "AADSTS7000215: Invalid client secret provided.");
    Check("OUR secret refused is OURS — their sign-in is kept", f4 == MsFailure.Ours && w4.Contains("ours to fix") && w4.Contains("kept"), w4);
    var (f5, _) = MicrosoftSignIn.Explain("temporarily_unavailable", "", 503);
    Check("Microsoft having trouble is tried again later, not a stop", f5 == MsFailure.Later);

    Head("what came back to the way back, instead of a code");
    Check("cancelled or declined", MicrosoftSignIn.CallbackError("access_denied", "AADSTS65004: User declined to consent") == "declined");
    Check("only an administrator may approve the app", MicrosoftSignIn.CallbackError("access_denied", "AADSTS90094: The grant requires admin permission.") == "admin"
        && MicrosoftSignIn.CallbackWords("admin").Contains("administrator"));
    Check("a personal or another organisation's account", MicrosoftSignIn.CallbackError("invalid_request", "AADSTS50020: User account from identity provider does not exist in tenant") == "account");
    Check("our app not recognised", MicrosoftSignIn.CallbackError("unauthorized_client", "AADSTS700016: Application not found in the directory") == "ours");
    Check("the codes are short words, fit for an address bar", new[] { "declined", "admin", "account", "ours", "expired", "failed", "approved" }.All(c => c.Length <= 20 && MicrosoftSignIn.CallbackWords(c).Length > 20));

    Head("a mailbox that says no to a good token");
    var reader = new MailServer("outlook.office365.com", 993, "ssl", Support, "") { AccessToken = "x", SignedInAs = Support };
    var (r1, stop1) = MailWire.Explain(new AuthenticationException("AUTHENTICATE failed."), reader, false, Support);
    Check("reading refused: IMAP switched off for the mailbox, where to switch it on, and checking stops", r1.Contains("IMAP is switched off") && r1.Contains("Manage email apps") && stop1, r1[..Math.Min(110, r1.Length)]);
    var sharedReader = reader with { SignedInAs = Admin };
    Check("...and for a shared mailbox, that the person signed in needs Full Access", MailWire.Explain(new AuthenticationException("x"), sharedReader, false, Support).words.Contains("Full Access"));
    var sender = new MailServer("smtp.office365.com", 587, "starttls", Admin, "") { AccessToken = "x", SignedInAs = Admin };
    var (s1, _) = MailWire.Explain(new AuthenticationException("535 5.7.3"), sender, true, Support);
    Check("sending refused: SMTP AUTH switched off, for the mailbox or the organisation", s1.Contains("SMTP AUTH is switched off") && s1.Contains("Authenticated SMTP"), s1[..Math.Min(110, s1.Length)]);
    var sendAs = new SmtpCommandException(SmtpErrorCode.MessageNotAccepted, SmtpStatusCode.TransactionFailed, "5.2.252 SendAsDenied; admin@m365co.test not allowed to send as support@m365co.test");
    Check("not allowed to send as the shared mailbox: Send as, and where", MailWire.Explain(sendAs, sender, true, Support).words.Contains("\"Send as\""));
    var pwMs = new MailServer("outlook.office365.com", 993, "ssl", Support, "pw");
    Check("a PASSWORD refused by Microsoft now points at Sign in with Microsoft", MailWire.Explain(new AuthenticationException("x"), pwMs, false, Support).words.Contains("sign in with Microsoft"));

    Head("who signed in, from the id_token");
    var claims = MicrosoftSignIn.Claims(FakeMicrosoft.Jwt(new() { ["preferred_username"] = "Support@M365co.test", ["tid"] = Tid, ["nonce"] = "n1" }));
    Check("its claims are read", claims["preferred_username"] == "Support@M365co.test" && claims["tid"] == Tid && claims["nonce"] == "n1");

    // =========================================================== the wire
    Head("the platform, and our app on it from the command line");
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
    ProcessStartInfo Psi()
    {
        var p = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "run", "-c", "Debug", "--project", proj }) p.ArgumentList.Add(a);
        p.Environment["RSP_Platform__DataDirectory"] = dataDir;
        HarnessDb.Use(p.Environment, dataDir);
        p.Environment["RSP_Security__RequireTwoFactor"] = "false";   // a throwaway platform (HANDOVER §72)
        p.Environment["RSP_Platform__PublicUrl"] = baseUrl;
        p.Environment["RSP_Testing__IntakeSeconds"] = "2";           // the minute, made two seconds
        // Microsoft, made this harness: its sign-in service, and its mail servers.
        p.Environment["RSP_Microsoft__Authority"] = $"http://127.0.0.1:{ms.Port}";
        p.Environment["RSP_Microsoft__ImapHost"] = "127.0.0.1"; p.Environment["RSP_Microsoft__ImapPort"] = imap.Port.ToString();
        p.Environment["RSP_Microsoft__SmtpHost"] = "127.0.0.1"; p.Environment["RSP_Microsoft__SmtpPort"] = smtp.Port.ToString();
        p.Environment["RSP_Urls"] = baseUrl;
        p.Environment["ASPNETCORE_URLS"] = baseUrl;
        return p;
    }
    async Task<string> Cli(Dictionary<string, string>? env, params string[] args)
    {
        var p = Psi(); p.ArgumentList.Add("--no-build"); p.ArgumentList.Add("--");
        foreach (var a in args) p.ArgumentList.Add(a);
        if (env != null) foreach (var (k, v) in env) p.Environment[k] = v;
        var pr = Process.Start(p)!; var o = await pr.StandardOutput.ReadToEndAsync(); await pr.WaitForExitAsync(); return o;
    }
    var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{proj}\" -c Debug -v q -nologo") { RedirectStandardOutput = true, UseShellExecute = false })!;
    var built = await build.StandardOutput.ReadToEndAsync(); await build.WaitForExitAsync();
    if (build.ExitCode != 0) { Console.WriteLine("The platform did not build:\n" + built); return 1; }
    // Run once with no command so the database is made, then companies and people by the command line.
    Check("a company and its people", (await Cli(null, "create-tenant", "M365 Co", Slug, "7")).Contains("Created tenant"));
    await Cli(null, "create-tenant", "Other Co", OtherSlug, "7");
    await Cli(null, "add-tech", Slug, "owner@m365co.test", Pw, "Olga Owner", "--owner", "--admin");
    await Cli(null, "add-tech", Slug, "tech@m365co.test", Pw, "Tess Tech", "--technician");
    await Cli(null, "add-tech", OtherSlug, "owner@other.test", Pw, "Otto Other", "--owner", "--admin");
    await Cli(null, "platform-mail", "set", "--host", "127.0.0.1", "--port", smtp.Port.ToString(), "--nossl", "--from", PlatformFrom, "--from-name", "The Remote Support Program");
    var none = await Cli(null, "microsoft-app", "show");
    Check("`microsoft-app show` before: not set, so not offered", none.Contains("not set — Sign in with Microsoft is not offered"), none.Split('\n')[0].Trim());
    var bad = await Cli(new() { ["RSP_MS_CLIENT_SECRET"] = Secret }, "microsoft-app", "set", "--client-id", "not-a-guid");
    Check("a client id that is not one is refused", bad.Contains("Application (client) ID"));
    var set = await Cli(new() { ["RSP_MS_CLIENT_SECRET"] = Secret }, "microsoft-app", "set", "--client-id", ClientId, "--expires", "2028-10-19");
    Check("`microsoft-app set`, the secret from the environment, never an argument", set.Contains("Saved: the Microsoft app") && set.Contains("/portal/mailintake/microsoft"), set.Trim().Split('\n').Last());
    var shown = await Cli(null, "microsoft-app", "show");
    Check("`show` says stored, when it runs out, and the redirect URI — and never prints the secret",
        shown.Contains("stored") && shown.Contains("2028-10-19") && shown.Contains($"{baseUrl}/portal/mailintake/microsoft") && !shown.Contains(Secret));
    await using (var db = new PlatformDb(options))
    {
        var ps = await db.PlatformSettings.FirstAsync();
        Check("the secret is kept encrypted", ps.MsClientId == ClientId && ps.MsClientSecretEnc.Length > 0 && !ps.MsClientSecretEnc.Contains(Secret));
    }

    var psi = Psi(); psi.ArgumentList.Add("--no-build"); psi.ArgumentList.Add("--"); psi.ArgumentList.Add("run");
    server = Process.Start(psi)!;
    server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    if (!await WaitForPort(port, 90000)) { Console.WriteLine("The platform never started listening."); return 1; }
    int tid; await using (var db = new PlatformDb(options)) tid = (await db.Tenants.FirstAsync(t => t.Slug == Slug)).Id;

    using (var anon = new HttpClient { BaseAddress = new Uri(baseUrl) })
    {
        var assoc = await anon.GetAsync("/.well-known/microsoft-identity-association.json");
        var assocJson = await assoc.Content.ReadAsStringAsync();
        Check("the publisher-domain file Microsoft fetches names our app, as JSON, to anybody",
            assoc.IsSuccessStatusCode && assoc.Content.Headers.ContentType?.MediaType == "application/json"
            && assocJson.Contains($"\"associatedApplications\":[{{\"applicationId\":\"{ClientId}\"}}]"), assocJson);
    }

    Head("the portal page");
    var owner = await Browser.SignInAsync(baseUrl, "owner@m365co.test", Pw);
    var page = Html(await owner.Get("/portal/mailintake"));
    Check("offers Microsoft 365 — sign in with Microsoft, chooseable", page.Contains("Microsoft 365 — sign in with Microsoft") && Regex.IsMatch(page, "value=\"microsoft\"(?![^>]*disabled)"));
    Check("...with the button", page.Contains("Sign in with Microsoft</button>"));
    Check("...and, before it, what to tick in Microsoft 365: IMAP and Authenticated SMTP",
        page.Contains("Before you sign in:") && page.IndexOf("Before you sign in:") < page.IndexOf("Sign in with Microsoft</button>")
        && Regex.Match(page, "Before you sign in:(.*?)</div>", RegexOptions.Singleline).Groups[1].Value is var note && note.Contains("<strong>IMAP</strong>") && note.Contains("<strong>Authenticated SMTP</strong>") && note.Contains("Turn off SMTP AUTH"));
    Check("...and the approval link for an administrator, our app's and nothing wider", page.Contains("/organizations/v2.0/adminconsent?client_id=" + ClientId) && !page.Contains("Mail.Read"));
    Check("What can go wrong says IMAP, SMTP AUTH, admin approval and Unverified", page.Contains("IMAP switched off") && page.Contains("SMTP AUTH switched off")
        && page.Contains("Need admin approval") && page.Contains("Unverified"));
    Check("...and keeps forwarding as the fallback", page.Contains("Forwarding still works"));
    var tech = await Browser.SignInAsync(baseUrl, "tech@m365co.test", Pw);
    Check("a technician cannot open it — by its heading, not a status code (§34)", !Html(await tech.Get("/portal/mailintake")).Contains("<h1>Tickets by email and form</h1>"));
    var techCb = await tech.Raw.GetAsync("/portal/mailintake/microsoft?code=x&state=y");
    Check("...nor the way back", techCb.StatusCode != HttpStatusCode.Redirect || techCb.Headers.Location?.ToString().Contains("ms=") != true, $"{(int)techCb.StatusCode} {techCb.Headers.Location}");

    Dictionary<string, string> Form(string way = "microsoft", bool enabled = true, string address = Support) => new()
    {
        ["emailEnabled"] = enabled ? "true" : "false", ["address"] = address, ["way"] = way,
        ["imapHost"] = "", ["imapPort"] = "993", ["imapSecurity"] = "ssl", ["imapUser"] = "", ["imapPassword"] = "", ["folder"] = "INBOX",
        ["autoReply"] = "true", ["sendVia"] = "own", ["smtpSameLogin"] = "true", ["smtpHost"] = "", ["smtpPort"] = "465", ["smtpSecurity"] = "ssl",
        ["smtpUser"] = "", ["smtpPassword"] = "", ["autoReplyText"] = TicketIntake.DefaultReply
    };
    var early = Html(await owner.Post("/portal/mailintake?handler=Email", Form()));
    Check("Save and test before anybody signed in says to press Sign in with Microsoft", early.Contains("Press Sign in with Microsoft"));

    Head("Sign in with Microsoft: where the browser is sent");
    async Task<(Uri url, Dictionary<string, string> q)> Start(Browser b, string address = Support)
    {
        var r = await b.Post("/portal/mailintake?handler=Microsoft", Form(address: address), follow: false);
        var loc = r.Headers.Location ?? new Uri("http://none/");
        return (loc, Q(loc));
    }
    var (go, q) = await Start(owner);
    Check("to Microsoft's authorize page, organizations", go.ToString().StartsWith($"http://127.0.0.1:{ms.Port}/organizations/oauth2/v2.0/authorize?"), go.GetLeftPart(UriPartial.Path));
    Check("our app, back to our one redirect URI, a code", q.GetValueOrDefault("client_id") == ClientId && q.GetValueOrDefault("redirect_uri") == $"{baseUrl}/portal/mailintake/microsoft" && q.GetValueOrDefault("response_type") == "code");
    Check("asking exactly the scopes above", q.GetValueOrDefault("scope") == MicrosoftSignIn.SignInScopes, q.GetValueOrDefault("scope") ?? "");
    Check("PKCE (S256), a state and a nonce", q.GetValueOrDefault("code_challenge_method") == "S256" && (q.GetValueOrDefault("code_challenge") ?? "").Length >= 43
        && (q.GetValueOrDefault("state") ?? "").Length > 40 && (q.GetValueOrDefault("nonce") ?? "").Length > 10);
    Check("the support address filled in, and Microsoft always asking WHICH account", q.GetValueOrDefault("login_hint") == Support && q.GetValueOrDefault("prompt") == "select_account");
    Check("a cookie that only this browser sends back: HttpOnly, SameSite=Lax", owner.SetCookie.Any(c => c.StartsWith("rsp_ms=") && c.Contains("httponly", StringComparison.OrdinalIgnoreCase) && c.Contains("samesite=lax", StringComparison.OrdinalIgnoreCase)));
    await using (var db = new PlatformDb(options))
        Check("the form was saved first: Microsoft 365, the support address", await db.TicketIntake.AnyAsync(s => s.TenantId == tid && s.Way == "microsoft" && s.Address == Support && s.MsRefreshTokenEnc == ""));

    Head("the way back, refused when it is not this sign-in's");
    var code1 = ms.Issue(Support, Tid, q["nonce"], q["code_challenge"], q["redirect_uri"]);
    var other = await Browser.SignInAsync(baseUrl, "owner@other.test", Pw);
    var stolen = await other.Raw.GetAsync($"/portal/mailintake/microsoft?code={code1}&state={Uri.EscapeDataString(q["state"])}");
    Check("in ANOTHER company's browser (no cookie, another company): sent back with 'expired'", stolen.Headers.Location?.ToString() == "/portal/mailintake?ms=expired", stolen.Headers.Location?.ToString() ?? "");
    var tampered = await owner.Raw.GetAsync($"/portal/mailintake/microsoft?code={code1}&state={Uri.EscapeDataString(q["state"][..^4] + "AAAA")}");
    Check("a tampered state in the RIGHT browser: refused", tampered.Headers.Location?.ToString() == "/portal/mailintake?ms=expired");
    var (_, qa) = await Start(owner);
    var (_, qb) = await Start(owner);   // a second start: its cookie replaces the first's
    var crossed = await owner.Raw.GetAsync($"/portal/mailintake/microsoft?code={ms.Issue(Support, Tid, qa["nonce"], qa["code_challenge"], qa["redirect_uri"])}&state={Uri.EscapeDataString(qa["state"])}");
    Check("the first start's state with the second start's cookie: refused", crossed.Headers.Location?.ToString() == "/portal/mailintake?ms=expired");
    Check("...and none of them was redeemed at Microsoft", ms.Count("authorization_code") == 0, $"{ms.Count("authorization_code")} codes redeemed");
    var expiredPage = Html(await owner.Get("/portal/mailintake?ms=expired"));
    Check("the page says why, in words", expiredPage.Contains("not started from this page in the last 15 minutes"));
    await using (var db = new PlatformDb(options))
        Check("nothing was connected", await db.TicketIntake.AnyAsync(s => s.TenantId == tid && s.MsRefreshTokenEnc == ""));

    Head("the way back, as it should be");
    var (_, q2) = await Start(owner);
    var code2 = ms.Issue(Support, Tid, q2["nonce"], q2["code_challenge"], q2["redirect_uri"]);
    var back = await owner.Raw.GetAsync($"/portal/mailintake/microsoft?code={code2}&state={Uri.EscapeDataString(q2["state"])}");
    Check("sent back to Tickets by email, 'connected' — a code in the address, not words", back.Headers.Location?.ToString() == "/portal/mailintake?ms=connected", back.Headers.Location?.ToString() ?? $"{(int)back.StatusCode}");
    Check("the code was redeemed with our secret and the PKCE verifier that matches", ms.Count("authorization_code") == 1 && ms.LastCodeVerified);
    var connected = Html(await owner.Get("/portal/mailintake?ms=connected"));
    Check("the page: signed in with Microsoft as the support mailbox", connected.Contains($"Signed in with Microsoft as {Support}"), Snip(connected, "Signed in with"));
    Check("...tested at once: read as that account, its inbox counted", connected.Contains($"Reading: signed in with Microsoft as {Support}") && connected.Contains("holds 0 messages"), Snip(connected, "Reading:"));
    Check("...and sending signed in to Microsoft 365", connected.Contains($"Sending: signed in to Microsoft 365 as {Support}"), Snip(connected, "Sending:"));
    Check("the status line: on, signed in as", connected.Contains($"on — signed in with Microsoft as {Support}"));
    Check("Disconnect is offered", connected.Contains(">Disconnect</button>"));
    Check("...and the before-you-sign-in note has gone (the test now says whether it reads and sends)", !connected.Contains("Before you sign in:"));
    var again = await owner.Raw.GetAsync($"/portal/mailintake/microsoft?code={code2}&state={Uri.EscapeDataString(q2["state"])}");
    Check("the same way back again (a refresh) does nothing: the cookie is spent", again.Headers.Location?.ToString() == "/portal/mailintake?ms=expired" && ms.Count("authorization_code") == 1);
    TicketIntakeSettings row;
    await using (var db = new PlatformDb(options)) row = await db.TicketIntake.FirstAsync(s => s.TenantId == tid);
    Check("kept: the account, its organisation, who pressed it, email switched on", row.MsUser == Support && row.MsTenant == Tid && row.MsConnectedBy == "Olga Owner" && row.EmailEnabled && row.Way == "microsoft");
    Check("the refresh token kept ENCRYPTED — no token Microsoft gave out is in the database", row.MsRefreshTokenEnc.Length > 40 && !ms.AllTokens().Any(t => row.MsRefreshTokenEnc.Contains(t)));
    Check("no password was stored or asked for", row.ImapPasswordEnc == "" && row.SmtpPasswordEnc == "");
    Check("its token is refreshed against THEIR organisation, not 'organizations'", ms.Directories("refresh_token").All(d => d == Tid) && ms.Count("refresh_token") > 0, string.Join(",", ms.Directories("refresh_token").Distinct()));
    Check("...asking only the mail scopes again", ms.LastRefreshScope == "offline_access " + MicrosoftSignIn.MailScopes, ms.LastRefreshScope);

    Head("mail arrives — a ticket by the real timer, and the reply through Microsoft 365");
    await Task.Delay(2500);
    imap.Deliver(Support, Mime("Jane Smith <jane@acme.test>", Support, "Printer jams", "The printer in accounts jams on every page.", mid: "<jane-m365@acme.test>"));
    var janeT = await WaitTicket(options, tid, "Printer jams");
    Check("a ticket opens by itself", janeT != null && janeT.Source == TicketSource.Email && janeT.RequesterEmail == "jane@acme.test");
    var reply = janeT == null ? null : await smtp.WaitFor("jane@acme.test", $"[Ticket #{janeT.Number}]");
    Check("the auto-reply leaves through Microsoft 365, from the support address", reply != null && reply.FromAddress == Support, reply == null ? "nothing arrived" : reply.FromAddress);
    Check("...signed in by XOAUTH2 as the account, with a token Microsoft gave out — never a password",
        reply != null && reply.AuthUser == Support && reply.Mechanism == "XOAUTH2", reply == null ? "" : $"{reply.Mechanism} as {reply.AuthUser}");
    Check("only XOAUTH2 at the mailbox: no LOGIN with a password, ever", imap.Mechanisms(Support).Count > 0 && imap.Mechanisms(Support).All(m => m == "XOAUTH2") && imap.PasswordLogins == 0,
        string.Join(",", imap.Mechanisms(Support).Distinct()));
    var cmds = imap.Commands(Support);
    var writes = cmds.Where(c => c is "SELECT" or "STORE" or "UID STORE" or "EXPUNGE" or "UID EXPUNGE" or "COPY" or "UID COPY" or "MOVE" or "UID MOVE" or "APPEND" or "DELETE").Distinct().ToList();
    Check("read-only, as every mailbox is: EXAMINE, never SELECT, STORE, EXPUNGE, COPY or MOVE", cmds.Contains("EXAMINE") && writes.Count == 0, writes.Count == 0 ? $"{cmds.Count} commands" : string.Join(", ", writes));
    Check("...and BODY.PEEK, which marks nothing read", imap.PeekOnly);
    await using (var db = new PlatformDb(options)) row = await db.TicketIntake.FirstAsync(s => s.TenantId == tid);
    Check("Microsoft's new refresh token with each refresh is STORED (rotated)", ms.RotatedFrom.Count > 1 && ms.RotatedFrom.Skip(1).Any(rt => rt != "RT-1"),
        string.Join(" → ", ms.RotatedFrom.Take(4)));
    Check("an update to the client is now offered from a ticket (Tickets by email is ready)", ClientUpdateService.Ready(row));

    Head("IMAP switched off for the mailbox: said once, and not asked again");
    imap.ImapOff.Add(Support);
    var off = Html(await owner.Post("/portal/mailintake?handler=Email", Form()));
    Check("Save and test says IMAP is switched off, and where", off.Contains("would not let") && off.Contains("IMAP is switched off") && off.Contains("Manage email apps"), Snip(off, "would not let"));
    await using (var db = new PlatformDb(options)) row = await db.TicketIntake.FirstAsync(s => s.TenantId == tid);
    Check("checking has stopped — but the sign-in is kept (it was good)", row.Paused && row.MsRefreshTokenEnc.Length > 0);
    var tries = imap.Attempts(Support);
    await Task.Delay(7000);
    Check("three timer ticks later it has not tried again", imap.Attempts(Support) == tries, $"{tries} then {imap.Attempts(Support)}");
    imap.ImapOff.Remove(Support);
    var on = Html(await owner.Post("/portal/mailintake?handler=Email", Form()));
    Check("switched back on, Save and test works again", on.Contains("The test worked"), Snip(on, "The test"));

    Head("OUR app refused (an expired secret): theirs is kept, and it is said to be ours");
    ms.RejectClient = true;
    await WaitUntil(async () => { await using var db = new PlatformDb(options); return (await db.TicketIntake.FirstAsync(s => s.TenantId == tid)).LastProblem.Contains("ours to fix"); }, 15000);
    await using (var db = new PlatformDb(options)) row = await db.TicketIntake.FirstAsync(s => s.TenantId == tid);
    Check("the page's problem says ours to fix, not theirs", row.LastProblem.Contains("ours to fix"), row.LastProblem);
    Check("...their sign-in is NOT deleted, and checking is not stopped (it backs off)", row.MsRefreshTokenEnc.Length > 0 && !row.Paused && row.NextTryUtc != null);
    ms.RejectClient = false;
    await PostAndForget(owner, Form());   // saving starts checking again at once, as after any trouble

    Head("THEIR sign-in refused — the password changed: stopped, said, and not asked again");
    var beforeRefusal = ms.Count("refresh_token");
    ms.Revoke(Support, "AADSTS50173: The provided grant has expired due to it being revoked, a fresh auth token is needed. The user might have changed or reset their password.");
    await WaitUntil(async () => { await using var db = new PlatformDb(options); return (await db.TicketIntake.FirstAsync(s => s.TenantId == tid)).MsRefreshTokenEnc == ""; }, 15000);
    await using (var db = new PlatformDb(options)) row = await db.TicketIntake.FirstAsync(s => s.TenantId == tid);
    Check("the dead token is DELETED from our server", row.MsRefreshTokenEnc == "", row.MsRefreshTokenEnc.Length > 0 ? "still there" : "");
    Check("checking has stopped, the reason in plain words", row.Paused && row.LastProblem.Contains("password was changed") && row.LastProblem.Contains("Sign in with Microsoft"), row.LastProblem);
    Check("...and who it was for is kept, to say who to sign in as again", row.MsUser == Support);
    var refused = ms.Count("refresh_token");
    await Task.Delay(7000);
    Check("three timer ticks later Microsoft has not been asked again", ms.Count("refresh_token") == refused, $"{refused} then {ms.Count("refresh_token")} refreshes ({refused - beforeRefusal} since the refusal)");
    var stopped = Html(await owner.Get("/portal/mailintake"));
    Check("the status line says stopped, and to sign in again", stopped.Contains("stopped — Microsoft no longer accepts the sign-in; sign in again"));
    Check("the page says so beside the button, with the reason", stopped.Contains($"Microsoft no longer accepts the sign-in for {Support}") && stopped.Contains("password was changed"));
    Check("...and the button is Sign in with Microsoft again", stopped.Contains("Sign in with Microsoft again</button>"));
    Check("client updates are no longer offered — a reply could not come back", !ClientUpdateService.Ready(row));
    imap.Deliver(Support, Mime("Bob Brown <bob@acme.test>", Support, "While stopped", "Sent while the sign-in was refused."));
    await Task.Delay(5000);
    Check("mail sent meanwhile is not read", await WaitTicket(options, tid, "While stopped", 500) == null);

    Head("signing in again carries on from where it stopped");
    ms.Unrevoke(Support);
    var (_, q3) = await Start(owner);
    var back3 = await owner.Raw.GetAsync($"/portal/mailintake/microsoft?code={ms.Issue(Support, Tid, q3["nonce"], q3["code_challenge"], q3["redirect_uri"])}&state={Uri.EscapeDataString(q3["state"])}");
    Check("signed in again", back3.Headers.Location?.ToString() == "/portal/mailintake?ms=connected");
    await owner.Get("/portal/mailintake?ms=connected");
    var whileT = await WaitTicket(options, tid, "While stopped");
    Check("the email that arrived while stopped becomes a ticket — nothing lost, nothing older taken", whileT != null && await Count(options, tid) == 2, $"{await Count(options, tid)} tickets");

    Head("a shared mailbox: read through a person with Full Access, replies need Send as");
    var (_, q4) = await Start(owner);
    await owner.Raw.GetAsync($"/portal/mailintake/microsoft?code={ms.Issue(Admin, Tid, q4["nonce"], q4["code_challenge"], q4["redirect_uri"])}&state={Uri.EscapeDataString(q4["state"])}");
    var noAccess = Html(await owner.Get("/portal/mailintake?ms=connected"));
    Check("signed in as the administrator without Full Access: the test says Full Access is needed",
        noAccess.Contains($"would not let {Admin} read {Support}") && noAccess.Contains("Full Access"), Snip(noAccess, "would not let"));
    imap.FullAccess.Add((Admin, Support));
    var shared = Html(await owner.Post("/portal/mailintake?handler=Email", Form()));
    Check("given Full Access, it reads the support mailbox as the administrator", shared.Contains($"Reading: signed in with Microsoft as {Admin}, reading {Support}"), Snip(shared, "Reading:"));
    Check("...and the page says it is a shared mailbox", shared.Contains($"We read <strong>{Support}</strong> as {Admin}"));
    imap.Deliver(Support, Mime("Cara <cara@cust.test>", Support, "Shared box mail", "My email is down."));
    var caraT = await WaitTicket(options, tid, "Shared box mail");
    Check("its mail becomes a ticket", caraT != null);
    var fromOurs = caraT == null ? null : await smtp.WaitFor("cara@cust.test", $"[Ticket #{caraT.Number}]");
    InboundMail? caraLog; await using (var db = new PlatformDb(options)) caraLog = await db.InboundMail.FirstOrDefaultAsync(x => x.TenantId == tid && x.Subject == "Shared box mail");
    Check("without Send as, the reply still reaches her — from our mailbox, in the company's name", fromOurs != null && fromOurs.FromAddress == PlatformFrom && fromOurs.FromName == "M365 Co");
    Check("...and the page says why, with Send as", caraLog != null && caraLog.ReplyResult.Contains("sent from our mailbox") && caraLog.ReplyResult.Contains("Send as"), caraLog?.ReplyResult ?? "");
    smtp.SendAs.Add((Admin, Support));
    imap.Deliver(Support, Mime("Dan <dan@delta.test>", Support, "Shared box again", "x"));
    var danT = await WaitTicket(options, tid, "Shared box again");
    var fromThem = danT == null ? null : await smtp.WaitFor("dan@delta.test", $"[Ticket #{danT.Number}]");
    Check("with Send as, from the support address, signed in as the administrator", fromThem != null && fromThem.FromAddress == Support && fromThem.AuthUser == Admin && fromThem.Mechanism == "XOAUTH2");

    Head("Disconnect, and choosing another way");
    var disc = Html(await owner.Post("/portal/mailintake?handler=Disconnect", new()));
    Check("Disconnect says the sign-in is deleted, and how to remove the app from their organisation too", disc.Contains("is deleted from our server") && disc.Contains("Enterprise applications"));
    await using (var db = new PlatformDb(options)) row = await db.TicketIntake.FirstAsync(s => s.TenantId == tid);
    Check("...and it is: no token, no account", row.MsRefreshTokenEnc == "" && row.MsUser == "" && row.MsTenant == "");
    Check("the page asks for a sign-in again", disc.Contains("waiting for somebody to sign in with Microsoft") && disc.Contains("Nobody has signed in yet"));
    var afterDisc = ms.Count("refresh_token");
    imap.Deliver(Support, Mime("Eve <eve@else.test>", Support, "After disconnect", "x"));
    await Task.Delay(6000);
    Check("nothing more is asked of Microsoft, and nothing is read", ms.Count("refresh_token") == afterDisc && await WaitTicket(options, tid, "After disconnect", 300) == null);
    var (_, q5) = await Start(owner);
    await owner.Raw.GetAsync($"/portal/mailintake/microsoft?code={ms.Issue(Support, Tid, q5["nonce"], q5["code_challenge"], q5["redirect_uri"])}&state={Uri.EscapeDataString(q5["state"])}");
    await PostAndForget(owner, Form(way: "forward", enabled: false));
    await using (var db = new PlatformDb(options)) row = await db.TicketIntake.FirstAsync(s => s.TenantId == tid);
    Check("saving ANOTHER way in deletes the sign-in too — a live key is kept only while it is used", row.Way == "forward" && row.MsRefreshTokenEnc == "" && row.MsUser == "");

    Head("Microsoft's other answers at the way back");
    var declined = await owner.Raw.GetAsync("/portal/mailintake/microsoft?error=access_denied&error_description=AADSTS65004%3A+User+declined+to+consent&state=x");
    Check("declined: back with 'declined'", declined.Headers.Location?.ToString() == "/portal/mailintake?ms=declined");
    var adminNeeded = await owner.Raw.GetAsync("/portal/mailintake/microsoft?error=access_denied&error_description=AADSTS90094%3A+The+grant+requires+admin+permission.&state=x");
    Check("admin approval needed: back with 'admin'", adminNeeded.Headers.Location?.ToString() == "/portal/mailintake?ms=admin");
    Check("...and the page says to ask the administrator, with the approval link", Html(await owner.Get("/portal/mailintake?ms=admin")) is var ap && ap.Contains("only an administrator approve") && ap.Contains("adminconsent?client_id="));
    var approved = await owner.Raw.GetAsync("/portal/mailintake/microsoft?admin_consent=True&tenant=" + Tid + "&state=admin");
    Check("an administrator's approval: back with 'approved', and the page says sign in now", approved.Headers.Location?.ToString() == "/portal/mailintake?ms=approved"
        && Html(await owner.Get("/portal/mailintake?ms=approved")).Contains("Now press Sign in with Microsoft"));
    var words = await owner.Raw.GetAsync("/portal/mailintake?ms=" + new string('x', 200));
    Check("a long code in the address shows nothing (codes, not words)", words.StatusCode == HttpStatusCode.OK);

    return Done();
}
finally
{
    try { if (server is { HasExited: false }) server.Kill(true); } catch { }
    try { await Task.Delay(300); Directory.Delete(dataDir, true); } catch { }
}

int Done()
{
    Console.WriteLine();
    Console.WriteLine($"{pass} of {pass + fail} checks passed.");
    return fail == 0 ? 0 : 1;
}

// ---------------------------------------------------------------- small things

static string Html(string s) => WebUtility.HtmlDecode(s);
static Dictionary<string, string> Q(Uri u) =>
    u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
        .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "");
static string Snip(string html, string near)
{
    var i = html.IndexOf(near, StringComparison.OrdinalIgnoreCase);
    if (i < 0) return "(not found)";
    return Regex.Replace(Regex.Replace(html.Substring(i, Math.Min(240, html.Length - i)), "<[^>]+>", " "), "\\s+", " ").Trim();
}
static async Task PostAndForget(Browser b, Dictionary<string, string> f) => await b.Post("/portal/mailintake?handler=Email", f);
static async Task WaitUntil(Func<Task<bool>> ok, int ms)
{
    var until = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < until) { if (await ok()) return; await Task.Delay(300); }
}
static MimeMessage Mime(string from, string to, string subject, string text, string? mid = null)
{
    var m = new MimeMessage();
    m.From.Add(MailboxAddress.Parse(from)); m.To.Add(MailboxAddress.Parse(to));
    m.Subject = subject; m.MessageId = (mid ?? $"<{Guid.NewGuid():N}@sender.test>").Trim('<', '>'); m.Date = DateTimeOffset.Now;
    m.Body = new TextPart("plain") { Text = text };
    return m;
}
static async Task<Ticket?> WaitTicket(DbContextOptions<PlatformDb> o, int tenantId, string subject, int ms = 20000)
{
    var until = DateTime.UtcNow.AddMilliseconds(ms);
    while (true)
    {
        await using (var db = new PlatformDb(o))
        {
            var t = await db.Tickets.Where(x => x.TenantId == tenantId && x.Subject == subject).OrderBy(x => x.Id).FirstOrDefaultAsync();
            if (t != null) return t;
        }
        if (DateTime.UtcNow >= until) return null;
        await Task.Delay(300);
    }
}
static async Task<int> Count(DbContextOptions<PlatformDb> o, int tenantId) { await using var db = new PlatformDb(o); return await db.Tickets.CountAsync(x => x.TenantId == tenantId); }
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
// ---------------------------------------------------------------- a browser, signed in to the portal

/// <summary>One person's browser: one cookie jar, two ways of asking — following redirects as a person sees pages,
/// and not, to read where the platform sends them (to Microsoft, and back).</summary>
file sealed class Browser
{
    private readonly HttpClient _follow;
    public HttpClient Raw { get; }
    public List<string> SetCookie { get; } = new();
    private Browser(string baseUrl)
    {
        var jar = new CookieContainer();
        _follow = new HttpClient(new HttpClientHandler { CookieContainer = jar, AllowAutoRedirect = true }) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(60) };
        Raw = new HttpClient(new HttpClientHandler { CookieContainer = jar, AllowAutoRedirect = false }) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(60) };
    }
    public static async Task<Browser> SignInAsync(string baseUrl, string email, string pw)
    {
        var b = new Browser(baseUrl);
        await b.Post("/login", new() { ["Email"] = email, ["Password"] = pw });
        return b;
    }
    public Task<string> Get(string path) => _follow.GetStringAsync(path);
    public async Task<string> Post(string path, Dictionary<string, string> fields) => await (await Post(path, fields, true)).Content.ReadAsStringAsync();
    public async Task<HttpResponseMessage> Post(string path, Dictionary<string, string> fields, bool follow)
    {
        // The page is fetched WITHOUT its handler, and its own form's token taken (§80).
        var page = await _follow.GetStringAsync(Regex.Replace(path, @"[?&]handler=[^&]*", ""));
        var m = Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
        if (!m.Success) m = Regex.Match(page, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"");
        fields = new(fields) { ["__RequestVerificationToken"] = m.Groups[1].Value };
        var r = await (follow ? _follow : Raw).PostAsync(path, new FormUrlEncodedContent(fields));
        if (r.Headers.TryGetValues("Set-Cookie", out var sc)) SetCookie.AddRange(sc);
        return r;
    }
}

// ---------------------------------------------------------------- Microsoft's sign-in service

/// <summary>
/// Microsoft's token endpoint, as much of it as the platform uses: an authorization code redeemed (checking our app's
/// id and secret, the redirect URI and the PKCE verifier against the challenge), and a refresh token refreshed — with a
/// NEW refresh token each time, as Microsoft does. Access tokens last a minute here, so every use of one refreshes.
/// Revoke() is the password being changed; RejectClient is our secret running out.
/// </summary>
file sealed class FakeMicrosoft : IDisposable
{
    private TcpListener? _l;
    private readonly string _clientId, _secret;
    private readonly Dictionary<string, (string user, string tid, string nonce, string challenge, string redirect)> _codes = new();
    private readonly Dictionary<string, (string user, string tid)> _refresh = new();
    private readonly Dictionary<string, (string user, DateTime until)> _access = new();
    private readonly Dictionary<string, string> _revoked = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string grant, string dir)> _requests = new();
    private int _n;
    public int Port { get; private set; }
    public bool RejectClient { get; set; }
    public bool LastCodeVerified { get; private set; }
    public string LastRefreshScope { get; private set; } = "";
    /// <summary>Every refresh token presented, in order — the stored one must move on each time.</summary>
    public List<string> RotatedFrom { get; } = new();

    public FakeMicrosoft(string clientId, string secret) { _clientId = clientId; _secret = secret; }
    public void Start()
    {
        _l = new TcpListener(IPAddress.Loopback, 0); _l.Start();
        Port = ((IPEndPoint)_l.LocalEndpoint).Port;
        _ = Task.Run(async () => { while (true) { TcpClient c; try { c = await _l.AcceptTcpClientAsync(); } catch { return; } _ = Handle(c); } });
    }

    /// <summary>The person at Microsoft's page signs in as <paramref name="user"/> and agrees: a code for the way back.</summary>
    public string Issue(string user, string tid, string nonce, string challenge, string redirect)
    {
        lock (this) { var code = "CODE-" + (++_n); _codes[code] = (user, tid, nonce, challenge, redirect); return code; }
    }
    public void Revoke(string user, string why) { lock (this) _revoked[user] = why; }
    public void Unrevoke(string user) { lock (this) _revoked.Remove(user); }
    public int Count(string grant) { lock (this) return _requests.Count(r => r.grant == grant); }
    public List<string> Directories(string grant) { lock (this) return _requests.Where(r => r.grant == grant).Select(r => r.dir).ToList(); }
    public List<string> AllTokens() { lock (this) return _refresh.Keys.Concat(_access.Keys).ToList(); }
    /// <summary>The account a still-good access token was given to, or null.</summary>
    public string? Owner(string token) { lock (this) return _access.TryGetValue(token, out var a) && a.until > DateTime.UtcNow && !_revoked.ContainsKey(a.user) ? a.user : null; }

    public static string Jwt(Dictionary<string, string> claims) =>
        MicrosoftSignIn.Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"none\",\"typ\":\"JWT\"}")) + "." + MicrosoftSignIn.Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims)) + ".sig";

    private async Task Handle(TcpClient c)
    {
        using (c)
        try
        {
            var s = c.GetStream();
            var request = await ReadLineStatic(s);
            int length = 0;
            while (await ReadLineStatic(s) is { Length: > 0 } h)
                if (h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(h[15..].Trim());
            var body = new byte[length]; var got = 0;
            while (got < length) { var n = await s.ReadAsync(body.AsMemory(got)); if (n == 0) break; got += n; }
            var form = Encoding.UTF8.GetString(body).Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
                .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "");
            var path = request.Split(' ').ElementAtOrDefault(1) ?? "";
            var (status, json) = Answer(path, form);
            var bytes = Encoding.UTF8.GetBytes(json);
            await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));
            await s.WriteAsync(bytes);
        }
        catch { }
    }

    private (int, string) Answer(string path, Dictionary<string, string> f)
    {
        var m = Regex.Match(path, "^/([^/]+)/oauth2/v2.0/token$");
        if (!m.Success) return (404, "{\"error\":\"not_found\"}");
        static string Err(string e, string d) => JsonSerializer.Serialize(new { error = e, error_description = d });
        lock (this)
        {
            var grant = f.GetValueOrDefault("grant_type") ?? "";
            _requests.Add((grant, Uri.UnescapeDataString(m.Groups[1].Value)));
            if (RejectClient || f.GetValueOrDefault("client_id") != _clientId || f.GetValueOrDefault("client_secret") != _secret)
                return (401, Err("invalid_client", "AADSTS7000215: Invalid client secret provided. Trace ID: x"));
            (string user, string tid) who;
            string idToken = "";
            if (grant == "authorization_code")
            {
                var code = f.GetValueOrDefault("code") ?? "";
                if (!_codes.Remove(code, out var c)) return (400, Err("invalid_grant", "AADSTS54005: OAuth2 Authorization code was already redeemed."));
                var verifier = f.GetValueOrDefault("code_verifier") ?? "";
                LastCodeVerified = MicrosoftSignIn.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) == c.challenge && f.GetValueOrDefault("redirect_uri") == c.redirect;
                if (!LastCodeVerified) return (400, Err("invalid_grant", "AADSTS501481: The Code_Verifier does not match the code_challenge supplied in the authorization request."));
                who = (c.user, c.tid);
                idToken = Jwt(new() { ["preferred_username"] = c.user, ["email"] = c.user, ["tid"] = c.tid, ["nonce"] = c.nonce, ["name"] = "Support" });
            }
            else if (grant == "refresh_token")
            {
                var rt = f.GetValueOrDefault("refresh_token") ?? "";
                RotatedFrom.Add(rt);
                LastRefreshScope = f.GetValueOrDefault("scope") ?? "";
                if (!_refresh.TryGetValue(rt, out who)) return (400, Err("invalid_grant", "AADSTS9002313: Invalid request."));
                if (_revoked.TryGetValue(who.user, out var why)) return (400, Err("invalid_grant", why + " Trace ID: x"));
            }
            else return (400, Err("unsupported_grant_type", "x"));
            var access = $"AT-{++_n}-{Guid.NewGuid():N}";
            var refresh = $"RT-{_refresh.Count + 1}";
            _access[access] = (who.user, DateTime.UtcNow.AddSeconds(60));
            _refresh[refresh] = who;
            var answer = new Dictionary<string, object> { ["token_type"] = "Bearer", ["expires_in"] = 60, ["access_token"] = access, ["refresh_token"] = refresh, ["scope"] = f.GetValueOrDefault("scope") ?? "" };
            if (idToken.Length > 0) answer["id_token"] = idToken;
            return (200, JsonSerializer.Serialize(answer));
        }
    }

    private static async Task<string> ReadLineStatic(Stream s)
    {
        var sb = new StringBuilder(); var one = new byte[1];
        while (true)
        {
            var n = await s.ReadAsync(one);
            if (n == 0 || one[0] == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append((char)one[0]);
        }
    }
    public void Dispose() { try { _l?.Stop(); } catch { } }
}

// ---------------------------------------------------------------- Microsoft 365's IMAP

/// <summary>
/// mailintakecheck's fake IMAP server, taught Microsoft 365's sign-in: AUTHENTICATE XOAUTH2 with a token the fake
/// Microsoft gave out, for the mailbox named in it — the token's own account, or a mailbox it has Full Access to —
/// unless IMAP is switched off for that mailbox. LOGIN (a password) is always refused and counted. Every command is
/// recorded by mailbox.
/// </summary>
file sealed class FakeImap : IDisposable
{
    private readonly FakeMicrosoft _ms;
    private TcpListener? _l;
    private readonly Dictionary<string, List<(uint uid, byte[] raw, MimeMessage m)>> _boxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, uint> _next = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _mechs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ImapOff { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<(string who, string box)> FullAccess { get; } = new();
    public int PasswordLogins { get; private set; }
    public bool PeekOnly { get; private set; } = true;
    public int Port { get; private set; }
    public FakeImap(FakeMicrosoft ms) => _ms = ms;

    public void AddMailbox(string box) { lock (this) { _boxes[box] = new(); _next[box] = 1; _commands[box] = new(); _mechs[box] = new(); } }
    public void Deliver(string box, MimeMessage m) { using var s = new MemoryStream(); m.WriteTo(s); lock (this) { var u = _next[box]++; _boxes[box].Add((u, s.ToArray(), m)); } }
    public List<string> Commands(string box) { lock (this) return _commands[box].ToList(); }
    public List<string> Mechanisms(string box) { lock (this) return _mechs[box].ToList(); }
    public int Attempts(string box) { lock (this) return _attempts.TryGetValue(box, out var n) ? n : 0; }

    public void Start()
    {
        _l = new TcpListener(IPAddress.Loopback, 0); _l.Start();
        Port = ((IPEndPoint)_l.LocalEndpoint).Port;
        _ = Task.Run(async () => { while (true) { TcpClient c; try { c = await _l.AcceptTcpClientAsync(); } catch { return; } _ = Handle(c); } });
    }

    private async Task Handle(TcpClient c)
    {
        using (c)
        try
        {
            var s = c.GetStream();
            async Task Send(string line) => await s.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"));
            await Send("* OK [CAPABILITY IMAP4rev1 AUTH=XOAUTH2 SASL-IR] fake Microsoft 365 IMAP ready");
            string? box = null;
            while (await ReadLineStatic(s) is { } line && line != "\u0000EOF")
            {
                var sp = line.IndexOf(' ');
                if (sp < 0) continue;
                var tag = line[..sp]; var rest = line[(sp + 1)..];
                var words = rest.Split(' ', 3);
                var verb = words[0].ToUpperInvariant();
                if (verb == "UID" && words.Length > 1) verb = "UID " + words[1].ToUpperInvariant();
                if (box != null) lock (this) _commands[box].Add(verb);
                switch (verb)
                {
                    case "CAPABILITY": await Send("* CAPABILITY IMAP4rev1 AUTH=XOAUTH2 SASL-IR"); await Send($"{tag} OK CAPABILITY completed"); break;
                    case "LOGIN": lock (this) PasswordLogins++; await Send($"{tag} NO LOGIN failed."); break;
                    case "AUTHENTICATE":
                    {
                        var mech = words.ElementAtOrDefault(1)?.ToUpperInvariant() ?? "";
                        var initial = words.ElementAtOrDefault(2) ?? "";
                        if (initial.Length == 0) { await Send("+ "); initial = (await ReadLineStatic(s)).Trim(); }
                        var (user, token) = XOAuth2(initial);
                        lock (this) { if (_mechs.ContainsKey(user)) _mechs[user].Add(mech); _attempts[user] = (_attempts.TryGetValue(user, out var n) ? n : 0) + 1; }
                        var owner = _ms.Owner(token);
                        bool ok; lock (this) ok = mech == "XOAUTH2" && owner != null && _boxes.ContainsKey(user) && !ImapOff.Contains(user)
                                               && (owner.Equals(user, StringComparison.OrdinalIgnoreCase) || FullAccess.Contains((owner, user)));
                        if (ok) { box = user; await Send($"{tag} OK AUTHENTICATE completed."); }
                        else await Send($"{tag} NO AUTHENTICATE failed.");   // what Microsoft 365 says, whatever the reason
                        break;
                    }
                    case "LIST": case "LSUB": await Send($"* {verb} (\\HasNoChildren) \"/\" INBOX"); await Send($"{tag} OK {verb} completed"); break;
                    case "EXAMINE": case "SELECT":
                    {
                        if (box == null) { await Send($"{tag} NO not signed in"); break; }
                        int count; uint next; lock (this) { count = _boxes[box].Count; next = _next[box]; }
                        await Send($"* {count} EXISTS"); await Send("* 0 RECENT");
                        await Send("* OK [UIDVALIDITY 365] UIDs valid"); await Send($"* OK [UIDNEXT {next}] Predicted next UID");
                        await Send($"{tag} OK [{(verb == "EXAMINE" ? "READ-ONLY" : "READ-WRITE")}] {verb} completed");
                        break;
                    }
                    case "UID SEARCH":
                    {
                        List<uint> uids; lock (this) uids = _boxes[box!].Select(x => x.uid).ToList();
                        var m = Regex.Match(rest, @"UID\s+([0-9:*,]+)\s*$", RegexOptions.IgnoreCase);
                        await Send("* SEARCH" + string.Concat((m.Success ? InSet(m.Groups[1].Value, uids) : uids).Select(u => " " + u)));
                        await Send($"{tag} OK SEARCH completed");
                        break;
                    }
                    case "UID FETCH":
                    {
                        var parts = rest.Split(' ', 4);
                        List<(uint uid, byte[] raw, MimeMessage m)> list; lock (this) list = _boxes[box!].ToList();
                        var want = InSet(parts[2], list.Select(x => x.uid).ToList()).ToHashSet();
                        var items = parts.Length > 3 ? parts[3].ToUpperInvariant() : "";
                        if (items.Contains("BODY[]") && !items.Contains("BODY.PEEK[]")) PeekOnly = false;
                        for (int i = 0; i < list.Count; i++)
                        {
                            if (!want.Contains(list[i].uid)) continue;
                            var (uid, raw, msg) = list[i];
                            if (items.Contains("BODY.PEEK[]") || items.Contains("BODY[]"))
                            {
                                await Send($"* {i + 1} FETCH (UID {uid} BODY[] {{{raw.Length}}}"); await s.WriteAsync(raw); await Send(")");
                            }
                            else
                            {
                                var from = msg.From.Mailboxes.First(); var at = from.Address.LastIndexOf('@');
                                await Send($"* {i + 1} FETCH (UID {uid} RFC822.SIZE {raw.Length} ENVELOPE (NIL {Qs(msg.Subject)} (({Qs(from.Name)} NIL {Qs(from.Address[..at])} {Qs(from.Address[(at + 1)..])})) NIL NIL NIL NIL NIL NIL {Qs("<" + msg.MessageId + ">")}))");
                            }
                        }
                        await Send($"{tag} OK FETCH completed");
                        break;
                    }
                    case "LOGOUT": await Send("* BYE"); await Send($"{tag} OK LOGOUT completed"); return;
                    default: await Send($"{tag} OK {verb} done"); break;
                }
            }
        }
        catch { }
    }

    /// <summary>"user=X^Aauth=Bearer T^A^A", base64.</summary>
    public static (string user, string token) XOAuth2(string b64)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split('\u0001');
            var user = parts.FirstOrDefault(p => p.StartsWith("user="))?[5..] ?? "";
            var token = parts.FirstOrDefault(p => p.StartsWith("auth=Bearer "))?[12..] ?? "";
            return (user, token);
        }
        catch { return ("", ""); }
    }
    private static string Qs(string? s) => s == null || s.Any(ch => ch > 126 || ch < 32) ? "NIL" : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    private static List<uint> InSet(string set, List<uint> uids)
    {
        if (uids.Count == 0) return new();
        var max = uids.Max(); var r = new List<uint>();
        foreach (var part in set.Split(','))
        {
            var ab = part.Split(':');
            uint P(string x) => x == "*" ? max : uint.Parse(x) == uint.MaxValue ? max : uint.Parse(x);
            var a = P(ab[0]); var b = ab.Length > 1 ? P(ab[1]) : a;
            if (a > b) (a, b) = (b, a);
            r.AddRange(uids.Where(u => u >= a && u <= b));
        }
        return r.Distinct().OrderBy(x => x).ToList();
    }
    private static async Task<string> ReadLineStatic(Stream s)
    {
        var sb = new StringBuilder(); var one = new byte[1];
        while (true)
        {
            var n = await s.ReadAsync(one);
            if (n == 0) return sb.Length > 0 ? sb.ToString() : "\u0000EOF";
            if (one[0] == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append((char)one[0]);
        }
    }
    public void Dispose() { try { _l?.Stop(); } catch { } }
}

// ---------------------------------------------------------------- Microsoft 365's SMTP

/// <summary>
/// mailintakecheck's fake SMTP server, taught Microsoft 365: AUTH XOAUTH2 with a token the fake Microsoft gave out, for
/// the account the token is for. Sending as another address needs Send as on it, or the message is refused at the end
/// of DATA with SendAsDenied, as Exchange Online does. With no AUTH at all it is the platform's own mailbox and takes anything.
/// </summary>
file sealed class FakeSmtp : IDisposable
{
    private readonly FakeMicrosoft _ms;
    private TcpListener? _l;
    private readonly List<Caught> _mails = new();
    public HashSet<(string who, string box)> SendAs { get; } = new();
    public int Port { get; private set; }
    public FakeSmtp(FakeMicrosoft ms) => _ms = ms;

    public void Start()
    {
        _l = new TcpListener(IPAddress.Loopback, 0); _l.Start();
        Port = ((IPEndPoint)_l.LocalEndpoint).Port;
        _ = Task.Run(async () => { while (true) { TcpClient c; try { c = await _l.AcceptTcpClientAsync(); } catch { return; } _ = Handle(c); } });
    }

    private async Task Handle(TcpClient c)
    {
        using (c)
        try
        {
            var s = c.GetStream();
            var r = new StreamReader(s, Encoding.UTF8);
            var w = new StreamWriter(s, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
            await w.WriteLineAsync("220 fake Microsoft 365 ESMTP");
            var to = new List<string>(); string authed = "", mech = "", from = "";
            while (await r.ReadLineAsync() is { } line)
            {
                var cmd = line.ToUpperInvariant();
                if (cmd.StartsWith("EHLO")) { await w.WriteLineAsync("250-fake"); await w.WriteLineAsync("250-8BITMIME"); await w.WriteLineAsync("250 AUTH LOGIN XOAUTH2"); }
                else if (cmd.StartsWith("AUTH XOAUTH2"))
                {
                    var arg = line.Length > 12 ? line[12..].Trim() : "";
                    if (arg.Length == 0) { await w.WriteLineAsync("334 "); arg = (await r.ReadLineAsync() ?? "").Trim(); }
                    var (user, token) = FakeImap.XOAuth2(arg);
                    var owner = _ms.Owner(token);
                    if (owner != null && owner.Equals(user, StringComparison.OrdinalIgnoreCase)) { authed = owner; mech = "XOAUTH2"; await w.WriteLineAsync("235 2.7.0 Authentication successful"); }
                    else await w.WriteLineAsync("535 5.7.3 Authentication unsuccessful");
                }
                else if (cmd.StartsWith("AUTH")) await w.WriteLineAsync("535 5.7.139 Authentication unsuccessful, basic authentication is disabled.");
                else if (cmd.StartsWith("MAIL FROM:")) { from = Regex.Match(line, "<([^>]*)>").Groups[1].Value.ToLowerInvariant(); await w.WriteLineAsync("250 OK"); }
                else if (cmd.StartsWith("RCPT TO:")) { to.Add(Regex.Match(line, "<([^>]+)>").Groups[1].Value.ToLowerInvariant()); await w.WriteLineAsync("250 OK"); }
                else if (cmd == "DATA")
                {
                    await w.WriteLineAsync("354 go ahead");
                    var sb = new StringBuilder();
                    while (await r.ReadLineAsync() is { } d && d != ".") sb.Append(d.StartsWith("..") ? d[1..] : d).Append("\r\n");
                    bool denied; lock (this) denied = authed.Length > 0 && !from.Equals(authed, StringComparison.OrdinalIgnoreCase) && !SendAs.Contains((authed, from));
                    if (denied) await w.WriteLineAsync($"554 5.2.252 SendAsDenied; {authed} not allowed to send as {from}");
                    else
                    {
                        lock (_mails) _mails.Add(new Caught(to.ToList(), authed, mech, MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString())))));
                        await w.WriteLineAsync("250 queued");
                    }
                    to.Clear();
                }
                else if (cmd == "QUIT") { await w.WriteLineAsync("221 bye"); break; }
                else await w.WriteLineAsync("250 OK");
            }
        }
        catch { }
    }

    public async Task<Caught?> WaitFor(string to, string subjectPart, int ms = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            lock (_mails) { var m = _mails.LastOrDefault(x => x.To.Contains(to.ToLowerInvariant()) && x.Subject.Contains(subjectPart)); if (m != null) return m; }
            await Task.Delay(150);
        }
        return null;
    }
    public void Dispose() { try { _l?.Stop(); } catch { } }
}

file sealed class Caught
{
    public List<string> To { get; }
    public string AuthUser { get; }
    public string Mechanism { get; }
    private readonly MimeMessage _m;
    public Caught(List<string> to, string auth, string mech, MimeMessage m) { To = to; AuthUser = auth; Mechanism = mech; _m = m; }
    public string Subject => _m.Subject ?? "";
    public string FromAddress => _m.From.Mailboxes.FirstOrDefault()?.Address ?? "";
    public string FromName => _m.From.Mailboxes.FirstOrDefault()?.Name ?? "";
}
