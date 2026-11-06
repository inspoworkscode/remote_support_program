using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Tickets by email and by a contact form (3.8.4, 2026-10-05). Clint: "someone sends an email to
/// support@anywhereit.co.za, it automatically creates a ticket using the email address, subject and
/// body ... check the email address against the client database, and automatically link to the
/// client if the email address exists. The same would work from the contact form. The system should
/// then auto reply to the email address given with the ticket reference number."
///
/// His answers, the same day: it must work with ANY mail host, explaining what can go wrong; an
/// unknown sender's ticket opens with NO client; and when the client is known but the person is not,
/// the ticket stays unlinked and SAYS so, suggesting the person be added as a contact first so that
/// it links correctly. Microsoft 365's own sign-in, "later" then, is the third way in since
/// 2026-10-19 (<see cref="MicrosoftSignIn"/>, HANDOVER §151).
///
/// Every rule is here. <see cref="MailWire"/> is how mail is read and sent; <c>TicketIntakeSweep</c>
/// only calls <see cref="CheckMailboxAsync"/> and <see cref="CheckForwardingAsync"/> on a timer; the
/// portal page and the form only call in. The ticket page asks <see cref="MatchAsync"/> every time it
/// is drawn, so adding a contact links a waiting ticket without anything being redone.
/// </summary>
public sealed class TicketIntake
{
    /// <summary>Tickets one address may open in an hour. A person does not write six times an hour;
    /// a mail loop or a compromised account does.</summary>
    public const int PerSenderPerHour = 5;
    /// <summary>Tickets one company may receive in an hour, whatever the senders: a flood stops here.</summary>
    public const int PerCompanyPerHour = 200;
    /// <summary>Messages fetched in one look. The rest wait a minute.</summary>
    public const int PerCheck = 50;
    public const int FormTextMax = 5000;

    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly TicketService _tickets;
    private readonly ContactService _contacts;
    private readonly PlatformMailService _platformMail;
    private readonly TenantService _tenants;
    private readonly IDataProtector _protector;
    private readonly MicrosoftSignIn _ms;
    private readonly ILogger<TicketIntake> _log;
    private readonly string _publicUrl;

    public TicketIntake(IDbContextFactory<PlatformDb> dbf, TicketService tickets, ContactService contacts,
        PlatformMailService platformMail, TenantService tenants, IDataProtectionProvider dp, IConfiguration cfg, MicrosoftSignIn microsoft, ILogger<TicketIntake> log)
    {
        _dbf = dbf; _tickets = tickets; _contacts = contacts; _platformMail = platformMail; _tenants = tenants; _ms = microsoft; _log = log;
        // Its own purpose string: a mailbox password must not be readable by anything holding the
        // health check's protector, nor the other way round.
        _protector = dp.CreateProtector("RemoteSupport.TicketIntakePassword.v1");
        _publicUrl = (cfg["Platform:PublicUrl"] ?? "").TrimEnd('/');
    }

    /// <summary>A ticket was opened, or a customer's reply added to one — the consoles' lists hear it at once.</summary>
    public event Action<int, int>? TicketMade;

    /// <summary>One look at a mailbox at a time, so the timer and the Test button never both make a ticket of one message.</summary>
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> Gates = new();
    private static SemaphoreSlim Gate(int key) => Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    private const int ForwardingGate = -1;

    // ================================================================ settings

    /// <summary>The company's settings, or the defaults (not saved) when it has never saved any.</summary>
    public async Task<TicketIntakeSettings> GetAsync(int tenantId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.TicketIntake.FirstOrDefaultAsync(s => s.TenantId == tenantId) ?? new TicketIntakeSettings { TenantId = tenantId };
    }

    public async Task SaveAsync(int tenantId, Action<TicketIntakeSettings> edit)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var s = await db.TicketIntake.FirstOrDefaultAsync(x => x.TenantId == tenantId);
        if (s == null) { s = new TicketIntakeSettings { TenantId = tenantId }; db.TicketIntake.Add(s); }
        edit(s);
        s.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public string Protect(string plain) => string.IsNullOrEmpty(plain) ? "" : _protector.Protect(plain);
    private string? Unprotect(string cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        try { return _protector.Unprotect(cipher); } catch { return null; }
    }

    /// <summary>The mailbox to read, or null when its password can no longer be read (data/keys moved, §10).</summary>
    public MailServer? ImapOf(TicketIntakeSettings s) =>
        Unprotect(s.ImapPasswordEnc) is { } pw ? new MailServer(s.ImapHost.Trim(), s.ImapPort, s.ImapSecurity, s.ImapUser.Trim(), pw) : null;

    /// <summary>The company's own sending server — the reading login on the reading server unless they said otherwise.</summary>
    public MailServer? SmtpOf(TicketIntakeSettings s)
    {
        if (s.SmtpSameLogin && s.Way == "mailbox")
            return Unprotect(s.ImapPasswordEnc) is { } pw
                ? new MailServer(s.SmtpHost.Trim().Length > 0 ? s.SmtpHost.Trim() : s.ImapHost.Trim(), s.SmtpPort, s.SmtpSecurity, s.ImapUser.Trim(), pw) : null;
        return Unprotect(s.SmtpPasswordEnc) is { } p2 ? new MailServer(s.SmtpHost.Trim(), s.SmtpPort, s.SmtpSecurity, s.SmtpUser.Trim(), p2) : null;
    }

    // ================================================================ Sign in with Microsoft (2026-10-19, §151)

    /// <summary>We read this company's mailbox ourselves: with a password, or with a Microsoft sign-in still in hand.</summary>
    public static bool ReadsMailbox(TicketIntakeSettings s) => s.Way == "mailbox" || (s.Way == "microsoft" && MicrosoftConnected(s));
    public static bool MicrosoftConnected(TicketIntakeSettings s) => s.MsRefreshTokenEnc.Length > 0;

    /// <summary>
    /// The mailbox to read, and how to sign in to it — or what is wrong, and whether checking must STOP (nothing will
    /// change until a person does something) rather than try again later.
    /// </summary>
    private async Task<(MailServer? server, string problem, bool stop)> ReaderAsync(TicketIntakeSettings s, CancellationToken ct)
    {
        if (s.Way == "microsoft")
        {
            var token = await MicrosoftTokenAsync(s, ct);
            if (!token.Ok) return (null, token.Words, token.Failure == MsFailure.Refused);
            // The mailbox is the support address; the token is whoever signed in. They differ for a shared mailbox.
            return (new MailServer(_ms.ImapHost, _ms.ImapPort, "ssl", s.Address, "") { AccessToken = token.AccessToken, SignedInAs = s.MsUser }, "", false);
        }
        if (ImapOf(s) is not { } imap) return (null, "The saved password can no longer be read. Type it in again and save.", true);
        if (imap.Host.Length == 0 || imap.User.Length == 0) return (null, "Fill in the mail server, the username and the password first.", true);
        return (imap, "", false);
    }

    /// <summary>The company's own way of sending — their server with a password, or Microsoft 365 as the account that
    /// signed in — or null and why not (the caller sends from ours instead).</summary>
    private async Task<(MailServer? server, string problem)> SenderAsync(TicketIntakeSettings s, CancellationToken ct)
    {
        if (s.Way != "microsoft") return (SmtpOf(s), "");
        var token = await MicrosoftTokenAsync(s, ct);
        if (!token.Ok) return (null, token.Words);
        var who = s.MsUser.Length > 0 ? s.MsUser : s.Address;
        return (new MailServer(_ms.SmtpHost, _ms.SmtpPort, "starttls", who, "") { AccessToken = token.AccessToken, SignedInAs = who }, "");
    }

