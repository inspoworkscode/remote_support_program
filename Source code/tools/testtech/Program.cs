// =====================================================================================
// testtech - make (or reset) a test account to sign in with while stage 3.6 is built.
//
// Stage 3.6.3 lets technicians sign in to the portal for the first time, and there was no
// way to try that without an account that is a TECHNICIAN rather than an administrator —
// signing in as the owner proves nothing, because the owner could always get in and sees
// everything by design.
//
// It makes one of each role by default, all in the same company, so the three answers to
// "what am I allowed to see" can be compared side by side in three browser windows.
//
//   dotnet run -c Release --project tools/testtech
//   dotnet run -c Release --project tools/testtech -- --role technician
//   dotnet run -c Release --project tools/testtech -- --remove
//
// The password is GENERATED and written to a file next to the database rather than printed,
// because the agreement on this project is that passwords do not appear in a chat window
// (CLAUDE.md). It prints the path. Run it again and the same accounts get a fresh password.
//
// It refuses to touch an account whose email is not one of its own, so it can never reset
// a real technician's password by being pointed at the wrong company.
// =====================================================================================

using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

var dbPath = ArgValue("--db") ?? DefaultDbPath();
var remove = Has("--remove");
var only = ArgValue("--role");

if (!File.Exists(dbPath))
{
    Console.Error.WriteLine($"Database not found: {dbPath}");
    Console.Error.WriteLine("Start the platform once so it creates one, or pass --db \"C:\\path\\to\\platform.db\".");
    return 2;
}

// The suffix is what makes these accounts recognisable, and is the only thing this tool will
// write to or delete. A real address can never match it.
const string Domain = "@testing.local";

var wanted = new (string Key, string Email, string Name, TenantRole Role)[]
{
    ("technician", "tech" + Domain,    "Test Technician",  TenantRole.Technician),
    ("owner",      "owner" + Domain,   "Test Owner",       TenantRole.Admin),
    ("office",     "office" + Domain,  "Test Office Admin", TenantRole.Billing),
}.Where(w => only == null || string.Equals(only, w.Key, StringComparison.OrdinalIgnoreCase)).ToArray();

if (wanted.Length == 0)
{
    Console.Error.WriteLine($"Unknown role \"{only}\". Use technician, owner or office.");
    return 2;
}

var options = new DbContextOptionsBuilder<PlatformDb>().UseSqlite($"Data Source={dbPath}").Options;
await using var db = new PlatformDb(options);

var tenantId = ArgValue("--tenant") is { } t && int.TryParse(t, out var tid) ? tid : 0;
Tenant? tenant = tenantId > 0
    ? await db.Tenants.FirstOrDefaultAsync(x => x.Id == tenantId)
    // The oldest company, which on a development machine is the only one.
    : await db.Tenants.OrderBy(x => x.Id).FirstOrDefaultAsync();

if (tenant == null)
{
    Console.Error.WriteLine("There are no companies in that database yet. Sign up once in the portal first.");
    return 2;
}

Console.WriteLine($"Database : {dbPath}");
Console.WriteLine($"Company  : {tenant.CompanyName} (id {tenant.Id})");
Console.WriteLine();

if (remove)
{
    var gone = await db.Technicians.Where(x => x.Email.EndsWith(Domain)).ToListAsync();
    if (gone.Count == 0) { Console.WriteLine("No test accounts to remove."); return 0; }
    db.Technicians.RemoveRange(gone);
    await db.SaveChangesAsync();
    foreach (var g in gone) Console.WriteLine($"  removed  {g.Email}");
    Console.WriteLine();
    Console.WriteLine("Gone. Their tickets are untouched — AssignedTo is a name, not a link.");
    return 0;
}

var password = MakePassword();
var lines = new List<string>
{
    "Test accounts for The Remote Support Program",
    $"Written {DateTime.Now:yyyy-MM-dd HH:mm} by tools/testtech",
    $"Company: {tenant.CompanyName}",
    "",
    "These are for testing only. Remove them with:  dotnet run -c Release --project tools/testtech -- --remove",
    "",
    $"Password (all of them): {password}",
    ""
};

foreach (var w in wanted)
{
    var existing = await db.Technicians.FirstOrDefaultAsync(x => x.Email == w.Email);
    var (salt, hash) = Passwords.Create(password);

    if (existing != null)
    {
        // Belt and braces. The query above can only find a @testing.local address, but this is the
        // line that stops a future edit turning this tool into something that resets a real
        // person's password.
        if (!existing.Email.EndsWith(Domain, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Refusing to touch {existing.Email} — not a test account.");
            return 2;
        }
        existing.Salt = salt; existing.Hash = hash;
        existing.DisplayName = w.Name; existing.Role = w.Role;
        existing.Disabled = false; existing.TenantId = tenant.Id;
        Console.WriteLine($"  reset    {w.Email,-24} {w.Role}");
    }
    else
    {
        db.Technicians.Add(new Technician
        {
            TenantId = tenant.Id, Email = w.Email, DisplayName = w.Name,
            Salt = salt, Hash = hash, Role = w.Role
        });
        Console.WriteLine($"  created  {w.Email,-24} {w.Role}");
    }
    lines.Add($"{w.Role,-12} {w.Email}   \"{w.Name}\"");
}

await db.SaveChangesAsync();

var outPath = Path.Combine(Path.GetDirectoryName(dbPath)!, "test-accounts.txt");
await File.WriteAllLinesAsync(outPath, lines);

Console.WriteLine();
Console.WriteLine($"Password written to: {outPath}");
Console.WriteLine();
Console.WriteLine("Sign in at /login. The technician lands on /portal/tickets, the owner on /portal,");
Console.WriteLine("the office administrator on /portal/billing — each is sent to their own home.");
Console.WriteLine();
Console.WriteLine("NOTE: these consume seats, and the technician and owner can both sign in to the");
Console.WriteLine("console as well. Remove them before the seat count matters.");
return 0;

// ---------------------------------------------------------------------------------------

static bool Has(string flag) => Environment.GetCommandLineArgs().Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

static string? ArgValue(string name)
{
    var args = Environment.GetCommandLineArgs();
    for (int i = 0; i < args.Length - 1; i++)
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
    return null;
}

/// <summary>
/// Readable, typeable and still not guessable: three words and a number. Somebody is going to
/// type this into a browser by hand from a text file, and a base64 blob would get mistyped twice
/// before it worked.
/// </summary>
static string MakePassword()
{
    string[] words =
    {
        "amber", "harbour", "kestrel", "lantern", "meadow", "quartz", "ribbon", "saffron",
        "thistle", "willow", "cobalt", "ferry", "granite", "juniper", "marlin", "poppy"
    };
    var rnd = System.Security.Cryptography.RandomNumberGenerator.Create();
    string Pick()
    {
        var b = new byte[4]; rnd.GetBytes(b);
        return words[BitConverter.ToUInt32(b, 0) % words.Length];
    }
    var n = new byte[4]; rnd.GetBytes(n);
    return $"{Pick()}-{Pick()}-{Pick()}-{BitConverter.ToUInt32(n, 0) % 9000 + 1000}";
}

static string DefaultDbPath()
{
    var dir = AppContext.BaseDirectory;
    for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "RemoteSupport.sln")))
            return Path.Combine(d.FullName, "src", "RemoteSupport.Platform", "bin", "Release", "net10.0", "data", "platform.db");
    return Path.Combine(dir, "platform.db");
}
