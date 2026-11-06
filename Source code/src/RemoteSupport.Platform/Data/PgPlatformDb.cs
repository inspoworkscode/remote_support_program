using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace RemoteSupport.Platform.Data;

/// <summary>
/// The same database on PostgreSQL (2026-10-08, HANDOVER §118). Used when <c>ConnectionStrings:Postgres</c> is
/// set (on the server, <c>RSP_ConnectionStrings__Postgres</c>); without it the platform stays on SQLite.
///
/// <para>A subclass for ONE reason: EF finds a context's migrations by its type, and PostgreSQL needs a set of its
/// own (<c>Data/PgMigrations</c>) beside SQLite's 103. Everything else — every table, index, rule and the UTC
/// converter — is <see cref="PlatformDb"/>'s, and the rest of the program only ever sees a
/// <see cref="PlatformDb"/> (<see cref="PlatformDbFactory"/>).</para>
///
/// <para><b>Two contexts means <c>dotnet ef</c> needs <c>--context</c></b>: <c>--context PlatformDb</c> for a SQLite
/// migration as before, <c>--context PgPlatformDb --output-dir Data/PgMigrations</c> for this one. <b>Every model
/// change now needs BOTH</b>, the same name in each set (2026-10-09, step 4 of HANDOVER §118): <c>PgInitial</c> is
/// frozen — PostgreSQL servers hold data, and a PgInitial made again has a new id that a server would try to
/// apply on top of its tables and refuse to start. <c>pgcheck</c> fails when either set is behind the model, and
/// when PgInitial has been made again.</para>
/// </summary>
public sealed class PgPlatformDb : PlatformDb
{
    public PgPlatformDb(DbContextOptions<PgPlatformDb> o) : base(o) { }

    /// <summary>The platform's own connections at most, unless the connection string names a number (step 3 of §118).</summary>
    public const int DefaultMaxPoolSize = 40;

    /// <summary>
    /// The connection string the platform actually uses: the configured one, with the pool kept well under
    /// PostgreSQL's own limit (100 by default, three of them kept for the administrator). Npgsql's default
    /// pool is ALSO 100, so in a rush of machines saying hello it asked PostgreSQL for more connections than it
    /// allows, each refusal ("remaining connection slots are reserved...") dropped a machine, and the machines
    /// came straight back for more (loadtest, 2026-10-08: 1,847 of 5,000 held). Under the cap a request waits
    /// for a free connection instead — milliseconds, since every call is short. The command line, backups and
    /// an administrator use the rest.
    /// </summary>
    public static string Prepare(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        if (!System.Text.RegularExpressions.Regex.IsMatch(connectionString, @"max(imum)?\s*pool\s*size", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            b.MaxPoolSize = DefaultMaxPoolSize;
        return b.ConnectionString;
    }

    /// <summary>"PostgreSQL host/database" — what the start-up line says, never the password.</summary>
    public static string Describe(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        return $"PostgreSQL {b.Host}{(b.Port != 5432 ? ":" + b.Port : "")}/{b.Database}";
    }
}

/// <summary>Hands out <see cref="PgPlatformDb"/>s to code that asks for a <see cref="PlatformDb"/>.</summary>
public sealed class PlatformDbFactory : IDbContextFactory<PlatformDb>
{
    private readonly IDbContextFactory<PgPlatformDb> _pg;
    public PlatformDbFactory(IDbContextFactory<PgPlatformDb> pg) => _pg = pg;
    public PlatformDb CreateDbContext() => _pg.CreateDbContext();
    public async Task<PlatformDb> CreateDbContextAsync(CancellationToken ct = default) => await _pg.CreateDbContextAsync(ct);
}

/// <summary>
/// For <c>dotnet ef migrations add … --context PlatformDb</c> (2026-10-09). Without it, EF answered a request
/// for <see cref="PlatformDb"/> with the factory below — a <see cref="PgPlatformDb"/> IS a PlatformDb — so the
/// first SQLite migration after the split went into the PostgreSQL set, against PostgreSQL. Measured:
/// <c>dotnet ef dbcontext info --context PlatformDb</c> said "Type: PgPlatformDb, Provider: Npgsql".
/// </summary>
public sealed class PlatformDbDesignTime : IDesignTimeDbContextFactory<PlatformDb>
{
    public PlatformDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PlatformDb>().UseSqlite("Data Source=design-time.db").Options);
}

/// <summary>For <c>dotnet ef migrations add … --context PgPlatformDb</c>: making a migration needs no server.</summary>
public sealed class PgPlatformDbDesignTime : IDesignTimeDbContextFactory<PgPlatformDb>
{
    public PgPlatformDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PgPlatformDb>().UseNpgsql("Host=localhost;Database=rsp_design").Options);
}
