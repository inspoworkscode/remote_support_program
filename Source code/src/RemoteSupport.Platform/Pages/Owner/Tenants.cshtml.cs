using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Owner;

/// <summary>
/// Every company on every server (2026-10-13, stage B), grouped by server. Every change goes through
/// <see cref="ControlServers.ActAsync"/> to that server's <see cref="AdminCompanies"/> — checked against the admin's
/// level, logged with their name, followed through (a suspension emails the owners and takes the company offline).
/// </summary>
public sealed class TenantsModel : OwnerFleetPage
{
    public TenantsModel(ControlServers servers) : base(servers) { }
}
