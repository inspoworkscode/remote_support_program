using System.Text;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Hub;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// What a platform admin does to a company (2026-10-13, docs/NEXT-SESSION-CONTROL.md, stage A) — the ONE path,
/// for the owner pages now and for another server's dashboard reaching this one later (stage B), so a change
/// is checked, logged and followed through the same way whoever asks.
///
/// <list type="bullet">
/// <item><b>A Manager or above</b>; a Viewer is refused here, not only by a missing button.</item>
/// <item><b>Every change is logged</b> with the admin's name (<see cref="PlatformAdmins.LogAsync"/>).</item>
/// <item><b>Suspended is emailed to the company's owners</b>, and so is being made active again after it
/// (Clint's answer, 2026-10-13).</item>
/// <item><b>A company that can no longer use the service is turned away NOW</b> — its consoles signed out, its
/// machines disconnected, its sessions ended (<see cref="TenantHub.TurnAway"/>). The page always said
/// "Suspended blocks logins and devices immediately"; it blocked only the NEXT sign-in and the NEXT connect,
/// and a machine already connected stayed connected until it dropped for some other reason.</item>
/// </list>
/// </summary>
public sealed class AdminCompanies
{
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly TenantService _tenants;
    private readonly PlatformAdmins _admins;
    private readonly PlatformMailService _mail;
    private readonly HubRegistry _hubs;
    private readonly AuthService _auth;
    private readonly ILogger<AdminCompanies> _log;

    public AdminCompanies(IDbContextFactory<PlatformDb> dbf, TenantService tenants, PlatformAdmins admins, PlatformMailService mail,
        HubRegistry hubs, AuthService auth, ILogger<AdminCompanies> log, IConfiguration cfg)
    { _dbf = dbf; _tenants = tenants; _admins = admins; _mail = mail; _hubs = hubs; _auth = auth; _log = log; _publicUrl = (cfg["Platform:PublicUrl"] ?? "").TrimEnd('/'); }

    /// <summary>Our public address, for the sign-in link in an approval email.</summary>
    private readonly string _publicUrl;

    public const string ViewerRefusal = "Your admin level is Viewer — you can see everything but change nothing.";

    /// <summary>A change's answer: empty error and what to tell the admin, or the error.</summary>
    public sealed record Done(string Error, string Said = "")
    {
        public static Done No(string e) => new(e);
    }

    public async Task<Done> ApproveAsync(PlatformAdmins.Actor by, int id)
    {
        if (!PlatformAdmins.MayManage(by.Level)) return Done.No(ViewerRefusal);
        var t = await _tenants.ByIdAsync(id);
        if (t == null) return Done.No("That company no longer exists.");
        if (t.Status != TenantStatus.Pending) return Done.No($"{t.CompanyName} is not waiting for approval.");
        // A full-version signup whose card is at PayFast (2026-10-13): PayFast's notice switches it on, and a person must not
        // switch on a company that has not paid. Hiding the button on Approvals is not protection; this is.
        await using (var db = await _dbf.CreateDbContextAsync())
            if (await db.PayfastSubscriptions.AnyAsync(s => s.TenantId == id && s.Purpose == "signup" && s.Status == PayfastStatus.AwaitingCard))
                return Done.No($"{t.CompanyName} is waiting for its card at PayFast — it switches on by itself when PayFast confirms it.");
        var e = await _tenants.ApproveAsync(id);
        if (e.Length > 0) return Done.No(e);
        await _admins.LogAsync(by.Name, FreePlan.IsFree(t.Plan) ? "Approved (Free version, active)" : FreePlan.Is(t.Plan) ? "Approved (Basic version) — 14-day trial started" : "Approved — 14-day trial started", t.CompanyName);
        // The signup page always said "we'll email you as soon as your account is approved" — and until 2026-10-13 nothing
        // did. It does now.
        var said = FreePlan.IsFree(t.Plan) ? $"{t.CompanyName} approved — Free version, active." : FreePlan.Is(t.Plan) ? $"{t.CompanyName} approved — Basic version, trial started." : $"{t.CompanyName} approved — trial started.";
        return new("", said + " " + await TellAsync(by, (await _tenants.ByIdAsync(id)) ?? t, Approved(t, _publicUrl)));
    }

