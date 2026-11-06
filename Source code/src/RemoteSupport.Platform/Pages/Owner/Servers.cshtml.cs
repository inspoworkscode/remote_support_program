using Microsoft.AspNetCore.Mvc;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Owner;

/// <summary>
/// The servers on the dashboard (2026-10-13, stage B): add one by its address and server key, rename it, stop it taking
/// new companies, give it a measured capacity, change its key, take it off the dashboard. A Full admin or an Owner;
/// every rule is <see cref="ControlServers"/>.
/// </summary>
public sealed class ServersModel : OwnerFleetPage
{
    private readonly IConfiguration _cfg;
    public ServersModel(ControlServers servers, IConfiguration cfg) : base(servers) => _cfg = cfg;

    /// <summary>Whether THIS server can be reached by another dashboard — it has a server key of its own.</summary>
    public bool HasOwnKey => (_cfg["Platform:ServerKey"] ?? "").Length >= 32;

    private async Task<IActionResult> Said(string error, string ok) { if (error.Length > 0) Problem = error; else Message = ok; return await Render(); }

    public async Task<IActionResult> OnPostAddAsync(string? name, string? address, string? key) =>
        await Said(await Servers.AddAsync(AdminIdentity.ActorOf(User), name, address, key), "Server added. It is on the dashboard now.");

    public async Task<IActionResult> OnPostSaveAsync(int id, string? name, bool takingNew, int capacity) =>
        await Said(await Servers.UpdateAsync(AdminIdentity.ActorOf(User), id, name, takingNew, capacity), "Saved.");

    public async Task<IActionResult> OnPostKeyAsync(int id, string? key) =>
        await Said(await Servers.NewKeyAsync(AdminIdentity.ActorOf(User), id, key), "The new key works and is stored.");

    public async Task<IActionResult> OnPostRemoveAsync(int id) =>
        await Said(await Servers.RemoveAsync(AdminIdentity.ActorOf(User), id), "Taken off the dashboard. The server itself is untouched.");
}
