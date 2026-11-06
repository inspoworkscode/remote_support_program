using System.Net.Http.Json;
using System.Net.WebSockets;
using RemoteSupport.Shared;

namespace RemoteSupport.Technician;

/// <summary>Technician control connection to /ws/tech, plus the REST login call.</summary>
public sealed class TechLink
{
    private readonly string _httpBase;
    private readonly string _wsBase;
    private WsConnection? _ws;
    private readonly object _lock = new();

    public string Token { get; private set; } = "";
    /// <summary>What the server gave after a two-factor code, for TrustedSignIn to keep; "" otherwise.</summary>
    public string Trust { get; private set; } = "";
    public string DisplayName { get; private set; } = "";
    /// <summary>"light", "dark", or empty to follow whatever the company chose. Stage 3.6.9.</summary>
    public string Appearance { get; private set; } = "";
    public string WsBase => _wsBase;
    /// <summary>Every technician in the company, for reassigning tickets.</summary>
    public List<string> Roster { get; private set; } = new();
    /// <summary>Job types (2026-10-27, §168): the ticket categories the company has switched off, by value — New ticket and
    /// Edit leave them out. From the latest state, so a save on the portal reaches an open console.</summary>
    public HashSet<int> CategoriesOff { get; private set; } = new();
    /// <summary>Photos a workshop booking needs now (StateMsg.BookingPhotos): 3, or 0 when the company does not ask.</summary>
    public int BookingPhotos { get; private set; } = 3;
    /// <summary>Whether New ticket and Edit offer this category (by value).</summary>
    public bool CategoryOn(int category) => !CategoriesOff.Contains(category);

    public event Action<StateMsg>? OnState;
    public event Action<CallInfo>? OnRing;
    public event Action<CallInfo>? OnCallUpdate;
    public event Action<DeviceInfo>? OnDeviceUpdate;
    public event Action<SessionInfo>? OnSessionReady;
    public event Action<List<string>>? OnPresence;
    public event Action<ChatMsg>? OnChat;
    public event Action<bool>? OnConnectedChanged;
    public event Action<string>? OnError;
    /// <summary>A connection refused for want of a machine's unattended password, or for a wrong one:
    /// the machine, the server's words, and whether none was given at all. Raised INSTEAD of OnError, so the console can ask again.</summary>
    public event Action<string, string, bool>? OnPasswordRefused;
    /// <summary>A connection sent without a reason, and the machine has no ticket to carry on (2026-10-09):
    /// the console asks why. Raised INSTEAD of OnError.</summary>
    public event Action<string>? OnReasonNeeded;
    public event Action<TicketListMsg>? OnTicketList;
    public event Action<TicketDetail>? OnTicketDetail;
    public event Action<TicketSummary>? OnTicketChanged;
    public event Action<ClientListMsg>? OnClientList;
    public event Action<PortalLinkMsg>? OnPortalLink;
    /// <summary>Tickets with an @mention waiting for this technician's reply.</summary>
    public event Action<int>? OnMentionCount;
    /// <summary>The billing queue — only for the people told when a job lands in it (2026-10-05).</summary>
    public event Action<BillingQueueMsg>? OnBillingQueue;
    /// <summary>Latest activity (2026-10-26, HANDOVER §164): a page of the list, and this person's count of what is new.</summary>
    public event Action<ActivityListMsg>? OnActivityList;
    public event Action<ActivityCountMsg>? OnActivityCount;
    /// <summary>Somebody from another company has connected to one of THIS company's machines with a code (3.9).</summary>
    public event Action<OutsiderVisitMsg>? OnOutsiderVisit;
    /// <summary>A code this technician made in a session (§155): the code, why not, or what has become of it.</summary>
    public event Action<OutsiderInviteMsg>? OnOutsiderInvite;
    /// <summary>Somebody typed this technician's code and waits to be let in (§155).</summary>
    public event Action<OutsiderJoinAskMsg>? OnOutsiderJoinAsk;
    /// <summary>The knowledge base's articles for a machine's client (3.8.5), answering <see cref="RequestKnowledgeCount"/>.</summary>
    public event Action<KnowledgeCountMsg>? OnKnowledgeCount;
    // ---- the workflow (stage 3.7) ----
    public event Action<TicketNoticeMsg>? OnTicketNotice;
    public event Action<RecurringKindsMsg>? OnRecurringKinds;
    /// <summary>A recurring task was set up from New ticket: what to tell the person who did it.</summary>
    public event Action<RecurringSetMsg>? OnRecurringSet;
    public event Action<ItemListMsg>? OnItemList;
    public event Action<QuoteSimilarMsg>? OnQuoteSimilar;
    // ---- office chat (stage 3.8.1) ----
    public event Action<StaffChatMsg>? OnStaffChat;
    public event Action<StaffChatHistoryMsg>? OnStaffChatHistory;
    public event Action<StaffChatOverviewMsg>? OnStaffChatOverview;
    // Website chat (3.8.3): visitors to the company's own website.
    public event Action<WebChatListMsg>? OnWebChatList;
    public event Action<WebChatUpdateMsg>? OnWebChatUpdate;
    public event Action<WebChatLineMsg>? OnWebChatLine;
    public event Action<WebChatHistoryMsg>? OnWebChatHistory;
    /// <summary>Who could take a website chat now, asked when Hand over is pressed (2026-10-15).</summary>
    public event Action<WebChatPeopleMsg>? OnWebChatPeople;
    // Processes and Performance (2026-10-05): a machine's live figures, for the windows this console opened.
    public event Action<WatchSampleMsg>? OnWatchSample;
    public event Action<WatchEndedMsg>? OnWatchEnded;
    /// <summary>What became of a Wake up (2026-10-06): sent from which machine, or why not.</summary>
    public event Action<WakeResultMsg>? OnWakeResult;
    /// <summary>The machines whose unattended password this technician has saved, and whether they may manage other people's (2026-10-06).</summary>
    public event Action<SavedPasswordsMsg>? OnSavedPasswords;
    public event Action<SavedPasswordWhoMsg>? OnSavedPasswordWho;
    /// <summary>A machine left the list: removed (the free version), or the old id of a reinstalled one.</summary>
    public event Action<DeviceGoneMsg>? OnDeviceGone;

