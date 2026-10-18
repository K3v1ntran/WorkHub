using System.Globalization;
using Microsoft.Data.Sqlite;
using WorkHub.Core.Workspace;

namespace WorkHub.Infrastructure.Persistence;

/// <summary>
/// SQLite implementation of <see cref="IWorkspaceStore"/> (schema v3; Workspace Trash v7; Project Archive v8). Same conventions as <see cref="SqliteOwnerStore"/>:
/// one pooled connection per call on a worker thread, parameterized SQL, transactions for multi-row writes.
/// Writes whose validity depends on other rows (parent checks, moves) use <c>BEGIN IMMEDIATE</c> so the checked state
/// cannot change before the write. The schema's indexes and triggers back every rule up.
/// </summary>
public sealed class SqliteWorkspaceStore(SqliteConnectionFactory connections, TimeProvider? time = null) : IWorkspaceStore
{
    /// <summary>Bound for one folder's listing and for the Move destination list.</summary>
    public const int MaxRowsPerQuery = 10000;

    /// <summary>Bound for ancestry walks, so corrupt data can never make a recursive query run away.</summary>
    internal const int MaxDepth = 10000;

    private const int SqliteConstraintUnique = 2067;
    private const int SqliteConstraintForeignKey = 787;
    private const int SqliteConstraintTrigger = 1811;
    private const string Folder = "Folder";
    private const string Project = "Project";
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public Task<IReadOnlyList<WorkspaceNode>> ListChildrenAsync(long? parentId, CancellationToken cancellationToken = default) =>
        Run<IReadOnlyList<WorkspaceNode>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT n.Id, n.ParentId, n.NodeType, n.Name,
                       EXISTS (SELECT 1 FROM WorkspaceNodes AS c WHERE c.ParentId = n.Id AND {Visible("c")}) AS HasChildren
                FROM WorkspaceNodes AS n
                WHERE n.ParentId IS $parent AND {Visible("n")}
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", MaxRowsPerQuery);
            using var reader = command.ExecuteReader();
            var nodes = new List<WorkspaceNode>();
            while (reader.Read())
            {
                nodes.Add(new WorkspaceNode(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    ParseType(reader.GetString(2)),
                    reader.GetString(3),
                    reader.GetInt64(4) == 1));
            }

