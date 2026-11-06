using System.Globalization;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// What a company pays a month, and why — the ONE place the prices live on the server
/// (<c>docs/PRICING.md</c> is the authority; the landing page keeps its own copy in <c>PRICES</c>).
///
/// <para>Decided by Clint 2026-09-16 and 2026-10-05, changed 2026-10-08, 2026-10-22 and 2026-10-25: <b>FIVE seats, any mix
/// of roles; more ONLY on request.</b> On 2026-10-22 Clint made it "a 5 license only product ... available to small I.T
/// companies only"; on 2026-10-25 he kept five for everybody at signup but let a company ASK for more: "if I decide it's fine,
/// I right click on their tenant, allow more seats, then they can add more seats", charged through PayFast. So the platform
/// owner sets <see cref="Tenant.MostSeats"/> for that company alone (<see cref="MostSeatsFor"/>), and each seat past the
/// package is the region's <see cref="Region.ExtraSeat"/> — R300 in South Africa, the price it was until 2026-10-22 — which
/// the owner agrees to before it is added (<see cref="RiseIf(Tenant, IEnumerable{Technician}, TenantRole, Technician?)"/>,
/// <see cref="SeatAgreement"/>), from the next bill (no part-months). ONE owner-only login is free on top; every other person
/// who can sign in takes a seat — an owner who also ticks Supervisor, Technician or Office administrator, and any second
/// owner-only login (<see cref="SeatsFor"/>). <b>Anywhere IT, Clint's own company, is never limited</b> — the company not
/// billed (<see cref="Limited"/>).</para>
///
/// <para>These are prices, not conversions. R500 is not $49 at any exchange rate, on purpose.</para>
/// </summary>
public static class Pricing
{
    /// <summary>Seats in the package price, unless the platform owner has set fewer for a company (<see cref="Tenant.Seats"/>).</summary>
    public const int IncludedSeats = 5;

    /// <summary>The package's most (2026-10-22): a package is never above five. More seats are extras, and only on request (<see cref="MostSeatsFor"/>).</summary>
    public const int MaxSeats = IncludedSeats;

    /// <summary>The most the platform owner may allow any one company (2026-10-25) — a guard against a typed 500, not a policy.</summary>
    public const int MostAllowed = 50;

    /// <summary>
    /// Why five, and how to have more, in the words every page uses (Clint, 2026-10-22: "our vision is to make proper I.T
    /// support more affordable for smaller I.T companies"; 2026-10-25: "five seats; more on request"; 2026-10-27: not only IT
    /// companies — "other types of industries also require this type of software").
    /// </summary>
    public const string WhyFive = "We built this to make proper support software affordable for small businesses — so every company starts with five seats, and more are on request.";

    /// <summary>Whether a company is held to <see cref="MostSeatsFor"/>: every company except the one not billed — Anywhere IT, Clint's own (2026-10-22).</summary>
    public static bool Limited(Tenant t) => !t.NotBilled;

    /// <summary>
    /// The seats in this company's package: <see cref="Tenant.Seats"/>, five unless the platform owner set fewer on Owner →
    /// All companies, never above <see cref="MaxSeats"/>. Past it each seat is an extra (<see cref="BillFor"/>).
    /// </summary>
    public static int SeatsAllowed(Tenant t) => Math.Clamp(t.Seats > 0 ? t.Seats : IncludedSeats, 1, MaxSeats);

    /// <summary>
    /// The most seats this company may fill (2026-10-25): its package, or more when we have allowed it more
    /// (<see cref="Tenant.MostSeats"/>, never below the package nor above <see cref="MostAllowed"/>).
    /// </summary>
    public static int MostSeatsFor(Tenant t) => Math.Max(SeatsAllowed(t), Math.Min(t.MostSeats, MostAllowed));

    /// <summary>Whether we have allowed this company seats past its package — what shows the extra-seat price on its pages.</summary>
    public static bool MayHaveExtra(Tenant t) => MostSeatsFor(t) > SeatsAllowed(t);

    /// <summary>One region's money a month: the package of five seats, each seat we allow past it, the Basic version, and each computer it adds past three (all 2026-10-25).</summary>
    public sealed record Region(string Code, string Name, string Symbol, string Currency, decimal Package, decimal ExtraSeat, decimal Basic, decimal ExtraMachine)
    {
        /// <summary>The name inside a sentence: "open in the United Kingdom", "terms for South Africa".</summary>
        public string In => Code is "US" or "UK" ? "the " + Name : Name;
    }

