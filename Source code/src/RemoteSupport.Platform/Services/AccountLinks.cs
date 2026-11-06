using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Invitations and password resets (2026-10-01, HANDOVER §73) — every rule in one place.
///
/// Clint: <i>"when setting up users, will it send each one a link for their download and request
/// them to change their password? I think this is an important feature."</i> It did not: the
/// supervisor typed a password for the new person and passed it on by hand, nobody could change
/// their own, and "Forgot password?" said "contact support".
///
/// Now: a person is added with a name, an email and boxes — no password. They get a mail, from our
/// mailbox in the company's name, with a one-use link (72 hours) to choose their own password and
/// set up two-factor sign-in, and the console download link if they have a seat. A supervisor's
/// Reset sends a link (2 hours) instead of typing a password, and "Forgot password?" sends the same.
/// Nobody ever knows anybody else's password.
///
/// <para>The rules: only the SHA-256 of a link's secret is stored; a link is spent by the POST that
/// sets the password, never by opening it (mail filters open every link first); a new link replaces
/// the person's earlier one of the same kind; setting a password moves the security stamp, so a
/// reset signs out everything signed in with the old one; "Forgot password?" answers the same
/// whether or not the address exists, and is limited per address and per caller.</para>
/// </summary>
public sealed class AccountLinks
{
    public static readonly TimeSpan InviteLife = TimeSpan.FromHours(72);
    public static readonly TimeSpan ResetLife = TimeSpan.FromHours(2);

    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly PlatformMailService _mail;
    private readonly AuthService _auth;
    private readonly ILogger<AccountLinks> _log;
    private readonly string _publicUrl;
    private readonly ConcurrentDictionary<string, List<DateTime>> _asked = new();

    public AccountLinks(IDbContextFactory<PlatformDb> dbf, PlatformMailService mail, AuthService auth, IConfiguration cfg, ILogger<AccountLinks> log)
    {
        _dbf = dbf; _mail = mail; _auth = auth; _log = log;
        _publicUrl = (cfg["Platform:PublicUrl"] ?? "").TrimEnd('/');
    }

    public string PublicUrl => _publicUrl;

    /// <summary>What happened to a link that was made: the link itself, and whether the mail went.</summary>
    public sealed record Sent(string Link, bool Mailed, string Why);

    /// <summary>
    /// Makes a link for this person and mails it. <paramref name="by"/> is who asked, for the mail
    /// and the record. When the mail cannot go (no platform mailbox yet, or the server refused it)
    /// the link comes back so a supervisor can pass it on — it is still one person's, once.
    /// </summary>
    /// <param name="cc">Copies the recipient sees — only ever from <see cref="FreeCompanies"/>, where a platform admin names the
    /// technician who will sit with them through the set-up (2026-10-23). They get the link too, which is the point.</param>
    public async Task<Sent> IssueAsync(int technicianId, AccountTokenPurpose purpose, string by, IReadOnlyList<string>? cc = null)
    {
        cc ??= Array.Empty<string>();
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Technicians.Include(x => x.Tenant).FirstOrDefaultAsync(x => x.Id == technicianId);
        if (t == null) return new Sent("", false, "That person no longer exists.");

        // One live link per person and kind: a new one replaces the old.
        var old = await db.AccountTokens.Where(x => x.TechnicianId == t.Id && x.Purpose == purpose && x.UsedUtc == null).ToListAsync();
        db.AccountTokens.RemoveRange(old);
        var secret = Passwords.RandomToken(32);
        var life = purpose == AccountTokenPurpose.Invite ? InviteLife : ResetLife;
        db.AccountTokens.Add(new AccountToken
        {
            TechnicianId = t.Id, Purpose = purpose, TokenHash = Hash(secret),
            ExpiresUtc = DateTime.UtcNow.Add(life), CreatedBy = by
        });
        await db.SaveChangesAsync();

        var link = $"{_publicUrl}/account/setup?t={secret}";
        var (subject, body) = Compose(t, purpose, link, by, life, cc);
        // A free company's first owner is set up by US (FreeCompanies): the company is themselves, so the mail comes from us and a
        // reply goes to the technician helping them, when there is one.
        var ours = FreeCompanies.IsTheirFirstInvite(t, purpose);
        var r = await _mail.SendAsync(new[] { t.Email }, subject, body, ours ? FreeCompanies.FromName : t.Tenant.CompanyName,
            ours ? cc.FirstOrDefault() : t.Tenant.SupportEmail, copyOwner: false, cc: cc);
        if (r != "sent") _log.LogWarning("[{Tenant}] {Purpose} link for {Email} not mailed: {Why}", t.Tenant.CompanyName, purpose, t.Email, r);
        else _log.LogInformation("[{Tenant}] {Purpose} link mailed to {Email} (asked by {By})", t.Tenant.CompanyName, purpose, t.Email, by);
        return new Sent(link, r == "sent", r == "sent" ? "" : r);
    }

