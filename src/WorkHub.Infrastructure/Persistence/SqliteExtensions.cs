using Microsoft.Data.Sqlite;

namespace WorkHub.Infrastructure.Persistence;

internal static class SqliteExtensions
{
    public static void Execute(this SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Runs a multi-statement script and steps through EVERY statement's result, so an error in any later statement is
    /// thrown here. Needed for scripts that alter STRICT tables: Microsoft.Data.Sqlite (10.0.12, SQLite 3.53.3) reports
    /// <c>ALTER TABLE … ADD COLUMN</c> on a STRICT table as a statement with a result column, so <c>ExecuteNonQuery</c> stops
    /// there and runs the remaining statements while disposing its reader — where an error ends the script WITHOUT an
    /// exception, and the caller would commit a partial schema change.
    /// </summary>
    public static void ExecuteScript(this SqliteConnection connection, string sql, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.NextResult())
        {
        }
    }

    public static T? Scalar<T>(this SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static int GetUserVersion(this SqliteConnection connection, SqliteTransaction? transaction = null) =>
        connection.Scalar<int>("PRAGMA user_version;", transaction);

    /// <summary>PRAGMA cannot be parameterized; the value is an int so formatting is safe.</summary>
    public static void SetUserVersion(this SqliteConnection connection, int version, SqliteTransaction transaction) =>
        connection.Execute($"PRAGMA user_version = {version:D};", transaction);

    /// <summary>Returns null when the quick integrity check passes, otherwise the first reported problem.</summary>
    public static string? QuickCheck(this SqliteConnection connection)
    {
        var result = connection.Scalar<string>("PRAGMA quick_check(1);");
        return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase) ? null : result ?? "no result";
    }

    public static bool HasUserObjects(this SqliteConnection connection) =>
        connection.Scalar<long>("SELECT count(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%';") > 0;
}
