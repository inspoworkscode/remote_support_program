using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Hub;

/// <summary>
/// Outside help, step 1 (roadmap 3.9, 2026-10-06): the switches and the customer's code. Somebody
/// outside the company — an accounting firm, or another company on the platform — is let onto one of
/// this company's machines by the PERSON AT IT, with a code they make in the tray app.
///
/// <para>The company's switch (off by default) and each machine's own choice decide whether the tray
/// app offers it at all — <see cref="OutsiderAccess.Allowed"/>, nothing else. Only the CLIENT
/// connection may make or stop a code (§31's rule): not the agent, and nothing from the office.
/// Switching it off stops every code it no longer allows, at once.</para>
///
/// <para>Nothing here lets anybody in yet: spending a code is step 2.</para>
/// </summary>
public sealed partial class TenantHub
{
    /// <summary>The company's switch, as the hub holds it. Guarded by <c>_lock</c>.</summary>
    private bool _outsidersOn;

    /// <summary>Works out what a machine's outside help comes to. Call under <c>_lock</c>.</summary>
    private void ApplyOutsider(DeviceInfo info)
    {
        info.OutsiderCompanyOn = _outsidersOn;
        info.OutsiderAllowed = OutsiderAccess.Allowed(_outsidersOn, info.OutsiderMode);
    }

    /// <summary>
    /// Tells the machine's tray app whether it may make a code, and what code is live. Sent when it
    /// connects and after every change, for the reason TellDeviceItsUnattendedState gives.
    /// </summary>
    private async Task TellDeviceItsOutsiderStateAsync(string deviceId)
    {
        bool allowed; List<DeviceConn> clients;
        lock (_lock)
        {
            if (!_devices.TryGetValue(deviceId, out var info)) return;
            allowed = info.OutsiderAllowed;
            clients = _deviceConns.Where(d => d.DeviceId == deviceId && d.Role == DeviceRole.Client).ToList();
        }
        if (clients.Count == 0) return;
        var msg = new OutsiderStateMsg { Allowed = allowed };
        try
        {
            if (allowed && _registry.Outsiders is { } codes && await codes.LiveAsync(TenantId, deviceId) is { } live)
            { msg.LiveFor = live.ForWhom; msg.LiveUntilUtc = DateTime.SpecifyKind(live.ExpiresUtc, DateTimeKind.Utc); }
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] reading the live outsider code for {Id}", TenantName, deviceId); }
        foreach (var c in clients) c.Ws.Send(msg);
    }

    /// <summary>The person at the machine asking for a code.</summary>
    private async Task MakeOutsiderCodeAsync(DeviceConn dc, string forWhom)
    {
        if (dc.Role != DeviceRole.Client)
        {
            _log.LogWarning("[{Tenant}] a {Role} connection asked for an outsider code on {Id} — refused", TenantName, dc.Role, dc.DeviceId);
            return;
        }
        bool allowed; string who;
        lock (_lock)
        {
            if (!_devices.TryGetValue(dc.DeviceId, out var info)) return;
            allowed = info.OutsiderAllowed;
            who = string.IsNullOrWhiteSpace(info.UserName) ? info.MachineName : $"{info.UserName} at {info.MachineName}";
        }
        if (!allowed)
        {
            dc.Ws.Send(new OutsiderCodeMsg { For = forWhom, Error = "Letting somebody from outside connect is switched off for this computer." });
            await TellDeviceItsOutsiderStateAsync(dc.DeviceId);
            return;
        }
        if (_registry.Outsiders is not { } codes) { dc.Ws.Send(new OutsiderCodeMsg { For = forWhom, Error = "Codes cannot be made on this server." }); return; }

        var (code, expires, error) = await codes.CreateAsync(TenantId, dc.DeviceId, forWhom, who);
        if (error.Length > 0) { dc.Ws.Send(new OutsiderCodeMsg { For = forWhom, Error = error }); return; }
        // Said in the log, never the code: this is the record of a door being opened.
        _log.LogInformation("[{Tenant}] outsider code made on {Who} for {For}, until {Until:HH:mm} UTC", TenantName, who, forWhom.Trim(), expires);
        dc.Ws.Send(new OutsiderCodeMsg { Code = code, For = forWhom.Trim(), ExpiresUtc = DateTime.SpecifyKind(expires, DateTimeKind.Utc) });
        await TellDeviceItsOutsiderStateAsync(dc.DeviceId);
    }

