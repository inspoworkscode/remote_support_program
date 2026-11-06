using System.Collections.Concurrent;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Stops password and code guessing (2026-10-01). Before this, nothing did: a script could try
/// passwords against /api/login as fast as the server answered, slowed only by a 100–300 ms delay.
///
/// Two counts, both of FAILURES in the last 15 minutes:
///   - per account: 5, then that account is refused for 15 minutes — whatever is typed, even the
///     right password, so a guesser learns nothing from the answer;
///   - per address: 30, then that address is refused for 15 minutes, which is what stops one
///     machine walking through a list of accounts five guesses at a time.
///
/// In memory on purpose. A restart forgets the counts, and that is fine: it cannot be triggered
/// from outside, and a guesser who waits for a restart has gained five more guesses.
///
/// A KNOWN device has its own account count (2026-10-23, the pentest's F1). The account count was a
/// lever: anybody who knew an address could keep its owner out for good, five wrong passwords every
/// fifteen minutes. A browser or console holding the person's trust token (the code was typed on it
/// within the last week — AuthService.Trusted) is counted apart, so a stranger's failures never lock
/// it, and its own failures still stop at five. The token is Data Protection's and bound to the
/// person's stamps, so a guesser cannot make one. The address count applies to everybody.
///
/// The per-address count only means anything because the server sees the real address behind
/// Caddy (UseForwardedHeaders in Program.cs). Without that every caller was 127.0.0.1, and this
/// would have locked out the whole internet after thirty typos.
/// </summary>
public sealed class SignInGuard
{
    public const int PerAccount = 5;
    public const int PerAddress = 30;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, List<DateTime>> _failures = new();

    /// <summary>Seconds until this account or address may try again, or 0 if it may now.</summary>
    public int RetryAfter(string account, string address, bool knownDevice = false, DateTime? now = null)
    {
        var at = now ?? DateTime.UtcNow;
        return Math.Max(Wait(AccountKey(account, knownDevice), PerAccount, at), Wait("ip:" + address, PerAddress, at));
    }

    /// <summary>Seconds until this address may try again, whatever the account.</summary>
    public int AddressWait(string address, DateTime? now = null) => Wait("ip:" + address, PerAddress, now ?? DateTime.UtcNow);

    public void Fail(string account, string address, bool knownDevice = false, DateTime? now = null)
    {
        var at = now ?? DateTime.UtcNow;
        Add(AccountKey(account, knownDevice), at);
        Add("ip:" + address, at);
    }

    /// <summary>A full sign-in clears the account's counts (never the address's: one success must not wash a list of guesses).</summary>
    public void Succeeded(string account)
    {
        _failures.TryRemove(AccountKey(account, false), out _);
        _failures.TryRemove(AccountKey(account, true), out _);
    }

    private static string AccountKey(string account, bool knownDevice) => (knownDevice ? "k:" : "a:") + Key(account);

    private static string Key(string account) => account.Trim().ToLowerInvariant();

    private void Add(string key, DateTime at)
    {
        var list = _failures.GetOrAdd(key, _ => new List<DateTime>());
        lock (list) { list.RemoveAll(x => at - x > Window); list.Add(at); }
    }

    private int Wait(string key, int limit, DateTime at)
    {
        if (!_failures.TryGetValue(key, out var list)) return 0;
        lock (list)
        {
            list.RemoveAll(x => at - x > Window);
            if (list.Count < limit) return 0;
            // Refused until the oldest failure that still counts has aged out of the window.
            var until = list[list.Count - limit] + Window;
            return Math.Max(1, (int)Math.Ceiling((until - at).TotalSeconds));
        }
    }

    public static string Describe(int seconds)
    {
        var minutes = (int)Math.Ceiling(seconds / 60.0);
        return $"Too many attempts. Please wait {minutes} minute{(minutes == 1 ? "" : "s")} and try again.";
    }
}
