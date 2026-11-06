using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Hub;

/// <summary>
/// A company that can no longer use the service — suspended, closed, rejected — leaves NOW (2026-10-13,
/// <see cref="Services.AdminCompanies"/>). Before this, suspending stopped the next sign-in and the next
/// connect only: a console already open kept working and a machine already connected stayed connected.
///
/// <para>Every session ends, every console is told why and closed, every machine connection (tray app, agent,
/// a browser caller) is told and closed. They come back by themselves the way they always do, and are refused
/// at the door (<c>/ws/device</c>, <c>AuthService</c>) for as long as the company stays off — and let in again,
/// with nothing to do at the machine, once it is active.</para>
/// </summary>
public sealed partial class TenantHub
{
    /// <summary>Ends everything of this company's. Answers how many consoles and machine connections were closed.</summary>
    public (int consoles, int machines) TurnAway(string why)
    {
        List<TechConn> consoles; List<DeviceConn> machines; List<string> sessions;
        lock (_lock)
        {
            consoles = _techs.ToList();
            machines = _deviceConns.ToList();
            sessions = _sessions.Keys.ToList();
        }
        foreach (var id in sessions) { try { EndSession(id, why); } catch { } }
        foreach (var c in consoles) { try { c.Ws.Send(new SignedOutMsg { Reason = why }); _ = c.Ws.CloseAsync("service off"); } catch { } }
        foreach (var d in machines) { try { d.Ws.Send(new ErrorMsg { Message = why + " Please contact them." }); _ = d.Ws.CloseAsync("service off"); } catch { } }
        _log.LogWarning("[{Tenant}] turned away: {Why} ({Consoles} console(s), {Machines} machine connection(s), {Sessions} session(s) ended)",
            TenantName, why, consoles.Count, machines.Count, sessions.Count);
        return (consoles.Count, machines.Count);
    }
}

public sealed partial class TenantHub
{
    /// <summary>
    /// What this company holds open right now (2026-10-13, the control dashboard): consoles, machine connections (a
    /// machine's tray app and agent are two), machines online and sessions — what a server's capacity is spent on.
    /// </summary>
    public (int consoles, int machineConnections, int machinesOnline, int sessions) LoadNow()
    {
        lock (_lock) return (_techs.Count, _deviceConns.Count, _devices.Values.Count(d => d.Online), _sessions.Count);
    }
}