    /// <summary>
    /// A token for the company's Microsoft sign-in. Microsoft hands out a new refresh token with each one, which is
    /// stored. <b>A refusal is tried once</b>: Microsoft saying the sign-in has ended (password changed, access removed,
    /// permission withdrawn) is not mended by asking again every minute, so the dead token is DELETED and checking
    /// stops, and the page and its status line say so and offer Sign in again — the rule a refused password follows (§79).
    /// </summary>
    private async Task<MsAnswer> MicrosoftTokenAsync(TicketIntakeSettings s, CancellationToken ct)
    {
        var used = s.MsRefreshTokenEnc;
        var a = await _ms.AccessTokenAsync(s.TenantId, used, s.MsTenant, ct);
        if (a.Ok && a.RefreshEnc != used)
        {
            // Only over the one it came from: a new sign-in made meanwhile must not be overwritten by the old one's heir.
            await SaveAsync(s.TenantId, x => { if (x.MsRefreshTokenEnc == used) x.MsRefreshTokenEnc = a.RefreshEnc; });
            s.MsRefreshTokenEnc = a.RefreshEnc;
        }
        else if (a.Failure == MsFailure.Refused && used.Length > 0)
        {
            _log.LogInformation("[{Tenant}] Microsoft refused the sign-in for {User}: {Why}", s.TenantId, s.MsUser, a.Words);
            MicrosoftSignIn.Forget(s.TenantId);
            await SaveAsync(s.TenantId, x =>
            {
                if (x.MsRefreshTokenEnc != used) return;   // somebody signed in again meanwhile: theirs stands
                x.MsRefreshTokenEnc = ""; x.Paused = true; x.LastProblem = a.Words; x.LastCheckUtc = DateTime.UtcNow;
            });
        }
        else if (a.Failure == MsFailure.Ours)
            _log.LogWarning("[{Tenant}] our Microsoft app was refused — check `microsoft-app show` (the secret may have run out): {Why}", s.TenantId, a.Words);
        return a;
    }

    /// <summary>A sign-in that came back from Microsoft, kept. It switches email on: pressing the button said so.</summary>
    public async Task ConnectMicrosoftAsync(int tenantId, MsAnswer a, string by)
    {
        MicrosoftSignIn.Forget(tenantId);
        await SaveAsync(tenantId, x =>
        {
            // Another way in (or another account's view of the mailbox) starts again from "now", as another mailbox does.
            if (x.Way != "microsoft" || !x.MsUser.Equals(a.User, StringComparison.OrdinalIgnoreCase)) { x.UidValidity = 0; x.LastUid = 0; }
            x.Way = "microsoft"; x.EmailEnabled = true;
            x.MsRefreshTokenEnc = a.RefreshEnc; x.MsUser = a.User; x.MsTenant = a.Tenant;
            x.MsConnectedUtc = DateTime.UtcNow; x.MsConnectedBy = by;
            x.Paused = false; x.Failures = 0; x.NextTryUtc = null; x.LastProblem = "";
        });
    }

    /// <summary>Disconnect: the token is deleted from our server. Nothing is read or sent through Microsoft again until
    /// somebody signs in.</summary>
    public async Task DisconnectMicrosoftAsync(int tenantId)
    {
        MicrosoftSignIn.Forget(tenantId);
        await SaveAsync(tenantId, x =>
        {
            x.MsRefreshTokenEnc = ""; x.MsUser = ""; x.MsTenant = ""; x.MsConnectedUtc = null; x.MsConnectedBy = "";
            x.Paused = false; x.Failures = 0; x.NextTryUtc = null; x.LastProblem = "";
        });
    }

    /// <summary>What is wrong with an address a company wants to take mail at, or empty.</summary>
    public async Task<string> CheckAddressAsync(int tenantId, string address)
    {
        address = (address ?? "").Trim().ToLowerInvariant();
        if (address.Length == 0) return "Give the address your customers write to — support@yourcompany.co.za.";
        if (!LooksLikeEmail(address)) return "That address does not look right.";
        var platform = await _platformMail.GetAsync();
        if (Same(address, platform.IntakeAddress) || Same(address, platform.SmtpFrom) || Same(address, platform.SmtpUser))
            return "That is the platform's own address, not yours. Give the address YOUR customers write to.";
        await using var db = await _dbf.CreateDbContextAsync();
        if (await db.TicketIntake.AnyAsync(x => x.TenantId != tenantId && x.Address == address && x.AddressConfirmed))
            return "That address is already taking tickets for another company on this platform. If it is yours, contact us.";
        return "";
    }

    // ================================================================ testing a mailbox

    /// <summary>
    /// Signs in to everything the settings name and says, in words, what works and what does not.
    /// A good test also starts checking again after a refused password, and — for a mailbox never
    /// looked at — notes where it is up to, so mail from NOW on becomes tickets and nothing older does.
    /// </summary>
    public async Task<(bool ok, List<string> lines)> TestAsync(int tenantId, CancellationToken ct = default)
    {
        var s = await GetAsync(tenantId);
        var lines = new List<string>();
        bool ok = true;
        if (s.Way is "mailbox" or "microsoft")
        {
            if (s.Way == "microsoft" && !MicrosoftConnected(s))
                return (false, new() { s.MsUser.Length > 0 && s.LastProblem.Length > 0 ? s.LastProblem : "Nobody has signed in with Microsoft yet — press Sign in with Microsoft." });
            var (imap, problem, _) = await ReaderAsync(s, ct);
            if (imap == null) return (false, new() { "Reading: " + problem });
            var folder = s.Way == "microsoft" ? "INBOX" : s.Folder;
            await Gate(tenantId).WaitAsync(ct);
            try
            {
                var read = await MailWire.ReadNewAsync(imap, folder, s.UidValidity, s.LastUid, 0, ct);
                lines.Add(imap.AccessToken != null
                    ? $"Reading: signed in with Microsoft as {s.MsUser}{(s.MsUser.Equals(imap.User, StringComparison.OrdinalIgnoreCase) ? "" : $", reading {imap.User}")}. Its inbox holds {read.Count:N0} message{(read.Count == 1 ? "" : "s")}."
                    : $"Reading: signed in to {imap.Host} as {imap.User}. {(s.Folder.Length > 0 ? s.Folder : "INBOX")} holds {read.Count:N0} message{(read.Count == 1 ? "" : "s")}.");
                await SaveAsync(tenantId, x =>
                {
                    if (read.Baseline) { x.UidValidity = read.UidValidity; x.LastUid = read.LastUid; }
                    x.Paused = false; x.Failures = 0; x.NextTryUtc = null; x.LastProblem = ""; x.LastOkUtc = x.LastCheckUtc = DateTime.UtcNow;
                });
                if (read.Baseline) lines.Add("Mail already in it stays as it is — only mail arriving from now on becomes a ticket.");
            }
            catch (Exception ex)
            {
                var (words, login) = MailWire.Explain(ex, imap, false, s.Address);
                await SaveAsync(tenantId, x => { x.LastProblem = words; x.Paused = login; x.LastCheckUtc = DateTime.UtcNow; });
                return (false, new() { "Reading: " + words });
            }
            finally { Gate(tenantId).Release(); }
        }
        else
        {
            var p = await _platformMail.GetAsync();
            if (!p.ForwardingOffered) { lines.Add("Forwarding is not available on this server yet."); ok = false; }
            else if (!s.AddressConfirmed) { lines.Add($"Forwarding: {s.Address} is not confirmed yet — press the link in the email we sent to it."); ok = false; }
            else lines.Add($"Forwarding: mail sent to {s.Address} and forwarded to {p.IntakeAddress} becomes a ticket.");
        }

        if (s.AutoReply && s.SendVia == "own")
        {
            var (smtp, problem) = await SenderAsync(s, ct);
            if (problem.Length > 0) { ok = false; lines.Add("Sending: " + problem + " Until it works, the auto-reply comes from our mailbox with your name on it."); }
            else if (smtp == null || smtp.Host.Length == 0) { lines.Add("Sending: no sending server is filled in — the auto-reply will come from our mailbox instead."); }
            else
                try
                {
                    await MailWire.TestSendAsync(smtp, ct);
                    lines.Add(smtp.AccessToken != null
                        ? $"Sending: signed in to Microsoft 365 as {smtp.User} — the auto-reply will come from {s.Address}."
                        : $"Sending: signed in to {smtp.Host} — the auto-reply will come from {s.Address}.");
                }
                catch (Exception ex) { ok = false; lines.Add("Sending: " + MailWire.Explain(ex, smtp, true, s.Address).words + " Until it works, the auto-reply comes from our mailbox with your name on it."); }
        }
        return (ok, lines);
    }

