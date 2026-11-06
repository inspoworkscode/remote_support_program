using System.Net.WebSockets;
using RemoteSupport.Shared;

namespace RemoteSupport.Technician;

/// <summary>
/// Viewer side of a session socket: relays in-session messages and binary frames, and reconnects
/// when the socket drops.
///
/// It did not used to reconnect at all — it connected once, and when the receive loop ended the
/// session window was dead while the pill on screen said "reconnecting". Two things had to be
/// true before a retry loop here could work, and only one of them is in this file:
///
/// - The SERVER has to keep the session alive. It used to destroy it the instant the last
///   viewer's socket closed, so a retry would have dialled back into a session that no longer
///   existed. See TenantHub.BeginGrace.
/// - A retry has to be able to tell "not yet" from "never again". A session that has genuinely
///   ended answers with a SessionEndedMsg, and we stop dialling rather than hammering the
///   server for two minutes about something that is over.
///
/// The trap that governs the shape of this class: every OnMessage handler must be attached
/// before the pump starts. The server replays the cached MonitorsMsg within milliseconds of the
/// socket opening, and MonitorsMsg is the only thing that triggers the console's first
/// SubscribeMsg — so a handler attached late leaves a reconnected viewer on a permanently black
/// screen, which is indistinguishable from a reconnect that failed. Subscribers attach to THIS
/// object once, in the SessionForm constructor, and the per-attempt WsConnection forwards to
/// them; nothing re-subscribes per attempt.
/// </summary>
public sealed class SessionLink
{
    private readonly string _wsBase;
    private readonly string _sessionId;
    private readonly string _token;
    private WsConnection? _ws;
    private readonly object _lock = new();

    /// <summary>
    /// How long to keep trying before giving up. Deliberately longer than the server's 90-second
    /// grace period: the server is the thing that decides when the session is over, and a client
    /// that stopped dialling first would turn a recoverable drop into a lost session.
    /// </summary>
    private static readonly TimeSpan RetryWindow = TimeSpan.FromSeconds(120);

    /// <summary>Backoff between attempts, in seconds, then the last value repeats. The first
    /// retry is quick because most drops are a blink.</summary>
    private static readonly int[] Backoff = { 1, 2, 3, 5, 5, 8 };

    public event Action<Msg>? OnMessage;
    public event Action<ReadOnlyMemory<byte>>? OnBinary;
    public event Action<bool>? OnConnectedChanged;

    /// <summary>
    /// Raised once, when reconnection has been abandoned — either the server said the session is
    /// over or the retry window ran out. <c>ended</c> is true when the server said so, which is
    /// the difference between "that session is gone" and "we could not reach the server".
    /// </summary>
    public event Action<bool, string>? OnGaveUp;

    /// <summary>Raised after a reconnect succeeds, so the window can put back the state the
    /// technician had set — the server replays the monitor list but knows nothing about which
    /// monitor was selected, the quality, or whether Listen was on.</summary>
    public event Action? OnReconnected;

    public bool Connected { get { lock (_lock) return _ws?.IsOpen ?? false; } }

    public SessionLink(string wsBase, string sessionId, string token)
    { _wsBase = wsBase; _sessionId = sessionId; _token = token; }

    public async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;
        bool everConnected = false;
        DateTime? droppedAt = null;

