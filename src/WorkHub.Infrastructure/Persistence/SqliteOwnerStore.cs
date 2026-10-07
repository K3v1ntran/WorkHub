using System.Globalization;
using Microsoft.Data.Sqlite;
using WorkHub.Core.Owner;

namespace WorkHub.Infrastructure.Persistence;

/// <summary>
/// SQLite implementation of <see cref="IOwnerStore"/>. Same conventions as <see cref="SqliteSystemStore"/>:
/// one pooled connection per call on a worker thread, explicit bounded queries, transactions for multi-row writes.
/// </summary>
public sealed class SqliteOwnerStore(SqliteConnectionFactory connections, TimeProvider? time = null) : IOwnerStore
{
    /// <summary>Upper bound for one group's list; Owner groups are expected to be small.</summary>
    public const int MaxItemsPerQuery = 5000;

    private const int SqliteConstraintUnique = 2067;
    private const int SqliteConstraintForeignKey = 787;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public Task<IReadOnlyList<OwnerGroup>> ListGroupsAsync(CancellationToken cancellationToken = default) =>
        Run<IReadOnlyList<OwnerGroup>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id, Name, SortOrder FROM OwnerGroups ORDER BY SortOrder, Id;";
            using var reader = command.ExecuteReader();
            var groups = new List<OwnerGroup>();
            while (reader.Read())
                groups.Add(new OwnerGroup(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2)));
            return groups;
        }, cancellationToken);

    public Task<OwnerGroup> CreateGroupAsync(string name, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var sortOrder = NextSortOrder(connection, transaction, "SELECT coalesce(max(SortOrder), -1) + 1 FROM OwnerGroups;", null);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO OwnerGroups (Name, NameKey, SortOrder, CreatedAt, UpdatedAt)
                VALUES ($name, $key, $sort, $now, $now)
                RETURNING Id;
                """;
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$key", OwnerGroupName.Key(name));
            command.Parameters.AddWithValue("$sort", sortOrder);
            command.Parameters.AddWithValue("$now", Now());
            var id = GuardDuplicateName(() => (long)command.ExecuteScalar()!, name);
            transaction.Commit();
            return new OwnerGroup(id, name, sortOrder);
        }, cancellationToken);

    public Task<bool> RenameGroupAsync(long groupId, string name, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE OwnerGroups SET Name = $name, NameKey = $key, UpdatedAt = $now WHERE Id = $id;";
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$key", OwnerGroupName.Key(name));
            command.Parameters.AddWithValue("$now", Now());
            command.Parameters.AddWithValue("$id", groupId);
            return GuardDuplicateName(command.ExecuteNonQuery, name) == 1;
        }, cancellationToken);

    public Task<bool> DeleteGroupAsync(long groupId, CancellationToken cancellationToken = default) =>
        Run(connection => Execute(connection, null, "DELETE FROM OwnerGroups WHERE Id = $id;", ("$id", groupId)) == 1, cancellationToken);

    public Task<int> CountItemsAsync(long groupId, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM OwnerItems WHERE GroupId = $id;";
            command.Parameters.AddWithValue("$id", groupId);
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }, cancellationToken);

    public Task<bool> MoveGroupAsync(long groupId, int offset, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var ids = ReadIds(connection, transaction, "SELECT Id FROM OwnerGroups ORDER BY SortOrder, Id;", null);
            if (!Reorder(ids, groupId, offset))
                return false;

            Renumber(connection, transaction, "UPDATE OwnerGroups SET SortOrder = $sort WHERE Id = $id;", ids);
            transaction.Commit();
            return true;
        }, cancellationToken);

    public Task<IReadOnlyList<OwnerItemListItem>> ListItemsAsync(long groupId, CancellationToken cancellationToken = default) =>
        Run<IReadOnlyList<OwnerItemListItem>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, GroupId, Name, ActionType, Target
                FROM OwnerItems
                WHERE GroupId = $id
                ORDER BY SortOrder, Id
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$id", groupId);
            command.Parameters.AddWithValue("$limit", MaxItemsPerQuery);
            using var reader = command.ExecuteReader();
            var items = new List<OwnerItemListItem>();
            while (reader.Read())
            {
                items.Add(new OwnerItemListItem(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                    ParseType(reader.GetString(3)), reader.GetString(4)));
            }

            return items;
        }, cancellationToken);

    public Task<OwnerItemDetails?> GetItemAsync(long itemId, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, GroupId, Name, ActionType, Target, Arguments, WorkingDirectory, Username, CredentialId,
                       Note, SortOrder, IsFavorite, CreatedAt, UpdatedAt
                FROM OwnerItems WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$id", itemId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;

            return new OwnerItemDetails(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), ParseType(reader.GetString(3)), reader.GetString(4),
                NullableString(reader, 5), NullableString(reader, 6), NullableString(reader, 7), NullableString(reader, 8),
                NullableString(reader, 9), reader.GetInt32(10), reader.GetInt64(11) == 1,
                ParseTime(reader.GetString(12)), ParseTime(reader.GetString(13)));
        }, cancellationToken);

    public Task<long> CreateItemAsync(long groupId, OwnerItemDraft draft, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var sortOrder = NextSortOrder(connection, transaction, "SELECT coalesce(max(SortOrder), -1) + 1 FROM OwnerItems WHERE GroupId = $group;", groupId);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // CredentialId and IsFavorite are intentionally not parameters: new items get NULL / 0.
            command.CommandText = """
                INSERT INTO OwnerItems (GroupId, Name, ActionType, Target, Arguments, WorkingDirectory, Username, Note,
                                        SortOrder, CreatedAt, UpdatedAt)
                VALUES ($group, $name, $type, $target, $arguments, $workingDirectory, $username, $note, $sort, $now, $now)
                RETURNING Id;
                """;
            command.Parameters.AddWithValue("$group", groupId);
            AddDraftParameters(command, draft);
            command.Parameters.AddWithValue("$sort", sortOrder);
            command.Parameters.AddWithValue("$now", Now());
            var id = GuardMissingGroup(() => (long)command.ExecuteScalar()!, groupId);
            transaction.Commit();
            return id;
        }, cancellationToken);

    public Task<bool> UpdateItemAsync(long itemId, OwnerItemDraft draft, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE OwnerItems
                SET Name = $name, ActionType = $type, Target = $target, Arguments = $arguments,
                    WorkingDirectory = $workingDirectory, Username = $username, Note = $note, UpdatedAt = $now
                WHERE Id = $id;
                """;
            AddDraftParameters(command, draft);
            command.Parameters.AddWithValue("$now", Now());
            command.Parameters.AddWithValue("$id", itemId);
            return command.ExecuteNonQuery() == 1;
        }, cancellationToken);

    public Task<bool> DeleteItemAsync(long itemId, CancellationToken cancellationToken = default) =>
        Run(connection => Execute(connection, null, "DELETE FROM OwnerItems WHERE Id = $id;", ("$id", itemId)) == 1, cancellationToken);

    public Task<bool> MoveItemToGroupAsync(long itemId, long targetGroupId, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var currentGroup = Scalar(connection, transaction, "SELECT GroupId FROM OwnerItems WHERE Id = $id;", ("$id", itemId));
            if (currentGroup is null || (long)currentGroup == targetGroupId)
                return false;

            var sortOrder = NextSortOrder(connection, transaction, "SELECT coalesce(max(SortOrder), -1) + 1 FROM OwnerItems WHERE GroupId = $group;", targetGroupId);
            var changed = GuardMissingGroup(() => Execute(connection, transaction,
                "UPDATE OwnerItems SET GroupId = $group, SortOrder = $sort, UpdatedAt = $now WHERE Id = $id;",
                ("$group", targetGroupId), ("$sort", sortOrder), ("$now", Now()), ("$id", itemId)), targetGroupId);
            transaction.Commit();
            return changed == 1;
        }, cancellationToken);

    public Task<bool> MoveItemAsync(long itemId, int offset, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var groupId = Scalar(connection, transaction, "SELECT GroupId FROM OwnerItems WHERE Id = $id;", ("$id", itemId));
            if (groupId is null)
                return false;

            var ids = ReadIds(connection, transaction, "SELECT Id FROM OwnerItems WHERE GroupId = $group ORDER BY SortOrder, Id;", (long)groupId);
            if (!Reorder(ids, itemId, offset))
                return false;

            Renumber(connection, transaction, "UPDATE OwnerItems SET SortOrder = $sort WHERE Id = $id;", ids);
            transaction.Commit();
            return true;
        }, cancellationToken);

    private Task<T> Run<T>(Func<SqliteConnection, T> work, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            using var connection = connections.OpenExisting();
            return work(connection);
        }, cancellationToken);

    private static void AddDraftParameters(SqliteCommand command, OwnerItemDraft draft)
    {
        command.Parameters.AddWithValue("$name", draft.Name);
        command.Parameters.AddWithValue("$type", draft.ActionType.ToString());
        command.Parameters.AddWithValue("$target", draft.Target);
        command.Parameters.AddWithValue("$arguments", (object?)draft.Arguments ?? DBNull.Value);
        command.Parameters.AddWithValue("$workingDirectory", (object?)draft.WorkingDirectory ?? DBNull.Value);
        command.Parameters.AddWithValue("$username", (object?)draft.Username ?? DBNull.Value);
        command.Parameters.AddWithValue("$note", (object?)draft.Note ?? DBNull.Value);
    }

    /// <summary>Moves <paramref name="id"/> by <paramref name="offset"/> inside <paramref name="ids"/>. False when nothing changes.</summary>
    private static bool Reorder(List<long> ids, long id, int offset)
    {
        var from = ids.IndexOf(id);
        if (from < 0)
            return false;

        var to = Math.Clamp(from + offset, 0, ids.Count - 1);
        if (to == from)
            return false;

        ids.RemoveAt(from);
        ids.Insert(to, id);
        return true;
    }

    /// <summary>Writes dense 0..n-1 SortOrder values for the given order.</summary>
    private static void Renumber(SqliteConnection connection, SqliteTransaction transaction, string sql, List<long> ids)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var sort = command.Parameters.Add("$sort", SqliteType.Integer);
        var id = command.Parameters.Add("$id", SqliteType.Integer);
        for (var i = 0; i < ids.Count; i++)
        {
            sort.Value = i;
            id.Value = ids[i];
            command.ExecuteNonQuery();
        }
    }

    private static List<long> ReadIds(SqliteConnection connection, SqliteTransaction transaction, string sql, long? group)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (group is not null)
            command.Parameters.AddWithValue("$group", group.Value);
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read())
            ids.Add(reader.GetInt64(0));
        return ids;
    }

    private static int NextSortOrder(SqliteConnection connection, SqliteTransaction transaction, string sql, long? group) =>
        Convert.ToInt32(group is null
            ? Scalar(connection, transaction, sql)
            : Scalar(connection, transaction, sql, ("$group", group.Value)), CultureInfo.InvariantCulture);

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        var result = command.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command.ExecuteNonQuery();
    }

    private static T GuardDuplicateName<T>(Func<T> execute, string name)
    {
        try
        {
            return execute();
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintUnique)
        {
            throw new DuplicateOwnerGroupNameException(name, ex);
        }
    }

    private static T GuardMissingGroup<T>(Func<T> execute, long groupId)
    {
        try
        {
            return execute();
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintForeignKey)
        {
            throw new OwnerGroupNotFoundException(groupId);
        }
    }

    private static OwnerActionType ParseType(string value) =>
        Enum.TryParse<OwnerActionType>(value, ignoreCase: false, out var type) && Enum.IsDefined(type)
            ? type
            : throw new InvalidDataException($"Unknown Owner action type '{value}' in database.");

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private string Now() => _time.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
