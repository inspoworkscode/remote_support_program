using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Owner;

/// <summary>Signups waiting for approval, and the waiting lists — on every server (2026-10-13; on `/owner` before).</summary>
public sealed class ApprovalsModel : OwnerFleetPage
{
    public ApprovalsModel(ControlServers servers) : base(servers) { }
}