    public async Task<Done> RejectAsync(PlatformAdmins.Actor by, int id)
    {
        if (!PlatformAdmins.MayManage(by.Level)) return Done.No(ViewerRefusal);
        var t = await _tenants.ByIdAsync(id);
        if (t == null) return Done.No("That company no longer exists.");
        if (t.Status != TenantStatus.Pending) return Done.No($"{t.CompanyName} is not waiting for approval.");
        var e = await _tenants.SetStatusAsync(id, TenantStatus.Rejected);
        if (e.Length > 0) return Done.No(e);
        await _admins.LogAsync(by.Name, "Rejected the signup", t.CompanyName);
        return new("", $"{t.CompanyName} rejected.");
    }

    /// <summary>Active, Suspended, Trial, Closed… — with the email and the turning away that go with it.</summary>
    /// <param name="notice">The email to send instead of the usual one (PayFast stage D: suspended for a payment, back because it came, closed at the end of a cancelled subscription). With it, the owners are told whatever the change.</param>
    public async Task<Done> SetStatusAsync(PlatformAdmins.Actor by, int id, TenantStatus status, (string subject, string body)? notice = null)
    {
        if (!PlatformAdmins.MayManage(by.Level)) return Done.No(ViewerRefusal);
        if (!Enum.IsDefined(status)) return Done.No("Choose a status.");
        var t = await _tenants.ByIdAsync(id);
        if (t == null) return Done.No("That company no longer exists.");
        var was = t.Status;
        if (was == status) return new("", $"{t.CompanyName} is already {status}.");
        var e = await _tenants.SetStatusAsync(id, status);
        if (e.Length > 0) return Done.No(e);
        await _admins.LogAsync(by.Name, $"Status {was} → {status}", t.CompanyName);

        var now = await _tenants.ByIdAsync(id);
        var said = $"{t.CompanyName} is now {status}.";
        if (now != null && !now.IsServiceEnabled)
        {
            _auth.SignOutCompany(id);
            var (consoles, machines) = _hubs.TurnAway(id, status == TenantStatus.Suspended
                ? $"{t.CompanyName}'s account has been suspended."
                : $"{t.CompanyName}'s support service is not active.");
            if (consoles + machines > 0) said += $" {Plural(consoles, "console")} signed out and {Plural(machines, "machine connection")} closed.";
        }
        if (notice is { } own) said += " " + await TellAsync(by, now ?? t, own);
        else if (status == TenantStatus.Suspended) said += " " + await TellAsync(by, now ?? t, Suspended(t));
        else if (was == TenantStatus.Suspended && now is { IsServiceEnabled: true }) said += " " + await TellAsync(by, now, Restored(now));
        return new("", said.Trim());
    }

    public async Task<Done> SetSeatsAsync(PlatformAdmins.Actor by, int id, int seats)
    {
        if (!PlatformAdmins.MayManage(by.Level)) return Done.No(ViewerRefusal);
        var t = await _tenants.ByIdAsync(id);
        if (t == null) return Done.No("That company no longer exists.");
        seats = Math.Clamp(seats, 1, Pricing.MaxSeats);
        if (t.Seats == seats) return new("", $"{t.CompanyName} already has {seats} seats in the package.");
        var e = await _tenants.UpdateBrandingAsync(id, x => x.Seats = seats);
        if (e.Length > 0) return Done.No(e);
        // Fewer in the package can make seats extras (2026-10-25), so PayFast may need the new amount.
        _tenants.PriceMayHaveChanged(id);
        await _admins.LogAsync(by.Name, $"Seats in the package {t.Seats} → {seats}", t.CompanyName);
        return new("", $"{t.CompanyName}: {seats} seats in the package.");
    }