    /// <summary>The five places we sell, in the order signup offers them. Canada pays the US price, in US dollars (assumed 2026-10-05; PRICING.md).</summary>
    public static readonly Region[] Regions =
    {
        new("ZA", "South Africa", "R", "South African rand", 500m, 300m, 50m, 10m),
        new("US", "United States", "$", "US dollars", 49m, 20m, 5m, 1m),
        new("CA", "Canada", "$", "US dollars", 49m, 20m, 5m, 1m),
        new("UK", "United Kingdom", "£", "pounds sterling", 49m, 20m, 5m, 1m),
        new("EU", "Europe", "€", "euros", 49m, 20m, 5m, 1m),
    };

    private static readonly HashSet<string> Europe = new(StringComparer.OrdinalIgnoreCase)
    {
        "Europe", "Ireland", "Germany", "Netherlands", "France", "Spain", "Italy", "Portugal", "Belgium", "Switzerland",
        "Austria", "Sweden", "Norway", "Denmark", "Finland", "Poland", "Luxembourg", "Greece", "Czech Republic", "Czechia"
    };

    /// <summary>The countries a company choosing Europe picks from at signup, in order — whose law its data falls under.</summary>
    public static readonly IReadOnlyList<string> EuropeanCountries =
        Europe.Where(c => c is not ("Europe" or "Czech Republic")).OrderBy(c => c, StringComparer.Ordinal).ToList();

