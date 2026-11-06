using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Copies the live SQLite database into PostgreSQL, then proves the copy (2026-10-09, step 4 of HANDOVER §118):
///
/// <code>RemoteSupport.Platform copy-to-postgres --from &lt;platform.db&gt; --to "&lt;connection string&gt;" [--verify-only]</code>
///
/// <list type="number">
/// <item>Refuses a SQLite database with migrations still to apply (deploy first), a PostgreSQL database that already
/// holds a row — a copy is only ever made into an empty one — and a model this copy cannot carry faithfully (a
/// column with no property behind it, two kinds of row in one table, an owned type): both the copy and the check
/// read C# properties, so such a column would be lost AND the check would agree with the loss.</item>
/// <item>Refuses text holding a NUL character, naming the row: SQLite stores one, PostgreSQL cannot.</item>
/// <item>Builds the PostgreSQL schema from <see cref="PgPlatformDb"/>'s own migrations.</item>
/// <item>Copies every table in the order its foreign keys need, ids and all. A table that points at itself
/// (a ticket's parent) is written with that link empty and then given it, so no row waits on one not yet there.</item>
/// <item>Moves every id counter past the highest id copied, or the next row the platform adds would collide.</item>
/// <item>Checks EVERY table: the same number of rows, and the same contents — every column of every row, by a
/// SHA-256 of both sides in key order. Times are compared to the microsecond, which is what PostgreSQL keeps
/// (§119); nothing else is allowed to differ.</item>
/// </list>
/// The SQLite file is opened read-only and stays the way back. <b>Program.cs runs this BEFORE the platform is
/// built</b>, because building it migrates the SQLite database — a write, and it would hide a file that is behind.
/// </summary>
public static class PostgresCopy
{
    private const int Batch = 1000;

