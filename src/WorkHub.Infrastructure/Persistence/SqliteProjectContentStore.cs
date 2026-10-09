using System.Globalization;
using Microsoft.Data.Sqlite;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;

namespace WorkHub.Infrastructure.Persistence;

/// <summary>
/// SQLite implementation of <see cref="IProjectContentStore"/> (schema v4). Same conventions as
/// <see cref="SqliteWorkspaceStore"/>: one pooled connection per call on a worker thread, parameterized SQL, and
/// <c>BEGIN IMMEDIATE</c> for writes whose validity depends on other rows, so the checked state is the written state.
/// The schema's composite foreign keys, unique indexes and triggers back every rule up.
/// </summary>
public sealed class SqliteProjectContentStore : IProjectContentStore
{
    /// <summary>Bound for one container listing and for the Move destination list.</summary>
    public const int MaxRowsPerQuery = 10000;

    /// <summary>Default bound for ancestry walks, so corrupt data can never make a recursive query run away.</summary>
    public const int MaxDepth = 10000;

    private const int SqliteConstraintUnique = 2067;
    private const int SqliteConstraintForeignKey = 787;

    private readonly SqliteConnectionFactory _connections;
    private readonly TimeProvider _time;
    private readonly int _maxDepth;

    public SqliteProjectContentStore(SqliteConnectionFactory connections, TimeProvider? time = null)
        : this(connections, time, MaxDepth)
    {
    }

    /// <param name="maxDepth">Ancestry walk bound (tests lower it to prove the walk fails closed).</param>
    internal SqliteProjectContentStore(SqliteConnectionFactory connections, TimeProvider? time, int maxDepth)
    {
        _connections = connections;
        _time = time ?? TimeProvider.System;
        _maxDepth = maxDepth;
    }

