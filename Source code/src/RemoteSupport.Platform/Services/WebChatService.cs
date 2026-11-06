using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Website chat (3.8.3, 2026-09-30). Clint: "a chat window for a tenants website. This would allow
/// them to install it on their own website and when one chats in it is goes through to every logged
/// in technician including supervisors and office administrators. this would be a great way for
/// someone to answer questions immediately and guide them to the support page."
///
/// His answers, the same day: <b>the first person to reply takes it</b> (everybody else sees who is
/// answering, and may still join in); <b>a visitor gives their name and an email or a phone</b> first,
/// and becomes a Contact; <b>when nobody is signed in, or outside the chat's hours, they leave a
/// message, which becomes a General ticket</b>.
///
/// Every rule lives here. The console hears through <see cref="Changed"/> and <see cref="Line"/>
/// (HubRegistry hands them to the hubs), the portal through the same events on its live stream, and
/// the visitor's own socket (/ws/webchat) likewise — so all three always agree.
/// </summary>
public sealed class WebChatService
{
    public const int NameMax = 80, TextMax = 2000;
    /// <summary>Chats going on at once per company. A public bubble must not become a flood.</summary>
    public const int MaxOpen = 20;
    /// <summary>A chat whose visitor has gone and nobody has said anything in this long is ended.</summary>
    public static readonly TimeSpan IdleEnd = TimeSpan.FromMinutes(30);

    /// <summary>Chats and messages from one address to one company in an hour. More than browser
    /// support's five: a customer's whole office can sit behind one internet address.</summary>
    public const int PerAddressPerHour = 10;
    private static readonly ConcurrentDictionary<string, List<DateTime>> Recent = new();

    /// <summary>A sliding hour per company and address. In memory: a restart forgets, which is fine.</summary>
    public static bool Allow(int tenantId, string ip, DateTime? now = null)
    {
        var at = now ?? DateTime.UtcNow;
        var list = Recent.GetOrAdd($"{tenantId}|{ip}", _ => new List<DateTime>());
        lock (list)
        {
            list.RemoveAll(x => at - x > TimeSpan.FromHours(1));
            if (list.Count >= PerAddressPerHour) return false;
            list.Add(at);
            return true;
        }
    }

    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly Visitors _visitors;
    private readonly TicketService _tickets;
    private readonly StaffPresence _presence;
    private readonly string _publicUrl;
    /// <summary>For tools/webchatcheck alone, never set in production (Testing:WebChatHandOverGapMs): holds the gap between
    /// reading who has a chat and moving it, so hand-overs at once on a fast local database really do meet.</summary>
    private readonly int _handOverGapMs;
    /// <summary>How many of each chat's visitor pages are connected right now.</summary>
    private readonly ConcurrentDictionary<int, int> _here = new();

    public WebChatService(IDbContextFactory<PlatformDb> dbf, Visitors visitors, TicketService tickets,
        StaffPresence presence, IConfiguration cfg)
    {
        _dbf = dbf; _visitors = visitors; _tickets = tickets; _presence = presence;
        _publicUrl = (cfg["Platform:PublicUrl"] ?? "").TrimEnd('/');
        _handOverGapMs = cfg.GetValue<int>("Testing:WebChatHandOverGapMs");
    }

    /// <summary>A chat started, was answered, its visitor came or went, or it ended.</summary>
    public event Action<int, WebChatInfo>? Changed;
    /// <summary>Somebody said something in a chat.</summary>
    public event Action<int, WebChatLineInfo>? Line;
    /// <summary>A left message became this ticket — so the consoles' lists can show it at once.</summary>
    public event Action<int, int>? TicketMade;

    // ---------------------------------------------------------------- what the bubble does

    public enum Mode { Off, Open, Message }

    /// <summary>
    /// Off: no bubble at all. Open: somebody is signed in and it is within the chat's hours. Message:
    /// switched on, but nobody to answer — the bubble takes a message instead.
    /// </summary>
    public Mode ModeFor(Tenant t, DateTime utcNow) =>
        // A lapsed trial or a closed company has no bubble, the rule the downloads follow.
        !t.WebChatEnabled || !t.IsServiceEnabled || FreePlan.Is(t.Plan) ? Mode.Off
        : WebSupportHours.IsChatOpen(t, utcNow) && _presence.Count(t.Id) > 0 ? Mode.Open
        : Mode.Message;

