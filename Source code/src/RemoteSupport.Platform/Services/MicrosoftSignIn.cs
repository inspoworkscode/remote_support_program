using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace RemoteSupport.Platform.Services;

/// <summary>Why Microsoft did not give a token.</summary>
public enum MsFailure
{
    None,
    /// <summary>Microsoft refused THEIR sign-in — password changed, access removed, permission withdrawn. Asking again
    /// changes nothing: a person must sign in again. The token is dead and is deleted.</summary>
    Refused,
    /// <summary>Microsoft refused OUR app — a wrong or expired secret. Theirs is fine; it is ours to fix.</summary>
    Ours,
    /// <summary>Microsoft did not answer, or answered with an error worth trying again later.</summary>
    Later
}

/// <summary>A token from Microsoft, or why not. <see cref="RefreshEnc"/> is the refresh token as it must now be STORED
/// (Microsoft hands out a new one with each refresh); <see cref="User"/> and <see cref="Tenant"/> come from the
/// id_token, so only a sign-in fills them.</summary>
public sealed record MsAnswer(string AccessToken, string RefreshEnc, string User, string Tenant, MsFailure Failure, string Words)
{
    public bool Ok => Failure == MsFailure.None && AccessToken.Length > 0;
    public static MsAnswer Fail(MsFailure f, string words) => new("", "", "", "", f, words);
}

/// <summary>
/// "Sign in with Microsoft" for Tickets by email (2026-10-19, HANDOVER §151), and the only file that talks to
/// Microsoft's identity platform. Microsoft 365 no longer takes a mailbox password from a program like ours (basic
/// authentication is gone for IMAP, and goes for SMTP AUTH at the end of 2026 — MC786329), so a company on 365 had only
/// forwarding, which its administrator must allow. This is the third way in.
///
/// ONE multi-tenant Entra app, ours (docs/MICROSOFT-365.md registers it). The scopes are DELEGATED and only these:
/// <see cref="MailScopes"/> — IMAP.AccessAsUser.All and SMTP.Send — with offline_access for a refresh token and
/// openid/profile/email to know who signed in. So we read only the mailbox that signed in, or a shared mailbox that
/// account may open; never an application-wide Mail.Read, which would open every mailbox in their organisation.
///
/// The code flow with PKCE: the state names the company and the person (protected, 15 minutes), and a cookie carries
/// the PKCE verifier and the nonce — so a callback is only accepted in the browser that started it, by the person who
/// started it, for their own company. The refresh token is kept with Data Protection under its own purpose.
/// <c>TicketIntake</c> holds every rule about WHEN to ask and what a refusal does; this only asks.
/// </summary>
public sealed class MicrosoftSignIn
{
    /// <summary>What we may do in their mailbox: read it over IMAP and send over SMTP, as the person who signed in.</summary>
    public const string MailScopes = "https://outlook.office.com/IMAP.AccessAsUser.All https://outlook.office.com/SMTP.Send";
    /// <summary>At sign-in also: a refresh token, and who signed in (OpenID Connect's own three).</summary>
    public const string SignInScopes = "openid profile email offline_access " + MailScopes;
    public const string CallbackPath = "/portal/mailintake/microsoft";
    public const string CookieName = "rsp_ms";
    private static readonly TimeSpan StartLife = TimeSpan.FromMinutes(15);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly PlatformMailService _platformMail;
    private readonly IDataProtector _secret, _token;
    private readonly ITimeLimitedDataProtector _start;
    private readonly ILogger<MicrosoftSignIn> _log;
    private readonly string _publicUrl;

    /// <summary>login.microsoftonline.com; a harness's fake on loopback. Never anything else over plain http.</summary>
    public string Authority { get; }
    /// <summary>"organizations": any work or school account. Personal Outlook.com accounts cannot use Microsoft 365's IMAP this way.</summary>
    public string Directory { get; }
    public string ImapHost { get; }
    public int ImapPort { get; }
    public string SmtpHost { get; }
    public int SmtpPort { get; }

