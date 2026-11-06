using System.Net.WebSockets;
using RemoteSupport.Shared;

namespace RemoteSupport.Host;

/// <summary>
/// Connects to the server as a session host and serves screen frames, input, files and scripts.
/// One instance per active support session.
/// </summary>
public sealed partial class HostEngine
{
    private readonly string _serverWs;
    private readonly string _sessionId;
    private readonly string _hostToken;
    // Which capture path is running is decided once, on the capture thread, by trying the fast
    // one -- see ChooseSource. It starts as the GDI path so that everything reading Monitors
    // before the capture thread is up has something to read.
    private IScreenSource _capture = new ScreenCapture();
    private readonly InputInjector _input;
    private readonly ScriptRunner _scripts = new();
    private readonly FileTransfer _files = new();
    private readonly ClipboardAccess _clipboard = new();
    private readonly AudioCapture _audio = new();
    // Talking to the customer (3.3). Built in the constructor, because it sends on _ws.
    private readonly VoiceCall _voice;
    // Trees arriving from the technician, and the offers we have made that are waiting to be
    // accepted before we start streaming.
    private readonly Dictionary<string, FileSetReceiver> _incomingSets = new();
    private readonly Dictionary<string, TaskCompletionSource<FileSetAcceptMsg>> _pullWaiters = new();
    private readonly object _setLock = new();
    private readonly HashSet<int> _subscribed = new();
    // Input is injected on its own desktop-following thread, not on the socket receive thread.
    private readonly System.Collections.Concurrent.BlockingCollection<Msg> _inputQueue = new(new System.Collections.Concurrent.ConcurrentQueue<Msg>(), 4096);
    private readonly object _subLock = new();
    private int _jpeg = 55, _fps = 12;
    // Stage 2.3. The slider sets its ceiling; the link decides what is actually sent.
    private readonly LinkController _link = new(55, 12);
    private string _linkWhy = "";
    private volatile bool _forceFull = true;
    // Last values traced, so a link that is losing frames steadily says so once per report rather
    // than on every pass.
    private long _lastDroppedFrames, _lastDroppedCritical;
    /// <summary>
    /// How long every viewer must be unsubscribed before the capture loop gives the screen back.
    ///
    /// Long enough that switching monitors, or a viewer reconnecting after a blink, does not tear
    /// the duplication down and rebuild it; short enough that a session left open does not keep
    /// the machine's screen locked away from everything else on it.
    /// </summary>
    private static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(3);
    // ---- stage 2.2, the codec ----------------------------------------------------------------
    // What the viewer said it could decode, what we settled on, and why — all three, because "the
    // picture is soft" and "the encoder was refused and we are back on JPEG tiles" are the same
    // complaint from the technician's chair and only the third field tells them apart.
    private readonly List<string> _viewerCodecs = new();
    private RemoteSupport.Media.H264Encoder? _encoder;
    private int _encoderMonitor = -1;
    private string _codec = VideoCodec.JpegTiles;
    private string _codecWhy = "";
    // Set from the socket thread, acted on by the capture thread: an encoder belongs to the thread
    // that follows the user between desktops, the same as the duplication it shares a device with.
    private volatile bool _codecDirty;
    private WsConnection? _ws;
    private readonly bool _askConsent;
    private readonly string _technicianName;
    /// <summary>Another company's name when somebody from outside is asking (3.9); empty otherwise.</summary>
    private readonly string _visitorCompany;
    /// <summary>True once the person at the machine has allowed the session, or was never asked.
    /// Until then OnMessage and OnBinary act on nothing.</summary>
    private volatile bool _consented;
    /// <summary>Cancelled when the session ends while the question is still on their screen.</summary>
    private readonly CancellationTokenSource _consentWithdrawn = new();

    // ---- end-to-end encryption (2026-10-07; docs/NEXT-SESSION-E2E.md) ----
    /// <summary>This session's key: made here, given to each viewer wrapped for it alone.</summary>
    private readonly byte[] _sessionKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    private readonly System.Security.Cryptography.ECDiffieHellman _hostEph = E2eHandshake.NewEphemeral();
    /// <summary>This machine's identity (SYSTEM only), or null — then the answers are unsigned.</summary>
    private readonly Lazy<System.Security.Cryptography.ECDsa?> _identity = new(MachineIdentityStore.TryLoad);
    private SessionSeal? _seal;
    /// <summary>Viewer key -> its answer, so a viewer saying hello twice gets the SAME sender number (a second
    /// number would restart its counter, and its frames would be refused as replays).</summary>
    private readonly Dictionary<string, E2eKeyMsg> _answers = new();
    private uint _nextSender;

    private void AnswerViewer(E2eHelloMsg hello)
    {
        E2eKeyMsg? answer;
        lock (_answers)
        {
            if (!_answers.TryGetValue(hello.Pub, out answer))
            {
                answer = E2eHandshake.Answer(_sessionId, _hostEph, _sessionKey, hello.Pub, ++_nextSender, _identity.Value);
                if (answer == null) { HostTrace.Write("e2e: a viewer's key was not a key — not answered"); return; }
                _answers[hello.Pub] = answer;
                HostTrace.Write($"e2e: viewer {answer.SenderId} given the session key{(answer.Signature.Length > 0 ? ", signed" : ", UNSIGNED (no identity here)")}");
            }
        }
        _ws?.Send(answer);
    }

    public HostEngine(string serverWs, string sessionId, string hostToken, bool askConsent = false, string technicianName = "", string visitorCompany = "",
        bool showNotice = false)
    {
        _serverWs = serverWs; _sessionId = sessionId; _hostToken = hostToken; _showNotice = showNotice;
        _askConsent = askConsent; _technicianName = technicianName; _visitorCompany = visitorCompany;
        _input = new InputInjector(() => _capture.Monitors);
        _voice = new VoiceCall(m => _ws?.Send(m), b => _ws?.SendBinary(b) ?? false);
        // While a call is on, Listen leaves this process out of what it captures: the technician
        // is listening to the machine, not to their own voice coming back out of its speakers.
        _voice.CallChanged += on => _audio.ExcludeSelf = on;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Native.SetDpiAware();
        HostTrace.WriteIdentity(_sessionId);

        // Never across the internet unencrypted: the screen and every keystroke go down this socket.
        SecureTransport.Require(_serverWs);

        // Asking the person at the machine. The socket is opened FIRST (2026-10-02), saying so on
        // its address, so the technician is shown "waiting for them to accept" instead of nothing
        // for a minute — internal testing, item 4. Nothing that could expose the machine happens
        // before the answer: OnMessage reads nothing but "the session ended" until _consented,
        // and the monitors, the picture and the clipboard all start below the question.
        var brand = _askConsent ? Branding.Load() : null;
        int askSeconds = ConsentPrompt.Seconds(brand?.ConsentTimeoutSeconds ?? 0);
        var address = $"{_serverWs}/ws/session?role=host&id={_sessionId}&token={_hostToken}"
                    + (_askConsent ? $"&consent=asking&secs={askSeconds}" : "");
        var cws = new ClientWebSocket();
        await cws.ConnectAsync(new Uri(address), ct);
        _ws = new WsConnection(cws);
        // End-to-end encryption (2026-10-07): the session key is made HERE and never leaves this machine except
        // wrapped for a viewer that asked (E2eHelloMsg). Nothing is sealed until the hub says every viewer can open it.
        _seal = new SessionSeal(_sessionId);
        _seal.UseKey(_sessionKey, 0);
        _ws.Seal = _seal;
        _ws.OnMessage += OnMessage;
        _ws.OnBinary += OnBinary;
        var pump = _ws.RunAsync(ct);

        if (_askConsent)
        {
            // A socket that drops while they are deciding leaves nothing to say yes TO.
            _ = pump.ContinueWith(_ => { try { _consentWithdrawn.Cancel(); } catch { } }, TaskScheduler.Default);
            var (answer, files) = ConsentPrompt.Ask(brand!, _technicianName, askSeconds, _consentWithdrawn.Token, _visitorCompany);
            HostTrace.Write($"consent: {answer}" + (files ? " — and files" : ""));
            if (answer != ConsentAnswer.Granted)
            {
                // Withdrawn means the technician cancelled or the session ended: there is nobody
                // to tell. Otherwise the reason goes to the technician in words — "nobody answered"
                // and "they said no" are different conversations to have with a customer.
                if (answer != ConsentAnswer.Withdrawn)
                    _ws.Send(new DeclineSessionMsg
                    {
                        SessionId = _sessionId,
                        Reason = answer == ConsentAnswer.TimedOut
                            ? $"Nobody at {Environment.MachineName} answered within {askSeconds} seconds, so the connection was not allowed."
                            : $"The person at {Environment.MachineName} declined the connection."
                    });
                await _ws.CloseAsync("declined");
                try { await pump; } catch { }
                return;
            }
            _ws.Send(new ConsentGrantedMsg { SessionId = _sessionId, Files = files });
        }
        _consented = true;
        // Attached before RunAsync, like every other handler, for the reason §22 records: a
        // handler attached afterwards misses whatever arrived in between and the loss is silent.
        _clipboard.Changed += OnCustomerClipboardChanged;
        // Clear out what earlier pastes staged under the user's temp. At session start, never
        // during one: a folder is still being read by any paste Explorer has not finished copying,
        // and Explorer does that on its own schedule. The mirror of DragOut.SweepOlderThan on the
        // console side, for the same reason and with the same timing.
        HostPaths.SweepPasteStaging(TimeSpan.FromHours(12));
        // A technician's printer left behind by a host that was killed mid-session (remote printing):
        // gone before anybody can print to a printer nothing is collecting from.
        _ = Task.Run(() =>
        {
            try { var n = PrintRedirect.SweepLeftovers(); if (n > 0) HostTrace.Write($"print: removed {n} printer(s) left by an earlier session"); }
            catch (Exception ex) { HostTrace.Write("print: sweep failed: " + ex.Message); }
        });

        _capture.Refresh();
        _ws.Send(new MonitorsMsg { Monitors = _capture.Monitors });
        _ws.Send(new SysInfoMsg { Info = CollectSysInfo() });
        // This host seals: every console that can says hello and is given the key. After the yes, because the hub
        // passes on nothing a host says before it.
        _ws.Send(new E2eReadyMsg());

        // Capture, cursor and input each run on a DEDICATED thread. Desktop attachment is a
        // per-thread property, and async continuations hop threads, so these cannot be tasks:
        // a thread-pool thread would lose its desktop the moment it resumed elsewhere.
        var threads = new[]
        {
            StartLoop("capture", () => CaptureLoop(ct)),
            StartLoop("cursor",  () => CursorLoop(ct)),
            StartLoop("input",   () => InputLoop(ct)),
        };
        await pump;
        // Stop listening before anything else: an audio thread still pushing frames into a dead
        // socket is a quiet way to keep a session's resources alive after it has ended.
        _audio.Stop();
        _voice.End();
        _inputQueue.CompleteAdding();
        // Only a session that ended WITHOUT the server saying so reaches here with anything held —
        // the socket dropped — and then there is nobody to tell; OnSessionEnded is where the last
        // line goes out. If that close is still draining, let it finish before the process tears down.
        if (_closing != null) { try { await _closing.WaitAsync(TimeSpan.FromSeconds(3)); } catch { } }
        _clipLedger?.Dispose();
        // However the session ended, their screen comes back — and the technicians' printers go.
        _privacy.Dispose();
        _printing?.Dispose();
        _notice.Dispose();
        foreach (var t in threads) { try { t.Join(2000); } catch { } }
        // The clipboard thread owns a window; let it destroy it rather than leaking it for the
        // life of the process.
        _clipboard.Dispose();
    }

