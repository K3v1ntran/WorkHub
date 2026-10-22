using System.Globalization;
using Microsoft.Data.Sqlite;
using WorkHub.Core.Workspace;

namespace WorkHub.Infrastructure.Persistence;

/// <summary>
/// SQLite implementation of <see cref="IWorkspaceStore"/> (schema v3; Workspace Trash v7; Project Archive v8; Folder Archive and
/// Trash v9). Same conventions as <see cref="SqliteOwnerStore"/>: one pooled connection per call on a worker thread,
/// parameterized SQL, transactions for multi-row writes. Writes whose validity depends on other rows (parent checks, moves,
/// lifecycle) use <c>BEGIN IMMEDIATE</c> so the checked state cannot change before the write. The schema's indexes and triggers
/// back every rule up; rules about DESCENDANTS of a marked Folder are enforced here only (Phase 13 D9): every write evaluates
/// the effective lifecycle (<see cref="NodeLifecycle"/>) inside its own transaction.
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

    public Task<WorkspaceChildrenResult> ListChildrenAsync(long? parentId, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            // One read transaction: the parent's effective state and its rows come from the same snapshot (§7.9 C3).
            using var transaction = connection.BeginTransaction(deferred: true);
            if (parentId is { } id)
            {
                if (new NodeLifecycle(connection, transaction, MaxDepth).Chain(id) is not { } chain)
                    return WorkspaceChildrenResult.Missing;
                var state = NodeLifecycle.Of(chain);
                if (!state.IsLive)
                    return new WorkspaceChildrenResult(ChildrenReadStatus.ParentNotLive, [], state, NodeLifecycle.FromRoot(chain, state));
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT n.Id, n.ParentId, n.NodeType, n.Name,
                       EXISTS (SELECT 1 FROM WorkspaceNodes AS c WHERE c.ParentId = n.Id AND {Visible("c")}) AS HasChildren
                FROM WorkspaceNodes AS n
                WHERE n.ParentId IS $parent AND {Visible("n")}
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", MaxRowsPerQuery);
            var nodes = new List<WorkspaceNode>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                    nodes.Add(ReadShownNode(reader));
            }

            transaction.Commit();
            return WorkspaceChildrenResult.Listed(nodes);
        }, cancellationToken);

    public Task<WorkspaceNode> CreateFolderAsync(long? parentId, string name, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            RequireFolderParent(connection, transaction, parentId);
            RequireLive(new NodeLifecycle(connection, transaction, MaxDepth), parentId);
            var id = InsertNode(connection, transaction, parentId, Folder, name);
            transaction.Commit();
            return new WorkspaceNode(id, parentId, WorkspaceNodeType.Folder, name, false);
        }, cancellationToken);

    public Task<WorkspaceNode> CreateProjectAsync(long? parentId, string projectId, ProjectDraft draft, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            RequireFolderParent(connection, transaction, parentId);
            RequireLive(new NodeLifecycle(connection, transaction, MaxDepth), parentId);
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
            var lifecycle = new NodeLifecycle(connection, transaction, MaxDepth);
            RequireLive(lifecycle, nodeId); // a marked Folder, or anything below a marked Folder (§7.6: store only for descendants)

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE WorkspaceNodes SET Name = $name, NameKey = $key, UpdatedAt = $now WHERE Id = $id;";
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$key", WorkspaceName.Key(name));
            command.Parameters.AddWithValue("$now", Now());
            command.Parameters.AddWithValue("$id", nodeId);
            var changed = GuardNotLive(() => GuardTrashed(() => GuardDuplicateName(command.ExecuteNonQuery, name,
                () => Blocker(connection, transaction, state.ParentId, name, nodeId)), nodeId), nodeId, lifecycle) == 1;
            transaction.Commit();
            return changed;
        }, cancellationToken);

    public Task<ProjectDetails?> GetProjectAsync(long nodeId, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            // One read transaction: the details and their effective lifecycle come from the same snapshot.
            using var transaction = connection.BeginTransaction(deferred: true);
            var details = ReadProject(connection, transaction, nodeId);
            if (details is not null && new NodeLifecycle(connection, transaction, MaxDepth).Chain(nodeId) is { } chain)
            {
                var state = NodeLifecycle.Of(chain);
                details = details with { Effective = state, ChainFromRoot = NodeLifecycle.FromRoot(chain, state) };
            }

            transaction.Commit();
            return details;
        }, cancellationToken);

    public Task<ProjectDetails?> UpdateProjectAsync(long nodeId, ProjectDraft draft, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            var state = ReadState(connection, transaction, nodeId);
            if (state is { Trashed: true })
                throw new ProjectTrashedException(nodeId); // checked first: a trashed Project is read-only
            if (state is { Archived: true })
                throw new ProjectArchivedException(nodeId); // Phase 12 A3: a stale editor never silently unarchives
            if (state is not null)
                RequireLive(new NodeLifecycle(connection, transaction, MaxDepth), nodeId); // below an archived or trashed Folder

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
                        () => Blocker(connection, transaction, state!.ParentId, draft.Name!, nodeId)), nodeId) != 1)
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
            // Walking down from the roots and never entering the moving node excludes it and its whole subtree; never entering an
            // archived or trashed Folder excludes every Folder whose effective state is not Live (§7.5).
            command.CommandText = """
                WITH RECURSIVE folders (Id, Path, Depth) AS (
                    SELECT Id, Name, 0 FROM WorkspaceNodes
                    WHERE ParentId IS NULL AND NodeType = 'Folder' AND Id <> $moving AND FolderArchivedAt IS NULL AND FolderTrashedAt IS NULL
                    UNION ALL
                    SELECT c.Id, f.Path || ' / ' || c.Name, f.Depth + 1
                    FROM WorkspaceNodes AS c JOIN folders AS f ON c.ParentId = f.Id
                    WHERE c.NodeType = 'Folder' AND c.Id <> $moving AND c.FolderArchivedAt IS NULL AND c.FolderTrashedAt IS NULL AND f.Depth < $maxDepth
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
            var lifecycle = new NodeLifecycle(connection, transaction, MaxDepth);

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

            RequireLive(lifecycle, nodeId); // the source: a marked Folder, or anything below one, stays where it is

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

                // 4. The destination and its ancestors must be live (§7.5: an archived or trashed Folder is never a target).
                RequireLive(lifecycle, target);
            }

            // 5. Already there: nothing to write.
            if (currentParent == targetParentId)
                return false;

            // 6. The update, guarded by the parent read in step 1. A destination name conflict raises 2067.
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE WorkspaceNodes SET ParentId = $target, UpdatedAt = $now WHERE Id = $id AND ParentId IS $expected;";
            update.Parameters.AddWithValue("$target", (object?)targetParentId ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", Now());
            update.Parameters.AddWithValue("$id", nodeId);
            update.Parameters.AddWithValue("$expected", (object?)currentParent ?? DBNull.Value);
            var changed = GuardParentTrigger(() => GuardDuplicateName(update.ExecuteNonQuery, name,
                () => Blocker(connection, transaction, targetParentId, name, nodeId)), targetParentId ?? 0, lifecycle);
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
            if (!ParentState(connection, transaction, nodeId).IsLive)
                return ProjectTrashResult.Refused(ProjectTrashOutcome.AncestorNotLive); // below an archived or trashed Folder

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

            // 3. Strict: only into a live chain (§7.1); an archived or trashed ancestor refuses (no relocation).
            switch (ParentState(connection, transaction, nodeId).State)
            {
                case LifecycleState.Trashed:
                    return ProjectRestoreResult.Refused(ProjectRestoreOutcome.ParentTrashed);
                case LifecycleState.Archived:
                    return ProjectRestoreResult.Refused(ProjectRestoreOutcome.ParentArchived);
            }

            // 4. A LIVE sibling (Folder or Project) with the same name key refuses; nothing is renamed or re-parented.
            var name = ReadName(connection, transaction, nodeId);
            if (LiveSiblingName(connection, transaction, state.ParentId, nodeId) is { } blocker)
                return ProjectRestoreResult.Refused(ProjectRestoreOutcome.NameConflict, blocker);

            // 5. Clear the marker only.
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

            // 6. Read back: a Project has no tree children. An archived-plus-trashed Project (earlier build) comes back archived.
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
            // One read transaction: the roots, their effective state and their parents' paths come from the same snapshot.
            using var transaction = connection.BeginTransaction(deferred: true);
            var entries = ReadTrashRoots(connection, transaction, withPaths: true);
            transaction.Commit();
            return entries;
        }, cancellationToken);

    public Task<int> CountTrashAsync(CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var count = ReadTrashRoots(connection, transaction, withPaths: false).Count; // the rows the panel lists
            transaction.Commit();
            return count;
        }, cancellationToken);

    /// <summary>
    /// The Trash panel (§7.1): every node with T(x) and no T on a strict ancestor (the OUTERMOST trash roots, Projects and
    /// Folders), newest first. A root below an archived ancestor names it ("in archived folder …").
    /// </summary>
    private static List<WorkspaceTrashEntry> ReadTrashRoots(SqliteConnection connection, SqliteTransaction transaction, bool withPaths)
    {
        var lifecycle = new NodeLifecycle(connection, transaction, MaxDepth);
        var candidates = ReadCandidates(connection, transaction, lifecycle,
            "SELECT Id FROM WorkspaceNodes WHERE TrashedAt IS NOT NULL UNION SELECT Id FROM WorkspaceNodes WHERE FolderTrashedAt IS NOT NULL");
        var roots = new List<(LifecycleNodeRow Row, string? ArchivedAncestor)>();
        foreach (var candidate in candidates)
        {
            var chain = lifecycle.Chain(candidate.Id)!;
            if (NodeLifecycle.Of(chain).RootId != candidate.Id)
                continue; // inside an outer trash root: listed (and restored) with it
            var above = NodeLifecycle.OfParent(chain); // Live or Archived: no T above an outermost trash root
            roots.Add((candidate, above.State == LifecycleState.Archived ? lifecycle.Row(above.RootId!.Value)!.Name : null));
        }

        var paths = new Dictionary<long, string?>();
        return roots
            .OrderByDescending(r => r.Row.TrashedAt ?? r.Row.FolderTrashedAt, StringComparer.Ordinal)
            .ThenByDescending(r => r.Row.Id)
            .Take(MaxRowsPerQuery)
            .Select(r =>
            {
                string? path = null;
                if (withPaths && r.Row.ParentId is { } parent && !paths.TryGetValue(parent, out path))
                    paths[parent] = path = ReadPath(connection, transaction, parent);
                return new WorkspaceTrashEntry(r.Row.Id, r.Row.Name, r.Row.ParentId, path, ParseTime((r.Row.TrashedAt ?? r.Row.FolderTrashedAt)!))
                {
                    IsArchived = r.Row.Status == "Archived",
                    Type = r.Row.Type,
                    ArchivedAncestorName = r.ArchivedAncestor,
                };
            })
            .ToList();
    }

    /// <summary>Rows of the nodes <paramref name="idsSql"/> selects, remembered by the lifecycle reader (ordered by Id).</summary>
    private static List<LifecycleNodeRow> ReadCandidates(SqliteConnection connection, SqliteTransaction transaction, NodeLifecycle lifecycle, string idsSql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {NodeLifecycle.Columns} {NodeLifecycle.From} WHERE n.Id IN ({idsSql}) ORDER BY n.Id;";
        using var reader = command.ExecuteReader();
        var rows = new List<LifecycleNodeRow>();
        while (reader.Read())
        {
            var row = NodeLifecycle.Read(reader);
            lifecycle.Remember(row);
            rows.Add(row);
        }

        return rows;
    }

    public Task<FolderRemoveResult> RemoveEmptyFolderAsync(long nodeId, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);

            // 1. The node.
            if (ReadState(connection, transaction, nodeId) is not { } state)
                return FolderRemoveResult.Of(FolderRemoveOutcome.NotFound);
            if (state.Type != WorkspaceNodeType.Folder)
                return FolderRemoveResult.Of(FolderRemoveOutcome.NotAFolder);
            if (new NodeLifecycle(connection, transaction, MaxDepth).Effective(nodeId) is not { IsLive: true })
                return FolderRemoveResult.Of(FolderRemoveOutcome.NotLive); // only a live Folder is ever removed (V009 backs the marked row up)

            // 2. Every child row counts: visible nodes, archived Projects and Folders (Unarchive needs their parent) and trashed
            // Projects and Folders (strict restore needs their parent).
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
            if (state is not null && new NodeLifecycle(connection, transaction, MaxDepth).Chain(nodeId) is { } chain)
            {
                var (active, archived, trashed) = CountChildren(connection, transaction, nodeId);
                var effective = NodeLifecycle.Of(chain);
                state = state with
                {
                    ActiveChildren = active,
                    ArchivedChildren = archived,
                    TrashedChildren = trashed,
                    FolderArchived = chain[^1].FolderArchivedAt is not null,
                    FolderTrashed = chain[^1].FolderTrashedAt is not null,
                    Effective = effective,
                    ChainFromRoot = NodeLifecycle.FromRoot(chain, effective),
                };
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
            if (!ParentState(connection, transaction, nodeId).IsLive)
                return ProjectArchiveResult.Refused(ProjectArchiveOutcome.AncestorNotLive); // below an archived or trashed Folder

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

            // 2. Only an archive ROOT (§7.1: listed in the Archived panel): an ancestor that is archived or trashed refuses.
            switch (ParentState(connection, transaction, nodeId).State)
            {
                case LifecycleState.Trashed:
                    return ProjectUnarchiveResult.Refused(ProjectUnarchiveOutcome.ParentTrashed);
                case LifecycleState.Archived:
                    return ProjectUnarchiveResult.Refused(ProjectUnarchiveOutcome.ParentArchived);
            }

            // 3. Status only (state-based: whichever archive event is current); the serial is left as it is.
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
            switch (ParentState(connection, transaction, nodeId).State)
            {
                case LifecycleState.Trashed:
                    return ArchiveUndoResult.Refused(ArchiveUndoOutcome.ParentTrashed);
                case LifecycleState.Archived:
                    return ArchiveUndoResult.Refused(ArchiveUndoOutcome.ParentArchived);
            }

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
            // One read transaction: the roots, their effective state and their parents' paths come from the same snapshot.
            using var transaction = connection.BeginTransaction(deferred: true);
            var entries = ReadArchivedRoots(connection, transaction, withPaths: true);
            transaction.Commit();
            return entries;
        }, cancellationToken);

    public Task<int> CountArchivedAsync(CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var count = ReadArchivedRoots(connection, transaction, withPaths: false).Count; // the rows the panel lists
            transaction.Commit();
            return count;
        }, cancellationToken);

    /// <summary>
    /// The Archived panel (§7.1): every node with A(x), no T anywhere on its chain and no A on a strict ancestor (the OUTERMOST
    /// archive roots that are not trashed, Folders and Projects), in tree order.
    /// </summary>
    private static List<ArchivedProjectEntry> ReadArchivedRoots(SqliteConnection connection, SqliteTransaction transaction, bool withPaths)
    {
        var lifecycle = new NodeLifecycle(connection, transaction, MaxDepth);
        var candidates = ReadCandidates(connection, transaction, lifecycle,
            "SELECT Id FROM WorkspaceNodes WHERE FolderArchivedAt IS NOT NULL UNION SELECT NodeId FROM Projects WHERE Status = 'Archived'");
        var paths = new Dictionary<long, string?>();
        var entries = new List<ArchivedProjectEntry>();
        foreach (var candidate in candidates)
        {
            var state = NodeLifecycle.Of(lifecycle.Chain(candidate.Id)!);
            if (state.State != LifecycleState.Archived || state.RootId != candidate.Id)
                continue; // trashed (itself or above), or inside an outer archive root
            string? path = null;
            if (withPaths && candidate.ParentId is { } parent && !paths.TryGetValue(parent, out path))
                paths[parent] = path = ReadPath(connection, transaction, parent);
            entries.Add(new ArchivedProjectEntry(candidate.Id, candidate.Name, candidate.ParentId, path, ParseTime(ReadUpdatedAt(connection, transaction, candidate.Id)))
            {
                Type = candidate.Type,
                ArchivedAt = candidate.FolderArchivedAt is { } at ? ParseTime(at) : null,
            });
        }

        return entries.OrderBy(e => (e.Type, e.Name, e.NodeId), WorkspaceNodeOrder.Instance).Take(MaxRowsPerQuery).ToList();
    }

    // ---- Folder Archive and Folder Trash (schema v9; Phase 13 proposal revision 4 §7.2-§7.4) ----------------------------------

    public Task<FolderLifecyclePreview> PrepareFolderLifecycleAsync(long nodeId, FolderLifecycleKind kind, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var chain = new NodeLifecycle(connection, transaction, MaxDepth).Chain(nodeId);
            var preview = MarkRefusal(chain, kind) is { } refusal
                ? FolderLifecyclePreview.Refused(refusal.Outcome, refusal.Blocker)
                : new FolderLifecyclePreview(FolderLifecycleConfirmations.Compute(connection, transaction, chain!, kind, MaxDepth), null);
            transaction.Commit();
            return preview;
        }, cancellationToken);

    public Task<FolderLifecycleResult> ArchiveFolderAsync(long nodeId, string confirmationToken, CancellationToken cancellationToken = default) =>
        MarkFolderAsync(nodeId, FolderLifecycleKind.Archive, confirmationToken, cancellationToken);

    public Task<FolderLifecycleResult> TrashFolderAsync(long nodeId, string confirmationToken, CancellationToken cancellationToken = default) =>
        MarkFolderAsync(nodeId, FolderLifecycleKind.Trash, confirmationToken, cancellationToken);

    public Task<FolderClearResult> UnarchiveFolderAsync(long nodeId, CancellationToken cancellationToken = default) =>
        ClearFolderAsync(nodeId, FolderLifecycleKind.Archive, serial: null, cancellationToken);

    public Task<FolderClearResult> RestoreFolderAsync(long nodeId, CancellationToken cancellationToken = default) =>
        ClearFolderAsync(nodeId, FolderLifecycleKind.Trash, serial: null, cancellationToken);

    public Task<FolderClearResult> UndoFolderLifecycleAsync(FolderLifecycleEvent lifecycleEvent, CancellationToken cancellationToken = default) =>
        ClearFolderAsync(lifecycleEvent.NodeId, lifecycleEvent.Kind, lifecycleEvent.Serial, cancellationToken);

    /// <summary>Why the Folder cannot be archived or trashed now; null when it can (a live Folder under a live chain).</summary>
    private static (FolderLifecycleOutcome Outcome, EffectiveLifecycle? Blocker)? MarkRefusal(IReadOnlyList<LifecycleNodeRow>? chain, FolderLifecycleKind kind)
    {
        if (chain is null)
            return (FolderLifecycleOutcome.NotFound, null);
        var folder = chain[^1];
        if (folder.Type != WorkspaceNodeType.Folder)
            return (FolderLifecycleOutcome.NotAFolder, null);
        if (folder.FolderTrashedAt is not null)
            return (FolderLifecycleOutcome.FolderTrashed, null);
        if (folder.FolderArchivedAt is not null)
            return (kind == FolderLifecycleKind.Archive ? FolderLifecycleOutcome.AlreadyArchived : FolderLifecycleOutcome.FolderArchived, null);
        var above = NodeLifecycle.OfParent(chain);
        return above.IsLive ? null : (FolderLifecycleOutcome.AncestorNotLive, above);
    }

    private Task<FolderLifecycleResult> MarkFolderAsync(long nodeId, FolderLifecycleKind kind, string confirmationToken, CancellationToken cancellationToken) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);

            // 1. The Folder and its chain, as they are now (under the write lock): Effective must be Live.
            var chain = new NodeLifecycle(connection, transaction, MaxDepth).Chain(nodeId);
            if (MarkRefusal(chain, kind) is { } refusal)
                return FolderLifecycleResult.Refused(refusal.Outcome, refusal.Blocker);

            // 2. The confirmation, recomputed in THIS transaction: anything it describes that changed refuses (§7.4). Nothing is
            // written and the fresh preview goes back for a NEW explicit confirmation; it is never resubmitted.
            var confirmation = FolderLifecycleConfirmations.Compute(connection, transaction, chain!, kind, MaxDepth);
            if (!string.Equals(confirmation.Token, confirmationToken, StringComparison.Ordinal))
                return new FolderLifecycleResult(FolderLifecycleOutcome.ConfirmationStale, null, confirmation);

            // 3. The marker and the serial only, guarded by what step 1 read. THIS statement raises the serial; the V009 triggers
            // validate it and refuse any other change in the same statement.
            long serial;
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = $"""
                    UPDATE WorkspaceNodes SET {MarkerColumn(kind)} = $at, LifecycleSerial = LifecycleSerial + 1
                    WHERE Id = $id AND NodeType = 'Folder' AND FolderArchivedAt IS NULL AND FolderTrashedAt IS NULL AND LifecycleSerial = $observed
                    RETURNING LifecycleSerial;
                    """;
                update.Parameters.AddWithValue("$at", Now());
                update.Parameters.AddWithValue("$id", nodeId);
                update.Parameters.AddWithValue("$observed", chain![^1].LifecycleSerial);
                serial = update.ExecuteScalar() is long committed
                    ? committed
                    : throw new InvalidOperationException("A live Folder was not marked under the write lock; nothing was changed.");
            }

            transaction.Commit();
            return new FolderLifecycleResult(FolderLifecycleOutcome.Committed, new FolderLifecycleEvent(nodeId, kind, serial), null);
        }, cancellationToken);

    /// <param name="serial">Undo: exactly this event; null for the state-based Unarchive / Restore.</param>
    private Task<FolderClearResult> ClearFolderAsync(long nodeId, FolderLifecycleKind kind, long? serial, CancellationToken cancellationToken) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false);

            // 1. The Folder (cleared elsewhere → NotArchived / NotTrashed; a later event → MarkedAgain; nothing written).
            var chain = new NodeLifecycle(connection, transaction, MaxDepth).Chain(nodeId);
            if (chain is null)
                return FolderClearResult.Refused(FolderClearOutcome.NotFound);
            var folder = chain[^1];
            if (folder.Type != WorkspaceNodeType.Folder)
                return FolderClearResult.Refused(FolderClearOutcome.NotAFolder);
            if ((kind == FolderLifecycleKind.Archive ? folder.FolderArchivedAt : folder.FolderTrashedAt) is null)
                return FolderClearResult.Refused(kind == FolderLifecycleKind.Archive ? FolderClearOutcome.NotArchived : FolderClearOutcome.NotTrashed);
            if (serial is { } expected && folder.LifecycleSerial != expected)
                return FolderClearResult.Refused(FolderClearOutcome.MarkedAgain);

            // 2. Strict: the ORIGINAL parent (never removable while it holds the Folder; a broken chain already failed closed) must
            // be reached through a live chain (§7.1). No parent is created, nothing is relocated.
            if (folder.ParentId is not null && chain[^2].Type != WorkspaceNodeType.Folder)
                throw new InvalidDataException($"The parent of folder {nodeId} is not a folder; nothing was changed.");
            switch (NodeLifecycle.OfParent(chain).State)
            {
                case LifecycleState.Trashed:
                    return FolderClearResult.Refused(FolderClearOutcome.ParentTrashed);
                case LifecycleState.Archived:
                    return FolderClearResult.Refused(FolderClearOutcome.ParentArchived);
            }

            // 3. Restore only: a live sibling with the same name key refuses; nothing is renamed (an archived Folder kept its name).
            if (kind == FolderLifecycleKind.Trash && LiveSiblingName(connection, transaction, folder.ParentId, nodeId) is { } blocker)
                return FolderClearResult.Refused(FolderClearOutcome.NameConflict, blocker);

            // 4. Clear ONE marker on ONE row; every descendant marker is untouched (independent states re-emerge as they were).
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                var column = MarkerColumn(kind);
                update.CommandText = serial is null
                    ? $"UPDATE WorkspaceNodes SET {column} = NULL WHERE Id = $id AND {column} IS NOT NULL;"
                    : $"UPDATE WorkspaceNodes SET {column} = NULL WHERE Id = $id AND {column} IS NOT NULL AND LifecycleSerial = $serial;";
                update.Parameters.AddWithValue("$id", nodeId);
                if (serial is { } s)
                    update.Parameters.AddWithValue("$serial", s);
                try
                {
                    if (update.ExecuteNonQuery() != 1)
                        throw new InvalidOperationException("A marked Folder was not cleared under the write lock; nothing was changed.");
                }
                catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintUnique)
                {
                    return FolderClearResult.Refused(FolderClearOutcome.NameConflict); // backstop: Dispose rolls back
                }
            }

            // 5. The Folder as it now appears in the tree (collapsed; its shown children load lazily).
            using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = $"""
                SELECT n.Id, n.ParentId, n.NodeType, n.Name,
                       EXISTS (SELECT 1 FROM WorkspaceNodes AS c WHERE c.ParentId = n.Id AND {Visible("c")}) AS HasChildren
                FROM WorkspaceNodes AS n WHERE n.Id = $id;
                """;
            read.Parameters.AddWithValue("$id", nodeId);
            WorkspaceNode node;
            using (var reader = read.ExecuteReader())
            {
                reader.Read();
                node = ReadShownNode(reader);
            }

            var restored = new RestoredFolder(node, ReadPath(connection, transaction, folder.ParentId));
            transaction.Commit();
            return new FolderClearResult(FolderClearOutcome.Cleared, restored, null);
        }, cancellationToken);

    private static string MarkerColumn(FolderLifecycleKind kind) => kind == FolderLifecycleKind.Archive ? "FolderArchivedAt" : "FolderTrashedAt";

    /// <summary>The name of a LIVE sibling (not trashed: Folders and Projects share one namespace) holding the node's name key.</summary>
    private static string? LiveSiblingName(SqliteConnection connection, SqliteTransaction transaction, long? parentId, long nodeId)
    {
        using var conflict = connection.CreateCommand();
        conflict.Transaction = transaction;
        conflict.CommandText = """
            SELECT Name FROM WorkspaceNodes
            WHERE ParentId IS $parent AND TrashedAt IS NULL AND FolderTrashedAt IS NULL AND Id <> $id
              AND NameKey = (SELECT NameKey FROM WorkspaceNodes WHERE Id = $id)
            LIMIT 1;
            """;
        conflict.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
        conflict.Parameters.AddWithValue("$id", nodeId);
        return conflict.ExecuteScalar() as string;
    }

    /// <summary>The effective lifecycle of the node's parent chain (its strict ancestors); Live at the top level.</summary>
    private static EffectiveLifecycle ParentState(SqliteConnection connection, SqliteTransaction transaction, long nodeId) =>
        new NodeLifecycle(connection, transaction, MaxDepth).Chain(nodeId) is { } chain ? NodeLifecycle.OfParent(chain) : EffectiveLifecycle.Live;

    /// <summary>Store enforcement (§7.6): the node (null = the top level) must be effectively Live, or nothing is written.</summary>
    private static void RequireLive(NodeLifecycle lifecycle, long? nodeId)
    {
        if (nodeId is { } id && lifecycle.Effective(id) is { IsLive: false } state)
            throw new WorkspaceNotLiveException(id, state);
    }

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

    private static string ReadUpdatedAt(SqliteConnection connection, SqliteTransaction transaction, long nodeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT UpdatedAt FROM WorkspaceNodes WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", nodeId);
        return (string)command.ExecuteScalar()!;
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

    /// <returns>
    /// Direct child rows: visible (shown in the tree), archived (Projects and Folders that are not trashed), and trashed (Projects
    /// and Folders).
    /// </returns>
    private static (int Active, int Archived, int Trashed) CountChildren(SqliteConnection connection, SqliteTransaction transaction, long nodeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT count(*) FILTER (WHERE {Visible("n")}),
                   count(*) FILTER (WHERE n.TrashedAt IS NULL AND n.FolderTrashedAt IS NULL AND (n.FolderArchivedAt IS NOT NULL OR p.Status IS 'Archived')),
                   count(*) FILTER (WHERE n.TrashedAt IS NOT NULL OR n.FolderTrashedAt IS NOT NULL)
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

    /// <summary>Defense in depth: the explicit effective-state check normally reports a marked Folder first (V009 trigger 8).</summary>
    private static T GuardNotLive<T>(Func<T> execute, long nodeId, NodeLifecycle lifecycle)
    {
        try
        {
            return execute();
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintTrigger && ex.Message.Contains("WH_FOLDER_NOT_LIVE", StringComparison.Ordinal))
        {
            throw new WorkspaceNotLiveException(nodeId, lifecycle.Effective(nodeId) ?? EffectiveLifecycle.Live, ex);
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
            () => Blocker(connection, transaction, parentId, name, excludingId: 0)), parentId ?? 0, new NodeLifecycle(connection, transaction, MaxDepth));
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

    /// <param name="blocker">Read after the refused statement, inside the same transaction: is the sibling that holds the name an
    /// archived Project or an archived Folder (hidden from the tree)?</param>
    private static T GuardDuplicateName<T>(Func<T> execute, string name, Func<(bool Project, bool Folder)> blocker)
    {
        try
        {
            return execute();
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintUnique)
        {
            var (archivedProject, archivedFolder) = blocker();
            throw new DuplicateWorkspaceNameException(name, ex) { BlockerArchived = archivedProject, BlockerArchivedFolder = archivedFolder };
        }
    }

    /// <summary>
    /// The LIVE sibling under <paramref name="parentId"/> with the name key of <paramref name="name"/> is an archived Project, or an
    /// archived Folder (D10: an archived Folder keeps its name reserved).
    /// </summary>
    private static (bool Project, bool Folder) Blocker(SqliteConnection connection, SqliteTransaction transaction, long? parentId, string name, long excludingId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (
                       SELECT 1 FROM WorkspaceNodes AS n JOIN Projects AS p ON p.NodeId = n.Id
                       WHERE n.ParentId IS $parent AND n.NameKey = $key AND n.TrashedAt IS NULL AND n.Id <> $id AND p.Status = 'Archived'),
                   EXISTS (
                       SELECT 1 FROM WorkspaceNodes AS n
                       WHERE n.ParentId IS $parent AND n.NameKey = $key AND n.FolderTrashedAt IS NULL AND n.Id <> $id AND n.FolderArchivedAt IS NOT NULL);
            """;
        command.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$key", WorkspaceName.Key(name));
        command.Parameters.AddWithValue("$id", excludingId);
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0) == 1, reader.GetInt64(1) == 1);
    }

    /// <summary>
    /// SQL condition: the node aliased <paramref name="alias"/> is shown in the tree when its parent is — no own lifecycle marker:
    /// not in the Workspace Trash, not an archived Project (Phase 12), not an archived or trashed Folder (schema v9). Shared by tree
    /// listings and the search store's reveal branches.
    /// </summary>
    internal static string Visible(string alias) =>
        $"{alias}.TrashedAt IS NULL AND {alias}.FolderTrashedAt IS NULL AND {alias}.FolderArchivedAt IS NULL"
        + $" AND NOT EXISTS (SELECT 1 FROM Projects AS ap WHERE ap.NodeId = {alias}.Id AND ap.Status = 'Archived')";

    /// <summary>Defense in depth: the explicit parent checks normally report these first.</summary>
    private static T GuardParentTrigger<T>(Func<T> execute, long parentId, NodeLifecycle lifecycle)
    {
        try
        {
            return execute();
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintTrigger && ex.Message.Contains("WH_PARENT_NOT_FOLDER", StringComparison.Ordinal))
        {
            throw new WorkspaceParentException(parentId, WorkspaceParentProblem.NotAFolder, ex);
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintTrigger && ex.Message.Contains("WH_PARENT_NOT_LIVE", StringComparison.Ordinal))
        {
            throw new WorkspaceNotLiveException(parentId, lifecycle.Effective(parentId) ?? EffectiveLifecycle.Live, ex);
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintForeignKey)
        {
            throw new WorkspaceParentException(parentId, WorkspaceParentProblem.NotFound, ex);
        }
    }

    private static WorkspaceNode ReadShownNode(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.IsDBNull(1) ? null : reader.GetInt64(1),
        ParseType(reader.GetString(2)),
        reader.GetString(3),
        reader.GetInt64(4) == 1);

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
