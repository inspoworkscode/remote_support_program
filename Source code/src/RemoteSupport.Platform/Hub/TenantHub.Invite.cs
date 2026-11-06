using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Hub;

/// <summary>
/// Outside help asked for by a TECHNICIAN (2026-10-22, HANDOVER §155). Clint: "sometimes it happens that a tech
/// needs the outside assistance rather than the client." In a session, Tools → Get outside help… makes a code for
/// THAT session; whoever types it — another company's technician in their console, or a guest in the guest console
/// or a browser — waits for the technician who made it to let them in. From there it is §89's visit: the same
/// allow-lists (<see cref="VisitorMaySend"/>, <see cref="VisitorMaySee"/>), files only if ticked, the company's
/// consoles told with the red pop-up, the ticket written.
///
/// <para>What is different, each by Clint's answer or the recommendation he took:</para>
/// <list type="bullet">
/// <item>The <b>technician</b> lets them in, not the person at the machine, who is <b>told</b> — never asked.</item>
/// <item>The visitor leaving does NOT end the session; it is the company's. And when <b>nobody of the company</b>
/// is left in it, the visitor goes at once — nobody from outside stays on a customer's machine alone.</item>
/// <item>Any technician in the session may; the machine's outside-help switch is the CUSTOMER's and is not asked.
/// The company's own switch (<c>Tenant.TechnicianInvitesAllowed</c>, on by default) is a supervisor's or the owner's.</item>
/// <item>One person from outside in a session at a time — the session's single <see cref="Session.Visitor"/>.</item>
/// </list>
/// </summary>
public sealed partial class TenantHub
{
    /// <summary>The company's switch for technicians bringing somebody in. Guarded by <c>_lock</c>.</summary>
    private bool _invitesOn = true;

    /// <summary>How long the technician's question waits before the visitor is turned away by itself: two minutes.
    /// <c>Testing:OutsiderJoinAskSeconds</c> shortens it for a harness.</summary>
    private int JoinAskSeconds => int.TryParse(_cfg["Testing:OutsiderJoinAskSeconds"], out var s) && s > 0 ? s : 120;

    // ---------------------------------------------------------------- the technician's side

    /// <summary>Why this technician may not bring somebody into this session now, or "". Call under <c>_lock</c>.</summary>
    private string InviteProblem(string sessionId, int technicianId, out Session? s)
    {
        if (!_sessions.TryGetValue(sessionId, out s)) return "That session has ended.";
        if (!_invitesOn) return "Bringing in somebody from outside is switched off for your company. A supervisor or the owner can switch it on in the portal, under Outside help.";
        if (!s.ViewerIds.ContainsValue(technicianId)) return "Only a technician in this session can bring somebody in.";
        if (s.Info.ViewOnly) return "Not in a browser session: the customer is only sharing their screen, so there is nothing to control.";
        if (s.Info.AwaitingConsent || !s.Info.HostConnected) return "Wait until the session is under way.";
        if (s.Visitor != null)
            return s.InvitedBy == null
                ? $"{s.Visitor.Label} is already in this session from outside, with the customer's own code."
                : $"{s.Visitor.Label} is already in this session. Remove them first to bring somebody else in.";
        if (s.Pending != null) return $"{s.Pending.Label} is waiting to be let in already.";
        return "";
    }