    public static async Task<int> RunAsync(string[] a)
    {
        string? Arg(string name) { var i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
        var from = Arg("--from"); var to = Arg("--to");
        bool verifyOnly = a.Contains("--verify-only");
        if (from == null || to == null)
        {
            Console.WriteLine("Usage: copy-to-postgres --from <platform.db> --to \"<PostgreSQL connection string>\" [--verify-only]");
            return 1;
        }
        if (!File.Exists(from)) { Console.WriteLine($"REFUSED: no SQLite database at {from}"); return 1; }

        var srcOptions = new DbContextOptionsBuilder<PlatformDb>().UseSqlite($"Data Source={from};Mode=ReadOnly;Pooling=False").Options;
        var dstOptions = new DbContextOptionsBuilder<PgPlatformDb>().UseNpgsql(PgPlatformDb.Prepare(to)).Options;
        PlatformDb Src() => new(srcOptions);
        PgPlatformDb Dst() { var d = new PgPlatformDb(dstOptions); d.ChangeTracker.AutoDetectChangesEnabled = false; return d; }

        Console.WriteLine($"From  : {from}");
        Console.WriteLine($"To    : {PgPlatformDb.Describe(to)}");

        await using (var s = Src())
        {
            var pending = (await s.Database.GetPendingMigrationsAsync()).ToList();
            if (pending.Count > 0)
            {
                Console.WriteLine($"REFUSED: the SQLite database is {pending.Count} migration(s) behind this program ({pending[0]}...). Start the platform on it once (a deploy does), then copy.");
                return 1;
            }
            var cannot = CannotCarry(s.Model);
            if (cannot.Count > 0)
            {
                Console.WriteLine("REFUSED: this copy cannot carry every column of this model faithfully:");
                foreach (var c in cannot) Console.WriteLine("  " + c);
                return 1;
            }
        }

        List<IEntityType> order;
        await using (var d = Dst())
        {
            await d.Database.MigrateAsync();
            order = CopyOrder(d.Model);
            if (!verifyOnly)
                foreach (var t in order)
                    if (await AnyAsync(d, t))
                    {
                        Console.WriteLine($"REFUSED: the PostgreSQL database already has rows in {t.GetTableName()}. A copy is only made into an empty database.");
                        return 1;
                    }
        }

        var started = DateTime.UtcNow;
        if (!verifyOnly)
        {
            var nul = new List<string>();
            foreach (var t in order) nul.AddRange(await NulsAsync(t, Src));
            if (nul.Count > 0)
            {
                Console.WriteLine("REFUSED: PostgreSQL cannot store a NUL character in text, and these hold one:");
                foreach (var n in nul.Take(20)) Console.WriteLine("  " + n);
                if (nul.Count > 20) Console.WriteLine($"  ...and {nul.Count - 20} more");
                Console.WriteLine("Nothing was copied. The PostgreSQL database holds the empty schema only.");
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine("== copying ==");
            foreach (var t in order)
            {
                var n = await CopyTableAsync(t, Src, Dst);
                Console.WriteLine($"  {t.GetTableName(),-28} {n,8} rows");
            }
            await using (var d = Dst()) await ResetSequencesAsync(d);
            Console.WriteLine($"  (id counters moved past the copied ids; {(DateTime.UtcNow - started).TotalSeconds:0.0} s)");
        }

        Console.WriteLine();
        Console.WriteLine("== checking every table, row by row ==");
        int bad = 0; long total = 0;
        foreach (var t in order)
        {
            var (sn, sh) = await FingerprintAsync(t, Src);
            var (dn, dh) = await FingerprintAsync(t, () => Dst());
            bool ok = sn == dn && sh == dh;
            if (!ok) bad++;
            total += sn;
            Console.WriteLine($"  {(ok ? "same" : "DIFFERENT"),-9} {t.GetTableName(),-28} {sn,8} rows{(sn != dn ? $" - PostgreSQL has {dn}" : sh != dh ? " - the same number, but the contents differ" : "")}");
            if (!ok) Console.WriteLine("            first difference: " + await FirstDifferenceAsync(t, Src, () => Dst()));
        }
        Console.WriteLine();
        if (bad > 0) { Console.WriteLine($"FAILED: {bad} table(s) differ. The platform must NOT be switched to this copy."); return 1; }
        Console.WriteLine($"OK: all {order.Count} tables, {total} rows, are identical in PostgreSQL ({(DateTime.UtcNow - started).TotalSeconds:0.0} s).");
        return 0;
    }

    /// <summary>What this copy would lose without a word: a column with no C# property (a shadow property), two
    /// entity types sharing one table, an owned type, a table with no key.</summary>
    private static List<string> CannotCarry(IModel model)
    {
        var why = new List<string>();
        var types = model.GetEntityTypes().ToList();
        foreach (var t in types)
        {
            if (t.IsOwned()) why.Add($"{t.ClrType.Name} is an owned type");
            if (t.FindPrimaryKey() == null) why.Add($"{t.ClrType.Name} has no key");
            foreach (var p in t.GetProperties().Where(p => p.PropertyInfo == null))
                why.Add($"{t.ClrType.Name}.{p.Name} has a column but no property");
        }
        foreach (var g in types.Where(t => t.GetTableName() != null).GroupBy(t => t.GetTableName()).Where(g => g.Count() > 1))
            why.Add($"table {g.Key} holds {string.Join(" and ", g.Select(t => t.ClrType.Name))}");
        return why;
    }

    /// <summary>Every table, each after the tables its foreign keys point at (a table pointing at itself is handled
    /// in <see cref="CopyOf{T}"/>, not here).</summary>
    private static List<IEntityType> CopyOrder(IModel model)
    {
        var all = model.GetEntityTypes().Where(t => t.GetTableName() != null && !t.IsOwned()).ToList();
        var done = new List<IEntityType>();
        var placed = new HashSet<IEntityType>();
        while (done.Count < all.Count)
        {
            var next = all.Where(t => !placed.Contains(t)
                && t.GetForeignKeys().All(fk => fk.PrincipalEntityType == t || placed.Contains(fk.PrincipalEntityType))).ToList();
            if (next.Count == 0)
                throw new InvalidOperationException("The tables' foreign keys go round in a circle: " +
                    string.Join(", ", all.Where(t => !placed.Contains(t)).Select(t => t.GetTableName())));
            foreach (var t in next.OrderBy(t => t.GetTableName())) { done.Add(t); placed.Add(t); }
        }
        return done;
    }

    private static Task<T> Generic<T>(string method, IEntityType t, params object[] args) =>
        (Task<T>)typeof(PostgresCopy).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(t.ClrType).Invoke(null, args)!;

    private static Task<bool> AnyAsync(DbContext d, IEntityType t) => Generic<bool>(nameof(AnyOf), t, d);
    private static Task<bool> AnyOf<T>(DbContext d) where T : class => d.Set<T>().AnyAsync();

    private static Task<List<string>> NulsAsync(IEntityType t, Func<PlatformDb> src) => Generic<List<string>>(nameof(NulsOf), t, t, src);
    private static async Task<List<string>> NulsOf<T>(IEntityType t, Func<PlatformDb> src) where T : class
    {
        List<T> rows;
        await using (var s = src()) rows = await s.Set<T>().AsNoTracking().ToListAsync();
        var key = t.FindPrimaryKey()!.Properties.Select(p => p.PropertyInfo!).ToList();
        var texts = t.GetProperties().Select(p => p.PropertyInfo!).Where(p => p.PropertyType == typeof(string)).ToList();
        var found = new List<string>();
        foreach (var r in rows)
            foreach (var p in texts)
                if (p.GetValue(r) is string v && v.Contains('\0'))
                    found.Add($"{t.GetTableName()}.{p.Name}, row {string.Join("/", key.Select(k => k.GetValue(r)))}");
        return found;
    }

    private static Task<int> CopyTableAsync(IEntityType t, Func<PlatformDb> src, Func<PgPlatformDb> dst) => Generic<int>(nameof(CopyOf), t, t, src, dst);
    private static async Task<int> CopyOf<T>(IEntityType t, Func<PlatformDb> src, Func<PgPlatformDb> dst) where T : class
    {
        List<T> rows;
        await using (var s = src()) rows = await s.Set<T>().AsNoTracking().ToListAsync();
        rows = OrderByKey(t, rows);

        // A link to a row of this same table (a ticket's parent): written empty, then given, after every row exists.
        var selfLinks = t.GetForeignKeys().Where(fk => fk.PrincipalEntityType == t)
            .SelectMany(fk => fk.Properties).Select(p => p.PropertyInfo!).Distinct().ToList();
        var later = new List<(T row, PropertyInfo prop, object value)>();
        foreach (var r in rows)
            foreach (var p in selfLinks)
                if (p.GetValue(r) is { } v) { later.Add((r, p, v)); p.SetValue(r, null); }

        for (int i = 0; i < rows.Count; i += Batch)
        {
            await using var d = dst();
            d.Set<T>().AddRange(rows.Skip(i).Take(Batch));
            d.ChangeTracker.DetectChanges();
            await d.SaveChangesAsync();
        }
        for (int i = 0; i < later.Count; i += Batch)
        {
            await using var d = dst();
            foreach (var (r, p, v) in later.Skip(i).Take(Batch))
            {
                p.SetValue(r, v);
                var e = d.Attach(r);
                e.State = EntityState.Unchanged;
                e.Property(p.Name).IsModified = true;
            }
            await d.SaveChangesAsync();
        }
        return rows.Count;
    }

    private static List<T> OrderByKey<T>(IEntityType t, List<T> rows)
    {
        var key = t.FindPrimaryKey()!.Properties.Select(p => p.PropertyInfo!).ToList();
        IOrderedEnumerable<T> o = rows.OrderBy(r => key[0].GetValue(r));
        foreach (var k in key.Skip(1)) o = o.ThenBy(r => k.GetValue(r));
        return o.ToList();
    }

    private static Task<(long, string)> FingerprintAsync(IEntityType t, Func<PlatformDb> open) => Generic<(long, string)>(nameof(FingerprintOf), t, t, open);

    /// <summary>Every column of every row, in key order, into one SHA-256 — the same on both sides or the copy is wrong.</summary>
    private static async Task<(long, string)> FingerprintOf<T>(IEntityType t, Func<PlatformDb> open) where T : class
    {
        var (_, rows) = await CanonicalRowsOf<T>(t, open);
        using var sha = SHA256.Create();
        foreach (var (_, cols) in rows)
        {
            var bytes = Encoding.UTF8.GetBytes(string.Join('\u001f', cols) + "\u001f\u001e");
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return (rows.Count, Convert.ToHexString(sha.Hash!));
    }

    /// <summary>Every row as its key and its columns' canonical values, in key order; the columns by name.</summary>
    private static async Task<(List<IProperty> props, List<(string key, string[] cols)> rows)> CanonicalRowsOf<T>(IEntityType t, Func<PlatformDb> open) where T : class
    {
        List<T> rows;
        await using (var d = open()) rows = await d.Set<T>().AsNoTracking().ToListAsync();
        rows = OrderByKey(t, rows);
        var key = t.FindPrimaryKey()!.Properties.Select(p => p.PropertyInfo!).ToList();
        var props = t.GetProperties().OrderBy(p => p.Name).ToList();
        return (props, rows.Select(r => (string.Join("/", key.Select(k => k.GetValue(r))),
            props.Select(p => Canonical(p.PropertyInfo!.GetValue(r))).ToArray())).ToList());
    }

    private static Task<string> FirstDifferenceAsync(IEntityType t, Func<PlatformDb> src, Func<PlatformDb> dst) =>
        Generic<string>(nameof(FirstDifferenceOf), t, t, src, dst);

    /// <summary>Where the first difference is: the row's key and the column, and for text only WHERE in it — never
    /// the text itself, which may be a password hash or a customer's words.</summary>
    private static async Task<string> FirstDifferenceOf<T>(IEntityType t, Func<PlatformDb> src, Func<PlatformDb> dst) where T : class
    {
        var (props, s) = await CanonicalRowsOf<T>(t, src);
        var (_, d) = await CanonicalRowsOf<T>(t, dst);
        var dByKey = d.GroupBy(r => r.key).ToDictionary(g => g.Key, g => g.First().cols);
        foreach (var (key, cols) in s)
        {
            if (!dByKey.TryGetValue(key, out var other)) return $"row {key} is missing from PostgreSQL";
            for (int i = 0; i < cols.Length; i++)
            {
                if (cols[i] == other[i]) continue;
                var type = Nullable.GetUnderlyingType(props[i].ClrType) ?? props[i].ClrType;
                if (type == typeof(string) || type == typeof(byte[]))
                {
                    int at = 0; while (at < cols[i].Length && at < other[i].Length && cols[i][at] == other[i][at]) at++;
                    return $"row {key}, {props[i].Name}: {cols[i].Length} characters in SQLite, {other[i].Length} in PostgreSQL, first differing at {at}";
                }
                return $"row {key}, {props[i].Name}: SQLite {cols[i]}, PostgreSQL {other[i]}";
            }
        }
        var extra = d.Select(r => r.key).Except(s.Select(r => r.key)).FirstOrDefault();
        return extra != null ? $"row {extra} is in PostgreSQL only" : "(none found row by row)";
    }

    /// <summary>PostgreSQL counts time in microseconds from here, and Npgsql drops a tenth of one TOWARD it — down
    /// after 2000, UP before (pgcopycheck, 2026-10-09: 1999-12-31 23:59:59.9999999 is stored as 2000-01-01).</summary>
    private static readonly long PgEpochTicks = new DateTime(2000, 1, 1).Ticks;
    private static long ToPgMicrosecond(long ticks) { var rel = ticks - PgEpochTicks; return PgEpochTicks + rel - rel % 10; }

    private static string Canonical(object? v) => v switch
    {
        null => "∅",
        string s => s,
        // To the microsecond, exactly as PostgreSQL keeps it, and a copy cannot be blamed for a tenth of one.
        DateTime dt => ToPgMicrosecond(dt.Ticks).ToString(),
        DateTimeOffset dto => ToPgMicrosecond(dto.UtcTicks).ToString(),
        TimeSpan ts => (ts.Ticks - ts.Ticks % 10).ToString(),
        decimal m => m.ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture),
        double x => x.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        byte[] b => Convert.ToBase64String(b),
        bool b => b ? "1" : "0",
        Enum e => Convert.ToInt64(e).ToString(),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        // A list stored in one column: its contents, never its type's name (which is the same on both sides).
        IEnumerable list => "[" + string.Join("\u001d", list.Cast<object?>().Select(Canonical)) + "]",
        _ => v.ToString() ?? ""
    };

    /// <summary>Every identity counter past the highest id in its table (the harnesses' HarnessDb does the same).</summary>
    private static Task ResetSequencesAsync(DbContext d) => d.Database.ExecuteSqlRawAsync("""
        DO $body$
        DECLARE r record;
        BEGIN
          FOR r IN SELECT table_name, column_name FROM information_schema.columns
                   WHERE table_schema = 'public' AND is_identity = 'YES' LOOP
            EXECUTE format('SELECT setval(pg_get_serial_sequence(%L, %L), COALESCE((SELECT MAX(%I) FROM %I), 0) + 1, false)',
                           '"' || r.table_name || '"', r.column_name, r.column_name, r.table_name);
          END LOOP;
        END
        $body$;
        """);
}
