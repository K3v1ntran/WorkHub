using Microsoft.Data.Sqlite;

namespace WorkHub.Infrastructure.Persistence;

/// <summary>
/// The only place SQLite connection strings are built. Every connection has foreign keys enforced.
/// Open connections per unit of work and dispose them promptly; pooling keeps reopen cost low.
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly int? _defaultTimeoutSeconds;

    public SqliteConnectionFactory(string databaseFile)
        : this(databaseFile, defaultTimeoutSeconds: null)
    {
    }

    /// <param name="defaultTimeoutSeconds">
    /// Tests only: how long a command waits on a locked database before failing. Null keeps Microsoft.Data.Sqlite's default,
    /// which is what the app uses.
    /// </param>
    internal SqliteConnectionFactory(string databaseFile, int? defaultTimeoutSeconds)
        : this(databaseFile, defaultTimeoutSeconds, opened: null)
    {
    }

    /// <param name="opened">
    /// Tests only (Phase 8 D14): called with every connection right after it opens, for example to register a blocking
    /// function and a TEMP trigger that pause a store write inside its transaction. Connections of such a factory are never
    /// pooled, so each one gets exactly the current registration. Null (the app) disables the hook.
    /// </param>
    internal SqliteConnectionFactory(string databaseFile, int? defaultTimeoutSeconds, Action<SqliteConnection>? opened)
    {
        DatabaseFile = databaseFile;
        _defaultTimeoutSeconds = defaultTimeoutSeconds;
        _opened = opened;
    }

    private readonly Action<SqliteConnection>? _opened;

    public string DatabaseFile { get; }

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
        var connection = new SqliteConnection(ConnectionString(mode, pooling && _opened is null));
        try
        {
            connection.Open();
            _opened?.Invoke(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private string ConnectionString(SqliteOpenMode mode, bool pooling)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabaseFile,
            Mode = mode,
            ForeignKeys = true,
            Pooling = pooling,
        };
        if (_defaultTimeoutSeconds is { } seconds)
            builder.DefaultTimeout = seconds;
        return builder.ToString();
    }
}
