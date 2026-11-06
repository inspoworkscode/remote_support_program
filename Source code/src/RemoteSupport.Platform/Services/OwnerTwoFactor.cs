using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// The platform owner's second step (2026-10-01). The owner portal used to be one shared admin
/// key and nothing else, behind which sits every company on the platform.
///
/// Now: the key, then a code from an authenticator app. Whoever first signs in with the key after
/// this arrived sets the authenticator up — the key is already the secret, so that is no weaker
/// than it was, and from then on the key alone is not enough. Lost phone: <c>reset-owner-2fa</c> on
/// the server's command line, which needs a shell on the server and is therefore its own proof.
///
/// The owner's cookie carries a stamp made from the admin key and the stored secret, so changing
/// either signs every owner browser out (<see cref="Stamp"/>).
/// </summary>
public sealed class OwnerTwoFactor
{
    private readonly PlatformMailService _settings;
    private readonly IDataProtector _protector;
    private readonly IConfiguration _cfg;
    private readonly AuthService _auth;
    private readonly ConcurrentDictionary<string, (DateTime at, string secret, int wrong)> _pending = new();
    private (string stamp, DateTime at) _stampCache;

    public OwnerTwoFactor(PlatformMailService settings, IDataProtectionProvider dp, IConfiguration cfg, AuthService auth)
    {
        _settings = settings; _cfg = cfg; _auth = auth;
        _protector = dp.CreateProtector("RemoteSupport.OwnerTotpSecret.v1");
    }

    private const string Account = "platform-owner";

    public sealed record Step(bool Done, string Error, string Pending = "", string Secret = "", string Qr = "");

    /// <summary>Step one: the admin key. Answers with the code step, or the set-up step the first time.</summary>
    public async Task<Step> KeyAsync(string? key, string address)
    {
        var wait = _auth.Guard.RetryAfter(Account, address);
        if (wait > 0) return new(false, SignInGuard.Describe(wait));
        var expected = _cfg["Platform:AdminKey"];
        if (string.IsNullOrEmpty(expected)) return new(false, "Platform:AdminKey is not configured on the server.");
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key ?? ""), Encoding.UTF8.GetBytes(expected)))
        {
            _auth.Guard.Fail(Account, address);
            return new(false, "Invalid key.");
        }
        var s = await _settings.GetAsync();
        var id = Passwords.RandomToken(24);
        if (s.OwnerTotpSecretEnc.Length > 0)
        {
            _pending[id] = (DateTime.UtcNow, "", 0);
            return new(false, "", id);
        }
        var secret = Totp.NewSecret();
        _pending[id] = (DateTime.UtcNow, secret, 0);
        return new(false, "", id, secret, QrImage.DataUri(Totp.Uri("The Remote Support Program", "Platform owner", secret)));
    }

    /// <summary>Step two: the six digits — checked against the stored secret, or, the first time, against the one just shown.</summary>
    public async Task<Step> CodeAsync(string? pendingId, string? code, string address)
    {
        if (pendingId == null || !_pending.TryGetValue(pendingId, out var p) || DateTime.UtcNow - p.at > AuthService.PendingLife)
        { if (pendingId != null) _pending.TryRemove(pendingId, out _); return new(false, "That sign-in has expired. Please start again."); }
        var wait = _auth.Guard.RetryAfter(Account, address);
        if (wait > 0) { _pending.TryRemove(pendingId, out _); return new(false, SignInGuard.Describe(wait)); }

        var s = await _settings.GetAsync();
        bool enrolling = p.secret.Length > 0;
        string secret = p.secret;
        if (!enrolling)
        {
            try { secret = _protector.Unprotect(s.OwnerTotpSecretEnc); }
            catch (CryptographicException) { return new(false, "The stored authenticator secret cannot be read (was data/keys lost?). Run reset-owner-2fa on the server."); }
        }
        var step = Totp.Match(secret, code, DateTime.UtcNow);
        if (step == null || (!enrolling && step.Value <= s.OwnerTotpLastStep))
        {
            _auth.Guard.Fail(Account, address);
            if (p.wrong + 1 >= AuthService.CodesPerPending) { _pending.TryRemove(pendingId, out _); return new(false, "Too many wrong codes. Please start again."); }
            _pending[pendingId] = (p.at, p.secret, p.wrong + 1);
            return new(false, "That code is not right. Codes change every 30 seconds — use the one showing now.", pendingId, p.secret,
                enrolling ? QrImage.DataUri(Totp.Uri("The Remote Support Program", "Platform owner", p.secret)) : "");
        }
        _pending.TryRemove(pendingId, out _);
        await _settings.SaveAsync(x =>
        {
            if (enrolling) x.OwnerTotpSecretEnc = _protector.Protect(secret);
            x.OwnerTotpLastStep = step.Value;
        });
        _auth.Guard.Succeeded(Account);
        _stampCache = default;
        return new(true, "");
    }

    /// <summary>
    /// What an owner cookie must carry to stay signed in: a hash of the admin key and the stored
    /// secret. Changing the key, or resetting the authenticator, signs every owner browser out.
    /// </summary>
    public async Task<string> StampAsync()
    {
        if (DateTime.UtcNow - _stampCache.at < TimeSpan.FromSeconds(15)) return _stampCache.stamp;
        var s = await _settings.GetAsync();
        var stamp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((_cfg["Platform:AdminKey"] ?? "") + "|" + s.OwnerTotpSecretEnc)))[..32];
        _stampCache = (stamp, DateTime.UtcNow);
        return stamp;
    }
}
