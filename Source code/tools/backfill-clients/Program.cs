// =====================================================================================
// backfill-clients - file orphaned tickets under the client that owns their machine.
//
// Why this exists: a ticket raised from a machine that was not yet linked to a client is
// stored with ClientId = NULL. Linking the device afterwards does not go back and fix it,
// so the work never appears on that customer's page. Reinstalls make this common: the PC
// comes back as a second Device row with no client, and everything raised from it in the
// meantime is orphaned.
//
// Safe by default. It prints what it would do and changes nothing unless you pass --apply,
// and --apply copies the database first.
// =====================================================================================

using System.Globalization;
using Microsoft.Data.Sqlite;

var apply = args.Contains("--apply", StringComparer.OrdinalIgnoreCase);
var dbPath = ArgValue("--db") ?? DefaultDbPath();

if (!File.Exists(dbPath))
{
    Console.Error.WriteLine($"Database not found: {dbPath}");
    Console.Error.WriteLine("Pass --db \"C:\\path\\to\\platform.db\".");
    return 2;
}

Console.WriteLine($"Database : {dbPath}");
Console.WriteLine($"Mode     : {(apply ? "APPLY - changes will be written" : "dry run - nothing will be written")}");
Console.WriteLine();

using var cn = new SqliteConnection($"Data Source={dbPath}");
cn.Open();

// ---- what the machines tell us -------------------------------------------------------
// A device row only counts as evidence if it actually has a client.
var byDeviceId = new Dictionary<string, (int tenantId, int clientId)>(StringComparer.OrdinalIgnoreCase);
var byMachine = new Dictionary<(int tenantId, string machine), HashSet<int>>();

using (var q = cn.CreateCommand())
{
    q.CommandText = "SELECT TenantId, DeviceId, MachineName, ClientId FROM Devices WHERE ClientId IS NOT NULL";
    using var r = q.ExecuteReader();
    while (r.Read())
    {
        var tenantId = r.GetInt32(0);
        var deviceId = r.IsDBNull(1) ? "" : r.GetString(1);
        var machine = r.IsDBNull(2) ? "" : r.GetString(2);
        var clientId = r.GetInt32(3);
        if (deviceId.Length > 0) byDeviceId[deviceId] = (tenantId, clientId);
        if (machine.Length > 0)
        {
            var key = (tenantId, machine.ToLowerInvariant());
            if (!byMachine.TryGetValue(key, out var set)) byMachine[key] = set = new HashSet<int>();
            set.Add(clientId);
        }
    }
}

var clientNames = new Dictionary<int, string>();
using (var q = cn.CreateCommand())
{
    q.CommandText = "SELECT Id, Name FROM Clients";
    using var r = q.ExecuteReader();
    while (r.Read()) clientNames[r.GetInt32(0)] = r.GetString(1);
}

// ---- the orphans ---------------------------------------------------------------------
var planned = new List<(int id, int number, int tenantId, string subject, string machine, int clientId, string how)>();
var skipped = new List<(int number, string machine, string why)>();

using (var q = cn.CreateCommand())
{
    q.CommandText = "SELECT Id, Number, TenantId, Subject, DeviceId, MachineName FROM Tickets WHERE ClientId IS NULL ORDER BY Number";
    using var r = q.ExecuteReader();
    while (r.Read())
    {
        var id = r.GetInt32(0);
        var number = r.GetInt32(1);
        var tenantId = r.GetInt32(2);
        var subject = r.IsDBNull(3) ? "" : r.GetString(3);
        var deviceId = r.IsDBNull(4) ? "" : r.GetString(4);
        var machine = r.IsDBNull(5) ? "" : r.GetString(5);

        // The device's own identity is the strongest evidence, so try it first.
        if (deviceId.Length > 0 && byDeviceId.TryGetValue(deviceId, out var hit) && hit.tenantId == tenantId)
        {
            planned.Add((id, number, tenantId, subject, machine, hit.clientId, "device id"));
            continue;
        }

        if (machine.Length == 0) { skipped.Add((number, "-", "no machine recorded - nothing to match on")); continue; }

        if (!byMachine.TryGetValue((tenantId, machine.ToLowerInvariant()), out var clients))
        { skipped.Add((number, machine, "no linked device with that machine name")); continue; }

        // Two customers with a machine of the same name is not something to guess at.
        if (clients.Count > 1)
        { skipped.Add((number, machine, $"machine name maps to {clients.Count} different clients - left alone")); continue; }

        planned.Add((id, number, tenantId, subject, machine, clients.First(), "machine name"));
    }
}

