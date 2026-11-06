using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Services;

/// <summary>Tenant CRUD, technician management with seat limits, and branding export.</summary>
public sealed class TenantService
{
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly string _dataDir;
    private readonly string _publicUrl;
    private readonly int _defaultSeats;
    private readonly IDataProtector _smtpProtector;
    private readonly RegionServers _regions;

    public TenantService(IDbContextFactory<PlatformDb> dbf, IConfiguration cfg, string dataDir, IDataProtectionProvider dp)
    {
        _dbf = dbf; _dataDir = dataDir;
        _regions = new RegionServers(cfg);
        _publicUrl = (cfg["Platform:PublicUrl"] ?? "").TrimEnd('/');
        _defaultSeats = int.TryParse(cfg["Platform:DefaultSeats"], out var s) ? s : Pricing.IncludedSeats;
        // Its own purpose string, separate from the device-password protector in ClientService: a
        // ciphertext from one must not be decryptable as the other, and the two are rotated apart.
        _smtpProtector = dp.CreateProtector("RemoteSupport.TenantSmtpPassword.v1");
        Directory.CreateDirectory(LogoDir);
    }

    public string ProtectSmtpPassword(string plain) => string.IsNullOrEmpty(plain) ? "" : _smtpProtector.Protect(plain);

    /// <summary>
    /// Returns the stored password, or null when there is none or it cannot be decrypted. Null
    /// rather than a placeholder string on purpose: the standalone toolkit learned that sending
    /// with no credentials after a failed decrypt produces "authentication required" from the
    /// server, which reads like a wrong password rather than a password that never arrived.
    /// The caller must treat null as "do not send" and say so.
    /// </summary>
    public string? UnprotectSmtpPassword(string cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return null;
        try { return _smtpProtector.Unprotect(cipher); } catch { return null; }
    }

    public string LogoDir => Path.Combine(_dataDir, "logos");

