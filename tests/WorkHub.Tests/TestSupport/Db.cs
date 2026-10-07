using Microsoft.Data.Sqlite;

namespace WorkHub.Tests.TestSupport;

internal static class Db
{
    public const string Now = "2026-01-01T00:00:00.000Z";

    public static void Exec(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public static long Count(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM \"{table}\";";
        return (long)command.ExecuteScalar()!;
    }

    public static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    public static List<string> UserTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    public static long InsertSystem(SqliteConnection connection, string code, string name = "Test system")
    {
        Exec(connection,
            "INSERT INTO Systems (SystemCode, SystemName, CreatedAt, UpdatedAt) VALUES ($code, $name, $now, $now);",
            ("$code", code), ("$name", name), ("$now", Now));
        return ScalarLong(connection, "SELECT last_insert_rowid();");
    }

    public static void InsertSystemRepository(SqliteConnection connection, long systemId, string repositoryType) =>
        Exec(connection,
            "INSERT INTO SystemRepositories (SystemId, RepositoryType) VALUES ($id, $type);",
            ("$id", systemId), ("$type", repositoryType));
}
