using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Hub;

/// <summary>
/// Support in the browser (3.8.2, 2026-09-30). Clint: "potential clients to be able to get remote
/// support via a browser. So they click on a link, put in their contact details, and request a
/// support call online and it rings through to the technicians the same way the app works."
///
/// <para>A browser caller is a PRETEND DEVICE, "web-…", that is never saved and never listed with
/// the machines: it lives here, in <see cref="_web"/>, for as long as their page is open. The few
/// places the hub looks a device up — its name, its "client" socket, starting a session, routing
/// chat, which client it belongs to — ask this file too, and everything else (the ring, accepting,
/// the session relay, the ticket's story, ending) is the path the tray app already takes.</para>
///
/// <para>What a browser cannot do is let anybody move the pointer or type: the page can only SHOW
/// the screen, with the person's permission. So the session is <see cref="SessionInfo.ViewOnly"/>,
/// and the page offers the real app for full control.</para>
/// </summary>
public sealed partial class TenantHub
{
    public const string WebPrefix = "web-";
    /// <summary>At most this many browser callers per company at once, waiting or being helped.
    /// It is a public page: a flood of form posts must not become a flood of ringing consoles.</summary>
    public const int MaxWebCallers = 10;
    /// <summary>A form posted and its page never connected: forgotten after this.</summary>
    private static readonly TimeSpan WebPendingLife = TimeSpan.FromMinutes(10);

    private sealed class WebCaller
    {
        public string Id = "", Token = "";
        public string Name = "", Company = "", Phone = "", Email = "", Issue = "", Ip = "";
        public DateTime CreatedUtc = DateTime.UtcNow;
        public WsConnection? Ws;
        public DeviceConn? Conn;
        public int? ClientId;
        public int TicketId;
        public string? CallId;
    }

    /// <summary>Guarded by <c>_lock</c>.</summary>
    private readonly Dictionary<string, WebCaller> _web = new(StringComparer.Ordinal);

    private static bool IsWeb(string deviceId) => deviceId.StartsWith(WebPrefix, StringComparison.Ordinal);

    /// <summary>The caller's page, as the "client" of their pretend device — null until it connects.</summary>
    private DeviceConn? WebConn(string deviceId)
    {
        if (!IsWeb(deviceId)) return null;
        lock (_lock) return _web.TryGetValue(deviceId, out var w) ? w.Conn : null;
    }

    private string WebMachineName(string deviceId)
    {
        lock (_lock) return _web.TryGetValue(deviceId, out var w) ? $"{w.Name} (web browser)" : "Web browser";
    }

    private int? WebClientId(string deviceId)
    {
        if (!IsWeb(deviceId)) return null;
        lock (_lock) return _web.TryGetValue(deviceId, out var w) ? w.ClientId : null;
    }

    /// <summary>How many browser callers are waiting or being helped — for the harness and the status page.</summary>
    public int WebCallers { get { lock (_lock) return _web.Count; } }

