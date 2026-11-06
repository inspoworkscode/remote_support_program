using System.Net.WebSockets;

namespace RemoteSupport.Shared;

/// <summary>
/// Auto-reconnecting connection to the server's /ws/device endpoint, used by both the client app
/// and the unattended agent. Raises events for the messages a device cares about.
/// </summary>
public sealed class DeviceLink
{
    private readonly string _serverWs;
    private readonly DeviceIdentity _id;
    private readonly DeviceRole _role;
    private readonly string? _tenantKey;
    private WsConnection? _ws;
    private readonly object _lock = new();
    /// <summary>Collected once and reused across reconnects.</summary>
    private SystemSpec? _spec;
    /// <summary>Collected once — it shells out, and the answer cannot change while we run.</summary>
    private string? _hardwareId;

    public event Action<StartSessionMsg>? OnStartSession;
    public event Action<SessionEndedMsg>? OnSessionEnded;
    public event Action<CallUpdateMsg>? OnCallUpdate;
    public event Action<ChatMsg>? OnChat;
    /// <summary>The host reporting that a technician has started or stopped listening to this
    /// machine's sound. Relayed by the hub from the session socket, because the person at the
    /// machine is entitled to know — see MainForm.</summary>
    public event Action<AudioStatusMsg>? OnAudioStatus;
    /// <summary>A file moved on or off this machine. Logged by the tray app, never announced —
    /// see FileActivityMsg for why that is the decision it is.</summary>
    public event Action<FileActivityMsg>? OnFileActivity;
    /// <summary>A voice call on this machine (3.3), relayed by the hub from the host: who is
    /// talking, and where the person's microphone stands. The tray app shows it and ASKS.</summary>
    public event Action<VoiceStateMsg>? OnVoiceState;
    /// <summary>A technician started or stopped recording the session (1.6): disclosed in the tray app.</summary>
    public event Action<RecordingStateMsg>? OnRecordingState;
    /// <summary>Somebody from outside, brought in by the technician, joined or left (§155): TOLD in the tray app.</summary>
    public event Action<OutsiderJoinedMsg>? OnOutsiderJoined;
    /// <summary>
    /// What the server says is happening on this machine. It arrives whenever the state changes
    /// AND whenever this connection appears — which is the point. This link reconnects on its own
    /// and nothing used to re-send it anything when it did, so a tray app whose socket blinked
    /// mid-session never learned the session had ended (BUGS #2).
    /// </summary>
    public event Action<SessionStateMsg>? OnSessionState;
    /// <summary>What the SERVER holds for this machine's unattended access. The tray checkbox
    /// shows this rather than what it last clicked, so the two can never disagree and a machine
    /// that was switched off while the setting changed comes back telling the truth.</summary>
    public event Action<UnattendedStateMsg>? OnUnattendedState;
    /// <summary>Outside help (roadmap 3.9): whether this machine may make a code, and the live one.</summary>
    public event Action<OutsiderStateMsg>? OnOutsiderState;
    /// <summary>The answer to a code request: the code, or why not.</summary>
    public event Action<OutsiderCodeMsg>? OnOutsiderCode;
    /// <summary>
    /// A system action the technician has asked for that the HOST cannot perform. In practice
    /// that is Ctrl+Alt+Del and only Ctrl+Alt+Del: `SendSAS` requires a **service running as
    /// LocalSystem**, and the host is a LocalSystem process launched into the interactive
    /// session, which Windows does not treat the same way. Measured on 2026-09-15 — the host is
    /// SYSTEM, the call returns without error, and nothing happens, for either value of AsUser.
    /// The agent is a real session-0 service, so it is the one place on the machine that can.
    /// </summary>
    public event Action<SysActionMsg>? OnSysAction;
    /// <summary>Whether the monthly health check should be on this machine. Only the agent acts on
    /// it — see HealthCheckWantedMsg for why nothing else on the machine can.</summary>
    public event Action<HealthCheckWantedMsg>? OnHealthCheckWanted;
    /// <summary>Start (true) or stop sending figures for the console's Processes and Performance
    /// windows. Only reaches a link that said <see cref="DeviceCan.Watch"/>.</summary>
    public event Action<bool>? OnWatchRun;
    /// <summary>
    /// The server turning this machine away, in its own words — an unknown tenant key, a lapsed
    /// trial, a suspended account, a device key that does not match. Until this existed the
    /// reason arrived and was thrown on the floor: ErrorMsg had no case in HandleMessage at all,
    /// so every refusal looked identical to "the network is down" from the customer's side, and
    /// identical to nothing at all from the server's.
    /// </summary>
    public event Action<string>? OnServerError;
    public event Action<bool>? OnConnectedChanged;

