using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Who a token belongs to, including their own light or dark (3.6.9). <see cref="Stamp"/> is the
/// person's security stamp when it was issued and <see cref="Token"/> the console token itself
/// (empty for a portal sign-in) — see <see cref="AuthService"/>.
/// </summary>
public sealed record TechSession(int TenantId, int TechnicianId, string DisplayName, string Email, bool IsAdmin, TenantRole Role, string Appearance = "")
{
    public string Stamp { get; init; } = "";
    public string Token { get; init; } = "";
    /// <summary>
    /// Which sign-in this is (2026-10-05, one at a time): "b:{id}" for a browser — the person's
    /// <c>BrowserSignIn</c> — or "c:{id}" for a console, and for a portal cookie the console's
    /// Tickets button made, which lives exactly as long as that console does.
    /// </summary>
    public string SignIn { get; init; } = "";
    /// <summary>The machine it came from: the console's or browser's own random id, "" if it sent none.</summary>
    public string Device { get; init; } = "";
}

/// <summary>Why a portal cookie was refused, so the sign-in page can say so.</summary>
public enum CookieVerdict { Good, Changed, Elsewhere }

/// <summary>Where a sign-in has got to.</summary>
public enum SignInStep
{
    /// <summary>Signed in: <see cref="SignInResult.Session"/> and, for the console, a token.</summary>
    Done,
    /// <summary>Password right; now the six digits from their authenticator app.</summary>
    Code,
    /// <summary>Password right, and no authenticator set up yet: show the QR code, then ask for a code.</summary>
    Enrol,
    /// <summary>Not signed in; <see cref="SignInResult.Error"/> says why.</summary>
    Refused
}

public sealed record SignInResult(SignInStep Step, string Error = "", TechSession? Session = null, string Token = "",
    string Pending = "", string Secret = "", string OtpUri = "", IReadOnlyList<string>? RecoveryCodes = null, string Trust = "")
{
    /// <summary>
    /// Not an error: why the code is being asked even though this machine is trusted — the person
    /// is signed in somewhere else, and this sign-in will sign that one out.
    /// </summary>
    public string Notice { get; init; } = "";

    public static SignInResult No(string error) => new(SignInStep.Refused, error);
}

/// <summary>
/// Signing in, for the console and the portal alike, and the console's tokens.
///
/// <para><b>Rewritten 2026-10-01, before go-live (HANDOVER §72).</b> What it was: a password, then
/// a token that lived until the server restarted — kept even if the person was removed, their
/// password changed, or their account disabled; and a portal cookie that renewed itself for as long
/// as it was used. With unattended access and scripts that run as SYSTEM, one technician's
/// password was every customer machine of that company, and a sacked technician kept it.</para>
///
/// <para>What it is now:</para>
/// <list type="bullet">
/// <item><b>Two steps.</b> The password, then a code from an authenticator app. Somebody with no
/// app set up is shown a QR code at their next sign-in and cannot get in without finishing it
/// (<c>Security:RequireTwoFactor</c>, on unless a harness turns it off for a platform of its
/// own). Ten recovery codes, each good once, are the way in with a lost phone.</item>
/// <item><b>Guessing stops</b> — <see cref="SignInGuard"/>.</item>
/// <item><b>The security stamp.</b> Every token and portal cookie carries the person's stamp from
/// when it was issued. Change their password, boxes or two-factor, or remove them, and the stamp
/// changes; anything carrying the old one is signed out — the console at once, by the hub
/// (<see cref="SignedOut"/>), and the portal on its next page. A sweep every 15 s catches changes
/// made from the server's command line, which runs in a different process.</item>
/// <item><b>Tokens expire</b> after <c>Security:TokenIdleHours</c> (12) unused. A running console
/// pings every 15 s and so never expires; a token copied off a laptop that was then closed does.</item>
/// <item><b>A new place</b> — a network this person has never signed in from — is mailed to them,
/// from the platform mailbox, when there is one.</item>
/// <item><b>One console and one browser at a time</b> (2026-10-05, Clint: <i>"If they log into a
/// second machine it must automatically log them out of the first machine and request a code again
/// incase it someone trying to hack the system."</i>). A console sign-in closes every other console
/// of theirs; a browser sign-in signs every other browser out on its next page. When the one pushed
/// off is on a DIFFERENT machine, the code is asked even on a trusted machine, and
/// <c>TrustStamp</c> moves so every trusted machine asks again. The same machine signing in again
/// (a restart, Sign out and back) is not "somewhere else": each console and browser sends a random
/// id of its own. HANDOVER §82.</item>
/// </list>
/// </summary>
public sealed class AuthService : IDisposable
{
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly IDataProtector _secrets;
    private readonly PlatformMailService? _mail;
    private readonly ILogger<AuthService>? _log;
    private readonly ConcurrentDictionary<string, Live> _tokens = new();
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private readonly ConcurrentDictionary<int, (string stamp, string browser, bool ok, DateTime at)> _stampCache = new();
    private readonly Timer _sweep;

