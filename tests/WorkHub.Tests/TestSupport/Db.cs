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

    public static long ScalarLongWith(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return (long)command.ExecuteScalar()!;
    }

    public static string? ScalarString(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteScalar() as string;
    }

    /// <summary>Raw Workspace node insert (schema tests); the key is whatever the caller passes.</summary>
    public static long InsertNode(SqliteConnection connection, long? parentId, string type, string name, string? key = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO WorkspaceNodes (ParentId, NodeType, Name, NameKey, CreatedAt, UpdatedAt)
            VALUES ($parent, $type, $name, $key, $now, $now) RETURNING Id;
            """;
        command.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$key", key ?? name.ToUpperInvariant());
        command.Parameters.AddWithValue("$now", Now);
        return (long)command.ExecuteScalar()!;
    }

    public static void InsertProjectDetails(SqliteConnection connection, long nodeId, string projectId = "0123456789abcdef0123456789abcdef") =>
        Exec(connection, "INSERT INTO Projects (NodeId, ProjectId) VALUES ($id, $pid);", ("$id", nodeId), ("$pid", projectId));

    /// <summary>
    /// Simulates a Project removed outside WorkHub (V007 makes Project nodes undeletable): in one transaction, drops the two
    /// V007 delete guards, deletes the details row and node, and recreates the guards from their stored SQL.
    /// </summary>
    public static void DeleteProjectBypassingGuards(SqliteConnection connection, long nodeId)
    {
        using var transaction = connection.BeginTransaction();
        var guards = new List<string>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT sql FROM sqlite_schema WHERE type = 'trigger' AND name IN ('TR_WorkspaceNodes_DeleteOnlyEmptyFolder', 'TR_Projects_NoDelete');";
            using var reader = read.ExecuteReader();
            while (reader.Read())
                guards.Add(reader.GetString(0));
        }

        Run("DROP TRIGGER IF EXISTS TR_WorkspaceNodes_DeleteOnlyEmptyFolder; DROP TRIGGER IF EXISTS TR_Projects_NoDelete;");
        Run($"DELETE FROM Projects WHERE NodeId = {nodeId:D}; DELETE FROM WorkspaceNodes WHERE Id = {nodeId:D};");
        foreach (var sql in guards)
            Run(sql);
        transaction.Commit();

        void Run(string sql)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
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