    private void OnMessage(Msg m)
    {
        // Whether to seal (2026-10-07): may arrive with the yes, a moment before _consented is set; it reveals nothing.
        if (m is E2eModeMsg mode)
        {
            if (_seal != null) { _seal.Sealing = mode.Sealed; _seal.RefuseClearContent = mode.Sealed; }
            HostTrace.Write(mode.Sealed ? "e2e: sealing — every viewer can open it" : "e2e: CLEAR — a viewer in the session cannot open sealed content");
            // Whatever a viewer had was sealed differently a moment ago: a complete picture, and the pointer again.
            _forceFull = true; _cursorResend = true;
            return;
        }
        // Before the person at the machine has said yes, nothing a technician sends is acted on.
        // The one thing listened for is the session ending — the technician cancelling, or the
        // server giving up — which takes the question off their screen.
        if (!_consented)
        {
            if (m is SessionEndedMsg) { try { _consentWithdrawn.Cancel(); } catch { } }
            return;
        }
        switch (m)
        {
            case E2eHelloMsg hello:
                AnswerViewer(hello);
                break;
            case SessionEndedMsg:
                OnSessionEnded();
                break;
            case SelectionRequestMsg sr:
                // Off the receive loop: as SYSTEM this starts a process as the person and waits for
                // it, and the picture and the input must not wait with it.
                _ = Task.Run(() =>
                {
                    var a = ExplorerSelection.ReadAsUser();
                    HostTrace.Write($"selection: {a.Where} {a.Paths.Count} item(s){(a.Why.Length > 0 ? " — " + a.Why : "")}");
                    _ws?.Send(new SelectionMsg { RequestId = sr.RequestId, Where = a.Where, Folder = a.Folder, Paths = a.Paths, Why = a.Why });
                });
                break;
            case SubscribeMsg s:
                lock (_subLock) { if (s.On) _subscribed.Add(s.Monitor); else _subscribed.Remove(s.Monitor); }
                // A console that names no codec is an older build, and an older build only knows
                // JPEG tiles. Absent must therefore mean tiles, not "whatever we prefer".
                lock (_subLock) { _viewerCodecs.Clear(); _viewerCodecs.AddRange(s.Codecs ?? new List<string>()); }
                _codecDirty = true;
                _forceFull = true;
                // The pointer has to introduce itself to this viewer as well. The picture is not
                // the only thing a new -- or reconnected -- viewer has never seen, and the shape
                // travels once per session, so without this it would never travel again.
                _cursorResend = true;
                break;

            // EVERY TECHNICIAN HAS GONE, SO NOBODY IS SUBSCRIBED — whatever the flag says.
            //
            // A subscription used to be permanent for the life of the host: _subscribed was added
            // to on SubscribeMsg and never cleared when the viewer went away. A viewer that
            // simply DISCONNECTS — a dropped socket, a closed console, a technician going home —
            // never sends On=false, so the host went on believing somebody was watching.
            //
            // That is a privacy leak, and it is precisely the one Clint's decision of 2026-09-18
            // exists to prevent: the customer's clipboard is allowed to cross only while a
            // technician is actually looking. Caught by clipcheck on 2026-09-21, which received
            // the customer's own copied text on a viewer that had never subscribed, because an
            // earlier viewer of the same host had and then left.
            //
            // The server already tells us: it removes a viewer from the session on disconnect and
            // broadcasts the participants, host included. An empty technician list means there is
            // no viewer at all — the name is only removed once that technician's LAST viewer has
            // gone — so there can be nothing to keep subscribed. A reconnecting viewer subscribes
            // again, which is how the pointer and the first full frame already work.
            case ParticipantsMsg pm when pm.Technicians.Count == 0:
            {
                FollowNotice(pm.Technicians);
                // Nobody left to talk to: the call ends and the microphone with it.
                _voice.Participants(pm.Technicians);
                // And nobody's printer stays on their machine.
                _printing?.RemoveAll();
                bool had;
                lock (_subLock) { had = _subscribed.Count > 0; _subscribed.Clear(); }
                if (had) HostTrace.Write("subscriptions cleared: every technician has left the session, so nobody is watching");
                // Nobody is working, so nobody's work is being hidden: a customer must not sit in
                // front of a black screen for the length of a reconnect hold with no one there.
                if (_privacy.On) { _privacy.Hide(); SendPrivacyState(""); }
                break;
            }
            case PrivacyScreenMsg ps:
                // Off the receive loop: Show waits for its windows to come up.
                _ = Task.Run(() =>
                {
                    string err = "";
                    if (ps.On) err = _privacy.Show(PrivacyMessage());
                    else _privacy.Hide();
                    SendPrivacyState(err);
                });
                break;
            // The server had to drop a frame on its way to one of the viewers, so that viewer's picture
            // is broken until the next key frame — and with key frames a minute apart, the next one
            // must be asked for (HANDOVER §142). The server limits it to one a second.
            case KeyFrameRequestMsg:
                if (++_keyRequests == 1) HostTrace.Write("codec: the server asked for a complete picture — a frame to a viewer was dropped on the way");
                _forceFull = true;
                break;
            // Quality moves the H.264 bitrate as well as the JPEG quality, and rate control is set
            // when the encoder is built, so the encoder is rebuilt. That is cheap and rare, and it
            // is the reason _forceFull is set here rather than only for the tile path: a rebuilt
            // encoder and the console no longer agree about the previous frame.
            // The slider is a CEILING, not a setting: the controller may go below it and may never
            // go above it. See LinkController.
            //
            // It no longer rebuilds the encoder or demands a complete frame, and that is the whole
            // difference between a slider a human drags twice a session and a controller that
            // nudges twice a second. A rebuild costs a key frame and a stall, and a forced full
            // frame is the most expensive thing the host can send -- spending either on the
            // congested link that caused the nudge is how adaptive quality makes a bad link worse.
            // A CODEC change still demands both; that is handled where the codec changes.
            case QualityMsg q:
                _link.SetCeiling(q.Jpeg, q.Fps);
                ApplyLink(traceIt: true);
                break;
            // Injection must happen on the input thread, which is the one attached to the desktop
            // the user is looking at. This callback runs on the socket receive thread.
            case MouseMsg mo: Enqueue(mo); break;
            case KeyMsg k: Enqueue(k); break;
            case ScriptRunMsg sr: _ = _scripts.RunAsync(sr, o => _ws?.Send(o)); break;
            case ScriptKillMsg sk: _scripts.Kill(sk.RunId); break;
            // Every path arriving from the technician's end goes through HostPaths.Resolve, here,
            // once. Below this line nothing else in the host has to know that %DESKTOP% exists —
            // which is what stopped it being wired up in three places out of four. BUGS #6.
            case FileOfferMsg fo:
            {
                fo.TargetDir = HostPaths.Resolve(fo.TargetDir);
                var d = _files.BeginReceive(fo);
                if (d != null) { _ws?.Send(d); ReportFileActivity(false, fo.Name, 1, fo.Size, d.Error); }
                else _ws?.Send(new FileAcceptMsg { FileId = fo.FileId, Accept = true });
                break;
            }
            case FileDoneMsg fd: break; // ack from viewer, ignore
            case FileListRequestMsg fl: _ws?.Send(_files.List(HostPaths.Resolve(fl.Path) ?? "")); break;
            case FilePullMsg fp:
            {
                var path = HostPaths.Resolve(fp.Path) ?? fp.Path;
                // A print job on its way to a technician's printer: already on the ticket as a print,
                // so not again as a copy — and gone from their disk once it has been sent.
                bool printJob = PrintRedirect.IsJobFile(path);
                _ = _files.SendFileAsync(path, fp.FileId, x =>
                {
                    _ws?.Send(x);
                    // Reported when the bytes have actually gone, by the end that sent them.
                    if (x is FileDoneMsg done)
                    {
                        if (printJob) { try { File.Delete(path); } catch { } }
                        else ReportFileActivity(true, Path.GetFileName(path), 1, done.Ok ? SizeOf(path) : 0, done.Error);
                    }
                }, b => _ws?.SendBinary(b) ?? false, CancellationToken.None);
                break;
            }
            case SysActionMsg sa: DoSysAction(sa.Action); break;

            // Listening runs on its own thread (AudioCapture), so this callback — which is the
            // socket receive thread — only starts and stops it.
            // Enumerating endpoints and the mixer takes a moment and does COM work; do it off the
            // socket receive thread so a slow audio stack cannot stall the session's messages.
            case AudioDevicesRequestMsg:
                _ = Task.Run(() => { try { _ws?.Send(AudioDiagnostics.Collect()); } catch (Exception ex) { HostTrace.Write("audio diagnostics: " + ex.Message); } });
                break;

            case AudioListenMsg al:
                if (al.On) _audio.Start(b => _ws?.SendBinary(b), s => _ws?.Send(s));
                else _audio.Stop();
                break;

            // ---- talking to the customer (3.3) ----------------------------------------------
            case VoiceTalkMsg vt: _voice.Talk(vt); break;
            // Only ever from the person's tray app, carried by the hub — which refuses it in an
            // unattended session. See VoiceCall for the rules.
            case VoiceMicAnswerMsg va: _voice.MicAnswer(va.Allow); break;
            // A technician who left without saying "stop talking" is taken out of the call.
            case ParticipantsMsg pm:
                _voice.Participants(pm.Technicians);
                FollowNotice(pm.Technicians);
                // A technician who has left takes their printer with them.
                _printing?.KeepOnly(pm.Technicians);
                break;
            // Remote printing (roadmap 1.8). Off the receive loop: adding a printer and printing a
            // file both wait on the spooler, and the picture must not wait with them.
            case RemotePrintMsg rp: _ = Task.Run(() => SetRemotePrint(rp)); break;
            case PrintFileMsg pf: _ = Task.Run(() => PrintTheirFile(pf)); break;

            // ---- whole trees, arriving ----------------------------------------------------
            case FileSetOfferMsg fso:
            {
                // Dropping onto the remote desktop means "put this on that machine", and the
                // desktop is where anyone making that gesture expects it to land. The technician
                // cannot know the customer's profile path, so they ask for it by name and the
                // host — which is the only side that knows where the logged-on user's profile is,
                // and that it is not SYSTEM's — resolves it.
                fso.TargetDir = HostPaths.Resolve(fso.TargetDir);

                var rx = new FileSetReceiver(fso);
                if (!rx.Begin(_files.DefaultDir, out var why))
                {
                    rx.Dispose();
                    _ws?.Send(new FileSetAcceptMsg { SetId = fso.SetId, Accept = false, Error = why });
                    HostTrace.Write($"fileset {fso.SetId}: refused — {why}");
                    break;
                }
                lock (_setLock) _incomingSets[fso.SetId] = rx;
                _ws?.Send(new FileSetAcceptMsg { SetId = fso.SetId, Accept = true, TargetDir = rx.TargetDir });
                HostTrace.Write($"fileset {fso.SetId}: accepting {fso.Entries.Count} entries, " +
                                $"{FileSetLimits.Human(fso.TotalBytes)} into {rx.TargetDir}");
                break;
            }

            // ---- whole trees, being asked for ---------------------------------------------
            case FileSetPullMsg fsp: _ = SendFileSetAsync(fsp); break;

            // The technician's end accepting a set we offered, so we can start streaming.
            case FileSetAcceptMsg fsa:
            {
                TaskCompletionSource<FileSetAcceptMsg>? waiter;
                lock (_setLock) _pullWaiters.Remove(fsa.SetId, out waiter);
                waiter?.TrySetResult(fsa);
                break;
            }

            case FileSetDoneMsg: break;   // the far end's acknowledgement; nothing to do

            // ---- files straight from or to another machine the technician has open (HostEngine.Bridge.cs) ----
            case BridgeKeyMsg bk: OnBridgeKey(bk); break;
            case BridgeEndMsg be: OnBridgeEnd(be); break;

            // The technician asked for the customer's clipboard. Answer even when there is
            // nothing on it, so the console can say "nothing to copy" rather than sit silent.
            case ClipboardRequestMsg:
                SendClipboardToTechnician(auto: false);
                break;

            // The technician sent theirs. Put it on the customer's clipboard.
            case ClipboardMsg cb:
            {
                // A file list the technician wants pasteable on the customer's machine. The bytes
                // arrived as a file set first; these are the paths they landed at.
                if (cb.Files is { Count: > 0 })
                {
                    // As the logged-on user, not as this process: Explorer will not paste
                    // from a list a SYSTEM process owns (ClipboardAccess.SetFilesAsUser).
                    bool setOk = _clipboard.SetFilesAsUser(cb.Files, out var fileWhy);
                    if (!setOk) _ws?.Send(new ClipboardMsg { Text = "", Error = "could not put the files on the customer's clipboard: " + fileWhy });
                    HostTrace.Write(setOk
                        ? $"clipboard: put {cb.Files.Count} file(s) on the customer's clipboard"
                        : $"clipboard: FAILED to put {cb.Files.Count} file(s) on the clipboard — {fileWhy}");
                    break;
                }
                var text = cb.Text ?? "";
                if (text.Length > ClipboardAccess.MaxChars) text = text[..ClipboardAccess.MaxChars];
                bool ok = _clipboard.SetText(text, out var why);
                if (!ok) _ws?.Send(new ClipboardMsg { Text = "", Error = "could not set the customer's clipboard: " + why });
                HostTrace.Write(ok
                    ? $"clipboard: technician set {text.Length} characters on the customer's clipboard"
                    : $"clipboard: FAILED to set {text.Length} characters — {why}");
                // What arrived is now what is on the clipboard, so the watcher must not offer it
                // straight back the moment somebody looks at the screen again.
                if (ok) _lastMirroredText = text;
                NoteClipText(fromMachine: false, chars: text.Length, error: ok ? null : why);
                break;
            }
        }
    }