    /// <summary>
    /// Why the last attempt was refused, or null if it was not. It exists to slow the retry down:
    /// a refusal is a DECISION, not a blip, and the answer will be the same in three seconds. A
    /// lapsed trial turns away every machine on the tenant at once, and without this each of them
    /// dials back twenty times a minute for ever — filling the server's log with the very warnings
    /// that were just added to make this diagnosable.
    /// </summary>
    private string? _refusedWhy;
    private const int RetryMs = 3000;
    private const int RefusedRetryMs = 30000;
    /// <summary>Not let on at all (<see cref="ErrorMsg.NotAdmitted"/>, the free version: a third computer, or one
    /// that was removed). Asking again every 30 s would change nothing; every 15 minutes is enough to notice
    /// a place freed or the company upgraded.</summary>
    private const int NotAdmittedRetryMs = 15 * 60 * 1000;
    private bool _notAdmitted;

    public bool Connected { get { lock (_lock) return _ws?.IsOpen ?? false; } }

    /// <summary>Said in every hello: what this build can do (<see cref="DeviceCan"/>). Fill before RunAsync.</summary>
    public List<string> Can { get; } = new();

    private MachineWatcher? _watcher;

    /// <summary>
    /// Offer live figures for the console's Processes and Performance windows (2026-10-05): says
    /// <see cref="DeviceCan.Watch"/> in the hello and measures while the server asks. In ONE place so
    /// the agent and the tray app cannot drift apart. Measuring stops the moment the connection goes —
    /// the server forgets who was watching then, and nobody is left to send to. Call before RunAsync.
    /// </summary>
    public void OfferWatch()
    {
        if (_watcher != null) return;
        Can.Add(DeviceCan.Watch);
        _watcher = new MachineWatcher(s => Send(new WatchSampleMsg { Sample = s }));
        OnWatchRun += on => _watcher.Set(on);
    }

    /// <summary>
    /// Offer to send Wake-on-LAN wake-ups for OTHER machines on this network (2026-10-06): says
    /// <see cref="DeviceCan.Wake"/> and answers <see cref="WakeSendMsg"/>. In ONE place, like
    /// <see cref="OfferWatch"/>, so the agent and the tray app cannot drift apart. The server hands
    /// MAC addresses only; <see cref="WakeOnLan.Send"/> broadcasts on this machine's own networks.
    /// </summary>
    public void OfferWake()
    {
        if (Can.Contains(DeviceCan.Wake)) return;
        Can.Add(DeviceCan.Wake);
        OnWakeSend += m =>
        {
            var reply = new WakeSentMsg { RequestId = m.RequestId };
            try { reply.Packets = WakeOnLan.Send(m.Macs); reply.Sent = true; }
            catch (Exception ex) { reply.Error = ex.Message; }
            Send(reply);
        };
    }

    /// <summary>
    /// The machine was removed from the company's list while connected, and should take itself off (2026-10-06,
    /// ErrorMsg.Uninstall). Only the agent acts on it: it is the one running as SYSTEM, able to remove a service.
    /// </summary>
    public event Action<string>? OnRemoved;

    /// <summary>The server asking this machine to wake another one on its network.</summary>
    public event Action<WakeSendMsg>? OnWakeSend;

    /// <summary>The company's look as it is now (2026-10-07, roadmap 4.6). Only reaches a link that said
    /// <see cref="DeviceCan.Branding"/>, and only when <see cref="BrandHash"/> is not already it.</summary>
    public event Action<BrandingMsg>? OnBranding;

    /// <summary>A newer release for this machine (auto-update, 2026-10-16). Only reaches an agent that said <see cref="DeviceCan.Update"/>.</summary>
    public event Action<UpdateRelease>? OnUpdateOffer;

    /// <summary>The fingerprint of the look this machine has (<see cref="Branding.LiveHash"/>), said in every hello.
    /// The owner moves it on once a new look is kept, so the next connect asks for nothing.</summary>
    public string BrandHash { get; set; } = "";

    /// <summary>Who this machine was installed for (2026-10-23), said in every hello; null from a scripted install, a
    /// downloaded copy, or the free version. The server keeps it once (<see cref="GivenPerson"/>).</summary>
    public GivenPerson? InstalledFor { get; set; }

