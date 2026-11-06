using System.Text;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// A free-version account made by us for somebody (2026-10-23, HANDOVER §157). Clint: <i>"Can we rather have a button that i
/// can create the client in the portal from and send the links directly from there?"</i>, then <i>"I need an option to cc the
/// tech in when sending the links so that he can help her set it up."</i> Until now it was three commands on the server.
///
/// <list type="bullet">
/// <item><b>Owner → Free version</b>, a Manager or above: their name and email (the company name is optional, their own name
/// standing in — as at the free signup). The company is made <b>Active on the free plan</b> at once — nothing to approve, no
/// trial — with its one person, <b>Owner + Technician</b> (<see cref="TenantService.FreeRole"/>), who has no password yet.</item>
/// <item><b>One email</b> (<see cref="Invitation"/>): the set-up link (<see cref="AccountLinks"/>, 72 hours, once), the
/// installer for their two computers (<c>/download/{slug}</c>) and the console (<c>/tech/{slug}</c>).</item>
/// <item><b>Copy a technician</b> (<see cref="CcProblem"/>): a visible Cc, and Reply-To, so the person helping has the very
/// same email — set-up link included, which is the point: they sit with the customer and do it together.</item>
/// <item><b>Send the links again</b>: a new set-up link while they have not used the old one (the old one stops working);
/// once set up, the two download links alone.</item>
/// <item><b>The free terms are still accepted, word for word</b> — not at signup, which they never see, but on the set-up
/// page before the password (<see cref="TermsDueAsync"/>): "nothing is made unless the very text shown was ticked" (§84)
/// cannot be met by us typing their details, so their first sign-in waits for it instead.</item>
/// </list>
/// <para><b>Free OR Basic since 2026-10-25 (§163)</b>: the admin picks which. The Free version is Active at once with nothing to pay;
/// the Basic version starts its 14-day trial and its card is added on Billing. The notice to the free accounts of the morning
/// of 2026-10-25 is gone: with the Free version back, they simply stay on it.</para>
/// <para>Only on THIS server: a company lives on its own region's server (<see cref="RegionServers"/>), and the free version
/// is made for this server's region. Free companies never count against the open places (<see cref="OpenPlaces"/>).</para>
/// </summary>
public sealed class FreeCompanies
{
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly AccountLinks _links;
    private readonly PlatformMailService _mail;
    private readonly PlatformAdmins _admins;
    private readonly RegionServers _regions;
    private readonly ILogger<FreeCompanies> _log;

    public FreeCompanies(IDbContextFactory<PlatformDb> dbf, AccountLinks links, PlatformMailService mail, PlatformAdmins admins,
        RegionServers regions, ILogger<FreeCompanies> log)
    { _dbf = dbf; _links = links; _mail = mail; _admins = admins; _regions = regions; _log = log; }

    /// <summary>Who the free version's emails come from: the company is the person themselves.</summary>
    public const string FromName = "The Remote Support Program";

    /// <summary>The region a free company made here belongs to — this server's.</summary>
    public Pricing.Region Region => Pricing.For(_regions.Here);

    /// <summary>What happened: an error, or what to tell the admin — and the set-up link when the email did not go, to pass on once.</summary>
    public sealed record Done(string Error, string Said = "", string Link = "", int TenantId = 0);

    /// <summary>The first invitation to a free company's owner — the email this class writes, not the company's own.</summary>
    public static bool IsTheirFirstInvite(Technician t, AccountTokenPurpose purpose) =>
        purpose == AccountTokenPurpose.Invite && FreePlan.Is(t.Tenant.Plan) && t.Role.Has(TenantRole.Owner);

    /// <summary>
    /// The copy addresses typed by the admin: none, or each one an email address that is not the person's own. Several may be
    /// given, separated by commas or semicolons. Returns the problem, or empty with the list.
    /// </summary>
    public static (string problem, List<string> list) CcProblem(string? cc, string personEmail)
    {
        var list = (cc ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => a.ToLowerInvariant()).Distinct().ToList();
        foreach (var a in list)
            if (!TicketIntake.LooksLikeEmail(a)) return ($"\"{a}\" does not look like an email address. Copy to a technician takes email addresses only.", new());
        list.RemoveAll(a => a == (personEmail ?? "").Trim().ToLowerInvariant());
        return list.Count > 5 ? ("Copy to five people at most.", new()) : ("", list);
    }

