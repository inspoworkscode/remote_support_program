using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Fills a THROWAWAY platform with pretend companies for <c>tools/loadtest</c> (2026-10-07):
///
/// <code>RemoteSupport.Platform seed-loadtest &lt;companies&gt; &lt;people-per-company&gt; &lt;seed-file&gt;</code>
///
/// <para>Every company is <c>load-00001</c>, <c>load-00002</c>… and every person in it a technician
/// <c>t1@load-00001.test</c>…, all with ONE password made for this run and written to the seed file with
/// each company's key. One password is hashed once: 600,000 rounds each (§111) would make a thousand
/// companies take minutes, and how fast the seed is says nothing about the server.</para>
///
/// <para><b>It refuses on any database holding a company that is not one of its own</b>, so it cannot be
/// run on the live server by mistake, and on one it has already seeded (start again from an empty database).</para>
/// </summary>
public static class LoadTestSeed
{
    public const string SlugPrefix = "load-";

    public static async Task<int> RunAsync(string[] a, IDbContextFactory<PlatformDb> dbf)
    {
        if (a.Length < 4 || !int.TryParse(a[1], out var companies) || !int.TryParse(a[2], out var people) || companies < 1 || people < 1)
        {
            Console.WriteLine("Usage: seed-loadtest <companies> <people-per-company> <seed-file>");
            return 1;
        }
        var file = a[3];
        await using (var db = await dbf.CreateDbContextAsync())
        {
            // Said first, and checked by Prepare-LoadServer.ps1 (2026-10-12): a load server must be on PostgreSQL like
            // live, or it measures the database live left behind.
            Console.WriteLine("Database: " + (db.Database.ProviderName?.Contains("Npgsql") == true ? "PostgreSQL" : "SQLite"));
            if (await db.Tenants.AnyAsync(t => !t.Slug.StartsWith(SlugPrefix)))
            {
                Console.WriteLine("REFUSED: this database holds real companies. seed-loadtest only runs on a throwaway load-test server.");
                return 1;
            }
            if (await db.Tenants.AnyAsync())
            {
                Console.WriteLine("REFUSED: this database is already seeded. To start again, stop the platform, empty its database (delete platform.db, or drop and re-create the PostgreSQL database) and start it.");
                return 1;
            }
        }

        var password = "Load-" + Passwords.RandomToken(8);
        var (salt, hash) = Passwords.Create(password);
        var lines = new List<string> { "# The Remote Support Program load test seed - throwaway server only", "password\t" + password };
        var started = DateTime.UtcNow;
        const int Batch = 250;
        for (int from = 1; from <= companies; from += Batch)
        {
            await using var db = await dbf.CreateDbContextAsync();
            for (int n = from; n < from + Batch && n <= companies; n++)
            {
                var slug = $"{SlugPrefix}{n:00000}";
                var t = new Tenant
                {
                    Slug = slug, CompanyName = $"Load Test {n:00000}", ProductName = "Remote Support",
                    TenantKey = Passwords.RandomToken(24), Seats = Math.Max(7, people), NotBilled = true,
                };
                var emails = new List<string>();
                for (int p = 1; p <= people; p++)
                {
                    var email = $"t{p}@{slug}.test";
                    emails.Add(email);
                    t.Technicians.Add(new Technician
                    {
                        Email = email, DisplayName = $"Tech {p}", Salt = salt, Hash = hash,
                        Role = p == 1 ? TenantRole.Technician | TenantRole.Admin : TenantRole.Technician, SecurityStamp = AuthService.NewStamp(),
                    });
                }
                db.Tenants.Add(t);
                lines.Add($"{slug}\t{t.TenantKey}\t{string.Join(",", emails)}");
            }
            await db.SaveChangesAsync();
            Console.WriteLine($"  {Math.Min(from + Batch - 1, companies)} of {companies} companies");
        }
        File.WriteAllLines(file, lines);
        Console.WriteLine($"Seeded {companies} companies with {people} people each in {(DateTime.UtcNow - started).TotalSeconds:0.0} s. Seed file: {Path.GetFullPath(file)}");
        return 0;
    }
}
