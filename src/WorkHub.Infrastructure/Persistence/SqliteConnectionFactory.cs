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

    internal SqliteConnection Open(SqliteOpenMode mode, bool pooling = true)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabaseFile,
            Mode = mode,
            ForeignKeys = true,
            Pooling = pooling,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
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
}
