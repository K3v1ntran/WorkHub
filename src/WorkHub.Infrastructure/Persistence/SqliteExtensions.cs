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