    public TechLink(string serverUrl)
    {
        var u = serverUrl.Trim().TrimEnd('/');
        if (u.StartsWith("ws://")) u = "http://" + u["ws://".Length..];
        else if (u.StartsWith("wss://")) u = "https://" + u["wss://".Length..];
        else if (!u.StartsWith("http")) u = "https://" + u;
        _httpBase = u;
        _wsBase = u.StartsWith("https") ? "wss://" + u["https://".Length..] : "ws://" + u["http://".Length..];
    }

    /// <summary>
    /// Where a sign-in has got to (2026-10-01): signed in, refused, or wanting the second step —
    /// the code from their authenticator app, or setting one up (with the QR code to scan).
    /// </summary>
    public sealed class LoginStep
    {
        public bool Ok;
        public string Error = "";
        /// <summary>"", "code" or "enrol".</summary>
        public string Next = "";
        public string Pending = "";
        public byte[] QrPng = Array.Empty<byte>();
        public string SecretGrouped = "";
        /// <summary>
        /// Not an error: why the code is asked on a trusted PC — they are signed in to the console on
        /// another computer, which this sign-in will sign out (2026-10-05).
        /// </summary>
        public string Notice = "";
        /// <summary>Shown once, after setting up an authenticator.</summary>
        public List<string> RecoveryCodes = new();
    }

    /// <summary>The fixed part of the sign-in: the email and password.</summary>
    public async Task<LoginStep> LoginAsync(string username, string password, string? trust = null) =>
        await PostLogin("/api/login", new { username, password, trust, device = TrustedSignIn.DeviceId() });