    public DeviceLink(string serverWs, DeviceIdentity id, DeviceRole role, string? tenantKey = null)
    { _serverWs = serverWs.TrimEnd('/'); _id = id; _role = role; _tenantKey = string.IsNullOrWhiteSpace(tenantKey) ? null : tenantKey; }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Never across the internet unencrypted (SecureTransport). Told to the person the
                // same way a server's refusal is, because to them it is the same problem.
                if (SecureTransport.Refusal(_serverWs) is { } insecure)
                {
                    _refusedWhy = insecure;
                    try { OnServerError?.Invoke(insecure); } catch { }
                    throw new InsecureServerException(insecure);
                }
                var cws = new ClientWebSocket();
                cws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                await cws.ConnectAsync(new Uri($"{_serverWs}/ws/device"), ct);
                var ws = new WsConnection(cws);
                lock (_lock) _ws = ws;
                _refusedWhy = null; _notAdmitted = false;
                ws.OnMessage += m => { try { HandleMessage(m); } catch { } };
                Notify(true);
                ws.Send(new HelloMsg
                {
                    Role = _role, DeviceId = _id.DeviceId, DeviceKey = _id.DeviceKey,
                    MachineName = Environment.MachineName, UserName = Environment.UserName,
                    Os = Environment.OSVersion.VersionString,
                    Version = AppVersion.Current,
                    TenantKey = _tenantKey,
                    HardwareId = _hardwareId ??= MachineFingerprint.Get(),
                    Spec = _spec ??= SystemSpecCollector.Collect(),
                    Can = Can.ToList(),
                    // Every connect: a laptop moves between networks, and the server must know the
                    // one it was on LAST to wake it there.
                    Cards = WakeOnLan.Cards(),
                    BrandHash = BrandHash,
                    InstalledForId = InstalledFor?.Id ?? "", InstalledForName = InstalledFor?.Name ?? "",
                    InstalledForEmail = InstalledFor?.Email ?? ""
                });
                _ = PingLoop(ws, ct);
                await ws.RunAsync(ct);
            }
            catch { }
            lock (_lock) _ws = null;
            _watcher?.Set(false);
            Notify(false);
            try { await Task.Delay(_notAdmitted ? NotAdmittedRetryMs : _refusedWhy != null ? RefusedRetryMs : RetryMs, ct); } catch { break; }
        }
    }

    // A misbehaving subscriber must never be able to stop the reconnect loop.
    private void Notify(bool connected) { try { OnConnectedChanged?.Invoke(connected); } catch { } }

    private async Task PingLoop(WsConnection ws, CancellationToken ct)
    {
        try { while (ws.IsOpen && !ct.IsCancellationRequested) { ws.Send(new PingMsg()); await Task.Delay(15000, ct); } } catch { }
    }

    private void HandleMessage(Msg m)
    {
        switch (m)
        {
            case StartSessionMsg s: OnStartSession?.Invoke(s); break;
            case SessionEndedMsg e: OnSessionEnded?.Invoke(e); break;
            case CallUpdateMsg c: OnCallUpdate?.Invoke(c); break;
            case ChatMsg chat: OnChat?.Invoke(chat); break;
            case AudioStatusMsg a: OnAudioStatus?.Invoke(a); break;
            case FileActivityMsg fa: OnFileActivity?.Invoke(fa); break;
            case RecordingStateMsg rs: OnRecordingState?.Invoke(rs); break;
            case OutsiderJoinedMsg oj: OnOutsiderJoined?.Invoke(oj); break;
            case VoiceStateMsg vs: OnVoiceState?.Invoke(vs); break;
            case SessionStateMsg ss: OnSessionState?.Invoke(ss); break;
            case UnattendedStateMsg us: OnUnattendedState?.Invoke(us); break;
            case OutsiderStateMsg os: OnOutsiderState?.Invoke(os); break;
            case OutsiderCodeMsg oc: OnOutsiderCode?.Invoke(oc); break;
            case SysActionMsg sa: OnSysAction?.Invoke(sa); break;
            case HealthCheckWantedMsg hw: OnHealthCheckWanted?.Invoke(hw); break;
            case WatchRunMsg wr: OnWatchRun?.Invoke(wr.On); break;
            case WakeSendMsg ws: OnWakeSend?.Invoke(ws); break;
            case BrandingMsg bm: OnBranding?.Invoke(bm); break;
            case UpdateOfferMsg uo: OnUpdateOffer?.Invoke(uo.Release); break;
            // The server closes the socket straight after this, so it is the last thing that will
            // be heard until the retry — which is exactly why it has to be passed on rather than
            // dropped. A subscriber that throws must not stop the reconnect loop.
            case ErrorMsg err:
                _refusedWhy = err.Message;
                _notAdmitted = err.NotAdmitted;
                if (err.Uninstall) try { OnRemoved?.Invoke(err.Message); } catch { }
                try { OnServerError?.Invoke(err.Message); } catch { }
                break;
        }
    }

    /// <summary>A support request: the subject, what is wrong, and a picture of the screen if they sent one (2026-10-09).</summary>
    public void RequestCall(string contact, string subject, string details = "", byte[]? screenshot = null, string email = "")
    { lock (_lock) _ws?.Send(new RequestCallMsg { ContactName = contact, ContactEmail = email, Issue = subject, Details = details, Screenshot = screenshot }); }
    public void CancelCall() { lock (_lock) _ws?.Send(new CancelCallMsg()); }
    public void SendChat(string from, string text) { lock (_lock) _ws?.Send(new ChatMsg { From = from, Text = text, FromTech = false }); }
    public void Send(Msg m) { lock (_lock) _ws?.Send(m); }

    /// <summary>
    /// Tells the server the person at the machine chose Exit (<see cref="ClientClosingMsg"/>) and
    /// closes the socket once that has actually gone — CloseAsync drains the send queue first. The
    /// caller cancels RunAsync afterwards so nothing dials back in. Returns false when there was no
    /// connection to say it over: then the server was never told.
    /// </summary>
    public async Task<bool> SayClosingAsync()
    {
        WsConnection? ws; lock (_lock) ws = _ws;
        if (ws is not { IsOpen: true }) return false;
        ws.Send(new ClientClosingMsg());
        await ws.CloseAsync("closed by the user");
        return true;
    }
}