    public Task<ProjectContentListResult> ListAsync(long projectNodeId, long? folderId, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            // One read transaction: the container check, breadcrumb and rows come from the same snapshot.
            using var transaction = connection.BeginTransaction(deferred: true);
            if (ProjectProblem(connection, transaction, projectNodeId) is { } projectProblem)
                return ProjectContentListResult.Failed(projectProblem);
            if (FolderProblem(connection, transaction, projectNodeId, folderId) is { } folderProblem)
                return ProjectContentListResult.Failed(folderProblem);

            var breadcrumb = folderId is { } id ? ReadBreadcrumb(connection, transaction, id) : [];

            var folders = new List<ProjectFolder>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"""
                    SELECT {FolderColumns} FROM ProjectFolders
                    WHERE ProjectNodeId = $project AND ParentFolderId IS $folder
                    LIMIT $limit;
                    """;
                AddContainer(command, projectNodeId, folderId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    folders.Add(ReadFolder(reader));
            }

            var links = new List<ProjectLink>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"""
                    SELECT {LinkColumns} FROM ProjectLinks
                    WHERE ProjectNodeId = $project AND FolderId IS $folder
                    LIMIT $limit;
                    """;
                AddContainer(command, projectNodeId, folderId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    links.Add(ReadLink(reader));
            }

            transaction.Commit();
            return ProjectContentListResult.Ok(new ProjectContentListing(projectNodeId, folderId, breadcrumb, folders, links));
        }, cancellationToken);

    public Task<ProjectFolder> CreateFolderAsync(long projectNodeId, long? parentFolderId, string name, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            RequireContainer(connection, transaction, projectNodeId, parentFolderId);

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                INSERT INTO ProjectFolders (ProjectNodeId, ParentFolderId, Name, NameKey, CreatedAt, UpdatedAt)
                VALUES ($project, $parent, $name, $key, $now, $now)
                RETURNING {FolderColumns};
                """;
            command.Parameters.AddWithValue("$project", projectNodeId);
            command.Parameters.AddWithValue("$parent", (object?)parentFolderId ?? DBNull.Value);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$key", WorkspaceName.Key(name));
            command.Parameters.AddWithValue("$now", Now());
            var folder = GuardWrite(() => ReadSingle(command, ReadFolder)!, name);
            transaction.Commit();
            return folder;
        }, cancellationToken);

    public Task<ProjectFolder?> RenameFolderAsync(long folderId, string name, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                UPDATE ProjectFolders SET Name = $name, NameKey = $key, UpdatedAt = $now WHERE Id = $id
                RETURNING {FolderColumns};
                """;
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$key", WorkspaceName.Key(name));
            command.Parameters.AddWithValue("$now", Now());
            command.Parameters.AddWithValue("$id", folderId);
            return GuardWrite(() => ReadSingle(command, ReadFolder), name);
        }, cancellationToken);

    public Task<ProjectLink> CreateLinkAsync(long projectNodeId, long? folderId, ProjectLinkDraft draft, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            RequireContainer(connection, transaction, projectNodeId, folderId);

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                INSERT INTO ProjectLinks (ProjectNodeId, FolderId, LinkType, Name, Target, CreatedAt, UpdatedAt)
                VALUES ($project, $folder, $type, $name, $target, $now, $now)
                RETURNING {LinkColumns};
                """;
            command.Parameters.AddWithValue("$project", projectNodeId);
            command.Parameters.AddWithValue("$folder", (object?)folderId ?? DBNull.Value);
            AddLinkParameters(command, draft);
            var link = GuardWrite(() => ReadSingle(command, ReadLink)!, draft.Name!);
            transaction.Commit();
            return link;
        }, cancellationToken);

    public Task<ProjectLink?> UpdateLinkAsync(long linkId, ProjectLinkDraft draft, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                UPDATE ProjectLinks SET LinkType = $type, Name = $name, Target = $target, UpdatedAt = $now WHERE Id = $id
                RETURNING {LinkColumns};
                """;
            command.Parameters.AddWithValue("$id", linkId);
            AddLinkParameters(command, draft);
            return ReadSingle(command, ReadLink);
        }, cancellationToken);

    public Task<ProjectLink?> GetLinkAsync(long linkId, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {LinkColumns} FROM ProjectLinks WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", linkId);
            return ReadSingle(command, ReadLink);
        }, cancellationToken);

    public Task<IReadOnlyList<WorkspaceFolderPath>> ListFolderMoveTargetsAsync(long projectNodeId, long? movingFolderId, CancellationToken cancellationToken = default) =>
        Run<IReadOnlyList<WorkspaceFolderPath>>(connection =>
        {
            using var command = connection.CreateCommand();
            // Walking down from the Project's root folders and never entering the moving folder excludes its subtree.
            command.CommandText = """
                WITH RECURSIVE folders (Id, Path, Depth) AS (
                    SELECT Id, Name, 0 FROM ProjectFolders
                    WHERE ProjectNodeId = $project AND ParentFolderId IS NULL AND Id IS NOT $moving
                    UNION ALL
                    SELECT c.Id, f.Path || ' / ' || c.Name, f.Depth + 1
                    FROM ProjectFolders AS c JOIN folders AS f ON c.ParentFolderId = f.Id
                    WHERE c.Id IS NOT $moving AND f.Depth < $maxDepth
                )
                SELECT Id, Path FROM folders LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$project", projectNodeId);
            command.Parameters.AddWithValue("$moving", (object?)movingFolderId ?? DBNull.Value);
            command.Parameters.AddWithValue("$maxDepth", _maxDepth);
            command.Parameters.AddWithValue("$limit", MaxRowsPerQuery);
            using var reader = command.ExecuteReader();
            var folders = new List<WorkspaceFolderPath>();
            while (reader.Read())
                folders.Add(new WorkspaceFolderPath(reader.GetInt64(0), reader.GetString(1)));
            return folders;
        }, cancellationToken);

    public Task<ContentMoveOutcome> MoveFolderAsync(long folderId, long? expectedParentFolderId, long? targetParentFolderId, CancellationToken cancellationToken = default) =>
        Move("ProjectFolders", "ParentFolderId", isFolder: true, folderId, expectedParentFolderId, targetParentFolderId, cancellationToken);

    public Task<ContentMoveOutcome> MoveLinkAsync(long linkId, long? expectedFolderId, long? targetFolderId, CancellationToken cancellationToken = default) =>
        Move("ProjectLinks", "FolderId", isFolder: false, linkId, expectedFolderId, targetFolderId, cancellationToken);

    /// <summary>
    /// Revision 3 §8.1, in ONE <c>BEGIN IMMEDIATE</c> transaction (the write lock is taken first, so the validated state
    /// is the updated state). Every non-Moved outcome writes nothing. Table and column names are constants, never input.
    /// </summary>
    private Task<ContentMoveOutcome> Move(string table, string containerColumn, bool isFolder, long id, long? expected, long? target, CancellationToken cancellationToken) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);

            // 1. Source.
            long projectNodeId;
            long? current;
            using (var source = connection.CreateCommand())
            {
                source.Transaction = transaction;
                source.CommandText = $"SELECT ProjectNodeId, {containerColumn} FROM {table} WHERE Id = $id;";
                source.Parameters.AddWithValue("$id", id);
                using var reader = source.ExecuteReader();
                if (!reader.Read())
                    return ContentMoveOutcome.NotFound;
                projectNodeId = reader.GetInt64(0);
                current = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            }

            // 2. Observed container (NULL-safe). Checked before AlreadyThere: a stale observation is always reported.
            if (current != expected)
                return ContentMoveOutcome.MovedMeanwhile;

            if (target is { } destination)
            {
                // 3. Destination exists in the same Project.
                if (FolderProblem(connection, transaction, projectNodeId, destination) is { } problem)
                    return problem == ContentContainerProblem.WrongProject ? ContentMoveOutcome.WrongProject : ContentMoveOutcome.DestinationNotFound;

                // 4. Folders only: ancestry, failing closed.
                if (isFolder && CheckAncestry(connection, transaction, id, destination) is { } refusal)
                    return refusal;
            }

            // 5. Already there: nothing written, timestamps untouched.
            if (current == target)
                return ContentMoveOutcome.AlreadyThere;

            // 6. Guarded update.
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = $"UPDATE {table} SET {containerColumn} = $target, UpdatedAt = $now WHERE Id = $id AND {containerColumn} IS $expected;";
            update.Parameters.AddWithValue("$target", (object?)target ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", Now());
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$expected", (object?)expected ?? DBNull.Value);
            int changed;
            try
            {
                changed = update.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintUnique)
            {
                return ContentMoveOutcome.Duplicate;
            }
            catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintForeignKey)
            {
                return ContentMoveOutcome.DestinationNotFound; // defense in depth: the explicit check reports this first
            }

            if (changed != 1)
                return ContentMoveOutcome.MovedMeanwhile; // cannot happen under the write lock; never treated as success

            transaction.Commit();
            return ContentMoveOutcome.Moved;
        }, cancellationToken);

    /// <summary>
    /// Walks up from the destination. Cycle when the walk reaches the moving folder. Safe ONLY when the walk reaches a
    /// root folder within the depth bound; a truncated or broken walk is <see cref="ContentMoveOutcome.AncestryUnverified"/>.
    /// </summary>
    private ContentMoveOutcome? CheckAncestry(SqliteConnection connection, SqliteTransaction transaction, long movingFolderId, long destination)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH RECURSIVE ancestors (Id, ParentFolderId, Depth) AS (
                SELECT Id, ParentFolderId, 0 FROM ProjectFolders WHERE Id = $destination
                UNION ALL
                SELECT f.Id, f.ParentFolderId, a.Depth + 1
                FROM ProjectFolders AS f JOIN ancestors AS a ON f.Id = a.ParentFolderId
                WHERE a.Depth < $maxDepth
            )
            SELECT EXISTS (SELECT 1 FROM ancestors WHERE Id = $moving),
                   EXISTS (SELECT 1 FROM ancestors WHERE ParentFolderId IS NULL);
            """;
        command.Parameters.AddWithValue("$destination", destination);
        command.Parameters.AddWithValue("$moving", movingFolderId);
        command.Parameters.AddWithValue("$maxDepth", _maxDepth);
        using var reader = command.ExecuteReader();
        reader.Read();
        if (reader.GetInt64(0) == 1)
            return ContentMoveOutcome.Cycle;
        return reader.GetInt64(1) == 1 ? null : ContentMoveOutcome.AncestryUnverified;
    }

    private Task<T> Run<T>(Func<SqliteConnection, T> work, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            using var connection = _connections.OpenExisting();
            return work(connection);
        }, cancellationToken);

    /// <summary>Checks inside the caller's write transaction that the Project and the (optional) folder exist and match.</summary>
    private static void RequireContainer(SqliteConnection connection, SqliteTransaction transaction, long projectNodeId, long? folderId)
    {
        if ((ProjectProblem(connection, transaction, projectNodeId) ?? FolderProblem(connection, transaction, projectNodeId, folderId)) is { } problem)
            throw new ContentContainerException(problem);
    }

    private static ContentContainerProblem? ProjectProblem(SqliteConnection connection, SqliteTransaction transaction, long projectNodeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM Projects WHERE NodeId = $project);";
        command.Parameters.AddWithValue("$project", projectNodeId);
        return (long)command.ExecuteScalar()! == 1 ? null : ContentContainerProblem.ProjectNotFound;
    }

    private static ContentContainerProblem? FolderProblem(SqliteConnection connection, SqliteTransaction transaction, long projectNodeId, long? folderId)
    {
        if (folderId is not { } id)
            return null;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT ProjectNodeId FROM ProjectFolders WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() switch
        {
            null => ContentContainerProblem.FolderNotFound,
            long owner when owner != projectNodeId => ContentContainerProblem.WrongProject,
            _ => null,
        };
    }

    private IReadOnlyList<ProjectFolderCrumb> ReadBreadcrumb(SqliteConnection connection, SqliteTransaction transaction, long folderId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH RECURSIVE chain (Id, ParentFolderId, Name, Depth) AS (
                SELECT Id, ParentFolderId, Name, 0 FROM ProjectFolders WHERE Id = $id
                UNION ALL
                SELECT f.Id, f.ParentFolderId, f.Name, c.Depth + 1
                FROM ProjectFolders AS f JOIN chain AS c ON f.Id = c.ParentFolderId
                WHERE c.Depth < $maxDepth
            )
            SELECT Id, Name FROM chain ORDER BY Depth DESC;
            """;
        command.Parameters.AddWithValue("$id", folderId);
        command.Parameters.AddWithValue("$maxDepth", _maxDepth);
        using var reader = command.ExecuteReader();
        var crumbs = new List<ProjectFolderCrumb>();
        while (reader.Read())
            crumbs.Add(new ProjectFolderCrumb(reader.GetInt64(0), reader.GetString(1)));
        return crumbs;
    }

    private const string FolderColumns = "Id, ProjectNodeId, ParentFolderId, Name, CreatedAt, UpdatedAt";
    private const string LinkColumns = "Id, ProjectNodeId, FolderId, LinkType, Name, Target, CreatedAt, UpdatedAt";

    private static void AddContainer(SqliteCommand command, long projectNodeId, long? folderId)
    {
        command.Parameters.AddWithValue("$project", projectNodeId);
        command.Parameters.AddWithValue("$folder", (object?)folderId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", MaxRowsPerQuery);
    }

    private void AddLinkParameters(SqliteCommand command, ProjectLinkDraft draft)
    {
        command.Parameters.AddWithValue("$type", ProjectLinkTypes.StorageCode(draft.Type));
        command.Parameters.AddWithValue("$name", draft.Name!);
        command.Parameters.AddWithValue("$target", draft.Target!);
        command.Parameters.AddWithValue("$now", Now());
    }

    private static T? ReadSingle<T>(SqliteCommand command, Func<SqliteDataReader, T> read) where T : class
    {
        using var reader = command.ExecuteReader();
        return reader.Read() ? read(reader) : null;
    }

    private static ProjectFolder ReadFolder(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.IsDBNull(2) ? null : reader.GetInt64(2),
        reader.GetString(3),
        ParseTime(reader.GetString(4)),
        ParseTime(reader.GetString(5)));

    private static ProjectLink ReadLink(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.IsDBNull(2) ? null : reader.GetInt64(2),
        ProjectLinkTypes.FromStorageCode(reader.GetString(3)),
        reader.GetString(4),
        reader.GetString(5),
        ParseTime(reader.GetString(6)),
        ParseTime(reader.GetString(7)));

    /// <summary>Maps a sibling-name conflict and (defense in depth) a container FK violation to typed exceptions.</summary>
    private static T GuardWrite<T>(Func<T> execute, string name)
    {
        try
        {
            return execute();
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintUnique)
        {
            throw new DuplicateProjectFolderNameException(name, ex);
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintForeignKey)
        {
            throw new ContentContainerException(ContentContainerProblem.FolderNotFound, ex);
        }
    }

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private string Now() => _time.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
