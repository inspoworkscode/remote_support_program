using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Services;

/// <summary>What a good code opens: the machine, whose it is, and who the customer said it was for.</summary>
public sealed record OutsiderRedeemed(int CodeId, int TenantId, string DeviceId, string ForWhom, string SessionId = "", int InvitedById = 0)
{
    /// <summary>A technician's code for one session (§155), not the customer's.</summary>
    public bool ForSession => SessionId.Length > 0;
}

/// <summary>
/// The codes the person at a machine gives somebody outside the company (roadmap 3.9, step 1,
/// 2026-10-06). Nine digits, ten minutes, used once; a new one stops the old. Whether the machine may
/// make one at all is the hub's question (<see cref="OutsiderAccess.Allowed"/>) — it holds the
/// switches; this holds the codes.
///
/// <para><b>Only the hash is stored</b>, so a copy of the database lets nobody in. Codes are kept after
/// they are spent or stopped: they are the record of who was let in.</para>
///
/// <para><b>Guessing.</b> A wrong code names no machine, so — unlike the unattended password (§77),
/// which locks the machine — the limit is on whoever is guessing: five wrong in fifteen minutes and
/// they are refused, the right code included, until it passes. A blank is not a guess. With a handful
/// of live codes among a billion, five tries is nothing to a guesser; the limit is there so that a
/// script is a script nobody can run.</para>
/// </summary>
public sealed class OutsiderCodeService
{
    public const int MaxWrong = 5;
    public static readonly TimeSpan WrongWindow = TimeSpan.FromMinutes(15);

    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly ConcurrentDictionary<string, List<DateTime>> _wrong = new();

    public OutsiderCodeService(IDbContextFactory<PlatformDb> dbf) => _dbf = dbf;

