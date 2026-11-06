using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Hub;

/// <summary>
/// Job types on the console (2026-10-27, HANDOVER §168): every console is told which categories the company has switched off
/// and how many photos a booking needs (<c>StateMsg.CategoriesOff</c>, <c>BookingPhotos</c>), at sign-in and again the moment
/// the page is saved — so New ticket and Edit follow it without anybody signing out. What a ticket ALREADY open needs is its
/// own (<c>TicketDetail</c>, from its <c>RouteOff</c>); the service refuses a switched-off category whatever a console draws.
/// </summary>
public sealed partial class TenantHub
{
    /// <summary>The company's <c>Tenant.JobTypesOff</c> as this hub last heard it. A reference swapped whole, never changed in place.</summary>
    private string _jobTypesOff = "";

    /// <summary>The categories switched off, by value, for the console.</summary>
    private List<int> CategoriesOff() =>
        JobTypes.Categories.Where(c => !JobTypes.CategoryOn(_jobTypesOff, c)).Select(c => (int)c).ToList();

    /// <summary>Photos a booking needs now: three, or none when the company does not ask for them.</summary>
    private int BookingPhotos() => JobTypes.PhotosInRequired(_jobTypesOff) ? TicketPhotoService.Required : 0;

    /// <summary>The Job types page was saved: every console signed in is sent its state again, as an upgrade does.</summary>
    public async Task JobTypesChangedAsync(string off)
    {
        _jobTypesOff = off ?? "";
        var roster = await RosterAsync();
        List<TechConn> techs; lock (_lock) techs = _techs.ToList();
        foreach (var tc in techs)
        {
            StateMsg state; lock (_lock) state = StateFor(tc.Who, roster);
            tc.Ws.Send(state);
        }
        _log.LogInformation("[{Tenant}] Job types changed; {Consoles} console(s) told", TenantName, techs.Count);
    }
}
