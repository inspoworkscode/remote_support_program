using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Hub;

/// <summary>
/// Website chat (3.8.3, 2026-09-30), the console's half. Every rule is <see cref="WebChatService"/>'s;
/// this only carries it: the list at sign-in, every change and line to every console of the company
/// (HubRegistry hands the service's events to <see cref="PushWebChat"/>), and what a console says or
/// ends — as the person the socket signed in as, never a name the console claims.
/// </summary>
public sealed partial class TenantHub
{
    /// <summary>A website chat change or line, to every console — everybody signed in hears every chat.</summary>
    public void PushWebChat(Msg m) => BroadcastTechs(m);

    /// <summary>A ticket made outside the hub (a website message), so the consoles' lists show it at once.</summary>
    public async Task PushTicketChangedAsync(int ticketId)
    {
        try
        {
            var fresh = await _tickets.GetAsync(TenantId, ticketId);
            if (fresh != null) BroadcastTechs(new TicketChangedMsg { Ticket = TicketService.ToSummary(fresh) });
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] website message ticket", TenantName); }
    }

    /// <summary>Whether it is on, what is going on now, and the links — to one console, or to all of them
    /// (after the settings page changes something).</summary>
    public Task PushWebChatListAsync() => SendWebChatListAsync(null);

    private async Task SendWebChatListAsync(TechConn? only)
    {
        try
        {
            Data.Tenant? t;
            await using (var db = await _dbf.CreateDbContextAsync()) t = await db.Tenants.FindAsync(TenantId);
            if (t == null) return;
            var chats = await _registry.WebChat.OpenAsync(TenantId);
            // One each: whether they may hand over a colleague's chat is their own (2026-10-15).
            List<TechConn> to;
            if (only != null) to = new() { only }; else lock (_lock) to = _techs.ToList();
            foreach (var tc in to)
                tc.Ws.Send(new WebChatListMsg
                {
                    Enabled = t.WebChatEnabled, Chats = chats,
                    AppLink = _registry.WebChat.AppLink(t), HelpLink = _registry.WebChat.HelpLink(t),
                    MayHandOverAny = Permissions.CanHandOverAnyWebChat(tc.Who.Role)
                });
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] website chat list", TenantName); }
    }

    private async Task WebChatSayAsync(TechConn tc, WebChatSayMsg m)
    {
        var (_, error) = await _registry.WebChat.StaffSaysAsync(TenantId, m.ChatId, tc.Who.DisplayName, m.Text);
        // A line that was said comes back to everybody through PushWebChat; only a refusal is answered here.
        if (error.Length > 0) tc.Ws.Send(new WebChatLineMsg { ChatId = m.ChatId, Error = error });
    }

    private async Task WebChatEndAsync(TechConn tc, WebChatEndMsg m)
    {
        var error = await _registry.WebChat.EndAsync(TenantId, m.ChatId, tc.Who.DisplayName);
        if (error.Length > 0) tc.Ws.Send(new WebChatLineMsg { ChatId = m.ChatId, Error = error });
    }

    /// <summary>A hand-over (2026-10-15), as the person and role the socket signed in as. The move reaches everybody
    /// through PushWebChat; only a refusal is answered here.</summary>
    private async Task WebChatHandOverAsync(TechConn tc, WebChatHandOverMsg m)
    {
        var error = await _registry.WebChat.HandOverAsync(TenantId, m.ChatId, tc.Who.DisplayName, tc.Who.Role, m.To);
        if (error.Length > 0) tc.Ws.Send(new WebChatLineMsg { ChatId = m.ChatId, Error = error });
    }

    private async Task WebChatHistoryAsync(TechConn tc, int chatId) =>
        tc.Ws.Send(new WebChatHistoryMsg { ChatId = chatId, Lines = await _registry.WebChat.LinesAsync(TenantId, chatId) });
}
