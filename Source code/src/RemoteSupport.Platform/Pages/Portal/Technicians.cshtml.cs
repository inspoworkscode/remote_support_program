using Microsoft.AspNetCore.Mvc;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

public sealed class TechniciansModel : PortalPage
{
    private readonly AccountLinks _links;
    public TechniciansModel(TenantService t, AccountLinks links) : base(t) => _links = links;
    public List<Technician> List { get; private set; } = new();

    /// <summary>
    /// A link the mail could not deliver (no platform mailbox yet, or the mail server refused it),
    /// shown ONCE to the supervisor who made it so they can pass it on. It is still one person's,
    /// once, and still leads to that person choosing their own password.
    /// </summary>
    public string HandOnLink { get; private set; } = "";

    /// <summary>Whether the person looking may make owners and change owners' rows — owners alone.</summary>
    public bool CanChangeOwner => Permissions.CanChangeOwner(MyRole);

    /// <summary>Whether this is the only owner left — their Owner box locked on, and no Remove.</summary>
    public bool LastOwner(Technician x) => x.IsOwner && !List.Any(o => o.IsOwner && !o.Disabled && o.Id != x.Id);

    /// <summary>Whether the one free owner-only login is in use already, so another owner on their own costs a seat.</summary>
    public bool FreeOwnerTaken => List.Any(o => !o.Disabled && o.Role == TenantRole.Owner);

    private async Task<IActionResult> Render() { List = await Tenants.TechniciansAsync(TenantId); return Page(); }

    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? await Render() : Redirect("/login");

    /// <summary>The form's value for each box. Owner is only ever offered on the owner's own row.</summary>
    public static string ValueOf(TenantRole box) => box switch
    {
        TenantRole.Admin => "supervisor", TenantRole.Billing => "office", TenantRole.Owner => "owner", _ => "technician"
    };

    /// <summary>
    /// Every ticked box, as one set. A single value still parses, and so do the combined forms a
    /// page loaded before 2026-09-29 posts ("owner-supervisor"), or before 2026-09-24 ("admin",
    /// "billing") — a stale tab should do what it says, not something else.
    /// </summary>
    public static TenantRole Parse(IEnumerable<string>? roles)
    {
        var r = TenantRole.None;
        foreach (var v in roles ?? Array.Empty<string>())
            r |= v switch
            {
                "supervisor" or "admin" => TenantRole.Admin,
                "office" or "billing" => TenantRole.Billing,
                "owner" => TenantRole.Owner,
                "owner-supervisor" => TenantRole.OwnerSupervisor,
                "technician" => TenantRole.Technician,
                _ => TenantRole.None
            };
        return r;
    }

    /// <summary>
    /// Adds a person by INVITATION (2026-10-01): no password is typed here. They are mailed a link to
    /// choose their own and set up two-factor, and — with a seat — the console download link.
    /// </summary>
    public async Task<IActionResult> OnPostAddAsync(string name, string email, string[] role, string? agreed = null)
    {
        if (!await LoadAsync()) return Redirect("/login");
        // Only an owner may add another owner (2026-10-08); anybody else's Owner box is dropped (the service refuses it too).
        var r = CanChangeOwner ? Parse(role) : Parse(role) & ~TenantRole.Owner;
        // Past the package, within seats we allowed (2026-10-25): the owner is shown the new price and asked first.
        // Everybody else goes straight to the service, which refuses them with the figure and "ask the owner".
        if (agreed == null && Permissions.CanAcceptPrice(MyRole) && await Tenants.PriceRiseAsync(TenantId, r) is { } rise)
        {
            // Never ask anybody to agree to a price for a person who would be refused anyway.
            if (await Tenants.NewPersonProblemAsync(TenantId, email, name, r) is { } problem) { Error = problem; return await Render(); }
            Asking = new PriceQuestion(rise, $"Adding {(string.IsNullOrWhiteSpace(name) ? email : name.Trim())}", "Add", name, email, role);
            return await Render();
        }
        var (tech, err) = await Tenants.AddTechnicianAsync(TenantId, email, null, name, r, MyRole, agreed, AgreedBy);
        if (tech == null) { Error = err; return await Render(); }
        await Tell(tech, AccountTokenPurpose.Invite,
            $"{tech.DisplayName} added as {tech.RoleLabel}. An invitation has gone to {tech.Email} to choose a password and set up two-factor sign-in"
            + (tech.CanUseConsole ? ", with the link to download the console." : "."));
        return await Render();
    }

