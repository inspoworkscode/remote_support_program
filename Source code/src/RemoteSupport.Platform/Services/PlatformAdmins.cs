using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// The people who help run the platform (2026-10-13, docs/NEXT-SESSION-CONTROL.md, stage A) — EVERY rule about
/// them: who may do what, adding and removing them, their set-up link, their sign-in, and the log of what they
/// did. Clint: <i>"an option for me to add admins that help me manage everything like activating and suspending
/// clients"</i>, and, on his answers, levels (<see cref="AdminLevel"/>), a log, and signing in once a week.
///
/// <para>The rules, decided 2026-10-13 and not to be re-asked:</para>
/// <list type="bullet">
/// <item>A Viewer changes nothing; a Manager runs companies; a Full admin also runs admins (Viewer and Manager
/// only), servers and the platform's mail; an Owner makes and changes Full admins and Owners.</item>
/// <item><b>An Owner is never removed or lowered from the pages</b> — not even by another Owner. The command
/// line can (<c>remove-admin</c>), which needs a shell on the server and is its own proof.</item>
/// <item>Nobody changes their own level or removes themselves (a slip would lock them out mid-click).</item>
/// <item>Set up from a link, as a company's people are (§73): password and authenticator saved TOGETHER, only
/// once a code from the app proves it works, so an abandoned set-up leaves nothing half-made. The link is
/// spent by that, never by opening it.</item>
/// <item>After a code, the browser is trusted for 7 days (the password is still asked), bound to the admin's
/// stamp — any change to them voids it, as it signs out every cookie of theirs.</item>
/// <item>The old admin key + authenticator (<see cref="OwnerTwoFactor"/>) stays as the way in when all else
/// fails, with Owner powers, named "Platform owner (admin key)".</item>
/// </list>
/// </summary>
public sealed class PlatformAdmins
{
    public static readonly TimeSpan LinkLife = TimeSpan.FromHours(72);
    public static readonly TimeSpan PendingLife = TimeSpan.FromMinutes(10);
    public const string TrustCookie = "rsp_owner_trust";
    public const string KeyName = "Platform owner (admin key)";

    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly PlatformMailService _mail;
    private readonly AuthService _auth;
    private readonly IDataProtector _secrets;
    private readonly ITimeLimitedDataProtector _trust;
    private readonly ILogger<PlatformAdmins> _log;
    private readonly string _publicUrl;
    public TimeSpan TrustFor { get; }
    /// <summary>What this server is called in the log and on the dashboard: <c>Platform:ServerName</c>, else its address's host.</summary>
    public string ServerName { get; }

    private readonly ConcurrentDictionary<string, SetupPending> _setups = new();
    private readonly ConcurrentDictionary<string, SignPending> _signs = new();
    private readonly ConcurrentDictionary<int, (string? stamp, DateTime at)> _stamps = new();
    private sealed record SetupPending(int AdminId, string LinkHash, string Secret, string Salt, string Hash, DateTime At, int Wrong);
    private sealed record SignPending(int AdminId, string Email, DateTime At, int Wrong);

    public PlatformAdmins(IDbContextFactory<PlatformDb> dbf, PlatformMailService mail, AuthService auth, IDataProtectionProvider dp,
        IConfiguration cfg, ILogger<PlatformAdmins> log)
    {
        _dbf = dbf; _mail = mail; _auth = auth; _log = log;
        _secrets = dp.CreateProtector("RemoteSupport.AdminTotpSecret.v1");
        _trust = dp.CreateProtector("RemoteSupport.AdminTrust.v1").ToTimeLimitedDataProtector();
        _publicUrl = (cfg["Platform:PublicUrl"] ?? "").TrimEnd('/');
        TrustFor = TimeSpan.FromDays(Math.Clamp(cfg.GetValue("Security:TwoFactorTrustDays", 7.0), 0, 30));
        var named = (cfg["Platform:ServerName"] ?? "").Trim();
        ServerName = named.Length > 0 ? named
            : Uri.TryCreate(_publicUrl, UriKind.Absolute, out var u) ? u.Host : Environment.MachineName;
    }