    // ================================================================ checking, on the timer

    /// <summary>One look at a company's own mailbox. Called by the sweep each minute; never throws.</summary>
    public async Task CheckMailboxAsync(int tenantId, CancellationToken ct)
    {
        var t = await _tenants.ByIdAsync(tenantId);
        var s = await GetAsync(tenantId);
        if (t == null || !t.IsServiceEnabled || !s.EmailEnabled || !ReadsMailbox(s) || s.Paused || s.Id == 0) return;
        var (imap, problem, stop) = await ReaderAsync(s, ct);
        if (imap == null)
        {
            await SaveAsync(tenantId, x =>
            {
                x.LastProblem = problem; x.LastCheckUtc = DateTime.UtcNow;
                // A Microsoft refusal has already deleted the token it refused — unless somebody signed in again
                // meanwhile, whose sign-in is not stopped by the old one's refusal.
                if (stop && (x.Way != "microsoft" || !MicrosoftConnected(x))) x.Paused = true;
                else if (!stop) { x.Failures++; x.NextTryUtc = DateTime.UtcNow.AddMinutes(Math.Min(30, Math.Pow(2, Math.Min(x.Failures, 5)))); }
            });
            return;
        }
        if (!await Gate(tenantId).WaitAsync(0, ct)) return;
        try
        {
            var read = await MailWire.ReadNewAsync(imap, s.Way == "microsoft" ? "INBOX" : s.Folder, s.UidValidity, s.LastUid, PerCheck, ct);
            if (!read.Baseline)
            {
                foreach (var m in read.Mails) await ProcessAsync(t, s, m, "mailbox");
                foreach (var m in read.TooLarge) await LogAsync(t.Id, "mailbox", m, "skipped", "it is larger than 15 MB — read it in your mail");
            }
            await SaveAsync(tenantId, x =>
            {
                x.UidValidity = read.UidValidity; x.LastUid = read.LastUid;
                x.Failures = 0; x.NextTryUtc = null; x.LastProblem = ""; x.LastOkUtc = x.LastCheckUtc = DateTime.UtcNow;
            });
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var (words, login) = MailWire.Explain(ex, imap, false, s.Address);
            _log.LogInformation("[{Tenant}] mailbox check failed: {Why}", t.Slug, words);
            await SaveAsync(tenantId, x =>
            {
                x.LastProblem = words; x.Paused = login; x.Failures++; x.LastCheckUtc = DateTime.UtcNow;
                // 2, 4, 8 … minutes, never more than half an hour: a server that is down for the
                // afternoon is not asked sixty times an hour.
                x.NextTryUtc = DateTime.UtcNow.AddMinutes(Math.Min(30, Math.Pow(2, Math.Min(x.Failures, 5))));
            });
        }
        finally { Gate(tenantId).Release(); }
    }

    /// <summary>
    /// One look at OUR forwarding mailbox. Each message goes to the company whose CONFIRMED address it
    /// was sent to — read from To, Cc and the headers a forward leaves (Delivered-To, X-Original-To…).
    /// A Gmail forwarding confirmation is put on the page of the company it is for, because Gmail
    /// forwards nothing until somebody uses it and it arrives here, not in their mailbox.
    /// </summary>
    public async Task CheckForwardingAsync(CancellationToken ct)
    {
        var p = await _platformMail.GetAsync();
        if (!p.ForwardingOffered || p.IntakePaused) return;
        var pw = _platformMail.Unprotect(p.IntakeImapPasswordEnc);
        if (pw == null) { await _platformMail.SaveAsync(x => { x.IntakePaused = true; x.IntakeLastProblem = "The forwarding mailbox's password can no longer be read."; }); return; }
        var imap = new MailServer(p.IntakeImapHost, p.IntakeImapPort, p.IntakeImapSecurity, p.IntakeImapUser, pw);
        if (!await Gate(ForwardingGate).WaitAsync(0, ct)) return;
        try
        {
            var read = await MailWire.ReadNewAsync(imap, "INBOX", p.IntakeUidValidity, p.IntakeLastUid, PerCheck, ct);
            if (!read.Baseline)
                foreach (var m in read.Mails.Concat(read.TooLarge)) await RouteAsync(m, p, read.TooLarge.Contains(m));
            await _platformMail.SaveAsync(x =>
            {
                x.IntakeUidValidity = read.UidValidity; x.IntakeLastUid = read.LastUid;
                x.IntakeFailures = 0; x.IntakeNextTryUtc = null; x.IntakeLastProblem = ""; x.IntakeLastCheckUtc = DateTime.UtcNow;
            });
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var (words, login) = MailWire.Explain(ex, imap, false, p.IntakeAddress);
            _log.LogWarning("the forwarding mailbox check failed: {Why}", words);
            await _platformMail.SaveAsync(x =>
            {
                x.IntakeLastProblem = words; x.IntakePaused = login; x.IntakeFailures++; x.IntakeLastCheckUtc = DateTime.UtcNow;
                x.IntakeNextTryUtc = DateTime.UtcNow.AddMinutes(Math.Min(30, Math.Pow(2, Math.Min(x.IntakeFailures, 5))));
            });
        }
        finally { Gate(ForwardingGate).Release(); }
    }

    /// <summary>The platform's forwarding mailbox, signed in to and looked at, for the owner's Mail page.</summary>
    public async Task<(bool ok, string words)> TestForwardingAsync(CancellationToken ct = default)
    {
        var p = await _platformMail.GetAsync();
        if (!p.ForwardingOffered) return (false, "Fill in the forwarding address and its mail server first.");
        var pw = _platformMail.Unprotect(p.IntakeImapPasswordEnc);
        if (pw == null) return (false, "The saved password can no longer be read. Type it in again.");
        var imap = new MailServer(p.IntakeImapHost, p.IntakeImapPort, p.IntakeImapSecurity, p.IntakeImapUser, pw);
        await Gate(ForwardingGate).WaitAsync(ct);
        try
        {
            var read = await MailWire.ReadNewAsync(imap, "INBOX", p.IntakeUidValidity, p.IntakeLastUid, 0, ct);
            await _platformMail.SaveAsync(x =>
            {
                if (read.Baseline) { x.IntakeUidValidity = read.UidValidity; x.IntakeLastUid = read.LastUid; }
                x.IntakePaused = false; x.IntakeFailures = 0; x.IntakeNextTryUtc = null; x.IntakeLastProblem = ""; x.IntakeLastCheckUtc = DateTime.UtcNow;
            });
            return (true, $"Signed in to {imap.Host} as {imap.User}; the inbox holds {read.Count:N0} message{(read.Count == 1 ? "" : "s")}. Forwarded mail is checked every minute.");
        }
        catch (Exception ex)
        {
            var (words, login) = MailWire.Explain(ex, imap, false, p.IntakeAddress);
            await _platformMail.SaveAsync(x => { x.IntakeLastProblem = words; x.IntakePaused = login; });
            return (false, words);
        }
        finally { Gate(ForwardingGate).Release(); }
    }