    /// <summary>Tools → Get outside help…: a code for this session, or why not.</summary>
    private async Task MakeInviteAsync(TechConn tc, OutsiderInviteRequestMsg m)
    {
        var reply = new OutsiderInviteMsg { SessionId = m.SessionId, For = (m.For ?? "").Trim() };
        if (_registry.Outsiders is not { } codes) { reply.Error = "Codes cannot be made on this server."; tc.Ws.Send(reply); return; }
        Session? s; string why;
        lock (_lock) why = InviteProblem(m.SessionId, tc.Who.TechnicianId, out s);
        if (why.Length > 0) { reply.Error = why; tc.Ws.Send(reply); return; }

        var (code, expires, error) = await codes.CreateForSessionAsync(TenantId, s!.Info.DeviceId, s.Info.SessionId, tc.Who.TechnicianId, m.For, tc.Who.DisplayName);
        if (error.Length > 0) { reply.Error = error; tc.Ws.Send(reply); return; }
        lock (_lock) s.InviteMakerId = tc.Who.TechnicianId;
        // Said in the log and on the ticket, never the code.
        _log.LogInformation("[{Tenant}] {Tech} made a code to bring \"{For}\" into the session on {Machine}, until {Until:HH:mm} UTC",
            TenantName, tc.Who.DisplayName, reply.For, s.Info.MachineName, expires);
        _ = LogToSessionTicketAsync(s.Info.SessionId, tc.Who.DisplayName, $"{tc.Who.DisplayName} made a code to bring \"{reply.For}\" in from outside.");
        reply.Code = code; reply.ExpiresUtc = DateTime.SpecifyKind(expires, DateTimeKind.Utc);
        reply.State = "Waiting for them to type it.";
        tc.Ws.Send(reply);
    }

    /// <summary>Stop this session's live code. The technician who made it, or any technician in the session.</summary>
    private async Task CancelInviteAsync(TechConn tc, OutsiderInviteCancelMsg m)
    {
        if (_registry.Outsiders is not { } codes) return;
        bool inIt;
        lock (_lock) inIt = _sessions.TryGetValue(m.SessionId, out var s) && s.ViewerIds.ContainsValue(tc.Who.TechnicianId);
        if (!inIt) return;
        if (await codes.CancelForSessionAsync(TenantId, m.SessionId, $"stopped by {tc.Who.DisplayName}"))
            _log.LogInformation("[{Tenant}] {Tech} stopped the code for session {Id}", TenantName, tc.Who.DisplayName, m.SessionId);
        lock (_lock) if (_sessions.TryGetValue(m.SessionId, out var s)) s.InviteMakerId = 0;
        tc.Ws.Send(new OutsiderInviteMsg { SessionId = m.SessionId, State = "Stopped. That code no longer works." });
    }