    // =============================================================================================
    // Who may do what — asked by every page and every handler, never worked out in markup
    // =============================================================================================

    /// <summary>Somebody acting: an admin, or the admin key (no id, Owner).</summary>
    public sealed record Actor(int? Id, string Name, AdminLevel Level);

    public static bool MayManage(AdminLevel l) => l >= AdminLevel.Manager;
    public static bool MayRun(AdminLevel l) => l >= AdminLevel.Full;

    public static string Label(AdminLevel l) => l switch
    {
        AdminLevel.Viewer => "Viewer", AdminLevel.Manager => "Manager", AdminLevel.Full => "Full", AdminLevel.Owner => "Owner", _ => l.ToString()
    };

    public static string Describe(AdminLevel l) => l switch
    {
        AdminLevel.Viewer => "sees everything, changes nothing",
        AdminLevel.Manager => "approves, activates and suspends companies; seats, billing; emails them",
        AdminLevel.Full => "all of that, plus Viewer and Manager admins, servers and mail",
        AdminLevel.Owner => "everything, including Full admins and Owners",
        _ => ""
    };

    /// <summary>The levels this actor may give somebody — what the Add form offers.</summary>
    public static IEnumerable<AdminLevel> Grantable(AdminLevel actor) =>
        Enum.GetValues<AdminLevel>().Where(l => MayGrant(actor, l).Length == 0);

    /// <summary>Empty if <paramref name="actor"/> may give somebody <paramref name="level"/>, else why not.</summary>
    public static string MayGrant(AdminLevel actor, AdminLevel level)
    {
        if (!Enum.IsDefined(level)) return "Choose a level.";
        if (actor == AdminLevel.Owner) return "";
        if (actor == AdminLevel.Full) return level <= AdminLevel.Manager ? "" : "Only an Owner can make somebody a Full admin or an Owner.";
        return "Only a Full admin or an Owner can add or change admins.";
    }

    /// <summary>Empty if <paramref name="actor"/> may change or remove <paramref name="target"/> at all, else why not.</summary>
    public static string MayTouch(Actor actor, PlatformAdmin target)
    {
        if (actor.Id == target.Id) return "You cannot change or remove yourself — ask another admin.";
        if (target.Level == AdminLevel.Owner) return "An Owner cannot be changed or removed here.";
        return MayGrant(actor.Level, target.Level);
    }

    // =============================================================================================
    // The list, adding, changing, removing
    // =============================================================================================