    private async Task RouteAsync(IncomingMail m, PlatformSettings p, bool tooLarge)
    {
        List<TicketIntakeSettings> owners;
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            var to = m.Recipients;
            owners = await db.TicketIntake.Where(s => s.EmailEnabled && s.Way == "forward" && s.AddressConfirmed && to.Contains(s.Address)).ToListAsync();
        }
        if (owners.Count == 0)
        {
            if (m.FromAddress.StartsWith("forwarding-noreply@google.com", StringComparison.OrdinalIgnoreCase) && await GmailConfirmationAsync(m)) return;
            await LogAsync(0, "forward", m, "skipped",
                $"it was not sent to any company's confirmed address (it was for {string.Join(", ", m.Recipients.DefaultIfEmpty("nobody named"))})");
            return;
        }
        foreach (var s in owners)
        {
            var t = await _tenants.ByIdAsync(s.TenantId);
            if (t == null) continue;
            if (!t.IsServiceEnabled) { await LogAsync(t.Id, "forward", m, "skipped", "the company's service is not active"); continue; }
            if (tooLarge) { await LogAsync(t.Id, "forward", m, "skipped", "it is larger than 15 MB — read it in your mail"); continue; }
            await ProcessAsync(t, s, m, "forward");
        }
    }

    /// <summary>Gmail's "confirm forwarding" mail, put where the company will see it. True when it was theirs.</summary>
    private async Task<bool> GmailConfirmationAsync(IncomingMail m)
    {
        var text = m.Subject + "\n" + m.Text;
        await using var db = await _dbf.CreateDbContextAsync();
        var waiting = await db.TicketIntake.Where(s => s.Way == "forward" && s.Address != "").ToListAsync();
        var s = waiting.FirstOrDefault(x => text.Contains(x.Address, StringComparison.OrdinalIgnoreCase));
        if (s == null) return false;
        var code = Regex.Match(text, @"Confirmation code:\s*(\d+)").Groups[1].Value;
        var link = Regex.Match(text, @"https://mail(?:-settings)?\.google\.com/\S+").Value;
        s.GmailConfirmation = string.Join("\n", new[] { code.Length > 0 ? "Confirmation code: " + code : "", link }.Where(x => x.Length > 0));
        if (s.GmailConfirmation.Length == 0) s.GmailConfirmation = Clip(m.Text, 1500);
        s.GmailConfirmationUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await LogAsync(s.TenantId, "forward", m, "skipped", "Gmail's forwarding confirmation — it is shown at the top of this page");
        return true;
    }

    // ================================================================ one message

    /// <summary>
    /// What an email does: nothing (with the reason logged), a reply on an open ticket, or a new
    /// ticket with an auto-reply. Public so a harness can feed a message without a mail server.
    /// </summary>
    public async Task<InboundMail> ProcessAsync(Tenant t, TicketIntakeSettings s, IncomingMail m, string channel)
    {
        var messageId = m.MessageId.Length > 0 ? Clip(m.MessageId, 300) : "nomid:" + Hash($"{m.FromAddress}|{m.Subject}|{m.Date:o}|{Clip(m.Text, 500)}")[..32];
        await using (var db = await _dbf.CreateDbContextAsync())
            if (await db.InboundMail.AnyAsync(x => x.TenantId == t.Id && x.MessageId == messageId))
                return new InboundMail { Outcome = "duplicate" };
        m.MessageId = messageId;

        var (who, name) = RequesterOf(m, s.Address);
        var platform = await _platformMail.GetAsync();

        // ---- left alone, always with the reason on the company's page ----
        if (who.Length == 0 || !LooksLikeEmail(who))
            return await LogAsync(t.Id, channel, m, "skipped", "it has no sender address to answer");
        if (Same(who, s.Address) || Same(who, platform.IntakeAddress) || Same(who, platform.SmtpFrom) || Same(who, platform.SmtpUser))
            return await LogAsync(t.Id, channel, m, "skipped",
                "it came FROM your own address. Either it is a loop, or it was forwarded by a rule that put your address in place of the customer's — "
                + "forward on the mail server (cPanel: Email → Forwarders) rather than with an Outlook rule that forwards.");
        if (m.Spam) return await LogAsync(t.Id, channel, m, "skipped", "your mail server marked it as spam");
        if (m.Automatic.Length > 0) return await LogAsync(t.Id, channel, m, "skipped", m.Automatic);
        if (IsRobot(who)) return await LogAsync(t.Id, channel, m, "skipped", "it is from a no-reply address — there is nobody to answer");

        // ---- a reply to one of ours goes on that ticket ----
        var number = TicketNumberIn(m, t.Id);
        string followsOn = "";
        if (number > 0)
        {
            await using var db = await _dbf.CreateDbContextAsync();
            var earlier = await db.Tickets.FirstOrDefaultAsync(x => x.TenantId == t.Id && x.Number == number);
            if (earlier != null)
            {
                // Only the person it was opened for, somebody at the same client, or an address an update
                // from this ticket was emailed to (2026-10-09 — it may have been typed in by hand) — anyone
                // at all can type "[Ticket #12]" in a subject, and must not be able to write on ticket 12.
                var theirs = Same(who, earlier.RequesterEmail)
                    || (earlier.ClientId is int cid && await db.Contacts.AnyAsync(c => c.TenantId == t.Id && c.ClientId == cid && !c.Archived && c.Email.ToLower() == who))
                    || await db.TicketClientUpdates.AnyAsync(u => u.TenantId == t.Id && u.TicketId == earlier.Id && u.Sent && u.ToAddress == who);
                var open = earlier.Status is not (TicketStatus.Closed or TicketStatus.Resolved);
                if (theirs && open)
                {
                    var body = StripQuoted(m.Text);
                    var extra = m.Attachments.Count > 0 ? $"\n\n[{Attached(m.Attachments)} — in the email, not copied here]" : "";
                    // Theirs, flagged to the people on the ticket until they open it (2026-10-09).
                    await _tickets.AddCustomerReplyAsync(t.Id, earlier.Id, $"{(name.Length > 0 ? name : who)} (by email)",
                        (body.Length > 0 ? body : "(an empty reply)") + extra, name.Length > 0 ? name : who);
                    TicketMade?.Invoke(t.Id, earlier.Id);
                    return await LogAsync(t.Id, channel, m, "reply", $"added to ticket #{earlier.Number}", earlier);
                }
                followsOn = !theirs
                    ? $"It mentions ticket #{earlier.Number}, but came from a different address, so it was not added to that ticket."
                    : $"It is a reply to ticket #{earlier.Number}, which is closed, so it opened this one.";
            }
        }

        // ---- the limits ----
        var hourAgo = DateTime.UtcNow.AddHours(-1);
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            if (await db.InboundMail.CountAsync(x => x.TenantId == t.Id && x.FromAddress == who && x.Outcome == "ticket" && x.ReceivedUtc > hourAgo) >= PerSenderPerHour)
                return await LogAsync(t.Id, channel, m, "skipped", $"{who} has already opened {PerSenderPerHour} tickets in the last hour");
            if (await db.InboundMail.CountAsync(x => x.TenantId == t.Id && x.Outcome == "ticket" && x.ReceivedUtc > hourAgo) >= PerCompanyPerHour)
                return await LogAsync(t.Id, channel, m, "skipped", $"more than {PerCompanyPerHour} tickets arrived in the last hour — a flood, so the rest were left in your mail");
        }

        // ---- the ticket ----
        var match = await MatchAsync(t.Id, who, s.Address);
        var subject = m.Subject.Length > 0 ? Clip(m.Subject, 200) : $"Email from {(name.Length > 0 ? name : who)}";
        var description = m.Text.Length > 0 ? m.Text : "(the email had no text)";
        if (m.Attachments.Count > 0) description += $"\n\n[{Attached(m.Attachments)} — in the email, not copied to the ticket]";
        var ticket = await _tickets.CreateAsync(t.Id, subject, description, name.Length > 0 ? name : who, TicketSource.Email,
            contactName: match.ContactName.Length > 0 ? match.ContactName : name.Length > 0 ? name : who,
            category: TicketCategory.General, clientId: match.ClientId,
            extra: x => { x.RequesterEmail = who; x.ContactId = match.ContactId; });
        var how = channel == "forward" ? $"forwarded from {s.Address}" : $"to {s.Address}";
        await _tickets.AddNoteAsync(t.Id, ticket.Id, "Email",
            $"Opened by an email from {Person(name, who)}, {how}. {match.Note}{(followsOn.Length > 0 ? " " + followsOn : "")}".Trim(), isSystem: true, automatic: true);

        var reply = s.AutoReply && !m.NoAutoReply
            ? await SendAutoReplyAsync(t, s, ticket, who, name, m.Subject, m.MessageId)
            : m.NoAutoReply ? "not sent — the email asked not to be answered automatically" : "";
        TicketMade?.Invoke(t.Id, ticket.Id);
        return await LogAsync(t.Id, channel, m, "ticket", match.Note, ticket, reply, who, name);
    }

    // ================================================================ the contact form

    public sealed record FormInput(string? Name, string? Email, string? Phone, string? Company, string? Subject, string? Message, string? Page);

    /// <summary>What is wrong with a form, in words for the person filling it in, or empty.</summary>
    public static string CheckForm(TicketIntakeSettings s, FormInput f)
    {
        var name = (f.Name ?? "").Trim(); var email = (f.Email ?? "").Trim(); var phone = (f.Phone ?? "").Trim(); var text = (f.Message ?? "").Trim();
        if (name.Length == 0) return "Please tell us your name.";
        if (name.Length > 80) return "That name is too long.";
        if (email.Length == 0) return "Please give us your email address, so that we can reply.";
        if (!LooksLikeEmail(email)) return "That email address does not look right.";
        if (s.FormPhone == "required" && phone.Length == 0) return "Please give us a phone number.";
        if (phone.Length > 0 && (phone.Length > 40 || phone.Count(char.IsDigit) < 7)) return "That phone number does not look right.";
        if ((f.Company ?? "").Trim().Length > 120) return "That company name is too long.";
        if ((f.Subject ?? "").Trim().Length > 200) return "That subject is too long.";
        if (text.Length == 0) return "Please tell us what you need.";
        if (text.Length > FormTextMax) return "That is too long — please shorten it, or email us instead.";
        return "";
    }

    private static readonly ConcurrentDictionary<string, List<DateTime>> FormRecent = new();
    /// <summary>Forms from one internet address to one company in an hour. A whole office can sit behind one address.</summary>
    public const int FormsPerAddressPerHour = 10;
    public static bool AllowForm(int tenantId, string ip)
    {
        var at = DateTime.UtcNow;
        var list = FormRecent.GetOrAdd($"{tenantId}|{ip}", _ => new List<DateTime>());
        lock (list)
        {
            list.RemoveAll(x => at - x > TimeSpan.FromHours(1));
            if (list.Count >= FormsPerAddressPerHour) return false;
            list.Add(at);
            return true;
        }
    }

    /// <summary>A form sent from the company's website: the same rules as an email, made a ticket, answered.</summary>
    public async Task<(int number, string error, bool replied)> SubmitFormAsync(Tenant t, FormInput f)
    {
        var s = await GetAsync(t.Id);
        if (!s.FormEnabled || !t.IsServiceEnabled) return (0, "This form is not taking messages at the moment — please email or phone us instead.", false);
        var problem = CheckForm(s, f);
        if (problem.Length > 0) return (0, problem, false);
        var name = f.Name!.Trim(); var who = f.Email!.Trim().ToLowerInvariant(); var phone = (f.Phone ?? "").Trim();
        var company = s.FormCompany ? (f.Company ?? "").Trim() : ""; var text = MailWire.Tidy(f.Message!.Trim());
        var mail = new IncomingMail { FromAddress = who, FromName = name, Subject = (f.Subject ?? "").Trim(), Text = text };

        await using (var db = await _dbf.CreateDbContextAsync())
            if (await db.InboundMail.CountAsync(x => x.TenantId == t.Id && x.FromAddress == who && x.Outcome == "ticket" && x.ReceivedUtc > DateTime.UtcNow.AddHours(-1)) >= PerSenderPerHour)
            {
                await LogAsync(t.Id, "form", mail, "skipped", $"{who} has already opened {PerSenderPerHour} tickets in the last hour");
                return (0, "You have sent us several messages in the last hour — we have them, and will be in touch.", false);
            }

        var match = await MatchAsync(t.Id, who, s.Address, company);
        var subject = mail.Subject.Length > 0 ? Clip(mail.Subject, 200) : text.Length <= 70 ? text.Replace('\n', ' ') : text[..67].Replace('\n', ' ') + "…";
        var details = string.Join("\n", new[]
        {
            text, "",
            $"Name: {name}",
            $"Email: {who}",
            phone.Length > 0 ? $"Phone: {phone}" : "",
            company.Length > 0 ? $"Company: {company}" : "",
            (f.Page ?? "").Trim().Length > 0 ? $"Sent from: {Clip(f.Page, 300)}" : ""
        }.Where((x, i) => i < 2 || x.Length > 0));
        var ticket = await _tickets.CreateAsync(t.Id, subject, details, name, TicketSource.Form,
            contactName: match.ContactName.Length > 0 ? match.ContactName : name, category: TicketCategory.General, clientId: match.ClientId,
            extra: x => { x.RequesterEmail = who; x.ContactId = match.ContactId; });
        await _tickets.AddNoteAsync(t.Id, ticket.Id, "Website form", $"Opened from the contact form on your website by {Person(name, who)}. {match.Note}".Trim(), isSystem: true, automatic: true);
        var reply = s.AutoReply ? await SendAutoReplyAsync(t, s, ticket, who, name, subject, "") : "";
        TicketMade?.Invoke(t.Id, ticket.Id);
        await LogAsync(t.Id, "form", mail, "ticket", match.Note, ticket, reply, who, name);
        // "sent", or sent from our mailbox when theirs failed: either way it reached them.
        return (ticket.Number, "", reply.StartsWith("sent"));
    }

    // ================================================================ who is it

    /// <summary>
    /// Who an address belongs to. Linked only when the answer is ONE client; otherwise the ticket
    /// stays unlinked and <see cref="Note"/> says why, in words for the office. <see cref="Suggested"/>
    /// is Clint's answer 3: the client is known (their domain), the person is not.
    /// </summary>
    public sealed record SenderMatch(int? ClientId, string ClientName, int? ContactId, string ContactName,
        List<(int id, string name)> Suggested, string Note);

    /// <summary>Free mail: a domain anybody can have, so it never says which client somebody is from.</summary>
    public static readonly HashSet<string> FreeMail = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "outlook.com", "hotmail.com", "hotmail.co.za", "hotmail.co.uk", "live.com", "live.co.za",
        "live.co.uk", "msn.com", "yahoo.com", "yahoo.co.za", "yahoo.co.uk", "ymail.com", "rocketmail.com", "icloud.com", "me.com",
        "mac.com", "aol.com", "gmx.com", "gmx.net", "mail.com", "protonmail.com", "proton.me", "pm.me", "zoho.com", "zohomail.com",
        "yandex.com", "tutanota.com", "fastmail.com",
        // South African internet providers, which hand every customer an address
        "mweb.co.za", "telkomsa.net", "vodamail.co.za", "webmail.co.za", "iafrica.com", "absamail.co.za", "lantic.net",
        "afrihost.co.za", "axxess.co.za", "cybersmart.co.za", "xsinet.co.za", "polka.co.za", "global.co.za", "wol.co.za",
        "netactive.co.za", "intekom.co.za", "ananzi.co.za", "mtnloaded.co.za", "rain.co.za", "cool-ideas.co.za", "webafrica.co.za"
    };

    /// <summary>A domain on this many clients or more is shared (an internet provider nobody listed), not one client's.</summary>
    public const int SharedDomainAt = 3;

    public async Task<SenderMatch> MatchAsync(int tenantId, string address, string? companyAddress = null, string? companyNamed = null)
    {
        var who = (address ?? "").Trim().ToLowerInvariant();
        var none = new List<(int, string)>();
        if (!LooksLikeEmail(who)) return new(null, "", null, "", none, "");
        await using var db = await _dbf.CreateDbContextAsync();

        // 1. A contact with exactly this address.
        var people = await db.Contacts.Where(c => c.TenantId == tenantId && !c.Archived && c.Email.ToLower() == who)
            .Select(c => new { c.Id, c.Name, c.ClientId, Client = c.Client.Name, ClientArchived = c.Client.Archived }).ToListAsync();
        people = people.Where(p => !p.ClientArchived).ToList();
        var byClient = people.Select(p => p.ClientId).Distinct().ToList();
        if (byClient.Count == 1)
        {
            var p = people.OrderByDescending(x => x.Id).First();
            return new(p.ClientId, p.Client, p.Id, p.Name, none, $"Linked to {p.Client} — {p.Name} is a contact there.");
        }
        if (byClient.Count > 1)
        {
            var names = people.Select(p => p.Client).Distinct().OrderBy(x => x).ToList();
            return new(null, "", null, "", people.GroupBy(p => p.ClientId).Select(g => (g.Key, g.First().Client)).ToList(),
                $"{who} is a contact at {names.Count} clients ({Few(names)}), so it was not linked — choose the right one.");
        }

        // 2. A client with exactly this address.
        var clients = await db.Clients.Where(c => c.TenantId == tenantId && !c.Archived && c.Email.ToLower() == who)
            .Select(c => new { c.Id, c.Name }).ToListAsync();
        if (clients.Count == 1)
            return new(clients[0].Id, clients[0].Name, null, "", none, $"Linked to {clients[0].Name} — it is their email address on file.");
        if (clients.Count > 1)
            return new(null, "", null, "", clients.Select(c => (c.Id, c.Name)).ToList(),
                $"{who} is on file for {clients.Count} clients ({Few(clients.Select(c => c.Name))}), so it was not linked — choose the right one.");

        // 3. The domain — Clint's answer 3: the client is known, the person is not.
        var domain = MailWire.Domain(who);
        if (domain.Length > 0 && !FreeMail.Contains(domain) && !await OwnDomainAsync(db, tenantId, domain, companyAddress))
        {
            var tail = "@" + domain;
            var ids = await db.Clients.Where(c => c.TenantId == tenantId && !c.Archived && c.Email.ToLower().EndsWith(tail)).Select(c => c.Id)
                .Union(db.Contacts.Where(c => c.TenantId == tenantId && !c.Archived && c.Email.ToLower().EndsWith(tail)).Select(c => c.ClientId))
                .Distinct().ToListAsync();
            var found = await db.Clients.Where(c => ids.Contains(c.Id) && !c.Archived).Select(c => new { c.Id, c.Name }).OrderBy(c => c.Name).ToListAsync();
            if (found.Count > 0 && found.Count < SharedDomainAt)
                return new(null, "", null, "", found.Select(c => (c.Id, c.Name)).ToList(),
                    found.Count == 1
                        ? $"{domain} belongs to {found[0].Name}, but {who} is not on file. Add them as a contact so that this ticket is linked to {found[0].Name}."
                        : $"{domain} belongs to {Few(found.Select(c => c.Name))}, but {who} is not on file. Add them as a contact at the right one so that this ticket is linked.");
        }

        // 4. The form's Company box, when it names a client exactly.
        var named = (companyNamed ?? "").Trim().ToLowerInvariant();
        if (named.Length > 0)
        {
            var same = await db.Clients.Where(c => c.TenantId == tenantId && !c.Archived && c.Name.ToLower() == named).Select(c => new { c.Id, c.Name }).ToListAsync();
            if (same.Count > 0)
                return new(null, "", null, "", same.Select(c => (c.Id, c.Name)).ToList(),
                    $"They said they are from {same[0].Name}, but {who} is not on file. Add them as a contact so that this ticket is linked to {same[0].Name}.");
        }
        return new(null, "", null, "", none, $"{who} is not on file, so the ticket is not linked to a client.");
    }

    /// <summary>The company's OWN domains — its support address, its people's addresses, the platform's —
    /// which say nothing about which customer somebody is.</summary>
    private async Task<bool> OwnDomainAsync(PlatformDb db, int tenantId, string domain, string? companyAddress)
    {
        if (domain == MailWire.Domain(companyAddress ?? "")) return true;
        var t = await db.Tenants.Where(x => x.Id == tenantId).Select(x => x.SupportEmail).FirstOrDefaultAsync();
        if (domain == MailWire.Domain(t ?? "")) return true;
        var p = await _platformMail.GetAsync();
        if (domain == MailWire.Domain(p.IntakeAddress) || domain == MailWire.Domain(p.SmtpFrom) || domain == MailWire.Domain(p.SmtpUser)) return true;
        var tail = "@" + domain;
        return await db.Technicians.AnyAsync(x => x.TenantId == tenantId && x.Email.ToLower().EndsWith(tail));
    }

    /// <summary>
    /// The button on the ticket: the sender becomes a contact at the client they belong to, and the
    /// ticket is linked to both. An existing contact of the same name there is reused (ContactService's
    /// rule) and given the address if it had none.
    /// </summary>
    public async Task<string> AddSenderAsContactAsync(int tenantId, int ticketId, int clientId, string? name, string by)
    {
        Ticket? t;
        await using (var db = await _dbf.CreateDbContextAsync())
            t = await db.Tickets.FirstOrDefaultAsync(x => x.Id == ticketId && x.TenantId == tenantId);
        if (t == null) return "That ticket is not on file.";
        if (t.RequesterEmail.Length == 0) return "This ticket did not come from an email address.";
        var match = await MatchAsync(tenantId, t.RequesterEmail);
        if (match.ClientId != null && match.ContactId != null)
            return await _tickets.SetClientAsync(tenantId, ticketId, match.ClientId, match.ContactName, by, match.ContactId);
        var person = (name ?? "").Trim();
        if (person.Length == 0) person = t.ContactName.Length > 0 && !t.ContactName.Contains('@') ? t.ContactName : t.RequesterEmail;
        var (contact, error) = await _contacts.CreateAsync(tenantId, clientId, person, "", t.RequesterEmail, "",
            $"Added from ticket #{t.Number}, which they opened by {(t.Source == TicketSource.Form ? "the website form" : "email")}.");
        if (contact == null) return error;
        if (contact.Email.Length == 0) await _contacts.UpdateAsync(tenantId, contact.Id, c => c.Email = t.RequesterEmail);
        return await _tickets.SetClientAsync(tenantId, ticketId, clientId, contact.Name, by, contact.Id);
    }

    // ================================================================ the auto-reply

    public const string DefaultReply =
        "Hello {name},\n\n"
        + "Thank you for contacting {company}. We have opened ticket #{number} for your message, and one of our team will be in touch soon.\n\n"
        + "If you need to add anything, simply reply to this email and keep the ticket number in the subject.\n\n"
        + "Kind regards\n{company}";

    /// <summary>The reply's words, with the blanks filled in. Used for the preview as well.</summary>
    public static string ReplyText(TicketIntakeSettings s, Tenant t, int number, string name, string subject) =>
        (string.IsNullOrWhiteSpace(s.AutoReplyText) ? DefaultReply : s.AutoReplyText)
            .Replace("{name}", name.Length > 0 ? name : "there")
            .Replace("{number}", number.ToString())
            .Replace("{subject}", subject)
            .Replace("{company}", t.CompanyName);

    public static string ReplySubject(int number, string subject) =>
        $"[Ticket #{number}] {(subject.Length > 0 ? Regex.Replace(subject, @"\s*\[Ticket\s*#\d+\]\s*", " ", RegexOptions.IgnoreCase).Trim() : "Your message")}";

    /// <summary>"sent", or why not. Through their own server when they chose it; from ours, in their
    /// name with their address to reply to, otherwise — and when theirs fails, so the customer is still answered.</summary>
    private async Task<string> SendAutoReplyAsync(Tenant t, TicketIntakeSettings s, Ticket ticket, string to, string name, string subject, string inReplyTo)
    {
        var (result, _) = await SendAsCompanyAsync(t, s, "auto-reply", (fromAddress, withReplyTo, replyTo) =>
        {
            var msg = new MimeMessage();
            msg.From.Add(new MailboxAddress(t.CompanyName, fromAddress));
            if (withReplyTo && LooksLikeEmail(replyTo)) msg.ReplyTo.Add(new MailboxAddress(t.CompanyName, replyTo));
            msg.To.Add(new MailboxAddress(name, to));
            msg.Subject = ReplySubject(ticket.Number, subject);
            // Ours, carrying the ticket — a reply's References brings it back, whatever they do to the subject.
            msg.MessageId = NewMessageId(t.Id, ticket.Number, fromAddress);
            if (inReplyTo.Length > 0 && !inReplyTo.StartsWith("nomid:")) { msg.InReplyTo = inReplyTo; msg.References.Add(inReplyTo); }
            // RFC 3834: an automatic answer says so, so another machine's out-of-office does not answer it back.
            msg.Headers.Add("Auto-Submitted", "auto-replied");
            msg.Headers.Add("X-Auto-Response-Suppress", "All");
            msg.Body = new TextPart("plain") { Text = ReplyText(s, t, ticket.Number, name, subject) };
            return msg;
        });
        return result;
    }

    /// <summary>A Message-ID carrying the ticket — rsp.{tenant}.{number}.{random}@ — which <see cref="TicketNumberIn"/> reads back from a reply.</summary>
    public static string NewMessageId(int tenantId, int number, string fromAddress) =>
        $"rsp.{tenantId}.{number}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}@{(MailWire.Domain(fromAddress) is { Length: > 0 } d ? d : "theremotesupportprogram.com")}";

    /// <summary>
    /// Sends a message as the company — the auto-reply, and an update to a client from a ticket
    /// (<c>ClientUpdates</c>, 2026-10-09). Through their own server when they chose it, so it really is
    /// from support@; otherwise, and when theirs fails, from ours in their name with support@ to reply to.
    /// <paramref name="make"/> is given the From address, whether to add a Reply-To, and the Reply-To.
    /// </summary>
    /// <returns>"sent", "sent from our mailbox, because yours said: …" or "not sent: …", and the message
    /// that went (null when none did).</returns>
    public async Task<(string result, MimeMessage? sent)> SendAsCompanyAsync(Tenant t, TicketIntakeSettings s, string what,
        Func<string, bool, string, MimeMessage> make)
    {
        var replyTo = s.Address.Length > 0 ? s.Address : t.SupportEmail;
        string ownFailed = "";
        if (s.SendVia == "own" && LooksLikeEmail(s.Address) && (s.Way != "microsoft" || MicrosoftConnected(s)))
        {
            var (smtp, problem) = await SenderAsync(s, CancellationToken.None);
            if (problem.Length > 0) ownFailed = problem;
            else if (smtp is { Host.Length: > 0 })
            {
                var msg = make(s.Address, false, replyTo);
                try { await MailWire.SendAsync(smtp, msg, CancellationToken.None); return ("sent", msg); }
                catch (Exception ex) { ownFailed = MailWire.Explain(ex, smtp, true, s.Address).words; _log.LogInformation("[{Tenant}] {What} through their server failed: {Why}", t.Slug, what, ownFailed); }
            }
        }
        var (server, from, why) = await _platformMail.SenderAsync();
        if (server == null) return (ownFailed.Length > 0 ? "not sent: " + ownFailed : "not sent: " + why, null);
        try
        {
            var msg = make(from, true, replyTo);
            await MailWire.SendAsync(server, msg, CancellationToken.None);
            return (ownFailed.Length > 0 ? "sent from our mailbox, because yours said: " + ownFailed : "sent", msg);
        }
        catch (Exception ex) { return ("not sent: " + MailWire.Explain(ex, server, true, from).words, null); }
    }

    // ================================================================ proving a forwarded address is theirs

    /// <summary>Mails a confirmation link to the address. Forwarding sorts nothing to a company until it is used.</summary>
    public async Task<string> SendConfirmationAsync(int tenantId)
    {
        var t = await _tenants.ByIdAsync(tenantId);
        var s = await GetAsync(tenantId);
        if (t == null || s.Address.Length == 0) return "Give your support address first.";
        var token = Passwords.RandomToken();
        await SaveAsync(tenantId, x => { x.ConfirmTokenHash = Hash(token); x.ConfirmSentUtc = DateTime.UtcNow; });
        var link = $"{_publicUrl}/mail-confirm?t={token}";
        var body = $"Somebody at {t.CompanyName} asked for mail to {s.Address} to become tickets in The Remote Support Program.\n\n"
                 + $"If that was you, open this link and press Confirm:\n{link}\n\n"
                 + "If it was not, ignore this email — nothing happens until the link is used. The link works for 7 days.";
        var sent = await _platformMail.SendAsync(new[] { s.Address }, $"Confirm {s.Address} for tickets by email", body, "The Remote Support Program", null, copyOwner: false);
        return sent == "sent" ? "" : "The confirmation email could not be sent: " + sent;
    }

    /// <summary>The address a confirmation link is for, without using it — the GET shows this, the POST confirms.</summary>
    public async Task<(string company, string address)?> PeekConfirmationAsync(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var hash = Hash(token);
        await using var db = await _dbf.CreateDbContextAsync();
        var s = await db.TicketIntake.FirstOrDefaultAsync(x => x.ConfirmTokenHash == hash);
        if (s == null || s.ConfirmSentUtc < DateTime.UtcNow.AddDays(-7)) return null;
        var name = await db.Tenants.Where(x => x.Id == s.TenantId).Select(x => x.CompanyName).FirstOrDefaultAsync() ?? "";
        return (name, s.Address);
    }

    /// <summary>Spends the link. A link is spent by the POST, never the GET — mail filters open links.</summary>
    public async Task<string> ConfirmAsync(string? token)
    {
        if (await PeekConfirmationAsync(token) is not { } found) return "That link has expired or has already been used.";
        await using var db = await _dbf.CreateDbContextAsync();
        var hash = Hash(token!);
        var s = await db.TicketIntake.FirstAsync(x => x.ConfirmTokenHash == hash);
        if (await db.TicketIntake.AnyAsync(x => x.TenantId != s.TenantId && x.Address == s.Address && x.AddressConfirmed))
            return "That address is already taking tickets for another company on this platform.";
        s.AddressConfirmed = true; s.ConfirmTokenHash = ""; s.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return "";
    }

    // ================================================================ reading the log

    public async Task<List<InboundMail>> RecentAsync(int tenantId, int take = 50)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.InboundMail.Where(x => x.TenantId == tenantId).OrderByDescending(x => x.Id).Take(take).ToListAsync();
    }

    // ================================================================ small rules

    private static readonly Regex SubjectTag = new(@"\[\s*Ticket\s*#\s*(\d{1,9})\s*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OurId = new(@"rsp\.(\d+)\.(\d+)\.[0-9a-f]+@", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The ticket an email answers: our Message-ID in its References first (it survives a
    /// rewritten subject), then "[Ticket #12]" in the subject. 0 when it answers none.</summary>
    public static int TicketNumberIn(IncomingMail m, int tenantId)
    {
        foreach (var r in m.References.Append(m.InReplyTo))
        {
            var x = OurId.Match(r ?? "");
            if (x.Success && int.Parse(x.Groups[1].Value) == tenantId && int.TryParse(x.Groups[2].Value, out var n)) return n;
        }
        var s = SubjectTag.Match(m.Subject);
        return s.Success && int.TryParse(s.Groups[1].Value, out var k) ? k : 0;
    }

    /// <summary>
    /// Who the person is. Normally the sender; but a website's form robot writes FROM itself
    /// (wordpress@, noreply@, or an address on the company's own domain) with the customer as
    /// Reply-To — that is how a company's existing contact form, emailing support@, still opens a
    /// ticket for the customer.
    /// </summary>
    public static (string address, string name) RequesterOf(IncomingMail m, string companyAddress)
    {
        var from = m.FromAddress.Trim().ToLowerInvariant();
        var robot = IsRobot(from) || from.Split('@')[0] is "wordpress" or "website" or "webmaster" or "forms" or "form" or "contact-form" or "wix-forms" or "mailer"
            || (MailWire.Domain(companyAddress) is { Length: > 0 } own && MailWire.Domain(from) == own);
        if (robot && m.ReplyToAddress.Length > 0 && m.ReplyToAddress != from && LooksLikeEmail(m.ReplyToAddress))
            return (m.ReplyToAddress, m.ReplyToName);
        return (from, m.FromName);
    }

    public static bool IsRobot(string address)
    {
        var local = address.Split('@')[0].ToLowerInvariant();
        return local.StartsWith("noreply") || local.StartsWith("no-reply") || local.StartsWith("no_reply")
            || local.StartsWith("donotreply") || local.StartsWith("do-not-reply") || local.StartsWith("do_not_reply");
    }

    /// <summary>
    /// A reply without the history under it — "On Monday, Jane wrote:", Outlook's "From: … Sent: …"
    /// block, "-----Original Message-----", and quoted "&gt;" lines. When that would leave nothing,
    /// the whole text is kept rather than losing what they wrote.
    /// </summary>
    public static string StripQuoted(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int cut = lines.Length;
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i].Trim();
            bool wroteHere = Regex.IsMatch(l, @"^On\s.+wrote:$", RegexOptions.IgnoreCase)
                || (l.EndsWith("wrote:", StringComparison.OrdinalIgnoreCase) && i > 0 && lines[i - 1].Trim().StartsWith("On ", StringComparison.OrdinalIgnoreCase));
            if (wroteHere) { cut = l.StartsWith("On ", StringComparison.OrdinalIgnoreCase) ? i : i - 1; break; }
            if (Regex.IsMatch(l, @"^-{2,}\s*Original Message\s*-{2,}$", RegexOptions.IgnoreCase) || Regex.IsMatch(l, "^_{10,}$")) { cut = i; break; }
            if (Regex.IsMatch(l, @"^From:\s.+", RegexOptions.IgnoreCase)
                && lines.Skip(i + 1).Take(4).Any(n => Regex.IsMatch(n.Trim(), @"^(Sent|Date):\s", RegexOptions.IgnoreCase))) { cut = i; break; }
            if (l.StartsWith(">") && lines.Skip(i).All(n => n.Trim().Length == 0 || n.TrimStart().StartsWith(">"))) { cut = i; break; }
        }
        var kept = string.Join("\n", lines.Take(cut)).Trim();
        return kept.Length > 0 ? kept : text.Trim();
    }

    public static bool LooksLikeEmail(string? s) =>
        !string.IsNullOrWhiteSpace(s) && s.Length <= 200 && Regex.IsMatch(s.Trim(), @"^[^@\s<>,;""]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}$");

    private static bool Same(string a, string? b) => b != null && b.Trim().Length > 0 && a.Trim().Equals(b.Trim(), StringComparison.OrdinalIgnoreCase);
    private static string Person(string name, string address) => name.Length > 0 ? $"{name} <{address}>" : address;
    private static string Few(IEnumerable<string> names) { var l = names.ToList(); return l.Count <= 3 ? string.Join(", ", l) : string.Join(", ", l.Take(3)) + $" and {l.Count - 3} more"; }
    private static string Attached(List<string> names) => names.Count == 1 ? $"1 attachment: {names[0]}" : $"{names.Count} attachments: {string.Join(", ", names.Take(5))}{(names.Count > 5 ? " …" : "")}";
    private static string Clip(string? s, int max) { s = (s ?? "").Trim(); return s.Length <= max ? s : s[..max]; }
    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private async Task<InboundMail> LogAsync(int tenantId, string channel, IncomingMail m, string outcome, string reason,
        Ticket? ticket = null, string reply = "", string? who = null, string? name = null)
    {
        var row = new InboundMail
        {
            TenantId = tenantId, Channel = channel, MessageId = Clip(m.MessageId, 300),
            FromAddress = Clip(who ?? m.FromAddress, 200), FromName = Clip(name ?? m.FromName, 120), Subject = Clip(m.Subject, 200),
            Outcome = outcome, Reason = Clip(reason, 600), TicketId = ticket?.Id, TicketNumber = ticket?.Number ?? 0, ReplyResult = Clip(reply, 400)
        };
        await using var db = await _dbf.CreateDbContextAsync();
        db.InboundMail.Add(row);
        await db.SaveChangesAsync();
        return row;
    }
}