    // ---- more seats, on request (2026-10-25) -----------------------------------------------------
    // Clint: "all tenants only get the 5 seats option, but they can request more seats, and if I decide it's fine, I right
    // click on their tenant, allow more seats, then they can add more seats". Allowing changes no price: the owner still
    // agrees to each seat past the package on the People page (TenantService), and PayFast follows from there.

    /// <summary>
    /// Let this company have up to <paramref name="most"/> seats in all — or, with its package's number (or 0), take the
    /// allowance back. Nobody already in a seat is removed. Clears what it asked for, and emails its owners.
    /// </summary>
    public async Task<Done> AllowSeatsAsync(PlatformAdmins.Actor by, int id, int most)
    {
        if (!PlatformAdmins.MayManage(by.Level)) return Done.No(ViewerRefusal);
        var t = await _tenants.ByIdAsync(id);
        if (t == null) return Done.No("That company no longer exists.");
        if (FreePlan.Is(t.Plan)) return Done.No($"{t.CompanyName} is on {FreePlan.TheNameOf(t.Plan)}, which is one person. It upgrades to have more.");
        if (t.NotBilled) return Done.No($"{t.CompanyName} is not billed, so it is not limited — there is nothing to allow.");
        int package = Pricing.SeatsAllowed(t);
        if (most > Pricing.MostAllowed) return Done.No($"{Pricing.MostAllowed} seats is the most one company may be allowed here.");
        if (most < package) most = package;
        int before = Pricing.MostSeatsFor(t), inSeats = Pricing.PeopleInSeats(await _tenants.TechniciansAsync(id));
        bool asked = t.SeatsAskedFor > 0;
        if (most == before && !asked) return new("", $"{t.CompanyName} may already have {most} seats.");
        var e = await _tenants.UpdateBrandingAsync(id, x =>
        {
            x.MostSeats = most > package ? most : 0;
            x.SeatsAskedFor = 0; x.SeatsAskedNote = ""; x.SeatsAskedBy = ""; x.SeatsAskedUtc = null;
        });
        if (e.Length > 0) return Done.No(e);
        await _admins.LogAsync(by.Name, $"Most seats {before} → {most}", t.CompanyName);
        t.MostSeats = most > package ? most : 0;
        var told = most == before ? "" : " " + await TellAsync(by, t, SeatsAllowedMail(t, most, inSeats));
        var over = most < inSeats ? $" {inSeats} are in use now; nobody was removed, and nobody more can be added until they are below {most}." : "";
        return new("", (most > package ? $"{t.CompanyName} may now have up to {most} seats." : $"{t.CompanyName} is back to its {package} seats.") + over + told);
    }

    /// <summary>We are not allowing what they asked for, this time: the ask is cleared, and the owners told — with our reason, when one is given.</summary>
    public async Task<Done> DeclineSeatsAsync(PlatformAdmins.Actor by, int id, string? reason)
    {
        if (!PlatformAdmins.MayManage(by.Level)) return Done.No(ViewerRefusal);
        reason = (reason ?? "").Trim();
        if (reason.Length > 1000) return Done.No("Keep the reason to 1,000 characters.");
        var t = await _tenants.ByIdAsync(id);
        if (t == null) return Done.No("That company no longer exists.");
        if (t.SeatsAskedFor == 0) return Done.No($"{t.CompanyName} has not asked for more seats.");
        int asked = t.SeatsAskedFor;
        var e = await _tenants.UpdateBrandingAsync(id, x => { x.SeatsAskedFor = 0; x.SeatsAskedNote = ""; x.SeatsAskedBy = ""; x.SeatsAskedUtc = null; });
        if (e.Length > 0) return Done.No(e);
        await _admins.LogAsync(by.Name, $"Did not allow {asked} seats", t.CompanyName);
        return new("", $"{t.CompanyName}'s request for {asked} seats was declined. " + await TellAsync(by, t, SeatsDeclinedMail(t, asked, reason)));
    }