    public SignInGuard Guard { get; } = new();
    public bool RequireTwoFactor { get; }
    public TimeSpan TokenIdle { get; }
    /// <summary>
    /// How long a code, once given, is trusted on that browser or console (2026-10-02 — Clint: "sign
    /// in requires two factor authentication on every sign in. can we change that to require
    /// authentication every 7 days?"). <c>Security:TwoFactorTrustDays</c>, 7; 0 asks every time.
    /// The PASSWORD is still asked every time; only the code is skipped. See <see cref="IssueTrust"/>.
    /// </summary>
    public TimeSpan TrustFor { get; }
    private readonly ITimeLimitedDataProtector _trust;

    /// <summary>A pending sign-in (password done, code not yet) lasts this long.</summary>
    public static readonly TimeSpan PendingLife = TimeSpan.FromMinutes(5);
    /// <summary>Wrong codes allowed on one pending sign-in before it is thrown away.</summary>
    public const int CodesPerPending = 5;
    public const int RecoveryCodeCount = 10;

    /// <summary>
    /// Somebody was signed out everywhere: tenant, person, display name, the reason in words. The
    /// hub closes their console and any session window they had open.
    /// </summary>
    public event Action<int, int, string, string>? SignedOut;

    /// <summary>
    /// This person signed in on another console: tenant, person, the NEW console's token (kept),
    /// display name, the reason in words. The hub closes every other console of theirs and the
    /// session windows those had open.
    /// </summary>
    public event Action<int, int, string, string, string>? ConsoleReplaced;

    /// <summary>Why the other sign-in was closed — shown on the console that was pushed off, and on the browser's sign-in page.</summary>
    public const string ElsewhereReason =
        "You were signed out because your account was just signed in on another computer. " +
        "If that was not you, change your password now and tell your supervisor.";

    /// <summary>The same PC signed in to the console again — a second copy started — so this one closes.</summary>
    public const string SamePcReason = "You signed in to the console again on this computer, so this window was closed. Only one console can be signed in at a time.";

    private sealed class Live { public TechSession S = null!; public DateTime LastUsedUtc; }
    private sealed class Pending
    {
        public int TechnicianId; public string Email = ""; public DateTime CreatedUtc;
        public string Secret = "";   // set when this is an enrolment
        public int WrongCodes; public string Channel = ""; public string Device = "";
        public bool Known;           // the password came from a known device (SignInGuard)
    }