    /// <summary>Somebody who has not finished setting up: a fresh invitation, the old link no longer working.</summary>
    public async Task<IActionResult> OnPostInviteAsync(string email)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var target = await Target(email);
        if (target == null) { Error = "Not found."; return await Render(); }
        if (!target.AwaitingSetup) { Error = $"{target.DisplayName} has already set up their account. Use Send reset link if they cannot sign in."; return await Render(); }
        await Tell(target, AccountTokenPurpose.Invite, $"A new invitation has gone to {target.Email}. The earlier link no longer works.");
        return await Render();
    }

    /// <summary>
    /// A link for them to choose a NEW password (2 hours). Replaces typing a password for somebody:
    /// a supervisor no longer knows anyone's. Their current password keeps working until they use it.
    /// </summary>
    public async Task<IActionResult> OnPostSendResetAsync(string email)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var target = await Target(email);
        if (target == null) { Error = "Not found."; return await Render(); }
        // A supervisor resetting the owner's password is a way into the owner's account.
        if (target.IsOwner && !CanChangeOwner) { Error = "Only an owner can reset an owner's password."; return await Render(); }
        await Tell(target, target.AwaitingSetup ? AccountTokenPurpose.Invite : AccountTokenPurpose.Reset,
            $"A link to choose a new password has gone to {target.Email}. It works once, for 2 hours; their current password works until then.");
        return await Render();
    }

    private async Task<Technician?> Target(string? email) =>
        (await Tenants.TechniciansAsync(TenantId)).FirstOrDefault(x => x.Email == (email ?? "").Trim().ToLowerInvariant());

    private async Task Tell(Technician who, AccountTokenPurpose purpose, string sentMessage)
    {
        var sent = await _links.IssueAsync(who.Id, purpose, Me);
        if (sent.Mailed) { Message = sentMessage; return; }
        if (sent.Link.Length == 0) { Error = sent.Why; return; }
        Message = $"{who.DisplayName}: the email could not be sent ({sent.Why}). Send them this link yourself — " +
                  $"it works once, for {(purpose == AccountTokenPurpose.Invite ? "72" : "2")} hours, and only lets them choose their own password:";
        HandOnLink = sent.Link;
    }

    public async Task<IActionResult> OnPostRoleAsync(string email, string[] role, string? agreed = null)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var r = Parse(role);
        // An owner's row can go from no seat to a seat (ticking a box beside Owner), and so can a row given Owner as a second
        // owner-only login: past the package that is a price to agree; past what we allowed, the service refuses it.
        if (agreed == null && Permissions.CanAcceptPrice(MyRole) && await Tenants.PriceRiseAsync(TenantId, r, email) is { } rise)
        {
            var who = await Target(email);
            Asking = new PriceQuestion(rise, $"Giving {who?.DisplayName ?? email} a seat", "Role", who?.DisplayName ?? "", email, role);
            return await Render();
        }
        var err = await Tenants.SetRoleAsync(TenantId, email, r, MyRole, agreed, AgreedBy);
        if (err.Length > 0) Error = err; else Message = "Roles updated.";
        return await Render();
    }

    /// <summary>"Don't do it" on the price question: nothing is changed, and the page says so.</summary>
    public async Task<IActionResult> OnPostDeclineAsync(string? doing)
    {
        if (!await LoadAsync()) return Redirect("/login");
        Message = $"{(string.IsNullOrWhiteSpace(doing) ? "That change" : doing)} — not done. Nobody was added and your price is unchanged.";
        return await Render();
    }

    /// <summary>
    /// The owner being asked to agree to a higher price before a change is made (2026-10-05, back 2026-10-25 for seats we
    /// allowed past the package). The page draws it at the top, with the change's own fields carried in hidden inputs so
    /// Accept posts the same change again — now naming the figure agreed to (<see cref="Pricing.PriceRise.Token"/>).
    /// </summary>
    public sealed record PriceQuestion(Pricing.PriceRise Rise, string Doing, string Handler, string Name, string Email, string[] Roles);
    public PriceQuestion? Asking { get; private set; }

    /// <summary>The bill as it stands, for the heading and the add form.</summary>
    public Pricing.Bill Bill => Pricing.BillFor(Tenant, Pricing.PeopleInSeats(List));

    /// <summary>The most seats this company may fill — five, or what we allowed (2026-10-25).</summary>
    public int Most => Pricing.MostSeatsFor(Tenant);

    /// <summary>Whether every seat this company may fill is taken, so the Add form is replaced by the reason — never for Anywhere IT (<see cref="Pricing.Limited"/>).</summary>
    public bool Full => Pricing.Limited(Tenant) && Bill.People >= Most;

    /// <summary>Who agreed, as the record keeps it.</summary>
    private string AgreedBy => $"{Me} ({User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value})";

    public async Task<IActionResult> OnPostRemoveAsync(string email)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var err = await Tenants.RemoveTechnicianAsync(TenantId, email, MyRole);
        if (err.Length > 0) Error = err; else Message = "Removed.";
        return await Render();
    }

    /// <summary>
    /// Lost phone (2026-10-01): clears their authenticator so they set one up again at their next
    /// sign-in, and signs them out everywhere. The owner's only by the owner, as with the password.
    /// </summary>
    public async Task<IActionResult> OnPostResetTwoFactorAsync(string email)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (await IsOwnerRowGuarded(email)) { Error = "Only an owner can reset an owner's two-factor sign-in."; return await Render(); }
        var err = await Tenants.ResetTwoFactorAsync(TenantId, email ?? "");
        if (err.Length > 0) Error = err; else Message = "Two-factor sign-in reset. They have been signed out everywhere, and set up their authenticator again when they next sign in.";
        return await Render();
    }

    /// <summary>A laptop left signed in, or a sign-in they did not make: out of every console, session and browser, now.</summary>
    public async Task<IActionResult> OnPostSignOutAsync(string email)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (await IsOwnerRowGuarded(email)) { Error = "Only an owner can sign an owner out."; return await Render(); }
        var err = await Tenants.SignOutEverywhereAsync(TenantId, email ?? "");
        if (err.Length > 0) Error = err; else Message = "Signed out of every console, session and browser.";
        return await Render();
    }

    private async Task<bool> IsOwnerRowGuarded(string? email)
    {
        var target = (await Tenants.TechniciansAsync(TenantId)).FirstOrDefault(x => x.Email == (email ?? "").Trim().ToLowerInvariant());
        return target is { IsOwner: true } && !CanChangeOwner;
    }
}
