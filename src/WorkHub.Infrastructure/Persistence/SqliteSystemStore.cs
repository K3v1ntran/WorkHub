using System.Globalization;
using Microsoft.Data.Sqlite;
using WorkHub.Core.Repositories;

namespace WorkHub.Infrastructure.Persistence;

/// <summary>
/// SQLite implementation of <see cref="ISystemStore"/>. Each call opens a pooled connection on a worker thread,
/// does one bounded unit of work and disposes it. Multi-row writes run in a single transaction.
/// </summary>
public sealed class SqliteSystemStore(SqliteConnectionFactory connections, TimeProvider? time = null) : ISystemStore
{
    private const int SqliteConstraintUnique = 2067;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public Task<SystemQueryResult> QueryAsync(SystemQuery query, CancellationToken cancellationToken = default) =>
        Task.Run(() => Query(query, cancellationToken), cancellationToken);

    public Task<SystemDetails?> GetAsync(long id, CancellationToken cancellationToken = default) =>
        Task.Run(() => Get(id), cancellationToken);

    public Task<long> CreateAsync(SystemDraft draft, CancellationToken cancellationToken = default) =>
        Task.Run(() => Create(draft), cancellationToken);

    public Task<bool> UpdateAsync(long id, SystemDraft draft, CancellationToken cancellationToken = default) =>
        Task.Run(() => Update(id, draft), cancellationToken);