    public AuthService(IDbContextFactory<PlatformDb> dbf, IDataProtectionProvider dp, IConfiguration cfg,
        PlatformMailService? mail = null, ILogger<AuthService>? log = null)
    {
        _dbf = dbf; _mail = mail; _log = log;
        _secrets = dp.CreateProtector("RemoteSupport.TotpSecret.v1");
        RequireTwoFactor = cfg.GetValue("Security:RequireTwoFactor", true);
        TokenIdle = TimeSpan.FromHours(Math.Max(1, cfg.GetValue("Security:TokenIdleHours", 12)));
        TrustFor = TimeSpan.FromDays(Math.Clamp(cfg.GetValue("Security:TwoFactorTrustDays", 7.0), 0, 30));
        _trust = dp.CreateProtector("RemoteSupport.TrustedDevice.v1").ToTimeLimitedDataProtector();
        _sweep = new Timer(_ => _ = SweepAsync(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public void Dispose() => _sweep.Dispose();

    // =========================================================================================
    // Signing in
    // =========================================================================================

    /// <summary>
    /// Step one: the email and password. <paramref name="code"/> may come in the same request (the
    /// harnesses do that); a person is asked for it as a second step. <paramref name="channel"/>
    /// is "console" or "browser"; <paramref name="device"/> is that console's or browser's own
    /// random id, which says whether a sign-in already held is on this machine or another.
    /// </summary>
    public async Task<SignInResult> PasswordAsync(string email, string password, string? code, string address, string channel,
        IEnumerable<string>? trust = null, string? device = null)
    {
        device = CleanDevice(device);
        email = (email ?? "").Trim().ToLowerInvariant();
        // The address first, so a refused address asks nothing of the database.
        if (Guard.AddressWait(address) is > 0 and var refused) return SignInResult.No(SignInGuard.Describe(refused));

        await using var db = await _dbf.CreateDbContextAsync();
        var tech = await db.Technicians.Include(t => t.Tenant).FirstOrDefaultAsync(t => t.Email == email);
        // A browser or console this person typed their code on within the week is counted on its own
        // (SignInGuard, the pentest's F1): a stranger's wrong passwords never lock it out.
        var known = tech != null && Trusted(tech, trust);
        var wait = Guard.RetryAfter(email, address, known);
        if (wait > 0) return SignInResult.No(SignInGuard.Describe(wait));
        // AwaitingSetup: invited, no password chosen yet (AccountLinks) — nothing can match it.
        if (tech == null || tech.Disabled || tech.AwaitingSetup || !Passwords.Verify(password ?? "", tech.Salt, tech.Hash))
        {
            Guard.Fail(email, address, known);
            return SignInResult.No("Invalid email or password.");
        }
        // The password is proved and in hand — the one moment an old hash can be made again at today's rounds
        // (Passwords.Rounds, 2026-10-07). The security stamp is NOT moved: nothing about the person changed, and
        // moving it would sign them out everywhere for having signed in.
        if (Passwords.NeedsUpgrade(tech.Salt))
        {
            (tech.Salt, tech.Hash) = Passwords.Create(password!);
            await db.SaveChangesAsync();
        }
        if (!tech.Tenant.IsServiceEnabled)
            return SignInResult.No(tech.Tenant.Status switch
            {
                TenantStatus.Pending => "Your signup is awaiting approval. We'll email you as soon as it's ready.",
                TenantStatus.Trial => "Your 14-day trial has ended. Please contact us to activate your subscription.",
                TenantStatus.Rejected => "This signup was not approved. Please contact support.",
                _ => "This account is suspended. Please contact support."
            });

        if (tech.HasTwoFactor)
        {
            if (!string.IsNullOrWhiteSpace(code))
            {
                if (await CheckCodeAsync(db, tech, code)) return await CompleteAsync(db, tech, address, channel, device, codeGiven: true);
                Guard.Fail(email, address, known);
                return SignInResult.No("That code is not right. Codes change every 30 seconds — use the one showing now.");
            }
            // Signed in on another machine: the code, even here — this could be somebody else
            // with the password and a machine the person once trusted.
            if (Elsewhere(tech, channel, device))
                return new SignInResult(SignInStep.Code, Pending: NewPending(tech, "", channel, device, known)) { Notice = ElsewhereNotice(channel) };
            // A code given here within the last TrustFor: the password alone. The trust is NOT
            // renewed by this, so the code is asked again when it runs out — every seven days.
            if (Trusted(tech, trust)) return await CompleteAsync(db, tech, address, channel, device);
            return new SignInResult(SignInStep.Code, Pending: NewPending(tech, "", channel, device));
        }

        if (!RequireTwoFactor) return await CompleteAsync(db, tech, address, channel, device);

        var secret = Totp.NewSecret();
        return new SignInResult(SignInStep.Enrol, Pending: NewPending(tech, secret, channel, device),
            Secret: secret, OtpUri: Totp.Uri(tech.Tenant.CompanyName, tech.Email, secret));
    }

    private static string ElsewhereNotice(string channel) => channel == "console"
        ? "Your account is signed in to the console on another computer. Type your code to sign in here — the other one will be signed out."
        : "Your account is signed in on another browser. Type your code to sign in here — the other one will be signed out.";

    /// <summary>A device id as sent: random letters and digits, never more than 64. Anything else is "none".</summary>
    public static string CleanDevice(string? device)
    {
        var d = (device ?? "").Trim();
        return d.Length is > 0 and <= 64 && d.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? d : "";
    }

    /// <summary>
    /// Would this sign-in push this person off ANOTHER machine? A console: another console of theirs
    /// is signed in with a different id (or either sent none). A browser: another browser holds
    /// their browser sign-in. The same machine signing in again is not somewhere else.
    /// </summary>
    private bool Elsewhere(Technician tech, string channel, string device)
    {
        if (channel == "console")
        {
            var now = DateTime.UtcNow;
            return _tokens.Values.Any(l => l.S.TechnicianId == tech.Id && l.S.SignIn.StartsWith("c:")
                && now - l.LastUsedUtc <= TokenIdle && (device.Length == 0 || l.S.Device != device));
        }
        return tech.BrowserSignIn.Length > 0 && (device.Length == 0 || tech.BrowserDevice != device);
    }

    /// <summary>Step two for somebody with an authenticator: the six digits, or a recovery code.</summary>
    public async Task<SignInResult> CodeAsync(string pendingId, string code, string address)
    {
        var p = TakeLivePending(pendingId, enrol: false);
        if (p == null) return SignInResult.No("That sign-in has expired. Please start again.");
        var wait = Guard.RetryAfter(p.Email, address, p.Known);
        if (wait > 0) { _pending.TryRemove(pendingId, out _); return SignInResult.No(SignInGuard.Describe(wait)); }

        await using var db = await _dbf.CreateDbContextAsync();
        var tech = await db.Technicians.Include(t => t.Tenant).FirstOrDefaultAsync(t => t.Id == p.TechnicianId);
        if (tech == null || tech.Disabled || !tech.Tenant.IsServiceEnabled)
        { _pending.TryRemove(pendingId, out _); return SignInResult.No("That account can no longer sign in."); }

        if (await CheckCodeAsync(db, tech, code))
        {
            _pending.TryRemove(pendingId, out _);
            return await CompleteAsync(db, tech, address, p.Channel, p.Device, codeGiven: true);
        }
        return WrongCode(pendingId, p, address, SignInStep.Code);
    }

    /// <summary>
    /// Step two for somebody setting up their authenticator: a code from the app proves it was set
    /// up right, and only then is the secret saved. The recovery codes come back once, here, and
    /// are never shown again.
    /// </summary>
    public async Task<SignInResult> EnrolAsync(string pendingId, string code, string address)
    {
        var p = TakeLivePending(pendingId, enrol: true);
        if (p == null) return SignInResult.No("That sign-in has expired. Please start again.");
        var wait = Guard.RetryAfter(p.Email, address, p.Known);
        if (wait > 0) { _pending.TryRemove(pendingId, out _); return SignInResult.No(SignInGuard.Describe(wait)); }

        await using var db = await _dbf.CreateDbContextAsync();
        var tech = await db.Technicians.Include(t => t.Tenant).FirstOrDefaultAsync(t => t.Id == p.TechnicianId);
        if (tech == null || tech.Disabled || !tech.Tenant.IsServiceEnabled)
        { _pending.TryRemove(pendingId, out _); return SignInResult.No("That account can no longer sign in."); }

        var step = Totp.Match(p.Secret, code, DateTime.UtcNow);
        if (step == null) return WrongCode(pendingId, p, address, SignInStep.Enrol, p.Secret, Totp.Uri(tech.Tenant.CompanyName, tech.Email, p.Secret));

        _pending.TryRemove(pendingId, out _);
        var codes = NewRecoveryCodes();
        tech.TotpSecretEnc = _secrets.Protect(p.Secret);
        tech.TotpEnabledUtc = DateTime.UtcNow;
        tech.TotpLastStep = step.Value;
        tech.RecoveryCodeHashes = string.Join(",", codes.Select(HashRecovery));
        // Anything signed in on the password alone, before this, is signed out by it.
        tech.SecurityStamp = NewStamp();
        var done = await CompleteAsync(db, tech, address, p.Channel, p.Device, codeGiven: true);
        _log?.LogInformation("[{Tenant}] {Email} set up two-factor sign-in", tech.Tenant.CompanyName, tech.Email);
        return done with { RecoveryCodes = codes };
    }

    private SignInResult WrongCode(string pendingId, Pending p, string address, SignInStep step, string secret = "", string uri = "")
    {
        Guard.Fail(p.Email, address, p.Known);
        if (++p.WrongCodes >= CodesPerPending)
        {
            _pending.TryRemove(pendingId, out _);
            return SignInResult.No("Too many wrong codes. Please start again.");
        }
        return new SignInResult(step, "That code is not right. Codes change every 30 seconds — use the one showing now.",
            Pending: pendingId, Secret: secret, OtpUri: uri);
    }

    private string NewPending(Technician tech, string secret, string channel, string device, bool known = false)
    {
        var id = Passwords.RandomToken(24);
        _pending[id] = new Pending { TechnicianId = tech.Id, Email = tech.Email, CreatedUtc = DateTime.UtcNow, Secret = secret, Channel = channel, Device = device, Known = known };
        return id;
    }

    private Pending? TakeLivePending(string? id, bool enrol)
    {
        if (id == null || !_pending.TryGetValue(id, out var p)) return null;
        if (DateTime.UtcNow - p.CreatedUtc > PendingLife || (p.Secret.Length > 0) != enrol) { _pending.TryRemove(id, out _); return null; }
        return p;
    }

    /// <summary>
    /// The six digits (each 30-second step accepted once, ever) or a recovery code (crossed off
    /// as it is used). Saves what it changes.
    /// </summary>
    private async Task<bool> CheckCodeAsync(PlatformDb db, Technician tech, string? code)
    {
        var typed = (code ?? "").Trim();
        if (typed.Any(char.IsLetter))
        {
            var hash = HashRecovery(typed);
            var left = tech.RecoveryCodeHashes.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (!left.Remove(hash)) return false;
            tech.RecoveryCodeHashes = string.Join(",", left);
            await db.SaveChangesAsync();
            _log?.LogWarning("[{Tenant}] {Email} signed in with a recovery code; {Left} left", tech.Tenant.CompanyName, tech.Email, left.Count);
            return true;
        }
        string secret;
        try { secret = _secrets.Unprotect(tech.TotpSecretEnc); }
        catch (CryptographicException) { _log?.LogError("{Email}: the stored authenticator secret could not be decrypted (data/keys lost?)", tech.Email); return false; }
        var step = Totp.Match(secret, typed, DateTime.UtcNow);
        if (step == null || step.Value <= tech.TotpLastStep) return false;
        tech.TotpLastStep = step.Value;
        await db.SaveChangesAsync();
        return true;
    }

    private async Task<SignInResult> CompleteAsync(PlatformDb db, Technician tech, string address, string channel, string device, bool codeGiven = false)
    {
        Guard.Succeeded(tech.Email);
        if (tech.SecurityStamp.Length == 0) tech.SecurityStamp = NewStamp();
        tech.LastLoginUtc = DateTime.UtcNow;
        var mailTo = NoteSignInPlace(tech, address);

        // One at a time. Pushing somebody off ANOTHER machine voids every machine's 7-day trust,
        // so the code is asked again everywhere — the trust issued below is under the new value.
        bool console = channel == "console";
        bool elsewhere = Elsewhere(tech, channel, device);
        if (elsewhere)
        {
            tech.TrustStamp = NewStamp();
            _log?.LogInformation("[{Tenant}] {Email} signed in on another {What}; the other is signed out and the code will be asked again",
                tech.Tenant.CompanyName, tech.Email, console ? "console" : "browser");
        }
        string signIn;
        if (console) signIn = "c:" + NewStamp();
        else { tech.BrowserSignIn = NewStamp(); tech.BrowserDevice = device; signIn = "b:" + tech.BrowserSignIn; }
        await db.SaveChangesAsync();
        if (mailTo) _ = MailNewPlaceAsync(tech.Email, tech.DisplayName, tech.Tenant.CompanyName, address, channel);

        var token = Passwords.RandomToken();
        var s = new TechSession(tech.TenantId, tech.Id, tech.DisplayName, tech.Email, tech.IsAdmin, tech.Role, tech.Appearance)
        { Stamp = tech.SecurityStamp, Token = token, SignIn = signIn, Device = device };
        _tokens[token] = new Live { S = s, LastUsedUtc = DateTime.UtcNow };
        _stampCache[tech.Id] = (tech.SecurityStamp, tech.BrowserSignIn, true, DateTime.UtcNow);
        if (console) ReplaceConsoles(tech.TenantId, tech.Id, token, elsewhere ? ElsewhereReason : SamePcReason);
        return new SignInResult(SignInStep.Done, Session: s, Token: token, Trust: codeGiven ? IssueTrust(tech) : "");
    }

    /// <summary>
    /// Every console token of this person's but <paramref name="keep"/> is revoked, and the hub
    /// closes those consoles, saying why. Same machine or not: a console started twice is still one.
    /// </summary>
    private void ReplaceConsoles(int tenantId, int technicianId, string keep, string why)
    {
        string name = "";
        foreach (var kv in _tokens)
            if (kv.Key != keep && kv.Value.S.TechnicianId == technicianId && kv.Value.S.SignIn.StartsWith("c:")
                && _tokens.TryRemove(kv.Key, out var gone)) name = gone.S.DisplayName;
        if (name.Length == 0) return;
        try { ConsoleReplaced?.Invoke(tenantId, technicianId, keep, name, why); }
        catch (Exception ex) { _log?.LogWarning(ex, "ConsoleReplaced handler threw"); }
    }

    /// <summary>
    /// The console is closing (Exit or Sign out): its token goes, so signing in on another computer
    /// afterwards is not "somewhere else" — nobody is pushed off, and the trust stands.
    /// </summary>
    public void ConsoleSignedOut(string? token)
    {
        if (token != null && _tokens.TryGetValue(token, out var live) && live.S.SignIn.StartsWith("c:")) _tokens.TryRemove(token, out _);
    }

    /// <summary>
    /// The browser holding this person's browser sign-in pressed Sign out: the slot is empty, so the
    /// next browser sign-in anywhere is not "somewhere else".
    /// </summary>
    public async Task BrowserSignedOutAsync(int technicianId, string signIn)
    {
        if (technicianId <= 0 || !signIn.StartsWith("b:")) return;
        var id = signIn[2..];
        await using var db = await _dbf.CreateDbContextAsync();
        var tech = await db.Technicians.FirstOrDefaultAsync(t => t.Id == technicianId);
        if (tech == null || tech.BrowserSignIn != id) return;
        tech.BrowserSignIn = ""; tech.BrowserDevice = "";
        await db.SaveChangesAsync();
        _stampCache.TryRemove(technicianId, out _);
    }

    // =========================================================================================
    // Trusting a browser or console for TrustFor after a code (2026-10-02)
    // =========================================================================================

    /// <summary>
    /// What a browser (cookie) or console (a file only that Windows user can read) keeps after a
    /// code: who, and their security stamp at the time, sealed by the server's own Data Protection
    /// keys and dated to run out after <see cref="TrustFor"/>. It is not a way in on its own — the
    /// password is still needed — and anything that changes the stamp voids it at once: a password
    /// change, Reset two-factor, Sign out on the People page, removal, a change of boxes.
    /// </summary>
    public string IssueTrust(Technician tech) =>
        TrustFor <= TimeSpan.Zero || !tech.HasTwoFactor ? "" : _trust.Protect($"{tech.Id}|{TrustKey(tech)}", TrustFor);

    /// <summary>
    /// What a trust token is bound to: the security stamp, and the trust stamp a sign-in on another
    /// machine moves (2026-10-05). With no trust stamp yet it is the stamp alone — exactly what
    /// every token issued before this carried, so none of them is voided by the change itself.
    /// </summary>
    private static string TrustKey(Technician tech) => tech.SecurityStamp + tech.TrustStamp;

    /// <summary>Whose a trust token is, if it is still good — so a browser keeps one per person.</summary>
    public int? TrustOwner(string token)
    {
        try { var parts = _trust.Unprotect(token).Split('|', 2); return int.TryParse(parts[0], out var id) ? id : null; }
        catch (CryptographicException) { return null; }   // expired, or not ours
    }

    private bool Trusted(Technician tech, IEnumerable<string>? tokens)
    {
        if (tokens == null || TrustFor <= TimeSpan.Zero || tech.SecurityStamp.Length == 0) return false;
        foreach (var t in tokens.Where(t => !string.IsNullOrWhiteSpace(t)).Take(10))
        {
            try
            {
                var parts = _trust.Unprotect(t.Trim()).Split('|', 2);
                if (parts.Length == 2 && parts[0] == tech.Id.ToString() && parts[1] == TrustKey(tech)) return true;
            }
            catch (CryptographicException) { }
        }
        return false;
    }

    // =========================================================================================
    // Tokens, stamps, signing out
    // =========================================================================================

    /// <summary>The person behind a console token, or null if it is unknown, expired or signed out. Using it keeps it alive.</summary>
    public TechSession? FromToken(string? token)
    {
        if (token == null || !_tokens.TryGetValue(token, out var live)) return null;
        var now = DateTime.UtcNow;
        if (now - live.LastUsedUtc > TokenIdle) { _tokens.TryRemove(token, out _); return null; }
        live.LastUsedUtc = now;
        return live.S;
    }

    /// <summary>The console is still there (its ping) — keeps its token from expiring.</summary>
    public void Touch(string? token)
    {
        if (token != null && _tokens.TryGetValue(token, out var live)) live.LastUsedUtc = DateTime.UtcNow;
    }

    public void Revoke(string token) => _tokens.TryRemove(token, out _);

    public static string NewStamp() => Passwords.RandomToken(16);

    /// <summary>
    /// Signs this person out of every console and session at once. The portal follows on its next
    /// page, because the stamp has changed (the caller changed it) — or because the row is gone.
    /// </summary>
    public void SignOutEverywhere(int tenantId, int technicianId, string why)
    {
        _stampCache.TryRemove(technicianId, out _);
        string name = "";
        foreach (var kv in _tokens)
            if (kv.Value.S.TechnicianId == technicianId && _tokens.TryRemove(kv.Key, out var gone)) name = gone.S.DisplayName;
        foreach (var kv in _pending)
            if (kv.Value.TechnicianId == technicianId) _pending.TryRemove(kv.Key, out _);
        if (name.Length == 0) return;
        _log?.LogInformation("Signed {Name} out everywhere: {Why}", name, why);
        try { SignedOut?.Invoke(tenantId, technicianId, name, why); } catch (Exception ex) { _log?.LogWarning(ex, "SignedOut handler threw"); }
    }

    /// <summary>
    /// Something about this person changed in THIS process (a page, the hub): check them now rather
    /// than at the next sweep. Signs them out if their stamp moved or they are gone.
    /// </summary>
    public Task RecheckAsync(int technicianId) { _stampCache.TryRemove(technicianId, out _); return SweepAsync(technicianId); }

    /// <summary>
    /// For the portal cookie, on every request: does the stamp it carries still match the person,
    /// and is it still their one browser? Cached for 15 s, so a page with ten images is one database
    /// read, and a change made anywhere is felt within 15 s at most — at once for a change made
    /// through this process.
    ///
    /// <para>The sign-in it carries (2026-10-05): "b:{id}" must be the person's browser sign-in now;
    /// "c:{id}" — made by the console's Tickets button — needs that console still signed in; none at
    /// all (a cookie from before this) is good only until the person signs in on a browser again.</para>
    /// </summary>
    public async Task<CookieVerdict> CookieCheckAsync(int technicianId, string stamp, string signIn)
    {
        if (technicianId <= 0) return CookieVerdict.Changed;
        var now = DateTime.UtcNow;
        if (!_stampCache.TryGetValue(technicianId, out var c) || now - c.at > TimeSpan.FromSeconds(15))
        {
            await using var db = await _dbf.CreateDbContextAsync();
            var row = await db.Technicians.Where(t => t.Id == technicianId)
                .Select(t => new { t.SecurityStamp, t.BrowserSignIn, t.Disabled, t.Tenant.Status, t.Tenant.TrialEndsUtc }).FirstOrDefaultAsync();
            // A company switched off (suspended, closed, a trial run out) is signed out of the portal too (2026-10-13):
            // before, a portal page already open kept working until its person signed out.
            c = row == null ? ("", "", false, now) : (row.SecurityStamp, row.BrowserSignIn, !row.Disabled && Serviceable(row.Status, row.TrialEndsUtc), now);
            _stampCache[technicianId] = c;
        }
        if (!c.ok || c.stamp != (stamp ?? "")) return CookieVerdict.Changed;
        signIn ??= "";
        if (signIn.StartsWith("b:")) return signIn[2..] == c.browser ? CookieVerdict.Good : CookieVerdict.Elsewhere;
        if (signIn.StartsWith("c:"))
            return _tokens.Values.Any(l => l.S.TechnicianId == technicianId && l.S.SignIn == signIn && now - l.LastUsedUtc <= TokenIdle)
                ? CookieVerdict.Good : CookieVerdict.Elsewhere;
        return c.browser.Length == 0 ? CookieVerdict.Good : CookieVerdict.Elsewhere;
    }

    /// <summary>
    /// A company can no longer use the service (2026-10-13, AdminCompanies): every console sign-in of its people is
    /// void now, and every portal cookie is checked afresh on its next page rather than after the 15-second cache.
    /// The hub has already told and closed the consoles (TenantHub.TurnAway); this stops one coming back on its token.
    /// </summary>
    public void SignOutCompany(int tenantId)
    {
        foreach (var kv in _tokens)
            if (kv.Value.S.TenantId == tenantId) _tokens.TryRemove(kv.Key, out _);
        _stampCache.Clear();
    }

    /// <summary><see cref="Tenant.IsServiceEnabled"/>, from the two columns a query can carry.</summary>
    private static bool Serviceable(TenantStatus status, DateTime? trialEndsUtc) =>
        status == TenantStatus.Active || (status == TenantStatus.Trial && !(trialEndsUtc.HasValue && trialEndsUtc.Value < DateTime.UtcNow));

    /// <summary>
    /// Every live token checked against the database; anyone whose stamp moved, who is gone or disabled, or whose
    /// company can no longer use the service (2026-10-13 — a suspension from the command line is felt here), is signed out.
    /// </summary>
    private async Task SweepAsync(int? only = null)
    {
        try
        {
            var now = DateTime.UtcNow;
            foreach (var kv in _tokens) if (now - kv.Value.LastUsedUtc > TokenIdle) _tokens.TryRemove(kv.Key, out _);
            foreach (var kv in _pending) if (now - kv.Value.CreatedUtc > PendingLife) _pending.TryRemove(kv.Key, out _);

            var held = _tokens.Values.Select(l => l.S).Where(s => only == null || s.TechnicianId == only)
                .GroupBy(s => s.TechnicianId).ToDictionary(g => g.Key, g => g.First());
            if (held.Count == 0) return;
            await using var db = await _dbf.CreateDbContextAsync();
            var ids = held.Keys.ToList();
            var rows = await db.Technicians.Where(t => ids.Contains(t.Id))
                .Select(t => new { t.Id, t.SecurityStamp, t.Disabled, t.Tenant.Status, t.Tenant.TrialEndsUtc }).ToDictionaryAsync(t => t.Id);
            foreach (var (id, s) in held)
            {
                string? why = !rows.TryGetValue(id, out var r) ? "Your account has been removed."
                    : r.Disabled ? "Your account has been disabled."
                    : !Serviceable(r.Status, r.TrialEndsUtc) ? "Your company's account is not active. Please contact support."
                    : r.SecurityStamp != s.Stamp ? "Your password, roles or two-factor sign-in were changed. Please sign in again."
                    : null;
                if (why != null) SignOutEverywhere(s.TenantId, id, why);
            }
        }
        catch (Exception ex) { _log?.LogWarning(ex, "sign-in sweep failed"); }
    }

    // =========================================================================================
    // Recovery codes, new places
    // =========================================================================================

    /// <summary>Ten codes like K7QM-2XPA-9RTD: 60 random bits each, in letters and digits nobody misreads.</summary>
    public static List<string> NewRecoveryCodes()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // no 0/O, 1/I
        var list = new List<string>();
        for (int i = 0; i < RecoveryCodeCount; i++)
        {
            var bytes = RandomNumberGenerator.GetBytes(12);
            var chars = bytes.Select(b => alphabet[b % alphabet.Length]).ToArray();
            list.Add($"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}-{new string(chars, 8, 4)}");
        }
        return list;
    }

    /// <summary>Forgiving of how it is typed: case, spaces and dashes do not matter.</summary>
    public static string HashRecovery(string code)
    {
        var norm = new string(code.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(norm)));
    }

    /// <summary>
    /// The network a sign-in came from: the first three parts of an IPv4 address, the first three
    /// groups of an IPv6 one. A home router's address changes within its range; a different place
    /// does not share one.
    /// </summary>
    public static string PlaceOf(string address)
    {
        if (!System.Net.IPAddress.TryParse(address, out var ip)) return address;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var s = ip.ToString();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) return string.Join('.', s.Split('.').Take(3));
        return string.Join(':', ip.GetAddressBytes().Chunk(2).Take(3).Select(c => ((c[0] << 8) | c[1]).ToString("x")));
    }

