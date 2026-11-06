using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Hub;

/// <summary>
/// THE FREE VERSION, at the hub (2026-10-06, docs/NEXT-SESSION-FREE.md, Stage B). The hub is the one
/// place every machine and every console passes through, so the limits are enforced HERE — a console
/// that hides a button is a convenience, never the rule. What the limits are is <see cref="FreePlan"/>.
///
/// <list type="bullet">
/// <item>Two machines. A third install is refused at hello, with words; a reinstall of one of the two
/// (the same hardware) is not a third.</item>
/// <item>Removing a machine frees its place, <see cref="FreePlan.RemovalsPerMonth"/> a calendar month,
/// and the removed install is refused if it comes back (its agent is still running over there).</item>
/// <item>Unattended only: no calls, no consent sessions, no outside help, no tray-app host — and every
/// machine must have an unattended password.</item>
/// <item>In a session, only what <see cref="FreePlan.MaySend"/> allows; from the console, only what
/// <see cref="ConsoleMaySendOnFree"/> allows. No tickets.</item>
/// </list>
/// </summary>
public sealed partial class TenantHub
{
    private volatile string _plan = "";
    /// <summary>The Basic version's computers paid for (Tenant.BasicMachines, 2026-10-25) — read through FreePlan.MachinesFor.</summary>
    private volatile int _basicMachines;
    /// <summary>Either one-person plan, Free or Basic (2026-10-25): one person, no tickets, the allow-lists.</summary>
    private bool Free => FreePlan.Is(_plan);
    /// <summary>The Free version alone (2026-10-25, §163): unattended only, with a password — never anybody asked. The Basic
    /// version may connect attended, the agent service asking the person (StartSession).</summary>
    private bool UnattendedOnly => FreePlan.IsFree(_plan);

    /// <summary>
    /// The company's plan changed — an upgrade from the free version (Stage G, 2026-10-06), or the Basic version's computers paid
    /// for (2026-10-25). From this moment the hub
    /// applies the full version's rules, and every console signed in is sent its state again, so it draws the
    /// full console at once rather than at its next sign-in. Machines, sessions and saved passwords are untouched.
    /// </summary>
    public async Task PlanChangedAsync(Tenant t)
    {
        _plan = t.Plan ?? "";
        _basicMachines = t.BasicMachines;
        ApplyRing(t);
        _healthAvailable = t.HealthCheckReady && !Free;
        var roster = await RosterAsync();
        List<TechConn> techs; lock (_lock) techs = _techs.ToList();
        foreach (var tc in techs)
        {
            StateMsg state; lock (_lock) state = StateFor(tc.Who, roster);
            tc.Ws.Send(state);
            tc.Ws.Send(MineFor(tc.Who));
        }
        _log.LogInformation("[{Tenant}] is now on the {Plan} plan; {Consoles} console(s) told", TenantName, Free ? FreePlan.ShortNameOf(_plan) : "full", techs.Count);
    }

    /// <summary>Install ids that were removed, refused at hello. Guarded by <c>_lock</c>.</summary>
    private readonly HashSet<string> _removedIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Each known machine's hardware fingerprint, so a reinstall is recognised as the SAME machine
    /// before PersistDeviceAsync merges its row. Guarded by <c>_lock</c>.</summary>
    private readonly Dictionary<string, string> _hardware = new(StringComparer.OrdinalIgnoreCase);

    private async Task LoadFreeAsync(Tenant tenant, PlatformDb db)
    {
        _plan = tenant.Plan ?? "";
        _basicMachines = tenant.BasicMachines;
        ApplyRing(tenant);
        foreach (var id in await db.DeviceRemovals.Where(r => r.TenantId == TenantId).Select(r => r.DeviceId).ToListAsync())
            _removedIds.Add(id);
    }

    /// <summary>
    /// Why this machine may not say hello on the free version, or null. Under <c>_lock</c>, in the same
    /// lock that adds it — so two new machines arriving together cannot both take the last place.
    /// </summary>
    private string? FreeRefusesHello(HelloMsg h)
    {
        // A removed install, on EITHER version (2026-10-06): its agent is still running over there, and would
        // otherwise put the machine straight back on the list it was taken off.
        if (_removedIds.Contains(h.DeviceId))
            return "This computer was removed from the account. To add it again, reinstall the support program on it.";
        if (!Free) return null;
        if (_devices.ContainsKey(h.DeviceId)) return null;                     // one of ours already
        if (!string.IsNullOrEmpty(h.HardwareId)
            && _hardware.Any(kv => kv.Value == h.HardwareId && _devices.ContainsKey(kv.Key)))
            return null;                                                       // one of ours, reinstalled
        // The Free version one; the Basic version as many as it pays for (2026-10-25): three in the price, more bought on
        // Billing, at most 20. Only a NEW computer is refused, so a Free company from before with two keeps both.
        int allowed = FreePlan.MachinesFor(_plan, _basicMachines);
        return MachineCount() >= allowed ? FreePlan.TooManyFor(_plan, allowed) : null;
    }