    /// <summary>The app's download page, to hand a visitor.</summary>
    public string AppLink(Tenant t) => $"{_publicUrl}/get/{t.Slug}";
    /// <summary>Browser support's page, while it is switched on — empty otherwise, so no button offers it.</summary>
    public string HelpLink(Tenant t) => t.WebSupportEnabled ? $"{_publicUrl}/help/{t.Slug}" : "";

    /// <summary>What is wrong with a visitor's details, or empty. A name, and an email OR a phone.</summary>
    public static string Check(string? name, string? email, string? phone, string? text)
    {
        name = (name ?? "").Trim(); email = (email ?? "").Trim(); phone = (phone ?? "").Trim(); text = (text ?? "").Trim();
        if (name.Length == 0) return "Please tell us your name.";
        if (name.Length > NameMax) return "That name is too long.";
        if (email.Length == 0 && phone.Length == 0) return "Please give us an email address or a phone number, so we can get back to you.";
        if (email.Length > 0 && (email.Length > 200 || !email.Contains('@') || !email.Contains('.'))) return "That email address does not look right.";
        if (phone.Length > 0 && (phone.Length > 40 || phone.Count(char.IsDigit) < 7)) return "That phone number does not look right.";
        if (text.Length == 0) return "Please type your question.";
        if (text.Length > TextMax) return "That is too long for one message — please shorten it.";
        return "";
    }

    // ---------------------------------------------------------------- the visitor

    /// <summary>
    /// A visitor starts a chat. Returns the secret their browser keeps to reconnect — it is stored
    /// only hashed — or why not. A chat can start only while <see cref="Mode.Open"/>.
    /// </summary>
    public async Task<(WebChat? chat, string token, string error)> StartAsync(Tenant t, string? name, string? email, string? phone,
        string? text, string? page, string ip)
    {
        var problem = Check(name, email, phone, text);
        if (problem.Length > 0) return (null, "", problem);
        if (ModeFor(t, DateTime.UtcNow) != Mode.Open) return (null, "", "Nobody is available to chat right now — please leave us a message instead.");
        await using var db = await _dbf.CreateDbContextAsync();
        if (await db.WebChats.CountAsync(c => c.TenantId == t.Id && c.EndedUtc == null && !c.LeftMessage) >= MaxOpen)
            return (null, "", "We are helping a lot of people right now — please leave us a message instead.");

        var (clientId, contactId, _) = await _visitors.FindOrCreateAsync(t.Id, name!, "", phone ?? "", email ?? "",
            $"Added by website chat on {CompanyClock.Now:d MMMM yyyy}.", "Chatted on the website.");
        var token = Passwords.RandomToken();
        var chat = new WebChat
        {
            TenantId = t.Id, TokenHash = Hash(token), Name = name!.Trim(), Email = (email ?? "").Trim(), Phone = (phone ?? "").Trim(),
            Page = Clip(page, 400), Ip = ip, ClientId = clientId, ContactId = contactId
        };
        db.WebChats.Add(chat);
        await db.SaveChangesAsync();
        var line = new WebChatLine { TenantId = t.Id, WebChatId = chat.Id, FromVisitor = true, Name = chat.Name, Text = text!.Trim() };
        db.WebChatLines.Add(line);
        await db.SaveChangesAsync();
        Changed?.Invoke(t.Id, Info(chat, line.Text));
        Line?.Invoke(t.Id, LineInfo(line));
        return (chat, token, "");
    }