    private (string subject, string body) Compose(Technician t, AccountTokenPurpose purpose, string link, string by, TimeSpan life, IReadOnlyList<string> cc)
    {
        var company = t.Tenant.CompanyName;
        var until = DateTime.UtcNow.Add(life).ToString("d MMM yyyy HH:mm") + " UTC";
        var portal = $"{_publicUrl}/login";
        if (FreeCompanies.IsTheirFirstInvite(t, purpose)) return FreeCompanies.Invitation(t, link, until, _publicUrl, cc);
        var sb = new StringBuilder();
        sb.Append($"Hello {t.DisplayName},\n\n");
        if (purpose == AccountTokenPurpose.Invite)
        {
            sb.Append($"{company} has added you to their support system as {TenantRoles.Label(t.Role)}");
            sb.Append(by.Length > 0 && by != "command line" ? $" ({by} set you up).\n\n" : ".\n\n");
            sb.Append("Set up your account — choose your password, then set up two-factor sign-in with an\n");
            sb.Append("authenticator app on your phone (Microsoft Authenticator or Google Authenticator):\n\n");
            sb.Append($"  {link}\n\n");
            sb.Append($"The link works once and expires on {until}. If it has expired, ask {company} to send another.\n\n");
            if (t.CanUseConsole)
                sb.Append($"Once you are set up, download the technician console here:\n\n  {_publicUrl}/tech/{t.Tenant.Slug}\n\n");
            sb.Append($"The portal is at {portal} — sign in with this email address ({t.Email}).\n\n");
            sb.Append("If you were not expecting this, you can ignore this email.\n");
            return ($"{company}: set up your account", sb.ToString());
        }
        sb.Append($"Somebody asked to reset the password for your {company} account");
        sb.Append(by.Length > 0 && by != "Forgot password" ? $" ({by}).\n\n" : ".\n\n");
        sb.Append($"Choose a new password here:\n\n  {link}\n\n");
        sb.Append($"The link works once and expires on {until}. You will still need the code from your\n");
        sb.Append("authenticator app to sign in afterwards.\n\n");
        sb.Append("If you did not ask for this, ignore this email — your password has not changed.\n");
        return ($"{company}: reset your password", sb.ToString());
    }

    /// <summary>A link that can still be used: whose it is, and what for. Opening the page asks this; it spends nothing.</summary>
    public async Task<(Technician? who, AccountToken? link, string error)> LookAsync(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return (null, null, "This link is not complete. Copy the whole link from the email.");
        await using var db = await _dbf.CreateDbContextAsync();
        var row = await db.AccountTokens.FirstOrDefaultAsync(x => x.TokenHash == Hash(secret.Trim()));
        if (row == null) return (null, null, "This link is not valid. It may have been replaced by a newer one — use the latest email.");
        if (row.UsedUtc != null) return (null, row, "This link has already been used. Sign in with the password you chose, or use Forgot password.");
        if (row.ExpiresUtc < DateTime.UtcNow)
            return (null, row, row.Purpose == AccountTokenPurpose.Invite
                ? "This invitation has expired. Ask your company to send you another."
                : "This link has expired. Ask for another with Forgot password.");
        var who = await db.Technicians.Include(x => x.Tenant).FirstOrDefaultAsync(x => x.Id == row.TechnicianId);
        if (who == null || who.Disabled) return (null, row, "That account no longer exists.");
        if (!who.Tenant.IsServiceEnabled) return (null, row, $"{who.Tenant.CompanyName}'s account is not active. Please contact them.");
        return (who, row, "");
    }

    /// <summary>
    /// Spends the link: sets the password the person chose, signs out anything signed in with the old
    /// one (the stamp moves), and returns who it was so the page can go straight on to the code step.
    /// </summary>
    public async Task<(Technician? who, string error)> CompleteAsync(string? secret, string? password, string? again)
    {
        var (who, link, error) = await LookAsync(secret);
        if (who == null || link == null) return (null, error);
        if (password != again) return (null, "The two passwords are not the same.");
        var problem = Passwords.Problem(password, who.Email);
        if (problem.Length > 0) return (null, problem);

        await using var db = await _dbf.CreateDbContextAsync();
        // Spent FIRST, by one conditional update: the same link posted twice at once sets one
        // password, and the second post finds nothing left to spend.
        var spent = await db.AccountTokens.Where(x => x.Id == link.Id && x.UsedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedUtc, DateTime.UtcNow));
        if (spent == 0) return (null, "This link has already been used.");
        var t = await db.Technicians.Include(x => x.Tenant).FirstAsync(x => x.Id == who.Id);
        (t.Salt, t.Hash) = Passwords.Create(password!);
        t.SecurityStamp = AuthService.NewStamp();
        await db.SaveChangesAsync();
        _ = _auth.RecheckAsync(t.Id);
        _log.LogInformation("[{Tenant}] {Email} chose a password ({Purpose} link)", t.Tenant.CompanyName, t.Email, link.Purpose);
        return (t, "");
    }

    /// <summary>
    /// "Forgot password?": mails a reset link if the address has an account, and says the same thing
    /// whether or not it does. Three a hour per address, ten per caller — enough for a person, too
    /// few to fill somebody's inbox or to use as a way of asking which addresses exist.
    /// </summary>
    public async Task ForgotAsync(string? email, string caller)
    {
        email = (email ?? "").Trim().ToLowerInvariant();
        if (email.Length < 3 || !email.Contains('@')) return;
        if (!Allow("e:" + email, 3) || !Allow("c:" + caller, 10)) return;
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Technicians.Include(x => x.Tenant).FirstOrDefaultAsync(x => x.Email == email);
        if (t == null || t.Disabled || !t.Tenant.IsServiceEnabled) return;
        // Somebody who never finished their invitation gets the invitation again, not a reset.
        await IssueAsync(t.Id, t.AwaitingSetup ? AccountTokenPurpose.Invite : AccountTokenPurpose.Reset, "Forgot password");
    }

    private bool Allow(string key, int perHour)
    {
        var now = DateTime.UtcNow;
        var list = _asked.GetOrAdd(key, _ => new List<DateTime>());
        lock (list)
        {
            list.RemoveAll(x => now - x > TimeSpan.FromHours(1));
            if (list.Count >= perHour) return false;
            list.Add(now);
            return true;
        }
    }

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
