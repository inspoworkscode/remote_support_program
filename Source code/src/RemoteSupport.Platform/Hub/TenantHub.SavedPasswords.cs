using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Hub;

/// <summary>
/// REMEMBERED UNATTENDED PASSWORDS (2026-10-06). Clint: "entering a password remembers it, but only
/// for the particular technician that entered it." His answers: kept on the server, so it works from
/// any PC they sign in on; forgotten when the password is changed or removed at the machine, when
/// the technician is removed, or by right-click → Forget; a supervisor or the owner may see who has
/// one saved (names only) and clear any of them.
///
/// <para>This loosens §77's "nobody without it" on purpose, and only this far: each technician still
/// has to type it ONCE. A colleague who never has is asked, and a session opened with it still admits
/// nobody else without it (<c>Session.Admitted</c>) — a remembered one counts as typed.</para>
///
/// <para>What is remembered is that it was typed right, against which password (the machine's salt
/// at the time) — never the password, never its hash. See <see cref="SavedUnattendedPassword"/>.</para>
/// </summary>
public sealed partial class TenantHub
{
    /// <summary>
    /// Machine → technician → the password salt it was typed against. A salt that is no longer the
    /// machine's is a password since changed, and counts for nothing. Guarded by <c>_lock</c>.
    /// </summary>
    private readonly Dictionary<string, Dictionary<int, string>> _savedPw = new(StringComparer.OrdinalIgnoreCase);

    private async Task LoadSavedPasswordsAsync(PlatformDb db)
    {
        var rows = await db.SavedUnattendedPasswords.Where(p => p.TenantId == TenantId)
            .Select(p => new { p.Device.DeviceId, p.TechnicianId, p.PasswordSalt }).ToListAsync();
        foreach (var r in rows)
        {
            if (!_savedPw.TryGetValue(r.DeviceId, out var m)) _savedPw[r.DeviceId] = m = new();
            m[r.TechnicianId] = r.PasswordSalt;
        }
    }

    /// <summary>A machine's row came back under a (possibly new) device id — a reinstall keeps the
    /// row, and so its remembered passwords, as it keeps the password itself. True when it had or has
    /// any, so the consoles need telling.</summary>
    private async Task<bool> ReloadSavedPasswordsAsync(PlatformDb db, int deviceRowId, string deviceId)
    {
        var rows = await db.SavedUnattendedPasswords.Where(p => p.TenantId == TenantId && p.DeviceRowId == deviceRowId)
            .Select(p => new { p.TechnicianId, p.PasswordSalt }).ToListAsync();
        lock (_lock)
        {
            bool had = _savedPw.Remove(deviceId);
            if (rows.Count > 0) _savedPw[deviceId] = rows.ToDictionary(r => r.TechnicianId, r => r.PasswordSalt);
            return had || rows.Count > 0;
        }
    }

    /// <summary>Whether this technician has typed this machine's CURRENT password before. Under <c>_lock</c>.</summary>
    private bool RemembersPassword(int technicianId, string deviceId) =>
        _unattendedPw.TryGetValue(deviceId, out var pw)
        && _savedPw.TryGetValue(deviceId, out var m)
        && m.TryGetValue(technicianId, out var salt)
        && salt == pw.Salt;

    /// <summary>The machines this technician need not type a password for.</summary>
    private List<string> SavedPasswordsOf(int technicianId)
    {
        lock (_lock)
            return _savedPw.Keys.Where(id => RemembersPassword(technicianId, id)).ToList();
    }

    private SavedPasswordsMsg MineFor(TechSession who) => new()
    {
        DeviceIds = SavedPasswordsOf(who.TechnicianId),
        MayManage = Permissions.CanClearSavedPasswords(who.Role)
    };

    /// <summary>Every console its own list. Called after anything changes it; cheap, and it means no
    /// change can leave a console showing a box it does not need, or missing one it does.</summary>
    private void PushSavedPasswords()
    {
        List<TechConn> techs; lock (_lock) techs = _techs.ToList();
        foreach (var t in techs) t.Ws.Send(MineFor(t.Who));
    }

    /// <summary>
    /// The password was typed right: remember it for this technician. Memory first, so a second
    /// Connect a moment later is already let through; then the row.
    /// </summary>
    private void RememberPassword(TechConn tc, string deviceId, string salt)
    {
        lock (_lock)
        {
            if (!_savedPw.TryGetValue(deviceId, out var m)) _savedPw[deviceId] = m = new();
            if (m.TryGetValue(tc.Who.TechnicianId, out var had) && had == salt) return;
            m[tc.Who.TechnicianId] = salt;
        }
        tc.Ws.Send(MineFor(tc.Who));
        _ = StoreRememberedAsync(tc.Who.TechnicianId, tc.Who.DisplayName, deviceId, salt);
    }