            return nodes;
        }, cancellationToken);

    public Task<WorkspaceNode> CreateFolderAsync(long? parentId, string name, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            RequireFolderParent(connection, transaction, parentId);
            var id = InsertNode(connection, transaction, parentId, Folder, name);
            transaction.Commit();
            return new WorkspaceNode(id, parentId, WorkspaceNodeType.Folder, name, false);
        }, cancellationToken);

    public Task<WorkspaceNode> CreateProjectAsync(long? parentId, string projectId, ProjectDraft draft, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            RequireFolderParent(connection, transaction, parentId);
            var id = InsertNode(connection, transaction, parentId, Project, draft.Name!);

            using var details = connection.CreateCommand();
            details.Transaction = transaction;
            details.CommandText = """
                INSERT INTO Projects (NodeId, ProjectId, Status, Description, Owner, ProjectManager)
                VALUES ($id, $projectId, $status, $description, $owner, $manager);
                """;
            details.Parameters.AddWithValue("$id", id);
            details.Parameters.AddWithValue("$projectId", projectId);
            AddDetailParameters(details, draft);
            details.ExecuteNonQuery();

            transaction.Commit(); // node and details together, or neither (Dispose rolls back on any exception)
            return new WorkspaceNode(id, parentId, WorkspaceNodeType.Project, draft.Name!, false);
        }, cancellationToken);

    public Task<bool> RenameAsync(long nodeId, string name, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            if (ReadState(connection, transaction, nodeId) is not { } state)
                return false;
            if (state.Trashed)
                throw new ProjectTrashedException(nodeId); // checked first: a trashed Project is read-only
            if (state.Archived)
                throw new ProjectArchivedException(nodeId); // Phase 12 A3: an archived Project keeps its name and place

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE WorkspaceNodes SET Name = $name, NameKey = $key, UpdatedAt = $now WHERE Id = $id;";
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$key", WorkspaceName.Key(name));
            command.Parameters.AddWithValue("$now", Now());
            command.Parameters.AddWithValue("$id", nodeId);
            var changed = GuardTrashed(() => GuardDuplicateName(command.ExecuteNonQuery, name,
                () => BlockerIsArchived(connection, transaction, state.ParentId, name, nodeId)), nodeId) == 1;
            transaction.Commit();
            return changed;
        }, cancellationToken);

    public Task<ProjectDetails?> GetProjectAsync(long nodeId, CancellationToken cancellationToken = default) =>
        Run(connection => ReadProject(connection, null, nodeId), cancellationToken);

    public Task<ProjectDetails?> UpdateProjectAsync(long nodeId, ProjectDraft draft, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            var state = ReadState(connection, transaction, nodeId);
            if (state is { Trashed: true })
                throw new ProjectTrashedException(nodeId); // checked first: a trashed Project is read-only
            if (state is { Archived: true })
                throw new ProjectArchivedException(nodeId); // Phase 12 A3: a stale editor never silently unarchives

            using (var node = connection.CreateCommand())
            {
                node.Transaction = transaction;
                node.CommandText = """
                    UPDATE WorkspaceNodes SET Name = $name, NameKey = $key, UpdatedAt = $now
                    WHERE Id = $id AND NodeType = 'Project';
                    """;
                node.Parameters.AddWithValue("$name", draft.Name!);
                node.Parameters.AddWithValue("$key", WorkspaceName.Key(draft.Name!));
                node.Parameters.AddWithValue("$now", Now());
                node.Parameters.AddWithValue("$id", nodeId);
                if (GuardTrashed(() => GuardDuplicateName(node.ExecuteNonQuery, draft.Name!,
                        () => BlockerIsArchived(connection, transaction, state!.ParentId, draft.Name!, nodeId)), nodeId) != 1)
                    return null;
            }

            using (var details = connection.CreateCommand())
            {
                details.Transaction = transaction;
                details.CommandText = """
                    UPDATE Projects SET Status = $status, Description = $description, Owner = $owner, ProjectManager = $manager
                    WHERE NodeId = $id;
                    """;
                details.Parameters.AddWithValue("$id", nodeId);
                AddDetailParameters(details, draft);
                if (details.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException($"Project node {nodeId} has no details row."); // rolls back the rename too
            }

            var saved = ReadProject(connection, transaction, nodeId);
            transaction.Commit();
            return saved;
        }, cancellationToken);

    public Task<IReadOnlyList<WorkspaceFolderPath>> ListMoveTargetsAsync(long movingNodeId, CancellationToken cancellationToken = default) =>
        Run<IReadOnlyList<WorkspaceFolderPath>>(connection =>
        {
            using var command = connection.CreateCommand();
            // Walking down from the roots and never entering the moving node excludes it and its whole subtree.
            command.CommandText = """
                WITH RECURSIVE folders (Id, Path, Depth) AS (
                    SELECT Id, Name, 0 FROM WorkspaceNodes
                    WHERE ParentId IS NULL AND NodeType = 'Folder' AND Id <> $moving
                    UNION ALL
                    SELECT c.Id, f.Path || ' / ' || c.Name, f.Depth + 1
                    FROM WorkspaceNodes AS c JOIN folders AS f ON c.ParentId = f.Id
                    WHERE c.NodeType = 'Folder' AND c.Id <> $moving AND f.Depth < $maxDepth
                )
                SELECT Id, Path FROM folders LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$moving", movingNodeId);
            command.Parameters.AddWithValue("$maxDepth", MaxDepth);
            command.Parameters.AddWithValue("$limit", MaxRowsPerQuery);
            using var reader = command.ExecuteReader();
            var folders = new List<WorkspaceFolderPath>();
            while (reader.Read())
                folders.Add(new WorkspaceFolderPath(reader.GetInt64(0), reader.GetString(1)));
            return folders;
        }, cancellationToken);

    public Task<bool> MoveAsync(long nodeId, long? targetParentId, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            // One write transaction for every check and the update: BEGIN IMMEDIATE takes the write lock first,
            // so the validated state is the state that gets updated.
            using var transaction = connection.BeginTransaction(deferred: false);

            // 1. Source.
            using var source = connection.CreateCommand();
            source.Transaction = transaction;
            source.CommandText = """
                SELECT n.ParentId, n.Name, n.TrashedAt IS NOT NULL, p.Status IS 'Archived'
                FROM WorkspaceNodes AS n LEFT JOIN Projects AS p ON p.NodeId = n.Id WHERE n.Id = $id;
                """;
            source.Parameters.AddWithValue("$id", nodeId);
            long? currentParent;
            string name;
            using (var reader = source.ExecuteReader())
            {
                if (!reader.Read())
                    throw new WorkspaceNodeNotFoundException(nodeId);
                if (reader.GetInt64(2) == 1)
                    throw new ProjectTrashedException(nodeId); // checked first: a trashed Project is read-only
                if (reader.GetInt64(3) == 1)
                    throw new ProjectArchivedException(nodeId); // Phase 12 A3: an archived Project keeps its place
                currentParent = reader.IsDBNull(0) ? null : reader.GetInt64(0);
                name = reader.GetString(1);
            }

            if (targetParentId is { } target)
            {
                // 2. Destination must be an existing Folder.
                RequireFolderParent(connection, transaction, target);

                // 3. Ancestry: the destination may not be the node itself or anywhere below it.
                using var ancestry = connection.CreateCommand();
                ancestry.Transaction = transaction;
                ancestry.CommandText = """
                    WITH RECURSIVE ancestors (Id, Depth) AS (
                        SELECT $target, 0
                        UNION ALL
                        SELECT n.ParentId, a.Depth + 1
                        FROM WorkspaceNodes AS n JOIN ancestors AS a ON n.Id = a.Id
                        WHERE n.ParentId IS NOT NULL AND a.Depth < $maxDepth
                    )
                    SELECT EXISTS (SELECT 1 FROM ancestors WHERE Id = $node)
                        OR (SELECT max(Depth) FROM ancestors) >= $maxDepth;
                    """;
                ancestry.Parameters.AddWithValue("$target", target);
                ancestry.Parameters.AddWithValue("$node", nodeId);
                ancestry.Parameters.AddWithValue("$maxDepth", MaxDepth);
                if (Convert.ToInt64(ancestry.ExecuteScalar(), CultureInfo.InvariantCulture) == 1)
                    throw new WorkspaceMoveCycleException(nodeId, target);
            }

            // 4. Already there: nothing to write.
            if (currentParent == targetParentId)
                return false;

            // 5. The update, guarded by the parent read in step 1. A destination name conflict raises 2067.
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE WorkspaceNodes SET ParentId = $target, UpdatedAt = $now WHERE Id = $id AND ParentId IS $expected;";
            update.Parameters.AddWithValue("$target", (object?)targetParentId ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", Now());
            update.Parameters.AddWithValue("$id", nodeId);
            update.Parameters.AddWithValue("$expected", (object?)currentParent ?? DBNull.Value);
            var changed = GuardParentTrigger(() => GuardDuplicateName(update.ExecuteNonQuery, name,
                () => BlockerIsArchived(connection, transaction, targetParentId, name, nodeId)), targetParentId ?? 0);
            if (changed != 1)
                throw new WorkspaceNodeNotFoundException(nodeId);

            transaction.Commit();
            return true;
        }, cancellationToken);

    // ---- Workspace Trash (schema v7) -----------------------------------------------------------------------------------

    public Task<ProjectTrashResult> TrashProjectAsync(long nodeId, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);

            // 1. The node by Id, as it is now (identity-based: no expected-parent precondition).
            if (ReadState(connection, transaction, nodeId) is not { } state)
                return ProjectTrashResult.Refused(ProjectTrashOutcome.NotFound);
            if (state.Type != WorkspaceNodeType.Project)
                return ProjectTrashResult.Refused(ProjectTrashOutcome.NotAProject);
            if (state.Trashed)
                return ProjectTrashResult.Refused(ProjectTrashOutcome.AlreadyTrashed);
            if (state.Archived)
                return ProjectTrashResult.Refused(ProjectTrashOutcome.Archived); // Phase 12 A7: never archived and trashed by this build

            // 2. The marker only (the V007 triggers refuse any other change in the same statement).
            var trashedAt = Now();
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE WorkspaceNodes SET TrashedAt = $at WHERE Id = $id AND TrashedAt IS NULL;";
                update.Parameters.AddWithValue("$at", trashedAt);
                update.Parameters.AddWithValue("$id", nodeId);
                if (update.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("An active Project was not marked under the write lock; nothing was changed.");
            }

            // 3. The entry as it now appears in the Workspace Trash.
            var entry = new WorkspaceTrashEntry(nodeId, ReadName(connection, transaction, nodeId), state.ParentId,
                ReadPath(connection, transaction, state.ParentId), ParseTime(trashedAt));
            transaction.Commit();
            return new ProjectTrashResult(ProjectTrashOutcome.Trashed, entry);
        }, cancellationToken);

    public Task<ProjectRestoreResult> RestoreProjectAsync(long nodeId, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);

            // 1. The node (already restored elsewhere → NotTrashed; nothing written).
            if (ReadState(connection, transaction, nodeId) is not { } state)
                return ProjectRestoreResult.Refused(ProjectRestoreOutcome.NotFound);
            if (!state.Trashed)
                return ProjectRestoreResult.Refused(ProjectRestoreOutcome.NotTrashed);

            // 2. The original parent (never changed while trashed): it cannot be deleted or trashed, so a broken chain is a
            // data error — fail closed, nothing written.
            if (state.ParentId is { } parentId)
            {
                using var parent = connection.CreateCommand();
                parent.Transaction = transaction;
                parent.CommandText = "SELECT NodeType FROM WorkspaceNodes WHERE Id = $id;";
                parent.Parameters.AddWithValue("$id", parentId);
                if (parent.ExecuteScalar() is not Folder)
                    throw new InvalidDataException($"The original parent of trashed Project {nodeId} is missing or not a folder; nothing was restored.");
            }

            // 3. An ACTIVE sibling (Folder or Project) with the same name key refuses; nothing is renamed or re-parented.
            var name = ReadName(connection, transaction, nodeId);
            using (var conflict = connection.CreateCommand())
            {
                conflict.Transaction = transaction;
                conflict.CommandText = """
                    SELECT Name FROM WorkspaceNodes
                    WHERE ParentId IS $parent AND TrashedAt IS NULL AND Id <> $id
                      AND NameKey = (SELECT NameKey FROM WorkspaceNodes WHERE Id = $id)
                    LIMIT 1;
                    """;
                conflict.Parameters.AddWithValue("$parent", (object?)state.ParentId ?? DBNull.Value);
                conflict.Parameters.AddWithValue("$id", nodeId);
                if (conflict.ExecuteScalar() is string blocker)
                    return ProjectRestoreResult.Refused(ProjectRestoreOutcome.NameConflict, blocker);
            }

            // 4. Clear the marker only.
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE WorkspaceNodes SET TrashedAt = NULL WHERE Id = $id AND TrashedAt IS NOT NULL;";
                update.Parameters.AddWithValue("$id", nodeId);
                try
                {
                    if (update.ExecuteNonQuery() != 1)
                        throw new InvalidOperationException("A trashed Project was not restored under the write lock; nothing was changed.");
                }
                catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintUnique)
                {
                    return ProjectRestoreResult.Refused(ProjectRestoreOutcome.NameConflict); // backstop: Dispose rolls back
                }
            }

            // 5. Read back: a Project has no tree children. An archived-plus-trashed Project (earlier build) comes back archived.
            var restored = new RestoredProject(new WorkspaceNode(nodeId, state.ParentId, WorkspaceNodeType.Project, name, false),
                ReadPath(connection, transaction, state.ParentId))
            {
                IsArchived = state.Archived,
            };
            transaction.Commit();
            return new ProjectRestoreResult(ProjectRestoreOutcome.Restored, restored, null);
        }, cancellationToken);

    public Task<IReadOnlyList<WorkspaceTrashEntry>> ListTrashAsync(CancellationToken cancellationToken = default) =>
        Run<IReadOnlyList<WorkspaceTrashEntry>>(connection =>
        {
            // One read transaction: the entries and their parents' paths come from the same snapshot.
            using var transaction = connection.BeginTransaction(deferred: true);
            var rows = new List<(long Id, string Name, long? ParentId, string TrashedAt, bool Archived)>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT n.Id, n.Name, n.ParentId, n.TrashedAt, p.Status IS 'Archived'
                    FROM WorkspaceNodes AS n LEFT JOIN Projects AS p ON p.NodeId = n.Id
                    WHERE n.TrashedAt IS NOT NULL
                    ORDER BY n.TrashedAt DESC, n.Id DESC
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$limit", MaxRowsPerQuery);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    rows.Add((reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.GetString(3), reader.GetInt64(4) == 1));
            }

            var paths = new Dictionary<long, string?>();
            var entries = new List<WorkspaceTrashEntry>(rows.Count);
            foreach (var (id, name, parentId, trashedAt, archived) in rows)
            {
                string? path = null;
                if (parentId is { } parent && !paths.TryGetValue(parent, out path))
                    paths[parent] = path = ReadPath(connection, transaction, parent);
                entries.Add(new WorkspaceTrashEntry(id, name, parentId, path, ParseTime(trashedAt)) { IsArchived = archived });
            }

            transaction.Commit();
            return entries;
        }, cancellationToken);

    public Task<int> CountTrashAsync(CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM WorkspaceNodes WHERE TrashedAt IS NOT NULL;";
            return (int)(long)command.ExecuteScalar()!;
        }, cancellationToken);

    public Task<FolderRemoveResult> RemoveEmptyFolderAsync(long nodeId, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);

            // 1. The node.
            if (ReadState(connection, transaction, nodeId) is not { } state)
                return FolderRemoveResult.Of(FolderRemoveOutcome.NotFound);
            if (state.Type != WorkspaceNodeType.Folder)
                return FolderRemoveResult.Of(FolderRemoveOutcome.NotAFolder);

            // 2. Every child row counts: visible nodes, archived Projects (Unarchive needs their parent) and trashed Projects
            // (strict restore needs their parent).
            var (active, archived, trashed) = CountChildren(connection, transaction, nodeId);
            if (active + archived + trashed > 0)
                return new FolderRemoveResult(FolderRemoveOutcome.NotEmpty, active, trashed) { ArchivedProjects = archived };

            // 3. The delete (the V007 guard and the V003 RESTRICT FK independently refuse a non-empty folder or any Project).
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM WorkspaceNodes WHERE Id = $id AND NodeType = 'Folder';";
                delete.Parameters.AddWithValue("$id", nodeId);
                if (delete.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("An empty folder was not removed under the write lock; nothing was changed.");
            }

            transaction.Commit();
            return FolderRemoveResult.Of(FolderRemoveOutcome.Removed);
        }, cancellationToken);

    public Task<WorkspaceNodeState?> GetNodeStateAsync(long nodeId, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var state = ReadState(connection, transaction, nodeId);
            if (state is not null)
            {
                var (active, archived, trashed) = CountChildren(connection, transaction, nodeId);
                state = state with { ActiveChildren = active, ArchivedChildren = archived, TrashedChildren = trashed };
            }

            transaction.Commit();
            return state;
        }, cancellationToken);

    // ---- Project Archive (schema v8) -----------------------------------------------------------------------------------

    public Task<ProjectArchiveResult> ArchiveProjectAsync(long nodeId, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);

            // 1. The node by Id, as it is now (under the write lock).
            if (ReadArchiveState(connection, transaction, nodeId) is not { } state)
                return ProjectArchiveResult.Refused(ProjectArchiveOutcome.NotFound);
            if (state.Type != WorkspaceNodeType.Project)
                return ProjectArchiveResult.Refused(ProjectArchiveOutcome.NotAProject);
            if (state.Trashed)
                return ProjectArchiveResult.Refused(ProjectArchiveOutcome.ProjectTrashed);
            if (state.Status == ProjectStatus.Archived)
                return ProjectArchiveResult.Refused(ProjectArchiveOutcome.AlreadyArchived);

            // 2. Status and serial only, guarded by what step 1 read. THIS statement raises the serial; the V008 trigger only
            // validates the transition, and TR_Projects_TrashedReadOnly backs up step 1's Trash check.
            long serial;
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE Projects SET Status = 'Archived', ArchiveSerial = ArchiveSerial + 1
                    WHERE NodeId = $id AND Status = $observedStatus AND ArchiveSerial = $observedSerial
                    RETURNING ArchiveSerial;
                    """;
                update.Parameters.AddWithValue("$id", nodeId);
                update.Parameters.AddWithValue("$observedStatus", ProjectStatuses.StorageCode(state.Status!.Value));
                update.Parameters.AddWithValue("$observedSerial", state.Serial!.Value);
                serial = update.ExecuteScalar() is long committed
                    ? committed
                    : throw new InvalidOperationException("An active Project was not archived under the write lock; nothing was changed.");
            }

            // 3. The entry as it now appears in the Archived panel.
            var entry = ReadArchivedEntry(connection, transaction, nodeId);
            transaction.Commit();
            return new ProjectArchiveResult(ProjectArchiveOutcome.Archived, entry, new ArchiveEvent(nodeId, serial, state.Status.Value));
        }, cancellationToken);

    public Task<ProjectUnarchiveResult> UnarchiveProjectAsync(long nodeId, ProjectStatus status, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);

            // 1. The node (unarchived elsewhere → NotArchived; nothing written).
            if (ReadArchiveState(connection, transaction, nodeId) is not { } state)
                return ProjectUnarchiveResult.Refused(ProjectUnarchiveOutcome.NotFound);
            if (state.Type != WorkspaceNodeType.Project)
                return ProjectUnarchiveResult.Refused(ProjectUnarchiveOutcome.NotAProject);
            if (state.Trashed)
                return ProjectUnarchiveResult.Refused(ProjectUnarchiveOutcome.ProjectTrashed);
            if (state.Status != ProjectStatus.Archived)
                return ProjectUnarchiveResult.Refused(ProjectUnarchiveOutcome.NotArchived);

            // 2. Status only (state-based: whichever archive event is current); the serial is left as it is.
            RequireArchiveParent(connection, transaction, nodeId, state.ParentId);
            SetStatusFromArchived(connection, transaction, nodeId, status, serial: null);
            var unarchived = ReadUnarchived(connection, transaction, nodeId, state.ParentId);
            transaction.Commit();
            return new ProjectUnarchiveResult(ProjectUnarchiveOutcome.Unarchived, unarchived);
        }, cancellationToken);

    public Task<ArchiveUndoResult> UndoArchiveAsync(ArchiveEvent archiveEvent, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            var nodeId = archiveEvent.NodeId;

            // 1. The SAME archive event: still archived, with exactly the serial that Archive committed.
            if (ReadArchiveState(connection, transaction, nodeId) is not { Type: WorkspaceNodeType.Project } state)
                return ArchiveUndoResult.Refused(ArchiveUndoOutcome.NotFound);
            if (state.Trashed)
                return ArchiveUndoResult.Refused(ArchiveUndoOutcome.ProjectTrashed);
            if (state.Status != ProjectStatus.Archived)
                return ArchiveUndoResult.Refused(ArchiveUndoOutcome.NotArchived);
            if (state.Serial != archiveEvent.Serial)
                return ArchiveUndoResult.Refused(ArchiveUndoOutcome.ArchivedAgain);

            // 2. The previous status, guarded by status AND serial; the serial is left as it is.
            RequireArchiveParent(connection, transaction, nodeId, state.ParentId);
            SetStatusFromArchived(connection, transaction, nodeId, archiveEvent.PreviousStatus, archiveEvent.Serial);
            var restored = ReadUnarchived(connection, transaction, nodeId, state.ParentId);
            transaction.Commit();
            return new ArchiveUndoResult(ArchiveUndoOutcome.Undone, restored);
        }, cancellationToken);

    public Task<IReadOnlyList<ArchivedProjectEntry>> ListArchivedAsync(CancellationToken cancellationToken = default) =>
        Run<IReadOnlyList<ArchivedProjectEntry>>(connection =>
        {
            // One read transaction: the entries and their parents' paths come from the same snapshot.
            using var transaction = connection.BeginTransaction(deferred: true);
            var rows = new List<(long Id, string Name, long? ParentId, string UpdatedAt)>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT n.Id, n.Name, n.ParentId, n.UpdatedAt
                    FROM Projects AS p JOIN WorkspaceNodes AS n ON n.Id = p.NodeId
                    WHERE p.Status = 'Archived' AND n.TrashedAt IS NULL
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$limit", MaxRowsPerQuery);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    rows.Add((reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.GetString(3)));
            }

            var paths = new Dictionary<long, string?>();
            var entries = new List<ArchivedProjectEntry>(rows.Count);
            foreach (var (id, name, parentId, updatedAt) in rows)
            {
                string? path = null;
                if (parentId is { } parent && !paths.TryGetValue(parent, out path))
                    paths[parent] = path = ReadPath(connection, transaction, parent);
                entries.Add(new ArchivedProjectEntry(id, name, parentId, path, ParseTime(updatedAt)));
            }

            transaction.Commit();
            return entries.OrderBy(e => (WorkspaceNodeType.Project, e.Name, e.NodeId), WorkspaceNodeOrder.Instance).ToList();
        }, cancellationToken);

    public Task<int> CountArchivedAsync(CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT count(*) FROM Projects AS p JOIN WorkspaceNodes AS n ON n.Id = p.NodeId
                WHERE p.Status = 'Archived' AND n.TrashedAt IS NULL;
                """;
            return (int)(long)command.ExecuteScalar()!;
        }, cancellationToken);

    private sealed record ArchiveState(WorkspaceNodeType Type, long? ParentId, bool Trashed, ProjectStatus? Status, long? Serial);

    private static ArchiveState? ReadArchiveState(SqliteConnection connection, SqliteTransaction transaction, long nodeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT n.NodeType, n.ParentId, n.TrashedAt IS NOT NULL, p.Status, p.ArchiveSerial
            FROM WorkspaceNodes AS n LEFT JOIN Projects AS p ON p.NodeId = n.Id WHERE n.Id = $id;
            """;
        command.Parameters.AddWithValue("$id", nodeId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new ArchiveState(
            ParseType(reader.GetString(0)),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            reader.GetInt64(2) == 1,
            reader.IsDBNull(3) ? null : ProjectStatuses.FromStorageCode(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetInt64(4));
    }

    /// <summary>The parent of an archived Project (never removable while it holds the Project): a broken chain fails closed.</summary>
    private static void RequireArchiveParent(SqliteConnection connection, SqliteTransaction transaction, long nodeId, long? parentId)
    {
        if (parentId is not { } id)
            return;
        using var parent = connection.CreateCommand();
        parent.Transaction = transaction;
        parent.CommandText = "SELECT NodeType FROM WorkspaceNodes WHERE Id = $id;";
        parent.Parameters.AddWithValue("$id", id);
        if (parent.ExecuteScalar() is not Folder)
            throw new InvalidDataException($"The parent of archived Project {nodeId} is missing or not a folder; nothing was changed.");
    }

    /// <summary>Unarchive (serial null: any archive event) or Undo (exactly <paramref name="serial"/>); never writes the serial.</summary>
    private static void SetStatusFromArchived(SqliteConnection connection, SqliteTransaction transaction, long nodeId, ProjectStatus status, long? serial)
    {
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = serial is null
            ? "UPDATE Projects SET Status = $status WHERE NodeId = $id AND Status = 'Archived';"
            : "UPDATE Projects SET Status = $status WHERE NodeId = $id AND Status = 'Archived' AND ArchiveSerial = $serial;";
        update.Parameters.AddWithValue("$status", ProjectStatuses.StorageCode(status));
        update.Parameters.AddWithValue("$id", nodeId);
        if (serial is { } s)
            update.Parameters.AddWithValue("$serial", s);
        if (update.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("An archived Project was not unarchived under the write lock; nothing was changed.");
    }

    private static ArchivedProjectEntry ReadArchivedEntry(SqliteConnection connection, SqliteTransaction transaction, long nodeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Name, ParentId, UpdatedAt FROM WorkspaceNodes WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", nodeId);
        string name, updatedAt;
        long? parentId;
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            (name, parentId, updatedAt) = (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetString(2));
        }

        return new ArchivedProjectEntry(nodeId, name, parentId, ReadPath(connection, transaction, parentId), ParseTime(updatedAt));
    }

    /// <summary>The Project as it now appears in the tree (a Project has no tree children).</summary>
    private static RestoredProject ReadUnarchived(SqliteConnection connection, SqliteTransaction transaction, long nodeId, long? parentId) =>
        new(new WorkspaceNode(nodeId, parentId, WorkspaceNodeType.Project, ReadName(connection, transaction, nodeId), false),
            ReadPath(connection, transaction, parentId));

    private static WorkspaceNodeState? ReadState(SqliteConnection connection, SqliteTransaction transaction, long nodeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT n.NodeType, n.ParentId, n.TrashedAt IS NOT NULL, p.Status IS 'Archived'
            FROM WorkspaceNodes AS n LEFT JOIN Projects AS p ON p.NodeId = n.Id WHERE n.Id = $id;
            """;
        command.Parameters.AddWithValue("$id", nodeId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new WorkspaceNodeState(nodeId, ParseType(reader.GetString(0)), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetInt64(2) == 1)
        {
            Archived = reader.GetInt64(3) == 1,
        };
    }

    /// <returns>Direct child rows: visible (active, not archived), archived Projects that are not trashed, and trashed Projects.</returns>
    private static (int Active, int Archived, int Trashed) CountChildren(SqliteConnection connection, SqliteTransaction transaction, long nodeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT count(*) FILTER (WHERE n.TrashedAt IS NULL AND p.Status IS NOT 'Archived'),
                   count(*) FILTER (WHERE n.TrashedAt IS NULL AND p.Status IS 'Archived'),
                   count(*) FILTER (WHERE n.TrashedAt IS NOT NULL)
            FROM WorkspaceNodes AS n LEFT JOIN Projects AS p ON p.NodeId = n.Id WHERE n.ParentId = $id;
            """;
        command.Parameters.AddWithValue("$id", nodeId);
        using var reader = command.ExecuteReader();
        reader.Read();
        return ((int)reader.GetInt64(0), (int)reader.GetInt64(1), (int)reader.GetInt64(2));
    }

    private static string ReadName(SqliteConnection connection, SqliteTransaction transaction, long nodeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Name FROM WorkspaceNodes WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", nodeId);
        return (string)command.ExecuteScalar()!;
    }

    /// <summary>"Clients / Tokyo" for a folder (the same separator as Move destinations); null for the root.</summary>
    private static string? ReadPath(SqliteConnection connection, SqliteTransaction transaction, long? folderId)
    {
        if (folderId is not { } id)
            return null;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH RECURSIVE chain (Id, ParentId, Name, Depth) AS (
                SELECT Id, ParentId, Name, 0 FROM WorkspaceNodes WHERE Id = $id
                UNION ALL
                SELECT n.Id, n.ParentId, n.Name, c.Depth + 1
                FROM WorkspaceNodes AS n JOIN chain AS c ON n.Id = c.ParentId
                WHERE c.Depth < $maxDepth
            )
            SELECT Name FROM chain ORDER BY Depth DESC;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$maxDepth", MaxDepth);
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return string.Join(" / ", names);
    }

    /// <summary>Defense in depth: the explicit state check normally reports a trashed Project first.</summary>
    private static T GuardTrashed<T>(Func<T> execute, long nodeId)
    {
        try
        {
            return execute();
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintTrigger && ex.Message.Contains("WH_PROJECT_TRASHED", StringComparison.Ordinal))
        {
            throw new ProjectTrashedException(nodeId, ex);
        }
    }

    private Task<T> Run<T>(Func<SqliteConnection, T> work, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            using var connection = connections.OpenExisting();
            return work(connection);
        }, cancellationToken);

    private long InsertNode(SqliteConnection connection, SqliteTransaction transaction, long? parentId, string type, string name)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO WorkspaceNodes (ParentId, NodeType, Name, NameKey, SortOrder, CreatedAt, UpdatedAt)
            VALUES ($parent, $type, $name, $key, 0, $now, $now)
            RETURNING Id;
            """;
        command.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$key", WorkspaceName.Key(name));
        command.Parameters.AddWithValue("$now", Now());
        return GuardParentTrigger(() => GuardDuplicateName(() => (long)command.ExecuteScalar()!, name,
            () => BlockerIsArchived(connection, transaction, parentId, name, excludingId: 0)), parentId ?? 0);
    }

    /// <summary>Checks inside the caller's write transaction that a non-null parent exists and is a Folder.</summary>
    private static void RequireFolderParent(SqliteConnection connection, SqliteTransaction transaction, long? parentId)
    {
        if (parentId is not { } id)
            return;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT NodeType FROM WorkspaceNodes WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        switch (command.ExecuteScalar())
        {
            case null:
                throw new WorkspaceParentException(id, WorkspaceParentProblem.NotFound);
            case string type when type != Folder:
                throw new WorkspaceParentException(id, WorkspaceParentProblem.NotAFolder);
        }
    }

    private static ProjectDetails? ReadProject(SqliteConnection connection, SqliteTransaction? transaction, long nodeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT n.Id, p.ProjectId, n.ParentId, n.Name, p.Status, p.Description, p.Owner, p.ProjectManager, n.CreatedAt, n.UpdatedAt,
                   n.TrashedAt IS NOT NULL
            FROM WorkspaceNodes AS n JOIN Projects AS p ON p.NodeId = n.Id
            WHERE n.Id = $id AND n.NodeType = 'Project';
            """;
        command.Parameters.AddWithValue("$id", nodeId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return new ProjectDetails(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.GetString(3),
            ProjectStatuses.FromStorageCode(reader.GetString(4)),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            ParseTime(reader.GetString(8)),
            ParseTime(reader.GetString(9)))
        {
            IsTrashed = reader.GetInt64(10) == 1,
        };
    }

    private static void AddDetailParameters(SqliteCommand command, ProjectDraft draft)
    {
        command.Parameters.AddWithValue("$status", ProjectStatuses.StorageCode(draft.Status));
        command.Parameters.AddWithValue("$description", (object?)draft.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$owner", (object?)draft.Owner ?? DBNull.Value);
        command.Parameters.AddWithValue("$manager", (object?)draft.ProjectManager ?? DBNull.Value);
    }

    /// <param name="blockerArchived">Read after the refused statement, inside the same transaction: is the sibling that holds
    /// the name an archived Project (hidden from the tree)?</param>
    private static T GuardDuplicateName<T>(Func<T> execute, string name, Func<bool> blockerArchived)
    {
        try
        {
            return execute();
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintUnique)
        {
            throw new DuplicateWorkspaceNameException(name, ex) { BlockerArchived = blockerArchived() };
        }
    }

    /// <summary>The ACTIVE sibling under <paramref name="parentId"/> with the name key of <paramref name="name"/> is an archived Project.</summary>
    private static bool BlockerIsArchived(SqliteConnection connection, SqliteTransaction transaction, long? parentId, string name, long excludingId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM WorkspaceNodes AS n JOIN Projects AS p ON p.NodeId = n.Id
                WHERE n.ParentId IS $parent AND n.NameKey = $key AND n.TrashedAt IS NULL AND n.Id <> $id AND p.Status = 'Archived');
            """;
        command.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$key", WorkspaceName.Key(name));
        command.Parameters.AddWithValue("$id", excludingId);
        return (long)command.ExecuteScalar()! == 1;
    }

    /// <summary>
    /// SQL condition: the node aliased <paramref name="alias"/> is shown in the tree — active (not in the Workspace Trash) and not an
    /// archived Project (Phase 12). Shared by tree listings and the search store's reveal branches.
    /// </summary>
    internal static string Visible(string alias) =>
        $"{alias}.TrashedAt IS NULL AND NOT EXISTS (SELECT 1 FROM Projects AS ap WHERE ap.NodeId = {alias}.Id AND ap.Status = 'Archived')";

    /// <summary>Defense in depth: the explicit parent check normally reports these first.</summary>
    private static T GuardParentTrigger<T>(Func<T> execute, long parentId)
    {
        try
        {
            return execute();
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintTrigger && ex.Message.Contains("WH_PARENT_NOT_FOLDER", StringComparison.Ordinal))
        {
            throw new WorkspaceParentException(parentId, WorkspaceParentProblem.NotAFolder, ex);
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintForeignKey)
        {
            throw new WorkspaceParentException(parentId, WorkspaceParentProblem.NotFound, ex);
        }
    }

    private static WorkspaceNodeType ParseType(string value) => value switch
    {
        Folder => WorkspaceNodeType.Folder,
        Project => WorkspaceNodeType.Project,
        _ => throw new FormatException($"Unknown workspace node type '{value}'."),
    };

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private string Now() => _time.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