    /// <summary>The technician's answer to "let them in?". Only the one who made the code.</summary>
    private void AnswerJoin(TechConn tc, OutsiderJoinAnswerMsg a)
    {
        Session? s; Visitor? v; OutsiderRedeemed? hit;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(a.SessionId, out s) || s.Pending == null) return;   // answered, timed out, or over
            if (s.PendingHit!.InvitedById != tc.Who.TechnicianId)
            {
                _log.LogWarning("[{Tenant}] {Tech} answered for a code they did not make (session {Id}) — refused", TenantName, tc.Who.DisplayName, a.SessionId);
                return;
            }
            v = s.Pending; hit = s.PendingHit;
            s.Pending = null; s.PendingHit = null; s.PendingToken++;
            if (a.Allow)
            {
                s.Visitor = v; s.InvitedBy = tc.Who.DisplayName; s.InvitedById = tc.Who.TechnicianId;
                s.Info.Visitor = v.Label; s.Info.InvitedBy = tc.Who.DisplayName; s.Info.VisitorFiles = a.Files;
                s.VisitorGraceToken++;
                _visitorSinks[s.Info.SessionId] = v;
            }
        }
        if (!a.Allow)
        {
            TurnAwayWaiting(s, v, $"{tc.Who.DisplayName} did not let you in.");
            TellInviter(s.Info.SessionId, tc.Who.TechnicianId, new OutsiderInviteMsg { SessionId = s.Info.SessionId, State = $"You did not let {v.Label} in. Make a new code to try again." });
            _ = LogToSessionTicketAsync(s.Info.SessionId, tc.Who.DisplayName, $"{tc.Who.DisplayName} did not let {v.Label} in.");
            return;
        }
        _log.LogInformation("[{Tenant}] OUTSIDE HELP: {Tech} let {Visitor} into the session on {Machine}{Files}",
            TenantName, tc.Who.DisplayName, v.Label, s.Info.MachineName, a.Files ? ", with files" : "");
        _ = LogToSessionTicketAsync(s.Info.SessionId, tc.Who.DisplayName,
            $"{tc.Who.DisplayName} let {v.Label} into the session on {s.Info.MachineName}"
            + (a.Files ? " — and ticked that they may copy files." : ", without files.")
            + " The person at the machine was told.");
        // TOLD, not asked (Clint, 2026-10-22).
        var told = new OutsiderJoinedMsg { Visitor = v.Label, By = tc.Who.DisplayName, Files = a.Files };
        foreach (var c in DeviceConns(s.Info.DeviceId, DeviceRole.Client)) c.Ws.Send(told);
        // Their console opens the session now (TellVisitor, from the broadcast); this company's see the red pop-up.
        BroadcastTechs(new SessionReadyMsg { Session = s.Info });
        BroadcastTechs(new OutsiderVisitMsg
        {
            SessionId = s.Info.SessionId, DeviceId = s.Info.DeviceId, MachineName = s.Info.MachineName,
            Visitor = v.Label, ForWhom = hit!.ForWhom, MadeBy = tc.Who.DisplayName, InvitedBy = tc.Who.DisplayName
        });
        TellInviter(s.Info.SessionId, tc.Who.TechnicianId, new OutsiderInviteMsg
        {
            SessionId = s.Info.SessionId, Connected = v.Label,
            State = $"{v.Label} is connected" + (a.Files ? ", with files." : ", without files.")
        });
    }

    /// <summary>Remove — the session window's or the red pop-up's. Any of this company's technicians.</summary>
    private void RemoveAsked(TechConn tc, OutsiderRemoveMsg m)
    {
        string? refused = null;
        lock (_lock)
            if (_sessions.TryGetValue(m.SessionId, out var s) && s.Visitor != null && s.InvitedBy == null)
                refused = $"{s.Visitor.Label} came in with the customer's own code: End it ends that session.";
        if (refused != null) { tc.Ws.Send(new ErrorMsg { Message = refused }); return; }
        Session? found; lock (_lock) _sessions.TryGetValue(m.SessionId, out found);
        if (found != null) RemoveInvited(found, $"Removed by {tc.Who.DisplayName}.");
    }

    /// <summary>The company's switch changed (the portal's Outside help page). Off stops every technician's code, turns
    /// away anybody waiting, and removes anybody let in that way.</summary>
    public async Task RefreshInvitesAsync(bool on, string who)
    {
        List<Session> invited;
        lock (_lock)
        {
            if (_invitesOn == on) return;
            _invitesOn = on;
            invited = on ? new() : _sessions.Values.Where(s => s.InvitedBy != null || s.Pending != null).ToList();
        }
        _log.LogInformation("[{Tenant}] technicians bringing in outside help switched {State} by {Who}", TenantName, on ? "ON" : "OFF", who);
        if (on) return;
        if (_registry.Outsiders is { } codes) await codes.CancelAllSessionCodesAsync(TenantId, $"switched off by {who}");
        foreach (var s in invited)
        {
            Visitor? waiting;
            lock (_lock) { waiting = s.Pending; s.Pending = null; s.PendingHit = null; s.PendingToken++; s.InviteMakerId = 0; }
            if (waiting != null) TurnAwayWaiting(s, waiting, $"Outside help was switched off by {who}.");
            RemoveInvited(s, $"Outside help was switched off by {who}.");
        }
    }

    // ---------------------------------------------------------------- the visitor's side

    /// <summary>
    /// Whether a code can be used NOW, asked before it is spent — the customer's code (§89) or a technician's
    /// (this file). "" or why not; a refused code keeps working.
    /// </summary>
    public string CheckCode(OutsiderRedeemed found) => found.ForSession ? CanJoinInvite(found) : CanBeVisited(found.DeviceId);

    /// <summary>A spent code: a visit the person at the machine allows (§89), or a session a technician lets them into.</summary>
    public Task<string> ArriveWithCodeAsync(Visitor v, OutsiderRedeemed hit) => hit.ForSession ? JoinInvitedAsync(v, hit) : StartVisitAsync(v, hit);

    private string CanJoinInvite(OutsiderRedeemed hit)
    {
        lock (_lock)
        {
            if (!_invitesOn) return "That code no longer works: bringing in somebody from outside has been switched off. Ask for a new one.";
            if (!_sessions.TryGetValue(hit.SessionId, out var s)) return "The session that code was made for has ended. Ask for a new one.";
            if (s.Visitor != null || s.Pending != null) return "Somebody else is being let into that session already. Try again in a moment.";
            if (!s.ViewerIds.ContainsValue(hit.InvitedById)) return "The technician who made that code has left the session. Ask them for a new one.";
            // They are the one asked; a session window without its console could never answer.
            if (!_techs.Any(t => t.Who.TechnicianId == hit.InvitedById)) return "The technician who made that code cannot answer at the moment. Try again in a minute.";
            return "";
        }
    }

    /// <summary>
    /// Somebody has spent a technician's code: they wait, and the technician who made it is asked. "" when they are
    /// waiting, or why not (the session ended between the check and now).
    /// </summary>
    private async Task<string> JoinInvitedAsync(Visitor v, OutsiderRedeemed hit)
    {
        await UseCompanyClockAsync();
        Session s; List<TechConn> inviters; int token; string inviter;
        lock (_lock)
        {
            var why = CanJoinInvite(hit);
            if (why.Length > 0) return why;
            s = _sessions[hit.SessionId];
            s.Pending = v; s.PendingHit = hit; token = ++s.PendingToken; s.InviteMakerId = 0;   // spent
            inviters = _techs.Where(t => t.Who.TechnicianId == hit.InvitedById).ToList();
            inviter = inviters.First().Who.DisplayName;
        }
        var ask = new OutsiderJoinAskMsg { SessionId = s.Info.SessionId, MachineName = s.Info.MachineName, Visitor = v.Label, ForWhom = hit.ForWhom, Seconds = JoinAskSeconds };
        foreach (var t in inviters)
        {
            t.Ws.Send(ask);
            t.Ws.Send(new OutsiderInviteMsg { SessionId = s.Info.SessionId, State = $"{v.Label} typed the code and is waiting for you to let them in." });
        }
        TellVisitorWaiting(s, v, inviter);
        _ = LogToSessionTicketAsync(s.Info.SessionId, inviter, $"{v.Label} typed the code {inviter} made for \"{hit.ForWhom}\" and is waiting to be let in.");
        _log.LogInformation("[{Tenant}] OUTSIDE HELP: {Visitor} typed {Tech}'s code for the session on {Machine}; asking {Tech}", TenantName, v.Label, inviter, s.Info.MachineName, inviter);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(JoinAskSeconds));
                Visitor? still;
                lock (_lock)
                {
                    still = s.PendingToken == token ? s.Pending : null;
                    if (still != null) { s.Pending = null; s.PendingHit = null; s.PendingToken++; }
                }
                if (still == null) return;
                TurnAwayWaiting(s, still, $"{inviter} did not answer.");
                TellInviter(s.Info.SessionId, hit.InvitedById, new OutsiderInviteMsg { SessionId = s.Info.SessionId, State = $"{still.Label} was not let in: you did not answer in time." });
                _ = LogToSessionTicketAsync(s.Info.SessionId, inviter, $"{still.Label} was not let in: {inviter} did not answer.");
            }
            catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] the outside-help wait for session {Id}", TenantName, s.Info.SessionId); }
        });
        return "";
    }

    /// <summary>The waiting visitor's console, guest console or browser page: "waiting for {technician} to let you in".</summary>
    private void TellVisitorWaiting(Session s, Visitor v, string inviter)
    {
        var copy = VisitorCopy(s, v);
        copy.HostConnected = false; copy.AwaitingConsent = true; copy.ConsentSeconds = JoinAskSeconds;
        copy.AwaitingTechnician = inviter; copy.InvitedBy = inviter; copy.Technicians = new();
        try { v.Sink(new SessionReadyMsg { Session = copy }); } catch { }
    }

    /// <summary>Turned away before they were let in — no, no answer, the session ended, switched off.</summary>
    private void TurnAwayWaiting(Session s, Visitor v, string reason)
    {
        var copy = VisitorCopy(s, v);
        copy.HostConnected = false; copy.AwaitingConsent = false; copy.Ended = true; copy.EndReason = reason; copy.Technicians = new();
        try { v.Sink(new SessionReadyMsg { Session = copy }); } catch { }
    }

    /// <summary>The session as a visitor must see it: started by them, by their own name, and somebody else's machine.</summary>
    private static SessionInfo VisitorCopy(Session s, Visitor v)
    {
        var copy = Json.Deserialize<SessionInfo>(Json.Serialize(s.Info))!;
        copy.StartedBy = v.Name; copy.Visiting = true; copy.Visitor = "";
        return copy;
    }

    /// <summary>
    /// Takes the visitor a technician brought in out of the session; the session goes on. Their sockets stay marked
    /// as a visitor's until they close — so the allow-lists still hold in the moment between — but stop being sent
    /// anything at once. The person at the machine is told, and the ticket.
    /// </summary>
    private void RemoveInvited(Session s, string reason)
    {
        Visitor? v; List<WsConnection> socks; string by;
        lock (_lock)
        {
            if (s.Visitor == null || s.InvitedBy == null) return;
            v = s.Visitor; by = s.InvitedBy;
            socks = s.VisitorSockets.ToList();
            foreach (var w in socks) s.Viewers.Remove(w);
            s.Info.Technicians.Remove(v.Label);
            s.Visitor = null; s.InvitedBy = null; s.InvitedById = 0; s.VisitorGraceToken++;
            s.Info.Visitor = ""; s.Info.InvitedBy = ""; s.Info.VisitorFiles = false;
            _visitorSinks.Remove(s.Info.SessionId);
        }
        _log.LogInformation("[{Tenant}] OUTSIDE HELP: {Visitor} left the session on {Machine}: {Reason}", TenantName, v.Label, s.Info.MachineName, reason);
        TurnAwayWaiting(s, v, reason);
        var ended = new SessionEndedMsg { SessionId = s.Info.SessionId, Reason = reason };
        foreach (var w in socks) { w.Send(ended); _ = w.CloseAsync("removed"); }
        var told = new OutsiderJoinedMsg { Visitor = v.Label, By = by, Left = true };
        foreach (var c in DeviceConns(s.Info.DeviceId, DeviceRole.Client)) c.Ws.Send(told);
        _ = LogToSessionTicketAsync(s.Info.SessionId, by, $"{v.Label} left the session on {s.Info.MachineName}. {reason}");
        _ = RecordVisitorLeftAsync(v, s.Info.SessionId, reason);
        TellInviters(s.Info.SessionId, new OutsiderInviteMsg { SessionId = s.Info.SessionId, State = $"{v.Label} has left. {reason}" });
        SendParticipants(s);
        BroadcastTechs(new SessionReadyMsg { Session = s.Info });
    }

    /// <summary>Their own company's Sessions line (§89) ends when they leave, not when the session does.</summary>
    private async Task RecordVisitorLeftAsync(Visitor v, string sessionId, string reason)
    {
        if (v.TenantId == 0) return;   // a guest: no company keeps it
        try
        {
            await using var db = await _dbf.CreateDbContextAsync();
            var row = await db.Sessions.FirstOrDefaultAsync(x => x.TenantId == v.TenantId && x.SessionId == sessionId);
            if (row == null) return;
            row.EndedUtc = DateTime.UtcNow; row.EndReason = reason;
            await db.SaveChangesAsync();
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] the visitor's own record", TenantName); }
    }

    // ---------------------------------------------------------------- hooks from the session's life

    /// <summary>The visitor pressed Leave. Brought in by a technician: they go and the session stays. Else §89: it ends.
    /// A visitor already removed is nobody here — their late Leave must not end a session that went on without them.</summary>
    private void VisitorLeaves(Session s, TechSession who, string label)
    {
        bool still, invited;
        lock (_lock) { still = IsTheVisitor(s, who); invited = s.InvitedBy != null; }
        if (!still) return;
        if (invited) RemoveInvited(s, $"{label} left.");
        else EndSession(s.Info.SessionId, $"Ended by {label}");
    }

    /// <summary>This socket's visitor is no longer the session's — removed, or somebody else brought in since. What
    /// such a socket still sends, in the moment before it closes, goes nowhere.</summary>
    private bool NoLongerTheVisitor(Session s, TechSession who) { lock (_lock) return !IsTheVisitor(s, who); }

    /// <summary>
    /// One of the company's technicians closed their window on this session (or lost it). Their code stops if they
    /// are not in the session any more, and so does anybody waiting on their answer; and when nobody of the company
    /// is left, the visitor they brought in goes too.
    /// </summary>
    private void InviteViewerLeft(Session s, int technicianId)
    {
        bool codeTheirs = false, noneOfOurs; Visitor? waiting = null;
        lock (_lock)
        {
            if (!s.ViewerIds.ContainsValue(technicianId))
            {
                if (s.InviteMakerId == technicianId) { codeTheirs = true; s.InviteMakerId = 0; }
                if (s.Pending != null && s.PendingHit!.InvitedById == technicianId)
                { waiting = s.Pending; s.Pending = null; s.PendingHit = null; s.PendingToken++; }
            }
            noneOfOurs = s.Visitor != null && s.InvitedBy != null && !s.Viewers.Keys.Any(k => !s.VisitorSockets.Contains(k));
        }
        if (codeTheirs && _registry.Outsiders is { } codes) _ = codes.CancelForSessionAsync(TenantId, s.Info.SessionId, "the technician who made it left the session");
        if (waiting != null) TurnAwayWaiting(s, waiting, "The technician who made the code left the session.");
        if (noneOfOurs) RemoveInvited(s, $"Nobody from {TenantName} is left in the session.");
    }

    /// <summary>The session is over: a live code for it stops, and anybody waiting is told.</summary>
    private void InviteSessionEnded(Session s, string reason)
    {
        Visitor? waiting; bool hadCode;
        lock (_lock)
        {
            waiting = s.Pending; s.Pending = null; s.PendingHit = null; s.PendingToken++;
            hadCode = s.InviteMakerId != 0; s.InviteMakerId = 0;
        }
        if (waiting != null) TurnAwayWaiting(s, waiting, $"The session ended: {reason}");
        if (hadCode && _registry.Outsiders is { } codes) _ = codes.CancelForSessionAsync(TenantId, s.Info.SessionId, "the session ended");
    }

    /// <summary>To the consoles of one technician: their code's progress.</summary>
    private void TellInviter(string sessionId, int technicianId, OutsiderInviteMsg m)
    {
        List<TechConn> to; lock (_lock) to = _techs.Where(t => t.Who.TechnicianId == technicianId).ToList();
        foreach (var t in to) t.Ws.Send(m);
    }

    /// <summary>To every technician of this company in the session — whoever has its Get outside help window open.</summary>
    private void TellInviters(string sessionId, OutsiderInviteMsg m)
    {
        List<TechConn> to;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var s)) return;
            var ids = s.ViewerIds.Values.ToHashSet();
            to = _techs.Where(t => ids.Contains(t.Who.TechnicianId)).ToList();
        }
        foreach (var t in to) t.Ws.Send(m);
    }
}
