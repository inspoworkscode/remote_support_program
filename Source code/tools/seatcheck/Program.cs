// =====================================================================================
// seatcheck — FIVE seats; more ON REQUEST (2026-10-25, HANDOVER §160), every company but Anywhere IT.
//
// Clint, 2026-10-25: "all tenants only get the 5 seats option, but they can request more seats, and if I decide it's
// fine, I right click on their tenant, allow more seats, then they can add more seats to their basket" — through
// PayFast rather than invoiced by him. His answers: R300 a seat past five; a number per company, set by us; from the
// next payment, no part-months; a pilot's extras invoiced; the website says "five seats; more on request".
//
//   THE MIGRATIONS  (unchanged) regions from countries; FiveSeatPackage makes every package five.
//   THE RULE        (unchanged) one owner-only login free, a second one a seat, three boxes one seat.
//   THE SUMS        R500 / $49 / £49 / €49 for the five; R300 / $20 / £20 / €20 each past it; a pilot's extras are
//                   not free; PayFast is asked for the bill in cents; an allowance is held between the package and 50.
//   THE SERVICE     at five a sixth is refused to everybody, with how to ask; asking (owner only, more than they have,
//                   50 at most, not free, not unbilled) changes nothing; allowed seven: a sixth priced R500 → R800,
//                   refused to a supervisor with the figure, refused to the owner until the exact figure, then added
//                   and on record; the owner's own tick is the seventh; an eighth refused; removing lowers it; the
//                   command line recorded; the allowance taken back keeps everybody and adds nobody.
//   THE PAGES       5 of 5 with the way to ask; Billing's ask form (owner only); the ask on Owner → Approvals; Allow
//                   seven; All companies and the log say so; People asks Now / After, Don't do it adds nobody, Accept
//                   adds; Billing R800 with the extra seat; a supervisor told the figure; a second ask declined; the
//                   allowance taken back from All companies; not billed → not limited.
//
// History: seats were a price the owner agreed to (2026-10-05, §83) up to ten (2026-10-08, §116); then five and no
// more (2026-10-22, §153); now five and more on request. The words below are kept for the reasoning.
//
// Clint, 2026-10-05: "When one adds more than the allotted amount of users, it should prompt them and
// show an increase in the price which they must accept or reject. If they reject, it must not add the
// extra users." His answers: any role costs the same extra seat; only the OWNER accepts; the change
// shows on the next bill; existing companies keep everybody; Anywhere IT is not billed.
//
// Clint, 2026-10-08: "a 5 user license with Owner only still being a free seat ... the system must only
// allow for a maximum of 10 seats and one owner. If more than one owner is selected, this must be
// considered a seat taken." His answers: R500 / $49 stays the price of the five; ONE owner-only login is
// free and everybody else takes a seat; the limit is for every company, Anywhere IT included.
//
//   THE MIGRATIONS  every old company gets its region from its country and a seven-seat package (more
//                   kept) — and then FiveSeatPackage makes every package five; only Anywhere IT is
//                   marked not billed.
//   THE RULE        one owner-only login free, a second one a seat, three boxes one seat.
//   THE SUMS        R500 + R300 a seat in South Africa, $49 + $20 in the US and Canada, £ and €.
//   THE SERVICE     past the package: a supervisor is refused with the figure; the owner is refused
//                   until they name the exact figure shown; then the person is added and the yes is
//                   recorded. An office administrator costs a seat. Removing somebody lowers the
//                   price with nobody asked. A company not billed is never asked. AND the eleventh
//                   seat is refused to everybody — the owner agreeing, the command line, a company not
//                   billed — with no price question first.
//   THE PAGES       the owner adds a sixth person and is shown Now / After with Accept and Don't do
//                   it; Don't adds nobody; Accept adds them; Billing shows the new total and the
//                   agreement; a supervisor is told to ask the owner; a stale Accept is refused; at
//                   ten the Add form is gone, and posting it anyway adds nobody. An owner (only) can add a
//                   second owner, who takes a seat (2026-10-08).
//
// The first four in-process against a database of their own; the pages against a platform of its
// own (two-factor off — signing in is securitycheck's business, not this one's).
//
//   dotnet run -c Release --project tools/seatcheck/seatcheck.csproj
// =====================================================================================

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;
using RemoteSupport.Tools;

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}