    /// <summary>
    /// A company asked for more seats (<see cref="TenantService.AskForSeatsAsync"/>): every Full admin and Owner of THIS
    /// server is emailed, as the dashboard's warnings are. Owner → Approvals lists it whether or not the email went.
    /// </summary>
    public async Task<string> TellUsOfSeatRequestAsync(Tenant t)
    {
        var to = (await _admins.AllAsync()).Where(a => a.Level >= AdminLevel.Full).Select(a => a.Email).ToList();
        int now = Pricing.PeopleInSeats(t.Technicians), most = Pricing.MostSeatsFor(t);
        var b = new StringBuilder();
        b.Append($"{t.CompanyName} (/{t.Slug}) asks for {t.SeatsAskedFor} seats in all.\n\n");
        b.Append($"They may have {most} now, and {now} are in use.\n");
        b.Append($"Asked by: {t.SeatsAskedBy}\n");
        if (t.SeatsAskedNote.Length > 0) b.Append($"Their reason: {t.SeatsAskedNote}\n");
        b.Append($"\nAllow it, or not, on Owner → Approvals: {_publicUrl}/owner/approvals\n");
        var r = to.Count == 0 ? "no Full admin or Owner to tell" : await _mail.SendAsync(to, $"{t.CompanyName} asks for {t.SeatsAskedFor} seats", b.ToString());
        await _admins.LogAsync(t.SeatsAskedBy, $"Asked for {t.SeatsAskedFor} seats — {(r == "sent" ? "emailed " + string.Join(", ", to) : "NOT emailed: " + r)}", t.CompanyName);
        return r;
    }

    public static (string subject, string body) SeatsAllowedMail(Tenant t, int most, int inSeats)
    {
        var r = Pricing.For(t);
        int package = Pricing.SeatsAllowed(t);
        var b = new StringBuilder();
        b.Append("Hello,\n\n");
        if (most > package)
        {
            b.Append($"{t.CompanyName} may now have up to {most} seats on The Remote Support Program.\n\n");
            b.Append($"Your package stays {package} seats. Each seat past that is {Pricing.Money(r, r.ExtraSeat)} a month");
            b.Append(t.Pilot ? ", which we invoice each month.\n" : t.NotBilled ? ".\n" : ", added to your monthly payment from the next one.\n");
            b.Append("Nothing changes until an owner adds somebody on the People page, where the new monthly price is shown\n");
            b.Append("and must be accepted first. Removing somebody later brings it down again.\n\n");
        }
        else
        {
            b.Append($"{t.CompanyName} is back to its {package} seats on The Remote Support Program.\n\n");
            if (inSeats > package) b.Append($"{inSeats} seats are in use now. Nobody was removed, but nobody more can be added until you are below {package}.\n\n");
        }
        b.Append("To talk about it, reply to this email.\n\nThe Remote Support Program\n");
        return (most > package ? $"{t.CompanyName}: you may now have up to {most} seats" : $"{t.CompanyName}: back to {package} seats", b.ToString());
    }

    public static (string subject, string body) SeatsDeclinedMail(Tenant t, int asked, string reason)
    {
        var b = new StringBuilder();
        b.Append("Hello,\n\n");
        b.Append($"Thank you for asking for {asked} seats for {t.CompanyName}. We are not able to allow more seats this time.\n\n");
        if (reason.Length > 0) b.Append(reason + "\n\n");
        b.Append($"Your company keeps its {Pricing.MostSeatsFor(t)} seats. To talk about it, reply to this email.\n\nThe Remote Support Program\n");
        return ($"{t.CompanyName}: your request for more seats", b.ToString());
    }