    // ---- stage 3.5.1: the clipboard mirrors itself -------------------------------------------

    /// <summary>
    /// The last text that crossed in either direction, so the same string is never sent twice and
    /// never sent back where it came from.
    ///
    /// Two ends both watching one logical clipboard is a loop waiting to happen, and
    /// <c>ClipboardAccess</c>'s write guard only closes the half of it that happens inside this
    /// process. The other half is this: the technician's text lands here, the customer then copies
    /// something unrelated, and later the same text comes round again. Comparing content is
    /// crude and completely reliable.
    /// </summary>
    private string _lastMirroredText = "";

    /// <summary>
    /// Somebody changed the clipboard on the customer's machine. Called on the watcher thread.
    ///
    /// **Only while a viewer is actually subscribed** — Clint's decision of 2026-09-18, and the
    /// thing that keeps §8's objection answered. §8 refused an automatic sync because the
    /// customer's clipboard is very often a password; mirroring only while a technician is looking
    /// at the screen means the password they copied at nine this morning never crosses, and the
    /// licence key they copy while being helped does. Nobody watching, nothing leaves.
    /// </summary>
    private void OnCustomerClipboardChanged()
    {
        bool watched;
        lock (_subLock) watched = _subscribed.Count > 0;
        if (!watched) return;
        SendClipboardToTechnician(auto: true);
    }

    /// <summary>
    /// Puts whatever is on the customer's clipboard onto the session socket — as an answer to a
    /// request, or because it changed under a technician who is watching.
    ///
    /// Files are measured here rather than at the far end, because only this machine can walk the
    /// tree. The bytes do not move: a clipboard file list is paths, and the console decides from
    /// the size whether to fetch them now or offer them (see <see cref="FileSetLimits.AutoFetchBytes"/>).
    /// </summary>
    private void SendClipboardToTechnician(bool auto)
    {
        // Files first: when someone has copied files in Explorer the clipboard usually carries a
        // text form as well, and answering with the text would be the less useful half of what
        // they copied.
        // As the customer, not as this process: a real Ctrl+C on their machine is a delay-
        // rendered data object that SYSTEM cannot read at all (ClipboardAccess.GetFilesAsUser).
        var files = _clipboard.GetFilesAsUser();
        if (files is { Count: > 0 })
        {
            long bytes = 0;
            int count = 0;
            // Walking a copied folder can take a moment and this is the watcher thread, not the
            // socket thread, so it is allowed to. Plan stops itself at MaxEntries, so a Ctrl+C
            // over the root of a drive cannot sit here for ever.
            try { var plan = FileSetSender.Plan(files); bytes = plan.TotalBytes; count = plan.FileCount; }
            catch (Exception ex) { HostTrace.Write($"clipboard: could not measure the copied files: {ex.GetType().Name}"); }

            _ws?.Send(new ClipboardMsg { Text = "", Files = files, Bytes = bytes, Auto = auto });
            HostTrace.Write($"clipboard: {(auto ? "customer copied" : "technician asked for")} " +
                            $"{files.Count} item(s), {count} file(s), {FileSetLimits.Human(bytes)}");
            return;
        }

        var text = _clipboard.GetText();
        if (text == null)
        {
            // A push has nobody waiting on it, so silence is the right answer: the clipboard
            // holding a bitmap is not an error worth putting on a technician's screen. A request
            // always gets a reply, because somebody pressed something and is waiting for it.
            if (!auto)
            {
                _ws?.Send(new ClipboardMsg { Text = "", Error = "there is nothing on the customer's clipboard" });
                HostTrace.Write("clipboard: technician asked for it; nothing there");
            }
            return;
        }

        bool cut = text.Length > ClipboardAccess.MaxChars;
        if (cut) text = text[..ClipboardAccess.MaxChars];
        // Already sent, or it is what the technician put here in the first place. Either way
        // sending it would be noise at best and a loop at worst.
        if (auto && text == _lastMirroredText) return;
        _lastMirroredText = text;

        _ws?.Send(new ClipboardMsg { Text = text, Truncated = cut, Auto = auto });
        // The length, never the content: this is the feature where the clipboard is a password,
        // and the trace file is not the place for it.
        HostTrace.Write($"clipboard: sent {text.Length} characters to the technician{(cut ? " (truncated)" : "")}{(auto ? " (they copied it)" : "")}");
        NoteClipText(fromMachine: true, chars: text.Length, error: null);
    }

    // ---- the ledger: clipboard text on the ticket, without burying it -------------------------
    // Every movement is recorded, because seamless must not mean invisible (3.5.3); ClipLedger adds
    // them up per direction and reports once a minute. Until 2026-09-29 the minute was only noticed
    // when ANOTHER copy arrived, and the end-of-session report went out after the socket had
    // closed — so a session with one burst of copies never put a line on its ticket at all.

    private static readonly TimeSpan ClipLedgerQuiet = TimeSpan.FromSeconds(60);
    private ClipLedger? _clipLedger;
    private ClipLedger ClipLedgerNow => LazyInitializer.EnsureInitialized(ref _clipLedger, () => new ClipLedger(ClipLedgerQuiet, ReportClipText));

    private void NoteClipText(bool fromMachine, int chars, string? error) => ClipLedgerNow.Note(fromMachine, chars, error);

    // ---- the privacy screen (roadmap 1.7) -----------------------------------------------------
    private readonly PrivacyScreen _privacy = new();
    /// <summary>"Being controlled remotely" (2026-10-06): shown for a free computer's sessions, which have no tray
    /// app to say so. Asked for by the hub (<c>StartSessionMsg.ShowNotice</c>), passed as <c>--notice</c>.</summary>
    private readonly ControlledNotice _notice = new();
    private readonly bool _showNotice;

    /// <summary>The bar follows who is in the session: named while they are, gone when nobody is.</summary>
    private void FollowNotice(List<string> technicians)
    {
        if (!_showNotice) return;
        var words = technicians.Count == 0 ? "" : ControlledNotice.Wording(technicians);
        // Off the receive loop: the bar waits for its windows to come up.
        _ = Task.Run(() => { try { _notice.Set(words); } catch (Exception ex) { HostTrace.Write("notice: " + ex.Message); } });
    }

