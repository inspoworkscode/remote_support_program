using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

/// <summary>
/// Rows written the way the product wrote them BEFORE a migration: by SQL against the table as it
/// stood then. A harness that seeds the past through today's model breaks the day a column is added
/// (handover §44) — which is what happened to workflowcheck, rolecheck and calloutcheck on
/// 2026-09-29, when Tenants gained QuoteToAddress and QuoteDeliveryAddress.
/// </summary>
static class Seed
{
    /// <summary>A tenant, with every required column the table has at this migration given a blank.</summary>
    public static async Task OldTenant(PlatformDb db, int id, string slug, string name, string key, int seats = 5)
    {
        // On PostgreSQL the database is always current — no harness stands it at an older migration there — so
        // today's model is the right way to write the tenant (deliverycheck and machinescheck only want one).
        if (RemoteSupport.Tools.HarnessDb.Postgres) { await Tenant(db, id, slug, name, key, seats); return; }
        var conn = db.Database.GetDbConnection();
        bool opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) await conn.OpenAsync();
        var cols = new List<(string name, string type, bool required)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(Tenants)";
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                cols.Add((r.GetString(1), r.GetString(2), r.GetInt32(3) == 1 && r.IsDBNull(4) && r.GetInt32(5) == 0));
        }
        // The model's own defaults where they matter, so the tenant behaves as one made by the product.
        var given = new Dictionary<string, object>
        {
            ["Id"] = id, ["Slug"] = slug, ["CompanyName"] = name, ["TenantKey"] = key, ["Seats"] = seats,
            ["RequireConsent"] = 1, ["ConsentTimeoutSeconds"] = 60, ["Plan"] = "five-user", ["ProductName"] = "Remote Support",
            ["TechnicianTheme"] = "light", ["ClientTheme"] = "light", ["CreatedUtc"] = "2026-09-01 00:00:00"
        };
        foreach (var k in given.Keys.Where(k => !cols.Any(c => c.name == k)).ToList()) given.Remove(k);
        foreach (var c in cols.Where(c => c.required && !given.ContainsKey(c.name)))
            given[c.name] = c.type.StartsWith("INT") ? 0 : c.name.EndsWith("Utc") ? "2026-09-01 00:00:00" : "";
        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = $"INSERT INTO Tenants ({string.Join(",", given.Keys)}) VALUES ({string.Join(",", given.Keys.Select(k => "@" + k))})";
            foreach (var kv in given) { var p = ins.CreateParameter(); p.ParameterName = "@" + kv.Key; p.Value = kv.Value; ins.Parameters.Add(p); }
            await ins.ExecuteNonQueryAsync();
        }
        if (opened) await conn.CloseAsync();
    }

    /// <summary>
    /// The same tenant written through TODAY's model, for a harness on PostgreSQL (HANDOVER §118): there is no
    /// older migration to stand at, so the database is current and the model is the right way to write it.
    /// The values OldTenant gives that today's model has, it gives here too; the id counter is moved past it.
    /// </summary>
    public static async Task Tenant(PlatformDb db, int id, string slug, string name, string key, int seats = 5)
    {
        db.Tenants.Add(new Tenant
        {
            Id = id, Slug = slug, CompanyName = name, TenantKey = key, Seats = seats, ProductName = "Remote Support",
            CreatedUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
        await RemoteSupport.Tools.HarnessDb.ResetSequencesAsync(db);
    }

    /// <summary>
    /// Any row of any table as it stood at this migration: the columns given, and a blank for every
    /// other required one. Foreign keys are not checked — an old row is seeded for what one migration
    /// does to it, not to stand up a whole company around it.
    /// </summary>
    public static async Task OldRow(PlatformDb db, string table, Dictionary<string, object?> given)
    {
        var conn = db.Database.GetDbConnection();
        bool opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) await conn.OpenAsync();
        var cols = new List<(string name, string type, bool required)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info({table})";
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                cols.Add((r.GetString(1), r.GetString(2), r.GetInt32(3) == 1 && r.IsDBNull(4) && r.GetInt32(5) == 0));
        }
        var row = new Dictionary<string, object?>(given);
        foreach (var k in row.Keys.Where(k => !cols.Any(c => c.name == k)).ToList())
            throw new InvalidOperationException($"{table} has no column {k} at this migration.");
        foreach (var c in cols.Where(c => c.required && !row.ContainsKey(c.name)))
            row[c.name] = c.type.StartsWith("INT") ? 0 : c.name.EndsWith("Utc") ? "2026-09-01 00:00:00" : "";
        using (var off = conn.CreateCommand()) { off.CommandText = "PRAGMA foreign_keys = OFF"; await off.ExecuteNonQueryAsync(); }
        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = $"INSERT INTO {table} ({string.Join(",", row.Keys)}) VALUES ({string.Join(",", row.Keys.Select(k => "@" + k))})";
            foreach (var kv in row) { var p = ins.CreateParameter(); p.ParameterName = "@" + kv.Key; p.Value = kv.Value ?? DBNull.Value; ins.Parameters.Add(p); }
            await ins.ExecuteNonQueryAsync();
        }
        if (opened) await conn.CloseAsync();
    }
}
