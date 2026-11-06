using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace RemoteSupport.Tools;

/// <summary>The hand-written-SQL half of <see cref="HarnessDb"/>, for harnesses that query the platform's
/// database directly. The SQL must suit both: quoted names, <c>@name</c> parameters, TRUE/FALSE.</summary>
internal static partial class HarnessDb
{
    /// <summary>In place of <c>new SqliteConnection(connectionString)</c>.</summary>
    public static DbConnection Connect(string sqliteConnectionString) =>
        Postgres ? new NpgsqlConnection(ForFile(FileOf(sqliteConnectionString))) : new SqliteConnection(sqliteConnectionString);

    /// <summary>In place of <c>Parameters.AddWithValue</c>, which only the providers' own command types have.</summary>
    public static DbCommand With(this DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
        return cmd;
    }
}
