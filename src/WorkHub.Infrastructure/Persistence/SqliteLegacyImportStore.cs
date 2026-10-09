using System.Globalization;
using Microsoft.Data.Sqlite;
using WorkHub.Core.LegacyImport;
using WorkHub.Core.Repositories;

namespace WorkHub.Infrastructure.Persistence;

/// <summary>
/// Writes legacy master data into the existing <c>Systems</c> / <c>SystemRepositories</c> tables (no schema change).
/// The whole import is one IMMEDIATE transaction: snapshot, plan, insert, commit — or roll back everything.
/// Existing rows are never updated: no name overwrite, no re-enabling, no CustomTarget change.
/// </summary>
public sealed class SqliteLegacyImportStore(SqliteConnectionFactory connections, TimeProvider? time = null) : ILegacyImportStore
{
    private const int LookupChunk = 500;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public Task<IReadOnlyDictionary<string, ExistingSystemSnapshot>> LoadSnapshotAsync(IReadOnlyList<string> systemCodes, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyDictionary<string, ExistingSystemSnapshot>>(() =>
        {
            using var connection = connections.OpenExisting();
            return LoadSnapshot(connection, null, systemCodes);
        }, cancellationToken);

    public Task<LegacyImportPlan> ApplyAsync(IReadOnlyList<LegacyEntry> entries, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var connection = connections.OpenExisting();
            using var transaction = connection.BeginTransaction(deferred: false); // BEGIN IMMEDIATE: state cannot change under the plan
            var plan = Apply(connection, transaction, entries, _time.GetUtcNow(), cancellationToken);
            transaction.Commit();
            return plan;
        }, cancellationToken);

    /// <summary>
    /// Re-reads the current state, plans with <see cref="LegacyImportPlanner"/> and inserts new Systems and missing
    /// memberships inside the CALLER's transaction (the caller commits or rolls back). Existing rows are never updated.
    /// Shared by the manual import and the initial data of a new database.
    /// </summary>
    internal static LegacyImportPlan Apply(
        SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<LegacyEntry> entries, DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        var existing = LoadSnapshot(connection, transaction, LegacyImportPlanner.CodesToLookUp(entries));
        var plan = LegacyImportPlanner.Plan(entries, code => existing.GetValueOrDefault(code));
        var now = utcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        using var insertSystem = connection.CreateCommand();
        insertSystem.Transaction = transaction;
        insertSystem.CommandText = """
            INSERT INTO Systems (SystemCode, SystemName, Description, IsActive, CreatedAt, UpdatedAt)
            VALUES ($code, $name, NULL, 1, $now, $now)
            RETURNING Id;
            """;
        var code = insertSystem.Parameters.Add("$code", SqliteType.Text);
        var name = insertSystem.Parameters.Add("$name", SqliteType.Text);
        insertSystem.Parameters.AddWithValue("$now", now);

        using var insertMembership = connection.CreateCommand();
        insertMembership.Transaction = transaction;
        insertMembership.CommandText = "INSERT INTO SystemRepositories (SystemId, RepositoryType, IsEnabled, CustomTarget) VALUES ($id, $type, 1, NULL);";
        var systemId = insertMembership.Parameters.Add("$id", SqliteType.Integer);
        var type = insertMembership.Parameters.Add("$type", SqliteType.Text);

        foreach (var system in plan.Systems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long id;
            if (system.ExistingId is { } existingId)
            {
                id = existingId;
            }
            else
            {
                code.Value = system.SystemCode;
                name.Value = system.SystemName;
                id = (long)insertSystem.ExecuteScalar()!;
            }

            foreach (var membership in system.MembershipsToAdd)
            {
                systemId.Value = id;
                type.Value = RepositoryCatalog.Get(membership).StorageCode;
                insertMembership.ExecuteNonQuery();
            }
        }

        return plan;
    }

    private static Dictionary<string, ExistingSystemSnapshot> LoadSnapshot(SqliteConnection connection, SqliteTransaction? transaction, IReadOnlyList<string> codes)
    {
        var result = new Dictionary<string, ExistingSystemSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in codes.Chunk(LookupChunk))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var names = chunk.Select((_, i) => $"$c{i}").ToArray();
            // SystemCode is COLLATE NOCASE, so IN(...) matches case-insensitively, like the UNIQUE constraint.
            command.CommandText = $"""
                SELECT s.Id, s.SystemCode, s.SystemName, sr.RepositoryType, sr.IsEnabled
                FROM Systems AS s
                LEFT JOIN SystemRepositories AS sr ON sr.SystemId = s.Id
                WHERE s.SystemCode IN ({string.Join(", ", names)});
                """;
            for (var i = 0; i < chunk.Length; i++)
                command.Parameters.AddWithValue(names[i], chunk[i]);

            var rows = new Dictionary<long, (string Code, string Name, Dictionary<RepositoryType, bool> Memberships)>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                if (!rows.TryGetValue(id, out var row))
                    rows[id] = row = (reader.GetString(1), reader.GetString(2), new Dictionary<RepositoryType, bool>());
                if (!reader.IsDBNull(3) && RepositoryCatalog.TryFromStorageCode(reader.GetString(3), out var repositoryType))
                    row.Memberships[repositoryType] = reader.GetInt64(4) == 1;
            }

            foreach (var (id, row) in rows)
                result[row.Code] = new ExistingSystemSnapshot(id, row.Code, row.Name, row.Memberships);
        }

        return result;
    }
}
