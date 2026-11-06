using Microsoft.AspNetCore.Mvc.Filters;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Owner;

/// <summary>
/// Auto-update (2026-10-16, stage C): every server's release, how far it has gone out, and how each ring's machines stand.
/// Any admin sees it; a Full admin or an Owner moves a release on, pauses or resumes it — on whichever server holds it,
/// through <see cref="ControlServers"/> like every other change (<c>update-ring</c>, <c>update-pause</c>, <c>update-resume</c>).
/// Every rule is <see cref="UpdateService"/>.
/// </summary>
public sealed class UpdatesModel : OwnerFleetPage
{
    private readonly UpdateService _updates;
    public UpdatesModel(ControlServers servers, UpdateService updates) : base(servers) => _updates = updates;

    /// <summary>The newest steps machines on THIS server reported. Another server's are on its own page.</summary>
    public List<(DeviceUpdateEvent e, string company)> Recent { get; private set; } = new();

    /// <summary>After either handler (the page, or an action posted from it): the history as it is now.</summary>
    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        await next();
        Recent = await _updates.RecentAsync();
    }
}