    public async Task<Done> SetBilledAsync(PlatformAdmins.Actor by, int id, bool notBilled)
    {
        if (!PlatformAdmins.MayManage(by.Level)) return Done.No(ViewerRefusal);
        var t = await _tenants.ByIdAsync(id);
        if (t == null) return Done.No("That company no longer exists.");
        var e = await _tenants.UpdateBrandingAsync(id, x => x.NotBilled = notBilled);
        if (e.Length > 0) return Done.No(e);
        await _admins.LogAsync(by.Name, notBilled ? "Marked not billed" : "Marked billed", t.CompanyName);
        return new("", notBilled ? $"{t.CompanyName} marked not billed." : $"{t.CompanyName} marked billed.");
    }

    /// <summary>The company's country — and with it the region it is billed in (2026-10-01, 2026-10-05).</summary>
    public async Task<Done> SetCountryAsync(PlatformAdmins.Actor by, int id, string? country)
    {
        if (!PlatformAdmins.MayManage(by.Level)) return Done.No(ViewerRefusal);
        if (country == null || !Pages.SignupModel.Countries.Contains(country)) return Done.No("Choose a country from the list.");
        var t = await _tenants.ByIdAsync(id);
        if (t == null) return Done.No("That company no longer exists.");
        var e = await _tenants.UpdateBrandingAsync(id, x => { x.Country = country; x.Region = Pricing.RegionOfCountry(country); });
        if (e.Length > 0) return Done.No(e);
        await _admins.LogAsync(by.Name, $"Country {(t.Country.Length > 0 ? t.Country : "(none)")} → {country}", t.CompanyName);
        return new("", $"{t.CompanyName}: country set to {country}.");
    }

    public async Task<Done> RemoveWaitingAsync(PlatformAdmins.Actor by, int id)
    {
        if (!PlatformAdmins.MayManage(by.Level)) return Done.No(ViewerRefusal);
        var w = (await _tenants.WaitingListAsync()).FirstOrDefault(x => x.Id == id);
        if (w == null) return Done.No("That entry is no longer on the waiting list.");
        await _tenants.RemoveWaitingAsync(id);
        await _admins.LogAsync(by.Name, $"Removed {w.CompanyName} ({w.Email}) from the waiting list");
        return new("", "Removed from the waiting list.");
    }

    // ---- the company's owners: who they are, and writing to them ----------------------------------