    /// <summary>Makes the company and its one person, and sends them the links (<see cref="SendLinksAsync"/>).</summary>
    public async Task<Done> CreateAsync(PlatformAdmins.Actor by, string? name, string? email, string? company, string? phone,
        string? timeZone, string? country, string? cc, string? plan = null)
    {
        // The Free version unless the Basic version is asked for (2026-10-25, §163).
        bool basic = FreePlan.IsBasic(plan);
        if (!PlatformAdmins.MayManage(by.Level)) return new(AdminCompanies.ViewerRefusal);
        name = (name ?? "").Trim(); email = (email ?? "").Trim().ToLowerInvariant();
        company = (company ?? "").Trim(); phone = (phone ?? "").Trim();
        if (name.Length < 2) return new("Enter their name.");
        // A ticket keeps the people on it as names joined by commas (Assignees).
        if (name.Contains(',')) return new("A name cannot contain a comma. Use \"Jane Smith\" rather than \"Smith, Jane\".");
        if (!TicketIntake.LooksLikeEmail(email)) return new("Enter their email address — it is their login, and where the links go.");
        var (ccProblem, _) = CcProblem(cc, email);
        if (ccProblem.Length > 0) return new(ccProblem);
        var region = Region;
        string countryName = region.Name;
        if (region.Code == "EU")
        {
            countryName = (country ?? "").Trim();
            if (!Pricing.EuropeanCountries.Contains(countryName)) return new("Choose their country.");
        }
        var zone = CompanyClock.MustChoose(region.Code) ? (timeZone ?? "").Trim() : CompanyClock.For(region.Code).First().Id;
        if (!CompanyClock.For(region.Code).Any(z => z.Id == zone)) return new("Choose their time zone.");
        // Optional, as at the free signup: their own name stands in for a company they do not have.
        var companyName = company.Length > 0 ? company : name;
        if (companyName.Length < 2) return new("The company name is too short.");

        int tenantId; string slug;
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            var taken = await db.Technicians.Where(x => x.Email == email).Select(x => x.Tenant.CompanyName).FirstOrDefaultAsync();
            if (taken != null) return new($"{email} is already registered, at {taken}. Each login is one email address.");
            // The company and its person stand or fall together.
            await using var tx = await db.Database.BeginTransactionAsync();
            slug = TenantService.Slugify(companyName);
            if (slug.Length < 2) slug = TenantService.Slugify(name);
            var baseSlug = slug; int n = 2;
            while (await db.Tenants.AnyAsync(x => x.Slug == slug)) slug = $"{baseSlug}-{n++}";
            var t = new Tenant
            {
                Slug = slug, CompanyName = companyName, ContactName = name, ContactNumber = phone, Country = countryName, Region = region.Code,
                TimeZone = zone, ProductName = "Remote Support", SupportEmail = email, SupportPhone = phone,
                TenantKey = Passwords.RandomToken(24), Plan = basic ? FreePlan.BasicName : FreePlan.Name,
                // Nothing to approve. The Basic version's 14-day trial (2026-10-25, R50 a month after it — the card on Billing); the
                // Free version running at once, for as long as it is used.
                Status = basic ? TenantStatus.Trial : TenantStatus.Active, ApprovedUtc = DateTime.UtcNow,
                TrialEndsUtc = basic ? DateTime.UtcNow.AddDays(CompanyBilling.TrialDays) : null
            };
            db.Tenants.Add(t);
            await db.SaveChangesAsync();
            // No password: they choose their own from the link (AccountLinks), and AuthService matches nothing until then.
            db.Technicians.Add(new Technician
            {
                TenantId = t.Id, Email = email, DisplayName = name, Salt = "", Hash = "", Role = TenantService.FreeRole,
                SecurityStamp = AuthService.NewStamp()
            });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            tenantId = t.Id;
        }
        var kind = basic ? FreePlan.ShortName : FreePlan.FreeShortName;
        await _admins.LogAsync(by.Name, $"Made a {kind} account for {name} <{email}>", companyName);
        _log.LogInformation("{Kind} account {Slug} made for {Email} by {By}", kind, slug, email, by.Name);
        var sent = await SendLinksAsync(by, tenantId, cc);
        return sent with { Error = "", Said = $"{companyName} is ready — {(basic ? "Basic version, 14-day trial" : "Free version, active")}. " + (sent.Error.Length > 0 ? sent.Error : sent.Said), TenantId = tenantId };
    }

    /// <summary>
    /// The links again, to the company's owner with the copies chosen now: a NEW set-up link while they have not used the old one
    /// (one live link per person — the old one stops working), and once they are set up, the two download links alone.
    /// </summary>
    public async Task<Done> SendLinksAsync(PlatformAdmins.Actor by, int tenantId, string? cc)
    {
        if (!PlatformAdmins.MayManage(by.Level)) return new(AdminCompanies.ViewerRefusal);
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == tenantId);
        if (t == null) return new("That company no longer exists.");
        if (!FreePlan.Is(t.Plan)) return new($"{t.CompanyName} is on the full version — its people are invited from its own People page.");
        if (!t.IsServiceEnabled) return new($"{t.CompanyName} is {t.Status} — make it active first.");
        var who = await db.Technicians.Include(x => x.Tenant).AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.Disabled).OrderBy(x => x.Id).FirstOrDefaultAsync();
        if (who == null) return new($"{t.CompanyName} has nobody to send the links to.");
        var (ccProblem, copies) = CcProblem(cc, who.Email);
        if (ccProblem.Length > 0) return new(ccProblem);
        var copied = copies.Count > 0 ? $" (copy to {string.Join(", ", copies)})" : "";

        if (who.AwaitingSetup)
        {
            var sent = await _links.IssueAsync(who.Id, AccountTokenPurpose.Invite, by.Name, copies);
            await _admins.LogAsync(by.Name, sent.Mailed ? $"Sent the set-up and download links to {who.Email}{copied}"
                : $"Set-up link NOT emailed to {who.Email} ({sent.Why})", t.CompanyName);
            return sent.Mailed
                ? new("", $"The set-up and download links went to {who.Email}{copied}. The set-up link works once, for {AccountLinks.InviteLife.TotalHours:0} hours.")
                : new($"The email did not go ({sent.Why}). Give {who.DisplayName} this set-up link yourself — it works once, for {AccountLinks.InviteLife.TotalHours:0} hours.", Link: sent.Link);
        }
        var (subject, body) = DownloadsEmail(who, _links.PublicUrl, copies);
        var r = await _mail.SendAsync(new[] { who.Email }, subject, body, FromName, copies.FirstOrDefault(), copyOwner: false, cc: copies);
        await _admins.LogAsync(by.Name, r == "sent" ? $"Sent the download links to {who.Email}{copied}" : $"Download links NOT emailed to {who.Email} ({r})", t.CompanyName);
        return r == "sent" ? new("", $"{who.DisplayName} is already set up, so only the download links went to {who.Email}{copied}.")
            : new($"The email did not go: {r}");
    }

    /// <summary>A Free or Basic company, as the owner page lists it — with whether it pays by card.</summary>
    public sealed record Row(Tenant Tenant, Technician? Person, int Machines, bool TermsAccepted, bool Card);

    public async Task<List<Row>> ListAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var tenants = await db.Tenants.AsNoTracking().Include(x => x.Technicians).Where(x => x.Plan == FreePlan.Name || x.Plan == FreePlan.BasicName)
            .OrderByDescending(x => x.CreatedUtc).ToListAsync();
        var ids = tenants.Select(x => x.Id).ToList();
        var machines = await db.Devices.Where(d => ids.Contains(d.TenantId)).GroupBy(d => d.TenantId)
            .Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        var accepted = (await db.TermsAcceptances.Where(a => ids.Contains(a.TenantId)).Select(a => a.TenantId).Distinct().ToListAsync()).ToHashSet();
        var cards = (await db.PayfastSubscriptions.Where(s => ids.Contains(s.TenantId) && s.Status == PayfastStatus.Active && s.Purpose != "test")
            .Select(s => s.TenantId).Distinct().ToListAsync()).ToHashSet();
        return tenants.Select(x => new Row(x, x.Technicians.Where(p => !p.Disabled).OrderBy(p => p.Id).FirstOrDefault(),
            machines.GetValueOrDefault(x.Id), accepted.Contains(x.Id), cards.Contains(x.Id))).ToList();
    }

    // ---- the terms, at set-up ----------------------------------------------------------------------

    /// <summary>
    /// The free terms this person must accept before their password is saved: an owner of a free company that has accepted no
    /// terms yet — made here, never through signup. Null when there is nothing to accept.
    /// </summary>
    public async Task<Terms.Text?> TermsDueAsync(Technician who)
    {
        if (!FreePlan.Is(who.Tenant.Plan) || !who.Role.Has(TenantRole.Owner)) return null;
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.TermsAcceptances.AnyAsync(a => a.TenantId == who.TenantId) ? null : Terms.For(who.Tenant.Region, who.Tenant.Plan);
    }

    /// <summary>Keeps the terms they ticked, word for word, as signup does (<see cref="TermsAcceptance"/>).</summary>
    public async Task AcceptTermsAsync(Technician who, Terms.Text terms, string fromAddress)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        db.TermsAcceptances.Add(new TermsAcceptance
        {
            TenantId = who.TenantId, Region = terms.Region, Version = terms.Version, Draft = terms.Draft, Text = terms.Source, Sha256 = terms.Sha256,
            AcceptedByName = who.DisplayName, AcceptedByEmail = who.Email, FromAddress = fromAddress, AcceptedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    // ---- the two emails ----------------------------------------------------------------------------

    /// <summary>The installer and the console, numbered from <paramref name="first"/> — in both emails, the same words.</summary>
    private static void Downloads(StringBuilder sb, string publicUrl, string slug, int first, string plan)
    {
        if (FreePlan.IsFree(plan))
        {
            sb.Append($"{first}. On the computer you want to reach — the Free version has one — install the program from here. You choose\n");
            sb.Append("   its password while installing; you will type it to connect:\n\n");
        }
        else
        {
            sb.Append($"{first}. On each computer you want to reach — {FreePlan.MaxMachines} in the price, more on Billing — install the program from here.\n");
            sb.Append("   Choose a password for it while installing to connect with nobody there; leave it blank and the person\n");
            sb.Append("   at the computer is asked to allow each connection:\n\n");
        }
        sb.Append($"   {publicUrl}/download/{slug}\n\n");
        sb.Append($"{first + 1}. On the computer you connect FROM, download the console and sign in with your email address:\n\n");
        sb.Append($"   {publicUrl}/tech/{slug}\n\n");
    }

    private static void Helper(StringBuilder sb, IReadOnlyList<string> cc)
    {
        if (cc.Count > 0) sb.Append($"A copy of this email has gone to {string.Join(", ", cc)}, to help you set it up. Reply to this email to reach them.\n\n");
    }

    /// <summary>The first email: set up the account, then the two downloads.</summary>
    public static (string subject, string body) Invitation(Technician t, string link, string until, string publicUrl, IReadOnlyList<string> cc)
    {
        var sb = new StringBuilder();
        sb.Append($"Hello {t.DisplayName},\n\n");
        sb.Append("Your remote support account is ready");
        sb.Append(t.Tenant.CompanyName != t.DisplayName ? $" ({t.Tenant.CompanyName}).\n" : ".\n");
        if (FreePlan.IsFree(t.Tenant.Plan))
        {
            sb.Append($"It is {FreePlan.FreeFullName}: it lets you connect to one of your own computers from anywhere, at no charge.\n");
            sb.Append($"For more computers, or to connect with somebody there, upgrade to {FreePlan.FullName} from your account page.\n\n");
        }
        else
        {
            sb.Append($"It is {FreePlan.FullName}: it lets you connect to {FreePlan.MaxMachines} of your own computers from anywhere, and more if you pay for them on Billing.\n");
            var r = Pricing.For(t.Tenant);
            sb.Append($"The first 14 days are free; after that it is {Pricing.Money(r, r.Basic)} a month. Add your card on Billing in your account\n");
            sb.Append("page before then — nothing is charged until the 14 days are up, and you can cancel any time.\n\n");
        }
        Helper(sb, cc);
        sb.Append("1. Set up your account — read and accept the terms, choose your password, then set up two-factor\n");
        sb.Append("   sign-in with an authenticator app on your phone (Microsoft Authenticator or Google Authenticator):\n\n");
        sb.Append($"   {link}\n\n");
        sb.Append($"   The link works once and expires on {until}.\n\n");
        Downloads(sb, publicUrl, t.Tenant.Slug, 2, t.Tenant.Plan);
        sb.Append($"Your account page is at {publicUrl}/login — sign in with this email address ({t.Email}).\n\n");
        sb.Append("If you were not expecting this, you can ignore this email.\n");
        return ("Your remote support account: set up and download", sb.ToString());
    }

    /// <summary>Once they are set up: the two downloads alone.</summary>
    public static (string subject, string body) DownloadsEmail(Technician t, string publicUrl, IReadOnlyList<string> cc)
    {
        var sb = new StringBuilder();
        sb.Append($"Hello {t.DisplayName},\n\n");
        sb.Append("Here are the download links for your remote support account.\n\n");
        Helper(sb, cc);
        Downloads(sb, publicUrl, t.Tenant.Slug, 1, t.Tenant.Plan);
        sb.Append($"Your account page is at {publicUrl}/login — sign in with {t.Email}. Forgot your password? There is a link on that page.\n");
        return ("Your remote support: the download links", sb.ToString());
    }
}