    /// <summary>
    /// The console is closing — Exit or Sign out (2026-10-05). The server forgets this token, so
    /// signing in on another computer afterwards is not "somewhere else": no code, nobody pushed off.
    /// Best effort and short: a server that cannot be reached is no reason to hold the exit up.
    /// </summary>
    public async Task LogoutAsync()
    {
        if (Token.Length == 0 || SecureTransport.Refusal(_httpBase) != null) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_httpBase}/api/logout");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
            await http.SendAsync(req);
        }
        catch (Exception ex) { Trace.Write("logout: " + ex.Message); }
    }

    /// <summary>The six digits, or a recovery code.</summary>
    public async Task<LoginStep> CodeAsync(string pending, string code) => await PostLogin("/api/login/code", new { pending, code });

    /// <summary>The first code from a newly set-up authenticator.</summary>
    public async Task<LoginStep> EnrolAsync(string pending, string code) => await PostLogin("/api/login/enrol", new { pending, code });

    private async Task<LoginStep> PostLogin(string path, object body)
    {
        // Never across the internet unencrypted — the password is in this request (SecureTransport).
        if (SecureTransport.Refusal(_httpBase) is { } insecure) { Trace.Write("login: " + insecure); return new LoginStep { Error = insecure }; }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var resp = await http.PostAsJsonAsync($"{_httpBase}{path}", body, Json.Options);
            var json = await resp.Content.ReadAsStringAsync();
            System.Text.Json.JsonElement doc;
            try { doc = System.Text.Json.JsonDocument.Parse(json).RootElement; }
            catch { return new LoginStep { Error = $"Login failed ({(int)resp.StatusCode})." }; }
            string S(string name) => doc.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";

            if (!resp.IsSuccessStatusCode)
            {
                var step = new LoginStep { Next = S("step"), Pending = S("pending"), SecretGrouped = S("secretGrouped"), Notice = S("notice") };
                // An older console shows `error` ("update your console"); this one acts on `step`, and
                // shows `wrong` — why the last code was refused — when there is one.
                step.Error = step.Next.Length > 0 ? S("wrong") : (S("error") is { Length: > 0 } e ? e : "Login failed.");
                var qr = S("qrPng");
                if (qr.Length > 0) try { step.QrPng = Convert.FromBase64String(qr); } catch { }
                return step;
            }

            Token = S("token"); DisplayName = S("name");
            // After a code: kept and sent with the next sign-in, so the code is asked once a week (2026-10-02).
            Trust = S("trust");
            // Their own light or dark, chosen in the portal and stored against their account, so it
            // follows them to whichever machine they sign in at. Empty means follow the company.
            Appearance = S("appearance").Trim().ToLowerInvariant();
            var done = new LoginStep { Ok = Token.Length > 0 };
            if (doc.TryGetProperty("recoveryCodes", out var rc) && rc.ValueKind == System.Text.Json.JsonValueKind.Array)
                done.RecoveryCodes = rc.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
            // Which server, and as whom. A console pointed at the wrong server by its branding
            // logs in perfectly well and then shows an empty dashboard for ever.
            Trace.Write($"login: OK as {DisplayName} against {_httpBase}");
            return done;
        }
        catch (Exception ex) { Trace.Write($"login: FAILED against {_httpBase} — {ex.GetType().Name}: {ex.Message}"); return new LoginStep { Error = ex.Message }; }
    }

    /// <summary>
    /// The server has signed this console out (SignedOutMsg), or will no longer accept its token
    /// (a 401 on reconnecting: the server restarted, or the sign-in went unused too long). Carries
    /// the reason in words. The dashboard goes back to the sign-in window; dialling on would be
    /// a loop that can never succeed.
    /// </summary>
    public event Action<string>? OnSignedOut;

    private void SignedOut(string why)
    {
        if (Token.Length == 0) return;
        Token = "";
        Trace.Write("tech link: SIGNED OUT by the server — " + why);
        try { OnSignedOut?.Invoke(why); } catch { }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested && Token.Length > 0)
        {
            attempt++;
            ClientWebSocket? cws = null;
            try
            {
                Trace.Write($"tech link: connecting to {_wsBase}/ws/tech (attempt {attempt})");
                SecureTransport.Require(_wsBase);
                cws = new ClientWebSocket();
                cws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                // So a refusal can be told apart from a network that is down (below).
                cws.Options.CollectHttpResponseDetails = true;
                await cws.ConnectAsync(new Uri($"{_wsBase}/ws/tech?token={Token}"), ct);
                var ws = new WsConnection(cws);
                lock (_lock) _ws = ws;
                ws.OnMessage += Handle;
                OnConnectedChanged?.Invoke(true);
                Trace.Write("tech link: CONNECTED — waiting for the server's StateMsg");
                _ = Ping(ws, ct);
                await ws.RunAsync(ct);
                Trace.Write("tech link: socket closed");
            }
            // A bare catch here meant the dashboard sat empty with no explanation whatsoever while
            // this retried every three seconds for ever — the same swallowed-exception mistake
            // that hid the black-screen bug (HANDOVER 7). The reason a technician sees no devices
            // is almost always in this line.
            catch (Exception ex)
            {
                Trace.Write($"tech link: FAILED — {ex.GetType().Name}: {ex.Message}");
                // The server answered, and said this token is no good: restarted, signed out, or
                // unused too long. Not a network fault — no amount of retrying will mend it.
                if (cws?.HttpStatusCode == System.Net.HttpStatusCode.Unauthorized)
                    SignedOut("Your sign-in has ended — the server was restarted, or you were signed out. Please sign in again.");
            }
            lock (_lock) _ws = null;
            OnConnectedChanged?.Invoke(false);
            try { await Task.Delay(3000, ct); } catch { break; }
        }
        Trace.Write($"tech link: giving up (cancelled={ct.IsCancellationRequested}, haveToken={Token.Length > 0})");
    }

    private async Task Ping(WsConnection ws, CancellationToken ct)
    { try { while (ws.IsOpen && !ct.IsCancellationRequested) { ws.Send(new PingMsg()); await Task.Delay(15000, ct); } } catch { } }

    private void Handle(Msg m)
    {
        switch (m)
        {
            case StateMsg s:
                // The one line that separates "the server sent nothing" from "the server sent an
                // empty list" from "the dashboard threw them away" — three completely different
                // faults that look identical on screen.
                Trace.Write($"tech link: StateMsg — {s.Devices.Count} device(s) " +
                            $"[{string.Join(", ", s.Devices.Take(8).Select(d => d.MachineName + (d.Online ? "*" : "")))}], " +
                            $"{s.Sessions.Count} session(s), technicians online: {string.Join(", ", s.OnlineTechnicians)}");
                if (s.AllTechnicians.Count > 0) Roster = s.AllTechnicians;
                CategoriesOff = s.CategoriesOff.ToHashSet();
                BookingPhotos = Math.Max(0, s.BookingPhotos);
                OnState?.Invoke(s);
                break;
            case RingMsg r: OnRing?.Invoke(r.Call); break;
            case CallUpdateMsg c: OnCallUpdate?.Invoke(c.Call); break;
            case DeviceUpdateMsg d: OnDeviceUpdate?.Invoke(d.Device); break;
            case SessionReadyMsg sr: OnSessionReady?.Invoke(sr.Session); break;
            case TechPresenceMsg p: OnPresence?.Invoke(p.OnlineTechnicians); break;
            case ChatMsg chat: OnChat?.Invoke(chat); break;
            case TicketListMsg tl: OnTicketList?.Invoke(tl); break;
            case TicketDetailMsg td: OnTicketDetail?.Invoke(td.Detail); break;
            case TicketChangedMsg tc2: OnTicketChanged?.Invoke(tc2.Ticket); break;
            case ClientListMsg cl: OnClientList?.Invoke(cl); break;
            case PortalLinkMsg pl: OnPortalLink?.Invoke(pl); break;
            case MentionCountMsg mc: OnMentionCount?.Invoke(mc.Count); break;
            case BillingQueueMsg bq: OnBillingQueue?.Invoke(bq); break;
            case ActivityListMsg al: OnActivityList?.Invoke(al); break;
            case ActivityCountMsg ac: OnActivityCount?.Invoke(ac); break;
            case OutsiderVisitMsg ov: OnOutsiderVisit?.Invoke(ov); break;
            case OutsiderInviteMsg oi: OnOutsiderInvite?.Invoke(oi); break;
            case OutsiderJoinAskMsg oj: OnOutsiderJoinAsk?.Invoke(oj); break;
            case KnowledgeCountMsg kc: OnKnowledgeCount?.Invoke(kc); break;
            case TicketNoticeMsg tn2: OnTicketNotice?.Invoke(tn2); break;
            case RecurringKindsMsg rk: OnRecurringKinds?.Invoke(rk); break;
            case RecurringSetMsg rs: OnRecurringSet?.Invoke(rs); break;
            case ItemListMsg il: OnItemList?.Invoke(il); break;
            case QuoteSimilarMsg qs: OnQuoteSimilar?.Invoke(qs); break;
            case StaffChatMsg sc: OnStaffChat?.Invoke(sc); break;
            case StaffChatHistoryMsg sh: OnStaffChatHistory?.Invoke(sh); break;
            case StaffChatOverviewMsg so: OnStaffChatOverview?.Invoke(so); break;
            case WebChatListMsg wl: OnWebChatList?.Invoke(wl); break;
            case WebChatUpdateMsg wu: OnWebChatUpdate?.Invoke(wu); break;
            case WebChatLineMsg wline: OnWebChatLine?.Invoke(wline); break;
            case WebChatHistoryMsg wh: OnWebChatHistory?.Invoke(wh); break;
            case WebChatPeopleMsg wpm: OnWebChatPeople?.Invoke(wpm); break;
            case WatchSampleMsg wsm: OnWatchSample?.Invoke(wsm); break;
            case WatchEndedMsg we2: OnWatchEnded?.Invoke(we2); break;
            case WakeResultMsg wake: OnWakeResult?.Invoke(wake); break;
            case SavedPasswordsMsg spm: OnSavedPasswords?.Invoke(spm); break;
            case SavedPasswordWhoMsg spw: OnSavedPasswordWho?.Invoke(spw); break;
            case DeviceGoneMsg dg: OnDeviceGone?.Invoke(dg); break;
            case ErrorMsg { NeedsPassword: true, DeviceId: { } pd } pe: OnPasswordRefused?.Invoke(pd, pe.Message, pe.NoPasswordGiven); break;
            case ErrorMsg { NeedsReason: true, DeviceId: { } rd }: OnReasonNeeded?.Invoke(rd); break;
            case ErrorMsg e: OnError?.Invoke(e.Message); break;
            case SignedOutMsg so2: SignedOut(so2.Reason); break;
        }
    }

    public void Accept(string callId) { lock (_lock) _ws?.Send(new AcceptCallMsg { CallId = callId }); }
    /// <summary>Starts an unattended session. The reason is required and becomes the ticket's
    /// description - see ReasonDialog and TenantHub.ConnectDevice. The password is the machine's
    /// unattended password, when it has one; empty otherwise.</summary>
    public void ConnectDevice(string deviceId, string reason, string password = "") { lock (_lock) _ws?.Send(new ConnectDeviceMsg { DeviceId = deviceId, Reason = reason, Password = password }); }
    public void EndSession(string sessionId) { lock (_lock) _ws?.Send(new EndSessionMsg { SessionId = sessionId }); }
    /// <summary>Open (true) or close a Processes/Performance window on a machine. Look only.</summary>
    public void WatchMachine(string deviceId, bool on) { lock (_lock) _ws?.Send(new WatchMachineMsg { DeviceId = deviceId, On = on }); }
    public void WakeMachine(string deviceId) { lock (_lock) _ws?.Send(new WakeRequestMsg { DeviceId = deviceId }); }
    /// <summary>Forget a saved unattended password: one's own (technicianId 0), somebody else's, or everybody's.</summary>
    public void ForgetSavedPassword(string deviceId, int technicianId = 0, bool all = false) { lock (_lock) _ws?.Send(new ForgetSavedPasswordMsg { DeviceId = deviceId, TechnicianId = technicianId, All = all }); }
    public void AskWhoSavedPassword(string deviceId) { lock (_lock) _ws?.Send(new SavedPasswordWhoRequestMsg { DeviceId = deviceId }); }
    /// <summary>Take a machine off the list (the free version).</summary>
    public void RemoveDevice(string deviceId, string reason = "", string note = "") { lock (_lock) _ws?.Send(new DeviceRemoveMsg { DeviceId = deviceId, Reason = reason, Note = note }); }
    /// <summary>This reinstalled machine is the same machine (2026-10-07) — a supervisor's or the owner's; the hub decides.</summary>
    public void ConfirmReinstall(string deviceId) { lock (_lock) _ws?.Send(new ConfirmReinstallMsg { DeviceId = deviceId }); }
    public void SendChat(string deviceId, string text) { lock (_lock) _ws?.Send(new ChatMsg { DeviceId = deviceId, Text = text, FromTech = true, From = DisplayName }); }

    // ---- tickets ----
    public void RequestTickets(string filter, string search, string sort = "") { lock (_lock) _ws?.Send(new TicketListRequestMsg { Filter = filter, Search = search, Sort = sort }); }
    public void OpenTicket(int id) { lock (_lock) _ws?.Send(new TicketOpenMsg { TicketId = id }); }
    public void RequestActivity(ActivityRequestMsg m) { lock (_lock) _ws?.Send(m); }
    public void CreateTicket(TicketCreateMsg m) { lock (_lock) _ws?.Send(m); }
    /// <summary>Recurring tasks (2026-10-07): the company's types and people for New ticket, and stopping one.</summary>
    public void RequestRecurringKinds() { lock (_lock) _ws?.Send(new RecurringKindsRequestMsg()); }
    public void CancelRecurring(RecurringCancelMsg m) { lock (_lock) _ws?.Send(m); }
    public void UpdateTicket(TicketUpdateMsg m) { lock (_lock) _ws?.Send(m); }
    public void SetTicketClient(int ticketId, int clientId, string? contactName) { lock (_lock) _ws?.Send(new TicketSetClientMsg { TicketId = ticketId, ClientId = clientId, ContactName = contactName }); }
    /// <summary>"Not part of it" (2026-10-09): 0 takes the ticket out of the job it is filed under.</summary>
    public void SetTicketParent(int ticketId, int parentId) { lock (_lock) _ws?.Send(new TicketSetParentMsg { TicketId = ticketId, ParentTicketId = parentId }); }
    /// <summary>Edit the ticket (2026-10-13): subject, description, category, appointment — answered by a notice and the ticket.</summary>
    public void EditTicket(TicketEditMsg m) { lock (_lock) _ws?.Send(m); }
    /// <param name="emailTo">Also email it to the client at this address (2026-10-09); empty keeps it on the ticket only.</param>
    public void AddTicketNote(int id, string body, string emailTo = "", List<long>? files = null) { lock (_lock) _ws?.Send(new TicketNoteAddMsg { TicketId = id, Body = body, EmailTo = emailTo, AttachmentIds = files ?? new() }); }
    /// <summary>"Email to the client…" on a comment already posted (2026-10-09).</summary>
    public void EmailTicketNote(int id, long noteId, string to) { lock (_lock) _ws?.Send(new TicketEmailNoteMsg { TicketId = id, NoteId = noteId, To = to }); }
    // Office chat: who it is from is the server's to say, from the socket — never sent from here.
    public void SendStaffChat(string to, string text) { lock (_lock) _ws?.Send(new StaffChatSendMsg { To = to, Text = text }); }
    public void RequestStaffChatHistory(string with) { lock (_lock) _ws?.Send(new StaffChatHistoryRequestMsg { With = with }); }
    public void MarkStaffChatRead(string with, long upToId) { lock (_lock) _ws?.Send(new StaffChatReadMsg { With = with, UpToId = upToId }); }
    public void RequestStaffChatOverview() { lock (_lock) _ws?.Send(new StaffChatOverviewRequestMsg()); }
    // Website chat: who says it is the server's to decide, from the socket.
    public void SayWebChat(int chatId, string text) { lock (_lock) _ws?.Send(new WebChatSayMsg { ChatId = chatId, Text = text }); }
    public void EndWebChat(int chatId) { lock (_lock) _ws?.Send(new WebChatEndMsg { ChatId = chatId }); }
    public void RequestWebChatHistory(int chatId) { lock (_lock) _ws?.Send(new WebChatHistoryRequestMsg { ChatId = chatId }); }
    public void RequestWebChatPeople(int chatId) { lock (_lock) _ws?.Send(new WebChatPeopleRequestMsg { ChatId = chatId }); }
    public void HandOverWebChat(int chatId, string to) { lock (_lock) _ws?.Send(new WebChatHandOverMsg { ChatId = chatId, To = to }); }
    public void ActOnTicket(TicketActMsg m) { lock (_lock) _ws?.Send(m); }
    public void AddWorkDone(int id, string body) { lock (_lock) _ws?.Send(new TicketWorkDoneMsg { TicketId = id, Body = body }); }
    public void SuggestItems(string term) { lock (_lock) _ws?.Send(new ItemSuggestMsg { Term = term }); }
    public void AddQuoteLine(QuoteLineAddMsg m) { lock (_lock) _ws?.Send(m); }
    public void SetQuoteMachine(QuoteMachineMsg m) { lock (_lock) _ws?.Send(m); }
    public void RemoveQuoteLine(int ticketId, long lineId) { lock (_lock) _ws?.Send(new QuoteLineRemoveMsg { TicketId = ticketId, LineId = lineId }); }
    public void AddQuoteLinePhotos(int ticketId, long lineId, List<long> photoIds) { lock (_lock) _ws?.Send(new QuoteLinePhotosMsg { TicketId = ticketId, LineId = lineId, PhotoIds = photoIds }); }
    public void SearchClients(string term, int take = 12) { lock (_lock) _ws?.Send(new ClientSearchMsg { Term = term, Take = take }); }

    // ---- who a machine belongs to ----

    /// <summary>
    /// Says which customer or person owns a machine. Zero for both unlinks it.
    ///
    /// There is no reply to wait for: the server broadcasts the machine's new owner to every open
    /// console, this one included, so the row moves under its new customer by the same path as a
    /// change made in the portal. One mechanism, and no chance of the two disagreeing.
    /// </summary>
    public void LinkDevice(string deviceId, int clientId, int contactId, string newContactName = "")
    { lock (_lock) _ws?.Send(new DeviceLinkMsg { DeviceId = deviceId, ClientId = clientId, ContactId = contactId, NewContactName = newContactName ?? "" }); }

    /// <summary>
    /// One machine's monthly health check: follow its customer, on, or off (<see cref="HealthCheckModes"/>).
    /// No reply either — the server broadcasts the machine, and later broadcasts again when its
    /// agent says what it actually did.
    /// </summary>
    public void SetDeviceHealthCheck(string deviceId, int mode)
    { lock (_lock) _ws?.Send(new DeviceHealthCheckMsg { DeviceId = deviceId, Mode = mode }); }

    /// <summary>One machine's outside help: as the company, on, or off (<see cref="OutsiderAccess"/>). A supervisor's or the owner's; the server refuses anybody else.</summary>
    public void SetDeviceOutsiderMode(string deviceId, int mode)
    { lock (_lock) _ws?.Send(new DeviceOutsiderModeMsg { DeviceId = deviceId, Mode = mode }); }

    /// <summary>Connect to another company's customer with the code they gave (3.9). The answer is a
    /// SessionReadyMsg, started by this technician, or an ErrorMsg in words.</summary>
    public void ConnectWithCode(string code)
    { lock (_lock) _ws?.Send(new OutsiderConnectMsg { Code = code }); }

    /// <summary>Anything a session window says about outside help (§155), on this signed-in socket.</summary>
    public void Send(Msg m) { lock (_lock) _ws?.Send(m); }

    // ---- opening the portal as the person already signed in here ----

    /// <summary>Asks the server for a one-use link that signs this technician in to the portal.</summary>
    public void RequestPortalLink(string path) { lock (_lock) _ws?.Send(new PortalLinkRequestMsg { Path = path }); }

    /// <summary>How many knowledge base articles the client this machine belongs to has (3.8.5).</summary>
    public void RequestKnowledgeCount(string deviceId) { lock (_lock) _ws?.Send(new KnowledgeCountRequestMsg { DeviceId = deviceId }); }

    /// <summary>
    /// Turns the server's relative hand-off path into a URL a browser can open, against the server
    /// this console is actually connected to. See PortalLinkMsg for why the server does not name
    /// itself.
    /// </summary>
    public string ResolvePortalUrl(string relative) =>
        _httpBase + (relative.StartsWith('/') ? relative : "/" + relative);

    /// <summary>
    /// Uploads workshop photos over HTTP, against a booking form that has no ticket yet.
    ///
    /// Over HTTP and not over the websocket, deliberately: three 5 MB images sent down the control
    /// socket would block it, and that is the same socket a customer's call rings on. The console
    /// authenticates with the bearer token it already holds from signing in.
    ///
    /// <para><b>One photo per request</b> (2026-09-29). They all went in one, and fourteen phone
    /// photos of a dropped laptop — 60 MB — were past what the server took, so every one of them
    /// was lost with "Error while copying content to a stream". One at a time, each is a few MB and
    /// one bad photo cannot take the rest with it. <paramref name="progress"/> hears "3 of 14".</para>
    /// </summary>
    public async Task<(int uploaded, string error)> UploadPhotosAsync(IEnumerable<string> paths, string kind,
        string stageId, int ticketId = 0, Action<int, int>? progress = null)
    {
        var (ids, error) = await UploadPhotoIdsAsync(paths, kind, stageId, ticketId, progress);
        return (ids.Count, error);
    }

    /// <summary>The same, handing back the id of each photo stored — a quote line then names them.</summary>
    public async Task<(List<long> ids, string error)> UploadPhotoIdsAsync(IEnumerable<string> paths, string kind,
        string stageId, int ticketId = 0, Action<int, int>? progress = null)
    {
        var list = paths.ToList();
        var ids = new List<long>();
        if (list.Count == 0) return (ids, "");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
        var problems = new List<string>();
        for (int i = 0; i < list.Count; i++)
        {
            progress?.Invoke(i + 1, list.Count);
            try
            {
                using var form = new MultipartFormDataContent
                {
                    { new StringContent(kind), "kind" },
                    { new StringContent(stageId), "stage" }
                };
                if (ticketId > 0) form.Add(new StringContent(ticketId.ToString()), "ticketId");
                // Smaller first, as the portal does (PhotoShrink): half a megabyte rather than four.
                var (bytes, name) = await Task.Run(() => PhotoShrink.Prepare(list[i]));
                var part = new ByteArrayContent(bytes);
                part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                form.Add(part, "files", name);

                var resp = await http.PostAsync($"{_httpBase}/api/portal/photos", form);
                if (!resp.IsSuccessStatusCode) { problems.Add($"{Path.GetFileName(list[i])}: the server refused it ({(int)resp.StatusCode})."); continue; }
                var body = await resp.Content.ReadFromJsonAsync<PhotoUploadResult>(Json.Options);
                foreach (var p in body?.Photos ?? new())
                    if (string.IsNullOrEmpty(p.Error)) ids.Add(p.Id); else problems.Add(p.Error!);
            }
            catch (Exception ex) { problems.Add($"{Path.GetFileName(list[i])}: {ex.Message}"); }
        }
        return (ids, problems.Count == 0 ? "" : string.Join("\n", problems));
    }

    /// <summary>
    /// Saves this person's own light or dark against their account — the same setting as the
    /// portal's switch. Returns an empty string on success, or what went wrong.
    /// </summary>
    public async Task<string> SaveAppearanceAsync(string theme)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
            var resp = await http.PostAsJsonAsync($"{_httpBase}/api/appearance", new { theme }, Json.Options);
            if (!resp.IsSuccessStatusCode) return $"The server refused it ({(int)resp.StatusCode}).";
            var body = await resp.Content.ReadFromJsonAsync<AppearanceResult>(Json.Options);
            Appearance = (body?.Appearance ?? "").Trim().ToLowerInvariant();
            Trace.Write($"appearance: saved '{Appearance}'");
            return "";
        }
        catch (Exception ex) { Trace.Write($"appearance: FAILED — {ex.GetType().Name}: {ex.Message}"); return ex.Message; }
    }

    private sealed record AppearanceResult(string? Appearance);

    private sealed class PhotoUploadResult { public List<PhotoUploadItem>? Photos { get; set; } }
    private sealed class PhotoUploadItem { public long Id { get; set; } public string? Name { get; set; } public string? Error { get; set; } }

    /// <summary>
    /// Files and photos for a comment or a step (2026-10-13), each on its own request as they are picked — a photo made
    /// smaller first (PhotoShrink, which hands back anything that is not a picture as it is). Held loose on the ticket by
    /// the server until the comment that names them is posted. Over HTTP, not the socket, as photos are.
    /// </summary>
    public async Task<(List<AttachmentInfo> files, string error)> UploadAttachmentsAsync(int ticketId, IEnumerable<string> paths,
        Action<int, int>? progress = null)
    {
        var list = paths.ToList();
        var files = new List<AttachmentInfo>();
        if (list.Count == 0) return (files, "");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
        var problems = new List<string>();
        for (int i = 0; i < list.Count; i++)
        {
            progress?.Invoke(i + 1, list.Count);
            try
            {
                using var form = new MultipartFormDataContent { { new StringContent(ticketId.ToString()), "ticketId" } };
                var (bytes, name) = await Task.Run(() => PhotoShrink.Prepare(list[i]));
                var part = new ByteArrayContent(bytes);
                part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                form.Add(part, "files", name);
                var resp = await http.PostAsync($"{_httpBase}/api/portal/attachments", form);
                var body = await resp.Content.ReadFromJsonAsync<AttachmentUploadResult>(Json.Options);
                if (!resp.IsSuccessStatusCode) { problems.Add($"{Path.GetFileName(list[i])}: {body?.Error ?? $"the server refused it ({(int)resp.StatusCode})."}"); continue; }
                foreach (var f in body?.Files ?? new())
                    if (string.IsNullOrEmpty(f.Error)) files.Add(new AttachmentInfo { Id = f.Id, Name = f.Name ?? name, Size = f.Size ?? "", IsImage = f.Image });
                    else problems.Add(f.Error!);
            }
            catch (Exception ex) { problems.Add($"{Path.GetFileName(list[i])}: {ex.Message}"); }
        }
        return (files, problems.Count == 0 ? "" : string.Join("\n", problems));
    }

    /// <summary>Takes a picked file back off before the comment is posted — the × beside it.</summary>
    public async Task RemoveAttachmentAsync(long id)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
            await http.PostAsync($"{_httpBase}/api/portal/attachments/{id}/remove", null);
        }
        catch { }   // left loose, it is cleared after a day
    }

    private sealed class AttachmentUploadResult { public List<AttachmentUploadItem>? Files { get; set; } public string? Error { get; set; } }
    private sealed class AttachmentUploadItem { public long Id { get; set; } public string? Name { get; set; } public string? Size { get; set; } public bool Image { get; set; } public string? Error { get; set; } }
}