    /// <summary>The stored form. Peppered so the hash of a nine-digit number is not a table lookup away.</summary>
    public static string Hash(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("rsp-outsider-code:" + OutsiderAccess.Clean(code))));

    /// <summary>
    /// A new code for this machine. Any live one is stopped first ("replaced"): one machine, one code,
    /// so the customer never has to wonder which of two they read out.
    /// </summary>
    public Task<(string Code, DateTime ExpiresUtc, string Error)> CreateAsync(int tenantId, string deviceId, string? forWhom, string madeBy) =>
        MakeAsync(tenantId, deviceId, "", 0, forWhom, madeBy);

    /// <summary>
    /// A code a TECHNICIAN makes in a session to bring somebody in (2026-10-22, §155). It opens only that
    /// session, and that technician lets them in. One per session — a new one stops the last; the customer's own
    /// code on the machine is left alone, and so is theirs by this.
    /// </summary>
    public Task<(string Code, DateTime ExpiresUtc, string Error)> CreateForSessionAsync(int tenantId, string deviceId, string sessionId,
        int technicianId, string? forWhom, string madeBy) =>
        MakeAsync(tenantId, deviceId, sessionId, technicianId, forWhom, madeBy);

    private async Task<(string Code, DateTime ExpiresUtc, string Error)> MakeAsync(int tenantId, string deviceId, string sessionId,
        int technicianId, string? forWhom, string madeBy)
    {
        var problem = OutsiderAccess.ForProblem(forWhom);
        if (problem.Length > 0) return ("", default, problem);
        var now = DateTime.UtcNow;
        await using var db = await _dbf.CreateDbContextAsync();
        if (sessionId.Length == 0) await EndLiveAsync(db, tenantId, deviceId, "replaced by a new code", now);
        else await EndLiveForSessionAsync(db, tenantId, sessionId, "replaced by a new code", now);

        // Unique among the codes that still work, anywhere on this server: a code is typed with no
        // company or machine beside it, so it must name exactly one.
        string code, hash;
        int tries = 0;
        do
        {
            code = RandomNumberGenerator.GetInt32(0, 1_000_000_000).ToString("D" + OutsiderAccess.Digits);
            hash = Hash(code);
            if (++tries > 20) return ("", default, "A code could not be made. Try again.");
        }
        while (await db.OutsiderCodes.AnyAsync(c => c.CodeHash == hash && c.UsedUtc == null && c.EndedUtc == null && c.ExpiresUtc > now));

        var row = new OutsiderCode
        {
            TenantId = tenantId, DeviceId = deviceId, CodeHash = hash, ForWhom = forWhom!.Trim(),
            MadeBy = madeBy, CreatedUtc = now, ExpiresUtc = now + OutsiderAccess.Lifetime,
            SessionId = sessionId, InvitedById = technicianId
        };
        db.OutsiderCodes.Add(row);
        await db.SaveChangesAsync();
        return (code, row.ExpiresUtc, "");
    }

    /// <summary>Stops this machine's live code — the CUSTOMER's — if it has one. True if one was stopped.</summary>
    public async Task<bool> CancelAsync(int tenantId, string deviceId, string why)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await EndLiveAsync(db, tenantId, deviceId, why, DateTime.UtcNow) > 0;
    }

    /// <summary>Stops the technician's live code for one session (§155): stopped, the session ended, or they left it.</summary>
    public async Task<bool> CancelForSessionAsync(int tenantId, string sessionId, string why)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await EndLiveForSessionAsync(db, tenantId, sessionId, why, DateTime.UtcNow) > 0;
    }

    /// <summary>Stops every technician's live session code in the company — the company switched it off (§155).</summary>
    public async Task<int> CancelAllSessionCodesAsync(int tenantId, string why)
    {
        var now = DateTime.UtcNow;
        await using var db = await _dbf.CreateDbContextAsync();
        var live = await db.OutsiderCodes.Where(c => c.TenantId == tenantId && c.SessionId != ""
            && c.UsedUtc == null && c.EndedUtc == null && c.ExpiresUtc > now).ToListAsync();
        foreach (var c in live) { c.EndedUtc = now; c.EndedWhy = why; }
        if (live.Count > 0) await db.SaveChangesAsync();
        return live.Count;
    }

    // The customer's codes are the ones with no session (§155): a technician's code never stops theirs, nor theirs it.
    private static async Task<int> EndLiveAsync(PlatformDb db, int tenantId, string deviceId, string why, DateTime now)
    {
        var live = await db.OutsiderCodes.Where(c => c.TenantId == tenantId && c.DeviceId == deviceId && c.SessionId == ""
            && c.UsedUtc == null && c.EndedUtc == null && c.ExpiresUtc > now).ToListAsync();
        foreach (var c in live) { c.EndedUtc = now; c.EndedWhy = why; }
        if (live.Count > 0) await db.SaveChangesAsync();
        return live.Count;
    }

    private static async Task<int> EndLiveForSessionAsync(PlatformDb db, int tenantId, string sessionId, string why, DateTime now)
    {
        var live = await db.OutsiderCodes.Where(c => c.TenantId == tenantId && c.SessionId == sessionId
            && c.UsedUtc == null && c.EndedUtc == null && c.ExpiresUtc > now).ToListAsync();
        foreach (var c in live) { c.EndedUtc = now; c.EndedWhy = why; }
        if (live.Count > 0) await db.SaveChangesAsync();
        return live.Count;
    }

    /// <summary>The machine's code that still works, if any — for the tray app's "a code is live" line. The customer's only.</summary>
    public async Task<OutsiderCode?> LiveAsync(int tenantId, string deviceId)
    {
        var now = DateTime.UtcNow;
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.OutsiderCodes.Where(c => c.TenantId == tenantId && c.DeviceId == deviceId && c.SessionId == ""
                && c.UsedUtc == null && c.EndedUtc == null && c.ExpiresUtc > now)
            .OrderByDescending(c => c.Id).FirstOrDefaultAsync();
    }

    /// <summary>
    /// Spends a code. <paramref name="caller"/> is who is guessing (an account or an address) and
    /// <paramref name="usedBy"/> how they are written in the record. A code is spent ONCE: two people
    /// typing it at the same moment get one machine between them, never two — the update is conditional
    /// on its still being unspent.
    /// <para><paramref name="check"/> runs once a live code is found and BEFORE it is spent: its words refuse
    /// it and leave the code working (the machine is offline, or no longer allows it) — a customer should not
    /// have to make a new code because the first was typed a minute too early.</para>
    /// </summary>
    public async Task<(OutsiderRedeemed? Hit, string Error)> RedeemAsync(string? typed, string caller, string usedBy,
        Func<OutsiderRedeemed, Task<string>>? check = null)
    {
        var code = OutsiderAccess.Clean(typed);
        if (code.Length == 0) return (null, "Enter the code you were given.");
        if (Locked(caller)) return (null, "Too many wrong codes. Wait fifteen minutes and try again.");
        if (code.Length != OutsiderAccess.Digits) { Wrong(caller); return (null, "That is not a code. It has nine digits."); }

        var hash = Hash(code);
        var now = DateTime.UtcNow;
        await using var db = await _dbf.CreateDbContextAsync();
        var row = await db.OutsiderCodes.Where(c => c.CodeHash == hash && c.UsedUtc == null && c.EndedUtc == null && c.ExpiresUtc > now)
            .OrderByDescending(c => c.Id).FirstOrDefaultAsync();
        if (row == null)
        {
            Wrong(caller);
            // The same words for a code that never existed, has run out, was stopped or was spent:
            // telling them apart would tell a guesser which numbers were once real.
            return (null, "That code does not work. It may have run out — codes last ten minutes and work once. Ask for a new one.");
        }
        var found = new OutsiderRedeemed(row.Id, row.TenantId, row.DeviceId, row.ForWhom, row.SessionId, row.InvitedById);
        if (check != null && await check(found) is { Length: > 0 } refused) return (null, refused);
        int spent = await db.OutsiderCodes.Where(c => c.Id == row.Id && c.UsedUtc == null && c.EndedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedUtc, now).SetProperty(c => c.UsedBy, usedBy));
        if (spent == 0) return (null, "That code has just been used. Ask for a new one.");
        _wrong.TryRemove(caller, out _);
        return (found, "");
    }

    private bool Locked(string caller)
    {
        if (!_wrong.TryGetValue(caller, out var list)) return false;
        lock (list) { list.RemoveAll(t => DateTime.UtcNow - t > WrongWindow); return list.Count >= MaxWrong; }
    }

    private void Wrong(string caller)
    {
        var list = _wrong.GetOrAdd(caller, _ => new List<DateTime>());
        lock (list) list.Add(DateTime.UtcNow);
    }
}