    /// <summary>
    /// The one line on the black screen, naming the IT company from the branding installed beside
    /// this exe — the name the customer knows them by. Falls back to words rather than to the
    /// branding's placeholder, which would read "Your Company is working on this computer".
    /// </summary>
    private static string PrivacyMessage()
    {
        var name = "";
        try { name = Branding.Load().CompanyName.Trim(); } catch { }
        if (name.Length == 0 || name == new Branding().CompanyName) name = "Your IT company";
        return $"{name} is working on this computer.\nYour screen will come back when they finish.";
    }

    /// <summary>What the screen IS doing — sent after every change, and on a refusal.</summary>
    private void SendPrivacyState(string error) =>
        _ws?.Send(new PrivacyStateMsg { On = _privacy.On, Error = error, Monitors = _privacy.On ? _privacy.Covered.Count : 0 });

    // ---- remote printing (roadmap 1.8, 2026-10-06) --------------------------------------------
    private PrintRedirect? _printing;
    private PrintRedirect Printing => LazyInitializer.EnsureInitialized(ref _printing, () =>
    {
        var p = new PrintRedirect();
        p.JobReady += (tech, path, document) =>
        {
            _ws?.Send(new RemotePrintJobMsg { JobId = Guid.NewGuid().ToString("N"), Technician = tech, Document = document, Path = path, Bytes = SizeOf(path) });
            ReportPrint("print-out", document, "", 0, null);
        };
        p.JobsLost += (tech, n) => _ws?.Send(new RemotePrintStateMsg { Technician = tech, On = p.IsOn(tech), Printer = p.PrinterOf(tech), Lost = n });
        return p;
    });

    /// <summary>The product name the person knows, for the printer's name: "Sue Tech's printer (Coastal IT Remote Support)".</summary>
    private static string ProductName()
    {
        try
        {
            var b = Branding.Load();
            if (b.ProductName.Trim().Length > 0 && b.ProductName != new Branding().ProductName) return b.ProductName.Trim();
            if (b.CompanyName.Trim().Length > 0 && b.CompanyName != new Branding().CompanyName) return b.CompanyName.Trim();
        }
        catch { }
        return "remote support";
    }

    private void SetRemotePrint(RemotePrintMsg m)
    {
        if (m.On)
        {
            var name = Printing.Add(m.Technician, ProductName(), out var error);
            _ws?.Send(new RemotePrintStateMsg { Technician = m.Technician, On = name != null, Printer = name ?? "", Error = error });
        }
        else
        {
            _printing?.Remove(m.Technician);
            _ws?.Send(new RemotePrintStateMsg { Technician = m.Technician, On = false });
        }
    }

    /// <summary>
    /// A technician's file on the person's default printer. Only from the print folder — the host never
    /// prints a file a technician merely names — and by a helper running AS the person, whose printers
    /// and default printer they are.
    /// </summary>
    private void PrintTheirFile(PrintFileMsg m)
    {
        var name = m.Name.Length > 0 ? m.Name : Path.GetFileName(m.Path);
        void Answer(bool ok, string printer, int pages, string error)
        {
            _ws?.Send(new PrintFileResultMsg { JobId = m.JobId, Ok = ok, Printer = printer, Pages = pages, Error = error });
            ReportPrint("print-in", name, printer, pages, ok ? null : error);
        }
        if (!HostPaths.IsPrintStaging(m.Path)) { Answer(false, "", 0, "that file is not waiting to be printed"); return; }
        var folder = Path.GetDirectoryName(Path.GetFullPath(m.Path))!;
        var answerAt = Path.Combine(folder, "answer.json");
        try
        {
            if (!UserProcess.RunAsInteractiveUser(Environment.ProcessPath!, $"--print-file \"{m.Path}\" \"{answerAt}\"", 180000, out _, out var why))
            { Answer(false, "", 0, "nobody is logged on to print it as — " + why); return; }
            RemoteSupport.Media.DocumentPrinter.Result? r = null;
            try { r = Json.Deserialize<RemoteSupport.Media.DocumentPrinter.Result>(File.ReadAllText(answerAt)); } catch { }
            if (r == null) Answer(false, "", 0, "the printing did not say how it went");
            else Answer(r.Ok, r.Printer, r.Pages, r.Error);
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }

    /// <summary>A print on the ticket and in the person's tray app, like a file moving (FileActivityMsg).</summary>
    private void ReportPrint(string kind, string document, string printer, int pages, string? error)
    {
        try
        {
            _ws?.Send(new FileActivityMsg { Kind = kind, FromMachine = kind == "print-out", Label = document, Printer = printer, FileCount = pages, Error = error });
            HostTrace.Write($"activity: {kind} \"{document}\"{(printer.Length > 0 ? " on " + printer : "")}{(error != null ? " — " + error : "")}");
        }
        catch { }
    }

    /// <summary>The session is over: say what the ledger holds while the server can still hear it, then go.</summary>
    private void OnSessionEnded()
    {
        // Their screen first: the session is over the moment they are told so.
        _privacy.Hide();
        _notice.Hide();
        // Their microphone next: nothing of theirs stays open past the end of the session.
        _voice.End();
        // And no technician's printer stays on their machine.
        _printing?.RemoveAll();
        // A copy straight to or from another machine stops with the session, and a half-written one is removed.
        EndAllBridges("the session ended");
        _clipLedger?.Flush();
        HostTrace.Write("session ended by the server; ledger flushed, closing");
        // CloseAsync lets the send queue drain first, so the line just flushed goes before the close.
        _closing ??= _ws?.CloseAsync("ended");
    }
    private Task? _closing;

    private void ReportClipText(bool fromMachine, int times, long chars, string? error)
    {
        try
        {
            _ws?.Send(new FileActivityMsg
            {
                Kind = "clip-text",
                FromMachine = fromMachine,
                Label = "clipboard text",
                FileCount = 0,
                Bytes = chars,
                Times = times,
                Error = string.IsNullOrWhiteSpace(error) ? null : error
            });
        }
        catch { }
    }

    private void OnBinary(ReadOnlyMemory<byte> frame)
    {
        if (!_consented || frame.Length < 2) return;
        if (frame.Span[0] == BinKind.Voice) { _voice.Frame(frame); return; }
        // Files straight from or to another machine the technician has open (HostEngine.Bridge.cs).
        if (frame.Span[0] == BinKind.Bridge) { OnBridgeFrame(frame); return; }
        if (frame.Span[0] == BinKind.FileChunk)
        {
            var (id, off, data) = Bin.ParseFileChunk(frame);

            // A file set reuses this same frame with an id of "<setId>#<entry index>", so the
            // binary format did not have to change to carry whole trees.
            int hash = id.IndexOf('#');
            if (hash > 0)
            {
                var setId = id[..hash];
                if (!int.TryParse(id[(hash + 1)..], out var index)) return;
                FileSetReceiver? rx;
                lock (_setLock) _incomingSets.TryGetValue(setId, out rx);
                if (rx == null) return;
                if (rx.Chunk(index, off, data))
                {
                    lock (_setLock) _incomingSets.Remove(setId);
                    var msg = rx.Done();
                    _ws?.Send(msg);
                    HostTrace.Write($"fileset {setId}: finished into {msg.SavedDir}, " +
                                    $"{rx.FilesDone} file(s), {rx.Skipped.Count} skipped");
                    ReportFileActivity(false, msg.SavedDir ?? "", rx.FilesDone, rx.TotalBytes,
                                       rx.Skipped.Count > 0 ? $"{rx.Skipped.Count} item(s) could not be written" : null);
                }
                return;
            }

            var done = _files.Chunk(id, off, data);
            if (done != null) _ws?.Send(done);
        }
    }

    /// <summary>
    /// Sends a whole tree to the technician: plan it, offer it, wait to be accepted, then stream.
    ///
    /// The wait matters. Streaming straight after the offer would push chunks at an end that has
    /// not created its receiver yet, and every one of them would be dropped in silence.
    /// </summary>
    private async Task SendFileSetAsync(FileSetPullMsg req)
    {
        try
        {
            // Resolved here for the same reason as everywhere else: %DESKTOP% in a pull used to
            // arrive at FileSetSender.Plan as a relative path and read nothing at all.
            var plan = FileSetSender.Plan(HostPaths.ResolveAll(req.Paths));
            if (plan.Entries.Count == 0)
            {
                _ws?.Send(new FileSetDoneMsg { SetId = req.SetId, Ok = false, Error = "nothing could be read from the customer's machine", Skipped = plan.Skipped });
                ReportFileActivity(true, string.Join(", ", req.Paths.Take(3).Select(Path.GetFileName)), 0, 0,
                                   "nothing could be read");
                return;
            }

            var waiter = new TaskCompletionSource<FileSetAcceptMsg>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_setLock) _pullWaiters[req.SetId] = waiter;

            _ws?.Send(new FileSetOfferMsg
            {
                SetId = req.SetId, Entries = plan.Entries, TotalBytes = plan.TotalBytes, Label = plan.Label
            });
            HostTrace.Write($"fileset {req.SetId}: offered {plan.Entries.Count} entries, {FileSetLimits.Human(plan.TotalBytes)}");

            var accepted = await Task.WhenAny(waiter.Task, Task.Delay(30000)) == waiter.Task ? waiter.Task.Result : null;
            lock (_setLock) _pullWaiters.Remove(req.SetId);
            if (accepted == null || !accepted.Accept)
            {
                HostTrace.Write($"fileset {req.SetId}: not accepted — {accepted?.Error ?? "no answer in 30s"}");
                return;
            }

            var last = DateTime.UtcNow;
            await FileSetSender.SendAsync(req.SetId, plan, b => _ws?.SendBinary(b) ?? false, (sent, files, current) =>
            {
                // Progress often enough to look alive, rarely enough not to flood the socket with
                // messages that are more expensive than the data they describe.
                if ((DateTime.UtcNow - last).TotalMilliseconds < 400) return;
                last = DateTime.UtcNow;
                _ws?.Send(new FileSetProgressMsg
                {
                    SetId = req.SetId, BytesDone = sent, TotalBytes = plan.TotalBytes,
                    FilesDone = files, FilesTotal = plan.FileCount, Current = current
                });
            }, CancellationToken.None);

            // The receiver decides when it is done, by counting bytes. This only reports what
            // could not be read at this end.
            if (plan.Skipped.Count > 0)
                _ws?.Send(new FileSetProgressMsg { SetId = req.SetId, BytesDone = plan.TotalBytes, TotalBytes = plan.TotalBytes, FilesDone = plan.FileCount, FilesTotal = plan.FileCount, Current = $"{plan.Skipped.Count} item(s) could not be read" });
            HostTrace.Write($"fileset {req.SetId}: streamed {FileSetLimits.Human(plan.TotalBytes)}, {plan.Skipped.Count} skipped");
            ReportFileActivity(true, plan.Label, plan.FileCount, plan.TotalBytes,
                               plan.Skipped.Count > 0 ? $"{plan.Skipped.Count} item(s) could not be read" : null);
        }
        catch (Exception ex)
        {
            HostTrace.Write($"fileset {req.SetId}: failed — {ex.GetType().Name}: {ex.Message}");
            _ws?.Send(new FileSetDoneMsg { SetId = req.SetId, Ok = false, Error = ex.Message });
            ReportFileActivity(true, "", 0, 0, ex.Message);
        }
    }