    /// <summary>
    /// Nobody there to answer: the visitor leaves a message, and it becomes a General ticket under
    /// their contact — Clint's choice. Kept as a chat too, so the history page shows it.
    /// </summary>
    public async Task<(int ticketNumber, string error)> LeaveMessageAsync(Tenant t, string? name, string? email, string? phone,
        string? text, string? page, string ip)
    {
        var problem = Check(name, email, phone, text);
        if (problem.Length > 0) return (0, problem);
        if (ModeFor(t, DateTime.UtcNow) == Mode.Off) return (0, "Chat is switched off for this website.");
        name = name!.Trim(); email = (email ?? "").Trim(); phone = (phone ?? "").Trim(); text = text!.Trim();

        var (clientId, contactId, contactName) = await _visitors.FindOrCreateAsync(t.Id, name, "", phone, email,
            $"Added by website chat on {CompanyClock.Now:d MMMM yyyy}.", "Left a message on the website.");
        var subject = text.Length <= 70 ? text : text[..67] + "…";
        var details = string.Join("\n", new[]
        {
            text, "",
            $"Name: {name}",
            phone.Length > 0 ? $"Phone: {phone}" : "",
            email.Length > 0 ? $"Email: {email}" : "",
            page is { Length: > 0 } ? $"Left on: {Clip(page, 400)}" : "",
        }.Where((s, i) => i < 2 || s.Length > 0));
        var ticket = await _tickets.CreateAsync(t.Id, subject, details, name, TicketSource.Chat,
            contactName: contactName, category: TicketCategory.General, clientId: clientId);
        await _tickets.AddNoteAsync(t.Id, ticket.Id, name, "Left as a message on the website chat, while nobody was available to answer.", true, automatic: true);

        await using var db = await _dbf.CreateDbContextAsync();
        var chat = new WebChat
        {
            TenantId = t.Id, Name = name, Email = email, Phone = phone, Page = Clip(page, 400), Ip = ip,
            ClientId = clientId, ContactId = contactId, LeftMessage = true, TicketId = ticket.Id,
            EndedUtc = DateTime.UtcNow, EndedBy = "visitor"
        };
        db.WebChats.Add(chat);
        await db.SaveChangesAsync();
        db.WebChatLines.Add(new WebChatLine { TenantId = t.Id, WebChatId = chat.Id, FromVisitor = true, Name = name, Text = text });
        await db.SaveChangesAsync();
        TicketMade?.Invoke(t.Id, ticket.Id);
        return (ticket.Number, "");
    }