    public MicrosoftSignIn(PlatformMailService platformMail, IDataProtectionProvider dp, IConfiguration cfg, ILogger<MicrosoftSignIn> log)
    {
        _platformMail = platformMail; _log = log;
        // Purposes are part of the key: a refresh token is not readable by anything holding the mail-password protector.
        _secret = dp.CreateProtector("RemoteSupport.MicrosoftAppSecret.v1");
        _token = dp.CreateProtector("RemoteSupport.MicrosoftMailToken.v1");
        _start = dp.CreateProtector("RemoteSupport.MicrosoftSignInStart.v1").ToTimeLimitedDataProtector();
        _publicUrl = (cfg["Platform:PublicUrl"] ?? "").TrimEnd('/');
        const string real = "https://login.microsoftonline.com";
        var authority = (cfg["Microsoft:Authority"] ?? real).TrimEnd('/');
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var u) || (u.Scheme != "https" && !MailWire.IsLoopback(u.Host)))
        {
            log.LogWarning("Microsoft:Authority {A} is not https (nor a harness on this machine) — using {Real}", authority, real);
            authority = real;
        }
        Authority = authority;
        Directory = cfg["Microsoft:Tenant"] is { Length: > 0 } d ? d : "organizations";
        ImapHost = cfg["Microsoft:ImapHost"] is { Length: > 0 } ih ? ih : "outlook.office365.com";
        ImapPort = int.TryParse(cfg["Microsoft:ImapPort"], out var ip) ? ip : 993;
        SmtpHost = cfg["Microsoft:SmtpHost"] is { Length: > 0 } sh ? sh : "smtp.office365.com";
        SmtpPort = int.TryParse(cfg["Microsoft:SmtpPort"], out var sp) ? sp : 587;
    }

    public string RedirectUri => _publicUrl + CallbackPath;

    // ================================================================ our app

    /// <summary>Our app's id and secret, or null when this server has none (then the choice says "not available").</summary>
    public async Task<(string id, string secret)?> AppAsync()
    {
        var p = await _platformMail.GetAsync();
        if (!p.MicrosoftConfigured) return null;
        try { return (p.MsClientId, _secret.Unprotect(p.MsClientSecretEnc)); }
        catch (Exception ex) { _log.LogWarning(ex, "the Microsoft app secret could not be decrypted — data/keys has probably moved (handover section 10)"); return null; }
    }

    public async Task<bool> OfferedAsync() => await AppAsync() != null;

    /// <summary>Puts our app on this server (`microsoft-app set`). "" or what is wrong.</summary>
    public async Task<string> SetAppAsync(string clientId, string? secret, DateTime? expiresUtc)
    {
        clientId = (clientId ?? "").Trim();
        if (!Guid.TryParse(clientId, out _)) return "The client id is the Application (client) ID on the app's Overview page in Entra — a GUID like 1b2c3d4e-….";
        var p = await _platformMail.GetAsync();
        if (string.IsNullOrEmpty(secret) && p.MsClientSecretEnc.Length == 0) return "No secret: set RSP_MS_CLIENT_SECRET (deploy/Set-MicrosoftApp.ps1 does).";
        await _platformMail.SaveAsync(x =>
        {
            x.MsClientId = clientId;
            if (!string.IsNullOrEmpty(secret)) x.MsClientSecretEnc = _secret.Protect(secret.Trim());
            if (expiresUtc != null) x.MsSecretExpiresUtc = expiresUtc;
            x.MsUpdatedUtc = DateTime.UtcNow;
        });
        return "";
    }

    // ================================================================ signing in

    /// <summary>
    /// Where to send the person, and the cookie that must come back with the answer. Null when this server has no app.
    /// <paramref name="loginHint"/> fills in the support address on Microsoft's page.
    /// </summary>
    public async Task<(string url, string cookie)?> StartAsync(int tenantId, int technicianId, string loginHint)
    {
        if (await AppAsync() is not { } app) return null;
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(18));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = _start.Protect($"{tenantId}|{technicianId}|{nonce}", StartLife);
        var cookie = _start.Protect($"{nonce}|{verifier}", StartLife);
        var q = new Dictionary<string, string>
        {
            ["client_id"] = app.id, ["response_type"] = "code", ["redirect_uri"] = RedirectUri, ["response_mode"] = "query",
            ["scope"] = SignInScopes, ["state"] = state, ["nonce"] = nonce,
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
            // Always ask which account: the person at the keyboard is often signed in to Microsoft as THEMSELVES, and
            // the mailbox wanted is support@ — silently using the first account would read the wrong mailbox.
            ["prompt"] = "select_account"
        };
        if (TicketIntake.LooksLikeEmail(loginHint)) q["login_hint"] = loginHint;
        return ($"{Authority}/{Directory}/oauth2/v2.0/authorize?{Query(q)}", cookie);
    }

    /// <summary>The link an ADMINISTRATOR opens to approve the app for their whole organisation, when the organisation
    /// lets only administrators approve apps. It comes back to the same callback with admin_consent=True.</summary>
    public async Task<string> AdminConsentUrlAsync()
    {
        if (await AppAsync() is not { } app) return "";
        return $"{Authority}/organizations/v2.0/adminconsent?{Query(new() { ["client_id"] = app.id, ["redirect_uri"] = RedirectUri, ["scope"] = SignInScopes, ["state"] = "admin" })}";
    }

    /// <summary>
    /// The answer belongs to a sign-in THIS person started for THIS company, in THIS browser, in the last 15 minutes.
    /// Returns the PKCE verifier and the nonce, or null.
    /// </summary>
    public (string verifier, string nonce)? CheckReturn(string? state, string? cookie, int tenantId, int technicianId)
    {
        try
        {
            if (string.IsNullOrEmpty(state) || string.IsNullOrEmpty(cookie)) return null;
            var s = _start.Unprotect(state).Split('|');
            var c = _start.Unprotect(cookie).Split('|');
            if (s.Length != 3 || c.Length != 2) return null;
            if (s[0] != tenantId.ToString() || s[1] != technicianId.ToString()) return null;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(s[2]), Encoding.ASCII.GetBytes(c[0]))) return null;
            return (c[1], c[0]);
        }
        catch { return null; }   // tampered with, from another server's keys, or older than 15 minutes
    }

    /// <summary>The code Microsoft sent back, exchanged for tokens; who signed in is read from the id_token.</summary>
    public async Task<MsAnswer> RedeemAsync(string code, string verifier, string nonce, CancellationToken ct = default)
    {
        if (await AppAsync() is not { } app) return MsAnswer.Fail(MsFailure.Ours, "Sign in with Microsoft is not set up on this server.");
        var a = await TokenAsync(Directory, new()
        {
            ["client_id"] = app.id, ["client_secret"] = app.secret, ["grant_type"] = "authorization_code",
            ["code"] = code, ["redirect_uri"] = RedirectUri, ["code_verifier"] = verifier, ["scope"] = SignInScopes
        }, ct);
        if (!a.ok) return a.answer!;
        var claims = Claims(a.json.TryGetProperty("id_token", out var idt) ? idt.GetString() : null);
        // Straight from Microsoft's token endpoint over TLS, so its signature need not be checked (OpenID Connect
        // Core 3.1.3.7) — but it must be for the sign-in we started.
        if (claims.TryGetValue("nonce", out var n) && n != nonce)
            return MsAnswer.Fail(MsFailure.Later, "Microsoft's answer was not for this sign-in. Press Sign in with Microsoft again.");
        var user = (claims.GetValueOrDefault("email") is { Length: > 0 } e && TicketIntake.LooksLikeEmail(e) ? e
                  : claims.GetValueOrDefault("preferred_username") ?? "").Trim().ToLowerInvariant();
        var refresh = a.json.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
        if (refresh.Length == 0)
            return MsAnswer.Fail(MsFailure.Ours, "Microsoft did not give a refresh token, so we could not stay signed in. This is ours to fix — please tell us.");
        return new(a.json.GetProperty("access_token").GetString() ?? "", _token.Protect(refresh), user, claims.GetValueOrDefault("tid") ?? "", MsFailure.None, "");
    }

    // ================================================================ staying signed in

    private static readonly ConcurrentDictionary<int, (string fromEnc, string access, DateTime until)> Cache = new();
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> Gates = new();

    /// <summary>
    /// An access token for a company's stored refresh token: the one in hand if it has five minutes left, else a fresh
    /// one from Microsoft. A fresh answer carries a NEW refresh token (<see cref="MsAnswer.RefreshEnc"/>), which the
    /// caller must store; until it does, it is still the one cached against.
    /// </summary>
    public async Task<MsAnswer> AccessTokenAsync(int tenantId, string refreshEnc, string msTenant, CancellationToken ct = default)
    {
        if (refreshEnc.Length == 0) return MsAnswer.Fail(MsFailure.Refused, "Nobody has signed in with Microsoft yet.");
        var gate = Gates.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (Cache.TryGetValue(tenantId, out var c) && c.fromEnc == refreshEnc && c.until > DateTime.UtcNow.AddMinutes(5))
                return new(c.access, refreshEnc, "", "", MsFailure.None, "");
            string refresh;
            try { refresh = _token.Unprotect(refreshEnc); }
            catch { return MsAnswer.Fail(MsFailure.Refused, "The saved Microsoft sign-in can no longer be read (this server's keys changed). Press Sign in with Microsoft again."); }
            if (await AppAsync() is not { } app) return MsAnswer.Fail(MsFailure.Ours, "Sign in with Microsoft is no longer set up on this server — this is ours to fix, not yours. Until it is, forwarding works.");
            var a = await TokenAsync(msTenant.Length > 0 ? msTenant : Directory, new()
            {
                ["client_id"] = app.id, ["client_secret"] = app.secret, ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh, ["scope"] = "offline_access " + MailScopes
            }, ct);
            if (!a.ok) { Cache.TryRemove(tenantId, out _); return a.answer!; }
            var access = a.json.GetProperty("access_token").GetString() ?? "";
            var seconds = a.json.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var s) ? s : 3600;
            var newEnc = a.json.TryGetProperty("refresh_token", out var rt) && rt.GetString() is { Length: > 0 } r ? _token.Protect(r) : refreshEnc;
            Cache[tenantId] = (newEnc, access, DateTime.UtcNow.AddSeconds(seconds));
            return new(access, newEnc, "", "", MsFailure.None, "");
        }
        finally { gate.Release(); }
    }

    /// <summary>Forgets a company's access token at once — Disconnect, a new sign-in, a refusal.</summary>
    public static void Forget(int tenantId) => Cache.TryRemove(tenantId, out _);

    // ================================================================ the token endpoint

    private async Task<(bool ok, JsonElement json, MsAnswer? answer)> TokenAsync(string directory, Dictionary<string, string> form, CancellationToken ct)
    {
        try
        {
            using var r = await Http.PostAsync($"{Authority}/{Uri.EscapeDataString(directory)}/oauth2/v2.0/token", new FormUrlEncodedContent(form), ct);
            var body = await r.Content.ReadAsStringAsync(ct);
            JsonElement json;
            try { json = JsonDocument.Parse(body).RootElement.Clone(); }
            catch { return (false, default, MsAnswer.Fail(MsFailure.Later, $"Microsoft's sign-in service answered {(int)r.StatusCode} with something that was not a token. We will try again shortly.")); }
            if (r.IsSuccessStatusCode && json.TryGetProperty("access_token", out _)) return (true, json, null);
            var error = json.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
            var description = json.TryGetProperty("error_description", out var d) ? d.GetString() ?? "" : "";
            var (failure, words) = Explain(error, description, (int)r.StatusCode);
            _log.LogInformation("Microsoft token endpoint: {Error} {Code} — {Failure}", error, AadCode(description), failure);
            return (false, json, MsAnswer.Fail(failure, words));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested || ex is not OperationCanceledException)
        {
            return (false, default, MsAnswer.Fail(MsFailure.Later, "Microsoft's sign-in service did not answer. We will try again shortly."));
        }
    }

    /// <summary>
    /// What a refusal from the token endpoint means, in words for the company. invalid_grant and its family are THEIR
    /// sign-in ended (password changed, access removed, consent withdrawn, unused too long, a new policy): only a person
    /// signing in again mends it. invalid_client and its family are OUR app: their sign-in is untouched.
    /// </summary>
    public static (MsFailure failure, string words) Explain(string error, string description, int status = 400)
    {
        var code = AadCode(description);
        if (error is "invalid_client" or "unauthorized_client" or "invalid_scope" || code is "7000215" or "7000222" or "700016" or "70011")
            return (MsFailure.Ours, "Microsoft did not accept our app's own sign-in (its secret may have run out). This is ours to fix, not yours — "
                                  + "please tell us. Your Microsoft sign-in is kept, and checking starts again by itself once it is fixed.");
        if (error is "invalid_grant" or "interaction_required" or "consent_required" or "login_required")
        {
            var why = code switch
            {
                "50173" or "50133" or "50126" or "50055" or "50144" => "the account's password was changed or ran out since it signed in",
                "70008" or "700082" or "70043" or "700084" => "the sign-in ran out (unused for too long, or your organisation's limit)",
                "65001" or "65004" => "the permission for The Remote Support Program was withdrawn",
                "50057" or "50034" or "50053" => "the account is disabled, locked or no longer exists",
                "50076" or "50079" or "50158" or "53003" or "50097" => "your organisation now asks for another step at sign-in (multi-factor or a new policy)",
                "135011" or "50059" => "the account or the app was removed from your organisation",
                _ => "the password may have been changed, or the access removed, or the permission withdrawn"
            };
            return (MsFailure.Refused, $"Microsoft no longer accepts the sign-in: {why}. Checking has stopped. Press Sign in with Microsoft to connect it again.");
        }
        return (MsFailure.Later, status >= 500 || error is "temporarily_unavailable" or "server_error"
            ? "Microsoft's sign-in service is having trouble. We will try again shortly."
            : $"Microsoft refused: {Short(description.Length > 0 ? description : error)}. We will try again shortly.");
    }

    /// <summary>
    /// What came back to the callback instead of a code, as a short word for the address bar (codes, not words, go in
    /// an address — §105). <see cref="CallbackWords"/> turns it into a sentence on the page.
    /// </summary>
    public static string CallbackError(string? error, string? description)
    {
        var code = AadCode(description ?? "");
        if (code is "90094" or "900941" or "90095" or "65002" || error == "consent_required") return "admin";
        if (code is "50020" or "500200" or "500201" or "50177") return "account";
        if (code is "700016" or "7000215" or "650051" or "50011" or "500113" || error is "unauthorized_client" or "invalid_client" or "invalid_request") return "ours";
        if (error == "access_denied" || code == "65004") return "declined";
        return "failed";
    }

    public static string CallbackWords(string code) => code switch
    {
        "connected" => "",
        "declined" => "The sign-in was cancelled, or the permission was not given, so nothing was connected. Press Sign in with Microsoft to try again.",
        "admin" => "Your organisation lets only an administrator approve a new app. Ask your Microsoft 365 administrator to open the approval link "
                 + "under the button (or to press Sign in with Microsoft themselves and tick \"Consent on behalf of your organisation\"), then sign in again.",
        "approved" => "Your administrator approved The Remote Support Program for your organisation. Now press Sign in with Microsoft.",
        "account" => "That was a personal Microsoft account, or an account from another organisation. Sign in with the work account of the support "
                   + "mailbox, or of somebody in your organisation who can open it.",
        "ours" => "Microsoft did not recognise our app. This is ours to fix, not yours — please tell us. Until then, forwarding works.",
        "expired" => "That sign-in was not started from this page in the last 15 minutes, or was started by somebody else. Press Sign in with Microsoft again.",
        "noaccount" => "Microsoft did not say which account signed in, so nothing was connected. Press Sign in with Microsoft and choose the mailbox's account.",
        _ => "Microsoft did not complete the sign-in. Press Sign in with Microsoft to try again."
    };

    // ================================================================ small things

    /// <summary>"AADSTS70000: …" → "70000".</summary>
    public static string AadCode(string description)
    {
        var i = description.IndexOf("AADSTS", StringComparison.Ordinal);
        if (i < 0) return "";
        var j = i + 6;
        while (j < description.Length && char.IsDigit(description[j])) j++;
        return description[(i + 6)..j];
    }

    /// <summary>The claims of a JWT's middle part, as strings. Empty when there is none.</summary>
    public static Dictionary<string, string> Claims(string? jwt)
    {
        var d = new Dictionary<string, string>();
        var parts = (jwt ?? "").Split('.');
        if (parts.Length < 2) return d;
        try
        {
            var p = parts[1].Replace('-', '+').Replace('_', '/');
            p = p.PadRight(p.Length + (4 - p.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(p));
            foreach (var x in doc.RootElement.EnumerateObject())
                if (x.Value.ValueKind == JsonValueKind.String) d[x.Name] = x.Value.GetString() ?? "";
        }
        catch { }
        return d;
    }

    public static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Query(Dictionary<string, string> q) => string.Join("&", q.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    private static string Short(string s) { s = s.Replace("\r", " ").Replace("\n", " ").Trim(); var cut = s.IndexOf(" Trace ID", StringComparison.Ordinal); if (cut > 0) s = s[..cut]; return s.Length <= 200 ? s : s[..200] + "…"; }
}
