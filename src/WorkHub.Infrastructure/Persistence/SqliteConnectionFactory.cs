using Microsoft.Data.Sqlite;

namespace WorkHub.Infrastructure.Persistence;

/// <summary>
/// The only place SQLite connection strings are built. Every connection has foreign keys enforced.
/// Open connections per unit of work and dispose them promptly; pooling keeps reopen cost low.
/// </summary>
public sealed class SqliteConnectionFactory(string databaseFile)
{
    public string DatabaseFile { get; } = databaseFile;

    /// <summary>Opens the existing database. Never creates a file.</summary>
    public SqliteConnection OpenExisting() => Open(SqliteOpenMode.ReadWrite);

    /// <summary>Releases pooled connections for this database file only (other databases' pools are untouched).</summary>
    public void ClearPools()
    {
        foreach (var mode in new[] { SqliteOpenMode.ReadWrite, SqliteOpenMode.ReadWriteCreate })
        {
            using var connection = new SqliteConnection(ConnectionString(mode, pooling: true));
            SqliteConnection.ClearPool(connection);
        }
    }

    internal SqliteConnection Open(SqliteOpenMode mode, bool pooling = true)
    {
        var connection = new SqliteConnection(ConnectionString(mode, pooling));
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private string ConnectionString(SqliteOpenMode mode, bool pooling) => new SqliteConnectionStringBuilder
    {
        DataSource = DatabaseFile,
        Mode = mode,
        ForeignKeys = true,
        Pooling = pooling,
    }.ToString();
}