        while (!ct.IsCancellationRequested)
        {
            bool connectedThisAttempt = false;
            try
            {
                SecureTransport.Require(_wsBase);
                var cws = new ClientWebSocket();
                cws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                Trace.Write($"link: connecting viewer socket to {_wsBase}/ws/session id={_sessionId}" + (attempt > 0 ? $" (retry {attempt})" : ""));
                await cws.ConnectAsync(new Uri($"{_wsBase}/ws/session?role=viewer&id={_sessionId}&token={_token}"), ct);
                var ws = new WsConnection(cws);
                lock (_lock) _ws = ws;

                // End-to-end encryption (2026-10-07): a fresh key per attempt — a reconnect is a new viewer to the host,
                // with its own sender number — and a seal that opens nothing until the host's answer is checked.
                _keyed = false; _held = false;   // this attempt proves itself again; what arrived clear stays said
                var eph = E2eHandshake.NewEphemeral();
                var seal = new SessionSeal(_sessionId);
                seal.ClearContentArrived += () => { _clearSeen = true; Trace.Write("e2e: content arrived CLEAR — this session is not end-to-end encrypted"); RaiseE2e(); };
                ws.Seal = seal;
                var hello = new E2eHelloMsg { Pub = E2eHandshake.PubOf(eph) };

                // Attached before RunAsync, every time. The replayed MonitorsMsg arrives within
                // milliseconds and is gone for good if nothing is listening.
                ws.OnMessage += m =>
                {
                    // The handshake is this class's own business; the window only sees what it means (E2eStatus).
                    if (m is E2eReadyMsg) { ws.Send(hello); return; }
                    if (m is E2eKeyMsg key) { OnKey(ws, seal, eph, key); return; }
                    // The server telling us the session is over. Stop dialling: it will never
                    // come back, and a retry loop that ignores this spends two minutes proving
                    // it while the window says "reconnecting".
                    if (m is SessionEndedMsg) _sessionOver = true;
                    Notify(m);
                };
                ws.OnBinary += b => { try { OnBinary?.Invoke(b); } catch { } };

                // Said at once as well as on E2eReadyMsg: a host already running sent its ready before this socket.
                ws.Send(hello);
                connectedThisAttempt = true;
                attempt = 0;
                Trace.Write("link: viewer socket CONNECTED" + (everConnected ? $" — back after {(DateTime.UtcNow - (droppedAt ?? DateTime.UtcNow)).TotalSeconds:0.0}s down" : ""));
                Raise(OnConnectedChanged, true);
                if (everConnected) { try { OnReconnected?.Invoke(); } catch (Exception ex) { Trace.Write("link: OnReconnected subscriber threw — " + ex.Message); } }
                everConnected = true;

                await ws.RunAsync(ct);
                Trace.Write("link: viewer socket receive loop ended");
            }
            catch (Exception ex) { Trace.Write($"link: viewer socket {(connectedThisAttempt ? "LOST" : "FAILED")} — {ex.GetType().Name}: {ex.Message}"); }

            lock (_lock) _ws = null;
            if (connectedThisAttempt) { droppedAt = DateTime.UtcNow; Raise(OnConnectedChanged, false); }
            if (ct.IsCancellationRequested) return;

            if (_sessionOver)
            {
                Trace.Write("link: the server says this session has ended — not retrying");
                GiveUp(true, "That session has ended.");
                return;
            }

            droppedAt ??= DateTime.UtcNow;
            var down = DateTime.UtcNow - droppedAt.Value;
            if (down > RetryWindow)
            {
                Trace.Write($"link: giving up after {down.TotalSeconds:0}s of retrying");
                GiveUp(false, $"Could not get back to the session after {down.TotalSeconds:0} seconds.");
                return;
            }

            var wait = Backoff[Math.Min(attempt, Backoff.Length - 1)];
            attempt++;
            Trace.Write($"link: retrying in {wait}s (down {down.TotalSeconds:0}s of {RetryWindow.TotalSeconds:0}s)");
            try { await Task.Delay(TimeSpan.FromSeconds(wait), ct); } catch { return; }
        }
    }

    private bool _sessionOver;
    private bool _gaveUp;

    // ---------------------------------------------------------------- end-to-end encryption (2026-10-07)

    /// <summary>Which machine this is, as the technician knows it — what its identity is pinned under
    /// (<see cref="MachinePins"/>). Set by the window before the link runs.</summary>
    public string DeviceId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string Owner { get; set; } = "";
    public MachinePins Pins { get; set; } = new();

    public enum E2eStatus
    {
        /// <summary>Nothing known yet.</summary>
        Waiting,
        /// <summary>Content sealed under a key signed by this machine's pinned identity.</summary>
        Encrypted,
        /// <summary>Content sealed, but nothing proved who sealed it: a host running as the person, or a browser.</summary>
        EncryptedUnchecked,
        /// <summary>Content arrived clear — an old host, an old console in the session, or a server forcing it.</summary>
        NotEncrypted,
        /// <summary>The machine's identity is not the one on file: nothing is opened until the technician answers.</summary>
        Held,
    }

    /// <summary>What the badge says, from what actually arrived — never from what anybody claimed.</summary>
    public E2eStatus Status
    {
        get
        {
            if (_clearSeen) return E2eStatus.NotEncrypted;
            if (_held) return E2eStatus.Held;
            if (_keyed) return _signed ? E2eStatus.Encrypted : E2eStatus.EncryptedUnchecked;
            return E2eStatus.Waiting;
        }
    }

    public event Action<E2eStatus>? OnE2eChanged;

    /// <summary>This window's sends are sealed: the key is in use on the current socket.</summary>
    public bool Keyed => _keyed;

    /// <summary>The machine's identity is not the one on file. The window asks the technician; the answer is the
    /// callback: true = it was reinstalled, use it (and remember it); false = do not (the window ends the session).</summary>
    public event Action<MachinePins.Verdict, MachinePins.Pin?, string, Action<bool>>? OnIdentityQuestion;

    /// <summary>Raised once the key is in use, so the window can ask for a complete picture it can now open.</summary>
    public event Action? OnKeyed;

    private volatile bool _clearSeen, _held, _keyed, _signed;

    private void RaiseE2e() { try { OnE2eChanged?.Invoke(Status); } catch { } }

    /// <summary>The host's answer: ours? signed by whom? the identity on file? Only then is the key used.</summary>
    private void OnKey(WsConnection ws, SessionSeal seal, System.Security.Cryptography.ECDiffieHellman eph, E2eKeyMsg key)
    {
        if (seal.Keyed) return;   // a second answer to a second hello: the first is in use (and its counter with it)
        var got = E2eHandshake.Accept(_sessionId, eph, key, out var why);
        if (got == null)
        {
            if (why != "not for this viewer") Trace.Write("e2e: the host's answer was refused — " + why);
            return;
        }
        var (verdict, previous) = Pins.Check(DeviceId, MachineName, Owner, got.IdentityPub);
        Trace.Write($"e2e: key received, {(got.Signed ? "signed " + E2eHandshake.Fingerprint(got.IdentityPub) : "unsigned")} — {verdict}");
        void Use()
        {
            seal.UseKey(got.Key, got.SenderId);
            seal.Sealing = true;
            _keyed = true; _signed = got.Signed; _held = false;
            RaiseE2e();
            try { OnKeyed?.Invoke(); } catch { }
        }
        switch (verdict)
        {
            case MachinePins.Verdict.Matches: Pins.Remember(DeviceId, MachineName, Owner, got.IdentityPub); Use(); break;
            case MachinePins.Verdict.FirstTime: Pins.Remember(DeviceId, MachineName, Owner, got.IdentityPub); Use(); break;
            case MachinePins.Verdict.NoIdentity: Use(); break;
            default:
                // Changed, or a pinned machine that did not sign: NOTHING is opened — no picture, and the host refuses
                // the clear input this window would send — until the technician says it was expected.
                _held = true; RaiseE2e();
                var handler = OnIdentityQuestion;
                if (handler == null) { Trace.Write("e2e: identity changed and nobody to ask — held"); return; }
                handler(verdict, previous, got.IdentityPub, yes =>
                {
                    if (!yes) { Trace.Write("e2e: the technician did not accept the machine's new identity"); return; }
                    Trace.Write("e2e: the technician accepted the machine's new identity");
                    Pins.Remember(DeviceId, MachineName, Owner, got.IdentityPub);
                    Use();
                });
                break;
        }
    }

    private void GiveUp(bool ended, string why)
    {
        if (_gaveUp) return;
        _gaveUp = true;
        try { OnGaveUp?.Invoke(ended, why); } catch (Exception ex) { Trace.Write("link: OnGaveUp subscriber threw — " + ex.Message); }
    }

    // A misbehaving subscriber must never be able to stop the reconnect loop — the trap
    // DeviceLink already records. A throw from the UI thread's marshalling used to be the kind
    // of thing that quietly ended the only connection the window had.
    private void Notify(Msg m) { try { OnMessage?.Invoke(m); } catch (Exception ex) { Trace.Write($"link: OnMessage subscriber threw on {m.GetType().Name} — {ex.Message}"); } }
    private static void Raise(Action<bool>? h, bool v) { try { h?.Invoke(v); } catch { } }

    public void Send(Msg m)
    {
        lock (_lock)
        {
            // A send issued before the socket is up is silently lost. That is one of the
            // black-screen candidates, so say so rather than swallowing it.
            if (_ws == null) { Trace.Write($"link: DROPPED outbound {m.GetType().Name} — socket not connected"); return; }
            _ws.Send(m);
        }
    }

    /// <summary>
    /// Sends only if it goes SEALED — the key of a copy straight to another machine (SessionForm.Bridge.cs), which the
    /// server must never read. False, and nothing sent, when this socket is not keyed and sealing: a socket that has just
    /// reconnected, or a session gone clear.
    /// </summary>
    public bool SendSealed(Msg m)
    {
        lock (_lock)
        {
            if (_ws?.Seal is not { Sealing: true, Keyed: true }) { Trace.Write($"link: NOT sending {m.GetType().Name} — this socket is not sealed"); return false; }
            return _ws.Send(m);
        }
    }

    /// <summary>False means the link had no room and the frame was NOT sent. A screen frame can
    /// be made good by demanding a complete one; a file chunk cannot be made good by anything, so
    /// its sender waits instead. See BUGS #8.</summary>
    public bool SendBinary(byte[] b) { lock (_lock) return _ws?.SendBinary(b) ?? false; }
}