    /// <summary>
    /// Machines, not install ids: two ids on one fingerprint are one computer reinstalled (the old id
    /// lingers in memory until <see cref="ForgetReinstalledId"/> or a restart). Under <c>_lock</c>.
    /// </summary>
    private int MachineCount() =>
        _devices.Keys.Where(id => !IsWeb(id))
            .Select(id => _hardware.TryGetValue(id, out var hw) && hw.Length > 0 ? "hw:" + hw : "id:" + id)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();

    /// <summary>
    /// A reinstall took over <paramref name="previous"/>'s row under a new install id (PersistDeviceAsync).
    /// The old id's entry would otherwise sit in every console's list as an offline twin until the server
    /// restarted — and on the free version, removing that twin would spend one of the month's removals on
    /// a machine that is not there. Left alone while anything is still connected under it.
    /// </summary>
    private void ForgetReinstalledId(string previous, string machineName)
    {
        lock (_lock)
        {
            if (_deviceConns.Any(d => d.DeviceId == previous)) return;
            if (!_devices.Remove(previous)) return;
            _deviceKeys.Remove(previous);
            _hardware.Remove(previous);
            _unattendedPw.Remove(previous);
            _savedPw.Remove(previous);
            _closedByUser.Remove(previous);
            _pwGuesses.Remove(previous);
        }
        BroadcastTechs(new DeviceGoneMsg { DeviceId = previous, MachineName = machineName });
    }

    /// <summary>
    /// The installer's question, asked BEFORE it installs anything (Stage D): would this computer be let on?
    /// Null for yes. The hello is still the rule — two installs racing for the last place are settled there.
    /// </summary>
    public string? FreeWouldRefuse(string hardwareId)
    {
        lock (_lock) return FreeRefusesHello(new HelloMsg { DeviceId = "", HardwareId = hardwareId ?? "" });
    }

    /// <summary>What a free company's console may ask of the hub. Everything else is a part of the full
    /// version — tickets, chat, outside help, health checks, Processes, Wake up — and is refused in words.</summary>
    private static bool ConsoleMaySendOnFree(Msg m) =>
        m is ConnectDeviceMsg or EndSessionMsg or PingMsg
        or ForgetSavedPasswordMsg or SavedPasswordWhoRequestMsg
        or DeviceRemoveMsg or PortalLinkRequestMsg
        // Saying a computer installed again is theirs (2026-10-07, §111) — the free version has reinstalls too.
        or ConfirmReinstallMsg;

    /// <summary>Once per kind per session, so a console sending something it should not is a log line, not a flood.</summary>
    private void RefusedOnFree(Session s, Msg m)
    {
        bool first; lock (_lock) first = _refusedSaid.Add(s.Info.SessionId + "|free|" + m.GetType().Name);
        if (first) _log.LogInformation("[{Tenant}] {Kind} in the session on {Machine} is not part of the free version; dropped",
            TenantName, m.GetType().Name, s.Info.MachineName);
    }

    /// <summary>
    /// Takes a machine off the company's list: the free version's Remove (twice a month, no reason), and since
    /// 2026-10-06 the full version's too (a supervisor or the owner, with a reason, no limit). Its sessions end, its connections
    /// are told why and closed, its row goes (and with it everybody's remembered password for it), and the
    /// removal is recorded — which is what the monthly limit counts and what refuses the install if it
    /// comes back. Returns why not, or empty.
    /// </summary>
    public async Task<string> RemoveDeviceAsync(string deviceId, string who, TenantRole role, string reason = "", string note = "")
    {
        // The full version (2026-10-06, Clint: "for when machines get upgraded, replaced or a person has been
        // using their own private device"): a supervisor or the owner, with a reason, and no monthly limit.
        if (!Free)
        {
            if (!Permissions.CanRemoveDevices(role)) return "Only a supervisor or the owner can remove a machine.";
            if (RemovalReasons.Problem(reason, note) is { Length: > 0 } bad) return bad;
        }
        string machine;
        lock (_lock)
        {
            if (!_devices.TryGetValue(deviceId, out var info) || IsWeb(deviceId)) return "That machine is not on the list.";
            machine = info.MachineName;
        }

        await using var db = await _dbf.CreateDbContextAsync();
        var (left, next) = Free ? await RemovalsLeftAsync(db) : (1, default);
        if (Free && left <= 0)
            return $"{Cap(FreePlan.TheNameOf(_plan))} can remove {FreePlan.RemovalsPerMonth} computers a month, and this month's are used. "
                 + $"The next can be removed on {next:d MMMM}.";

        await RemoveCoreAsync(db, deviceId, machine, who, Free ? "" : reason, Free ? "" : (note ?? "").Trim(), uninstallAgent: true);
        if (Free)
            _log.LogInformation("[{Tenant}] {Who} removed {Machine} ({Id}); {Left} removal(s) left this month", TenantName, who, machine, deviceId, left - 1);
        else
            _log.LogInformation("[{Tenant}] {Who} removed {Machine} ({Id}): {Reason}{Note}", TenantName, who, machine, deviceId, reason, string.IsNullOrWhiteSpace(note) ? "" : " — " + note.Trim());
        return "";
    }

