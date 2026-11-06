using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Hub;

/// <summary>
/// COPYING FILES STRAIGHT FROM ONE MACHINE TO THE OTHER (2026-10-15; docs/NEXT-SESSION-HOST2HOST.md, HANDOVER §144). Clint:
/// with two machines open, copy on one and paste on the other "directly between both machines ... so that it doesnt go
/// through the technician PC". The console asks for a BRIDGE between its two sessions for one copy; this joins the two
/// hosts, frame by frame, by a bridge id it can read and nothing else — the files are sealed under a key only the two hosts
/// hold (<see cref="BridgeSeal"/>).
///
/// <para><b><see cref="BridgeProblem"/> is THE rule of who may</b>, and it is checked here whatever the console decided:</para>
/// <list type="bullet">
/// <item>the same technician has a viewer socket in BOTH sessions now (<c>Session.ViewerIds</c>, §143);</item>
/// <item>both are this company's own: never an outsider's visit, a guest, or browser support (view only);</item>
/// <item>both are sealed end to end (§112) — the key travels inside each session's seal, so a clear session would hand
/// it to us;</item>
/// <item>both hosts said they can (<see cref="HostCan.Bridge"/>) and both machines' people have said yes;</item>
/// <item>not over <see cref="FileSetLimits.MaxBytes"/>; one copy into a machine at a time (a newer one replaces it).</item>
/// </list>
/// <para>It ends when either host says so, the console cancels, either session ends, the technician leaves either one,
/// or nothing has moved for <see cref="BridgeSeal.StallMs"/> — and both hosts and the technician's windows are told.</para>
/// </summary>
public sealed partial class TenantHub
{
    private sealed class BridgeRoute
    {
        public required string Id;
        public required Session From, To;
        public required int TechnicianId;
        public required string Tech;
        public long LastMs = Environment.TickCount64;
        public bool Over;
    }

    private readonly Dictionary<string, BridgeRoute> _bridges = new();
    /// <summary>The two machines of a bridge, kept a while after it ends: the sending host writes its ticket line once the
    /// other end has said the files landed, which is after the route is gone.</summary>
    private readonly Dictionary<string, (string FromSession, string FromName, string ToSession, string ToName, DateTime At)> _bridgeNames = new();

    /// <summary>Null when this technician may bridge <paramref name="from"/> to <paramref name="to"/>; else the words.</summary>
    private string? BridgeProblem(Session from, Session to, int technicianId, long bytes)
    {
        lock (_lock)
        {
            if (from == to) return "that is the same machine";
            if (!_sessions.ContainsKey(from.Info.SessionId) || !_sessions.ContainsKey(to.Info.SessionId)) return "one of the two sessions has ended";
            if (!from.ViewerIds.ContainsValue(technicianId) || !to.ViewerIds.ContainsValue(technicianId))
                return "you need both machines open yourself";
            if (from.Visitor != null || to.Visitor != null) return "one of the two sessions belongs to somebody from outside the company";
            if (from.Info.ViewOnly || to.Info.ViewOnly) return "one of the two is browser support, which cannot take files";
            if (from.Host == null || to.Host == null || from.Info.AwaitingConsent || to.Info.AwaitingConsent)
                return "one of the two machines is not connected";
            if (from.E2eSealed != true || to.E2eSealed != true) return "one of the two sessions is not end-to-end encrypted";
            if (!HostCan.Bridge(from.LastSysInfo) || !HostCan.Bridge(to.LastSysInfo)) return "one of the two machines is running an older app";
            if (bytes > FileSetLimits.MaxBytes) return $"it is over {FileSetLimits.Human(FileSetLimits.MaxBytes)}";
        }
        return null;
    }

    /// <summary>A console asking, on the RECEIVING session's socket, to bridge its other session to this one.</summary>
    private void AskBridge(Session to, WsConnection viewer, BridgeAskMsg ask)
    {
        int techId; string tech; Session? from;
        lock (_lock)
        {
            to.ViewerIds.TryGetValue(viewer, out techId);
            tech = to.Viewers.TryGetValue(viewer, out var name) ? name : "";
            _sessions.TryGetValue(ask.FromSessionId ?? "", out from);
        }
        string? problem = !BridgeSeal.IsId(ask.BridgeId) ? "the copy had no proper id"
            : techId == 0 ? "only the company's own technicians can do this"
            : from == null ? "the other session has ended"
            : BridgeProblem(from, to, techId, ask.Bytes);
        if (problem != null)
        {
            _log.LogInformation("[{Tenant}] {Tech} asked to copy straight from session {From} to {To} — refused: {Why}",
                TenantName, tech, ask.FromSessionId, to.Info.SessionId, problem);
            viewer.Send(new BridgeEndMsg { BridgeId = ask.BridgeId, Refused = true, Why = problem });
            return;
        }
        var route = new BridgeRoute { Id = ask.BridgeId, From = from!, To = to, TechnicianId = techId, Tech = tech };
        List<BridgeRoute> replaced;
        lock (_lock)
        {
            if (_bridges.ContainsKey(ask.BridgeId)) return;   // asked twice
            replaced = _bridges.Values.Where(r => r.To == to).ToList();
            _bridges[route.Id] = route;
            var now = DateTime.UtcNow;
            foreach (var old in _bridgeNames.Where(kv => now - kv.Value.At > TimeSpan.FromMinutes(10)).Select(kv => kv.Key).ToList()) _bridgeNames.Remove(old);
            _bridgeNames[route.Id] = (from!.Info.SessionId, from.Info.MachineName, to.Info.SessionId, to.Info.MachineName, now);
        }
        foreach (var r in replaced) EndBridge(r, new BridgeEndMsg { BridgeId = r.Id, Why = "a newer copy took its place" });
        _log.LogInformation("[{Tenant}] {Tech}: copying {Items} item(s), {Size}, straight from {From} to {To} (bridge {Id})",
            TenantName, tech, ask.Items, FileSetLimits.Human(ask.Bytes), from!.Info.MachineName, to.Info.MachineName, route.Id);
        // Yes is the ask sent back: only now does the console hand each machine the key, so a refusal never leaves a host
        // waiting for a partner that is not coming.
        viewer.Send(ask);
        _ = WatchBridgeAsync(route);
    }