Console.WriteLine("seatcheck — five seats; more on request, except for Anywhere IT, which is never limited");
var dir = Path.Combine(Path.GetTempPath(), "seatcheck-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dir);
const string Pw = "SeatCheck!2026";

try
{
    // ---- the migrations --------------------------------------------------------------------------
    Console.WriteLine();
    Console.WriteLine("== the migrations, on companies as the product wrote them before them ==");
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dir, "platform.db")};Pooling=False").Options;
    bool upgrade = HarnessDb.SqliteUpgradePart("the seat migrations");
    if (!upgrade)
        await using (var db = new PlatformDb(options))
        {
            // What the migrations leave: the same six companies, their regions from their countries, five seats each.
            await HarnessDb.MigrateAsync(db);
            foreach (var (id, slug, country, region) in new[] { (1, "anywhereit", "", "ZA"), (2, "za-co", "South Africa", "ZA"), (3, "uk-co", "United Kingdom", "UK"),
                (4, "de-co", "Germany", "EU"), (5, "au-co", "Australia", "US"), (6, "ca-co", "Canada", "CA") })
            {
                await Seed.Tenant(db, id, slug, slug.ToUpperInvariant(), "k" + id, seats: 5);
                var t = await db.Tenants.FirstAsync(x => x.Id == id);
                t.Country = country; t.Region = region; t.NotBilled = slug == "anywhereit";
                await db.SaveChangesAsync();
            }
        }
    else {
    await using (var db = new PlatformDb(options))
    {
        await db.GetService<IMigrator>().MigrateAsync("20261005104801_OneSignInAtATime");
        var old = new (int id, string slug, string country, int seats)[]
        {
            (1, "anywhereit", "", 5), (2, "za-co", "South Africa", 5), (3, "uk-co", "United Kingdom", 10),
            (4, "de-co", "Germany", 5), (5, "au-co", "Australia", 5), (6, "ca-co", "Canada", 5)
        };
        foreach (var o in old)
        {
            await Seed.OldTenant(db, o.id, o.slug, o.slug.ToUpperInvariant(), "k" + o.id, seats: o.seats);
            await db.Database.ExecuteSqlRawAsync(HarnessDb.Portable("UPDATE Tenants SET Country = {0} WHERE Id = {1}"), o.country, o.id);
        }
        // Up to the migration before the five-seat package: SeatsAndPrice's seven, as it was.
        await db.GetService<IMigrator>().MigrateAsync("20261007100129_ReinstallConfirm");
    }
    await using (var db = new PlatformDb(options))
    {
        // By SQL against the table as it stood then: today's model has columns it does not
        // (ReviewAskAfterUtc, 2026-10-13, broke the model read here).
        var t = new List<(int Id, string Slug, string Region, int Seats, bool NotBilled)>();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Slug, Region, Seats, NotBilled FROM Tenants ORDER BY Id";
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                t.Add((r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt64(4) != 0));
        }
        await conn.CloseAsync();
        string R(int id) => t.First(x => x.Id == id).Region;
        Check("regions from countries: none → ZA, South Africa → ZA, UK → UK, Germany → EU, Australia → US, Canada → CA",
            R(1) == "ZA" && R(2) == "ZA" && R(3) == "UK" && R(4) == "EU" && R(5) == "US" && R(6) == "CA", string.Join(" ", t.Select(x => x.Region)));
        Check("before FiveSeatPackage: seven seats, and one given ten keeps ten",
            t.Where(x => x.Id != 3).All(x => x.Seats == 7) && t.First(x => x.Id == 3).Seats == 10, string.Join(" ", t.Select(x => x.Seats)));
        Check("only Anywhere IT is not billed", t.Single(x => x.NotBilled).Slug == "anywhereit");
        await db.GetService<IMigrator>().MigrateAsync();
    }
    await using (var db = new PlatformDb(options))
    {
        var t = await db.Tenants.OrderBy(x => x.Id).ToListAsync();
        Check("after FiveSeatPackage: every package is five seats, the one at ten included", t.All(x => x.Seats == 5), string.Join(" ", t.Select(x => x.Seats)));
    }
    }

    // ---- the rule --------------------------------------------------------------------------------
    Console.WriteLine();
    Console.WriteLine("== the rule: one owner-only login free, everybody else a seat ==");
    int S(params TenantRole[] r) => Pricing.SeatsFor(r);
    const TenantRole O = TenantRole.Owner, T = TenantRole.Technician, A = TenantRole.Admin, B = TenantRole.Billing;
    Check("an owner on their own: no seat", S(O) == 0);
    Check("a SECOND owner-only login takes a seat (Clint's rule)", S(O, O) == 1 && S(O, O, O) == 2, $"{S(O, O)}, {S(O, O, O)}");
    Check("an owner who is also a technician takes a seat", S(O | T) == 1);
    Check("...and beside them an owner-only login is the free one", S(O | T, O) == 1);
    Check("one person with all three roles is ONE seat", S(T | A | B) == 1 && S(O | T | A | B) == 1);
    Check("a technician, a supervisor, an office administrator: three", S(T, A, B) == 3);
    Check("no box at all is no seat", S(TenantRole.None) == 0);
    var people = new List<Technician>
    {
        new() { Id = 1, DisplayName = "First Owner", Role = O }, new() { Id = 2, DisplayName = "Second Owner", Role = O },
        new() { Id = 3, DisplayName = "Tess Tech", Role = T }, new() { Id = 4, DisplayName = "Gone", Role = T, Disabled = true }
    };
    var named = Pricing.InSeats(people).Select(x => x.DisplayName).ToList();
    Check("Billing's names: the first owner-only login free, the second named, the disabled left out",
        named.SequenceEqual(new[] { "Second Owner", "Tess Tech" }) && Pricing.PeopleInSeats(people) == 2, string.Join(", ", named));

    // ---- the sums --------------------------------------------------------------------------------
    Console.WriteLine();
    Console.WriteLine("== the sums: five seats; more only when we allow them, at the extra-seat price (2026-10-25) ==");
    Pricing.Bill Bl(string region, int n) => Pricing.BillFor(new Tenant { Region = region, Seats = Pricing.IncludedSeats }, n);
    Check("the package is five, and a company may have five unless we allow more", Pricing.IncludedSeats == 5 && Pricing.MaxSeats == 5 && new Tenant().Seats == 5
        && Pricing.MostSeatsFor(new Tenant()) == 5 && !Pricing.MayHaveExtra(new Tenant()));
    Check("allowed eight: may have eight; an allowance below the package is the package; 500 is held to 50",
        Pricing.MostSeatsFor(new Tenant { MostSeats = 8 }) == 8 && Pricing.MayHaveExtra(new Tenant { MostSeats = 8 })
        && Pricing.MostSeatsFor(new Tenant { MostSeats = 3 }) == 5 && Pricing.MostSeatsFor(new Tenant { MostSeats = 500 }) == Pricing.MostAllowed);
    Check("the extra seat: R300, $20 (Canada too), £20, €20", Pricing.For("ZA").ExtraSeat == 300m && Pricing.For("US").ExtraSeat == 20m && Pricing.For("CA").ExtraSeat == 20m
        && Pricing.For("UK").ExtraSeat == 20m && Pricing.For("EU").ExtraSeat == 20m);
    Check("seven people in South Africa: R500 + 2 × R300 = R1,100, the sum written out",
        Bl("ZA", 7).Total == "R1,100" && Bl("ZA", 7).Extra == 2 && Bl("ZA", 7).Sum == "R500 for 5 seats + 2 extra seats × R300 = R600 → R1,100 a month", Bl("ZA", 7).Sum);
    Check("...and in the UK £89", Bl("UK", 7).Total == "£89", Bl("UK", 7).Total);
    var pilotBill = Pricing.BillFor(new Tenant { Region = "ZA", Pilot = true }, 6);
    Check("a pilot's package is free, but a seat past it is not: R300", pilotBill.Total == "R300" && pilotBill.Extra == 1, pilotBill.Sum);
    Check("PayFast is asked for exactly the bill, in cents: R1,100 → 110000",
        CompanyBilling.OwedCents(new Tenant { Region = "ZA", MostSeats = 8 }, Enumerable.Range(1, 7).Select(i => new Technician { Id = i, Role = TenantRole.Technician })) == 110000);
    Check("South Africa, five people: R500", Bl("ZA", 5).Total == "R500", Bl("ZA", 5).Total);
    Check("fewer than five is still the package", Bl("ZA", 3).Total == "R500" && Bl("ZA", 1).Total == "R500");
    Check("US and Canada $49, in dollars; UK £49; Europe €49", Bl("US", 5).Total == "$49" && Bl("CA", 5).Total == "$49" && Pricing.For("CA").Currency == "US dollars"
        && Bl("UK", 5).Total == "£49" && Bl("EU", 5).Total == "€49");
    Check("the bill's sum says how many of the five are used", Bl("ZA", 3).Sum == "R500 a month (3 of 5 seats used)", Bl("ZA", 3).Sum);
    Check("a package set above five counts as five", Pricing.BillFor(new Tenant { Region = "ZA", Seats = 50 }, 5).Included == 5 && Pricing.SeatsAllowed(new Tenant { Seats = 50 }) == 5);
    Check("a package set below five is what the company may fill", Pricing.SeatsAllowed(new Tenant { Seats = 3 }) == 3);
    Check("an unknown region is South Africa's money", Pricing.For("XX").Code == "ZA" && Pricing.RegionOfCountry("") == "ZA" && Pricing.RegionOfCountry("Kenya") == "US");
    Check("every company is limited — except the one not billed", Pricing.Limited(new Tenant()) && !Pricing.Limited(new Tenant { NotBilled = true }));

    // ---- the service --------------------------------------------------------------------------------
    Console.WriteLine();
    Console.WriteLine("== the service: South Africa, five seats until we allow more ==");
    var tenants = new TenantService(new Factory(options), new ConfigurationBuilder().Build(), dir, DataProtectionProvider.Create("seatcheck"));
    // Company 2: an owner on their own (free), then five people the platform adds — every seat taken.
    await tenants.AddTechnicianAsync(2, "owner@za.test", Pw, "Olive Owner", TenantRole.Owner);
    await tenants.AddTechnicianAsync(2, "sup@za.test", Pw, "Sid Supervisor", TenantRole.Admin);
    for (int i = 1; i <= 3; i++) await tenants.AddTechnicianAsync(2, $"t{i}@za.test", Pw, $"Tech {i}", TenantRole.Technician);
    var (office, _) = await tenants.AddTechnicianAsync(2, "office@za.test", Pw, "Ossie Office", TenantRole.Billing);
    int People() => Pricing.PeopleInSeats(tenants.TechniciansAsync(2).Result);
    Check("an owner alone, a supervisor, three technicians and an office administrator: five seats", People() == 5 && office != null, $"{People()}");

    var why = await tenants.NewPersonProblemAsync(2, "six@za.test", "Sam Six", TenantRole.Technician);
    Check("a sixth person is a refusal, said before anything is done, saying more can be asked for",
        why?.Contains("may have 5 seats") == true && why.Contains("ask us for more seats on the Billing page"), why ?? "null");
    Check("...and there is no price to agree for it", await tenants.PriceRiseAsync(2, TenantRole.Technician) == null);
    var (bySup, supErr) = await tenants.AddTechnicianAsync(2, "six@za.test", null, "Sam Six", TenantRole.Technician, TenantRole.Admin);
    Check("...added by a supervisor: refused", bySup == null && People() == 5 && supErr.Contains("may have 5 seats"), supErr);
    var (byOwner, ownerErr) = await tenants.AddTechnicianAsync(2, "six@za.test", null, "Sam Six", TenantRole.Technician, TenantRole.Owner, "ZA:6:800", "Olive");
    Check("...added by the OWNER naming a price: refused just the same — we have not allowed more", byOwner == null && People() == 5 && ownerErr.Contains("may have 5 seats"), ownerErr);
    var (byCli, _) = await tenants.AddTechnicianAsync(2, "six@za.test", Pw, "Sam Six", TenantRole.Billing);
    Check("...added by the platform's command line: refused too", byCli == null && People() == 5);
    var (twoOwners, twoErr) = await tenants.AddTechnicianAsync(2, "owner2@za.test", null, "Ollie Second", TenantRole.Owner, TenantRole.Owner);
    Check("a second owner-only login is a seat, so it is refused at five", twoOwners == null && twoErr.Contains("may have 5 seats"), twoErr);
    Check("nothing on the price-agreement record", (await tenants.SeatAgreementsAsync(2)).Count == 0);

    var ownTick = await tenants.SetRoleAsync(2, "owner@za.test", TenantRole.Owner | TenantRole.Technician, TenantRole.Owner);
    Check("the owner ticking Technician beside Owner at five is refused — the limit counts the owner too", ownTick.Contains("may have 5 seats") && People() == 5, ownTick);

    Console.WriteLine();
    Console.WriteLine("== asking for more seats ==");
    var (supAsk, supAskErr) = await tenants.AskForSeatsAsync(2, 8, "", TenantRole.Admin, "Sid");
    Check("a supervisor cannot ask — only an owner", supAsk == null && supAskErr.Contains("Only an owner"), supAskErr);
    var (few, fewErr) = await tenants.AskForSeatsAsync(2, 5, "", TenantRole.Owner, "Olive");
    Check("asking for five, which they have, is refused", few == null && fewErr.Contains("already have 5"), fewErr);
    var (lots, lotsErr) = await tenants.AskForSeatsAsync(2, 51, "", TenantRole.Owner, "Olive");
    Check("asking for more than 50 here is refused", lots == null && lotsErr.Contains("50 seats at most"), lotsErr);
    var (asked, askErr) = await tenants.AskForSeatsAsync(2, 8, "  Two new technicians in Durban  ", TenantRole.Owner, "Olive Owner (owner@za.test)");
    var za = (await tenants.ByIdAsync(2))!;
    Check("the owner asks for eight, with a reason: kept on the company, trimmed, with who and when",
        asked != null && askErr == "" && za.SeatsAskedFor == 8 && za.SeatsAskedNote == "Two new technicians in Durban" && za.SeatsAskedBy.StartsWith("Olive") && za.SeatsAskedUtc != null, askErr);
    Check("...and asking changes nothing: still five, a sixth still refused", Pricing.MostSeatsFor(za) == 5
        && (await tenants.NewPersonProblemAsync(2, "six@za.test", "Sam Six", TenantRole.Technician))?.Contains("may have 5 seats") == true);
    using (var db = new PlatformDb(options)) { var b = await db.Tenants.FirstAsync(x => x.Id == 1); b.Plan = "free"; await db.SaveChangesAsync(); }
    var (freeAsk, freeErr) = await tenants.AskForSeatsAsync(1, 3, "", TenantRole.Owner, "x");
    using (var db = new PlatformDb(options)) { var b = await db.Tenants.FirstAsync(x => x.Id == 1); b.Plan = "five-user"; await db.SaveChangesAsync(); }
    var (aitAsk, aitErr) = await tenants.AskForSeatsAsync(1, 9, "", TenantRole.Owner, "x");
    Check("the free version cannot ask (it upgrades), nor a company not billed (it is not limited)", freeAsk == null && aitAsk == null && aitErr.Contains("not limited"), $"{freeErr} / {aitErr}");

    Console.WriteLine();
    Console.WriteLine("== we allow seven: each seat past five is R300, agreed by the owner first ==");
    using (var db = new PlatformDb(options)) { var t = await db.Tenants.FirstAsync(x => x.Id == 2); t.MostSeats = 7; await db.SaveChangesAsync(); }
    var rise6 = await tenants.PriceRiseAsync(2, TenantRole.Technician);
    Check("a sixth person now has a price: R500 → R800", rise6 != null && rise6.Before.Total == "R500" && rise6.After.Total == "R800" && rise6.Token == "ZA:6:800", rise6?.Token ?? "null");
    var (supSix, supSixErr) = await tenants.AddTechnicianAsync(2, "six@za.test", null, "Sam Six", TenantRole.Technician, TenantRole.Admin);
    Check("...added by a supervisor: refused with the figure, and told to ask the owner",
        supSix == null && People() == 5 && supSixErr.Contains("from R500 to R800") && supSixErr.Contains("Only an owner"), supSixErr);
    var (unagreed, unagreedErr) = await tenants.AddTechnicianAsync(2, "six@za.test", null, "Sam Six", TenantRole.Technician, TenantRole.Owner);
    Check("...by the owner without agreeing: refused, nothing done", unagreed == null && People() == 5 && unagreedErr.Contains("Please agree to the new price first"), unagreedErr);
    var (wrongFig, _) = await tenants.AddTechnicianAsync(2, "six@za.test", null, "Sam Six", TenantRole.Technician, TenantRole.Owner, "ZA:6:500", "Olive");
    Check("...by the owner naming another figure: refused", wrongFig == null && People() == 5);
    var (six, sixErr) = await tenants.AddTechnicianAsync(2, "six@za.test", null, "Sam Six", TenantRole.Technician, TenantRole.Owner, rise6!.Token, "Olive Owner (owner@za.test)");
    var agreements = await tenants.SeatAgreementsAsync(2);
    Check("...by the owner naming R800: added, and the yes on record with who and what",
        six != null && People() == 6 && agreements.Count == 1 && agreements[0].Monthly == 800m && agreements[0].MonthlyBefore == 500m
        && agreements[0].AgreedBy.StartsWith("Olive") && agreements[0].What.StartsWith("Added Sam Six"), sixErr);
    var zaNow = (await tenants.ByIdAsync(2))!;
    Check("the bill is R800 and PayFast is asked for 80000 cents", Pricing.BillFor(zaNow, People()).Total == "R800"
        && CompanyBilling.OwedCents(zaNow, await tenants.TechniciansAsync(2)) == 80000);
    var rise7 = await tenants.PriceRiseAsync(2, TenantRole.Owner | TenantRole.Technician, "owner@za.test");
    var tick7 = await tenants.SetRoleAsync(2, "owner@za.test", TenantRole.Owner | TenantRole.Technician, TenantRole.Owner, rise7?.Token, "Olive Owner (owner@za.test)");
    Check("the owner ticking Technician is the seventh seat: priced R800 → R1,100, agreed, done",
        rise7?.After.Total == "R1,100" && tick7 == "" && People() == 7 && (await tenants.SeatAgreementsAsync(2)).Count == 2, tick7);
    var (eighth, eighthErr) = await tenants.AddTechnicianAsync(2, "eight@za.test", null, "Ed Eight", TenantRole.Technician, TenantRole.Owner, "ZA:8:1400", "Olive");
    Check("an eighth is refused: seven is what we allowed", eighth == null && People() == 7 && eighthErr.Contains("may have 7 seats"), eighthErr);
    await tenants.RemoveTechnicianAsync(2, "six@za.test");
    Check("removing somebody brings the price down with nobody asked: R800", People() == 6 && Pricing.BillFor((await tenants.ByIdAsync(2))!, People()).Total == "R800");
    var (cliSeven, _) = await tenants.AddTechnicianAsync(2, "cli@za.test", Pw, "Cleo Cli", TenantRole.Technician);
    var cliRecord = (await tenants.SeatAgreementsAsync(2))[0];
    Check("the command line adds within the allowance, recorded as the platform's", cliSeven != null && People() == 7 && cliRecord.AgreedBy == "the platform (command line)");
    using (var db = new PlatformDb(options)) { var t = await db.Tenants.FirstAsync(x => x.Id == 2); t.MostSeats = 0; await db.SaveChangesAsync(); }
    Check("we take the allowance back: nobody is removed, and nobody more is added",
        People() == 7 && (await tenants.NewPersonProblemAsync(2, "eight@za.test", "Ed Eight", TenantRole.Technician))?.Contains("may have 5 seats") == true);
    var demote = await tenants.SetRoleAsync(2, "owner@za.test", TenantRole.Owner, TenantRole.Owner);
    Check("...though giving a seat back is always allowed", demote == "" && People() == 6, demote);
    await tenants.RemoveTechnicianAsync(2, "cli@za.test");
    Check("...and with one removed it is five again, as it was", People() == 5);
    Check("a box that costs no seat is still allowed at five",
        await tenants.SetRoleAsync(2, "t1@za.test", TenantRole.Technician | TenantRole.Billing | TenantRole.Admin, TenantRole.Owner) == "" && People() == 5);
    await tenants.RemoveTechnicianAsync(2, "t3@za.test");
    Check("removing somebody frees a seat: four", People() == 4);
    var ownAgain = await tenants.SetRoleAsync(2, "owner@za.test", TenantRole.Owner | TenantRole.Technician, TenantRole.Owner);
    Check("...which the owner may now take by ticking Technician: five again", ownAgain == "" && People() == 5, ownAgain);
    Check("...and the price never moved: R500", Pricing.BillFor((await tenants.ByIdAsync(2))!, People()).Total == "R500");
    var hand = await tenants.SetOwnerAsync(2, "sup@za.test");
    Check("handing the company over at five works when nobody new takes a seat", hand == "" && People() == 5, hand);

    Console.WriteLine();
    Console.WriteLine("== a company the platform owner set to three seats ==");
    using (var db = new PlatformDb(options)) { var uk = await db.Tenants.FirstAsync(x => x.Id == 3); uk.Seats = 3; await db.SaveChangesAsync(); }
    for (int i = 1; i <= 3; i++) await tenants.AddTechnicianAsync(3, $"u{i}@uk.test", Pw, $"UK {i}", TenantRole.Technician);
    var (fourth, fourthErr) = await tenants.AddTechnicianAsync(3, "u4@uk.test", Pw, "UK 4", TenantRole.Technician);
    Check("its fourth person is refused: it may have 3 seats", fourth == null && fourthErr.Contains("may have 3 seats"), fourthErr);

    Console.WriteLine();
    Console.WriteLine("== a company not billed (Anywhere IT): never limited ==");
    await tenants.AddTechnicianAsync(1, "boss@ait.test", Pw, "Boss", TenantRole.OwnerSupervisor);
    for (int i = 1; i <= 11; i++)
    {
        var (x, e) = await tenants.AddTechnicianAsync(1, $"a{i}@ait.test", null, $"A {i}", TenantRole.Technician, TenantRole.Admin);
        if (x == null) Check($"adding person {i} by a supervisor", false, e);
    }
    Check("twelve people, added by a supervisor, nobody refused or asked", Pricing.PeopleInSeats(await tenants.TechniciansAsync(1)) == 12);
    Check("...and no price on record", (await tenants.SeatAgreementsAsync(1)).Count == 0);
    var aitOwner = await tenants.AddTechnicianAsync(1, "owner2@ait.test", null, "Second Boss", TenantRole.Owner, TenantRole.OwnerSupervisor);
    Check("...a second owner too", aitOwner.tech != null, aitOwner.error);
    var aitHand = await tenants.SetOwnerAsync(1, "a1@ait.test");
    Check("handing it over works at any size", aitHand == "", aitHand);

    // ---- the pages ------------------------------------------------------------------------------------
    var pages = new Pages(dir, Check);
    await pages.RunAsync();
}
finally
{
    try { Directory.Delete(dir, true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
return fail == 0 ? 0 : 1;

/// <summary>The People and Billing pages, through a platform of its own.</summary>
file sealed class Pages
{
    private readonly string _dir; private readonly Action<string, bool, string> _check;
    private string _proj = "", _base = "", _data = "";
    private int _port;
    private const string Pw = "SeatCheck!2026", Slug = "seat-co", AdminKey = "seatcheck-admin-key-0123456789abcdef0123456789abcdef";

    public Pages(string dir, Action<string, bool, string> check) { _dir = dir; _check = check; }
    private void Check(string what, bool ok, string detail = "") => _check(what, ok, detail);

    public async Task RunAsync()
    {
        Console.WriteLine();
        Console.WriteLine("== the pages ==");
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "RemoteSupport.sln"))) d = d.Parent;
        _proj = Path.Combine(d!.FullName, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
        _data = Path.Combine(_dir, "pages"); Directory.CreateDirectory(_data);
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); _port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        _base = $"http://127.0.0.1:{_port}";

        var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{_proj}\" -c Debug -v q -nologo") { RedirectStandardOutput = true, UseShellExecute = false })!;
        await build.StandardOutput.ReadToEndAsync(); await build.WaitForExitAsync();
        if (build.ExitCode != 0) { Check("the platform builds", false); return; }

        await Cli("create-tenant", "Seat Co", Slug, "5");
        await Cli("add-tech", Slug, "owner@seat.test", Pw, "Olive Owner", "--owner");
        await Cli("add-tech", Slug, "sup@seat.test", Pw, "Sid Supervisor", "--admin");
        for (int i = 1; i <= 3; i++) await Cli("add-tech", Slug, $"t{i}@seat.test", Pw, $"Tech {i}", "--technician");
        await Cli("add-tech", Slug, "office@seat.test", Pw, "Ossie Office", "--billing");
        var listed = await Cli("list-techs", Slug);
        Check("the command line counts five people in seats, the owner free, at R500", listed.Contains("5 people in seats") && listed.Contains("R500"),
            listed.Trim().Split('\n').Last().Trim());
        var cliSix = await Cli("add-tech", Slug, "six@seat.test", Pw, "Sam Six", "--technician");
        Check("the command line refuses a sixth, saying why", cliSix.Contains("may have 5 seats"), cliSix.Trim());

        using var server = Start();
        try
        {
            var until = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < until) { try { using var c = new TcpClient(); await c.ConnectAsync("127.0.0.1", _port); break; } catch { await Task.Delay(400); } }

            var owner = await SignIn("owner@seat.test");
            var people = await Get(owner, "/portal/technicians");
            Check("the owner's People page: 5 of 5 seats in use, more on request from Billing", people.Contains("5 of 5 seats in use") && people.Contains("more are on request")
                && people.Contains("/portal/billing#moreSeats"));
            Check("...the Add form gone, the reason in its place with the way to ask, and no price question",
                people.Contains("id=\"seatsLimit\"") && people.Contains("ask us for more seats") && !people.Contains("id=\"addRoles\"") && !people.Contains("priceQuestion") && !people.Contains("R300"));
            Check("...and that office administrators use a seat", people.Contains("Everybody uses a seat") && !people.Contains("are free"));
            var posted = await Post(owner, "/portal/technicians", "Add", people, ("name", "Sam Six"), ("email", "six@seat.test"), ("role", "office"));
            Check("posting the Add form anyway adds nobody, with the reason", !Listed(posted, "six@seat.test") && posted.Contains("may have 5 seats"));
            var stale = await Post(owner, "/portal/technicians", "Add", people, ("name", "Sam Six"), ("email", "six@seat.test"), ("role", "technician"), ("agreed", "ZA:6:800"));
            Check("...and so does an Accept naming a price, before we have allowed more", !Listed(stale, "six@seat.test") && stale.Contains("may have 5 seats"));

            var billing = await Get(owner, "/portal/billing");
            Check("Billing: R500 a month, 5 of 5 seats in use, more on request", billing.Contains("id=\"monthlyTotal\">R500<") && billing.Contains("5 of 5 seats in use")
                && billing.Contains("more are on request") && !billing.Contains("id=\"extraSeats\""));
            Check("...naming the five in seats and not the free owner", billing.Contains("Sid Supervisor") && !Regex.IsMatch(billing, "seats in use: [^<]*Olive Owner"));
            Check("...and the owner may ask for more seats there, at R300 each", billing.Contains("id=\"moreSeats\"") && billing.Contains("id=\"askSeats\"") && billing.Contains("R300 a month each"));

            var removed = await Post(owner, "/portal/technicians", "Remove", people, ("email", "t3@seat.test"));
            Check("removing somebody brings the Add form back, with the Owner box for an owner",
                removed.Contains("4 of 5 seats in use") && Regex.IsMatch(removed, "id=\"addRoles\"[\\s\\S]*?name=\"role\" value=\"owner\"[\\s\\S]*?</div>"));
            var second = await Post(owner, "/portal/technicians", "Add", removed, ("name", "Ollie Second"), ("email", "owner2@seat.test"), ("role", "owner"));
            Check("the owner adds a SECOND owner, who takes the free seat: 5 of 5", Listed(second, "owner2@seat.test") && second.Contains("5 of 5 seats in use"));

            var sup = await SignIn("sup@seat.test");
            var supPeople = await Get(sup, "/portal/technicians");
            Check("a supervisor's page offers no Owner box anywhere, and the same limit", !supPeople.Contains("value=\"owner\"") && supPeople.Contains("id=\"seatsLimit\"")
                && supPeople.Contains("ask an owner to ask us"));

            // ---- asking, and our answer (2026-10-25) ------------------------------------------------------------
            var supBilling = await Get(sup, "/portal/billing");
            Check("a supervisor's Billing has no ask form — an owner asks", !supBilling.Contains("id=\"askSeats\"") && supBilling.Contains("An owner asks for more seats"));
            var supAsk = await Post(sup, "/portal/billing", "Seats", supBilling, ("seats", "8"), ("note", "x"));
            Check("...and a supervisor's forced ask is refused", supAsk.Contains("Only an owner can ask") && !supAsk.Contains("id=\"seatsAsked\""));
            var ask = await Post(owner, "/portal/billing", "Seats", billing, ("seats", "8"), ("note", "Two technicians starting in Durban"));
            Check("the owner asks for eight: thanked, and Billing says it is waiting", ask.Contains("we have your request for 8 seats") && ask.Contains("id=\"seatsAsked\"")
                && ask.Contains("You asked for <strong>8 seats</strong>"), Regex.Match(ask, "id=\"billing(Message|Error)\">([^<]*)").Groups[2].Value);
            var stillFive = await Get(owner, "/portal/technicians");
            Check("...which changes nothing yet: still 5 of 5", stillFive.Contains("5 of 5 seats in use") && stillFive.Contains("id=\"seatsLimit\""));

            var admin = await AdminSignIn();
            var approvals = await Get(admin, "/owner/approvals");
            var askRow = Regex.Match(approvals, "<tr data-seat-ask=\"" + Slug + "\">[\\s\\S]*?</tr>").Value;
            Check("Owner → Approvals lists it: eight asked for, the reason, who, what they have",
                askRow.Contains("8 seats") && askRow.Contains("Two technicians starting in Durban") && askRow.Contains("owner@seat.test") && askRow.Contains("may have 5 now"), askRow.Length.ToString());
            string srv = Regex.Match(askRow, "name=\"server\" value=\"(\\d+)\"").Groups[1].Value, id = Regex.Match(askRow, "name=\"id\" value=\"(\\d+)\"").Groups[1].Value;
            var allowed = await PostTo(admin, "/owner/approvals?handler=Act", approvals, ("server", srv), ("id", id), ("act", "allow-seats"), ("most", "7"));
            Check("we allow seven (not the eight asked): said, and no longer waiting", allowed.Contains("may now have up to 7 seats") && !allowed.Contains($"data-seat-ask=\"{Slug}\""),
                Regex.Match(allowed, "alert alert-(ok|bad)\">([^<]*)").Groups[2].Value);
            var companies = await Get(admin, "/owner/tenants");
            Check("All companies shows the allowance: 5 of 7 seats in use, 2 extra allowed, the box at 7",
                companies.Contains("5 of 7 seats in use · 2 extra allowed") && Regex.IsMatch(companies, $"data-allow-seats=\"{id}\"[\\s\\S]*?name=\"most\"[^>]*value=\"7\""));
            var log = await Get(admin, "/owner/log");
            Check("...and the admin log has it, by name", log.Contains("Most seats 5 → 7") && log.Contains("Asked for 8 seats"));

            var seven = await Get(owner, "/portal/technicians");
            Check("the owner's People page: 5 of 7 in use, 5 in the package, the Add form back, and the price said",
                seven.Contains("5 of 7 seats in use · 5 in your package") && seven.Contains("id=\"addRoles\"") && seven.Contains("id=\"seatsFull\"") && seven.Contains("costs R300 a month more"));
            var question = await Post(owner, "/portal/technicians", "Add", seven, ("name", "Sam Six"), ("email", "six@seat.test"), ("role", "technician"));
            Check("adding a sixth asks first: R500 now, R800 after, Accept and Don't do it — nobody added yet",
                question.Contains("id=\"priceQuestion\"") && question.Contains("R500") && question.Contains("Accept R800 a month") && question.Contains("id=\"declinePrice\"")
                && !Listed(question, "six@seat.test"));
            var declined = await Post(owner, "/portal/technicians", "Decline", question, ("doing", "Adding Sam Six"));
            Check("Don't do it adds nobody", declined.Contains("not done") && !Listed(declined, "six@seat.test") && declined.Contains("5 of 7 seats in use"));
            var token = Regex.Match(question, "name=\"agreed\" value=\"([^\"]+)\"").Groups[1].Value;
            var accepted = await Post(owner, "/portal/technicians", "Add", question, ("name", "Sam Six"), ("email", "six@seat.test"), ("role", "technician"), ("agreed", token));
            Check("Accept adds them: 6 of 7", token == "ZA:6:800" && Listed(accepted, "six@seat.test") && accepted.Contains("6 of 7 seats in use"), token);
            var billed = await Get(owner, "/portal/billing");
            Check("Billing: R800 — the package and one extra seat at R300, and the yes on record",
                billed.Contains("id=\"monthlyTotal\">R800<") && Regex.IsMatch(billed, "id=\"extraSeats\"><td>1 extra seat × R300") && billed.Contains("id=\"agreements\"")
                && billed.Contains("Added Sam Six") && billed.Contains("allowed you up to 7") && !billed.Contains("id=\"seatsAsked\""));
            var supSeven = await Get(sup, "/portal/technicians");
            var supAdd = await Post(sup, "/portal/technicians", "Add", supSeven, ("name", "Sue Seven"), ("email", "seven@seat.test"), ("role", "technician"));
            Check("a supervisor adding the seventh is told the figure and to ask the owner", !Listed(supAdd, "seven@seat.test") && supAdd.Contains("from R800 to R1,100")
                && supAdd.Contains("Only an owner can agree"));

            // Asked again, and this time not.
            var billing2 = await Get(owner, "/portal/billing");
            var ask2 = await Post(owner, "/portal/billing", "Seats", billing2, ("seats", "10"), ("note", ""));
            var tooFew = await Post(owner, "/portal/billing", "Seats", billing2, ("seats", "7"), ("note", ""));
            Check("asking for what they already may have is refused", tooFew.Contains("may already have 7 seats"));
            var approvals2 = await Get(admin, "/owner/approvals");
            var declinedAsk = await PostTo(admin, "/owner/approvals?handler=Act", approvals2, ("server", srv), ("id", id), ("act", "decline-seats"), ("reason", "Seven is the most for now."));
            Check("we decline ten: said, no longer waiting, and their Billing clear of it", ask2.Contains("request for 10 seats") && declinedAsk.Contains("request for 10 seats was declined")
                && !declinedAsk.Contains($"data-seat-ask=\"{Slug}\"") && !(await Get(owner, "/portal/billing")).Contains("id=\"seatsAsked\""));
            Check("...logged by name", (await Get(admin, "/owner/log")).Contains("Did not allow 10 seats"));

            // Taken back: nobody removed, nobody more.
            var tenants2 = await Get(admin, "/owner/tenants");
            var back = await PostTo(admin, "/owner/tenants?handler=Act", tenants2, ("server", srv), ("id", id), ("act", "allow-seats"), ("most", "5"));
            var after = await Get(owner, "/portal/technicians");
            Check("we take the allowance back: 6 kept, nobody more may be added", back.Contains("back to its 5 seats") && back.Contains("nobody was removed")
                && Listed(after, "six@seat.test") && after.Contains("id=\"seatsLimit\"") && !after.Contains("id=\"addRoles\""), Regex.Match(back, "alert alert-(ok|bad)\">([^<]*)").Groups[2].Value);

            await Cli("set-brand", Slug, "not-billed", "true");
            var notLimited = await Get(owner, "/portal/technicians");
            Check("once the company is not billed, its People page says not limited, with the Add form", notLimited.Contains("not limited") && notLimited.Contains("id=\"addRoles\""));
            var seventh = await Post(owner, "/portal/technicians", "Add", notLimited, ("name", "Sue Seven"), ("email", "seven@seat.test"), ("role", "office"));
            Check("...and a seventh is added, with no price asked", Listed(seventh, "seven@seat.test") && !seventh.Contains("priceQuestion"));
            var nb = await Get(owner, "/portal/billing");
            Check("...and Billing says Not billed, with nothing to ask for", nb.Contains("Not billed") && !nb.Contains("id=\"moreSeats\""));
        }
        finally { try { if (!server.HasExited) server.Kill(true); } catch { } }
    }

    /// <summary>A platform admin, by the admin key and the authenticator code it shows the first time (as pilotcheck does).</summary>
    private async Task<HttpClient> AdminSignIn()
    {
        var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(_base), Timeout = TimeSpan.FromSeconds(30) };
        var form = await Get(c, "/owner/login");
        var keyStep = await PostTo(c, "/owner/login", form, ("Key", AdminKey));
        var secret = Regex.Match(keyStep, "user-select:all\">([A-Z2-7 ]+)<").Groups[1].Value.Replace(" ", "");
        var pending = Regex.Match(keyStep, "name=\"Pending\" value=\"([^\"]*)\"").Groups[1].Value;
        await PostTo(c, "/owner/login", keyStep, ("UseKey", "true"), ("Pending", pending), ("Code", RemoteSupport.Shared.Totp.Now(secret)));
        return c;
    }

    private static async Task<string> PostTo(HttpClient c, string url, string fromHtml, params (string k, string v)[] f)
    {
        var all = f.Select(x => new KeyValuePair<string, string>(x.k, x.v)).Append(new("__RequestVerificationToken", Token(fromHtml)));
        var r = await c.PostAsync(url, new FormUrlEncodedContent(all));
        return WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());
    }

    /// <summary>Whether a person's row is on the People page — their email in a table cell.</summary>
    private static bool Listed(string html, string email) => html.Contains($"<td>{email}</td>");

    private async Task<HttpClient> SignIn(string email)
    {
        var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(_base), Timeout = TimeSpan.FromSeconds(30) };
        var form = await c.GetStringAsync("/login");
        await c.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = email, ["Password"] = Pw, ["__RequestVerificationToken"] = Token(form) }));
        return c;
    }

    private static async Task<string> Post(HttpClient c, string page, string handler, string fromHtml, params (string k, string v)[] f)
    {
        var all = f.Select(x => new KeyValuePair<string, string>(x.k, x.v)).Append(new("__RequestVerificationToken", Token(fromHtml)));
        var r = await c.PostAsync($"{page}?handler={handler}", new FormUrlEncodedContent(all));
        // Read as a person reads it: Razor writes "×" and "→" from C# strings as entities.
        return WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());
    }

    private static async Task<string> Get(HttpClient c, string path) => WebUtility.HtmlDecode(await c.GetStringAsync(path));

    private static string Token(string page) => Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

    private Process Start()
    {
        var psi = Psi(); psi.ArgumentList.Add("--"); psi.ArgumentList.Add("run");
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, _) => { }; p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        return p;
    }

    private async Task<string> Cli(params string[] args)
    {
        var psi = Psi(); psi.ArgumentList.Add("--");
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return stdout;
    }

    private ProcessStartInfo Psi()
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "run", "--no-build", "-c", "Debug", "--project", _proj }) psi.ArgumentList.Add(a);
        psi.Environment["RSP_Platform__DataDirectory"] = _data;
        HarnessDb.Use(psi.Environment, _data);
        psi.Environment["RSP_Platform__AdminKey"] = AdminKey;
        psi.Environment["RSP_Security__RequireTwoFactor"] = "false";
        psi.Environment["RSP_Urls"] = _base;
        psi.Environment["ASPNETCORE_URLS"] = _base;
        return psi;
    }
}

file sealed class Factory : IDbContextFactory<PlatformDb>
{
    private readonly DbContextOptions<PlatformDb> _o;
    public Factory(DbContextOptions<PlatformDb> o) => _o = o;
    public PlatformDb CreateDbContext() => new(_o);
}