    public async Task<List<PlatformAdmin>> AllAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.PlatformAdmins.OrderByDescending(a => a.Level).ThenBy(a => a.Name).ToListAsync();
    }

    public async Task<bool> AnyAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.PlatformAdmins.AnyAsync();
    }

    /// <summary>A set-up link made: the link, whether it was mailed, and why not. <see cref="Error"/> when nothing was made.</summary>
    public sealed record Linked(string Error, string Link = "", bool Mailed = false, string Why = "");

    public async Task<Linked> AddAsync(Actor by, string? name, string? email, AdminLevel level)
    {
        var why = MayGrant(by.Level, level);
        if (why.Length > 0) return new(why);
        name = (name ?? "").Trim(); email = (email ?? "").Trim();
        if (name.Length == 0 || name.Length > 100) return new("Give their name.");
        if (!LooksLikeEmail(email)) return new("Give an email address they read.");
        await using var db = await _dbf.CreateDbContextAsync();
        var lower = email.ToLowerInvariant();
        if (await db.PlatformAdmins.AnyAsync(a => a.Email.ToLower() == lower)) return new($"{email} is already an admin.");
        var admin = new PlatformAdmin { Name = name, Email = email, Level = level, CreatedBy = by.Name, SecurityStamp = NewStamp() };
        db.PlatformAdmins.Add(admin);
        await db.SaveChangesAsync();
        await LogAsync(by.Name, $"Added {name} ({email}) as {Label(level)}");
        return await IssueLinkAsync(admin.Id, by.Name, first: true);
    }

    /// <summary>
    /// A new set-up link — the one control for a lost link, a lost phone and a forgotten password. Their password
    /// and authenticator are cleared and their stamp moved, so whatever was signed in as them is signed out now.
    /// </summary>
    public async Task<Linked> NewLinkAsync(Actor by, int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var a = await db.PlatformAdmins.FindAsync(id);
        if (a == null) return new("That admin no longer exists.");
        var why = MayTouch(by, a);
        if (why.Length > 0) return new(why);
        a.PasswordSalt = ""; a.PasswordHash = ""; a.TotpSecretEnc = ""; a.TotpLastStep = 0; a.SecurityStamp = NewStamp();
        await db.SaveChangesAsync();
        Forget(a.Id);
        await LogAsync(by.Name, $"Sent {a.Name} a new set-up link (their sign-in was reset)");
        return await IssueLinkAsync(a.Id, by.Name, first: false);
    }

    public async Task<string> SetLevelAsync(Actor by, int id, AdminLevel level)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var a = await db.PlatformAdmins.FindAsync(id);
        if (a == null) return "That admin no longer exists.";
        var why = MayTouch(by, a);
        if (why.Length == 0) why = MayGrant(by.Level, level);
        if (why.Length > 0) return why;
        if (a.Level == level) return "";
        var was = a.Level;
        a.Level = level; a.SecurityStamp = NewStamp();   // their cookie carries the old level: they sign in again
        await db.SaveChangesAsync();
        Forget(a.Id);
        await LogAsync(by.Name, $"Changed {a.Name} from {Label(was)} to {Label(level)}");
        return "";
    }

    public async Task<string> RemoveAsync(Actor by, int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var a = await db.PlatformAdmins.FindAsync(id);
        if (a == null) return "That admin no longer exists.";
        var why = MayTouch(by, a);
        if (why.Length > 0) return why;
        db.PlatformAdmins.Remove(a);
        await db.SaveChangesAsync();
        Forget(a.Id);
        await LogAsync(by.Name, $"Removed {a.Name} ({a.Email}), {Label(a.Level)}");
        return "";
    }

    /// <summary>The command line's own: any level, Owners included — it needs a shell on the server.</summary>
    public async Task<string> RemoveByEmailAsync(string email)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var lower = (email ?? "").Trim().ToLowerInvariant();
        var a = await db.PlatformAdmins.FirstOrDefaultAsync(x => x.Email.ToLower() == lower);
        if (a == null) return "No admin has that email address.";
        db.PlatformAdmins.Remove(a);
        await db.SaveChangesAsync();
        await LogAsync("command line", $"Removed {a.Name} ({a.Email}), {Label(a.Level)}");
        return "";
    }

    private async Task<Linked> IssueLinkAsync(int id, string by, bool first)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var a = await db.PlatformAdmins.FindAsync(id);
        if (a == null) return new("That admin no longer exists.");
        var secret = Passwords.RandomToken(32);
        a.LinkHash = Hash(secret); a.LinkExpiresUtc = DateTime.UtcNow.Add(LinkLife);
        await db.SaveChangesAsync();
        _setups.Where(p => p.Value.AdminId == id).ToList().ForEach(p => _setups.TryRemove(p.Key, out _));

        var link = $"{_publicUrl}/owner/setup?t={secret}";
        var until = DateTime.UtcNow.Add(LinkLife).ToString("d MMM yyyy HH:mm") + " UTC";
        var body = new StringBuilder();
        body.Append($"Hello {a.Name},\n\n");
        body.Append(first
            ? $"{by} has made you a platform admin of The Remote Support Program ({Label(a.Level)}: {Describe(a.Level)}).\n\n"
            : $"{by} has sent you a new set-up link for your platform admin account. Your old password and authenticator no longer work.\n\n");
        body.Append("Set up your account — choose a password, then scan a code with an authenticator app on your phone\n");
        body.Append("(Microsoft Authenticator or Google Authenticator):\n\n");
        body.Append($"  {link}\n\n");
        body.Append($"The link works once and expires on {until}.\n\n");
        body.Append($"Afterwards, sign in at {_publicUrl}/owner/login with this email address ({a.Email}).\n\n");
        body.Append("If you were not expecting this, ignore this email.\n");
        var r = await _mail.SendAsync(new[] { a.Email }, "The Remote Support Program: set up your admin account", body.ToString(),
            "The Remote Support Program", null, copyOwner: false);
        if (r != "sent") _log.LogWarning("Admin set-up link for {Email} not mailed: {Why}", a.Email, r);
        return new("", link, r == "sent", r == "sent" ? "" : r);
    }

    // =============================================================================================
    // Setting up from the link
    // =============================================================================================

    /// <summary>Whose a link is, if it can still be used. Opening the page asks this; it spends nothing.</summary>
    public async Task<(PlatformAdmin? who, string error)> LookAsync(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return (null, "This link is not complete. Copy the whole link from the email.");
        await using var db = await _dbf.CreateDbContextAsync();
        var hash = Hash(secret.Trim());
        var a = await db.PlatformAdmins.FirstOrDefaultAsync(x => x.LinkHash == hash);
        if (a == null) return (null, "This link is not valid. It may have been used already, or replaced by a newer one — use the latest email.");
        if (a.LinkExpiresUtc < DateTime.UtcNow) return (null, "This link has expired. Ask the admin who added you to send a new one.");
        return (a, "");
    }

    /// <summary>One step of setting up or signing in, as the pages draw it.</summary>
    public sealed record Step(string Error, string Pending = "", string Secret = "", string Qr = "", PlatformAdmin? Done = null, string Trust = "");

    /// <summary>The password, checked; then the authenticator step. Nothing is saved yet — that waits for the code.</summary>
    public async Task<Step> ChoosePasswordAsync(string? secret, string? password, string? again)
    {
        var (a, error) = await LookAsync(secret);
        if (a == null) return new(error);
        if (password != again) return new("The two passwords are not the same.");
        var problem = Passwords.Problem(password, a.Email);
        if (problem.Length > 0) return new(problem);
        var (salt, hash) = Passwords.Create(password!);
        var totp = Totp.NewSecret();
        var id = Passwords.RandomToken(24);
        _setups[id] = new SetupPending(a.Id, a.LinkHash, totp, salt, hash, DateTime.UtcNow, 0);
        return new("", id, totp, QrFor(a, totp));
    }

    /// <summary>The first code from the app: everything is saved together, the link spent, and they are signed in.</summary>
    public async Task<Step> FinishSetupAsync(string? pending, string? code, string address)
    {
        if (pending == null || !_setups.TryGetValue(pending, out var p) || DateTime.UtcNow - p.At > PendingLife)
        { if (pending != null) _setups.TryRemove(pending, out _); return new("That set-up has expired. Open the link from the email again."); }
        await using var db = await _dbf.CreateDbContextAsync();
        var a = await db.PlatformAdmins.FindAsync(p.AdminId);
        if (a == null || a.LinkHash != p.LinkHash || a.LinkExpiresUtc < DateTime.UtcNow)
        { _setups.TryRemove(pending, out _); return new("This link has been replaced or has expired. Use the latest email."); }
        var step = Totp.Match(p.Secret, code, DateTime.UtcNow);
        if (step == null)
        {
            _auth.Guard.Fail(Account(a.Email), address);
            if (p.Wrong + 1 >= AuthService.CodesPerPending) { _setups.TryRemove(pending, out _); return new("Too many wrong codes. Open the link from the email again."); }
            _setups[pending] = p with { Wrong = p.Wrong + 1 };
            return new("That code is not right. Codes change every 30 seconds — use the one showing now.", pending, p.Secret, QrFor(a, p.Secret));
        }
        _setups.TryRemove(pending, out _);
        a.PasswordSalt = p.Salt; a.PasswordHash = p.Hash; a.TotpSecretEnc = _secrets.Protect(p.Secret); a.TotpLastStep = step.Value;
        a.LinkHash = ""; a.LinkExpiresUtc = null; a.SecurityStamp = NewStamp(); a.LastSignInUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        Forget(a.Id);
        await LogAsync(a.Name, "Set up their admin account");
        return new("", Done: a, Trust: IssueTrust(a));
    }

    // =============================================================================================
    // Signing in
    // =============================================================================================

    /// <summary>
    /// Step one: email and password. A good trust token for this admin (a code given on this browser in the last
    /// 7 days) finishes it; otherwise the code step. Wrong answers count against the account and the address
    /// (<see cref="SignInGuard"/>), and say the same whatever was wrong.
    /// </summary>
    public async Task<Step> PasswordAsync(string? email, string? password, string address, string? trust)
    {
        email = (email ?? "").Trim();
        if (_auth.Guard.AddressWait(address) is > 0 and var refused) return new(SignInGuard.Describe(refused));
        await using var db = await _dbf.CreateDbContextAsync();
        var lower = email.ToLowerInvariant();
        var a = email.Length == 0 ? null : await db.PlatformAdmins.FirstOrDefaultAsync(x => x.Email.ToLower() == lower);
        // A browser this admin typed their code on within the week is counted on its own (SignInGuard, the pentest's
        // F1): a stranger's wrong passwords never lock it out. A known browser never reaches the code step.
        var known = a != null && Trusted(a, trust);
        var wait = _auth.Guard.RetryAfter(Account(email), address, known);
        if (wait > 0) return new(SignInGuard.Describe(wait));
        if (a == null || !a.IsSetUp || !Passwords.Verify(password ?? "", a.PasswordSalt, a.PasswordHash))
        {
            _auth.Guard.Fail(Account(email), address, known);
            return new("That email and password do not match an admin account that is set up.");
        }
        if (Passwords.NeedsUpgrade(a.PasswordSalt)) { (a.PasswordSalt, a.PasswordHash) = Passwords.Create(password!); await db.SaveChangesAsync(); }
        if (known)
        {
            _auth.Guard.Succeeded(Account(email));
            a.LastSignInUtc = DateTime.UtcNow; await db.SaveChangesAsync();
            await LogAsync(a.Name, "Signed in (trusted browser)");
            return new("", Done: a);
        }
        var id = Passwords.RandomToken(24);
        _signs[id] = new SignPending(a.Id, a.Email, DateTime.UtcNow, 0);
        return new("", id);
    }

    /// <summary>Step two: the six digits.</summary>
    public async Task<Step> CodeAsync(string? pending, string? code, string address)
    {
        if (pending == null || !_signs.TryGetValue(pending, out var p) || DateTime.UtcNow - p.At > PendingLife)
        { if (pending != null) _signs.TryRemove(pending, out _); return new("That sign-in has expired. Please start again."); }
        var wait = _auth.Guard.RetryAfter(Account(p.Email), address);
        if (wait > 0) { _signs.TryRemove(pending, out _); return new(SignInGuard.Describe(wait)); }
        await using var db = await _dbf.CreateDbContextAsync();
        var a = await db.PlatformAdmins.FindAsync(p.AdminId);
        if (a == null || !a.IsSetUp) { _signs.TryRemove(pending, out _); return new("That account can no longer sign in."); }
        string secret;
        try { secret = _secrets.Unprotect(a.TotpSecretEnc); }
        catch (CryptographicException) { return new("Your authenticator secret cannot be read on this server. Ask another admin for a new set-up link."); }
        var step = Totp.Match(secret, code, DateTime.UtcNow);
        if (step == null || step.Value <= a.TotpLastStep)
        {
            _auth.Guard.Fail(Account(p.Email), address);
            if (p.Wrong + 1 >= AuthService.CodesPerPending) { _signs.TryRemove(pending, out _); return new("Too many wrong codes. Please start again."); }
            _signs[pending] = p with { Wrong = p.Wrong + 1 };
            return new("That code is not right. Codes change every 30 seconds — use the one showing now.", pending);
        }
        _signs.TryRemove(pending, out _);
        _auth.Guard.Succeeded(Account(p.Email));
        a.TotpLastStep = step.Value; a.LastSignInUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await LogAsync(a.Name, "Signed in");
        return new("", Done: a, Trust: IssueTrust(a));
    }

    public string IssueTrust(PlatformAdmin a) => TrustFor <= TimeSpan.Zero ? "" : _trust.Protect($"{a.Id}|{a.SecurityStamp}", TrustFor);

    private bool Trusted(PlatformAdmin a, string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || TrustFor <= TimeSpan.Zero || a.SecurityStamp.Length == 0) return false;
        try { return _trust.Unprotect(token) == $"{a.Id}|{a.SecurityStamp}"; }
        catch (CryptographicException) { return false; }   // expired, or not ours
    }

    /// <summary>An admin's stamp now (cached 15 s), or null if they are gone — what every cookie of theirs is checked against.</summary>
    public async Task<string?> StampAsync(int id)
    {
        if (_stamps.TryGetValue(id, out var c) && DateTime.UtcNow - c.at < TimeSpan.FromSeconds(15)) return c.stamp;
        await using var db = await _dbf.CreateDbContextAsync();
        var stamp = await db.PlatformAdmins.Where(a => a.Id == id).Select(a => a.SecurityStamp).FirstOrDefaultAsync();
        _stamps[id] = (stamp, DateTime.UtcNow);
        return stamp;
    }

    private void Forget(int id) => _stamps.TryRemove(id, out _);

    // =============================================================================================
    // The log
    // =============================================================================================

    public async Task LogAsync(string who, string what, string company = "", string? server = null)
    {
        try
        {
            await using var db = await _dbf.CreateDbContextAsync();
            db.AdminLog.Add(new AdminLogEntry { Who = who, What = what, Company = company, Server = server ?? ServerName });
            await db.SaveChangesAsync();
            _log.LogInformation("ADMIN {Who}: {What}{Company}", who, what, company.Length > 0 ? " — " + company : "");
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not write the admin log: {Who} {What}", who, what); }
    }

    public async Task<List<AdminLogEntry>> LogPageAsync(int take = 300)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.AdminLog.OrderByDescending(e => e.Id).Take(take).ToListAsync();
    }

    // =============================================================================================

    private static string Account(string email) => "admin:" + email;
    private static string NewStamp() => Passwords.RandomToken(16);
    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    private static string QrFor(PlatformAdmin a, string secret) => QrImage.DataUri(Totp.Uri("The Remote Support Program admin", a.Email, secret));
    private static bool LooksLikeEmail(string e) =>
        e.Length is > 3 and < 200 && e.IndexOf('@') > 0 && e.IndexOf('@') < e.Length - 3 && !e.Contains(' ') && e.Split('@')[1].Contains('.');
}