    /// <summary>A region by its code; an unknown or empty one is South Africa, where every company is until it says otherwise.</summary>
    public static Region For(string? code) =>
        Regions.FirstOrDefault(r => r.Code.Equals((code ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) ?? Regions[0];

    public static Region For(Tenant t) => For(t.Region);

    /// <summary>
    /// The region a country on file belongs to — for companies that signed up before the region was
    /// asked, and for the migration that filled it in (its SQL says the same). No country at all — a
    /// company made on the command line — is South Africa; anywhere not one of the five pays in dollars.
    /// </summary>
    public static string RegionOfCountry(string? country)
    {
        var c = (country ?? "").Trim();
        if (c.Length == 0) return "ZA";
        if (c.Equals("South Africa", StringComparison.OrdinalIgnoreCase) || c.Equals("ZA", StringComparison.OrdinalIgnoreCase)) return "ZA";
        if (c.Equals("Canada", StringComparison.OrdinalIgnoreCase)) return "CA";
        if (c.Equals("United Kingdom", StringComparison.OrdinalIgnoreCase) || c.Equals("UK", StringComparison.OrdinalIgnoreCase)) return "UK";
        if (Europe.Contains(c)) return "EU";
        return "US";
    }

    /// <summary>"R1,100", "$69", "£89", "€89" — whole amounts, a comma for thousands.</summary>
    public static string Money(Region r, decimal amount) =>
        r.Symbol + amount.ToString(amount == decimal.Truncate(amount) ? "#,0" : "#,0.00", CultureInfo.InvariantCulture);

    /// <summary>What a month costs a company with this many people in seats: the package, and each seat past it (2026-10-25).</summary>
    public static Bill BillFor(Tenant t, int people)
    {
        var r = For(t);
        // The Basic version (2026-10-25, Clint): R50 / $5 / £5 / €5 a month, for its one person and its three computers —
        // no seats to count and nothing extra (FreePlan, which keeps its old name in the code).
        // The Free version (back 2026-10-25, §163): one person, one computer, nothing to pay.
        if (RemoteSupport.Shared.FreePlan.IsFree(t.Plan))
            return new Bill(r, Math.Min(people, 1), 1, 0, 0m, t.NotBilled, FreeVersion: true);
        if (RemoteSupport.Shared.FreePlan.IsBasic(t.Plan))
        {
            // Each computer past the three in the price is R10 / $1 / £1 / €1 (Clint, 2026-10-25), up to 20 in all.
            int extraMachines = RemoteSupport.Shared.FreePlan.MachinesFor(t.Plan, t.BasicMachines) - RemoteSupport.Shared.FreePlan.MaxMachines;
            return new Bill(r, Math.Min(people, 1), 1, 0, r.Basic + extraMachines * r.ExtraMachine, t.NotBilled, Basic: true, ExtraMachines: extraMachines);
        }
        int included = SeatsAllowed(t);
        int extra = Math.Max(0, people - included);
        // A pilot's package is free for as long as it is a pilot (Clint, 2026-10-15); seats we allow past it are not.
        return new Bill(r, people, included, extra, (t.Pilot ? 0m : r.Package) + extra * r.ExtraSeat, t.NotBilled, t.Pilot);
    }

    /// <summary>
    /// The seats a set of people takes (2026-10-08): everybody with a paid box (<see cref="TenantRoles.Paid"/>)
    /// is one seat however many boxes they hold, and of the owner-only logins ONE is free and every other
    /// is a seat. THE rule — everything that counts seats comes through here.
    /// </summary>
    public static int SeatsFor(IEnumerable<TenantRole> roles)
    {
        int paid = 0, ownerOnly = 0;
        foreach (var r in roles)
        {
            if (r.Has(TenantRoles.Paid)) paid++;
            else if (r.Has(TenantRoles.Owners)) ownerOnly++;
        }
        return paid + Math.Max(0, ownerOnly - 1);
    }

    /// <summary>The people in seats now: everybody not disabled, by <see cref="SeatsFor"/>.</summary>
    public static int PeopleInSeats(IEnumerable<Technician> people) => SeatsFor(people.Where(x => !x.Disabled).Select(x => x.Role));

    /// <summary>
    /// Who those seats are — everybody not disabled except the one free owner-only login (the first one
    /// made, when there are several). For the Billing page, so the count can be checked by eye.
    /// </summary>
    public static List<Technician> InSeats(IEnumerable<Technician> people)
    {
        var active = people.Where(x => !x.Disabled && x.Role != TenantRole.None).ToList();
        var free = active.Where(x => !x.Role.Has(TenantRoles.Paid) && x.Role.Has(TenantRoles.Owners)).OrderBy(x => x.Id).FirstOrDefault();
        return active.Where(x => !ReferenceEquals(x, free)).ToList();
    }

    /// <summary>
    /// The seats once <paramref name="who"/> holds <paramref name="roleAfter"/> — or, with <paramref name="who"/>
    /// null, once a new person with those boxes is added. A disabled person counts neither before nor after.
    /// </summary>
    public static int SeatsIf(IEnumerable<Technician> people, Technician? who, TenantRole roleAfter) =>
        SeatsAfter(people, x => Same(x, who) ? roleAfter : x.Role, who == null ? roleAfter : null);

    /// <summary>
    /// The seats once every person holds <paramref name="roleAfter"/>(them) — for a change that moves more
    /// than one person's boxes, as handing the company over does — plus <paramref name="added"/>, a new person.
    /// </summary>
    public static int SeatsAfter(IEnumerable<Technician> people, Func<Technician, TenantRole> roleAfter, TenantRole? added = null)
    {
        var roles = people.Where(x => !x.Disabled).Select(roleAfter).ToList();
        if (added is { } a) roles.Add(a);
        return SeatsFor(roles);
    }

    private static bool Same(Technician x, Technician? who) => who != null && (ReferenceEquals(x, who) || (who.Id != 0 && x.Id == who.Id));

    /// <summary>
    /// Why a change may not happen because of <see cref="MostSeatsFor"/>, or null. Only a change that ADDS a
    /// seat is refused, so a company somehow above the limit can still take people off and change boxes
    /// that cost nothing. Whoever asks; never for a company that is not <see cref="Limited"/>.
    /// </summary>
    public static string? LimitProblem(Tenant t, IEnumerable<Technician> people, Technician? who, TenantRole roleAfter)
    {
        var list = people as IList<Technician> ?? people.ToList();
        return LimitProblem(t, PeopleInSeats(list), SeatsIf(list, who, roleAfter));
    }

    /// <summary><see cref="LimitProblem(Tenant, IEnumerable{Technician}, Technician?, TenantRole)"/> for seats counted already.</summary>
    public static string? LimitProblem(Tenant t, int now, int after)
    {
        int most = MostSeatsFor(t);
        if (!Limited(t) || after <= now || after <= most) return null;
        return $"Your company may have {most} seats, and {(now >= most ? $"all {most} are" : $"{now} are")} in use. "
             + "Remove somebody, or untick the boxes of somebody who no longer needs a seat — or, if you need more, an owner can ask us for more seats on the Billing page. Nothing was changed.";
    }

    /// <summary>A month's bill, with the sums written out.</summary>
    public sealed record Bill(Region Region, int People, int Included, int Extra, decimal Monthly, bool NotBilled, bool Pilot = false, bool Basic = false,
        int ExtraMachines = 0, bool FreeVersion = false)
    {
        /// <summary>The Basic version's computers in all: the three in its price and the extras paid for. The Free version's one.</summary>
        public int Machines => FreeVersion ? RemoteSupport.Shared.FreePlan.FreeMachines : RemoteSupport.Shared.FreePlan.MaxMachines + ExtraMachines;
        /// <summary>"2 extra computers × R10 = R20" — the Basic version's extras, or empty.</summary>
        public string ExtraMachinesText => ExtraMachines == 0 ? ""
            : $"{ExtraMachines} extra computer{(ExtraMachines == 1 ? "" : "s")} × {Money(Region, Region.ExtraMachine)} = {Money(Region, ExtraMachines * Region.ExtraMachine)}";
        public string Total => Money(Region, Monthly);
        /// <summary>What the package costs this company: the region's price, nothing for a pilot, the Basic price for Basic.</summary>
        public decimal PackagePrice => FreeVersion ? 0m : Basic ? Region.Basic : Pilot ? 0m : Region.Package;
        public string PackageText => FreeVersion ? $"Nothing for {RemoteSupport.Shared.FreePlan.FreeTheName}"
            : Basic ? $"{Money(Region, Region.Basic)} for {RemoteSupport.Shared.FreePlan.TheName}"
            : Pilot ? $"{Included} seats free (pilot)" : $"{Money(Region, Region.Package)} for {Included} seats";
        public string ExtraText => Extra == 0 ? "" : $"{Extra} extra seat{(Extra == 1 ? "" : "s")} × {Money(Region, Region.ExtraSeat)} = {Money(Region, Extra * Region.ExtraSeat)}";
        /// <summary>"R500 a month (3 of 5 seats used)"; with extras "R500 for 5 seats + 2 extra seats × R300 = R600 → R1,100 a month".</summary>
        public string Sum => FreeVersion ? $"Nothing to pay — {RemoteSupport.Shared.FreePlan.FreeTheName}, one person and {RemoteSupport.Shared.FreePlan.FreeMachines} computer"
            : Basic ? (ExtraMachines > 0
                ? $"{PackageText} + {ExtraMachinesText} → {Total} a month — one person, {Machines} computers"
                : $"{Total} a month — {RemoteSupport.Shared.FreePlan.TheName}, one person and up to {RemoteSupport.Shared.FreePlan.MaxMachines} computers")
            : Extra > 0
            ? $"{PackageText} + {ExtraText} → {Total} a month"
            : Pilot ? $"{Total} a month — your {Included} pilot seats are free ({People} of {Included} used)"
            : $"{Total} a month ({People} of {Included} seats used)";
    }

    /// <summary>
    /// What adding someone with <paramref name="roleAfter"/> — or giving <paramref name="who"/> those boxes — would do to
    /// the price, or null when it does nothing (there is still a seat in the package, the change adds no seat, or the
    /// company is not billed). Asked by <see cref="TenantService"/>, which enforces it (2026-10-25, as before 2026-10-22).
    /// </summary>
    public static PriceRise? RiseIf(Tenant t, IEnumerable<Technician> people, TenantRole roleAfter, Technician? who = null)
    {
        var list = people as IList<Technician> ?? people.ToList();
        return RiseIf(t, PeopleInSeats(list), SeatsIf(list, who, roleAfter));
    }

    /// <summary><see cref="RiseIf(Tenant, IEnumerable{Technician}, TenantRole, Technician?)"/> for seats counted already.</summary>
    public static PriceRise? RiseIf(Tenant t, int seatsNow, int seatsAfter)
    {
        if (t.NotBilled || seatsAfter <= seatsNow) return null;
        var now = BillFor(t, seatsNow);
        var after = BillFor(t, seatsAfter);
        return after.Monthly > now.Monthly ? new PriceRise(now, after) : null;
    }

    /// <summary>
    /// A price going up, for somebody to agree to first — a seat past the package (2026-10-25), or the free version's
    /// upgrade (<see cref="TenantService.UpgradeQuote"/>). <see cref="Token"/> is what the agreement must name, so a figure
    /// that changed in the meantime is asked again.
    /// </summary>
    public sealed record PriceRise(Bill Before, Bill After)
    {
        public string Token => $"{After.Region.Code}:{After.People}:{After.Monthly.ToString(CultureInfo.InvariantCulture)}";
    }
}