    /// <summary>
    /// The public page's form, accepted: remembered until the page connects its socket, which is
    /// what makes it ring. The token is what that socket must show — it goes back only to the
    /// browser that posted the form.
    /// </summary>
    public (string id, string token, string error) RegisterWebCaller(string name, string company, string phone, string email, string issue, string ip)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            foreach (var stale in _web.Values.Where(w => w.Ws == null && now - w.CreatedUtc > WebPendingLife).Select(w => w.Id).ToList())
                _web.Remove(stale);
            if (_web.Count >= MaxWebCallers)
                return ("", "", "Our technicians are all busy right now. Please try again in a few minutes, or phone us.");
            var w = new WebCaller
            {
                Id = WebPrefix + Guid.NewGuid().ToString("N")[..16], Token = Passwords.RandomToken(),
                Name = name.Trim(), Company = company.Trim(), Phone = phone.Trim(), Email = email.Trim(), Issue = issue.Trim(), Ip = ip
            };
            _web[w.Id] = w;
            return (w.Id, w.Token, "");
        }
    }

    /// <summary>
    /// The caller's page, connected: it rings, exactly as a tray app's Request Support does, and
    /// stays theirs until they close it — which cancels a call nobody answered and ends a session.
    /// </summary>
    public async Task RunWebCallerAsync(string id, string token, WsConnection ws, CancellationToken ct)
    {
        await UseCompanyClockAsync();
        WebCaller? w;
        lock (_lock)
        {
            if (!_web.TryGetValue(id, out w) || w.Token != token || w.Ws != null) w = null;
            else { w.Ws = ws; w.Conn = new DeviceConn { DeviceId = id, Role = DeviceRole.Client, Ws = ws }; }
        }
        if (w == null)
        {
            ws.Send(new ErrorMsg { Message = "This support request has expired. Please fill in the form again." });
            await ws.CloseAsync();
            return;
        }
        ws.OnMessage += m => OnWebMessage(w, m);

        // The ticket first, so it exists before anybody answers: the session joins it, and the chat
        // before an answer is written on it.
        await OpenWebTicketAsync(w);
        CallInfo call;
        lock (_lock)
        {
            call = new CallInfo
            {
                CallId = Guid.NewGuid().ToString("N"), DeviceId = id, MachineName = WebMachineName(id),
                ContactName = w.Company.Length > 0 ? $"{w.Name} ({w.Company})" : w.Name, Issue = w.Issue, Status = CallStatus.Ringing
            };
            _calls[call.CallId] = call;
            w.CallId = call.CallId;
        }
        ws.Send(new CallUpdateMsg { Call = call });
        BroadcastTechs(new RingMsg { Call = call });
        _log.LogInformation("[{Tenant}] browser support request from {Name} ({Ip})", TenantName, w.Name, w.Ip);

        await ws.RunAsync(ct);

        // Their page closed: nobody is there to answer or to watch.
        string? sessionId; bool wasRinging;
        lock (_lock)
        {
            _web.Remove(id);
            wasRinging = call.Status == CallStatus.Ringing;
            if (wasRinging) call.Status = CallStatus.Cancelled;
            sessionId = _sessions.Values.FirstOrDefault(s => s.Info.DeviceId == id)?.Info.SessionId;
        }
        if (wasRinging) BroadcastTechs(new CallUpdateMsg { Call = call });
        if (sessionId != null) EndSession(sessionId, "The customer closed the support page.");
        if (w.TicketId != 0)
            await LogToTicketAsync(w.TicketId, w.Name, wasRinging ? "The customer closed the support page before anybody answered." : "The customer closed the support page.");
        _log.LogInformation("[{Tenant}] browser support page closed by {Name}", TenantName, w.Name);
    }

    private void OnWebMessage(WebCaller w, Msg m)
    {
        try
        {
            switch (m)
            {
                case ChatMsg c:
                    var text = (c.Text ?? "").Trim();
                    if (text.Length == 0) return;
                    if (text.Length > 2000) text = text[..2000];
                    // Who they are is what they typed into the form, never what the page claims now.
                    RouteChat(new ChatMsg { DeviceId = w.Id, From = w.Name, FromTech = false, Text = text, Ts = DateTimeOffset.UtcNow });
                    break;
                case CancelCallMsg:
                {
                    CallInfo? call = null;
                    lock (_lock)
                        if (w.CallId != null && _calls.TryGetValue(w.CallId, out var found) && found.Status == CallStatus.Ringing)
                        { found.Status = CallStatus.Cancelled; call = found; }
                    if (call == null) return;
                    BroadcastTechs(new CallUpdateMsg { Call = call });
                    w.Ws?.Send(new CallUpdateMsg { Call = call });
                    break;
                }
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] browser caller message", TenantName); }
    }

    /// <summary>
    /// Clint: "yes definitely" — a browser caller becomes a Contact and a ticket. Found by their
    /// email or phone if the company already knows them; otherwise under the company they named
    /// (made if it is new), or under their own name when they named none. Never duplicated: the
    /// services reuse a client or contact of the same name.
    /// </summary>
    private async Task OpenWebTicketAsync(WebCaller w)
    {
        try
        {
            // One rule with website chat (3.8.3): Services/Visitors.
            var (clientId, _, contactName) = await _registry.Visitors.FindOrCreateAsync(TenantId, w.Name, w.Company, w.Phone, w.Email,
                $"Added by browser support on {CompanyClock.Now:d MMMM yyyy}.", "Asked for support from the browser.");
            lock (_lock) w.ClientId = clientId;

            var subject = w.Issue.Length == 0 ? $"Browser support request from {w.Name}"
                : w.Issue.Length <= 70 ? w.Issue : w.Issue[..67] + "…";
            var details = string.Join("\n", new[]
            {
                w.Issue,
                "",
                $"Name: {w.Name}",
                w.Company.Length > 0 ? $"Company: {w.Company}" : "",
                w.Phone.Length > 0 ? $"Phone: {w.Phone}" : "",
                w.Email.Length > 0 ? $"Email: {w.Email}" : "",
            }.Where((s, i) => i < 2 || s.Length > 0));
            var t = await _tickets.CreateAsync(TenantId, subject, details, w.Name, TicketSource.Call,
                w.Id, WebMachineName(w.Id), contactName, TicketPriority.Normal, "", TicketCategory.RemoteSupport, clientId);
            await _tickets.AddNoteAsync(TenantId, t.Id, w.Name, "Customer asked for support from the web browser page (view only: they can share their screen and chat).", true, automatic: true);
            lock (_lock) w.TicketId = t.Id;
            var fresh = await _tickets.GetAsync(TenantId, t.Id);
            if (fresh != null) BroadcastTechs(new TicketChangedMsg { Ticket = TicketService.ToSummary(fresh) });
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] browser support ticket", TenantName); }
    }
}