/// <summary>Who is signed in to <c>/owner</c>, read from the cookie (2026-10-13).</summary>
public static class AdminIdentity
{
    public const string IdClaim = "rsp:admin", LevelClaim = "rsp:admin-level";

    /// <summary>The admin's id, or null for the admin key (and for an owner cookie from before admins).</summary>
    public static int? IdOf(ClaimsPrincipal u) => int.TryParse(u.FindFirst(IdClaim)?.Value, out var id) ? id : null;

    /// <summary>Their level. The admin key, and an owner cookie from before admins, is an Owner.</summary>
    public static AdminLevel LevelOf(ClaimsPrincipal u)
    {
        if (!u.IsInRole("owner")) return 0;
        return int.TryParse(u.FindFirst(LevelClaim)?.Value, out var l) && Enum.IsDefined((AdminLevel)l) ? (AdminLevel)l : AdminLevel.Owner;
    }

    public static PlatformAdmins.Actor ActorOf(ClaimsPrincipal u) =>
        new(IdOf(u), u.Identity?.Name is { Length: > 0 } n && n != "Owner" ? n : PlatformAdmins.KeyName, LevelOf(u));

    public static ClaimsPrincipal Build(PlatformAdmin a) => new(new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.Name, a.Name), new Claim(ClaimTypes.Role, "owner"),
        new Claim(IdClaim, a.Id.ToString()), new Claim(LevelClaim, ((int)a.Level).ToString()),
        new Claim(PortalIdentity.StampClaim, a.SecurityStamp)
    }, "cookie"));
}
