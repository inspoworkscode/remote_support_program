using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Hub;

/// <summary>
/// Auto-update at the hub (2026-10-16, stage C). The rules are <see cref="Services.UpdateService"/>'s; this is only the
/// wire: which agents could take an update, handing one an offer (<see cref="Services.UpdateSweep"/> decides who and when),
/// and passing what the agent then reports to the service. Only an AGENT that said <see cref="DeviceCan.Update"/> is ever
/// offered one, and only an agent's report is taken — a tray app has no business saying how an update went.
/// </summary>
public sealed partial class TenantHub
{
    /// <summary>This company's ring (<see cref="Services.UpdateService.RingOf"/>), set when it loads and when its plan changes.</summary>
    private volatile int _ring = 2;

    private void ApplyRing(Data.Tenant t) => _ring = Services.UpdateService.RingOf(t);

    /// <summary>The ring as the sweep last read it from the database — a company marked not billed, or a pilot, later on.</summary>
    public int Ring { get => _ring; set => _ring = value; }

    /// <summary>Every connected agent that updates itself: its machine, its name and the version it said.</summary>
    public List<(string DeviceId, string Machine, string AgentVersion)> UpdateCandidates()
    {
        lock (_lock)
            return _deviceConns.Where(c => c.Role == DeviceRole.Agent && c.CanUpdate)
                .Select(c => _devices.TryGetValue(c.DeviceId, out var i) ? (c.DeviceId, i.MachineName, i.AgentVersion) : (c.DeviceId, "", ""))
                .ToList();
    }

    /// <summary>Hands the machine's agent the offer. False when it is no longer connected.</summary>
    public bool OfferUpdate(string deviceId, UpdateRelease release)
    {
        DeviceConn? agent;
        lock (_lock) agent = _deviceConns.LastOrDefault(c => c.DeviceId == deviceId && c.Role == DeviceRole.Agent && c.CanUpdate);
        if (agent == null) return false;
        agent.Ws.Send(new UpdateOfferMsg { Release = release });
        return true;
    }

    private void TakeUpdateReport(DeviceConn dc, UpdateReportMsg report)
    {
        if (dc.Role != DeviceRole.Agent || _registry.Updates is not { } updates) return;
        string machine;
        lock (_lock) machine = _devices.TryGetValue(dc.DeviceId, out var i) ? i.MachineName : "";
        _ = updates.ReportAsync(TenantId, dc.DeviceId, machine, report);
    }
}