    /// <summary>The people to write to: the company's owners (not removed), or its support address when it has none.</summary>
    public async Task<List<(string name, string email)>> OwnersAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var people = await db.Technicians.Where(p => p.TenantId == id && !p.Disabled && (p.Role & TenantRoles.Owners) != 0)
            .OrderBy(p => p.DisplayName).Select(p => new { p.DisplayName, p.Email }).ToListAsync();
        var list = people.Where(p => p.Email.Contains('@')).Select(p => (p.DisplayName, p.Email)).ToList();
        if (list.Count == 0)
        {
            var t = await db.Tenants.FindAsync(id);
            if (t != null && t.SupportEmail.Contains('@')) list.Add((t.ContactName.Length > 0 ? t.ContactName : t.CompanyName, t.SupportEmail));
        }
        return list;
    }

    /// <summary>
    /// An admin writes to a company's owners (Clint's answer, 2026-10-13). From our mailbox, signed by the admin,
    /// replies to the admin's own address when they have one; our copy-to address gets its copy, as every notice does.
    /// </summary>
    public async Task<Done> EmailOwnersAsync(PlatformAdmins.Actor by, int id, string? subject, string? message, string? replyTo)
    {
        if (!PlatformAdmins.MayManage(by.Level)) return Done.No(ViewerRefusal);
        subject = (subject ?? "").Trim(); message = (message ?? "").Trim();
        if (subject.Length == 0) return Done.No("Give the email a subject.");
        if (message.Length == 0) return Done.No("Write the message.");
        if (subject.Length > 200 || message.Length > 20000) return Done.No("That is too long for one email.");
        var t = await _tenants.ByIdAsync(id);
        if (t == null) return Done.No("That company no longer exists.");
        var owners = await OwnersAsync(id);
        if (owners.Count == 0) return Done.No($"{t.CompanyName} has no owner or support address to write to.");
        var body = $"{message}\n\n{by.Name}\nThe Remote Support Program\n";
        var r = await _mail.SendAsync(owners.Select(o => o.email), subject, body, "The Remote Support Program", replyTo, copyOwner: true);
        if (r != "sent") return Done.No("The email did not go: " + r);
        await _admins.LogAsync(by.Name, $"Emailed the owners: \"{subject}\"", t.CompanyName);
        return new("", $"Sent to {string.Join(", ", owners.Select(o => o.email))}.");
    }

    /// <summary>
    /// One of our notices to a company's owners, logged by name, with no change to the company — the pilot's notice
    /// and its withdrawal (<see cref="PilotService"/>). Says who was emailed, or why nobody was.
    /// </summary>
    public Task<string> NoticeAsync(PlatformAdmins.Actor by, Tenant t, (string subject, string body) mail) => TellAsync(by, t, mail);

    // ---- the two notices -------------------------------------------------------------------------

    private async Task<string> TellAsync(PlatformAdmins.Actor by, Tenant t, (string subject, string body) mail)
    {
        var owners = await OwnersAsync(t.Id);
        if (owners.Count == 0) { await _admins.LogAsync(by.Name, $"Not emailed ({mail.subject}): no owner or support address", t.CompanyName); return "They have no owner or support address, so nobody was emailed."; }
        var r = await _mail.SendAsync(owners.Select(o => o.email), mail.subject, mail.body, "The Remote Support Program", null, copyOwner: true);
        if (r == "sent") { await _admins.LogAsync(by.Name, $"Emailed: {mail.subject}", t.CompanyName); return $"Their owners were emailed ({string.Join(", ", owners.Select(o => o.email))})."; }
        _log.LogWarning("Notice to {Company} not sent: {Why}", t.CompanyName, r);
        await _admins.LogAsync(by.Name, $"NOT emailed ({mail.subject}): {r}", t.CompanyName);
        return "The email to their owners did NOT go: " + r;
    }

    public static (string subject, string body) Suspended(Tenant t)
    {
        var b = new StringBuilder();
        b.Append("Hello,\n\n");
        b.Append($"{t.CompanyName}'s account on The Remote Support Program has been suspended.\n\n");
        b.Append("While it is suspended, your people cannot sign in to the console or the portal, and your\n");
        b.Append("customers' machines cannot connect. Nothing has been deleted: your tickets, clients and\n");
        b.Append("machines are kept, and everything works again as soon as the account is made active.\n\n");
        b.Append("To talk about it, reply to this email.\n\n");
        b.Append("The Remote Support Program\n");
        return ($"{t.CompanyName}: your account has been suspended", b.ToString());
    }

    /// <summary>A signup approved by hand: the free version, or a full-version trial with no card (outside card payment).</summary>
    public static (string subject, string body) Approved(Tenant t, string publicUrl)
    {
        var b = new StringBuilder();
        b.Append("Hello,\n\n");
        b.Append($"{t.CompanyName}'s account on The Remote Support Program has been approved");
        b.Append(FreePlan.IsFree(t.Plan) ? $", and {FreePlan.FreeTheName} is ready to use.\n\n"
            : FreePlan.Is(t.Plan) ? $", and your 14-day free trial of {FreePlan.TheName} has started.\n\n" : ", and your 14-day free trial has started.\n\n");
        if (publicUrl.Length > 0) b.Append($"Sign in: {publicUrl}/login\n\n");
        b.Append("The Remote Support Program\n");
        return ($"{t.CompanyName}: your account is approved", b.ToString());
    }

    public static (string subject, string body) Restored(Tenant t)
    {
        var b = new StringBuilder();
        b.Append("Hello,\n\n");
        b.Append($"{t.CompanyName}'s account on The Remote Support Program is active again.\n\n");
        b.Append("Your people can sign in, and your customers' machines reconnect by themselves within a minute.\n\n");
        b.Append("The Remote Support Program\n");
        return ($"{t.CompanyName}: your account is active again", b.ToString());
    }

    private static string Plural(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";
}