    public Task<bool> SetActiveAsync(long id, bool isActive, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var connection = connections.OpenExisting();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE Systems SET IsActive = $active, UpdatedAt = $now WHERE Id = $id;";
            command.Parameters.AddWithValue("$active", isActive ? 1 : 0);
            command.Parameters.AddWithValue("$now", Now());
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() == 1;
        }, cancellationToken);

    public Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var connection = connections.OpenExisting();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Systems WHERE Id = $id;"; // SystemRepositories rows cascade (FK)
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() == 1;
        }, cancellationToken);

    public Task<RepositoryUsage> GetUsageAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var connection = connections.OpenExisting();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT sr.RepositoryType, max(sr.CustomTarget IS NULL)
                FROM SystemRepositories AS sr
                JOIN Systems AS s ON s.Id = sr.SystemId
                WHERE sr.IsEnabled = 1 AND s.IsActive = 1
                GROUP BY sr.RepositoryType;
                """;
            var inUse = new HashSet<RepositoryType>();
            var generated = new HashSet<RepositoryType>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var type = ParseType(reader.GetString(0));
                inUse.Add(type);
                if (reader.GetInt64(1) == 1)
                    generated.Add(type);
            }

            return new RepositoryUsage(inUse, generated);
        }, cancellationToken);

    private SystemQueryResult Query(SystemQuery query, CancellationToken cancellationToken)
    {
        using var connection = connections.OpenExisting();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.Id, s.SystemCode, s.SystemName, s.IsActive, sr.CustomTarget
            FROM SystemRepositories AS sr
            JOIN Systems AS s ON s.Id = sr.SystemId
            WHERE sr.RepositoryType = $type
              AND sr.IsEnabled = 1
              AND ($includeInactive = 1 OR s.IsActive = 1)
              AND ($pattern IS NULL
                   OR s.SystemCode LIKE $pattern ESCAPE '\'
                   OR s.SystemName LIKE $pattern ESCAPE '\')
            ORDER BY s.IsActive DESC, s.SystemCode
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$type", RepositoryCatalog.Get(query.Type).StorageCode);
        command.Parameters.AddWithValue("$includeInactive", query.IncludeInactive ? 1 : 0);
        command.Parameters.AddWithValue("$pattern", query.Search is null ? DBNull.Value : $"%{EscapeLike(query.Search)}%");
        command.Parameters.AddWithValue("$limit", query.Limit + 1); // one extra row tells us whether more exist
        command.Parameters.AddWithValue("$offset", query.Offset);

        var items = new List<SystemListItem>(Math.Min(query.Limit, 256));
        var hasMore = false;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (items.Count == query.Limit)
            {
                hasMore = true;
                break;
            }

            if ((items.Count & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            items.Add(new SystemListItem(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3) == 1,
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return new SystemQueryResult(items, hasMore);
    }

    private SystemDetails? Get(long id)
    {
        using var connection = connections.OpenExisting();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT SystemCode, SystemName, Description, IsActive, CreatedAt, UpdatedAt FROM Systems WHERE Id = $id;
            SELECT RepositoryType, IsEnabled, CustomTarget FROM SystemRepositories WHERE SystemId = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var code = reader.GetString(0);
        var name = reader.GetString(1);
        var description = reader.IsDBNull(2) ? null : reader.GetString(2);
        var isActive = reader.GetInt64(3) == 1;
        var createdAt = ParseTime(reader.GetString(4));
        var updatedAt = ParseTime(reader.GetString(5));

        reader.NextResult();
        var memberships = new List<MembershipDetails>();
        while (reader.Read())
        {
            memberships.Add(new MembershipDetails(
                ParseType(reader.GetString(0)),
                reader.GetInt64(1) == 1,
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        memberships.Sort((a, b) => a.Type.CompareTo(b.Type));
        return new SystemDetails(id, code, name, description, isActive, memberships, createdAt, updatedAt);
    }

    private long Create(SystemDraft draft)
    {
        using var connection = connections.OpenExisting();
        using var transaction = connection.BeginTransaction();
        var now = Now();

        long id;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO Systems (SystemCode, SystemName, Description, IsActive, CreatedAt, UpdatedAt)
                VALUES ($code, $name, $description, $active, $now, $now)
                RETURNING Id;
                """;
            AddSystemParameters(command, draft, now);
            id = ExecuteGuardingCode(() => (long)command.ExecuteScalar()!, draft.SystemCode);
        }

        InsertMemberships(connection, transaction, id, draft.Memberships);
        transaction.Commit();
        return id;
    }

    private bool Update(long id, SystemDraft draft)
    {
        using var connection = connections.OpenExisting();
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE Systems
                SET SystemCode = $code, SystemName = $name, Description = $description, IsActive = $active, UpdatedAt = $now
                WHERE Id = $id;
                """;
            AddSystemParameters(command, draft, Now());
            command.Parameters.AddWithValue("$id", id);
            if (ExecuteGuardingCode(command.ExecuteNonQuery, draft.SystemCode) == 0)
                return false; // disposing the transaction rolls it back
        }

        // At most five rows per System: replacing them is simpler and still atomic.
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM SystemRepositories WHERE SystemId = $id;";
            delete.Parameters.AddWithValue("$id", id);
            delete.ExecuteNonQuery();
        }

        InsertMemberships(connection, transaction, id, draft.Memberships);
        transaction.Commit();
        return true;
    }

    private static void InsertMemberships(SqliteConnection connection, SqliteTransaction transaction, long systemId, IEnumerable<MembershipDraft> memberships)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO SystemRepositories (SystemId, RepositoryType, IsEnabled, CustomTarget) VALUES ($id, $type, $enabled, $target);";
        var type = command.Parameters.Add("$type", SqliteType.Text);
        var enabled = command.Parameters.Add("$enabled", SqliteType.Integer);
        var target = command.Parameters.Add("$target", SqliteType.Text);
        command.Parameters.AddWithValue("$id", systemId);

        foreach (var membership in memberships)
        {
            type.Value = RepositoryCatalog.Get(membership.Type).StorageCode;
            enabled.Value = membership.IsEnabled ? 1 : 0;
            target.Value = (object?)membership.CustomTarget ?? DBNull.Value;
            command.ExecuteNonQuery();
        }
    }

    private static void AddSystemParameters(SqliteCommand command, SystemDraft draft, string now)
    {
        command.Parameters.AddWithValue("$code", draft.SystemCode);
        command.Parameters.AddWithValue("$name", draft.SystemName);
        command.Parameters.AddWithValue("$description", (object?)draft.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$active", draft.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$now", now);
    }

    private static T ExecuteGuardingCode<T>(Func<T> execute, string code)
    {
        try
        {
            return execute();
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintUnique)
        {
            throw new DuplicateSystemCodeException(code, ex);
        }
    }

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal)
             .Replace("%", @"\%", StringComparison.Ordinal)
             .Replace("_", @"\_", StringComparison.Ordinal);

    private static RepositoryType ParseType(string code) =>
        RepositoryCatalog.TryFromStorageCode(code, out var type)
            ? type
            : throw new InvalidDataException($"Unknown repository type code '{code}' in database.");

    private string Now() => _time.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