    /// <summary>
    /// Says on the session socket that something actually moved. The hub carries it to the
    /// customer's tray app and writes it on the ticket; this end is the only one that knows what
    /// was really read or written, as opposed to what was asked for.
    ///
    /// Sent when the transfer is over rather than when it starts: a line saying a file was taken
    /// when the read failed is worse than no line, because it is a log somebody will rely on.
    /// </summary>
    private void ReportFileActivity(bool fromMachine, string label, int files, long bytes, string? error, string bridgeId = "")
    {
        try
        {
            _ws?.Send(new FileActivityMsg
            {
                FromMachine = fromMachine,
                Label = string.IsNullOrWhiteSpace(label) ? "(unnamed)" : label,
                FileCount = files, Bytes = bytes, BridgeId = bridgeId,
                Error = string.IsNullOrWhiteSpace(error) ? null : error
            });
            HostTrace.Write($"activity: {(fromMachine ? "taken from" : "put on")} this machine — " +
                            $"{label} ({files} file(s), {FileSetLimits.Human(bytes)}){(error != null ? " — " + error : "")}");
        }
        catch { }
    }

    private static long SizeOf(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    /// <summary>Starts a dedicated foreground thread; desktop attachment cannot survive the thread pool.</summary>
    private static Thread StartLoop(string name, Action body)
    {
        var t = new Thread(() => { try { body(); } catch (Exception ex) { HostTrace.Write($"{name} thread died: {ex.GetType().Name}: {ex.Message}"); } })
        {
            IsBackground = true,
            Name = "rs-" + name
        };
        t.Start();
        return t;
    }

    private void Enqueue(Msg m)
    {
        try { if (!_inputQueue.IsAddingCompleted) _inputQueue.Add(m); } catch { }
    }

    /// <summary>
    /// Injects mouse and keyboard on a thread that follows the user's desktop. Without this the
    /// technician's input is silently swallowed the moment a UAC prompt or the lock screen appears.
    /// </summary>
    private void InputLoop(CancellationToken ct)
    {
        using var desktop = new DesktopFollower(n => HostTrace.Write($"input thread followed the user to the {n} desktop"));
        desktop.Sync(force: true);
        try
        {
            foreach (var m in _inputQueue.GetConsumingEnumerable(ct))
            {
                desktop.Sync();
                switch (m)
                {
                    case MouseMsg mo: _input.Mouse(mo); break;
                    case KeyMsg k: _input.Key(k); break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }   // queue completed
    }

    private void CaptureLoop(CancellationToken ct)
    {
        // Which desktop the capture is attached to is reported to the console, not just traced.
        // "Winlogon" means a UAC prompt, the lock screen or Ctrl+Alt+Del is on screen — and it is
        // the only thing that can tell a capture which has stopped because the screen is still
        // from one which has stopped because it followed the user somewhere it cannot see. Those
        // two are identical in every other measurement.
        using var desktop = new DesktopFollower(n =>
        {
            HostTrace.Write($"capture thread followed the user to the {n} desktop");
            _inputDesktop = n;
            _forceFull = true;
            ReportCapture();
        });
        // The source is chosen HERE, not in the constructor. Desktop Duplication is bound to the
        // desktop of the thread that creates it, and this is the thread that follows the user
        // between Default and Winlogon -- creating it anywhere else would tie the session to
        // whichever desktop happened to be in front when the host started.
        desktop.Sync();
        ChooseSource();
        int refreshCounter = 0;
        // When the last viewer unsubscribed, and whether we have already given the screen back.
        DateTime idleSince = default;
        bool released = false;
        try
        {
        while (!ct.IsCancellationRequested && (_ws?.IsOpen ?? false))
        {
            // Follow the user to whatever desktop currently has input, or we keep sending the
            // last picture of a desktop that is no longer on screen.
            desktop.Sync();
            int[] mons; lock (_subLock) mons = _subscribed.ToArray();
            bool full = _forceFull; _forceFull = false;
            if (mons.Length == 0)
            {
                // Nobody is watching. After a short grace, give the screen back: Desktop
                // Duplication is EXCLUSIVE per output, so holding it locks every other program on
                // the machine out of capturing that screen -- including our own diagnostics, which
                // is how this was found. The grace exists because a technician flicking between
                // monitors unsubscribes one and subscribes another within the same second, and
                // tearing the duplication down and rebuilding it each time would be worse than
                // holding it.
                //
                // The encoder goes first and it is not optional: it was built on the duplication's
                // Direct3D device, so releasing the outputs underneath it would leave it holding a
                // device that no longer exists. Both happen here, on the capture thread, because
                // that is the thread they belong to.
                if (idleSince == default) idleSince = DateTime.UtcNow;
                else if (!released && (DateTime.UtcNow - idleSince) > IdleGrace)
                {
                    released = true;
                    var idleEnc = _encoder; _encoder = null; _encoderMonitor = -1;
                    if (idleEnc != null) { try { idleEnc.Dispose(); } catch { } _codecDirty = true; }
                    try { _capture.ReleaseIdle(); } catch (Exception ex) { HostTrace.Write("capture: releasing while idle failed: " + ex.Message); }
                    _forceFull = true;
                    HostTrace.Write($"capture: nobody subscribed for {IdleGrace.TotalSeconds:0}s; released the duplication and the encoder");
                }
                Thread.Sleep(120);
                continue;
            }
            // Somebody is watching again. A rebuilt duplication has no idea what the viewer holds,
            // so the next frame must be a complete one.
            if (released) { released = false; _forceFull = true; full = true; _codecDirty = true; }
            idleSince = default;

            if (++refreshCounter >= 200)
            {
                refreshCounter = 0; var before = _capture.Monitors.Count; _capture.Refresh();
                if (_capture.Monitors.Count != before) _ws?.Send(new MonitorsMsg { Monitors = _capture.Monitors });
                // A link that cannot carry the picture has to leave a record, or "it went blocky
                // for a minute" is a report nobody can act on. Only when it has happened, and only
                // when the count has moved, so a healthy session writes nothing.
                long dropped = _ws?.DroppedFrames ?? 0;
                if (dropped != _lastDroppedFrames)
                {
                    _lastDroppedFrames = dropped;
                    HostTrace.Write($"link: {dropped} frames refused so far, queue {(_ws?.PendingBytes ?? 0) / 1024} KB deep, peak {(_ws?.PeakBytes ?? 0) / 1024} KB");
                }
                if ((_ws?.DroppedCritical ?? 0) > _lastDroppedCritical)
                {
                    _lastDroppedCritical = _ws!.DroppedCritical;
                    HostTrace.Write($"link: {_lastDroppedCritical} TEXT MESSAGES OR FILE CHUNKS were dropped — that is data loss, not congestion");
                }
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var mon in mons)
            {
                // The codec is chosen here, on the capture thread, for the same reason the capture
                // source is: the encoder shares the duplication's Direct3D device, and a device
                // that follows the user between desktops cannot be handed to an object built on
                // another thread.
                if (_codecDirty || (_encoder == null && _codecRetryAt != default && DateTime.UtcNow >= _codecRetryAt))
                {
                    _codecRetryAt = default;
                    ChooseCodec(mon);
                }
                // Only the monitor the encoder was built for takes the video path. Any other
                // subscribed screen keeps sending tiles, which is a mixture the console handles
                // frame by frame because the kind is in byte zero.
                if (_encoder != null && mon == _encoderMonitor)
                {
                    // A demand for a complete frame that produced nothing has to be put back, for
                    // exactly the reason the tile path does it below -- and the consequence here is
                    // worse, because without a key frame the console has nothing to decode against
                    // and stays black rather than merely stale.
                    if (!SendVideo(mon, full) && full) _forceFull = true;
                    if (!CheckSourceHealth()) { _forceFull = true; break; }
                    continue;
                }

                List<(int x, int y, int w, int h, byte[] jpeg)> tiles;
                try { tiles = _capture.CaptureTiles(mon, _jpeg, full); }
                catch { if (full) _forceFull = true; continue; }
                // A demand for a complete frame must not be spent on a pass that produced none.
                // The pass which reads this flag is the same one that discovers the desktop has
                // changed underneath it, and that pass fails -- so without putting the demand
                // back, the only frame anybody asked for is the one that never went out.
                if (full && tiles.Count == 0) _forceFull = true;
                // A swap to the fallback leaves the viewer holding half a picture from a source
                // that is gone, so demand a complete one on the next pass.
                if (!CheckSourceHealth()) { _forceFull = true; break; }
                if (tiles.Count == 0) continue;
                // The whole of the delta-loss repair is this line. CaptureTiles has ALREADY
                // recorded these tiles as what the far end holds -- the hash is written as the
                // comparison is made -- so a frame that is refused here is a frame the next pass
                // will find no reason to send again, and the far end keeps those rectangles stale
                // until something else happens to change them. Demanding a complete frame is what
                // makes it converge, and it costs one expensive frame rather than a wrong picture.
                if (_ws?.SendBinary(EncodeTiles(mon, tiles)) == false) _forceFull = true;
            }
            // Stage 2.3. One pass' worth of evidence, after the frames for this pass have been
            // offered to the socket -- so the depth being read is the depth this pass produced,
            // not the one before it.
            var t = _capture.LastTiming;
            // A sharpening frame is the same still screen again, not a change: counted as one, every
            // still screen would put the controller on the video ladder for two seconds (§142).
            double changed = _passRefined ? 0 : t.TilesTotal > 0 ? (double)t.TilesChanged / t.TilesTotal : 0;
            _passRefined = false;
            if (_link.Update(_ws?.PendingBytes ?? 0, _ws?.DroppedFrames ?? 0, changed, DateTime.UtcNow))
                ApplyLink(traceIt: true);

            int budget = 1000 / Math.Max(1, _fps);
            int wait = budget - (int)sw.ElapsedMilliseconds;
            if (wait > 0) Thread.Sleep(wait);
        }
        }
        finally
        {
            // On the capture thread, not from RunAsync. A hardware encoder holds a reservation on
            // the graphics card until it is told to let go, and letting it go from another thread
            // is the sort of thing that works on the machine it was written on.
            var enc = _encoder; _encoder = null;
            try { enc?.Dispose(); } catch { }
        }
    }

    // ---- stage 2.2: choosing and driving the codec -----------------------------------------------

    /// <summary>
    /// Settles on a codec for this monitor, and says in the console's Info tab which one and why.
    ///
    /// Called from the capture thread whenever the viewer subscribes or moves the quality slider.
    /// Everything about the H.264 path can refuse -- an old console, the GDI fallback, a machine
    /// with no encoder, a Direct3D device without video support, an encoder another application is
    /// already using -- and every one of those refusals ends the same way: JPEG tiles, and a
    /// sentence a technician can read.
    /// </summary>
    private void ChooseCodec(int monitor)
    {
        _codecDirty = false;
        string[] want; lock (_subLock) want = _viewerCodecs.ToArray();

        var old = _encoder; _encoder = null; _encoderMonitor = -1;
        try { old?.Dispose(); } catch { }

        if (!want.Any(c => c.Equals(VideoCodec.H264, StringComparison.OrdinalIgnoreCase)))
        {
            SetCodec(VideoCodec.JpegTiles, want.Length == 0
                ? "this console is an older build that only understands JPEG tiles"
                : "this console did not offer H.264");
            return;
        }
        if (_capture is not DxgiCapture dx)
        {
            SetCodec(VideoCodec.JpegTiles, $"H.264 encodes the frame on the graphics card and this session is capturing with {_capture.Name}");
            return;
        }
        var mon = _capture.Monitors.FirstOrDefault(m => m.Index == monitor);
        if (mon == null) { SetCodec(VideoCodec.JpegTiles, $"monitor {monitor} is no longer there"); return; }

        try
        {
            // Get a frame onto the card BEFORE building an encoder around its device. Without this
            // the first thing the encoder meets is a texture that does not exist yet, and Media
            // Foundation reports that as an argument error from somewhere deep inside itself,
            // which is a long way from the cause.
            if (!dx.TryAcquireGpuFrame(monitor, true, out var probe) || probe.Device == IntPtr.Zero)
                throw new NotSupportedException(dx.LastError.Length > 0 ? dx.LastError : "no frame could be taken from the graphics card");

            var enc = new RemoteSupport.Media.H264Encoder(probe.Device, mon.Width, mon.Height, _fps, _jpeg);
            _encoder = enc; _encoderMonitor = monitor; _encoderDevice = probe.Device;
            _codecRetriesLeft = 0;
            SetCodec(VideoCodec.H264, "");
            // SetCodec says nothing when the codec's NAME has not changed, and a rebuild is H.264
            // to H.264 — but the report sent while there was no encoder said "JPEG tiles", and the
            // Info tab went on saying so after every UAC prompt. Measured by sysactioncheck
            // --observe, 2026-09-29, against a trace showing H.264 the whole time.
            ReportCapture();
            HostTrace.Write($"codec: H.264 via {enc.Name}, {enc.Width}x{enc.Height} at {enc.Bitrate / 1000} kbit/s" +
                            $"{(enc.DirectBgra ? ", taking the frame as BGRA" : "")}");
        }
        catch (Exception ex)
        {
            SetCodec(VideoCodec.JpegTiles, ex.Message);
            // After a rebuild the refusal is usually a moment's timing, not a verdict: the encoder
            // is rebuilt on the pass after the duplication broke, which can be before this thread
            // has followed the user onto the new desktop, and Windows refuses a duplication asked
            // for from the desktop that has just gone. Measured on DESKTOP-2N6FVGT, 2026-09-29:
            // without this, two sessions in three spent the rest of their life on tiles after one
            // Ctrl+Alt+Del. A few tries a moment apart, then it is a verdict.
            if (_codecRetriesLeft > 0)
            {
                _codecRetriesLeft--;
                _codecRetryAt = DateTime.UtcNow.AddMilliseconds(750);
                HostTrace.Write($"codec: falling back to JPEG tiles for now - {ex.Message}; trying H.264 again shortly ({_codecRetriesLeft} more)");
            }
            else HostTrace.Write($"codec: falling back to JPEG tiles - {ex.Message}");
        }
        _forceFull = true;
    }

    /// <summary>Tries left to get H.264 back after a rebuild, and when the next one is due. See ChooseCodec.</summary>
    private int _codecRetriesLeft;
    private DateTime _codecRetryAt;

    /// <summary>
    /// One pass of the H.264 path. Returns whether a demand for a whole picture was actually met.
    ///
    /// That is deliberately not "did anything go out". An encoder can answer a forced key frame
    /// with a predicted one, and a console with no picture cannot decode a predicted frame -- so
    /// treating "a packet was sent" as success is how a technician ends up looking at black while
    /// the byte counters climb.
    /// </summary>
    private bool SendVideo(int monitor, bool forceKey)
    {
        if (_encoder == null) return false;
        // A monitor the encoder was not built for falls through to tiles for that monitor rather
        // than rebuilding. Rebuilding here would thrash: with two screens subscribed, every pass
        // would tear down the encoder built for the first and build one for the second, and the
        // pass after that would do it the other way round, forever. A genuine change of monitor
        // arrives as a SubscribeMsg, which is what marks the codec dirty.
        if (monitor != _encoderMonitor || _capture is not DxgiCapture dx) return false;
        bool refining = false;
        if (dx.TryAcquireGpuFrame(monitor, forceKey, out var frame))
            _refine.Saw(dx.LastVideoChange, forceKey, DateTime.UtcNow);
        else
        {
            // Nothing changed. Encode the still screen again for a moment after a change, so the
            // constant bit rate spends its budget sharpening the picture the technician is looking
            // at (StillRefiner, HANDOVER §142) — but never into a queue that is already backing up.
            if (forceKey || !_refine.Wanted(DateTime.UtcNow) || (_ws?.PendingBytes ?? 0) > RefineQueueLimit) return false;
            if (!dx.TryAcquireGpuFrame(monitor, true, out frame)) return false;
            refining = true;
        }

        // An encoder takes textures from the ONE device it was built on, and only that one. A
        // rebuilt duplication comes with a new device, and a hardware encoder handed a texture from
        // another device does not throw -- it takes the process down. Measured on DESKTOP-2N6FVGT,
        // 2026-09-29: three crashes of the host (0xc0000409 in ntdll, Control Flow Guard), each two
        // to four seconds after a switch to the Winlogon desktop, on the only machine here encoding
        // with Quick Sync. That is Ctrl+Alt+Del, UAC and the lock screen. CheckSourceHealth drops the
        // encoder when it sees a rebuild; this is the guard for any path that does not go through it.
        // The old device is kept alive by the encoder's own reference, so its address cannot be
        // reused while this comparison can be made.
        if (frame.Device != _encoderDevice)
        {
            DropEncoder("the capture's graphics device changed under it");
            return false;
        }

        List<RemoteSupport.Media.VideoPacket> packets;
        try { packets = _encoder.Encode(frame.Texture, forceKey); }
        catch (Exception ex)
        {
            // An encoder that has started failing does not recover, and a session with no picture
            // is worse than a session with a soft one. Same reasoning as the GDI fallback.
            HostTrace.Write($"codec: the encoder failed mid-session ({ex.Message}); falling back to JPEG tiles");
            SetCodec(VideoCodec.JpegTiles, ex.Message);
            var dead = _encoder; _encoder = null;
            try { dead.Dispose(); } catch { }
            _forceFull = true;
            return false;
        }

        // A refused packet is worse here than on the tile path: H.264 is a chain, and a frame the
        // far end never receives makes every frame after it undecodable until the next key frame.
        // So a refusal does not just cost that picture, it costs all of them -- demand a key frame
        // and let the loop send one.
        bool refused = false;
        foreach (var p in packets)
            if (_ws?.SendBinary(Bin.Video(monitor, p.KeyFrame, _encoder.Width, _encoder.Height, p.Data)) == false) refused = true;
        if (refining) { _passRefined = true; _refinedFrames++; }
        if (refused) { _forceFull = true; return false; }
        return !forceKey || packets.Any(p => p.KeyFrame);
    }

    /// <summary>Still-screen sharpening. See <see cref="StillRefiner"/>.</summary>
    private readonly StillRefiner _refine = new();
    /// <summary>True when this pass's only video frame was sharpening, which the link controller must not count as a change.</summary>
    private bool _passRefined;
    private long _refinedFrames;
    /// <summary>Sharpening stops while this much is waiting to go: a queue is the link saying it is full, a
    /// quarter of the depth at which the controller starts backing off.</summary>
    private const long RefineQueueLimit = LinkController.BackOffBytes / 4;
    /// <summary>How often the server has asked for a complete picture because one of the frames it was
    /// passing to a viewer had to be dropped. See <see cref="KeyFrameRequestMsg"/>.</summary>
    private long _keyRequests;

    /// <summary>The Direct3D device the current encoder was built on. See SendVideo.</summary>
    private IntPtr _encoderDevice;

    /// <summary>
    /// Disposes the encoder and has the next pass build a fresh one on whatever device the capture
    /// has now. On the capture thread only, like every other encoder lifetime change.
    /// </summary>
    private void DropEncoder(string why)
    {
        var old = _encoder;
        if (old == null) return;
        _encoder = null; _encoderMonitor = -1; _encoderDevice = IntPtr.Zero;
        try { old.Dispose(); } catch { }
        _codecDirty = true;
        _codecRetriesLeft = 5;
        _forceFull = true;
        HostTrace.Write($"codec: rebuilding the encoder - {why}");
    }

    private void SetCodec(string codec, string why)
    {
        if (_codec == codec && _codecWhy == why) return;
        _codec = codec; _codecWhy = why;
        ReportCapture();
    }

    private int _dxgiFaults;
    private string _captureFellBack = "";
    private volatile string _inputDesktop = "";
    // Starts at 0, not -1. Starting below zero made the first health check see 0 != -1 and write
    // "duplication rebuilt (0 so far)" into the trace of every session, with an empty reason,
    // before anything had been rebuilt. A log that reports events which did not happen is worse
    // than one that reports nothing.
    private int _reportedRebuilds;

    /// <summary>
    /// Re-sends the system info so the console's Info tab shows the capture path as it is now,
    /// not as it was when the socket opened. The tab clears and refills, so this is idempotent.
    /// </summary>
    private void ReportCapture()
    {
        try { _ws?.Send(new SysInfoMsg { Info = CollectSysInfo() }); } catch { }
    }

    /// <summary>
    /// Takes what the controller has decided and puts it into effect.
    ///
    /// The bitrate moves LIVE through ICodecAPI rather than by rebuilding the encoder. If this
    /// encoder will not take it live, the bitrate simply stays where it is rather than triggering
    /// a rebuild: on the tile path the quality change is enough on its own, and on the H.264 path
    /// a rebuild per nudge would cost more than the adaptation saves. The frame rate needs nothing
    /// from the encoder at all -- the capture loop paces itself.
    /// </summary>
    private void ApplyLink(bool traceIt)
    {
        _jpeg = _link.Quality;
        _fps = Math.Clamp(_link.Fps, 1, 30);
        var enc = _encoder;
        if (enc != null)
        {
            try { enc.TrySetBitrate(RemoteSupport.Media.H264Encoder.BitrateFor(_jpeg, enc.Width, enc.Height, enc.Fps)); }
            catch { }
        }
        var why = _link.Describe();
        if (why != _linkWhy)
        {
            _linkWhy = why;
            if (traceIt) HostTrace.Write("link: " + why);
            ReportCapture();
        }
    }

    /// <summary>
    /// Tries Desktop Duplication and keeps it if it works. Called once, from the capture thread.
    /// </summary>
    private void ChooseSource()
    {
        try
        {
            var fast = new DxgiCapture();
            var old = _capture;
            _capture = fast;
            (old as IDisposable)?.Dispose();
            HostTrace.Write($"capture: using {fast.Name}");
        }
        catch (Exception ex)
        {
            // Not a failure worth alarming anyone about -- it is the documented state of some
            // adapters, some VMs, and any machine where another program already has the screen
            // duplicated. Say which, though, because "it is slow" is otherwise unexplainable.
            _captureFellBack = ex.Message;
            HostTrace.Write($"capture: falling back to {_capture.Name} - {ex.Message}");
        }
        ReportCapture();
    }

    /// <summary>
    /// Watches the fast path for repeated real failures and gives up on it if it keeps having
    /// them. Returns false on the pass where the swap happened.
    ///
    /// The threshold is not one. ACCESS_LOST is routine -- every UAC prompt and every resolution
    /// change costs one, and the capturer rebuilds itself -- so abandoning the fast path at the
    /// first sign of trouble would mean running the slow one for the rest of any session in which
    /// the customer touched a prompt.
    /// </summary>
    private bool CheckSourceHealth()
    {
        if (_capture is not DxgiCapture d) return true;
        // A rebuild is the visible trace of the user hitting a UAC prompt or changing resolution.
        // Surfacing it costs one message per event and turns "it went odd for a second" into
        // something with a name.
        if (d.Rebuilds != _reportedRebuilds)
        {
            _reportedRebuilds = d.Rebuilds;
            HostTrace.Write($"capture: duplication rebuilt ({d.Rebuilds} so far) - {d.LastError}");
            // The encoder goes, not just its frames. It was built on the duplication's Direct3D
            // device, and a rebuilt duplication is a NEW device (DxgiCapture.Create) -- the same
            // reason the idle release above disposes it first. Flushing it and carrying on is what
            // this used to do, and on a hardware encoder the next frame crashed the host: see
            // SendVideo. Rebuilding it also throws away the frames of the desktop that has gone,
            // which is what the flush was for.
            DropEncoder($"the duplication was rebuilt ({d.LastError})");
            _forceFull = true;
            ReportCapture();
        }
        if (d.LastError.Length == 0) { _dxgiFaults = 0; return true; }
        if (++_dxgiFaults < 30) return true;

        HostTrace.Write($"capture: giving up on {d.Name} after {_dxgiFaults} consecutive faults ({d.LastError}); falling back to GDI BitBlt");
        _captureFellBack = $"{_dxgiFaults} consecutive faults - {d.LastError}";
        _capture = new ScreenCapture();
        _capture.Refresh();
        d.Dispose();
        _dxgiFaults = 0;
        ReportCapture();
        return false;
    }

    // Shapes already sent to this viewer, by cursor handle. Windows keeps a handle for the life of
    // a shape and reuses a small set of them, so this stays tiny -- and it is what turns "send the
    // real pointer" into one bitmap per shape rather than one per poll.
    private readonly HashSet<long> _cursorsSent = new();
    /// <summary>Set when a viewer subscribes: the pointer must introduce itself again, because a
    /// new or reconnected viewer knows nothing about it. See CursorLoop.</summary>
    private volatile bool _cursorResend = true;

    /// <summary>
    /// Carries the customer's pointer: where it is, whether it is shown, and what it looks like.
    ///
    /// Stage 2.4. It runs on its own desktop-following thread rather than inside the capture loop
    /// for a reason that matters more now than it did: **the pointer moves when the screen does
    /// not.** Desktop Duplication hands over nothing at all for a mouse move, and with 2.3 in place
    /// the capture loop may be pacing itself down to a few frames a second on a bad link — but a
    /// cursor that keeps moving is most of what makes a slow picture feel alive rather than broken.
    /// This loop is unaffected by the frame rate and costs a few dozen bytes a message.
    /// </summary>
    private void CursorLoop(CancellationToken ct)
    {
        using var desktop = new DesktopFollower();
        long lastShape = -1; bool lastVisible = true; int lastX = int.MinValue, lastY = int.MinValue;
        var lastSent = DateTime.MinValue;
        // One line each, the first time each thing happens or fails. The cursor path traced NOTHING
        // until 2.4 was tested on a machine and sent no pointer at all -- at which point there was
        // no way to tell "the poll failed" from "the poll worked and the monitor lookup missed"
        // from "the shape would not render", and all three look identical from the console: a
        // yellow ring, exactly as before. Silence is the one thing a diagnostic must not do.
        bool saidPolling = false, saidPollFail = false, saidNoMonitor = false, saidShape = false, saidNoShape = false;
        while (!ct.IsCancellationRequested && (_ws?.IsOpen ?? false))
        {
            desktop.Sync();
            if (!CursorShape.TryPoll(out long id, out bool visible, out var p))
            {
                if (!saidPollFail) { saidPollFail = true; HostTrace.Write("cursor: " + CursorShape.LastPollError); }
            }
            else
            {
                if (!saidPolling)
                {
                    saidPolling = true;
                    HostTrace.Write($"cursor: polling works — shape {id:X}, visible {visible}, at {p.X},{p.Y}; " +
                                    $"{_capture.Monitors.Count} monitor(s) known");
                }
                // The bitmap first, so the console never has an id it cannot draw. It is sent once
                // per shape; a refused frame simply means the id is not registered yet and the
                // console keeps its ring until the next time this shape comes round.
                if (visible && id != 0 && !_cursorsSent.Contains(id))
                {
                    var img = CursorShape.TryRender(id);
                    if (img == null)
                    {
                        if (!saidNoShape) { saidNoShape = true; HostTrace.Write($"cursor: shape {id:X} would not render — {CursorShape.Describe(id)}"); }
                    }
                    else if (_ws?.SendBinary(EncodeCursor(img)) == true)
                    {
                        _cursorsSent.Add(id);
                        if (!saidShape) { saidShape = true; HostTrace.Write($"cursor: sent shape {id:X}, {img.Width}x{img.Height}, hot {img.HotX},{img.HotY}"); }
                    }
                }

                // A viewer that has just subscribed knows nothing about the pointer, and a viewer
                // that RECONNECTED has forgotten what it knew -- so start again for both.
                if (_cursorResend)
                {
                    _cursorResend = false;
                    _cursorsSent.Clear();
                    lastShape = -1; lastX = int.MinValue; lastY = int.MinValue;
                    continue;   // re-poll, so the shape is re-sent before the position
                }

                var mon = _capture.Monitors.FirstOrDefault(m => p.X >= m.X && p.X < m.X + m.Width && p.Y >= m.Y && p.Y < m.Y + m.Height);
                if (mon == null && !saidNoMonitor)
                {
                    saidNoMonitor = true;
                    HostTrace.Write($"cursor: {p.X},{p.Y} is on none of the {_capture.Monitors.Count} known monitor(s) — " +
                                    string.Join("; ", _capture.Monitors.Select(m => $"{m.Index}:{m.Width}x{m.Height}@{m.X},{m.Y}")));
                }
                if (mon != null)
                {
                    int x = p.X - mon.X, y = p.Y - mon.Y;
                    // Mostly when something changed -- a still pointer on an idle machine should
                    // cost nothing, for the same reason an idle screen does -- but ALSO at least
                    // once a second regardless.
                    //
                    // That heartbeat is not belt and braces, it is the fix for a real fault this
                    // found on hardware. Nobody was at the machine, so the pointer never moved, so
                    // change-detection alone sent exactly ONE message: 0.2 s after the host
                    // started, before the viewer had finished attaching. It was lost and nothing
                    // ever sent another, and the console sat there drawing its ring at 0,0 with
                    // no error anywhere. The previous build sent one every 80 ms forever, which is
                    // wasteful and is why it never hit this.
                    //
                    // The same trap is live in production without a test in sight: a technician
                    // whose socket drops and reconnects (stage 1.4) would never see the pointer
                    // again for the rest of the session.
                    bool due = (DateTime.UtcNow - lastSent) > TimeSpan.FromSeconds(1);
                    if (due || x != lastX || y != lastY || visible != lastVisible || id != lastShape)
                    {
                        lastX = x; lastY = y; lastVisible = visible; lastShape = id; lastSent = DateTime.UtcNow;
                        _ws?.Send(new CursorMsg { Monitor = mon.Index, X = x, Y = y, Visible = visible, ShapeId = id });
                    }
                }
            }
            // 40 ms rather than 80. The pointer is the one thing on screen the technician is
            // steering, and at 12 fps a cursor updated twice as often as the picture reads as
            // noticeably smoother than one updated with it.
            Thread.Sleep(40);
        }
    }

    /// <summary>The wire format for a pointer bitmap. Public so `codecbench --cursor` can prove
    /// the round trip against the real encoder rather than against a copy of it.</summary>
    public static byte[] EncodeCursor(CursorShape.Image img)
    {
        var buf = new byte[1 + 8 + 16 + img.Png.Length];
        int o = 0;
        buf[o++] = BinKind.Cursor;
        BitConverter.TryWriteBytes(buf.AsSpan(o), img.Id); o += 8;
        BitConverter.TryWriteBytes(buf.AsSpan(o), img.Width); o += 4;
        BitConverter.TryWriteBytes(buf.AsSpan(o), img.Height); o += 4;
        BitConverter.TryWriteBytes(buf.AsSpan(o), img.HotX); o += 4;
        BitConverter.TryWriteBytes(buf.AsSpan(o), img.HotY); o += 4;
        img.Png.CopyTo(buf.AsSpan(o));
        return buf;
    }

    private static byte[] EncodeTiles(int monitor, List<(int x, int y, int w, int h, byte[] jpeg)> tiles)
    {
        int size = 1 + 4 + 4;
        foreach (var t in tiles) size += 20 + t.jpeg.Length;
        var buf = new byte[size];
        int o = 0;
        buf[o++] = BinKind.Tiles;
        BitConverter.TryWriteBytes(buf.AsSpan(o), monitor); o += 4;
        BitConverter.TryWriteBytes(buf.AsSpan(o), tiles.Count); o += 4;
        foreach (var t in tiles)
        {
            BitConverter.TryWriteBytes(buf.AsSpan(o), t.x); o += 4;
            BitConverter.TryWriteBytes(buf.AsSpan(o), t.y); o += 4;
            BitConverter.TryWriteBytes(buf.AsSpan(o), t.w); o += 4;
            BitConverter.TryWriteBytes(buf.AsSpan(o), t.h); o += 4;
            BitConverter.TryWriteBytes(buf.AsSpan(o), t.jpeg.Length); o += 4;
            t.jpeg.CopyTo(buf.AsSpan(o)); o += t.jpeg.Length;
        }
        return buf;
    }

    private Dictionary<string, string> CollectSysInfo()
    {
        var d = new Dictionary<string, string>
        {
            // Which capture path is running, in the product's own Info tab rather than only in a
            // log file on the customer's machine. A session that feels slow and a session that is
            // slow because duplication was refused look identical from the technician's chair,
            // and this is the one line that tells them apart without a remote log fetch.
            ["Screen capture"] = _capture.Name,
            // Which codec, in the product's own Info tab. A session on JPEG tiles and a session on
            // H.264 differ by a factor of ten in bandwidth and look the same from the chair, and
            // "the picture is soft" is the complaint for both — one because the slider is low, the
            // other because the encoder was refused and nobody was told.
            ["Video codec"] = _codec == VideoCodec.H264 && _encoder != null
                ? $"H.264 — {_encoder.Name} at {_encoder.Bitrate / 1000} kbit/s, {_encoder.Width}x{_encoder.Height}"
                : "JPEG tiles (one picture per changed tile, no knowledge of the frame before)",
            // Stage 2.3. A quality number that moves on its own, with no explanation, reads as a
            // fault -- and "the picture is soft because the slider is low" and "because the link
            // cannot carry it" are the same complaint from the chair. This is the line that tells
            // them apart, and it is why the controller is not allowed to be silent.
            ["Link"] = _link.Describe(),
            ["Input desktop"] = _inputDesktop.Length > 0 ? _inputDesktop : "not yet attached",
            ["Machine"] = Environment.MachineName,
            // The host usually runs as SYSTEM, so report who is actually at the keyboard, and say
            // separately what the host itself is running as — that second line is the one that
            // explains a dead mouse and keyboard.
            ["User"] = UserContext.InteractiveUser,
            // Internal testing, 2026-10-02: "the logged-on user" was all this said, in a window
            // nobody could read, and it was the answer to a black UAC prompt, a Task Manager that
            // would not open and a DISM that said "elevation required". So it says what it means.
            ["Host running as"] = UserContext.IsSystem
                ? "SYSTEM, through the background service (can see UAC prompts and the lock screen, and run as administrator)"
                : "the logged-on user, through the support app — UAC prompts show black, elevated windows cannot be controlled, "
                  + "and scripts are not run as administrator. The background service was not available for this session.",
            ["OS"] = Environment.OSVersion.VersionString,
            ["CPUs"] = Environment.ProcessorCount.ToString(),
            ["64-bit OS"] = Environment.Is64BitOperatingSystem.ToString(),
            // Read by the hub and the console: this host can take part in a copy straight from or to another machine.
            [HostCan.BridgeKey] = HostCan.Yes,
        };
        // Rebuilds are only interesting when there are some. A handful over a session is normal —
        // every UAC prompt costs one — so the line appearing at all is not a fault; a number that
        // climbs steadily is.
        if (_capture is DxgiCapture dx && dx.Rebuilds > 0)
            d["Capture rebuilds"] = $"{dx.Rebuilds} (a UAC prompt or a resolution change costs one)";
        if (_captureFellBack.Length > 0)
            d["Capture fell back because"] = _captureFellBack;
        if (_codec != VideoCodec.H264 && _codecWhy.Length > 0)
            d["Video codec fell back because"] = _codecWhy;
        // Not a fault on its own: an asynchronous encoder refusing a frame because it is still
        // busy with the last one is the pipeline working. A number that keeps pace with the frame
        // rate means the machine cannot encode as fast as it can capture.
        if (_encoder is { Dropped: > 0 } enc2)
            d["Frames the encoder was too busy for"] = enc2.Dropped.ToString();
        // Snapshots, like the line above: the Info tab is refreshed when the codec or the link changes.
        if (_refinedFrames > 0) d["Frames sent to sharpen a still screen"] = _refinedFrames.ToString();
        if (_keyRequests > 0) d["Complete pictures the server asked for"] = _keyRequests.ToString();
        try { d["Working set (MB)"] = (Environment.WorkingSet / 1048576).ToString(); } catch { }
        return d;
    }

    /// <summary>
    /// Reboot, lock, or Ctrl+Alt+Del. It answers with a SysActionResultMsg every time, success or
    /// failure: a reboot Windows refused looks exactly like a reboot that is about to happen, and
    /// the technician's window would sit waiting five minutes for a machine that was never going
    /// anywhere. The old `catch {}` here made every failure look like a success.
    /// </summary>
    private void DoSysAction(string action)
    {
        try
        {
            switch (action)
            {
                case "lock":
                    // LockWorkStation is per-session and fails from session 0. This host is put
                    // into the interactive session by SessionLauncher, so it is in the right one.
                    if (!NativeSys.LockWorkStation())
                        throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                    SysResult(action, true, "locked");
                    break;

                case "task_manager":
                {
                    // Launched by the HOST, which is already in the interactive session, so the
                    // window appears on the desktop the technician is looking at rather than
                    // nowhere (a session-0 process starting it would be invisible to everybody).
                    // It inherits the host's SYSTEM token, which is what makes it able to show
                    // every user's processes and end an elevated one — the reason a technician
                    // wants it in the first place.
                    System.Diagnostics.Process? tm;
                    try { tm = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr.exe") { UseShellExecute = false }); }
                    catch (System.ComponentModel.Win32Exception w) when (w.NativeErrorCode == 740 && !UserContext.IsSystem)
                    {
                        // Internal testing, 2026-10-02: "does not open Task Manager". Run as the
                        // person, Windows refuses it outright for an administrator's account, and
                        // the old message ("The requested operation requires elevation") read
                        // like a fault in the button rather than in how the session is hosted.
                        SysResult(action, false, "Task Manager needs administrator rights, and this session is run by the support app "
                                               + "rather than the background service. Ask the person to open it, or connect through the service.");
                        break;
                    }
                    if (tm == null) throw new InvalidOperationException("taskmgr.exe did not start");
                    SysResult(action, true, "Task Manager opened");
                    break;
                }

                case "reboot":
                    // /t 5 gives the person at the machine five seconds of Windows' own warning
                    // before it goes, which is the only notice they get from us.
                    var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", "/r /t 5")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true });
                    if (p == null) throw new InvalidOperationException("shutdown.exe did not start");
                    var err = p.StandardError.ReadToEnd();
                    p.WaitForExit(8000);
                    if (p.ExitCode != 0) { SysResult(action, false, $"shutdown.exe exit {p.ExitCode}: {err.Trim()}"); break; }
                    SysResult(action, true, "restarting in 5 seconds");
                    break;

                case "ctrl_alt_del":
                    // THE HOST CANNOT DO THIS, and the interesting part is that it fails silently.
                    //
                    // The secure attention sequence cannot be synthesised with SendInput — Windows
                    // reserves it — so this has to be SendSAS. But SendSAS is only accepted from a
                    // **service running as LocalSystem**. This process is LocalSystem, yet it is a
                    // process launched into the interactive session by SessionLauncher, which is
                    // not the same thing. Measured on DESKTOP-OPHQIDH on 2026-09-15 through the
                    // product's own script execution, so with this exact token in this exact
                    // session: SendSAS returns without error for BOTH values of AsUser and the
                    // input desktop never leaves Default. An earlier version of this code called
                    // it here and logged "carried out", which was true and useless.
                    //
                    // The agent is a genuine session-0 service, so the server routes Ctrl+Alt+Del
                    // there instead (TenantHub). This branch is only reached by an older server
                    // that still sends it here, and it now says so rather than lying.
                    SysResult(action, false,
                        "the host cannot send Ctrl+Alt+Del — Windows only accepts it from a service. Update the server so it routes this to the agent.");
                    HostTrace.Write("sys action: ctrl_alt_del arrived at the HOST, which cannot perform it; it belongs to the agent");
                    break;

                default:
                    SysResult(action, false, "unknown action");
                    break;
            }
            HostTrace.Write($"sys action: {action} carried out");
        }
        catch (Exception ex)
        {
            HostTrace.Write($"sys action: {action} FAILED — {ex.GetType().Name}: {ex.Message}");
            SysResult(action, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void SysResult(string action, bool ok, string detail)
    { try { _ws?.Send(new SysActionResultMsg { Action = action, Ok = ok, Detail = detail }); } catch { } }
}

/// <summary>
/// The one system call here that has no managed equivalent worth the indirection. SendSAS used to
/// live here too and has moved to the agent, because Windows only accepts it from a service — see
/// DoSysAction.
/// </summary>
internal static class NativeSys
{
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static extern bool LockWorkStation();
}