    /// <summary>
    /// The person at the machine chose Uninstall… in the tray app and Windows let it run (2026-10-06, Clint: "an
    /// uninstall option of the client app tray for someone who no longer wants to use the product"). Their own
    /// machine, their decision: nobody at the company is asked. It leaves the list as "Uninstalled at the machine",
    /// by whoever was signed in there, so the company sees it went and why. Only the CLIENT connection may say so.
    /// The uninstaller is already running over there, so the agent is not told to run another.
    /// </summary>
    private async Task UninstalledAtMachineAsync(DeviceConn dc)
    {
        if (dc.Role != DeviceRole.Client)
        {
            _log.LogWarning("[{Tenant}] a {Role} connection said {Id} was uninstalled — only the tray app speaks for the person; ignored", TenantName, dc.Role, dc.DeviceId);
            return;
        }
        string machine, who;
        lock (_lock)
        {
            if (!_devices.TryGetValue(dc.DeviceId, out var info)) return;
            machine = info.MachineName;
            who = string.IsNullOrWhiteSpace(info.UserName) ? $"the person at {machine}" : $"{info.UserName} at {machine}";
        }
        await using var db = await _dbf.CreateDbContextAsync();
        await RemoveCoreAsync(db, dc.DeviceId, machine, who, UninstalledAtMachine, "", uninstallAgent: false);
        _log.LogInformation("[{Tenant}] {Machine} ({Id}) was uninstalled by {Who}", TenantName, machine, dc.DeviceId, who);
    }

    /// <summary>The reason a machine uninstalled by the person at it is recorded with.</summary>
    public const string UninstalledAtMachine = "Uninstalled at the machine";

    /// <summary>
    /// What every removal does, however it was asked for: the row goes (its tickets, sessions and reports stay),
    /// the removal is recorded, the install id is refused if it comes back, its sessions end, its connections are
    /// told and closed — the agent told to uninstall itself when <paramref name="uninstallAgent"/> — and every
    /// console drops the row.
    /// </summary>
    private async Task RemoveCoreAsync(PlatformDb db, string deviceId, string machine, string who, string reason, string note, bool uninstallAgent)
    {
        var row = await db.Devices.FirstOrDefaultAsync(d => d.TenantId == TenantId && d.DeviceId == deviceId);
        if (row != null) db.Devices.Remove(row);
        db.DeviceRemovals.Add(new DeviceRemoval { TenantId = TenantId, DeviceId = deviceId, MachineName = machine, RemovedBy = who, Reason = reason, Note = note });
        await db.SaveChangesAsync();

        List<string> sessions; List<DeviceConn> conns;
        lock (_lock)
        {
            _removedIds.Add(deviceId);
            sessions = _sessions.Values.Where(s => s.Info.DeviceId == deviceId).Select(s => s.Info.SessionId).ToList();
            conns = _deviceConns.Where(d => d.DeviceId == deviceId).ToList();
        }
        foreach (var id in sessions) EndSession(id, $"{machine} was removed from the account by {who}.");
        foreach (var c in conns)
        {
            // The agent takes itself off the machine (Clint's answer); a tray app is simply refused, and goes with it.
            c.Ws.Send(new ErrorMsg { Message = "This computer was removed from the account.", NotAdmitted = true, Uninstall = uninstallAgent && c.Role == DeviceRole.Agent });
            _ = c.Ws.CloseAsync("removed");
        }
        lock (_lock)
        {
            _deviceConns.RemoveAll(d => d.DeviceId == deviceId);
            _devices.Remove(deviceId);
            _deviceKeys.Remove(deviceId);
            _unattendedPw.Remove(deviceId);
            _savedPw.Remove(deviceId);
            _hardware.Remove(deviceId);
            _closedByUser.Remove(deviceId);
            _pwGuesses.Remove(deviceId);
        }
        BroadcastTechs(new DeviceGoneMsg { DeviceId = deviceId, MachineName = machine });
        PushSavedPasswords();
    }

    /// <summary>How many removals are left this calendar month (company time), and when the next month starts.</summary>
    public async Task<(int Left, DateTime NextMonth)> RemovalsLeftAsync(PlatformDb db)
    {
        var now = CompanyClock.Now;
        var monthStart = new DateTime(now.Year, now.Month, 1);
        var fromUtc = CompanyClock.ToUtc(monthStart);
        int used = await db.DeviceRemovals.CountAsync(r => r.TenantId == TenantId && r.RemovedUtc >= fromUtc);
        return (Math.Max(0, FreePlan.RemovalsPerMonth - used), monthStart.AddMonths(1));
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private async Task RemoveDeviceForConsoleAsync(TechConn tc, DeviceRemoveMsg m)
    {
        var why = await RemoveDeviceAsync(m.DeviceId, tc.Who.DisplayName, tc.Who.Role, m.Reason, m.Note);
        if (why.Length > 0) tc.Ws.Send(new ErrorMsg { Message = why });
    }
}