    /// <summary>
    /// Records where this sign-in came from and says whether to mail about it. The very first
    /// sign-in after this feature arrived is only recorded — otherwise go-live would mail everybody.
    /// </summary>
    private static bool NoteSignInPlace(Technician tech, string address)
    {
        if (address.Length == 0 || address == "?") return false;
        var place = PlaceOf(address);
        var known = tech.SignInPlaces.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        bool isNew = known.Count > 0 && !known.Contains(place);
        known.Remove(place);
        known.Insert(0, place);
        tech.SignInPlaces = string.Join(",", known.Take(10));
        return isNew;
    }

    private async Task MailNewPlaceAsync(string email, string name, string company, string address, string channel)
    {
        if (_mail == null) return;
        try
        {
            var how = channel == "console" ? "the technician console" : "a web browser";
            var r = await _mail.SendAsync(new[] { email }, $"New sign-in to your {company} account", NewPlaceBody(name, company, address, how), company, null, copyOwner: false);
            if (r != "sent") _log?.LogInformation("New-place mail to {Email} not sent: {Why}", email, r);
        }
        catch (Exception ex) { _log?.LogWarning(ex, "New-place mail to {Email} failed", email); }
    }

    private static string NewPlaceBody(string name, string company, string address, string how) =>
        $"Hello {name},\n\n" +
        $"Your {company} account was just signed in to from a network it has not been used from before.\n\n" +
        $"  When:  {DateTime.UtcNow:d MMM yyyy HH:mm} UTC\n" +
        $"  From:  {address}\n" +
        $"  Using: {how}\n\n" +
        "If this was you, there is nothing to do.\n\n" +
        "If it was NOT you, change your password now and tell your supervisor, who can sign you out " +
        "everywhere and reset your two-factor sign-in from the People page.\n";