    /// <summary>A bridge frame from a host: to the OTHER end of its route, and nowhere else — never to a viewer.</summary>
    private void HostBridgeFrame(Session s, byte[] frame)
    {
        var id = BridgeSeal.IdOf(frame);
        BridgeRoute? r = null;
        lock (_lock) { if (id != null) _bridges.TryGetValue(id, out r); }
        if (r == null || r.Over) return;
        var other = s == r.From ? r.To : s == r.To ? r.From : null;
        if (other == null) return;   // a host that is not either end of it
        r.LastMs = Environment.TickCount64;
        var host = other.Host;
        // Waiting for room rather than dropping: a lost chunk is a corrupt file. The sending host keeps within a window of
        // what the receiver has written, so this does not hold its socket (and its picture) for long.
        if (host == null || !BinaryRelay.Send(host.SendBinary, frame))
            EndBridge(r, new BridgeEndMsg { BridgeId = r.Id, Why = $"{other.Info.MachineName} could not keep up" });
    }

    /// <summary>The end of a bridge said by a host or the console. Only somebody it belongs to may end it.</summary>
    private void BridgeEndFrom(Session s, BridgeEndMsg e, int technicianId = 0)
    {
        BridgeRoute? r; lock (_lock) _bridges.TryGetValue(e.BridgeId ?? "", out r);
        if (r == null) return;
        if (s != r.From && s != r.To) return;
        if (technicianId != 0 && technicianId != r.TechnicianId) return;
        EndBridge(r, new BridgeEndMsg { BridgeId = r.Id, Ok = e.Ok && s == r.To && technicianId == 0, Why = e.Why ?? "", Files = e.Files, Bytes = e.Bytes });
    }

    private void EndBridge(BridgeRoute r, BridgeEndMsg e)
    {
        List<WsConnection> windows;
        lock (_lock)
        {
            if (r.Over) return;
            r.Over = true;
            _bridges.Remove(r.Id);
            windows = r.From.ViewerIds.Concat(r.To.ViewerIds).Where(kv => kv.Value == r.TechnicianId).Select(kv => kv.Key).Distinct().ToList();
        }
        _log.LogInformation("[{Tenant}] bridge {Id} from {From} to {To}: {Outcome}", TenantName, r.Id, r.From.Info.MachineName, r.To.Info.MachineName,
            e.Ok ? $"landed, {e.Files} file(s), {FileSetLimits.Human(e.Bytes)}" : "ended — " + (e.Why.Length > 0 ? e.Why : "no reason given"));
        r.From.Host?.Send(e);
        r.To.Host?.Send(e);
        foreach (var w in windows) w.Send(e);
    }

    /// <summary>A session ended, or a viewer left it: any bridge that no longer has both ends — and its technician in both
    /// sessions — is over.</summary>
    private void BridgesLost(Session s, string why)
    {
        List<BridgeRoute> lost;
        lock (_lock)
            lost = _bridges.Values.Where(r => (r.From == s || r.To == s)
                && (!_sessions.ContainsKey(r.From.Info.SessionId) || !_sessions.ContainsKey(r.To.Info.SessionId)
                    || !r.From.ViewerIds.ContainsValue(r.TechnicianId) || !r.To.ViewerIds.ContainsValue(r.TechnicianId))).ToList();
        foreach (var r in lost) EndBridge(r, new BridgeEndMsg { BridgeId = r.Id, Why = why });
    }

    /// <summary>Nothing moving for the stall time ends it — a host that died mid-copy must not leave the route open.</summary>
    private async Task WatchBridgeAsync(BridgeRoute r)
    {
        try
        {
            while (!r.Over)
            {
                await Task.Delay(2000);
                if (!r.Over && Environment.TickCount64 - r.LastMs > BridgeSeal.StallMs + 5000)
                    EndBridge(r, new BridgeEndMsg { BridgeId = r.Id, Why = "nothing moved for 30 seconds" });
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] bridge watchdog {Id}", TenantName, r.Id); }
    }

    /// <summary>The ticket and the technicians get the other machine's NAME; the person at the machine is told "another
    /// computer" (Clint's answer 3). Filled in here, never believed from a host. Null for a line that is not a bridge's.</summary>
    private string? OtherMachineOf(Session s, FileActivityMsg fa)
    {
        if (string.IsNullOrEmpty(fa.BridgeId)) return null;
        lock (_lock)
        {
            if (!_bridgeNames.TryGetValue(fa.BridgeId, out var n)) return "";
            return s.Info.SessionId == n.FromSession ? n.ToName : s.Info.SessionId == n.ToSession ? n.FromName : "";
        }
    }
}
