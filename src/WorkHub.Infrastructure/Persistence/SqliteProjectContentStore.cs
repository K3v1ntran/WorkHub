using System.Globalization;
using Microsoft.Data.Sqlite;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;

namespace WorkHub.Infrastructure.Persistence;

/// <summary>
/// SQLite implementation of <see cref="IProjectContentStore"/> (schema v4 folders/links, v5 notes, v6 Trash). Same conventions as
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
                    WHERE ProjectNodeId = $project AND ParentFolderId IS $folder AND TrashEntryId IS NULL
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
                    WHERE ProjectNodeId = $project AND FolderId IS $folder AND TrashEntryId IS NULL
                    LIMIT $limit;
                    """;
                AddContainer(command, projectNodeId, folderId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    links.Add(ReadLink(reader));
            }

            var notes = new List<ProjectNoteSummary>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT Id, ProjectNodeId, FolderId, Title, Revision, UpdatedAt FROM ProjectNotes
                    WHERE ProjectNodeId = $project AND FolderId IS $folder AND TrashEntryId IS NULL
                    LIMIT $limit;
                    """;
                AddContainer(command, projectNodeId, folderId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    notes.Add(new ProjectNoteSummary(
                        reader.GetInt64(0),
                        reader.GetInt64(1),
                        reader.IsDBNull(2) ? null : reader.GetInt64(2),
                        reader.GetString(3),
                        reader.GetInt64(4),
                        ParseTime(reader.GetString(5))));
                }
            }

            int trashCount;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT count(*) FROM ProjectTrashEntries WHERE ProjectNodeId = $project;";
                command.Parameters.AddWithValue("$project", projectNodeId);
                trashCount = (int)(long)command.ExecuteScalar()!;
            }

            transaction.Commit();
            return ProjectContentListResult.Ok(new ProjectContentListing(projectNodeId, folderId, breadcrumb, folders, links, notes) { TrashCount = trashCount });
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
            using var transaction = connection.BeginTransaction(deferred: false);
            if (TrashedProjectOfRow(connection, transaction, "ProjectFolders", folderId) is { } trashedProject)
                throw new ProjectTrashedException(trashedProject);

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                UPDATE ProjectFolders SET Name = $name, NameKey = $key, UpdatedAt = $now WHERE Id = $id AND TrashEntryId IS NULL
                RETURNING {FolderColumns};
                """;
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$key", WorkspaceName.Key(name));
            command.Parameters.AddWithValue("$now", Now());
            command.Parameters.AddWithValue("$id", folderId);
            var folder = GuardWrite(() => ReadSingle(command, ReadFolder), name) ?? ClassifyNotUpdated<ProjectFolder>(connection, transaction, "ProjectFolders", folderId);
            transaction.Commit();
            return folder;
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
            using var transaction = connection.BeginTransaction(deferred: false);
            if (TrashedProjectOfRow(connection, transaction, "ProjectLinks", linkId) is { } trashedProject)
                throw new ProjectTrashedException(trashedProject);

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                UPDATE ProjectLinks SET LinkType = $type, Name = $name, Target = $target, UpdatedAt = $now WHERE Id = $id AND TrashEntryId IS NULL
                RETURNING {LinkColumns};
                """;
            command.Parameters.AddWithValue("$id", linkId);
            AddLinkParameters(command, draft);
            var link = ReadSingle(command, ReadLink) ?? ClassifyNotUpdated<ProjectLink>(connection, transaction, "ProjectLinks", linkId);
            transaction.Commit();
            return link;
        }, cancellationToken);

    /// <summary>
    /// Nothing was updated: null when the row no longer exists, <see cref="ContentTrashedException"/> when it is in the
    /// Trash. Classified inside the caller's write transaction. The table name is a constant, never input.
    /// </summary>
    private static T? ClassifyNotUpdated<T>(SqliteConnection connection, SqliteTransaction transaction, string table, long id) where T : class
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT TrashEntryId IS NOT NULL FROM {table} WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() switch
        {
            null => null,
            1L => throw new ContentTrashedException(),
            _ => throw new InvalidOperationException("An active row was not updated under the write lock."),
        };
    }

    public Task<ProjectLink?> GetLinkAsync(long linkId, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var command = connection.CreateCommand();
            using var transaction = connection.BeginTransaction(deferred: true); // the link and its Project state from one snapshot
            command.Transaction = transaction;
            command.CommandText = $"SELECT {LinkColumns} FROM ProjectLinks WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", linkId);
            var link = ReadSingle(command, ReadLink) is { } read
                ? read with { IsProjectTrashed = TrashedProjectOfRow(connection, transaction, "ProjectLinks", linkId) is not null }
                : null;
            transaction.Commit();
            return link;
        }, cancellationToken);

    public Task<IReadOnlyList<WorkspaceFolderPath>> ListFolderMoveTargetsAsync(long projectNodeId, long? movingFolderId, CancellationToken cancellationToken = default) =>
        Run<IReadOnlyList<WorkspaceFolderPath>>(connection =>
        {
            using var command = connection.CreateCommand();
            // Walking down from the Project's active root folders, never entering the moving folder or a trashed folder, excludes
            // the moving subtree and everything in the Trash (an active folder never has a trashed ancestor).
            command.CommandText = """
                WITH RECURSIVE folders (Id, Path, Depth) AS (
                    SELECT Id, Name, 0 FROM ProjectFolders
                    WHERE ProjectNodeId = $project AND ParentFolderId IS NULL AND Id IS NOT $moving AND TrashEntryId IS NULL
                    UNION ALL
                    SELECT c.Id, f.Path || ' / ' || c.Name, f.Depth + 1
                    FROM ProjectFolders AS c JOIN folders AS f ON c.ParentFolderId = f.Id
                    WHERE c.Id IS NOT $moving AND c.TrashEntryId IS NULL AND f.Depth < $maxDepth
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

    public Task<ProjectContainerPath?> GetContainerPathAsync(long projectNodeId, long? folderId, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            // One read transaction: the container check, Project name and breadcrumb come from the same snapshot.
            using var transaction = connection.BeginTransaction(deferred: true);
            if ((ProjectProblem(connection, transaction, projectNodeId, requireActive: false) ?? FolderProblem(connection, transaction, projectNodeId, folderId, requireActive: false)) is not null)
                return null;

            string projectName;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT Name FROM WorkspaceNodes WHERE Id = $project;";
                command.Parameters.AddWithValue("$project", projectNodeId);
                projectName = (string)command.ExecuteScalar()!;
            }

            var breadcrumb = folderId is { } id ? ReadBreadcrumb(connection, transaction, id) : [];
            transaction.Commit();
            return (ProjectContainerPath?)new ProjectContainerPath(projectName, breadcrumb);
        }, cancellationToken);

    // ---- Notes ---------------------------------------------------------------------------------------------------------

    public Task<ProjectNote?> GetNoteAsync(long noteId, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {NoteColumns} FROM ProjectNotes WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", noteId);
            return ReadSingle(command, ReadNote);
        }, cancellationToken);

    public Task<IReadOnlyList<ProjectNote>> GetNotesAsync(IReadOnlyCollection<long> noteIds, CancellationToken cancellationToken = default) =>
        Run<IReadOnlyList<ProjectNote>>(connection =>
        {
            // One read transaction: every note comes from the same snapshot.
            using var transaction = connection.BeginTransaction(deferred: true);
            var notes = new List<ProjectNote>();
            foreach (var id in noteIds.Distinct())
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"SELECT {NoteColumns} FROM ProjectNotes WHERE Id = $id;";
                command.Parameters.AddWithValue("$id", id);
                if (ReadSingle(command, ReadNote) is { } note)
                    notes.Add(note);
            }

            transaction.Commit();
            return notes;
        }, cancellationToken);

    public Task<ProjectNote> CreateNoteAsync(long projectNodeId, long? folderId, ProjectNoteDraft draft, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            RequireContainer(connection, transaction, projectNodeId, folderId);
            var note = InsertNote(connection, transaction, projectNodeId, folderId, draft);
            transaction.Commit();
            return note;
        }, cancellationToken);

    public Task<NoteStoreSaveResult> SaveNoteAsync(long noteId, long expectedRevision, ProjectNoteDraft draft, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            if (TrashedProjectOfRow(connection, transaction, "ProjectNotes", noteId) is not null)
                return NoteStoreSaveResult.ProjectTrashed; // checked first; nothing written

            // Only Title, Content, Revision and UpdatedAt: the container is never written by a save.
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = $"""
                    UPDATE ProjectNotes SET Title = $title, Content = $content, Revision = Revision + 1, UpdatedAt = $now
                    WHERE Id = $id AND Revision = $expected AND TrashEntryId IS NULL
                    RETURNING {NoteColumns};
                    """;
                update.Parameters.AddWithValue("$title", draft.Title!);
                update.Parameters.AddWithValue("$content", draft.Content ?? string.Empty);
                update.Parameters.AddWithValue("$now", Now());
                update.Parameters.AddWithValue("$id", noteId);
                update.Parameters.AddWithValue("$expected", expectedRevision);
                if (ReadSingle(update, ReadNote) is { } saved)
                {
                    transaction.Commit();
                    return NoteStoreSaveResult.Saved(saved);
                }
            }

            // Nothing updated: classify inside the same write transaction (nothing can change in between).
            using var current = connection.CreateCommand();
            current.Transaction = transaction;
            current.CommandText = "SELECT Revision, TrashEntryId IS NOT NULL FROM ProjectNotes WHERE Id = $id;";
            current.Parameters.AddWithValue("$id", noteId);
            using var reader = current.ExecuteReader();
            if (!reader.Read())
                return NoteStoreSaveResult.NotFound; // Dispose rolls back (nothing was written)
            return reader.GetInt64(1) == 1
                ? NoteStoreSaveResult.Trashed // a trashed note is read-only, whatever its revision
                : NoteStoreSaveResult.Conflict(reader.GetInt64(0));
        }, cancellationToken);

    public Task<ProjectNote> SaveNoteAsNewAsync(long originalNoteId, long projectNodeId, long? lastKnownFolderId, ProjectNoteDraft draft, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);

            long targetProject;
            long? targetFolder;
            using (var original = connection.CreateCommand())
            {
                original.Transaction = transaction;
                original.CommandText = "SELECT ProjectNodeId, FolderId FROM ProjectNotes WHERE Id = $id;";
                original.Parameters.AddWithValue("$id", originalNoteId);
                using var reader = original.ExecuteReader();
                if (reader.Read())
                {
                    // 1. The original still exists (active or in the Trash): next to it, wherever it is now.
                    targetProject = reader.GetInt64(0);
                    targetFolder = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                }
                else
                {
                    // 2. The original is gone: the editor's last-known container, only if it still exists (no root fallback).
                    targetProject = projectNodeId;
                    targetFolder = lastKnownFolderId;
                }
            }

            RequireContainer(connection, transaction, targetProject, targetFolder); // 3. only an existing ACTIVE container; otherwise refuse
            var note = InsertNote(connection, transaction, targetProject, targetFolder, draft);
            transaction.Commit();
            return note;
        }, cancellationToken);

    public Task<ContentMoveOutcome> MoveNoteAsync(long noteId, long? expectedFolderId, long? targetFolderId, CancellationToken cancellationToken = default) =>
        Move("ProjectNotes", "FolderId", isFolder: false, noteId, expectedFolderId, targetFolderId, cancellationToken);

    // ---- Trash (schema v6) -------------------------------------------------------------------------------------------

    /// <summary>
    /// Test hook: called with a step name between the writes of Trash ("trash:entry", "trash:folders", "trash:links") and
    /// Restore ("restore:folders", "restore:links", "restore:notes"); throwing from it proves the transaction rolls back.
    /// </summary>
    internal Action<string>? OnWriteStep { get; set; }

    public Task<ContentState?> GetContentStateAsync(ContentRef item, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var root = ReadRoot(connection, transaction, item);
            var state = root is null ? null : new ContentState(item, root.ProjectNodeId, root.ContainerFolderId, root.Trashed)
            {
                ProjectTrashed = ProjectProblem(connection, transaction, root.ProjectNodeId) == ContentContainerProblem.ProjectTrashed,
            };
            transaction.Commit();
            return state;
        }, cancellationToken);

    public Task<TrashCount> CountTrashSetAsync(ContentRef item, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            // One read transaction: a snapshot for the confirmation text only. Trash recomputes the set when it writes.
            using var transaction = connection.BeginTransaction(deferred: true);
            var root = ReadRoot(connection, transaction, item);
            if (root is null)
                return new TrashCount(false, false, 0);
            if (root.Trashed)
                return new TrashCount(true, true, 0);
            var set = ReadActiveSet(connection, transaction, item);
            transaction.Commit();
            return new TrashCount(true, false, set.Count);
        }, cancellationToken);

    public Task<TrashResult> TrashAsync(ContentRef item, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);

            // 1. The row of that KIND and Id, as it is now (identity-based: no container or revision precondition).
            var root = ReadRoot(connection, transaction, item);
            if (root is null)
                return TrashResult.Refused(TrashOutcome.NotFound);
            if (ProjectProblem(connection, transaction, root.ProjectNodeId) == ContentContainerProblem.ProjectTrashed)
                return TrashResult.Refused(TrashOutcome.ProjectTrashed); // checked before every content precondition
            if (root.Trashed)
                return TrashResult.Refused(TrashOutcome.AlreadyTrashed);

            // 2. The set NOW: the root plus (folder) its currently active subtree; earlier entries keep their rows.
            var set = ReadActiveSet(connection, transaction, item);
            if (set.Truncated)
                return TrashResult.Refused(TrashOutcome.AncestryUnverified);

            // 3. The entry, with the count of that set (the trigger checks the root is active and in the same Project).
            var trashedAt = Now();
            long entryId;
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO ProjectTrashEntries (ProjectNodeId, RootKind, RootId, ItemCount, TrashedAt)
                    VALUES ($project, $kind, $root, $count, $at) RETURNING Id;
                    """;
                insert.Parameters.AddWithValue("$project", root.ProjectNodeId);
                insert.Parameters.AddWithValue("$kind", item.Kind.ToString());
                insert.Parameters.AddWithValue("$root", item.Id);
                insert.Parameters.AddWithValue("$count", set.Count);
                insert.Parameters.AddWithValue("$at", trashedAt);
                entryId = (long)insert.ExecuteScalar()!;
            }

            OnWriteStep?.Invoke("trash:entry");

            // 4. Markers only: no container, name, content, Revision or UpdatedAt change (T9).
            int marked;
            if (item.Kind == ContentKind.Folder)
            {
                var ids = JsonIds(set.FolderIds);
                marked = Mark(connection, transaction, "UPDATE ProjectFolders SET TrashEntryId = $entry WHERE Id IN (SELECT value FROM json_each($ids)) AND TrashEntryId IS NULL;", entryId, ids);
                OnWriteStep?.Invoke("trash:folders");
                marked += Mark(connection, transaction, "UPDATE ProjectLinks SET TrashEntryId = $entry WHERE FolderId IN (SELECT value FROM json_each($ids)) AND TrashEntryId IS NULL;", entryId, ids);
                OnWriteStep?.Invoke("trash:links");
                marked += Mark(connection, transaction, "UPDATE ProjectNotes SET TrashEntryId = $entry WHERE FolderId IN (SELECT value FROM json_each($ids)) AND TrashEntryId IS NULL;", entryId, ids);
            }
            else
            {
                marked = Mark(connection, transaction, $"UPDATE {Table(item.Kind)} SET TrashEntryId = $entry WHERE Id = $ids AND TrashEntryId IS NULL;", entryId, item.Id);
            }

            // 5. The marked set must be exactly the counted set; anything else rolls back (Dispose).
            if (marked != set.Count)
                throw new InvalidOperationException($"Trash marked {marked} rows but counted {set.Count}; nothing was changed.");

            transaction.Commit();
            return TrashResult.Done(new TrashEntry(entryId, root.ProjectNodeId, item, root.Name, root.ContainerFolderId, set.Count, ParseTime(trashedAt)));
        }, cancellationToken);

    public Task<IReadOnlyList<TrashEntry>> ListTrashAsync(long projectNodeId, CancellationToken cancellationToken = default) =>
        Run<IReadOnlyList<TrashEntry>>(connection =>
        {
            // One read transaction: entries, root rows and container paths come from the same snapshot.
            using var transaction = connection.BeginTransaction(deferred: true);
            var entries = new List<TrashEntry>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT e.Id, e.RootKind, e.RootId, e.ItemCount, e.TrashedAt,
                           CASE e.RootKind WHEN 'Folder' THEN f.Name WHEN 'Link' THEN l.Name ELSE n.Title END,
                           CASE e.RootKind WHEN 'Folder' THEN f.ParentFolderId WHEN 'Link' THEN l.FolderId ELSE n.FolderId END
                    FROM ProjectTrashEntries AS e
                    LEFT JOIN ProjectFolders AS f ON e.RootKind = 'Folder' AND f.Id = e.RootId
                    LEFT JOIN ProjectLinks AS l ON e.RootKind = 'Link' AND l.Id = e.RootId
                    LEFT JOIN ProjectNotes AS n ON e.RootKind = 'Note' AND n.Id = e.RootId
                    WHERE e.ProjectNodeId = $project
                    ORDER BY e.TrashedAt DESC, e.Id DESC
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$project", projectNodeId);
                command.Parameters.AddWithValue("$limit", MaxRowsPerQuery);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    entries.Add(new TrashEntry(
                        reader.GetInt64(0),
                        projectNodeId,
                        new ContentRef(Enum.Parse<ContentKind>(reader.GetString(1)), reader.GetInt64(2)),
                        reader.IsDBNull(5) ? string.Empty : reader.GetString(5), // a root row removed by an external edit
                        reader.IsDBNull(6) ? null : reader.GetInt64(6),
                        (int)reader.GetInt64(3),
                        ParseTime(reader.GetString(4))));
                }
            }

            var projectName = ProjectName(connection, transaction, projectNodeId);
            for (var i = 0; i < entries.Count; i++)
            {
                var (path, trashed) = DescribeContainer(connection, transaction, projectName, entries[i].RootContainerFolderId);
                entries[i] = entries[i] with { ContainerPath = path, ContainerTrashed = trashed };
            }

            transaction.Commit();
            return entries;
        }, cancellationToken);

    public Task<RestoreResult> RestoreAsync(long entryId, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);

            // 1. The entry (already restored elsewhere → EntryNotFound), then its Project (in the Workspace Trash → ProjectTrashed).
            ContentRef item;
            long entryProject;
            using (var entry = connection.CreateCommand())
            {
                entry.Transaction = transaction;
                entry.CommandText = "SELECT RootKind, RootId, ProjectNodeId FROM ProjectTrashEntries WHERE Id = $entry;";
                entry.Parameters.AddWithValue("$entry", entryId);
                using var reader = entry.ExecuteReader();
                if (!reader.Read())
                    return RestoreResult.Refused(RestoreOutcome.EntryNotFound);
                item = new ContentRef(Enum.Parse<ContentKind>(reader.GetString(0)), reader.GetInt64(1));
                entryProject = reader.GetInt64(2);
            }

            if (ProjectProblem(connection, transaction, entryProject) == ContentContainerProblem.ProjectTrashed)
                return RestoreResult.Refused(RestoreOutcome.ProjectTrashed); // checked before every content precondition

            var root = ReadRoot(connection, transaction, item)
                       ?? throw new InvalidDataException("The root row of a Trash entry no longer exists; nothing was restored.");

            // 2. The container the root came from (never changed while trashed): by Id, wherever it is now.
            if (root.ContainerFolderId is { } containerId)
            {
                using var container = connection.CreateCommand();
                container.Transaction = transaction;
                container.CommandText = "SELECT TrashEntryId FROM ProjectFolders WHERE Id = $id;";
                container.Parameters.AddWithValue("$id", containerId);
                using var reader = container.ExecuteReader();
                if (!reader.Read())
                    return RestoreResult.Refused(RestoreOutcome.ContainerMissing); // no root fallback, no re-parenting
                if (!reader.IsDBNull(0))
                    return RestoreResult.Refused(RestoreOutcome.ContainerTrashed, BlockingRootName(connection, transaction, reader.GetInt64(0)));
            }

            // 3. Folders only: an ACTIVE sibling folder with the same name key refuses (nothing is renamed).
            if (item.Kind == ContentKind.Folder && ActiveSiblingNameExists(connection, transaction, root, item.Id))
                return RestoreResult.Refused(RestoreOutcome.NameConflict);

            // 4. Clear this entry's markers (only the marker is written), read the root back, delete the entry.
            try
            {
                Mark(connection, transaction, "UPDATE ProjectFolders SET TrashEntryId = NULL WHERE TrashEntryId = $entry;", entryId, null);
                OnWriteStep?.Invoke("restore:folders");
                Mark(connection, transaction, "UPDATE ProjectLinks SET TrashEntryId = NULL WHERE TrashEntryId = $entry;", entryId, null);
                OnWriteStep?.Invoke("restore:links");
                Mark(connection, transaction, "UPDATE ProjectNotes SET TrashEntryId = NULL WHERE TrashEntryId = $entry;", entryId, null);
                OnWriteStep?.Invoke("restore:notes");
            }
            catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintUnique)
            {
                return RestoreResult.Refused(RestoreOutcome.NameConflict); // backstop: Dispose rolls everything back
            }

            var restored = ReadRestoredRoot(connection, transaction, item, root);
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM ProjectTrashEntries WHERE Id = $entry;";
                delete.Parameters.AddWithValue("$entry", entryId);
                delete.ExecuteNonQuery(); // the RESTRICT FKs allow this only once every marker is cleared
            }

            transaction.Commit();
            return RestoreResult.Done(restored);
        }, cancellationToken);

    /// <summary>A content row as Trash/Restore needs it. <see cref="NameKey"/> is set for folders only.</summary>
    private sealed record RootRow(long ProjectNodeId, long? ContainerFolderId, string Name, string? NameKey, bool Trashed);

    /// <summary>The set a Trash would mark now. <see cref="Truncated"/>: the subtree walk hit its depth bound (fails closed).</summary>
    private sealed record TrashSet(IReadOnlyList<long> FolderIds, int Links, int Notes, bool Truncated)
    {
        public int Count => FolderIds.Count + Links + Notes;
    }

    private static string Table(ContentKind kind) => kind switch
    {
        ContentKind.Folder => "ProjectFolders",
        ContentKind.Link => "ProjectLinks",
        ContentKind.Note => "ProjectNotes",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>ONLY the table of the item's kind: Folder, Link and Note Ids are independent.</summary>
    private static RootRow? ReadRoot(SqliteConnection connection, SqliteTransaction transaction, ContentRef item)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = item.Kind switch
        {
            ContentKind.Folder => "SELECT ProjectNodeId, ParentFolderId, Name, NameKey, TrashEntryId IS NOT NULL FROM ProjectFolders WHERE Id = $id;",
            ContentKind.Link => "SELECT ProjectNodeId, FolderId, Name, NULL, TrashEntryId IS NOT NULL FROM ProjectLinks WHERE Id = $id;",
            ContentKind.Note => "SELECT ProjectNodeId, FolderId, Title, NULL, TrashEntryId IS NOT NULL FROM ProjectNotes WHERE Id = $id;",
            _ => throw new ArgumentOutOfRangeException(nameof(item), item.Kind, null),
        };
        command.Parameters.AddWithValue("$id", item.Id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new RootRow(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt64(4) == 1);
    }

    /// <summary>
    /// The root plus, for a folder, every CURRENTLY ACTIVE folder, link and note below it. The walk descends through active
    /// folders only (rows of earlier entries stay in their own entry) and is bounded by the depth limit.
    /// </summary>
    private TrashSet ReadActiveSet(SqliteConnection connection, SqliteTransaction transaction, ContentRef item)
    {
        switch (item.Kind)
        {
            case ContentKind.Link:
                return new TrashSet([], 1, 0, false);
            case ContentKind.Note:
                return new TrashSet([], 0, 1, false);
        }

        var folders = new HashSet<long>();
        var deepest = new List<long>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                WITH RECURSIVE subtree (Id, Depth) AS (
                    SELECT Id, 0 FROM ProjectFolders WHERE Id = $root
                    UNION ALL
                    SELECT c.Id, s.Depth + 1
                    FROM ProjectFolders AS c JOIN subtree AS s ON c.ParentFolderId = s.Id
                    WHERE c.TrashEntryId IS NULL AND s.Depth < $maxDepth
                )
                SELECT Id, Depth FROM subtree;
                """;
            command.Parameters.AddWithValue("$root", item.Id);
            command.Parameters.AddWithValue("$maxDepth", _maxDepth);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                folders.Add(reader.GetInt64(0));
                if (reader.GetInt64(1) >= _maxDepth)
                    deepest.Add(reader.GetInt64(0));
            }
        }

        var ids = JsonIds(folders);
        // Truncated when a folder at the bound still has active child folders (deep data or a corrupt cycle).
        var truncated = deepest.Count > 0 && CountIn(connection, transaction, "SELECT count(*) FROM ProjectFolders WHERE ParentFolderId IN (SELECT value FROM json_each($ids)) AND TrashEntryId IS NULL;", JsonIds(deepest)) > 0;
        var links = CountIn(connection, transaction, "SELECT count(*) FROM ProjectLinks WHERE FolderId IN (SELECT value FROM json_each($ids)) AND TrashEntryId IS NULL;", ids);
        var notes = CountIn(connection, transaction, "SELECT count(*) FROM ProjectNotes WHERE FolderId IN (SELECT value FROM json_each($ids)) AND TrashEntryId IS NULL;", ids);
        return new TrashSet(folders.ToList(), links, notes, truncated);
    }

    private static string JsonIds(IEnumerable<long> ids) =>
        "[" + string.Join(",", ids.Select(id => id.ToString(CultureInfo.InvariantCulture))) + "]";

    private static int CountIn(SqliteConnection connection, SqliteTransaction transaction, string sql, string ids)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$ids", ids);
        return (int)(long)command.ExecuteScalar()!;
    }

    /// <summary>Runs one marker UPDATE (<c>$entry</c>, optional <c>$ids</c>) and returns the number of rows changed.</summary>
    private static int Mark(SqliteConnection connection, SqliteTransaction transaction, string sql, long entryId, object? ids)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$entry", entryId);
        if (ids is not null)
            command.Parameters.AddWithValue("$ids", ids);
        return command.ExecuteNonQuery();
    }

    /// <summary>The name of the entry root that holds a trashed container ("Restore “Drafts” first.").</summary>
    private static string? BlockingRootName(SqliteConnection connection, SqliteTransaction transaction, long blockingEntryId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT f.Name FROM ProjectTrashEntries AS e
            JOIN ProjectFolders AS f ON e.RootKind = 'Folder' AND f.Id = e.RootId
            WHERE e.Id = $entry;
            """;
        command.Parameters.AddWithValue("$entry", blockingEntryId);
        return command.ExecuteScalar() as string;
    }

    private static bool ActiveSiblingNameExists(SqliteConnection connection, SqliteTransaction transaction, RootRow root, long folderId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM ProjectFolders
                WHERE ProjectNodeId = $project AND ParentFolderId IS $parent AND NameKey = $key AND TrashEntryId IS NULL AND Id <> $id);
            """;
        command.Parameters.AddWithValue("$project", root.ProjectNodeId);
        command.Parameters.AddWithValue("$parent", (object?)root.ContainerFolderId ?? DBNull.Value);
        command.Parameters.AddWithValue("$key", root.NameKey!);
        command.Parameters.AddWithValue("$id", folderId);
        return (long)command.ExecuteScalar()! == 1;
    }

    /// <summary>The committed root row, typed by its kind, with its container's current path (same transaction).</summary>
    private RestoredRoot ReadRestoredRoot(SqliteConnection connection, SqliteTransaction transaction, ContentRef item, RootRow root)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", item.Id);
        ProjectFolder? folder = null;
        ProjectLink? link = null;
        ProjectNoteSummary? note = null;
        switch (item.Kind)
        {
            case ContentKind.Folder:
                command.CommandText = $"SELECT {FolderColumns} FROM ProjectFolders WHERE Id = $id;";
                folder = ReadSingle(command, ReadFolder);
                break;
            case ContentKind.Link:
                command.CommandText = $"SELECT {LinkColumns} FROM ProjectLinks WHERE Id = $id;";
                link = ReadSingle(command, ReadLink);
                break;
            default:
                command.CommandText = "SELECT Id, ProjectNodeId, FolderId, Title, Revision, UpdatedAt FROM ProjectNotes WHERE Id = $id;";
                note = ReadSingle(command, r => new ProjectNoteSummary(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.GetString(3), r.GetInt64(4), ParseTime(r.GetString(5))));
                break;
        }

        var (path, _) = DescribeContainer(connection, transaction, ProjectName(connection, transaction, root.ProjectNodeId), root.ContainerFolderId);
        return new RestoredRoot(item, root.ProjectNodeId, root.ContainerFolderId, path?.Display ?? string.Empty, folder, link, note);
    }

    private static string ProjectName(SqliteConnection connection, SqliteTransaction transaction, long projectNodeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Name FROM WorkspaceNodes WHERE Id = $project;";
        command.Parameters.AddWithValue("$project", projectNodeId);
        return command.ExecuteScalar() as string ?? string.Empty;
    }

    /// <summary>A container's current path (null when the folder no longer exists) and whether it is in the Trash.</summary>
    private (ProjectContainerPath? Path, bool Trashed) DescribeContainer(SqliteConnection connection, SqliteTransaction transaction, string projectName, long? folderId)
    {
        if (folderId is not { } id)
            return (new ProjectContainerPath(projectName, []), false);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT TrashEntryId IS NOT NULL FROM ProjectFolders WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteScalar() is not long trashed)
            return (null, false);
        return (new ProjectContainerPath(projectName, ReadBreadcrumb(connection, transaction, id)), trashed == 1);
    }

    private ProjectNote InsertNote(SqliteConnection connection, SqliteTransaction transaction, long projectNodeId, long? folderId, ProjectNoteDraft draft)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO ProjectNotes (ProjectNodeId, FolderId, Title, Content, Revision, CreatedAt, UpdatedAt)
            VALUES ($project, $folder, $title, $content, 1, $now, $now)
            RETURNING {NoteColumns};
            """;
        command.Parameters.AddWithValue("$project", projectNodeId);
        command.Parameters.AddWithValue("$folder", (object?)folderId ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", draft.Title!);
        command.Parameters.AddWithValue("$content", draft.Content ?? string.Empty);
        command.Parameters.AddWithValue("$now", Now());
        try
        {
            return ReadSingle(command, ReadNote)!;
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintForeignKey)
        {
            throw new ContentContainerException(ContentContainerProblem.FolderNotFound, ex); // defense in depth
        }
    }

    private const string NoteColumns = "Id, ProjectNodeId, FolderId, Title, Content, Revision, CreatedAt, UpdatedAt, TrashEntryId";

    private static ProjectNote ReadNote(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.IsDBNull(2) ? null : reader.GetInt64(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetInt64(5),
        ParseTime(reader.GetString(6)),
        ParseTime(reader.GetString(7)))
    {
        IsTrashed = !reader.IsDBNull(8),
    };

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
                source.CommandText = $"SELECT ProjectNodeId, {containerColumn}, TrashEntryId IS NOT NULL FROM {table} WHERE Id = $id;";
                source.Parameters.AddWithValue("$id", id);
                using var reader = source.ExecuteReader();
                if (!reader.Read())
                    return ContentMoveOutcome.NotFound;
                if (TrashedProjectOfRow(connection, transaction, table, id) is not null)
                    return ContentMoveOutcome.ProjectTrashed; // checked before every content precondition
                if (reader.GetInt64(2) == 1)
                    return ContentMoveOutcome.ItemTrashed; // trashed rows are read-only
                projectNodeId = reader.GetInt64(0);
                current = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            }

            // 2. Observed container (NULL-safe). Checked before AlreadyThere: a stale observation is always reported.
            if (current != expected)
                return ContentMoveOutcome.MovedMeanwhile;

            if (target is { } destination)
            {
                // 3. Destination exists in the same Project and is not in the Trash.
                if (FolderProblem(connection, transaction, projectNodeId, destination) is { } problem)
                {
                    return problem switch
                    {
                        ContentContainerProblem.WrongProject => ContentMoveOutcome.WrongProject,
                        ContentContainerProblem.FolderTrashed => ContentMoveOutcome.DestinationTrashed,
                        _ => ContentMoveOutcome.DestinationNotFound,
                    };
                }

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

    /// <param name="requireActive">A Project in the Workspace Trash is <see cref="ContentContainerProblem.ProjectTrashed"/>
    /// (listing, create, save as new). Display-only lookups pass false.</param>
    private static ContentContainerProblem? ProjectProblem(SqliteConnection connection, SqliteTransaction transaction, long projectNodeId, bool requireActive = true)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT n.TrashedAt IS NOT NULL FROM Projects AS p JOIN WorkspaceNodes AS n ON n.Id = p.NodeId WHERE p.NodeId = $project;";
        command.Parameters.AddWithValue("$project", projectNodeId);
        return command.ExecuteScalar() switch
        {
            null => ContentContainerProblem.ProjectNotFound,
            1L when requireActive => ContentContainerProblem.ProjectTrashed,
            _ => null,
        };
    }

    /// <summary>
    /// The Project owning row <paramref name="id"/> of <paramref name="table"/> if that Project is in the Workspace Trash;
    /// null when it is active or the row no longer exists. Every item-based write asks this FIRST inside its write
    /// transaction (schema v7); the V007 triggers back it up. The table name is a constant, never input.
    /// </summary>
    private static long? TrashedProjectOfRow(SqliteConnection connection, SqliteTransaction? transaction, string table, long id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT r.ProjectNodeId FROM {table} AS r JOIN WorkspaceNodes AS n ON n.Id = r.ProjectNodeId WHERE r.Id = $id AND n.TrashedAt IS NOT NULL;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is long project ? project : null;
    }

    /// <param name="requireActive">A folder in the Trash is <see cref="ContentContainerProblem.FolderTrashed"/> (listing, create,
    /// move destination, save as new). Display-only lookups pass false.</param>
    private static ContentContainerProblem? FolderProblem(SqliteConnection connection, SqliteTransaction transaction, long projectNodeId, long? folderId, bool requireActive = true)
    {
        if (folderId is not { } id)
            return null;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT ProjectNodeId, TrashEntryId IS NOT NULL FROM ProjectFolders WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return ContentContainerProblem.FolderNotFound;
        if (reader.GetInt64(0) != projectNodeId)
            return ContentContainerProblem.WrongProject;
        return requireActive && reader.GetInt64(1) == 1 ? ContentContainerProblem.FolderTrashed : null;
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
    private const string LinkColumns = "Id, ProjectNodeId, FolderId, LinkType, Name, Target, CreatedAt, UpdatedAt, TrashEntryId";

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
        ParseTime(reader.GetString(7)))
    {
        IsTrashed = !reader.IsDBNull(8),
    };

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
