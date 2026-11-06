using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Hub;

/// <summary>
/// End-to-end encryption of sessions, the server's half (2026-10-07; docs/NEXT-SESSION-E2E.md, HANDOVER §112).
///
/// <para>The server holds no key and opens nothing. What it still does:</para>
/// <list type="bullet">
/// <item><b>relays sealed frames</b>, applying the visitor and free-version allow-lists to their VISIBLE type
/// (<see cref="SessionSeal.Probe"/> — the same questions asked of a clear message). The receiving end refuses a
/// frame whose type lied, so the allow-lists still hold;</item>
/// <item><b>refuses a clear-list message that arrives sealed</b> — it would slip past everything the hub must see
/// (the consent gate, the audit trail, the microphone rule);</item>
/// <item><b>tells the host whether to seal</b> (<see cref="E2eModeMsg"/>): yes while every viewer said
/// <see cref="E2eHelloMsg"/>; no once one has been in the session for <see cref="E2eGrace"/> without — an old
/// console, which could read nothing sealed (Clint's answer 2: allowed, and every new console shows it).</item>
/// </list>
/// </summary>
public sealed partial class TenantHub
{
    /// <summary>How long a new viewer has to say it can open sealed content before the session goes clear for it.</summary>
    private static readonly TimeSpan E2eGrace = TimeSpan.FromSeconds(5);

    /// <summary>A viewer joined: start its clock, and look again once its grace is up.</summary>
    private void E2eViewerJoined(Session s, WsConnection ws)
    {
        lock (_lock) s.JoinedAt[ws] = DateTime.UtcNow;
        _ = Task.Delay(E2eGrace + TimeSpan.FromMilliseconds(100)).ContinueWith(_ => RecomputeE2e(s));
    }

    private void E2eViewerLeft(Session s, WsConnection ws)
    {
        lock (_lock) { s.JoinedAt.Remove(ws); s.E2eViewers.Remove(ws); }
        RecomputeE2e(s);
    }

    private void E2eHello(Session s, WsConnection ws)
    {
        lock (_lock) s.E2eViewers.Add(ws);
        RecomputeE2e(s);
    }

    /// <summary>Sealed unless a viewer has been here past its grace without saying it can open it. Told to the host
    /// only when it changes (or <paramref name="force"/>: a host that has just connected).</summary>
    private void RecomputeE2e(Session s, bool force = false)
    {
        bool sealedNow; WsConnection? host;
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            sealedNow = !s.Viewers.Keys.Any(v => !s.E2eViewers.Contains(v)
                && s.JoinedAt.TryGetValue(v, out var at) && now - at >= E2eGrace);
            host = s.Host;
            if (!force && s.E2eSealed == sealedNow) return;
            s.E2eSealed = sealedNow;
        }
        if (host == null) return;
        host.Send(new E2eModeMsg { Sealed = sealedNow });
        _log.LogInformation("[{Tenant}] session {Id} on {Machine}: content {Mode}", TenantName, s.Info.SessionId, s.Info.MachineName,
            sealedNow ? "sealed end to end" : "CLEAR — a viewer in it cannot open sealed content (an older console)");
    }

    /// <summary>
    /// A binary frame from a viewer, on its way to the host: file chunks and voice as ever, and now sealed
    /// messages — by their visible type, through the same allow-lists as a clear one.
    /// </summary>
    private bool ViewerBinaryMayPass(Session s, bool visiting, ReadOnlySpan<byte> b)
    {
        if (b.Length == 0) return false;
        // A bridge frame comes from a HOST, to the other host of its copy (TenantHub.Bridge.cs) — never from a viewer.
        if (BinKind.Of(b[0]) == BinKind.Bridge) return false;
        if (b[0] == BinKind.Sealed)
        {
            var probe = SessionSeal.TagOfFrame(b) is { } tag ? SessionSeal.Probe(tag) : null;
            // Not a message, or one the hub must read: it has no business arriving sealed.
            if (probe == null || SessionSeal.StaysClear(probe)) return false;
            if (visiting && !VisitorMaySend(s, probe)) return false;
            if (Free && !FreePlan.MaySend(probe)) return false;
            return true;
        }
        if (visiting && !VisitorMaySendBinary(s, b)) return false;
        if (Free && !FreePlan.MaySendBinary(b)) return false;
        return true;
    }

    /// <summary>A sealed message from the host, to one viewer: by its visible type (a visitor sees only what
    /// <see cref="VisitorMaySee"/> allows), and never a clear-list type in disguise.</summary>
    private bool MaySeeSealedMessage(Session s, WsConnection v, ReadOnlySpan<byte> b)
    {
        var probe = SessionSeal.TagOfFrame(b) is { } tag ? SessionSeal.Probe(tag) : null;
        if (probe == null || SessionSeal.StaysClear(probe)) return false;
        return MaySee(s, v, probe);
    }
}
