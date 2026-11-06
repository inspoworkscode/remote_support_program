using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Owner;

/// <summary>
/// The dashboard (2026-10-13, stage B) — Clint: <i>"an easy to read dashboard that shows all the servers and their specs
/// that we have up, how many tenants on each, how many users per tenant and importantly how many of those are
/// technicians ... It should show us when we are nearing the recommended capacity."</i> One card per server
/// (<see cref="ServerCapacity"/> decides its level), what is waiting for an admin, and every company's headcount.
/// </summary>
public sealed class IndexModel : OwnerFleetPage
{
    public IndexModel(ControlServers servers) : base(servers) { }
}
