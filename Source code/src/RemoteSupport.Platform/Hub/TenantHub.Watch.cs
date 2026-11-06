using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Hub;

/// <summary>
/// The console's Processes and Performance windows (2026-10-05): live figures from a machine
/// WITHOUT a session. Clint's rules: straight through on every online machine, attended or
/// unattended — no consent prompt and no unattended password, because it is "just to give one an
/// idea before deciding on connecting or not"; LOOK ONLY (nothing here reaches the machine except
/// start and stop); nothing logged and no ticket.
///
/// <para>The machine measures once however many technicians are looking: the first window opened
/// on it starts it, the last one closed stops it, and a console that goes away is taken off every
/// machine it was watching. The agent sends the figures where it is connected (as SYSTEM it can name
/// every process's account); the tray app otherwise. Each technician gets only the machines THEY
/// opened — figures are never broadcast.</para>
/// </summary>
public sealed partial class TenantHub
{
    /// <summary>deviceId -> the consoles with a window open on it. Guarded by <c>_lock</c>.</summary>
    private readonly Dictionary<string, HashSet<TechConn>> _watchers = new();
    /// <summary>deviceId -> the connection told to send figures. Guarded by <c>_lock</c>.</summary>
    private readonly Dictionary<string, DeviceConn> _watchSource = new();

    /// <summary>The agent if it can, the tray app if it can, else nobody. Call under <c>_lock</c>.</summary>
    private DeviceConn? PickWatchSource(string deviceId) =>
        _deviceConns.FirstOrDefault(d => d.DeviceId == deviceId && d.Role == DeviceRole.Agent && d.CanWatch)
        ?? _deviceConns.FirstOrDefault(d => d.DeviceId == deviceId && d.Role == DeviceRole.Client && d.CanWatch);

    /// <summary>Whether anything connected on the machine can send figures. Call under <c>_lock</c>.</summary>
    private void RecomputeCanWatch(DeviceInfo info) =>
        info.CanWatch = _deviceConns.Any(d => d.DeviceId == info.DeviceId && d.CanWatch);

    private void WatchMachine(TechConn tc, WatchMachineMsg m)
    {
        if (!m.On) { StopWatching(tc, m.DeviceId); return; }

        DeviceConn? source = null; string? refusal = null; bool start = false;
        lock (_lock)
        {
            if (!_devices.TryGetValue(m.DeviceId, out var info)) refusal = "That machine is not known.";
            else if (_closedByUser.Contains(m.DeviceId))
                refusal = $"The person at {info.MachineName} closed the support app, so it cannot be reached.";
            else
            {
                bool running = _watchSource.TryGetValue(m.DeviceId, out source);
                source ??= PickWatchSource(m.DeviceId);
                if (source == null)
                    refusal = info.Online
                        ? $"{info.MachineName} is running an older support client that cannot send these figures. Update the support client on it."
                        : $"{info.MachineName} is offline.";
                else
                {
                    if (!_watchers.TryGetValue(m.DeviceId, out var set)) _watchers[m.DeviceId] = set = new HashSet<TechConn>();
                    set.Add(tc);
                    if (!running) { _watchSource[m.DeviceId] = source; start = true; }
                }
            }
        }
        if (refusal != null) { tc.Ws.Send(new WatchEndedMsg { DeviceId = m.DeviceId, Reason = refusal }); return; }
        if (start) source!.Ws.Send(new WatchRunMsg { On = true });
    }

    /// <summary>One console closing its window on one machine; the last one out stops the machine measuring.</summary>
    private void StopWatching(TechConn tc, string deviceId)
    {
        DeviceConn? stop = null;
        lock (_lock)
        {
            if (!_watchers.TryGetValue(deviceId, out var set) || !set.Remove(tc) || set.Count > 0) return;
            _watchers.Remove(deviceId);
            if (_watchSource.Remove(deviceId, out var src)) stop = src;
        }
        stop?.Ws.Send(new WatchRunMsg { On = false });
    }

    /// <summary>A console has gone: off every machine it was watching.</summary>
    private void ForgetWatcher(TechConn tc)
    {
        List<string> ids;
        lock (_lock) ids = _watchers.Where(kv => kv.Value.Contains(tc)).Select(kv => kv.Key).ToList();
        foreach (var id in ids) StopWatching(tc, id);
    }

    /// <summary>
    /// A device connection has gone. If it was sending figures, the other one takes over (the agent
    /// restarting, with the tray app still there) — and if there is no other, every console watching
    /// is told why its window stopped rather than left looking at a frozen graph.
    /// </summary>
    private void WatchSourceGone(DeviceConn dc)
    {
        DeviceConn? next = null; List<TechConn> told = new(); string name = dc.DeviceId;
        lock (_lock)
        {
            if (!_watchSource.TryGetValue(dc.DeviceId, out var src) || src != dc) return;
            next = PickWatchSource(dc.DeviceId);
            if (next != null) _watchSource[dc.DeviceId] = next;
            else
            {
                _watchSource.Remove(dc.DeviceId);
                if (_watchers.Remove(dc.DeviceId, out var set)) told = set.ToList();
                if (_devices.TryGetValue(dc.DeviceId, out var info)) name = info.MachineName;
            }
        }
        if (next != null) { next.Ws.Send(new WatchRunMsg { On = true }); return; }
        foreach (var t in told) t.Ws.Send(new WatchEndedMsg { DeviceId = dc.DeviceId, Reason = $"{name} went offline." });
    }

    /// <summary>Everyone watching a machine is stopped, with a reason — its person closed the support app.</summary>
    private void EndWatching(string deviceId, string reason)
    {
        DeviceConn? src; List<TechConn> told = new();
        lock (_lock)
        {
            _watchSource.Remove(deviceId, out src);
            if (_watchers.Remove(deviceId, out var set)) told = set.ToList();
        }
        src?.Ws.Send(new WatchRunMsg { On = false });
        foreach (var t in told) t.Ws.Send(new WatchEndedMsg { DeviceId = deviceId, Reason = reason });
    }

    /// <summary>
    /// A reading from the machine, to the consoles watching it and nobody else. Only from the
    /// connection that was asked: anything else sending figures is told to stop — a tray app left
    /// measuring after the agent took over must not double the traffic or interleave two readings.
    /// </summary>
    private void RelayWatchSample(DeviceConn dc, WatchSampleMsg m)
    {
        List<TechConn> to; bool stray = false;
        lock (_lock)
        {
            if (!_watchSource.TryGetValue(dc.DeviceId, out var src) || src != dc) { to = new(); stray = true; }
            else to = _watchers.TryGetValue(dc.DeviceId, out var set) ? set.ToList() : new();
        }
        if (stray) { dc.Ws.Send(new WatchRunMsg { On = false }); return; }
        m.DeviceId = dc.DeviceId;
        foreach (var t in to) t.Ws.Send(m);
    }
}
