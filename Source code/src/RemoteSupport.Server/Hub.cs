using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using RemoteSupport.Shared;

namespace RemoteSupport.Server;

/// <summary>
/// All live state: signed-in technicians, connected devices, ringing calls, active sessions,
/// and the relay between session hosts and viewers. Single-process, in-memory, lock-guarded.
/// </summary>
public sealed class Hub
{
    private readonly object _lock = new();
    private readonly ILogger<Hub> _log;
    private readonly string _dataDir;

    // auth tokens for technicians (token -> display name)
    private readonly ConcurrentDictionary<string, string> _techTokens = new();
    // known device keys (deviceId -> key) to stop id spoofing
    private readonly Dictionary<string, string> _deviceKeys;
    private readonly Dictionary<string, DeviceInfo> _devices = new();

    private sealed class TechConn { public string Name = ""; public WsConnection Ws = null!; }
    private sealed class DeviceConn { public string DeviceId = ""; public DeviceRole Role; public WsConnection Ws = null!; }

    private readonly List<TechConn> _techs = new();
    private readonly List<DeviceConn> _deviceConns = new();
    private readonly Dictionary<string, CallInfo> _calls = new();
    private readonly Dictionary<string, Session> _sessions = new();
    private readonly LinkedList<ChatMsg> _chat = new();

    private sealed class Session
    {
        public SessionInfo Info = new();
        public string HostToken = "";
        public WsConnection? Host;
        public readonly Dictionary<WsConnection, string> Viewers = new(); // ws -> tech name
        public string? CallId;
        /// <summary>Replayed to technicians who join after the host has already announced itself.</summary>
        public MonitorsMsg? LastMonitors;
        public SysInfoMsg? LastSysInfo;
        // Mirrors TenantHub — see the comments there for why each of these exists.
        public readonly HashSet<WsConnection> Leaving = new();
        public int GraceToken;
        public DateTime? HeldSince;
        public bool Listening;
    }

    /// <summary>Mirrors TenantHub.GracePeriod. Both hubs must agree, or a fix applied to one
    /// leaves the other behaving differently on the same protocol.</summary>
    private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(90);