    /// <summary>The chat this secret belongs to, ended or not; null for a wrong secret.</summary>
    public async Task<WebChat?> AuthAsync(int chatId, string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        await using var db = await _dbf.CreateDbContextAsync();
        var c = await db.WebChats.FirstOrDefaultAsync(x => x.Id == chatId && !x.LeftMessage);
        return c != null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(c.TokenHash), Encoding.ASCII.GetBytes(Hash(token))) ? c : null;
    }

    /// <summary>The visitor says something. Their name is the one they gave, never one a page claims.</summary>
    public Task<(WebChatLineInfo? line, string error)> VisitorSaysAsync(WebChat chat, string? text) =>
        SayAsync(chat.TenantId, chat.Id, true, null, text);

    /// <summary>Their page connected (+1) or went (-1): the office sees whether they are still there.</summary>
    public async Task VisitorHereAsync(int tenantId, int chatId, int delta)
    {
        _here.AddOrUpdate(chatId, Math.Max(0, delta), (_, n) => Math.Max(0, n + delta));
        await using var db = await _dbf.CreateDbContextAsync();
        var c = await db.WebChats.FirstOrDefaultAsync(x => x.Id == chatId && x.TenantId == tenantId);
        if (c != null && c.EndedUtc == null) Changed?.Invoke(tenantId, Info(c, await LastTextAsync(db, chatId)));
    }

    public bool IsVisitorHere(int chatId) => _here.TryGetValue(chatId, out var n) && n > 0;

    // ---------------------------------------------------------------- the office

    /// <summary>
    /// Somebody at the office says something. The first to do so takes the chat — done as one
    /// conditional update, so two people replying in the same instant cannot both take it.
    /// </summary>
    public Task<(WebChatLineInfo? line, string error)> StaffSaysAsync(int tenantId, int chatId, string who, string? text) =>
        SayAsync(tenantId, chatId, false, who, text);

    private async Task<(WebChatLineInfo? line, string error)> SayAsync(int tenantId, int chatId, bool fromVisitor, string? who, string? text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return (null, "Type something first.");
        if (text.Length > TextMax) return (null, "That is too long for one message — please shorten it.");
        await using var db = await _dbf.CreateDbContextAsync();
        var c = await db.WebChats.FirstOrDefaultAsync(x => x.Id == chatId && x.TenantId == tenantId && !x.LeftMessage);
        if (c == null) return (null, "That chat is not there any more.");
        if (c.EndedUtc != null) return (null, "This chat has ended.");

        bool claimed = false;
        if (!fromVisitor && c.AnsweredBy.Length == 0)
            claimed = await db.WebChats.Where(x => x.Id == chatId && x.AnsweredBy == "")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.AnsweredBy, who!)) == 1;

        var line = new WebChatLine { TenantId = tenantId, WebChatId = chatId, FromVisitor = fromVisitor, Name = fromVisitor ? c.Name : who!, Text = text };
        db.WebChatLines.Add(line);
        c.LastUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        if (claimed) { await db.Entry(c).ReloadAsync(); Changed?.Invoke(tenantId, Info(c, text)); }
        var info = LineInfo(line);
        Line?.Invoke(tenantId, info);
        return (info, "");
    }

    /// <summary>
    /// Gives a chat to somebody else (2026-10-15). Clint asked whether a website chat could be transferred:
    /// joining always worked, but the first reply took it for good, and only the person answering is dinged
    /// for the visitor's lines — so a chat left with a colleague was silent for them.
    ///
    /// <para>Who may: whoever is answering it, a supervisor or the owner (<see cref="Permissions.CanHandOverAnyWebChat"/>),
    /// or — while nobody has answered — anybody, to a named person (themselves included, which simply takes it).
    /// To whom: somebody of THIS company signed in right now (<see cref="StaffPresence"/>), so a chat is never
    /// handed to an empty chair. The move is one conditional update on the answerer read here, so two hand-overs
    /// at once — or a hand-over and a first reply — cannot both win. The visitor is told by a note line.</para>
    /// </summary>
    public async Task<string> HandOverAsync(int tenantId, int chatId, string from, TenantRole fromRole, string? to)
    {
        to = (to ?? "").Trim();
        if (to.Length == 0) return "Choose who to hand it to.";
        await using var db = await _dbf.CreateDbContextAsync();
        var c = await db.WebChats.FirstOrDefaultAsync(x => x.Id == chatId && x.TenantId == tenantId && !x.LeftMessage);
        if (c == null) return "That chat is not there any more.";
        if (c.EndedUtc != null) return "This chat has ended.";
        var current = c.AnsweredBy;
        if (current.Length > 0 && current != from && !Permissions.CanHandOverAnyWebChat(fromRole))
            return $"{current} is answering this chat — only they, a supervisor or the owner can hand it over.";
        if (to == current) return $"{to} is already answering this chat.";
        if (!_presence.Names(tenantId).Contains(to, StringComparer.Ordinal))
            return $"{to} is not signed in right now — a chat can only go to somebody who is here to answer it.";

        if (_handOverGapMs > 0) await Task.Delay(_handOverGapMs);
        var moved = await db.WebChats.Where(x => x.Id == chatId && x.AnsweredBy == current && x.EndedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.AnsweredBy, to).SetProperty(x => x.LastUtc, DateTime.UtcNow));
        if (moved != 1) return "Somebody else has just changed who is answering this chat — have another look.";

        var line = new WebChatLine
        {
            TenantId = tenantId, WebChatId = chatId, FromVisitor = false, Name = "",
            Text = current.Length == 0 ? $"{to} will be chatting with you." : $"{to} has taken over this chat."
        };
        db.WebChatLines.Add(line);
        await db.SaveChangesAsync();
        await db.Entry(c).ReloadAsync();
        // The change first, so every screen knows who is answering before the note arrives — the dings key on it.
        var info = Info(c, line.Text);
        info.HandedOverBy = from;
        Changed?.Invoke(tenantId, info);
        Line?.Invoke(tenantId, LineInfo(line));
        return "";
    }

    /// <summary>Who a chat could be handed to: everybody of the company signed in now.</summary>
    public List<string> HereNow(int tenantId) => _presence.Names(tenantId);

    /// <summary>Ends a chat: by somebody at the office, by the visitor ("visitor"), or for idleness ("idle").</summary>
    public async Task<string> EndAsync(int tenantId, int chatId, string by)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var c = await db.WebChats.FirstOrDefaultAsync(x => x.Id == chatId && x.TenantId == tenantId && !x.LeftMessage);
        if (c == null) return "That chat is not there any more.";
        if (c.EndedUtc != null) return "";
        c.EndedUtc = DateTime.UtcNow; c.EndedBy = by;
        await db.SaveChangesAsync();
        Changed?.Invoke(tenantId, Info(c, await LastTextAsync(db, chatId)));
        return "";
    }

    /// <summary>Chats whose visitor has gone and that have been quiet for <see cref="IdleEnd"/>. Run each minute.</summary>
    public async Task SweepAsync(DateTime? utcNow = null)
    {
        var cutoff = (utcNow ?? DateTime.UtcNow) - IdleEnd;
        List<(int tenant, int id)> stale;
        await using (var db = await _dbf.CreateDbContextAsync())
            stale = (await db.WebChats.Where(c => c.EndedUtc == null && !c.LeftMessage && c.LastUtc < cutoff)
                .Select(c => new { c.TenantId, c.Id }).ToListAsync())
                .Where(c => !IsVisitorHere(c.Id)).Select(c => (c.TenantId, c.Id)).ToList();
        foreach (var (tenant, id) in stale) await EndAsync(tenant, id, "idle");
    }

    // ---------------------------------------------------------------- reading

    /// <summary>The chats waiting or going on now, oldest first.</summary>
    public async Task<List<WebChatInfo>> OpenAsync(int tenantId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var chats = await db.WebChats.Where(c => c.TenantId == tenantId && c.EndedUtc == null && !c.LeftMessage).OrderBy(c => c.Id).ToListAsync();
        var result = new List<WebChatInfo>();
        foreach (var c in chats) result.Add(Info(c, await LastTextAsync(db, c.Id)));
        return result;
    }

    public async Task<List<WebChatLineInfo>> LinesAsync(int tenantId, int chatId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return (await db.WebChatLines.Where(l => l.TenantId == tenantId && l.WebChatId == chatId).OrderBy(l => l.Id).ToListAsync())
            .Select(LineInfo).ToList();
    }

    /// <summary>The most recent chats and left messages, newest first, for the history page.</summary>
    public async Task<List<WebChat>> RecentAsync(int tenantId, int take = 50)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.WebChats.Where(c => c.TenantId == tenantId).OrderByDescending(c => c.Id).Take(take).ToListAsync();
    }

    public WebChatInfo Info(WebChat c, string lastText = "") => new()
    {
        Id = c.Id, Name = c.Name, Email = c.Email, Phone = c.Phone, Page = c.Page, AnsweredBy = c.AnsweredBy,
        StartedUtc = DateTime.SpecifyKind(c.StartedUtc, DateTimeKind.Utc), Ended = c.EndedUtc != null, EndedBy = c.EndedBy,
        VisitorHere = IsVisitorHere(c.Id), LastText = lastText
    };

    public static WebChatLineInfo LineInfo(WebChatLine l) => new()
    {
        Id = l.Id, ChatId = l.WebChatId, FromVisitor = l.FromVisitor, Name = l.Name, Text = l.Text,
        SentUtc = DateTime.SpecifyKind(l.SentUtc, DateTimeKind.Utc), Note = !l.FromVisitor && l.Name.Length == 0
    };

    private static async Task<string> LastTextAsync(PlatformDb db, int chatId) =>
        await db.WebChatLines.Where(l => l.WebChatId == chatId).OrderByDescending(l => l.Id).Select(l => l.Text).FirstOrDefaultAsync() ?? "";

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static string Clip(string? s, int max) { s = (s ?? "").Trim(); return s.Length <= max ? s : s[..max]; }
}