    private async Task StoreRememberedAsync(int technicianId, string name, string deviceId, string salt)
    {
        try
        {
            await using var db = await _dbf.CreateDbContextAsync();
            var row = await db.Devices.Where(d => d.TenantId == TenantId && d.DeviceId == deviceId).Select(d => (int?)d.Id).FirstOrDefaultAsync();
            if (row == null) return;
            var saved = await db.SavedUnattendedPasswords.FirstOrDefaultAsync(p => p.DeviceRowId == row && p.TechnicianId == technicianId);
            if (saved == null) db.SavedUnattendedPasswords.Add(saved = new SavedUnattendedPassword { TenantId = TenantId, DeviceRowId = row.Value, TechnicianId = technicianId });
            saved.PasswordSalt = salt;
            saved.SavedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
            _log.LogInformation("[{Tenant}] {Tech}'s unattended password for {Device} is remembered", TenantName, name, deviceId);
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] remembering an unattended password", TenantName); }
    }

    /// <summary>
    /// The password was changed or removed at the machine: everybody's remembered one goes. The
    /// salt check alone would already refuse them; deleting the rows keeps "who has it saved" honest.
    /// </summary>
    private async Task ForgetEveryoneAsync(string deviceId)
    {
        lock (_lock) _savedPw.Remove(deviceId);
        try
        {
            await using var db = await _dbf.CreateDbContextAsync();
            var rows = await db.SavedUnattendedPasswords
                .Where(p => p.TenantId == TenantId && p.Device.DeviceId == deviceId).ToListAsync();
            db.SavedUnattendedPasswords.RemoveRange(rows);
            await db.SaveChangesAsync();
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] forgetting remembered passwords for {Device}", TenantName, deviceId); }
        PushSavedPasswords();
    }

    /// <summary>
    /// Right-click → Forget saved password (one's own), or a supervisor or the owner clearing
    /// somebody else's, or everybody's.
    /// </summary>
    private async Task ForgetSavedPasswordAsync(TechConn tc, ForgetSavedPasswordMsg m)
    {
        bool own = !m.All && (m.TechnicianId == 0 || m.TechnicianId == tc.Who.TechnicianId);
        if (!own && !Permissions.CanClearSavedPasswords(tc.Who.Role))
        {
            tc.Ws.Send(new ErrorMsg { Message = "Only a supervisor or the owner can clear somebody else's saved password." });
            return;
        }
        int who = own ? tc.Who.TechnicianId : m.TechnicianId;
        lock (_lock)
            if (_savedPw.TryGetValue(m.DeviceId, out var map))
            {
                if (m.All) map.Clear(); else map.Remove(who);
                if (map.Count == 0) _savedPw.Remove(m.DeviceId);
            }
        try
        {
            await using var db = await _dbf.CreateDbContextAsync();
            var rows = await db.SavedUnattendedPasswords
                .Where(p => p.TenantId == TenantId && p.Device.DeviceId == m.DeviceId && (m.All || p.TechnicianId == who)).ToListAsync();
            db.SavedUnattendedPasswords.RemoveRange(rows);
            await db.SaveChangesAsync();
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] forgetting a saved password", TenantName); }
        _log.LogInformation("[{Tenant}] {Tech} cleared {Whose} saved unattended password for {Device}",
            TenantName, tc.Who.DisplayName, m.All ? "everybody's" : own ? "their own" : $"technician {who}'s", m.DeviceId);
        PushSavedPasswords();
        if (!own) await SendSavedPasswordWhoAsync(tc, m.DeviceId);
    }

    /// <summary>Who has this machine's password saved — names and when, never the password.</summary>
    private async Task SendSavedPasswordWhoAsync(TechConn tc, string deviceId)
    {
        if (!Permissions.CanClearSavedPasswords(tc.Who.Role))
        {
            tc.Ws.Send(new ErrorMsg { Message = "Only a supervisor or the owner can see who has a machine's password saved." });
            return;
        }
        string salt; string machine;
        lock (_lock)
        {
            salt = _unattendedPw.TryGetValue(deviceId, out var pw) ? pw.Salt : "";
            machine = _devices.TryGetValue(deviceId, out var info) ? info.MachineName : deviceId;
        }
        var people = new List<SavedPasswordHolder>();
        if (salt.Length > 0)
        {
            await using var db = await _dbf.CreateDbContextAsync();
            people = await db.SavedUnattendedPasswords
                .Where(p => p.TenantId == TenantId && p.Device.DeviceId == deviceId && p.PasswordSalt == salt)
                .OrderBy(p => p.Technician.DisplayName)
                .Select(p => new SavedPasswordHolder { TechnicianId = p.TechnicianId, Name = p.Technician.DisplayName, SavedUtc = p.SavedUtc })
                .ToListAsync();
        }
        tc.Ws.Send(new SavedPasswordWhoMsg { DeviceId = deviceId, MachineName = machine, People = people });
    }
}