    /// <summary>The person at the machine stopping their code.</summary>
    private async Task CancelOutsiderCodeAsync(DeviceConn dc)
    {
        if (dc.Role != DeviceRole.Client || _registry.Outsiders is not { } codes) return;
        if (await codes.CancelAsync(TenantId, dc.DeviceId, "stopped at the machine"))
            _log.LogInformation("[{Tenant}] outsider code stopped at the machine on {Id}", TenantName, dc.DeviceId);
        await TellDeviceItsOutsiderStateAsync(dc.DeviceId);
    }

    /// <summary>
    /// One machine's choice: as the company, on, or off — from the console's right-click and the
    /// portal's Outside help page, the ONE write path. Returns "" or why not. A machine it no longer
    /// allows loses its live code at once.
    /// </summary>
    public async Task<string> SetDeviceOutsiderModeAsync(string deviceId, int mode, string who)
    {
        mode = OutsiderAccess.Normalise(mode);
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            var d = await db.Devices.FirstOrDefaultAsync(x => x.TenantId == TenantId && x.DeviceId == deviceId);
            if (d == null) return "That machine is not on record.";
            d.OutsiderMode = mode;
            await db.SaveChangesAsync();
        }
        string name = deviceId; bool allowed = false;
        lock (_lock)
            if (_devices.TryGetValue(deviceId, out var info))
            {
                info.OutsiderMode = mode;
                ApplyOutsider(info);
                name = info.MachineName; allowed = info.OutsiderAllowed;
            }
        _log.LogInformation("[{Tenant}] outside help on {Machine} set to {Mode} by {Who}", TenantName, name,
            mode switch { OutsiderAccess.On => "ON", OutsiderAccess.Off => "OFF", _ => "as the company" }, who);
        if (!allowed && _registry.Outsiders is { } codes) await codes.CancelAsync(TenantId, deviceId, $"switched off by {who}");
        await TellDeviceItsOutsiderStateAsync(deviceId);
        BroadcastTechs(new DeviceUpdateMsg { Device = Snapshot(deviceId) });
        EndVisitsNoLongerAllowed(who);
        return "";
    }

    /// <summary>
    /// The company's switch changed (the portal's Outside help page, after it saved). Every machine
    /// that follows it moves with it; every code it no longer allows stops.
    /// </summary>
    public async Task RefreshOutsidersAsync(bool on, string who)
    {
        List<string> all, stopped;
        lock (_lock)
        {
            if (_outsidersOn == on) return;
            _outsidersOn = on;
            foreach (var info in _devices.Values) ApplyOutsider(info);
            all = _devices.Keys.ToList();
            stopped = _devices.Values.Where(i => !i.OutsiderAllowed).Select(i => i.DeviceId).ToList();
        }
        _log.LogInformation("[{Tenant}] outside help switched {State} for the company by {Who}", TenantName, on ? "ON" : "OFF", who);
        if (_registry.Outsiders is { } codes)
            foreach (var id in stopped) await codes.CancelAsync(TenantId, id, $"switched off by {who}");
        foreach (var id in all)
        {
            await TellDeviceItsOutsiderStateAsync(id);
            BroadcastTechs(new DeviceUpdateMsg { Device = Snapshot(id) });
        }
        EndVisitsNoLongerAllowed(who);
    }

    // ================================================================ step 2: another company connects
    //
    // A technician of ANOTHER company on this server types the code into their own console, signed in
    // as themselves (Clint, 2026-10-06: "two signed up tenants may compliment each other in the work
    // they do"). Their own company's hub spends the code and hands the visit to THIS hub — the machine's
    // — which starts the session as it starts any other, except: the person at the machine is ALWAYS
    // asked; the visitor alone may join from outside; what they send and see is an allow-list; this
    // company's consoles are told at once and can watch and end it; and when the visitor goes, so does
    // the session.

    /// <summary>
    /// Somebody from another company in a session here. <see cref="Sink"/> is their console's socket,
    /// on THEIR company's hub: the one way this hub reaches it, for that one session's changes.
    /// </summary>
    public sealed record Visitor(int TenantId, string Company, int TechnicianId, string Name, Action<Msg> Sink)
    {
        /// <summary>How this company sees them: "Jane Smith (Smith &amp; Co IT)". Never their bare name — a
        /// technician here with the same name would otherwise find the session opening on their screen.</summary>
        public string Label => $"{Name} ({Company})";
    }

    /// <summary>sessionId -> the visitor whose console hears that session's changes. Guarded by <c>_lock</c>.</summary>
    private readonly Dictionary<string, Visitor> _visitorSinks = new();
    /// <summary>"sessionId|message type" already logged as refused, so a busy console is one line, not thousands.</summary>
    private readonly HashSet<string> _refusedSaid = new();

    private static bool IsTheVisitor(Session s, TechSession who) =>
        s.Visitor is { } v && v.TenantId == who.TenantId && v.TechnicianId == who.TechnicianId;

    /// <summary>Program.cs asks this before letting somebody from another company open a session socket here.</summary>
    public bool AdmitsVisitor(string sessionId, TechSession who)
    {
        lock (_lock) return _sessions.TryGetValue(sessionId, out var s) && IsTheVisitor(s, who);
    }

    /// <summary>
    /// What a visitor may send the machine: the picture they want, and the mouse and keyboard — Clint's
    /// "see the screen and control it". Leaving is handled before this. No files, scripts, restarts,
    /// clipboard, sound, voice, recording, privacy screen or chat: an ALLOW-list, so anything added to the
    /// protocol later is theirs only when somebody decides it should be.
    /// </summary>
    private static bool VisitorMaySend(Session s, Msg m) =>
        m is SubscribeMsg or QualityMsg or MouseMsg or KeyMsg or PingMsg or E2eHelloMsg
        || (FilesTicked(s) && IsFileMessage(m));

    /// <summary>What a visitor is shown of what the host says: what draws the screen, and nothing that
    /// another viewer — this company's own technician — asked the machine for (a file, the clipboard) —
    /// unless the person ticked files, when the file messages are the visitor's too.</summary>
    private static bool VisitorMaySee(Session s, Msg m) =>
        m is MonitorsMsg or SysInfoMsg or CursorMsg or PrivacyStateMsg or ParticipantsMsg or ErrorMsg or PingMsg or SessionEndedMsg
        // the handshake (2026-10-07): a visitor's console opens sealed content like anybody's
        or E2eReadyMsg or E2eKeyMsg
        || (FilesTicked(s) && (IsFileMessage(m) || m is FileActivityMsg));

    /// <summary>
    /// Files for somebody from outside (Clint, 2026-10-06: "file transfer only if the customer ticks it"):
    /// the person ticked it in the Allow prompt, for THIS session, and nothing else turns it on. The Files
    /// tool's messages — a folder listed, a file or a set offered, pulled, accepted, done — and the chunks
    /// that carry them. Never the clipboard, scripts or anything else: files are files.
    /// </summary>
    private static bool FilesTicked(Session s) => s.Visitor != null && s.Info.VisitorFiles;

    private static bool IsFileMessage(Msg m) => m is FileListRequestMsg or FileListMsg or FileOfferMsg or FileAcceptMsg or FileDoneMsg
        or FilePullMsg or FileSetOfferMsg or FileSetAcceptMsg or FileSetProgressMsg or FileSetDoneMsg or FileSetPullMsg;

    /// <summary>What a visitor may send as binary: file chunks, and only when files were ticked. Never voice.</summary>
    private static bool VisitorMaySendBinary(Session s, ReadOnlySpan<byte> b) =>
        FilesTicked(s) && b.Length > 0 && BinKind.Of(b[0]) == BinKind.FileChunk;

    private bool MaySee(Session s, WsConnection v, Msg m)
    {
        bool visitor; lock (_lock) visitor = s.VisitorSockets.Contains(v);
        return !visitor || VisitorMaySee(s, m);
    }

    /// <summary>Binary from the host, by its first byte (<see cref="BinKind"/>): the picture and the pointer —
    /// file chunks only when files were ticked — never the machine's sound or the customer's voice.</summary>
    private bool MaySeeBinary(Session s, WsConnection v, byte[] b)
    {
        if (b.Length == 0) return false;
        // A sealed MESSAGE (2026-10-07): by its visible type, as a clear one would be — and never a clear type.
        if (b[0] == BinKind.Sealed) return MaySeeSealedMessage(s, v, b);
        bool visitor; lock (_lock) visitor = s.VisitorSockets.Contains(v);
        // By BinKind.Of: a sealed frame keeps its kind with the top bit set.
        var kind = BinKind.Of(b[0]);
        return !visitor || kind is BinKind.Tiles or BinKind.Video or BinKind.Cursor
                        || (kind == BinKind.FileChunk && FilesTicked(s));
    }

    private void RefusedFromVisitor(Session s, string label, Msg m)
    {
        bool first; lock (_lock) first = _refusedSaid.Add(s.Info.SessionId + "|" + m.GetType().Name);
        if (first) _log.LogWarning("[{Tenant}] {Visitor} sent {Kind} in the session on {Machine}; somebody from outside may not, and it was dropped",
            TenantName, label, m.GetType().Name, s.Info.MachineName);
    }

    /// <summary>A session's change, to the visitor's console — as THEY must see it: started by them, by
    /// their own name (their console opens what it started), and marked as somebody else's machine.</summary>
    private void TellVisitor(SessionInfo info)
    {
        Visitor? v;
        lock (_lock)
        {
            _visitorSinks.TryGetValue(info.SessionId, out v);
            if (v != null && info.Ended) _visitorSinks.Remove(info.SessionId);
        }
        if (v == null) return;
        var copy = Json.Deserialize<SessionInfo>(Json.Serialize(info))!;
        copy.StartedBy = v.Name; copy.Visiting = true; copy.Visitor = "";
        try { v.Sink(new SessionReadyMsg { Session = copy }); } catch { /* their console has gone; nothing to tell */ }
    }

    /// <summary>The host that will ASK the person, whatever the company's branding says: an agent that can, or
    /// a tray app that says it always will. Never the unattended path. Call under <c>_lock</c>.</summary>
    private DeviceConn? PickVisitHost(string deviceId) =>
        _deviceConns.FirstOrDefault(d => d.DeviceId == deviceId && d.Role == DeviceRole.Agent && d.CanAskConsent)
        ?? _deviceConns.FirstOrDefault(d => d.DeviceId == deviceId && d.Role == DeviceRole.Client && d.AlwaysAsk);

    /// <summary>
    /// Whether a code for this machine can be used NOW — asked before the code is spent, so a customer
    /// does not need a new code because the first was typed while their laptop was asleep. "" or why not.
    /// </summary>
    public string CanBeVisited(string deviceId)
    {
        lock (_lock)
        {
            if (!_devices.TryGetValue(deviceId, out var info)) return "That code does not work. Ask for a new one.";
            if (!info.OutsiderAllowed) return "That computer no longer lets anybody from outside connect.";
            if (_closedByUser.Contains(deviceId)) return "The support app on that computer has been closed. Ask them to open it again.";
            // Joining a session in progress would skip the person's Allow, so it is never offered.
            if (_sessions.Values.Any(x => x.Info.DeviceId == deviceId)) return "Somebody is already connected to that computer. Try again when they have finished.";
            if (PickVisitHost(deviceId) == null)
                return info.Online ? "The support app on that computer is too old for this. Ask them to update it, then make a new code."
                                   : "That computer is not connected at the moment. Ask them to check it is on and online.";
            return "";
        }
    }

    /// <summary>
    /// A technician HERE typing a code a customer of another company gave them (the visitor's side).
    /// Their own company's console, their own sign-in. A code for one of this company's own machines is
    /// refused unspent — that is what Connect is for.
    /// </summary>
    private async Task ConnectWithCodeAsync(TechConn tc, string code)
    {
        if (_registry.Outsiders is not { } codes) { tc.Ws.Send(new ErrorMsg { Message = "Codes cannot be used on this server." }); return; }
        var visitor = new Visitor(TenantId, TenantName, tc.Who.TechnicianId, tc.Who.DisplayName, m => tc.Ws.Send(m));
        TenantHub? home = null;
        var (hit, error) = await codes.RedeemAsync(code, $"tech:{TenantId}:{tc.Who.TechnicianId}", visitor.Label, found =>
        {
            if (found.TenantId == TenantId)
                return Task.FromResult("That code is for one of your own company's computers. Connect to it from your machine list instead.");
            home = _registry.Get(found.TenantId);
            return Task.FromResult(home == null ? "That code does not work. Ask for a new one." : home.CheckCode(found));
        });
        if (hit == null || home == null) { tc.Ws.Send(new ErrorMsg { Message = error }); return; }
        var why = await home.ArriveWithCodeAsync(visitor, hit);
        if (why.Length > 0) { tc.Ws.Send(new ErrorMsg { Message = why }); return; }
        _log.LogInformation("[{Tenant}] {Tech} connected with a code to a machine of {Home}", TenantName, tc.Who.DisplayName, home.TenantName);
    }

    /// <summary>
    /// The machine's side: a session for somebody from another company, who has just spent a code for
    /// this machine. "" when it is under way (waiting for the person's Allow), or why not.
    /// </summary>
    public async Task<string> StartVisitAsync(Visitor v, OutsiderRedeemed hit)
    {
        await UseCompanyClockAsync();
        string madeBy = "";
        try
        {
            await using var db = await _dbf.CreateDbContextAsync();
            madeBy = await db.OutsiderCodes.Where(c => c.Id == hit.CodeId).Select(c => c.MadeBy).FirstOrDefaultAsync() ?? "";
        }
        catch { }

        Session s; DeviceConn host;
        lock (_lock)
        {
            var why = CanBeVisited(hit.DeviceId);
            if (why.Length > 0) return why;
            host = PickVisitHost(hit.DeviceId)!;
            s = new Session
            {
                HostToken = Passwords.RandomToken(), StartedBy = v.Label, Unattended = false, Visitor = v,
                // Tell the tray app only once they have said yes, as for any attended session the agent hosts.
                TellTrayWhenAllowed = host.Role == DeviceRole.Agent,
                Info = new SessionInfo
                {
                    SessionId = Guid.NewGuid().ToString("N"), DeviceId = hit.DeviceId, MachineName = _devices[hit.DeviceId].MachineName,
                    StartedBy = v.Label, Visitor = v.Label, Unattended = false, HostedByApp = host.Role == DeviceRole.Client
                }
            };
            _sessions[s.Info.SessionId] = s;
            _visitorSinks[s.Info.SessionId] = v;
        }
        _registry.RegisterSession(s.Info.SessionId, TenantId);
        var line = $"{v.Label} connected to {s.Info.MachineName} with a code" + (madeBy.Length > 0 ? $" {madeBy} made" : "") + $" for \"{hit.ForWhom}\".";
        _ = AttachVisitToTicketAsync(s, line);
        // ALWAYS asked — AskConsent, to a host that honours it — and named, company and all.
        host.Ws.Send(new StartSessionMsg { SessionId = s.Info.SessionId, HostToken = s.HostToken, TechnicianName = v.Label, AskConsent = true, VisitorCompany = v.Company });
        BroadcastTechs(new SessionReadyMsg { Session = s.Info });
        BroadcastTechs(new OutsiderVisitMsg
        {
            SessionId = s.Info.SessionId, DeviceId = hit.DeviceId, MachineName = s.Info.MachineName,
            Visitor = v.Label, ForWhom = hit.ForWhom, MadeBy = madeBy
        });
        _ = LogSessionAsync(s, null);
        _log.LogInformation("[{Tenant}] OUTSIDE HELP: {Line} Waiting for the person to allow it (host: {Role}).", TenantName, line, host.Role);
        if (host.Role == DeviceRole.Agent) _ = FallBackToClientAsync(s, v.Label);
        return "";
    }

    /// <summary>The visit on this company's ticket: the machine's open one if a job is under way, or one of its
    /// own — unassigned, because the visitor is not one of this company's people.</summary>
    private async Task AttachVisitToTicketAsync(Session s, string line)
    {
        try
        {
            var t = await _tickets.ActiveForDeviceAsync(TenantId, s.Info.DeviceId)
                ?? await _tickets.CreateAsync(TenantId, $"Outside help: {s.Visitor!.Label}", line, s.Visitor!.Label, TicketSource.Manual,
                    s.Info.DeviceId, s.Info.MachineName, "", TicketPriority.Normal, "", TicketCategory.RemoteSupport, await ClientForDeviceAsync(s.Info.DeviceId));
            lock (_lock) _sessionTickets[s.Info.SessionId] = t.Id;
            await LinkSessionRowAsync(s.Info.SessionId, t.Id);
            await _tickets.AddNoteAsync(TenantId, t.Id, s.Visitor!.Label, line, true, automatic: true);
            var fresh = await _tickets.GetAsync(TenantId, t.Id);
            if (fresh != null) BroadcastTechs(new TicketChangedMsg { Ticket = TicketService.ToSummary(fresh) });
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] outside help on a ticket", TenantName); }
    }

    /// <summary>The visitor's last socket went without them saying End. Back within the grace period, and
    /// nothing happens; not back, and the session they were let in for is over — watchers or not.</summary>
    private void VisitorMayReturn(Session s, string sessionId)
    {
        int token;
        // Removed already (§155): there is nobody to wait for.
        lock (_lock) { if (s.VisitorSockets.Count > 0 || s.Visitor == null) return; token = ++s.VisitorGraceToken; }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(GracePeriod);
                bool end; string label = ""; bool invited = false;
                lock (_lock)
                {
                    end = _sessions.ContainsKey(sessionId) && s.VisitorGraceToken == token && s.VisitorSockets.Count == 0 && s.Visitor != null;
                    if (end) { label = s.Visitor!.Label; invited = s.InvitedBy != null; }
                }
                // Brought in by a technician (§155): the session is theirs, so only the visitor goes.
                if (end && invited) RemoveInvited(s, $"{label}'s connection was lost and did not come back.");
                else if (end) EndSession(sessionId, $"{label}'s connection was lost and did not come back.");
            }
            catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] visitor grace for session {Id}", TenantName, sessionId); }
        });
    }

    /// <summary>Outside help switched off for a machine, or for the company: any visit in progress on a
    /// machine it no longer allows ends now — the switch is also how a company says "stop".</summary>
    private void EndVisitsNoLongerAllowed(string who)
    {
        List<string> ids;
        lock (_lock)
            // The customer's switch governs the customer's codes; somebody a technician brought in is the company's switch (§155).
            ids = _sessions.Values.Where(x => x.Visitor != null && x.InvitedBy == null && !(_devices.TryGetValue(x.Info.DeviceId, out var d) && d.OutsiderAllowed))
                .Select(x => x.Info.SessionId).ToList();
        foreach (var id in ids) EndSession(id, $"Outside help was switched off by {who}.");
    }

    /// <summary>The visiting company's own record: a line on ITS Sessions page, beside its own sessions.</summary>
    private async Task RecordForVisitorAsync(PlatformDb db, Session s, string? endReason)
    {
        var v = s.Visitor!;
        if (v.TenantId == 0) return;   // a guest (step 3) belongs to no company: there is nobody else to keep it
        var row = await db.Sessions.FirstOrDefaultAsync(x => x.TenantId == v.TenantId && x.SessionId == s.Info.SessionId);
        if (row == null)
        {
            row = new SessionLog
            {
                TenantId = v.TenantId, SessionId = s.Info.SessionId, DeviceId = s.Info.DeviceId,
                MachineName = $"{s.Info.MachineName} ({TenantName}'s customer)", StartedBy = v.Name, Technicians = v.Name
            };
            db.Sessions.Add(row);
        }
        if (endReason != null) { row.EndedUtc = DateTime.UtcNow; row.EndReason = endReason; }
    }
}
