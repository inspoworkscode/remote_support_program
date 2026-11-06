using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// The servers the dashboard watches, and reaching them (2026-10-13, docs/NEXT-SESSION-CONTROL.md, stage B).
///
/// <para>Clint: <i>"Would that mean a sign in once into the servers that the dashboard remembers?"</i> Yes, and never
/// with anybody's password: each server has its own <c>Platform:ServerKey</c> — made by go-live, pasted here once when
/// the server is added, kept encrypted — and the control server presents it as a bearer token to that server's
/// <c>/api/control/*</c>. The admin signs in once, to the control server.</para>
///
/// <list type="bullet">
/// <item>Only a Full admin or an Owner adds, changes or removes a server; anybody signed in sees them.</item>
/// <item>A server is added only once it has ANSWERED with that key — a typo is caught at the door, not at 2 a.m.</item>
/// <item>Never over plain http to a public address (<see cref="SecureTransport"/>): the key would cross the internet bare.</item>
/// <item>A change to a company on another server goes through that server's <see cref="AdminCompanies"/> — checked,
/// logged and followed through there exactly as here — and is logged here too, by name, with the server's name.</item>
/// </list>
/// </summary>
public sealed class ControlServers
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly ServerReporter _reporter;
    private readonly AdminCompanies _act;
    private readonly UpdateService _updates;
    private readonly PlatformAdmins _admins;
    private readonly IDataProtector _keys;
    private readonly ILogger<ControlServers> _log;

    private readonly OpenPlaces _places;
    private readonly CompanyExportService _exports;

    public ControlServers(IDbContextFactory<PlatformDb> dbf, ServerReporter reporter, AdminCompanies act, PlatformAdmins admins,
        IDataProtectionProvider dp, ILogger<ControlServers> log, UpdateService updates, OpenPlaces places, CompanyExportService exports)
    {
        _dbf = dbf; _reporter = reporter; _act = act; _admins = admins; _log = log; _updates = updates; _places = places; _exports = exports;
        _keys = dp.CreateProtector("RemoteSupport.ControlServerKey.v1");
    }

    /// <summary>One server as the dashboard draws it: its row, what it said (or why it said nothing), and how full it is.</summary>
    public sealed record View(ControlServer Row, ServerReport? Report, string Error, ServerCapacity.Assessment? Assessment)
    {
        public ServerCapacity.Level Level => Assessment?.Level ?? ServerCapacity.Level.Unreachable;
    }

    // =============================================================================================
    // Reading them
    // =============================================================================================

    /// <summary>This server's own row, made the first time it is asked for.</summary>
    public async Task<ControlServer> LocalRowAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var row = await db.ControlServers.FirstOrDefaultAsync(s => s.IsLocal);
        if (row != null) return row;
        row = new ControlServer { IsLocal = true, Name = _admins.ServerName, AddedBy = "this server" };
        db.ControlServers.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    /// <summary>Every server: this one first, then the rest by name, each asked at the same time (10 s each at most).</summary>
    public async Task<List<View>> FleetAsync()
    {
        await LocalRowAsync();
        await using var db = await _dbf.CreateDbContextAsync();
        var rows = await db.ControlServers.OrderByDescending(s => s.IsLocal).ThenBy(s => s.Name).ToListAsync();
        var views = await Task.WhenAll(rows.Select(ViewAsync));
        // Remember what each said, for the next look and for placement (stage C).
        foreach (var v in views)
        {
            v.Row.LastError = v.Error;
            if (v.Report != null) { v.Row.LastReportUtc = v.Report.AtUtc; if (v.Report.Region.Length > 0) v.Row.Region = v.Report.Region; }
        }
        await db.SaveChangesAsync();
        return views.ToList();
    }

    private async Task<View> ViewAsync(ControlServer row)
    {
        ServerReport? r; string error;
        if (row.IsLocal) { r = await _reporter.BuildAsync(); error = ""; }
        else (r, error) = await FetchAsync(row.Address, Unprotect(row.KeyEnc));
        return new View(row, r, error, r == null ? null : ServerCapacity.Assess(r, row.CapacityConnections));
    }

    /// <summary>Asks one server for its report. The error says what a person would check: the address, the key, the server.</summary>
    public static async Task<(ServerReport? report, string error)> FetchAsync(string address, string key)
    {
        if (SecureTransport.Refusal(address) is { } insecure) return (null, insecure);
        if (key.Length == 0) return (null, "No server key is stored for it.");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, address.TrimEnd('/') + "/api/control/status");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var res = await Http.SendAsync(req);
            if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized) return (null, "It refused the server key.");
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return (null, "It has no server key set (Platform:ServerKey), or is too old for the dashboard.");
            if (res.StatusCode == System.Net.HttpStatusCode.TooManyRequests) return (null, "It is refusing this address for a while after wrong keys.");
            if (!res.IsSuccessStatusCode) return (null, $"It answered {(int)res.StatusCode}.");
            var r = await res.Content.ReadFromJsonAsync<ServerReport>(Json);
            return r == null ? (null, "It answered with nothing.") : (r, "");
        }
        catch (TaskCanceledException) { return (null, "It did not answer within 10 seconds."); }
        catch (HttpRequestException ex) { return (null, "Could not reach it: " + ex.Message); }
        catch (JsonException) { return (null, "Its answer was not a server report."); }
    }

    // =============================================================================================
    // Adding, changing, removing
    // =============================================================================================

    public async Task<string> AddAsync(PlatformAdmins.Actor by, string? name, string? address, string? key)
    {
        if (!PlatformAdmins.MayRun(by.Level)) return "Only a Full admin or an Owner can add a server.";
        address = (address ?? "").Trim().TrimEnd('/'); key = (key ?? "").Trim(); name = (name ?? "").Trim();
        if (!Uri.TryCreate(address, UriKind.Absolute, out var u) || u.AbsolutePath.Trim('/').Length > 0)
            return "Give the server's address on its own, like https://za2.theremotesupportprogram.com";
        if (SecureTransport.Refusal(address) is { } insecure) return insecure;
        if (key.Length < 32) return "Paste the whole server key — go-live printed it, and it is in that server's settings as Platform:ServerKey.";
        await using var db = await _dbf.CreateDbContextAsync();
        var lower = address.ToLowerInvariant();
        if (await db.ControlServers.AnyAsync(s => s.Address.ToLower() == lower)) return "That server is already on the dashboard.";
        var (report, error) = await FetchAsync(address, key);
        if (report == null) return "Not added — " + error;
        if (report.Name == _admins.ServerName && report.Version == BuildStamp.Value && report.StartedUtc == (await _reporter.BuildAsync()).StartedUtc)
            return "That is this server — it is on the dashboard already.";
        var row = new ControlServer
        {
            Name = name.Length > 0 ? name : report.Name, Address = address, KeyEnc = _keys.Protect(key),
            Region = report.Region, LastReportUtc = report.AtUtc, AddedBy = by.Name
        };
        db.ControlServers.Add(row);
        await db.SaveChangesAsync();
        await _admins.LogAsync(by.Name, $"Added the server {row.Name} ({address}, {Pricing.For(report.Region).Name}, {report.Companies.Count} companies)");
        return "";
    }

    public async Task<string> UpdateAsync(PlatformAdmins.Actor by, int id, string? name, bool takingNew, int capacity)
    {
        if (!PlatformAdmins.MayRun(by.Level)) return "Only a Full admin or an Owner can change a server.";
        await using var db = await _dbf.CreateDbContextAsync();
        var row = await db.ControlServers.FindAsync(id);
        if (row == null) return "That server is no longer on the dashboard.";
        name = (name ?? "").Trim();
        if (name.Length == 0 || name.Length > 40) return "Give it a short name, like za2.";
        capacity = Math.Clamp(capacity, 0, 10_000_000);
        var changes = new List<string>();
        if (name != row.Name) changes.Add($"name {row.Name} → {name}");
        if (takingNew != row.TakingNewCompanies) changes.Add(takingNew ? "taking new companies" : "NOT taking new companies");
        if (capacity != row.CapacityConnections) changes.Add(capacity == 0 ? "capacity from its memory" : $"capacity {capacity:N0} connections");
        if (changes.Count == 0) return "";
        row.Name = name; row.TakingNewCompanies = takingNew; row.CapacityConnections = capacity;
        await db.SaveChangesAsync();
        await _admins.LogAsync(by.Name, $"Server {row.Name}: {string.Join(", ", changes)}");
        return "";
    }

    public async Task<string> NewKeyAsync(PlatformAdmins.Actor by, int id, string? key)
    {
        if (!PlatformAdmins.MayRun(by.Level)) return "Only a Full admin or an Owner can change a server.";
        key = (key ?? "").Trim();
        await using var db = await _dbf.CreateDbContextAsync();
        var row = await db.ControlServers.FindAsync(id);
        if (row == null || row.IsLocal) return "That server is no longer on the dashboard.";
        var (report, error) = await FetchAsync(row.Address, key);
        if (report == null) return "Key not changed — " + error;
        row.KeyEnc = _keys.Protect(key);
        await db.SaveChangesAsync();
        await _admins.LogAsync(by.Name, $"Server {row.Name}: new server key");
        return "";
    }

    public async Task<string> RemoveAsync(PlatformAdmins.Actor by, int id)
    {
        if (!PlatformAdmins.MayRun(by.Level)) return "Only a Full admin or an Owner can remove a server.";
        await using var db = await _dbf.CreateDbContextAsync();
        var row = await db.ControlServers.FindAsync(id);
        if (row == null) return "That server is no longer on the dashboard.";
        if (row.IsLocal) return "This server is the dashboard itself; it cannot be removed from it.";
        db.ControlServers.Remove(row);
        await db.SaveChangesAsync();
        await _admins.LogAsync(by.Name, $"Removed the server {row.Name} ({row.Address}) from the dashboard — the server itself is untouched");
        return "";
    }

    // =============================================================================================
    // Changing a company, wherever it is
    // =============================================================================================

    /// <summary>
    /// One change to one company, on whichever server holds it. <paramref name="serverId"/> 0 (or this server's row) is
    /// here; any other is relayed. The actions are <see cref="Dispatch"/>'s.
    /// </summary>
    public async Task<AdminCompanies.Done> ActAsync(PlatformAdmins.Actor by, int serverId, string action, int id, IDictionary<string, string?> args)
    {
        if (!PlatformAdmins.MayManage(by.Level)) return AdminCompanies.Done.No(AdminCompanies.ViewerRefusal);
        ControlServer? row = null;
        if (serverId != 0)
        {
            await using var db = await _dbf.CreateDbContextAsync();
            row = await db.ControlServers.FindAsync(serverId);
            if (row == null) return AdminCompanies.Done.No("That server is no longer on the dashboard.");
        }
        if (row == null || row.IsLocal) return await Dispatch(_act, by, action, id, args, _updates, _places, _exports);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, row.Address.TrimEnd('/') + "/api/control/act")
            {
                Content = JsonContent.Create(new ControlActRequest(action, id, args.ToDictionary(k => k.Key, k => k.Value), by.Name, (int)by.Level, _admins.ServerName), options: Json)
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Unprotect(row.KeyEnc));
            using var res = await Http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return AdminCompanies.Done.No($"{row.Name} refused it ({(int)res.StatusCode}).");
            var answer = await res.Content.ReadFromJsonAsync<ControlActAnswer>(Json) ?? new ControlActAnswer("No answer.", "", "");
            if (answer.Error.Length > 0) return AdminCompanies.Done.No($"{row.Name}: {answer.Error}");
            // An offered place's answer can carry its link (when the email did not go): shown once, never logged.
            await _admins.LogAsync(by.Name, action == "offer-place" ? Describe(action, args) : $"{Describe(action, args)} — {answer.Said}", answer.Company, row.Name);
            return new AdminCompanies.Done("", $"{row.Name}: {answer.Said}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return AdminCompanies.Done.No($"Could not reach {row.Name}: {ex.Message}");
        }
    }

    /// <summary>The ONE list of what may be done to a company from the dashboard — here, and on a server asked by another.</summary>
    public static async Task<AdminCompanies.Done> Dispatch(AdminCompanies act, PlatformAdmins.Actor by, string action, int id, IDictionary<string, string?> a,
        UpdateService? updates = null, OpenPlaces? places = null, CompanyExportService? exports = null)
    {
        string? A(string k) => a.TryGetValue(k, out var v) ? v : null;
        // Auto-update (2026-10-16): the release THIS server hands out — moved on, paused or resumed; a Full admin or an Owner.
        if (action.StartsWith("update-") && updates != null)
        {
            var why = action switch
            {
                "update-ring" => int.TryParse(A("ring"), out var ring) ? await updates.SetRingAsync(by, ring) : "Choose who to offer it to.",
                "update-pause" => await updates.PauseAsync(by),
                "update-resume" => await updates.ResumeAsync(by),
                _ => "That is not something the dashboard does."
            };
            return why.Length > 0 ? AdminCompanies.Done.No(why) : new AdminCompanies.Done("", Describe(action, a));
        }
        return action switch
        {
            "approve" => await act.ApproveAsync(by, id),
            "reject" => await act.RejectAsync(by, id),
            "status" => Enum.TryParse<TenantStatus>(A("status"), out var s) ? await act.SetStatusAsync(by, id, s) : AdminCompanies.Done.No("Choose a status."),
            "seats" => int.TryParse(A("seats"), out var n) ? await act.SetSeatsAsync(by, id, n) : AdminCompanies.Done.No("Give a number of seats."),
            // More seats, on request (2026-10-25): the most this company may have, or not this time.
            "allow-seats" => int.TryParse(A("most"), out var most) ? await act.AllowSeatsAsync(by, id, most) : AdminCompanies.Done.No("Give the most seats they may have."),
            "decline-seats" => await act.DeclineSeatsAsync(by, id, A("reason")),
            "billed" => await act.SetBilledAsync(by, id, A("notBilled") == "true"),
            "country" => await act.SetCountryAsync(by, id, A("country")),
            "remove-waiting" => await act.RemoveWaitingAsync(by, id),
            "email" => await act.EmailOwnersAsync(by, id, A("subject"), A("message"), A("replyTo")),
            // A company's data, made by us for its owners (2026-10-22) — for one that cannot sign in to make its own.
            "export" when exports != null => await exports.StartForOwnersAsync(by, id, A("recordings") == "true"),
            // Five companies at a time (2026-10-22, OpenPlaces): the number of places on THIS server, and offering one.
            "places" when places != null => int.TryParse(A("places"), out var p)
                ? await places.SetLimitAsync(by, p) is { Length: > 0 } why ? AdminCompanies.Done.No(why) : new AdminCompanies.Done("", p == 0 ? "No limit on places." : $"{p} places.")
                : AdminCompanies.Done.No("Give a number of places (0 for no limit)."),
            "offer-place" when places != null => await places.OfferAsync(by, id) is var o && o.error.Length > 0
                ? AdminCompanies.Done.No(o.error)
                : new AdminCompanies.Done("", $"Place offered, held for {OpenPlaces.OfferDays} days. {o.emailed}{(o.emailed.StartsWith("Emailed") ? "" : " " + o.link)}"),
            _ => AdminCompanies.Done.No("That is not something the dashboard does.")
        };
    }

    private static string Describe(string action, IDictionary<string, string?> d) { var a = new Dictionary<string, string?>(d); return action switch
    {
        "status" => $"Status → {a.GetValueOrDefault("status")}",
        "seats" => $"Seats → {a.GetValueOrDefault("seats")}",
        "allow-seats" => $"Most seats → {a.GetValueOrDefault("most")}",
        "decline-seats" => "Did not allow more seats",
        "billed" => a.GetValueOrDefault("notBilled") == "true" ? "Marked not billed" : "Marked billed",
        "country" => $"Country → {a.GetValueOrDefault("country")}",
        "remove-waiting" => "Removed from the waiting list",
        "places" => $"Open places → {(a.GetValueOrDefault("places") is "0" ? "no limit" : a.GetValueOrDefault("places"))}",
        "offer-place" => "Offered a place from the waiting list",
        "email" => $"Emailed the owners: \"{a.GetValueOrDefault("subject")}\"",
        "export" => a.GetValueOrDefault("recordings") == "true" ? "Export started, with recordings" : "Export started",
        "update-ring" => $"Update offered to {UpdateService.RingName(int.TryParse(a.GetValueOrDefault("ring"), out var r) ? r : 2).ToLowerInvariant()}",
        "update-pause" => "Update paused",
        "update-resume" => "Update resumed",
        _ => char.ToUpperInvariant(action[0]) + action[1..]
    }; }

    private string Unprotect(string enc)
    {
        if (enc.Length == 0) return "";
        try { return _keys.Unprotect(enc); }
        catch (System.Security.Cryptography.CryptographicException) { return ""; }
    }
}

/// <summary>What the control server asks another server to do to a company.</summary>
public sealed record ControlActRequest(string Action, int Id, Dictionary<string, string?> Args, string By, int Level, string From);
/// <summary>What that server answers: an error, or what it did and to which company.</summary>
public sealed record ControlActAnswer(string Error, string Said, string Company);