if (planned.Count == 0 && skipped.Count == 0) { Console.WriteLine("No tickets are missing a client. Nothing to do."); return 0; }

if (planned.Count > 0)
{
    Console.WriteLine($"Would file {planned.Count} ticket(s):");
    Console.WriteLine();
    Console.WriteLine($"  {"#",-6} {"Machine",-20} {"Client",-24} {"Matched on",-12} Subject");
    Console.WriteLine($"  {new string('-', 6)} {new string('-', 20)} {new string('-', 24)} {new string('-', 12)} -------");
    foreach (var p in planned)
    {
        var name = clientNames.TryGetValue(p.clientId, out var n) ? n : $"client {p.clientId}";
        Console.WriteLine($"  {"#" + p.number,-6} {Trim(p.machine, 20),-20} {Trim(name, 24),-24} {p.how,-12} {Trim(p.subject, 40)}");
    }
    Console.WriteLine();
}

if (skipped.Count > 0)
{
    Console.WriteLine($"Leaving {skipped.Count} ticket(s) alone:");
    foreach (var s in skipped) Console.WriteLine($"  #{s.number,-5} {Trim(s.machine, 20),-20} {s.why}");
    Console.WriteLine();
}

if (!apply)
{
    Console.WriteLine("Dry run - nothing was written. Re-run with --apply to make these changes.");
    return 0;
}

if (planned.Count == 0) { Console.WriteLine("Nothing to apply."); return 0; }

// ---- apply ---------------------------------------------------------------------------
// Copy first. This edits history, and history is the point of a ticketing system.
var backup = dbPath + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
File.Copy(dbPath, backup);
Console.WriteLine($"Backup written: {backup}");

var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
using (var tx = cn.BeginTransaction())
{
    foreach (var p in planned)
    {
        var name = clientNames.TryGetValue(p.clientId, out var n) ? n : $"client {p.clientId}";

        using (var up = cn.CreateCommand())
        {
            up.Transaction = tx;
            // UpdatedUtc is deliberately left alone: bumping it would make every backfilled
            // ticket look freshly worked on and empty the dashboard's "untouched" list.
            up.CommandText = "UPDATE Tickets SET ClientId = $c WHERE Id = $id AND ClientId IS NULL";
            up.Parameters.AddWithValue("$c", p.clientId);
            up.Parameters.AddWithValue("$id", p.id);
            up.ExecuteNonQuery();
        }

        using (var note = cn.CreateCommand())
        {
            note.Transaction = tx;
            note.CommandText = @"INSERT INTO TicketNotes (TenantId, TicketId, Author, Body, IsSystem, CreatedUtc)
                                 VALUES ($t, $id, 'system', $b, 1, $c)";
            note.Parameters.AddWithValue("$t", p.tenantId);
            note.Parameters.AddWithValue("$id", p.id);
            note.Parameters.AddWithValue("$b", $"filed under {name} by backfill (matched on {p.how})");
            note.Parameters.AddWithValue("$c", stamp);
            note.ExecuteNonQuery();
        }
    }
    tx.Commit();
}

Console.WriteLine($"Done. {planned.Count} ticket(s) filed, each with a note recording why.");
Console.WriteLine("Restart the platform server so the portal reads the new values.");
return 0;

static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "...";

string? ArgValue(string name)
{
    var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// The live server's database, relative to this tool inside the repository.
static string DefaultDbPath()
{
    var dir = AppContext.BaseDirectory;
    for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "RemoteSupport.sln")))
            return Path.Combine(d.FullName, "src", "RemoteSupport.Platform", "bin", "Release", "net10.0", "data", "platform.db");
    return Path.Combine(dir, "platform.db");
}