    // =========================================================================================
    // Changing your own password (2026-10-01)
    // =========================================================================================

    /// <summary>
    /// The person signed in changes their own password: the current one, the new one twice, and a
    /// code — so a browser left open on somebody's desk cannot be used to lock them out. Moves the
    /// stamp, which signs out every OTHER console and browser; returns the new stamp so the page can
    /// keep THIS browser signed in. Empty error is success.
    /// </summary>
    public async Task<(string error, string stamp)> ChangePasswordAsync(int technicianId, string? current, string? password, string? again, string? code, string address)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var tech = await db.Technicians.Include(t => t.Tenant).FirstOrDefaultAsync(t => t.Id == technicianId);
        if (tech == null || tech.Disabled) return ("That account no longer exists.", "");
        var wait = Guard.RetryAfter(tech.Email, address);
        if (wait > 0) return (SignInGuard.Describe(wait), "");
        if (!Passwords.Verify(current ?? "", tech.Salt, tech.Hash)) { Guard.Fail(tech.Email, address); return ("Your current password is not right.", ""); }
        if (tech.HasTwoFactor && !await CheckCodeAsync(db, tech, code)) { Guard.Fail(tech.Email, address); return ("That code is not right. Codes change every 30 seconds — use the one showing now.", ""); }
        if (password != again) return ("The two new passwords are not the same.", "");
        var problem = Passwords.Problem(password, tech.Email);
        if (problem.Length > 0) return (problem, "");
        (tech.Salt, tech.Hash) = Passwords.Create(password!);
        tech.SecurityStamp = NewStamp();
        await db.SaveChangesAsync();
        Guard.Succeeded(tech.Email);
        await RecheckAsync(tech.Id);
        _log?.LogInformation("[{Tenant}] {Email} changed their password", tech.Tenant.CompanyName, tech.Email);
        return ("", tech.SecurityStamp);
    }

    // =========================================================================================
    // Unchanged
    // =========================================================================================

    /// <summary>
    /// Saves somebody's own light or dark against their account (3.6.9), from the console. Returns
    /// what was stored, or null if the person no longer exists. The portal's switch writes the same
    /// column, so the two can never disagree about which one is theirs.
    /// </summary>
    public async Task<string?> SaveAppearanceAsync(int tenantId, int technicianId, string? appearance)
    {
        var wanted = PortalIdentity.Clean(appearance);
        await using var db = await _dbf.CreateDbContextAsync();
        var me = await db.Technicians.FirstOrDefaultAsync(t => t.Id == technicianId && t.TenantId == tenantId);
        if (me == null) return null;
        me.Appearance = wanted;
        await db.SaveChangesAsync();
        return wanted;
    }

    /// <summary>
    /// Somebody's light or dark as it is NOW, not as it was when they signed in. A session carries
    /// the value from its sign-in, and the switch can have been flipped since, in either place.
    /// </summary>
    public async Task<string> AppearanceOfAsync(int tenantId, int technicianId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var v = await db.Technicians.Where(t => t.Id == technicianId && t.TenantId == tenantId)
            .Select(t => t.Appearance).FirstOrDefaultAsync();
        return PortalIdentity.Clean(v);
    }
}