    public Hub(ILogger<Hub> log, IConfiguration cfg)
    {
        _log = log;
        _dataDir = cfg["RemoteSupport:DataDirectory"] ?? "data";
        Directory.CreateDirectory(_dataDir);
        var p = Path.Combine(_dataDir, "devices.json");
        _deviceKeys = File.Exists(p) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(p)) ?? new() : new();
        var dp = Path.Combine(_dataDir, "device-info.json");
        if (File.Exists(dp))
            foreach (var d in JsonSerializer.Deserialize<List<DeviceInfo>>(File.ReadAllText(dp), Json.Options) ?? new())
            { d.Online = d.Unattended = d.ClientOnline = false; _devices[d.DeviceId] = d; }
    }

    // ------------------------------------------------------------------ auth
    public string IssueTechToken(string displayName)
    {
        var tok = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        _techTokens[tok] = displayName;
        return tok;
    }
    public string? TechFromToken(string? token) => token != null && _techTokens.TryGetValue(token, out var n) ? n : null;

    // ------------------------------------------------------------------ technician socket
    public async Task RunTechnicianAsync(string name, WsConnection ws, CancellationToken ct)
    {
        var tc = new TechConn { Name = name, Ws = ws };
        lock (_lock)
        {
            _techs.Add(tc);
            ws.Send(new StateMsg
            {
                You = name,
                OnlineTechnicians = _techs.Select(t => t.Name).Distinct().ToList(),
                Devices = _devices.Values.OrderByDescending(d => d.Online).ThenBy(d => d.MachineName).ToList(),
                Calls = _calls.Values.Where(c => c.Status is CallStatus.Ringing or CallStatus.Accepted).ToList(),
                Sessions = _sessions.Values.Select(s => s.Info).ToList(),
                RecentChat = _chat.TakeLast(200).ToList()
            });
        }
        BroadcastTechPresence();
        _log.LogInformation("Technician {Name} signed in", name);

        ws.OnMessage += m => HandleTechMessage(tc, m);
        await ws.RunAsync(ct);

        lock (_lock) _techs.Remove(tc);
        BroadcastTechPresence();
        _log.LogInformation("Technician {Name} disconnected", name);
    }

    private void HandleTechMessage(TechConn tc, Msg m)
    {
        try
        {
            switch (m)
            {
                case PingMsg: break;
                case AcceptCallMsg a: AcceptCall(tc, a.CallId); break;
                case ConnectDeviceMsg c: ConnectDevice(tc, c.DeviceId); break;
                case EndSessionMsg e: EndSession(e.SessionId, $"Ended by {tc.Name}"); break;
                case ChatMsg chat:
                    chat.From = tc.Name; chat.FromTech = true; chat.Ts = DateTimeOffset.UtcNow;
                    RouteChat(chat);
                    break;
                case JoinSessionMsg: break; // joining happens on the session socket
            }
        }
        catch (Exception ex) { _log.LogError(ex, "tech message"); tc.Ws.Send(new ErrorMsg { Message = ex.Message }); }
    }

    private void BroadcastTechPresence()
    {
        List<string> names; List<TechConn> techs;
        lock (_lock) { names = _techs.Select(t => t.Name).Distinct().ToList(); techs = _techs.ToList(); }
        foreach (var t in techs) t.Ws.Send(new TechPresenceMsg { OnlineTechnicians = names });
    }

    private void BroadcastTechs(Msg m)
    {
        List<TechConn> techs; lock (_lock) techs = _techs.ToList();
        foreach (var t in techs) t.Ws.Send(m);
    }

    // ------------------------------------------------------------------ device socket
    public async Task RunDeviceAsync(WsConnection ws, CancellationToken ct)
    {
        DeviceConn? dc = null;
        var helloTcs = new TaskCompletionSource<HelloMsg?>();
        ws.OnMessage += m =>
        {
            if (dc == null) { if (m is HelloMsg h) helloTcs.TrySetResult(h); return; }
            HandleDeviceMessage(dc, m);
        };
        ws.OnClosed += _ => helloTcs.TrySetResult(null);
        var run = ws.RunAsync(ct);

        var hello = await Task.WhenAny(helloTcs.Task, Task.Delay(10000, ct)) == helloTcs.Task ? helloTcs.Task.Result : null;
        if (hello == null || string.IsNullOrWhiteSpace(hello.DeviceId)) { await ws.CloseAsync("no hello"); return; }

        lock (_lock)
        {
            if (_deviceKeys.TryGetValue(hello.DeviceId, out var key) && key != hello.DeviceKey)
            {
                // Mirrors TenantHub: a refused device used to be invisible from both ends.
                _log.LogWarning("REFUSED {Machine}: it presented device id {Id} with the wrong key. " +
                                "Usually a machine restored from an image, or a device.json copied between machines — reinstall the client on it.",
                    hello.MachineName, hello.DeviceId);
                ws.Send(new ErrorMsg { Message = "This machine's identity does not match our records. Please reinstall the support client." });
                _ = ws.CloseAsync("bad key"); return;
            }
            if (!_deviceKeys.ContainsKey(hello.DeviceId)) { _deviceKeys[hello.DeviceId] = hello.DeviceKey; SaveDeviceKeys(); }

            if (!_devices.TryGetValue(hello.DeviceId, out var info)) _devices[hello.DeviceId] = info = new DeviceInfo { DeviceId = hello.DeviceId };
            info.MachineName = hello.MachineName; info.Os = hello.Os; info.LastSeen = DateTimeOffset.UtcNow;
            if (!string.IsNullOrEmpty(hello.UserName)) info.UserName = hello.UserName;
            if (hello.Role == DeviceRole.Agent) info.Unattended = true; else info.ClientOnline = true;
            info.Online = true;

            dc = new DeviceConn { DeviceId = hello.DeviceId, Role = hello.Role, Ws = ws };
            // replace stale connection of same role
            _deviceConns.RemoveAll(d => d.DeviceId == dc.DeviceId && d.Role == dc.Role);
            _deviceConns.Add(dc);
            SaveDeviceInfo();
        }
        _log.LogInformation("Device {Name} ({Role}) online", hello.MachineName, hello.Role);
        BroadcastTechs(new DeviceUpdateMsg { Device = Snapshot(hello.DeviceId) });

        // Tell a device that appears mid-session what is actually true. Mirrors TenantHub —
        // a reconnecting tray app used to be told nothing and stayed on "support in progress".
        if (dc.Role == DeviceRole.Client)
        {
            Session? live; lock (_lock) live = _sessions.Values.FirstOrDefault(x => x.Info.DeviceId == dc.DeviceId);
            if (live != null) NotifyDeviceSessionState(live);
            else ws.Send(new SessionStateMsg { Active = false });
        }

        await run;

        lock (_lock)
        {
            _deviceConns.Remove(dc);
            if (_devices.TryGetValue(dc.DeviceId, out var info))
            {
                if (dc.Role == DeviceRole.Agent) info.Unattended = _deviceConns.Any(d => d.DeviceId == dc.DeviceId && d.Role == DeviceRole.Agent);
                else info.ClientOnline = _deviceConns.Any(d => d.DeviceId == dc.DeviceId && d.Role == DeviceRole.Client);
                info.Online = info.Unattended || info.ClientOnline;
                info.LastSeen = DateTimeOffset.UtcNow;
            }
            // cancel ringing calls from this device if the client app went away
            if (dc.Role == DeviceRole.Client)
                foreach (var c in _calls.Values.Where(c => c.DeviceId == dc.DeviceId && c.Status == CallStatus.Ringing).ToList())
                { c.Status = CallStatus.Cancelled; BroadcastTechs(new CallUpdateMsg { Call = c }); }
        }
        BroadcastTechs(new DeviceUpdateMsg { Device = Snapshot(dc.DeviceId) });
        _log.LogInformation("Device {Id} ({Role}) offline", dc.DeviceId, dc.Role);
    }

    private DeviceInfo Snapshot(string id) { lock (_lock) return _devices.TryGetValue(id, out var d) ? Clone(d) : new DeviceInfo { DeviceId = id }; }
    private static DeviceInfo Clone(DeviceInfo d) => Json.Deserialize<DeviceInfo>(Json.Serialize(d))!;

    private void HandleDeviceMessage(DeviceConn dc, Msg m)
    {
        try
        {
            switch (m)
            {
                case PingMsg: break;
                case RequestCallMsg r:
                {
                    CallInfo call;
                    lock (_lock)
                    {
                        // one ringing call per device
                        foreach (var old in _calls.Values.Where(c => c.DeviceId == dc.DeviceId && c.Status == CallStatus.Ringing)) old.Status = CallStatus.Cancelled;
                        call = new CallInfo
                        {
                            CallId = Guid.NewGuid().ToString("N"), DeviceId = dc.DeviceId,
                            MachineName = _devices[dc.DeviceId].MachineName, ContactName = r.ContactName, Issue = r.Issue, Status = CallStatus.Ringing
                        };
                        _calls[call.CallId] = call;
                    }
                    dc.Ws.Send(new CallUpdateMsg { Call = call });
                    BroadcastTechs(new RingMsg { Call = call });
                    _log.LogInformation("Call {Id} ringing from {Name}", call.CallId, call.MachineName);
                    break;
                }
                case CancelCallMsg c:
                {
                    List<CallInfo> cancelled = new();
                    lock (_lock)
                        foreach (var call in _calls.Values.Where(x => x.DeviceId == dc.DeviceId && x.Status == CallStatus.Ringing && (c.CallId == null || x.CallId == c.CallId)))
                        { call.Status = CallStatus.Cancelled; cancelled.Add(call); }
                    foreach (var call in cancelled) { BroadcastTechs(new CallUpdateMsg { Call = call }); dc.Ws.Send(new CallUpdateMsg { Call = call }); }
                    break;
                }
                case ChatMsg chat:
                    chat.DeviceId = dc.DeviceId; chat.FromTech = false; chat.Ts = DateTimeOffset.UtcNow;
                    if (string.IsNullOrWhiteSpace(chat.From)) chat.From = _devices[dc.DeviceId].MachineName;
                    RouteChat(chat);
                    break;
            }
        }
        catch (Exception ex) { _log.LogError(ex, "device message"); }
    }

    // ------------------------------------------------------------------ chat
    private void RouteChat(ChatMsg chat)
    {
        List<TechConn> techs; List<DeviceConn> clients; List<WsConnection> viewers;
        lock (_lock)
        {
            _chat.AddLast(chat); while (_chat.Count > 500) _chat.RemoveFirst();
            techs = _techs.ToList();
            clients = _deviceConns.Where(d => d.DeviceId == chat.DeviceId && d.Role == DeviceRole.Client).ToList();
            // Session windows listen on their own viewer socket, not on the technician's dashboard
            // connection, so a reply that only went to _techs never reached the window it belongs to.
            viewers = _sessions.Values.Where(s => s.Info.DeviceId == chat.DeviceId)
                               .SelectMany(s => s.Viewers.Keys).ToList();
        }
        foreach (var t in techs) t.Ws.Send(chat);
        foreach (var c in clients) c.Ws.Send(chat);
        foreach (var v in viewers) v.Send(chat);
        try { File.AppendAllText(Path.Combine(_dataDir, "chat.jsonl"), Json.Serialize(chat) + Environment.NewLine); } catch { }
    }

    // ------------------------------------------------------------------ calls & sessions
    private void AcceptCall(TechConn tc, string callId)
    {
        CallInfo? call; Session? existing = null;
        lock (_lock)
        {
            if (!_calls.TryGetValue(callId, out call)) { tc.Ws.Send(new ErrorMsg { Message = "Call no longer exists." }); return; }
            if (call.Status == CallStatus.Accepted && call.SessionId != null && _sessions.TryGetValue(call.SessionId, out existing))
            {
                // someone else accepted already; let this tech join the same session
                tc.Ws.Send(new SessionReadyMsg { Session = existing.Info });
                return;
            }
            if (call.Status != CallStatus.Ringing) { tc.Ws.Send(new ErrorMsg { Message = "Call is no longer ringing." }); return; }
            call.Status = CallStatus.Accepted; call.AcceptedBy = tc.Name;
        }
        var session = StartSession(call.DeviceId, tc, call);
        if (session == null)
        {
            lock (_lock) { call.Status = CallStatus.Ringing; call.AcceptedBy = null; }
            tc.Ws.Send(new ErrorMsg { Message = "The client is no longer connected." });
            return;
        }
        call.SessionId = session.Info.SessionId;
        BroadcastTechs(new CallUpdateMsg { Call = call });
        foreach (var d in DeviceConns(call.DeviceId, DeviceRole.Client)) d.Ws.Send(new CallUpdateMsg { Call = call });
    }

    private void ConnectDevice(TechConn tc, string deviceId)
    {
        Session? existing;
        lock (_lock) existing = _sessions.Values.FirstOrDefault(s => s.Info.DeviceId == deviceId);
        if (existing != null) { tc.Ws.Send(new SessionReadyMsg { Session = existing.Info }); return; }
        var s = StartSession(deviceId, tc, null);
        if (s == null) tc.Ws.Send(new ErrorMsg { Message = "Device is offline." });
    }

    /// <summary>Creates a session and tells the device (agent preferred, else client app) to host it.</summary>
    private Session? StartSession(string deviceId, TechConn tc, CallInfo? call)
    {
        DeviceConn? hostConn;
        Session s;
        lock (_lock)
        {
            hostConn = _deviceConns.FirstOrDefault(d => d.DeviceId == deviceId && d.Role == DeviceRole.Agent)
                    ?? _deviceConns.FirstOrDefault(d => d.DeviceId == deviceId && d.Role == DeviceRole.Client);
            if (hostConn == null) return null;
            s = new Session
            {
                HostToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),
                CallId = call?.CallId,
                Info = new SessionInfo
                {
                    SessionId = Guid.NewGuid().ToString("N"), DeviceId = deviceId,
                    MachineName = _devices[deviceId].MachineName, StartedBy = tc.Name,
                    Unattended = hostConn.Role == DeviceRole.Agent
                }
            };
            _sessions[s.Info.SessionId] = s;
        }
        hostConn.Ws.Send(new StartSessionMsg { SessionId = s.Info.SessionId, HostToken = s.HostToken, TechnicianName = tc.Name });
        // let the client app know a technician is connecting even when the agent hosts
        if (hostConn.Role == DeviceRole.Agent)
            foreach (var c in DeviceConns(deviceId, DeviceRole.Client)) c.Ws.Send(new StartSessionMsg { SessionId = s.Info.SessionId, HostToken = "", TechnicianName = tc.Name });
        tc.Ws.Send(new SessionReadyMsg { Session = s.Info });
        BroadcastTechs(new SessionReadyMsg { Session = s.Info });
        _log.LogInformation("Session {Id} started on {Name} by {Tech} (host={Role})", s.Info.SessionId, s.Info.MachineName, tc.Name, hostConn.Role);
        return s;
    }

    private List<DeviceConn> DeviceConns(string deviceId, DeviceRole role) { lock (_lock) return _deviceConns.Where(d => d.DeviceId == deviceId && d.Role == role).ToList(); }

    public void EndSession(string sessionId, string reason)
    {
        Session? s; CallInfo? call = null;
        lock (_lock)
        {
            if (!_sessions.Remove(sessionId, out s)) return;
            if (s.CallId != null && _calls.TryGetValue(s.CallId, out call)) call.Status = CallStatus.Ended;
        }
        var ended = new SessionEndedMsg { SessionId = sessionId, Reason = reason };
        s.Host?.Send(ended); _ = s.Host?.CloseAsync("ended");
        foreach (var v in s.Viewers.Keys.ToList()) { v.Send(ended); _ = v.CloseAsync("ended"); }
        foreach (var d in DeviceConns(s.Info.DeviceId, DeviceRole.Client)) d.Ws.Send(ended);
        foreach (var d in DeviceConns(s.Info.DeviceId, DeviceRole.Agent)) d.Ws.Send(ended);
        if (call != null) BroadcastTechs(new CallUpdateMsg { Call = call });
        s.Info.HostConnected = false; s.Info.Technicians.Clear();
        lock (_lock) { s.GraceToken++; s.HeldSince = null; s.Info.Held = false; s.Listening = false; }
        BroadcastTechs(new SessionReadyMsg { Session = s.Info });
        _log.LogInformation("Session {Id} ended: {Reason}", sessionId, reason);
    }

    // ------------------------------------------------------------------ session relay
    public async Task RunSessionHostAsync(string sessionId, string hostToken, WsConnection ws, CancellationToken ct)
    {
        Session? s;
        bool bad;
        lock (_lock)
        {
            bad = !_sessions.TryGetValue(sessionId, out s) || s!.HostToken != hostToken;
            if (!bad) { s!.Host = ws; s.Info.HostConnected = true; }
        }
        if (bad) { ws.Send(new ErrorMsg { Message = "Invalid session" }); await ws.CloseAsync(); return; }
        ws.OnMessage += m =>
        {
            if (m is DeclineSessionMsg d) { EndSession(sessionId, string.IsNullOrWhiteSpace(d.Reason) ? "The client declined the session." : d.Reason); return; }
            if (m is MonitorsMsg mm) { lock (_lock) s!.LastMonitors = mm; }
            else if (m is SysInfoMsg si) { lock (_lock) s!.LastSysInfo = si; }
            // Mirrors TenantHub: a technician listening to the machine's sound is disclosed to
            // the person at it, and the only route to their tray app is the device connection.
            if (m is AudioStatusMsg au)
            {
                lock (_lock) s!.Listening = au.Listening && au.Error.Length == 0;
                foreach (var c in DeviceConns(s!.Info.DeviceId, DeviceRole.Client)) c.Ws.Send(m);
            }
            // Mirrors TenantHub again: a file moving on or off the machine is logged where the
            // person at it can read it afterwards. This server has no tickets, so the tray app is
            // the whole of it here.
            if (m is FileActivityMsg)
                foreach (var c in DeviceConns(s!.Info.DeviceId, DeviceRole.Client)) c.Ws.Send(m);
            foreach (var v in ViewersOf(s)) v.Send(m);
        };
        ws.OnBinary += b => { var arr = b.ToArray(); foreach (var v in ViewersOf(s)) BinaryRelay.Send(v.SendBinary, arr); };
        SendParticipants(s);
        BroadcastTechs(new SessionReadyMsg { Session = s.Info });
        await ws.RunAsync(ct);
        bool still; lock (_lock) { still = _sessions.ContainsKey(sessionId) && s.Host == ws; if (still) { s.Host = null; s.Info.HostConnected = false; } }
        if (still) EndSession(sessionId, "Client disconnected");
    }

    public async Task RunSessionViewerAsync(string sessionId, string techName, WsConnection ws, CancellationToken ct)
    {
        Session? s;
        bool missing, wasHeld = false;
        lock (_lock)
        {
            missing = !_sessions.TryGetValue(sessionId, out s);
            if (!missing)
            {
                s!.Viewers[ws] = techName; if (!s.Info.Technicians.Contains(techName)) s.Info.Technicians.Add(techName);
                s.GraceToken++; wasHeld = s.HeldSince != null; s.HeldSince = null; s.Info.Held = false;
            }
        }
        if (missing)
        {
            // Told as a session end, not a bare error: a reconnecting console has to tell
            // "not yet" apart from "never again". See TenantHub.
            ws.Send(new SessionEndedMsg { SessionId = sessionId, Reason = "That session has already ended." });
            await ws.CloseAsync(); return;
        }
        if (wasHeld) { _log.LogInformation("{Tech} came back to session {Id} inside the grace window", techName, sessionId); NotifyDeviceSessionState(s!); }

        MonitorsMsg? mons; SysInfoMsg? sys;
        lock (_lock) { mons = s!.LastMonitors; sys = s.LastSysInfo; }
        if (mons != null) ws.Send(mons);
        if (sys != null) ws.Send(sys);
        ws.OnMessage += m =>
        {
            if (m is ChatMsg c)
            {
                // Chat typed in a session window must reach the customer's tray app, which is on the
                // device connection — not the host process, which has no idea what to do with it.
                c.From = techName; c.FromTech = true; c.DeviceId = s.Info.DeviceId; c.Ts = DateTimeOffset.UtcNow;
                RouteChat(c);
                return;
            }
            // Leave / End, arriving on the session socket. Marking it is what lets the close
            // below tell a deliberate end from a dropped packet. Mirrors TenantHub.
            if (m is EndSessionMsg)
            {
                bool last; lock (_lock) { s.Leaving.Add(ws); last = s.Viewers.Count <= 1; }
                if (last) EndSession(sessionId, $"Ended by {techName}");
                return;
            }
            s.Host?.Send(m);
        };
        ws.OnBinary += b => { var host = s.Host; if (host != null) BinaryRelay.Send(host.SendBinary, b.ToArray()); };
        SendParticipants(s);
        BroadcastTechs(new SessionReadyMsg { Session = s.Info });
        _log.LogInformation("{Tech} joined session {Id}", techName, sessionId);
        await ws.RunAsync(ct);
        bool empty, deliberate, alive;
        lock (_lock)
        {
            s.Viewers.Remove(ws);
            if (!s.Viewers.ContainsValue(techName)) s.Info.Technicians.Remove(techName);
            empty = s.Viewers.Count == 0;
            deliberate = s.Leaving.Remove(ws);
            alive = _sessions.ContainsKey(sessionId);
        }
        if (!alive) return;
        if (empty && deliberate) EndSession(sessionId, "All technicians left");
        else if (empty) BeginGrace(s, sessionId);
        else { SendParticipants(s); BroadcastTechs(new SessionReadyMsg { Session = s.Info }); }
    }

    /// <summary>Mirrors TenantHub.BeginGrace — read the comments there.</summary>
    private void BeginGrace(Session s, string sessionId)
    {
        int token;
        lock (_lock) { token = ++s.GraceToken; s.HeldSince = DateTime.UtcNow; s.Info.Held = true; }
        _log.LogInformation("Session {Id} on {Machine}: last technician's socket dropped — holding it for {Secs}s",
            sessionId, s.Info.MachineName, (int)GracePeriod.TotalSeconds);
        NotifyDeviceSessionState(s);
        BroadcastTechs(new SessionReadyMsg { Session = s.Info });
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(GracePeriod);
                bool end;
                lock (_lock) end = _sessions.ContainsKey(sessionId) && s.GraceToken == token && s.Viewers.Count == 0;
                if (end) EndSession(sessionId, "The technician's connection was lost and did not come back.");
            }
            catch (Exception ex) { _log.LogWarning(ex, "grace timer for session {Id}", sessionId); }
        });
    }

    /// <summary>Mirrors TenantHub.NotifyDeviceSessionState — read the comments there.</summary>
    private void NotifyDeviceSessionState(Session s)
    {
        SessionStateMsg msg;
        lock (_lock)
            msg = new SessionStateMsg
            {
                SessionId = s.Info.SessionId, Active = _sessions.ContainsKey(s.Info.SessionId),
                Held = s.HeldSince != null, TechnicianName = s.Info.Technicians.FirstOrDefault() ?? s.Info.StartedBy,
                Listening = s.Listening
            };
        foreach (var c in DeviceConns(s.Info.DeviceId, DeviceRole.Client)) c.Ws.Send(msg);
    }

    private List<WsConnection> ViewersOf(Session s) { lock (_lock) return s.Viewers.Keys.ToList(); }

    private void SendParticipants(Session s)
    {
        ParticipantsMsg p; List<WsConnection> viewers; WsConnection? host;
        lock (_lock) { p = new ParticipantsMsg { Technicians = s.Info.Technicians.ToList(), HostConnected = s.Host != null }; viewers = s.Viewers.Keys.ToList(); host = s.Host; }
        foreach (var v in viewers) v.Send(p);
        host?.Send(p);
    }

    // ------------------------------------------------------------------ persistence
    private void SaveDeviceKeys() { try { File.WriteAllText(Path.Combine(_dataDir, "devices.json"), JsonSerializer.Serialize(_deviceKeys)); } catch { } }
    private void SaveDeviceInfo() { try { File.WriteAllText(Path.Combine(_dataDir, "device-info.json"), JsonSerializer.Serialize(_devices.Values.ToList(), Json.Options)); } catch { } }

    public object Status()
    {
        lock (_lock) return new
        {
            technicians = _techs.Select(t => t.Name).Distinct().ToList(),
            devicesOnline = _devices.Values.Count(d => d.Online),
            devicesKnown = _devices.Count,
            ringing = _calls.Values.Count(c => c.Status == CallStatus.Ringing),
            sessions = _sessions.Values.Select(s => new { s.Info.SessionId, s.Info.MachineName, s.Info.Technicians, s.Info.HostConnected }).ToList()
        };
    }
}