    public static string Slugify(string s)
    {
        var chars = s.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars);
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }

    public async Task<(Tenant? tenant, string error)> CreateAsync(string companyName, string? slug = null, int? seats = null, string? productName = null)
    {
        slug = Slugify(string.IsNullOrWhiteSpace(slug) ? companyName : slug);
        if (slug.Length < 2) return (null, "Company name is too short.");
        await using var db = await _dbf.CreateDbContextAsync();
        if (await db.Tenants.AnyAsync(t => t.Slug == slug)) return (null, $"A tenant with slug '{slug}' already exists.");
        var t = new Tenant
        {
            Slug = slug, CompanyName = companyName.Trim(),
            ProductName = string.IsNullOrWhiteSpace(productName) ? "Remote Support" : productName!.Trim(),
            TenantKey = Passwords.RandomToken(24), Seats = seats ?? _defaultSeats
        };
        db.Tenants.Add(t);
        await db.SaveChangesAsync();
        return (t, "");
    }

    /// <summary>Everything the signup form sends (2026-10-05): the region first, the terms as accepted, then the company.</summary>
    public sealed record SignupRequest(string Region, string? Country, string CompanyName, string ContactName, string ContactNumber,
        string Email, string Password, string? Primary, string? Accent, bool AcceptedTerms, string TermsSha256, string FromAddress, string? TimeZone = null, string? Plan = null,
        string? TicketEmail = null, string? PilotToken = null, string? PlaceToken = null)
    {
        /// <summary>A one-person plan, Free or Basic (2026-10-25, §163: <see cref="Plan"/> is FreePlan.Name, FreePlan.BasicName, or
        /// anything else for the full version).</summary>
        public bool Free => FreePlan.Is(Plan);
    }

    /// <summary>
    /// Self-service signup: creates a Pending tenant plus its first owner, and keeps the terms they
    /// accepted word for word (<see cref="TermsAcceptance"/>), in one save.
    ///
    /// <para>From a pilot link (<see cref="SignupRequest.PilotToken"/>, 2026-10-15): the pilot's terms, a pilot company,
    /// Active at once with no trial — and the link spent in the SAME save, by a conditional update, so a second use or two
    /// at once make one company. Allowed while public signup is closed: the link is the way in (<see cref="PilotService"/>).</para>
    ///
    /// <para>Country first (2026-10-05): the region must be one of the five, and one THIS server
    /// holds (<see cref="RegionServers"/>) — a company is never created on another country's server.
    /// The terms must have been accepted, and must be the very text shown (<see cref="Terms.Text.Sha256"/>):
    /// if they changed while the form was open, the person is asked to read them again.</para>
    /// </summary>
    public async Task<(Tenant? tenant, string error)> SignupAsync(SignupRequest q)
    {
        var region = Pricing.Regions.FirstOrDefault(r => r.Code == (q.Region ?? "").Trim().ToUpperInvariant());
        if (region == null) return (null, "Please choose where your company is.");
        if (_regions.Where(region.Code).place != RegionServers.Place.Here) return (null, $"We are not open in {region.In} on this server yet.");
        string country = region.Code == "EU" ? (q.Country ?? "").Trim() : region.Name;
        if (region.Code == "EU" && !Pricing.EuropeanCountries.Contains(country)) return (null, "Please choose your country.");
        // Chosen at signup (Clint: "selecting the us as location still could have different time zones"):
        // one of the region's own; where the region has only one, that one.
        var zone = CompanyClock.MustChoose(region.Code) ? (q.TimeZone ?? "").Trim() : CompanyClock.For(region.Code).First().Id;
        if (!CompanyClock.For(region.Code).Any(z => z.Id == zone)) return (null, "Please choose your time zone.");
        // A pilot link (2026-10-15): the full version, its own terms. Checked here, and spent below in the same save.
        bool pilot = !string.IsNullOrEmpty(q.PilotToken);
        if (pilot && q.Free) return (null, "A pilot link signs up the full version.");
        var pilotHash = pilot ? PilotService.Hash(q.PilotToken!) : "";
        if (pilot)
            await using (var check = await _dbf.CreateDbContextAsync())
                if (PilotService.Why(await check.PilotInvites.AsNoTracking().FirstOrDefaultAsync(i => i.TokenHash == pilotHash), DateTime.UtcNow) is { Length: > 0 } dead)
                    return (null, dead);
        // The free version (2026-10-06) accepts its own terms: the region's, with the free section ahead of them.
        var terms = Terms.For(region.Code, q.Plan, pilot);
        if (!q.AcceptedTerms) return (null, "Please read the terms and tick the box to accept them.");
        if (q.TermsSha256 != terms.Sha256) return (null, TermsChanged);
        string companyName = q.CompanyName ?? "", contactName = q.ContactName ?? "", contactNumber = q.ContactNumber ?? "", password = q.Password ?? "";
        var email = (q.Email ?? "").Trim().ToLowerInvariant();
        // The free version: the company name is optional (Clint) — the person's own name stands in for it.
        if (q.Free && companyName.Trim().Length == 0) companyName = contactName;
        if (companyName.Trim().Length < 2) return (null, q.Free ? "Please enter your name." : "Please enter your company name.");
        if (contactName.Trim().Length < 2) return (null, "Please enter a contact name.");
        // Optional on the free version: fewer questions for something that costs nothing.
        if (!q.Free && contactNumber.Trim().Length < 5) return (null, "Please enter a contact number.");
        if (!email.Contains('@')) return (null, "Please enter a valid email address.");
        if (Passwords.Problem(password, email) is { Length: > 0 } weak) return (null, weak);
        // The free version asks no colours: our own, with the light and dark switch every person has.
        string? primary = q.Free ? null : q.Primary, accent = q.Free ? null : q.Accent;
        // The ticket email address (2026-10-09), optional and full version only: the start of Tickets by email.
        var ticketEmail = q.Free ? "" : (q.TicketEmail ?? "").Trim().ToLowerInvariant();
        if (ticketEmail.Length > 0 && !TicketIntake.LooksLikeEmail(ticketEmail)) return (null, "That ticket email address does not look right — or leave it blank and set it up later.");

        await using var db = await _dbf.CreateDbContextAsync();
        if (await db.Technicians.AnyAsync(t => t.Email == email)) return (null, "That email address is already registered. Try signing in.");
        // Five companies at a time (2026-10-22, OpenPlaces): a full-version signup needs a free place, or a place offered from
        // the waiting list — checked here, and spent below in the same save. A pilot and the free version never wait.
        bool place = !string.IsNullOrEmpty(q.PlaceToken) && !q.Free && !pilot;
        var placeHash = place ? OpenPlaces.Hash(q.PlaceToken!) : "";
        if (place && OpenPlaces.Why(await db.WaitingList.AsNoTracking().FirstOrDefaultAsync(w => w.PlaceTokenHash == placeHash), DateTime.UtcNow) is { Length: > 0 } gone)
            return (null, gone);
        if (!place && !q.Free && !pilot && await OpenPlaces.StateAsync(db) is { Full: true } places) return (null, OpenPlaces.FullWords(places));
        if (ticketEmail.Length > 0 && await db.TicketIntake.AnyAsync(x => x.Address == ticketEmail && x.AddressConfirmed))
            return (null, "That ticket email address is already taking tickets for another company here. Leave it blank, or contact us if it is yours.");
        // The company, its owner and the terms they accepted stand or fall together.
        await using var tx = await db.Database.BeginTransactionAsync();
        var slug = Slugify(companyName);
        var baseSlug = slug; int n = 2;
        while (await db.Tenants.AnyAsync(t => t.Slug == slug)) slug = $"{baseSlug}-{n++}";

        var t = new Tenant
        {
            Slug = slug, CompanyName = companyName.Trim(), ContactName = contactName.Trim(), ContactNumber = contactNumber.Trim(), Country = country, Region = region.Code, TimeZone = zone,
            ProductName = "Remote Support", SupportEmail = email, SupportPhone = contactNumber.Trim(),
            TenantKey = Passwords.RandomToken(24), Seats = _defaultSeats, Status = TenantStatus.Pending
        };
        // Free or Basic (2026-10-25, §163), stored as itself.
        if (q.Free) t.Plan = FreePlan.IsFree(q.Plan) ? FreePlan.Name : FreePlan.BasicName;
        // A pilot is ready at once: nothing to approve, no trial, no card (PilotService).
        if (pilot) { t.Pilot = true; t.Status = TenantStatus.Active; t.ApprovedUtc = DateTime.UtcNow; t.TrialEndsUtc = null; }
        if (!string.IsNullOrWhiteSpace(primary)) t.PrimaryColor = primary!.Trim();
        if (!string.IsNullOrWhiteSpace(accent)) t.AccentColor = accent!.Trim();
        db.Tenants.Add(t);
        await db.SaveChangesAsync();

        var (salt, hash) = Passwords.Create(password);
        db.Technicians.Add(new Technician { TenantId = t.Id, Email = email, DisplayName = contactName.Replace(',', ' ').Trim(), Salt = salt, Hash = hash,
            // The free version's one person is the owner AND the technician (FreeRole) — they connect themselves.
            Role = q.Free ? FreeRole : TenantRole.OwnerSupervisor });
        db.TermsAcceptances.Add(new TermsAcceptance
        {
            TenantId = t.Id, Region = terms.Region, Version = terms.Version, Draft = terms.Draft, Text = terms.Source, Sha256 = terms.Sha256,
            AcceptedByName = contactName.Trim(), AcceptedByEmail = email, FromAddress = q.FromAddress ?? "", AcceptedUtc = DateTime.UtcNow
        });
        // Saved switched OFF: Tickets by email in the portal opens with it filled in, and asks only for the mailbox.
        if (ticketEmail.Length > 0) db.TicketIntake.Add(new TicketIntakeSettings { TenantId = t.Id, Address = ticketEmail });
        await db.SaveChangesAsync();
        if (pilot)
        {
            // Spent ONCE: only the update that finds it unused, uncancelled and in date changes it. Two sign-ups racing on
            // one link both get here; the second changes nothing and its company is rolled back with the transaction.
            var now = DateTime.UtcNow;
            int spent = await db.PilotInvites
                .Where(i => i.TokenHash == pilotHash && i.UsedUtc == null && i.CancelledUtc == null && i.ExpiresUtc > now)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.UsedUtc, now).SetProperty(i => i.TenantId, t.Id));
            if (spent != 1)
            {
                await tx.RollbackAsync();
                return (null, PilotService.Why(await db.PilotInvites.AsNoTracking().FirstOrDefaultAsync(i => i.TokenHash == pilotHash), now) is { Length: > 0 } why
                    ? why : PilotService.NotAValidLink);
            }
        }
        if (place)
        {
            // An offered place is spent ONCE, as a pilot link is: the loser of a race is rolled back with its company.
            var now = DateTime.UtcNow;
            int spent = await db.WaitingList
                .Where(w => w.PlaceTokenHash == placeHash && w.PlaceUsedUtc == null && w.PlaceExpiresUtc > now)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.PlaceUsedUtc, now).SetProperty(w => w.TenantId, t.Id));
            if (spent != 1)
            {
                await tx.RollbackAsync();
                return (null, OpenPlaces.Why(await db.WaitingList.AsNoTracking().FirstOrDefaultAsync(w => w.PlaceTokenHash == placeHash), now) is { Length: > 0 } why
                    ? why : OpenPlaces.NotAPlace);
            }
        }
        await tx.CommitAsync();
        return (t, "");
    }

    /// <summary>Said when the terms changed between being shown and being accepted — the page shows them again.</summary>
    public const string TermsChanged = "Our terms were updated while you were signing up. Please read them again and tick the box to accept them.";

    /// <summary>The terms a company accepted, newest first (one, today).</summary>
    public async Task<TermsAcceptance?> TermsAcceptedAsync(int tenantId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.TermsAcceptances.Where(a => a.TenantId == tenantId).OrderByDescending(a => a.AcceptedUtc).FirstOrDefaultAsync();
    }

    /// <summary>
    /// Somebody in a region with no server yet asks to be told when it opens (2026-10-05) — or, with
    /// <paramref name="full"/>, somebody who found every place taken (2026-10-22, <see cref="OpenPlaces"/>). The same
    /// email in the same region again updates their entry rather than adding a second.
    /// </summary>
    public async Task<string> JoinWaitingListAsync(string region, string? country, string? company, string? name, string? email, string? phone, bool full = false)
    {
        var r = Pricing.Regions.FirstOrDefault(x => x.Code == (region ?? "").Trim().ToUpperInvariant());
        email = (email ?? "").Trim().ToLowerInvariant();
        if (r == null) return "Please choose where your company is.";
        if ((company ?? "").Trim().Length < 2) return "Please enter your company name.";
        if ((name ?? "").Trim().Length < 2) return "Please enter your name.";
        if (!email.Contains('@') || email.Length < 5) return "Please enter a valid email address.";
        await using var db = await _dbf.CreateDbContextAsync();
        var e = await db.WaitingList.FirstOrDefaultAsync(w => w.Region == r.Code && w.Email == email);
        if (e == null) db.WaitingList.Add(e = new WaitingListEntry { Region = r.Code, Email = email });
        e.Country = r.Code == "EU" ? (country ?? "").Trim() : r.Name;
        e.CompanyName = company!.Trim(); e.ContactName = name!.Trim(); e.Phone = (phone ?? "").Trim(); e.CreatedUtc = DateTime.UtcNow;
        e.Full = full;
        await db.SaveChangesAsync();
        return "";
    }

    public async Task<List<WaitingListEntry>> WaitingListAsync()
    { await using var db = await _dbf.CreateDbContextAsync(); return await db.WaitingList.OrderBy(w => w.Region).ThenBy(w => w.CreatedUtc).ToListAsync(); }

    public async Task RemoveWaitingAsync(int id)
    { await using var db = await _dbf.CreateDbContextAsync(); var e = await db.WaitingList.FindAsync(id); if (e != null) { db.WaitingList.Remove(e); await db.SaveChangesAsync(); } }

    public async Task<List<Tenant>> PendingAsync()
    { await using var db = await _dbf.CreateDbContextAsync(); return await db.Tenants.Where(t => t.Status == TenantStatus.Pending).OrderBy(t => t.CreatedUtc).ToListAsync(); }

    /// <summary>Owner approves a signup: the 14-day trial starts now.</summary>
    public async Task<string> ApproveAsync(int tenantId, int trialDays = 14)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tenants.FindAsync(tenantId);
        if (t == null) return "Tenant not found.";
        // The Free version (back 2026-10-25, §163) has no trial and nothing to pay: approved means running, for as long as it is used.
        if (FreePlan.IsFree(t.Plan)) { t.Status = TenantStatus.Active; t.ApprovedUtc = DateTime.UtcNow; t.TrialEndsUtc = null; await db.SaveChangesAsync(); return ""; }
        // The Basic version starts its 14-day trial like the full version (2026-10-25: it is R50 a month).
        t.Status = TenantStatus.Trial; t.ApprovedUtc = DateTime.UtcNow; t.TrialEndsUtc = DateTime.UtcNow.AddDays(trialDays);
        await db.SaveChangesAsync();
        return "";
    }

    /// <summary>
    /// What upgrading from the free version would cost (Stage G, 2026-10-06): from nothing to the region's package
    /// for the people in seats now — one, on the free version. <see cref="Pricing.PriceRise.Token"/> is what the
    /// owner's yes must name, as every price rise does (§83), so a figure that changed in the meantime is asked again.
    /// </summary>
    public static Pricing.PriceRise UpgradeQuote(Tenant t, IEnumerable<Technician> people, string? to = null)
    {
        // From this plan's price (nothing, or R50) to the full version's for the people in seats now — or, from the Free version,
        // to the Basic version's three computers (2026-10-25, §163).
        int seats = Math.Max(1, Pricing.PeopleInSeats(people));
        var after = new Tenant { Region = t.Region, Seats = t.Seats, MostSeats = t.MostSeats, NotBilled = t.NotBilled, Pilot = t.Pilot,
            Plan = FreePlan.IsBasic(to) ? FreePlan.BasicName : "five-user" };
        return new Pricing.PriceRise(Pricing.BillFor(t, seats), Pricing.BillFor(after, seats));
    }

    /// <summary>
    /// Where a company on this plan may move up to (2026-10-25, §163): the Free version to the Basic version or the full version;
    /// the Basic version to the full version; the full version nowhere. <see cref="FreePlan.BasicName"/> or "full".
    /// </summary>
    public static IReadOnlyList<string> UpgradesFrom(string? plan) =>
        FreePlan.IsFree(plan) ? new[] { FreePlan.BasicName, "full" } : FreePlan.IsBasic(plan) ? new[] { "full" } : Array.Empty<string>();

    /// <summary>The days a company upgrading from the Basic version has before it pays — a new signup's trial.</summary>
    public const int UpgradeTrialDays = 14;

    /// <summary>
    /// When the full version's price starts for a Basic company upgrading (2026-10-25): <b>null</b> — from its next card
    /// payment, no trial — when it already pays by card (it is a paying customer, and a trial would have PayFast charge R500
    /// inside it); the end of the trial it is already on; or a new <see cref="UpgradeTrialDays"/>-day trial for one with
    /// neither (a free account from before 2026-10-25).
    /// </summary>
    public static DateTime? UpgradeTrialEnd(Tenant t, bool paysByCard, DateTime utcNow) =>
        paysByCard ? null
        : t.Status == TenantStatus.Trial && t.TrialEndsUtc is { } end && end > utcNow ? end
        : utcNow.AddDays(UpgradeTrialDays);

    /// <summary>Whether this company has a card subscription PayFast is charging.</summary>
    public async Task<bool> PaysByCardAsync(int tenantId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.PayfastSubscriptions.AnyAsync(s => s.TenantId == tenantId && s.Status == PayfastStatus.Active && s.Purpose != "test");
    }

    /// <summary>
    /// Upgrading from the free version, IN PLACE (Stage G, 2026-10-06; Clint's answer: "Yes, in place" — same account,
    /// same two machines, same saved passwords; a normal company once the owner accepts the full terms and price).
    /// Only the owner (<see cref="Permissions.CanAcceptPrice"/>); only with the region's FULL terms ticked, by their
    /// fingerprint, kept word for word as signup keeps them; only naming the exact price shown. The company then
    /// starts the same 14-day trial a new signup gets — it is already approved, so nobody has to approve it again.
    /// Nothing else changes. Empty for done, or why not.
    /// </summary>
    public async Task<string> UpgradeAsync(int tenantId, TenantRole asker, string askerName, string askerEmail,
        bool accepted, string termsSha, string agreedToken, string fromAddress, string? to = null)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tenants.Include(x => x.Technicians).FirstOrDefaultAsync(x => x.Id == tenantId);
        if (t == null) return "Not found.";
        if (!FreePlan.Is(t.Plan)) return "This company is already on the full version.";
        // To the Basic version only from the Free version (2026-10-25, §163).
        bool toBasic = FreePlan.IsBasic(to);
        if (toBasic && !FreePlan.IsFree(t.Plan)) return "This company is already on the Basic version.";
        if (!Permissions.CanAcceptPrice(asker)) return "Only the owner can upgrade the company.";
        var terms = toBasic ? Terms.For(t.Region, FreePlan.BasicName) : Terms.For(t.Region);
        if (!accepted) return "Please read the terms and tick the box to accept them.";
        if (termsSha != terms.Sha256) return TermsChanged;
        var quote = UpgradeQuote(t, t.Technicians, to);
        var was = FreePlan.TheNameOf(t.Plan);
        if (agreedToken != quote.Token) return "The price has changed since this page was opened. Please look at it again.";

        bool card = await db.PayfastSubscriptions.AnyAsync(s => s.TenantId == t.Id && s.Status == PayfastStatus.Active && s.Purpose != "test");
        var trialEnd = UpgradeTrialEnd(t, card, DateTime.UtcNow);
        if (toBasic) { t.Plan = FreePlan.BasicName; t.BasicMachines = 0; }
        else
        {
            t.Plan = "five-user";
            if (t.Seats <= 0) t.Seats = _defaultSeats;
        }
        // Paying by card already: it stays as it is, and PayFast's amount moves to the full price from the next charge (below).
        if (trialEnd is { } ends)
        {
            if (t.Status != TenantStatus.Trial || t.TrialEndsUtc != ends) t.TrialNoticesSent = "";
            t.Status = TenantStatus.Trial;
            t.TrialEndsUtc = ends;
        }
        db.TermsAcceptances.Add(new TermsAcceptance
        {
            TenantId = t.Id, Region = terms.Region, Version = terms.Version, Draft = terms.Draft, Text = terms.Source, Sha256 = terms.Sha256,
            AcceptedByName = askerName, AcceptedByEmail = askerEmail, FromAddress = fromAddress, AcceptedUtc = DateTime.UtcNow
        });
        Agreed(db, t, quote, toBasic ? "Upgraded from the Free version to the Basic version" : $"Upgraded from {was}", askerName);
        await db.SaveChangesAsync();
        // The price moved from R50 to the package: a card on file is told the new amount at once (CompanyBilling, stage C).
        Counted(t.Id);
        return "";
    }

    /// <summary>
    /// Why this Basic company may not move down to the Free version now, or "" (2026-10-25, §163; Clint asked whether the Basic
    /// version may go down to the Free one). The owner only; the Free version's terms ticked, by their fingerprint; and no more
    /// computers installed than the Free version has — the owner removes the others first, so nothing is cut off by surprise.
    /// </summary>
    public async Task<string> DowngradeProblemAsync(int tenantId, TenantRole asker, bool accepted, string termsSha)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == tenantId);
        if (t == null) return "Not found.";
        if (!FreePlan.IsBasic(t.Plan)) return "Only the Basic version moves down to the Free version.";
        if (!Permissions.CanAcceptPrice(asker)) return "Only the owner can change the company's version.";
        if (!accepted) return "Please read the terms and tick the box to accept them.";
        if (termsSha != Terms.For(t.Region, FreePlan.Name).Sha256) return TermsChanged;
        int installed = await InstalledMachinesAsync(tenantId);
        if (installed > FreePlan.FreeMachines)
            return $"{installed} computers are installed, and the Free version has {FreePlan.FreeMachines}. Remove the others in the console or on Your computers first. Nothing was changed.";
        return "";
    }

    /// <summary>
    /// Moves a Basic company down to the Free version (2026-10-25, §163), AFTER its card was cancelled at PayFast —
    /// <see cref="CompanyBilling.DowngradeToFreeAsync"/> is the one way in, and asks <see cref="DowngradeProblemAsync"/> first.
    /// At once: Active, no trial, the computers paid for back to none, the Free terms kept word for word, the change on Billing.
    /// The month already paid is not refunded: the terms say so (section 11). Empty for done, or why not.
    /// </summary>
    public async Task<string> DowngradeToFreeAsync(int tenantId, string askerName, string askerEmail, string fromAddress)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tenants.Include(x => x.Technicians).FirstOrDefaultAsync(x => x.Id == tenantId);
        if (t == null) return "Not found.";
        if (!FreePlan.IsBasic(t.Plan)) return "Only the Basic version moves down to the Free version.";
        var terms = Terms.For(t.Region, FreePlan.Name);
        var before = Pricing.BillFor(t, 1);
        t.Plan = FreePlan.Name;
        t.BasicMachines = 0;
        if (t.Status == TenantStatus.Trial) t.Status = TenantStatus.Active;
        t.TrialEndsUtc = null;
        t.TrialNoticesSent = "";
        db.TermsAcceptances.Add(new TermsAcceptance
        {
            TenantId = t.Id, Region = terms.Region, Version = terms.Version, Draft = terms.Draft, Text = terms.Source, Sha256 = terms.Sha256,
            AcceptedByName = askerName, AcceptedByEmail = askerEmail, FromAddress = fromAddress, AcceptedUtc = DateTime.UtcNow
        });
        Agreed(db, t, new Pricing.PriceRise(before, Pricing.BillFor(t, 1)), "Moved down from the Basic version to the Free version", askerName);
        await db.SaveChangesAsync();
        return "";
    }

    public async Task<string> SetStatusAsync(int tenantId, TenantStatus status)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tenants.FindAsync(tenantId);
        if (t == null) return "Tenant not found.";
        t.Status = status;
        if (status == TenantStatus.Active && t.ApprovedUtc == null) t.ApprovedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return "";
    }

    public async Task<List<Device>> DevicesAsync(int tenantId)
    { await using var db = await _dbf.CreateDbContextAsync(); return await db.Devices.Where(d => d.TenantId == tenantId).OrderByDescending(d => d.LastSeenUtc).ToListAsync(); }

    public async Task<List<SessionLog>> SessionsAsync(int tenantId, int take = 100)
    { await using var db = await _dbf.CreateDbContextAsync(); return await db.Sessions.Where(s => s.TenantId == tenantId).OrderByDescending(s => s.StartedUtc).Take(take).ToListAsync(); }

    public async Task<(int techs, int devices, int sessions)> CountsAsync(int tenantId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return (await db.Technicians.CountAsync(t => t.TenantId == tenantId && !t.Disabled && (t.Role & TenantRoles.Seated) != 0),
                await db.Devices.CountAsync(d => d.TenantId == tenantId),
                await db.Sessions.CountAsync(s => s.TenantId == tenantId));
    }

    public async Task<Tenant?> BySlugAsync(string slug)
    { await using var db = await _dbf.CreateDbContextAsync(); return await db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug); }

    public async Task<Tenant?> ByKeyAsync(string key)
    { await using var db = await _dbf.CreateDbContextAsync(); return await db.Tenants.FirstOrDefaultAsync(t => t.TenantKey == key); }

    public async Task<Tenant?> ByIdAsync(int id)
    { await using var db = await _dbf.CreateDbContextAsync(); return await db.Tenants.FindAsync(id); }

    public async Task<List<Tenant>> AllAsync()
    { await using var db = await _dbf.CreateDbContextAsync(); return await db.Tenants.Include(t => t.Technicians).OrderBy(t => t.CompanyName).ToListAsync(); }

    /// <summary>
    /// Saves which categories and steps the company uses (Job types, HANDOVER §166) — the only thing that writes
    /// <c>Tenant.JobTypesOff</c>. A supervisor or the owner; the list is cleaned (<see cref="JobTypes.Clean"/>) and then
    /// judged (<see cref="JobTypes.Problem"/>), and a route that could not finish is refused with nothing saved. Tickets
    /// already open keep the route they started on; only new ones follow this.
    /// </summary>
    public async Task<string> SetJobTypesAsync(int tenantId, TenantRole role, string by, IEnumerable<string> off)
    {
        if (!Permissions.CanSetJobTypes(role)) return "Job types are a supervisor's or the owner's to change.";
        var clean = JobTypes.Clean(off);
        var problem = JobTypes.Problem(clean);
        if (problem.Length > 0) return problem;
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tenants.FindAsync(tenantId);
        if (t == null) return "Tenant not found.";
        t.JobTypesOff = clean;
        t.JobTypesChosenUtc = DateTime.UtcNow;
        t.JobTypesChosenBy = by;
        await db.SaveChangesAsync();
        return "";
    }

    /// <summary>
    /// The dashboard's once-only trip to Job types (§167): true for the ONE call that marks it shown, false for every call
    /// after — one conditional update, so two tabs opened at once cannot both be sent there.
    /// </summary>
    public async Task<bool> MarkJobTypesShownAsync(int tenantId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Tenants.Where(t => t.Id == tenantId && t.JobTypesShownUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.JobTypesShownUtc, DateTime.UtcNow)) == 1;
    }

    public async Task<string> UpdateBrandingAsync(int tenantId, Action<Tenant> apply)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tenants.FindAsync(tenantId);
        if (t == null) return "Tenant not found.";
        apply(t);
        await db.SaveChangesAsync();
        CompanyClock.Forget(tenantId);   // its time zone may be what changed
        BrandingSaved(tenantId);
        return "";
    }

    /// <summary>
    /// A company's branding was saved (2026-10-07, roadmap 4.6) — possibly with nothing a machine sees changed: the
    /// hub compares fingerprints and sends only a real change. The command line is a separate process, so a change
    /// made there reaches each machine at its next connect instead.
    /// </summary>
    public event Action<int>? BrandingChanged;
    private void BrandingSaved(int tenantId) { try { BrandingChanged?.Invoke(tenantId); } catch { } }

    /// <summary>What a company's machines are sent: its look and its logo (<see cref="BrandRefresh"/>). Null for no company.</summary>
    public async Task<BrandingMsg?> BrandingMessageAsync(int tenantId)
    {
        var t = await ByIdAsync(tenantId);
        if (t == null) return null;
        byte[]? logo = null;
        try
        {
            var path = string.IsNullOrEmpty(t.LogoFile) ? "" : Path.Combine(LogoDir, t.LogoFile);
            if (path.Length > 0 && File.Exists(path)) logo = await File.ReadAllBytesAsync(path);
        }
        catch { }
        return BrandRefresh.Message(ToBranding(t), logo);
    }

    public async Task<string> SaveLogoAsync(int tenantId, Stream content, string originalName)
    {
        var ext = Path.GetExtension(originalName).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg")) return "Logo must be a PNG or JPG.";
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tenants.FindAsync(tenantId);
        if (t == null) return "Tenant not found.";
        var file = $"{t.Slug}{ext}";
        await using (var fs = File.Create(Path.Combine(LogoDir, file))) await content.CopyToAsync(fs);
        t.LogoFile = file;
        await db.SaveChangesAsync();
        BrandingSaved(tenantId);
        return "";
    }

    // ---- people ----
    /// <summary>
    /// Adds a person. <paramref name="password"/> null is the normal way since 2026-10-01: they are
    /// INVITED, with no password until they choose one from the mail AccountLinks sends. A password
    /// is given only by the command line, for a company's very first owner.
    ///
    /// <para><b>Five seats; more on request (2026-10-25).</b> Past the seats the company may have
    /// (<see cref="Pricing.MostSeatsFor"/> — five unless we allowed more) the add is refused
    /// (<see cref="Pricing.LimitProblem(Tenant, IEnumerable{Technician}, Technician?, TenantRole)"/>), except for
    /// Anywhere IT. Within an allowance we gave, each person past the package costs one more seat:
    /// <paramref name="asker"/> is the boxes of whoever is adding them — null for the platform's own command
    /// line, which is not asked — and a person asking must be the owner, and must pass
    /// <paramref name="agreed"/> = <see cref="Pricing.PriceRise.Token"/> of the rise they were shown; anything
    /// else adds NOBODY. The yes is kept as a <see cref="SeatAgreement"/>.</para>
    /// </summary>
    public async Task<(Technician? tech, string error)> AddTechnicianAsync(int tenantId, string email, string? password, string? displayName, TenantRole role = TenantRole.Technician,
        TenantRole? asker = null, string? agreed = null, string agreedBy = "")
    {
        email = email.Trim().ToLowerInvariant();
        if (password != null && Passwords.Problem(password, email) is { Length: > 0 } weak) return (null, weak);
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tenants.Include(x => x.Technicians).FirstOrDefaultAsync(x => x.Id == tenantId);
        if (t == null) return (null, "Tenant not found.");
        if (await NewPersonProblem(db, t, email, displayName, role) is { } problem) return (null, problem);
        // Several owners (2026-10-08) — but only an owner makes another one.
        if (role.Has(TenantRoles.Owners) && asker != null && !Permissions.CanChangeOwner(asker.Value)) return (null, OnlyAnOwner);
        var name = string.IsNullOrWhiteSpace(displayName) ? email.Split('@')[0] : displayName!.Trim();
        // Every box takes a seat but one owner-only login (Pricing.SeatsFor); past the package, that costs more.
        var rise = Pricing.RiseIf(t, t.Technicians, role);
        if (rise != null && asker != null && PriceRefusal(rise, asker.Value, agreed, $"Adding {name}") is { } refused) return (null, refused);
        var (salt, hash) = password == null ? ("", "") : Passwords.Create(password);
        var tech = new Technician { TenantId = tenantId, Email = email, DisplayName = name, Salt = salt, Hash = hash, Role = role, SecurityStamp = AuthService.NewStamp() };
        db.Technicians.Add(tech);
        if (rise != null) Agreed(db, t, rise, $"Added {name} ({TenantRoles.Label(role)})", asker == null ? "" : agreedBy);
        await db.SaveChangesAsync();
        Counted(tenantId);
        return (tech, "");
    }

    /// <summary>
    /// Why this person cannot be added, or null. The same checks <see cref="AddTechnicianAsync"/>
    /// makes, in the same words.
    /// </summary>
    public async Task<string?> NewPersonProblemAsync(int tenantId, string email, string? displayName, TenantRole role)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tenants.Include(x => x.Technicians).FirstOrDefaultAsync(x => x.Id == tenantId);
        return t == null ? "Tenant not found." : await NewPersonProblem(db, t, (email ?? "").Trim().ToLowerInvariant(), displayName, role);
    }

    /// <summary>The free version's one person: the owner, who is the technician too.</summary>
    public const TenantRole FreeRole = TenantRole.Owner | TenantRole.Technician;
    public const string FreeOnePerson = "The Free and Basic versions are one person — the owner, who is also the technician. Upgrade to the full version to add people.";

    private static async Task<string?> NewPersonProblem(PlatformDb db, Tenant t, string email, string? displayName, TenantRole role)
    {
        // The free version is ONE person, the owner who is also the technician (2026-10-06).
        if (FreePlan.Is(t.Plan) && t.Technicians.Count > 0) return FreeOnePerson;
        if (!email.Contains('@') || email.Length < 5) return "Enter a valid email address.";
        // A ticket keeps the people on it as names joined by commas (Assignees), so a name with a comma
        // in it would read as two people.
        if (displayName?.Contains(',') == true) return "A name cannot contain a comma. Use \"Jason Smith\" rather than \"Smith, Jason\".";
        if (await db.Technicians.AnyAsync(x => x.Email == email)) return "That email is already registered.";
        if (role == TenantRole.None || (role & ~TenantRoles.All) != 0) return "Tick at least one role.";
        // A company may have several owners since 2026-10-08 (Clint); every owner-only login past the first is a seat.
        // Five seats, or the most we allowed (2026-10-25), whoever asks — before anybody is asked to agree a price. Anywhere IT excepted.
        return Pricing.LimitProblem(t, t.Technicians, null, role);
    }

    /// <summary>
    /// What adding somebody with these boxes — or giving <paramref name="email"/> these boxes — would do to the
    /// monthly price, or null when it does nothing. The People page asks this before it shows the owner Accept /
    /// Don't do it; the change itself asks again, so a stale page cannot slip past (2026-10-25, as before 2026-10-22).
    /// </summary>
    public async Task<Pricing.PriceRise?> PriceRiseAsync(int tenantId, TenantRole roleAfter, string? email = null)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tenants.Include(x => x.Technicians).FirstOrDefaultAsync(x => x.Id == tenantId);
        if (t == null) return null;
        var who = email == null ? null : t.Technicians.FirstOrDefault(x => x.Email == email.Trim().ToLowerInvariant());
        // Past what we allowed there is no price to agree: the change itself is refused, with the reason.
        if (Pricing.LimitProblem(t, t.Technicians, who, roleAfter) != null) return null;
        return Pricing.RiseIf(t, t.Technicians, roleAfter, who);
    }

    /// <summary>Why a change that raises the price may not go ahead — null when the owner has agreed to exactly this figure.</summary>
    private static string? PriceRefusal(Pricing.PriceRise rise, TenantRole asker, string? agreed, string doing)
    {
        var sum = $"{doing} would make {rise.After.People} people in seats, which takes the monthly price from {rise.Before.Total} to {rise.After.Total}.";
        if (!Permissions.CanAcceptPrice(asker)) return sum + " Only an owner can agree to a higher price — please ask them. Nothing was changed.";
        if (agreed != rise.Token) return sum + " Please agree to the new price first. Nothing was changed.";
        return null;
    }

    /// <summary>
    /// The owner asks us for more seats (2026-10-25, Billing → Ask for more seats): how many in all, and why. Kept on the
    /// company — a newer ask replaces the last — for Owner → Approvals, where we allow it or not (<see cref="AdminCompanies"/>).
    /// Only an owner; never the free version (it upgrades instead) nor a company not billed (it is not limited).
    /// </summary>
    /// <summary>
    /// What paying for <paramref name="machines"/> computers would do to a Basic company's price — a rise to agree, or null when
    /// it costs nothing more (fewer, or the same).
    /// </summary>
    public static Pricing.PriceRise? BasicMachinesRise(Tenant t, int machines)
    {
        var before = Pricing.BillFor(t, 1);
        var after = Pricing.BillFor(new Tenant { Region = t.Region, Plan = t.Plan, NotBilled = t.NotBilled, BasicMachines = machines }, 1);
        return !t.NotBilled && after.Monthly > before.Monthly ? new Pricing.PriceRise(before, after) : null;
    }

    /// <summary>The computers a company has installed now — by hardware, as the hub counts them (a reinstall is one computer).</summary>
    public async Task<int> InstalledMachinesAsync(int tenantId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var rows = await db.Devices.Where(d => d.TenantId == tenantId).Select(d => new { d.DeviceId, d.HardwareId }).ToListAsync();
        return rows.Select(d => d.HardwareId.Length > 0 ? "hw:" + d.HardwareId : "id:" + d.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    }

    /// <summary>
    /// The Basic version's computers, paid for in all (2026-10-25, Clint: extra machines at R10 / $1 / £1 / €1 each, "no owner
    /// approval from my side necessary", at most 20). The OWNER sets it on Billing — the Basic version's one person is its owner —
    /// and a rise must name the exact price shown (<see cref="Pricing.PriceRise.Token"/>), kept as a <see cref="SeatAgreement"/>;
    /// fewer is no rise and needs no yes, but never below the computers installed now. PayFast's amount follows (<see cref="Counted"/>).
    /// "" for done (or nothing to do), else why not.
    /// </summary>
    public async Task<string> SetBasicMachinesAsync(int tenantId, int machines, TenantRole asker, string? agreed, string agreedBy)
    {
        if (!Permissions.CanAcceptPrice(asker)) return "Only the owner can change how many computers the company pays for.";
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tenants.FirstOrDefaultAsync(x => x.Id == tenantId);
        if (t == null) return "Not found.";
        if (!FreePlan.IsBasic(t.Plan)) return FreePlan.IsFree(t.Plan)
            ? "The Free version has one computer. Upgrade to the Basic version to pay for more."
            : "Extra computers are for the Basic version — the full version has every computer you look after.";
        if (machines < FreePlan.MaxMachines) return $"The Basic version has {FreePlan.MaxMachines} computers in its price — choose {FreePlan.MaxMachines} or more.";
        if (machines > FreePlan.MostMachines) return $"{FreePlan.MostMachines} computers is the most the Basic version has. For more, upgrade to the full version.";
        int now = FreePlan.MachinesFor(t.Plan, t.BasicMachines);
        if (machines == now) return "";
        int installed = await InstalledMachinesAsync(tenantId);
        if (machines < installed)
            return $"{installed} computers are installed. Remove one in the console or on Your computers first, then pay for fewer. Nothing was changed.";
        var rise = BasicMachinesRise(t, machines);
        if (rise != null && agreed != rise.Token)
            return $"Paying for {machines} computers takes the monthly price from {rise.Before.Total} to {rise.After.Total}. Please agree to the new price first. Nothing was changed.";
        t.BasicMachines = machines == FreePlan.MaxMachines ? 0 : machines;
        if (rise != null) Agreed(db, t, rise, $"Computers {now} → {machines}", agreedBy);
        await db.SaveChangesAsync();
        Counted(t.Id);
        return "";
    }

    public async Task<(Tenant? t, string error)> AskForSeatsAsync(int tenantId, int seats, string? note, TenantRole asker, string by)
    {
        if (!Permissions.CanAcceptPrice(asker)) return (null, "Only an owner can ask for more seats — please ask them.");
        note = (note ?? "").Trim();
        if (note.Length > 500) return (null, "Keep the reason to 500 characters.");
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tenants.Include(x => x.Technicians).FirstOrDefaultAsync(x => x.Id == tenantId);
        if (t == null) return (null, "Tenant not found.");
        if (FreePlan.Is(t.Plan)) return (null, FreeOnePerson);
        if (!Pricing.Limited(t)) return (null, "Your company is not limited, so there is nothing to ask for.");
        int most = Pricing.MostSeatsFor(t);
        if (seats <= most) return (null, $"Your company may already have {most} seats. Ask for more than that.");
        if (seats > Pricing.MostAllowed) return (null, $"Ask for {Pricing.MostAllowed} seats at most here; for more, reply to any email from us.");
        t.SeatsAskedFor = seats; t.SeatsAskedNote = note; t.SeatsAskedBy = by; t.SeatsAskedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return (t, "");
    }

    /// <summary>The record of a yes to a higher price, in the same save as the change it paid for — a seat past the package, or the free version's upgrade.</summary>
    private static void Agreed(PlatformDb db, Tenant t, Pricing.PriceRise rise, string what, string agreedBy) =>
        db.SeatAgreements.Add(new SeatAgreement
        {
            TenantId = t.Id, People = rise.After.People, Included = rise.After.Included, Region = rise.After.Region.Code,
            Monthly = rise.After.Monthly, MonthlyBefore = rise.Before.Monthly, What = what,
            AgreedBy = agreedBy.Length > 0 ? agreedBy : "the platform (command line)", AgreedUtc = DateTime.UtcNow
        });

    /// <summary>Every price rise the owner agreed to, newest first — for the Billing page.</summary>
    public async Task<List<SeatAgreement>> SeatAgreementsAsync(int tenantId, int take = 10)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.SeatAgreements.Where(a => a.TenantId == tenantId).OrderByDescending(a => a.AgreedUtc).Take(take).ToListAsync();
    }

    /// <summary>
    /// Set the boxes somebody holds — the WHOLE set, not one box at a time, because that is what a
    /// row of tick boxes posts. <paramref name="asker"/> is the boxes of whoever is asking; null is
    /// the platform's own command line, which may do anything except leave the company with no owner.
    ///
    /// <para><b>Several owners (2026-10-08, Clint).</b> The Owner box is a tick box like the others,
    /// but only an OWNER may tick or untick it, or change an owner's row at all — a supervisor runs the
    /// team, not the people who own the company. The last owner keeps it (<see cref="LastOwner"/>).</para>
    /// </summary>
    /// <para>A box that makes somebody take a seat they did not have — the owner ticking Technician
    /// beside Owner, or a second owner-only login — can raise the price, or be refused when every seat
    /// the company may have is in use, and is gated exactly as <see cref="AddTechnicianAsync"/> is.</para>
    public async Task<string> SetRoleAsync(int tenantId, string email, TenantRole role, TenantRole? asker = null, string? agreed = null, string agreedBy = "")
    {
        if (role == TenantRole.None || (role & ~TenantRoles.All) != 0) return "Tick at least one role.";
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tenants.Include(x => x.Technicians).FirstOrDefaultAsync(x => x.Id == tenantId);
        var tech = t?.Technicians.FirstOrDefault(x => x.Email == email.Trim().ToLowerInvariant());
        if (t == null || tech == null) return "Not found.";
        bool toOwner = role.Has(TenantRoles.Owners);
        if (FreePlan.Is(t.Plan) && role != FreeRole) return FreeOnePerson;
        if ((tech.IsOwner || toOwner) && asker != null && !Permissions.CanChangeOwner(asker.Value)) return OnlyAnOwner;
        if (tech.IsOwner && !toOwner && IsLastOwner(t, tech)) return LastOwner;
        if (Pricing.LimitProblem(t, t.Technicians, tech, role) is { } full) return full;
        var rise = Pricing.RiseIf(t, t.Technicians, role, tech);
        if (rise != null && asker != null && PriceRefusal(rise, asker.Value, agreed, $"Giving {tech.DisplayName} a seat") is { } refused) return refused;
        if (rise != null) Agreed(db, t, rise, $"{tech.DisplayName} became {TenantRoles.Label(role)}", asker == null ? "" : agreedBy);
        bool changed = tech.Role != role;
        tech.Role = role;
        // What they may do changed, so what they are signed in with no longer says so: sign them out.
        if (changed) tech.SecurityStamp = AuthService.NewStamp();
        await db.SaveChangesAsync();
        if (changed) Changed(tech);
        return "";
    }

    public const string OnlyAnOwner = "Only an owner can make somebody an owner, or change an owner's roles.";
    public const string LastOwner = "A company always has at least one owner. Make somebody else an owner first.";

    /// <summary>Whether <paramref name="tech"/> is the only owner left — never removed, never unticked.</summary>
    private static bool IsLastOwner(Tenant t, Technician tech) =>
        tech.IsOwner && !t.Technicians.Any(x => x.IsOwner && !x.Disabled && x.Id != tech.Id);

    /// <summary>
    /// Hand the company to somebody else, outright. Platform command line only. The new owner gains the
    /// Owner box and keeps every box they had; EVERY other owner loses it and becomes a supervisor — which
    /// needs a seat if they did not already hold one. To add an owner beside the others, tick the box
    /// instead (<see cref="SetRoleAsync"/>).
    /// </summary>
    public async Task<string> SetOwnerAsync(int tenantId, string email)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tenants.Include(x => x.Technicians).FirstOrDefaultAsync(x => x.Id == tenantId);
        var tech = t?.Technicians.FirstOrDefault(x => x.Email == email.Trim().ToLowerInvariant());
        if (t == null || tech == null) return "Not found.";
        var others = t.Technicians.Where(x => x.IsOwner && x.Id != tech.Id).ToList();
        if (tech.IsOwner && others.Count == 0) return "";
        static TenantRole Demoted(TenantRole r) => (r & ~TenantRole.Owner) | TenantRole.Admin;
        TenantRole After(Technician x) => x.Id == tech.Id ? x.Role | TenantRole.Owner : others.Contains(x) ? Demoted(x.Role) : x.Role;
        int now = Pricing.PeopleInSeats(t.Technicians), after = Pricing.SeatsAfter(t.Technicians, After);
        // The seat limit holds here too: a full company frees a seat before an old owner takes one.
        if (Pricing.LimitProblem(t, now, after) is { } full) return full;
        // The platform's command line only: no-one to ask, but a seat an old owner now takes past the package is recorded.
        if (Pricing.RiseIf(t, now, after) is { } rise)
            Agreed(db, t, rise, $"{string.Join(", ", others.Select(x => x.DisplayName))} handed the company to {tech.DisplayName} and became a supervisor", "");
        foreach (var o in others) { o.Role = Demoted(o.Role); o.SecurityStamp = AuthService.NewStamp(); }
        tech.Role |= TenantRole.Owner;
        tech.SecurityStamp = AuthService.NewStamp();
        await db.SaveChangesAsync();
        foreach (var o in others) Changed(o);
        Changed(tech);
        return "";
    }

    public async Task<List<Technician>> TechniciansAsync(int tenantId)
    { await using var db = await _dbf.CreateDbContextAsync(); return await db.Technicians.Where(t => t.TenantId == tenantId).OrderBy(t => t.DisplayName).ToListAsync(); }

    /// <summary>
    /// Takes somebody off the company. <paramref name="asker"/> is whoever asks — null for the command line.
    /// An owner is removed only by another owner (2026-10-08), and the last owner never.
    /// </summary>
    public async Task<string> RemoveTechnicianAsync(int tenantId, string email, TenantRole? asker = null)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tenants.Include(x => x.Technicians).FirstOrDefaultAsync(x => x.Id == tenantId);
        var tech = t?.Technicians.FirstOrDefault(x => x.Email == email.Trim().ToLowerInvariant());
        if (t == null || tech == null) return "Technician not found.";
        if (tech.IsOwner && asker != null && !Permissions.CanChangeOwner(asker.Value)) return "Only an owner can remove an owner.";
        if (IsLastOwner(t, tech)) return LastOwner;
        db.Technicians.Remove(tech);
        await db.SaveChangesAsync();
        // Out of every console and session now — not at the next server restart, which is what it
        // used to be — and out of the portal at their next page.
        Changed(tech);
        return "";
    }

    public async Task<string> SetPasswordAsync(int tenantId, string email, string password)
    {
        if (Passwords.Problem(password, email) is { Length: > 0 } weak) return weak;
        await using var db = await _dbf.CreateDbContextAsync();
        var tech = await db.Technicians.FirstOrDefaultAsync(t => t.TenantId == tenantId && t.Email == email.Trim().ToLowerInvariant());
        if (tech == null) return "Technician not found.";
        (tech.Salt, tech.Hash) = Passwords.Create(password);
        // A new password is usually because the old one got out: everything signed in with it goes.
        tech.SecurityStamp = AuthService.NewStamp();
        await db.SaveChangesAsync();
        Changed(tech);
        return "";
    }

    /// <summary>
    /// Lost phone: clears their authenticator and recovery codes, and signs them out everywhere.
    /// At their next sign-in they set up an authenticator again, as on their first.
    /// </summary>
    public async Task<string> ResetTwoFactorAsync(int tenantId, string email)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var tech = await db.Technicians.FirstOrDefaultAsync(t => t.TenantId == tenantId && t.Email == email.Trim().ToLowerInvariant());
        if (tech == null) return "Not found.";
        tech.TotpSecretEnc = ""; tech.TotpEnabledUtc = null; tech.TotpLastStep = 0; tech.RecoveryCodeHashes = "";
        tech.SecurityStamp = AuthService.NewStamp();
        await db.SaveChangesAsync();
        Changed(tech);
        return "";
    }

    /// <summary>"Sign out everywhere": every console, session and browser this person is signed in on.</summary>
    public async Task<string> SignOutEverywhereAsync(int tenantId, string email)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var tech = await db.Technicians.FirstOrDefaultAsync(t => t.TenantId == tenantId && t.Email == email.Trim().ToLowerInvariant());
        if (tech == null) return "Not found.";
        tech.SecurityStamp = AuthService.NewStamp();
        await db.SaveChangesAsync();
        Changed(tech);
        return "";
    }

    /// <summary>
    /// Somebody's password, boxes or two-factor changed, or they were removed. Program.cs points
    /// this at <see cref="AuthService.RecheckAsync"/>, so the running server signs them out at once.
    /// The command line runs in a process of its own; the running server's 15 s sweep catches that.
    /// </summary>
    public event Action<int>? PersonChanged;
    private void Changed(Technician t) { try { PersonChanged?.Invoke(t.Id); } catch { } Counted(t.TenantId); }

    /// <summary>
    /// Somebody was added, removed or had their boxes changed: the seats — and so the month's price — may be different
    /// (2026-10-13, PayFast stage C). <c>CompanyBilling</c> listens and tells PayFast the new amount. The command line is
    /// another process; the PayFast sweep catches what it changes.
    /// </summary>
    public event Action<int>? PeopleChanged;
    private void Counted(int tenantId) { try { PeopleChanged?.Invoke(tenantId); } catch { } }

    /// <summary>The package's seats were changed on the owner pages, so the price may be different too (2026-10-25).</summary>
    public void PriceMayHaveChanged(int tenantId) => Counted(tenantId);

    // ---- branding export (what the installers/apps download) ----
    public Branding ToBranding(Tenant t) => new()
    {
        CompanyName = t.CompanyName, ProductName = t.ProductName,
        SupportEmail = t.SupportEmail, SupportPhone = t.SupportPhone,
        PrimaryColor = t.PrimaryColor, AccentColor = t.AccentColor,
        TechnicianTheme = t.TechnicianTheme, ClientTheme = t.ClientTheme,
        WelcomeText = t.WelcomeText, Logo = string.IsNullOrEmpty(t.LogoFile) ? "" : "logo" + Path.GetExtension(t.LogoFile),
        ServerUrl = _publicUrl.Replace("https://", "wss://").Replace("http://", "ws://"),
        RequireConsent = t.RequireConsent, UnattendedRequireConsent = t.UnattendedRequireConsent,
        ConsentTimeoutSeconds = t.ConsentTimeoutSeconds, TenantKey = t.TenantKey,

        // HealthCheckReady, not HealthCheckEnabled: a tenant that has switched the check on but
        // has not finished the mail settings would otherwise ship installers that dutifully run
        // every month and produce a report with nowhere to go. Nothing about the mailbox itself
        // crosses into the installer.
        HealthCheckEnabled = t.HealthCheckReady,
        HealthCheckDayOfMonth = t.HealthCheckDayOfMonth,
        HealthCheckRunMaintenance = t.HealthCheckRunMaintenance,
        Free = FreePlan.Is(t.Plan),
        // The Basic version (2026-10-25, §163): its installer may leave the password blank, for a computer reached attended.
        Basic = FreePlan.IsBasic(t.Plan)
    };
}
