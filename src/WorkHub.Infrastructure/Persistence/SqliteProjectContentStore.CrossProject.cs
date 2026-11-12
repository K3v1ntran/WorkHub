using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Infrastructure.Backup;

namespace WorkHub.Infrastructure.Persistence;

/// <summary>
/// Phase 15: Copy / Move one content item to ANOTHER Project (proposal Revision 2 with the §0.3 update).
/// <list type="bullet">
/// <item>The plan is computed by one read inside the caller's transaction: the preview uses a deferred read transaction, the
/// commit recomputes it under its own <c>BEGIN IMMEDIATE</c> lock. Typed refusals come first, in the §3.6 order; only a Ready plan
/// has a token, and a token mismatch at commit is <see cref="CrossProjectOutcome.ConfirmationStale"/>.</item>
/// <item>The token (<see cref="TokenVersion"/>) binds everything displayed and copied: mode, identities, both displayed paths and
/// effective states, the confirmed root name, the counts, and every copied row with its parent, depth, fields and
/// <c>CreatedAt</c> (note contents as SHA-256). It does not use <c>Revision</c> or <c>UpdatedAt</c>.</item>
/// <item>The copy only INSERTS rows (the <c>*_Immutable</c> triggers are never touched); Move then runs the transaction-scoped Move
/// to Trash steps on the source root in the same transaction. Any exception rolls everything back.</item>
/// </list>
/// Nothing is logged here; tokens and payloads never leave this class except as the hash.
/// </summary>
public sealed partial class SqliteProjectContentStore
{
    internal const string TokenVersion = "P15-XPROJ-1";

    public Task<IReadOnlyList<CrossProjectTarget>> ListCrossProjectTargetsAsync(long sourceProjectNodeId, CancellationToken cancellationToken = default) =>
        Run<IReadOnlyList<CrossProjectTarget>>(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var ids = new List<long>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT n.Id FROM WorkspaceNodes AS n JOIN Projects AS p ON p.NodeId = n.Id
                    WHERE n.TrashedAt IS NULL AND n.Id <> $source
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$source", sourceProjectNodeId);
                command.Parameters.AddWithValue("$limit", MaxRowsPerQuery);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    ids.Add(reader.GetInt64(0));
            }

            var lifecycle = new NodeLifecycle(connection, transaction, SqliteWorkspaceStore.MaxDepth);
            var targets = new List<CrossProjectTarget>();
            foreach (var id in ids)
            {
                IReadOnlyList<LifecycleNodeRow>? chain;
                try
                {
                    chain = lifecycle.Chain(id);
                }
                catch (InvalidDataException)
                {
                    continue; // an unverifiable chain is never offered (fails closed)
                }

                if (chain is null)
                    continue;
                var state = NodeLifecycle.Of(chain);
                if (state.State == LifecycleState.Trashed)
                    continue;
                targets.Add(new CrossProjectTarget(id, string.Join(" › ", chain.Select(r => r.Name)), state.State == LifecycleState.Archived));
            }

            transaction.Commit();
            return targets.OrderBy(t => t.Path, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.ProjectNodeId).ToList();
        }, cancellationToken);

    public Task<CrossProjectPreview> PrepareCrossProjectAsync(CrossProjectRequest request, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var plan = PlanCrossProject(connection, transaction, request);
            transaction.Commit();
            return plan.ToPreview();
        }, cancellationToken);

    public Task<CrossProjectResult> CrossProjectAsync(CrossProjectRequest request, string confirmedToken, CancellationToken cancellationToken = default) =>
        Run(connection =>
        {
            SqliteTransaction transaction;
            try
            {
                transaction = connection.BeginTransaction(deferred: false);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCodes.Busy or SqliteCodes.Locked)
            {
                return new CrossProjectResult(CrossProjectOutcome.DatabaseBusy);
            }

            using (transaction)
            {
                OnWriteStep?.Invoke("cross:locked");

                // 1. Revalidate under the lock: typed refusals first (D-15.8), then the token.
                var plan = PlanCrossProject(connection, transaction, request);
                if (plan.Status != CrossProjectStatus.Ready)
                    return new CrossProjectResult(CrossProjectOutcome.Refused) { Refusal = plan.Status, RootName = plan.RootName };
                if (!string.Equals(plan.Token, confirmedToken, StringComparison.Ordinal))
                    return new CrossProjectResult(CrossProjectOutcome.ConfirmationStale) { RootName = plan.RootName };

                // 2. The copy: folders parents first (target-owned parent mapping), then links, then notes at Revision 1.
                var now = Now();
                var target = request.TargetProjectNodeId;
                var map = new Dictionary<long, long>();
                ContentRef newRoot;
                try
                {
                    InsertFolders(connection, transaction, plan, target, request.TargetFolderId, now, map);
                }
                catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintUnique)
                {
                    // Backstop only: the plan checked the name under this lock. Dispose rolls back.
                    return new CrossProjectResult(CrossProjectOutcome.Refused) { Refusal = CrossProjectStatus.NameConflict, RootName = plan.RootName };
                }

                OnWriteStep?.Invoke("cross:folders");
                var linkIds = InsertCopies(connection, transaction, plan.Links, "ProjectLinks",
                    "INSERT INTO ProjectLinks (ProjectNodeId, FolderId, LinkType, Name, Target, CreatedAt, UpdatedAt) SELECT $project, $folder, LinkType, Name, Target, CreatedAt, $now FROM ProjectLinks WHERE Id = $source AND TrashEntryId IS NULL RETURNING Id;",
                    target, request.TargetFolderId, now, map);
                OnWriteStep?.Invoke("cross:links");
                var noteIds = InsertCopies(connection, transaction, plan.Notes, "ProjectNotes",
                    "INSERT INTO ProjectNotes (ProjectNodeId, FolderId, Title, Content, Revision, CreatedAt, UpdatedAt) SELECT $project, $folder, Title, Content, 1, CreatedAt, $now FROM ProjectNotes WHERE Id = $source AND TrashEntryId IS NULL RETURNING Id;",
                    target, request.TargetFolderId, now, map);
                OnWriteStep?.Invoke("cross:notes");

                newRoot = request.Source.Kind switch
                {
                    ContentKind.Folder => new ContentRef(ContentKind.Folder, map[request.Source.Id]),
                    ContentKind.Link => new ContentRef(ContentKind.Link, linkIds[0]),
                    _ => new ContentRef(ContentKind.Note, noteIds[0]),
                };

                // 3. Move: the source root goes to ITS Project's Trash with the same steps as Move to Trash, in this transaction.
                long? entryId = null;
                if (request.Mode == CrossProjectMode.Move)
                {
                    var trashed = TrashInTransaction(connection, transaction, request.Source);
                    if (trashed.Outcome != TrashOutcome.Trashed || trashed.Entry!.ItemCount != plan.Rows)
                        throw new InvalidOperationException($"Move: the source Trash step returned {trashed.Outcome}; nothing was changed.");
                    entryId = trashed.Entry.Id;
                }

                transaction.Commit();
                OnWriteStep?.Invoke("cross:committed");
                return new CrossProjectResult(request.Mode == CrossProjectMode.Move ? CrossProjectOutcome.Moved : CrossProjectOutcome.Copied)
                {
                    NewRoot = newRoot,
                    TargetProjectNodeId = target,
                    TargetPath = plan.TargetPath,
                    RootName = plan.RootName,
                    Rows = plan.Rows,
                    SourceTrashEntryId = entryId,
                    SourceProjectName = plan.SourceProjectName,
                };
            }
        }, cancellationToken);

    // ---- Plan --------------------------------------------------------------------------------------------------------------

    private sealed record FolderRow(long Id, long? Parent, int Depth, string Name, string NameKey, string CreatedAt);

    private sealed record ItemRow(long Id, long? Folder);

    private sealed class CrossPlan(CrossProjectStatus status)
    {
        public CrossProjectStatus Status { get; set; } = status;
        public string? Token { get; set; }
        public string RootName { get; set; } = string.Empty;
        public string? SuggestedName { get; set; }
        public List<FolderRow> Folders { get; } = [];
        public List<ItemRow> Links { get; } = [];
        public List<ItemRow> Notes { get; } = [];
        public int SkippedItems { get; set; }
        public int SkippedEntries { get; set; }
        public long PayloadBytes { get; set; }
        public int FolderCount { get; set; }
        public int LinkCount { get; set; }
        public int NoteCount { get; set; }
        public string SourcePath { get; set; } = string.Empty;
        public string TargetPath { get; set; } = string.Empty;
        public string SourceProjectName { get; set; } = string.Empty;
        public string SourceName { get; set; } = string.Empty;
        public bool TargetArchived { get; set; }
        public int Rows => FolderCount + LinkCount + NoteCount;

        public CrossProjectPreview ToPreview() => new(Status)
        {
            Token = Token,
            RootName = RootName,
            SuggestedName = SuggestedName,
            Folders = FolderCount,
            Links = LinkCount,
            Notes = NoteCount,
            SkippedItems = SkippedItems,
            SkippedEntries = SkippedEntries,
            PayloadBytes = PayloadBytes,
            SourcePath = SourcePath,
            TargetPath = TargetPath,
            SourceProjectName = SourceProjectName,
            SourceName = SourceName,
            TargetArchived = TargetArchived,
        };
    }

    /// <summary>The plan, evaluated in the §3.6 order. Each refusal returns at once; only a Ready plan carries rows and a token.</summary>
    private CrossPlan PlanCrossProject(SqliteConnection connection, SqliteTransaction transaction, CrossProjectRequest request)
    {
        var source = request.Source;

        // 1-3. Source row, source Project's effective state, source marker.
        var root = ReadRoot(connection, transaction, source);
        if (root is null)
            return new CrossPlan(CrossProjectStatus.SourceNotFound);
        var lifecycle = new NodeLifecycle(connection, transaction, SqliteWorkspaceStore.MaxDepth);
        var sourceChain = lifecycle.Chain(root.ProjectNodeId);
        if (sourceChain is null)
            return new CrossPlan(CrossProjectStatus.SourceNotFound);
        var sourceState = NodeLifecycle.Of(sourceChain);
        if (sourceState.State == LifecycleState.Trashed)
            return new CrossPlan(CrossProjectStatus.SourceProjectTrashed);
        if (root.Trashed)
            return new CrossPlan(CrossProjectStatus.SourceTrashed);

        // 4-6. Target Project: exists, is another Project, is not effectively trashed (itself or any ancestor Folder).
        var targetProject = request.TargetProjectNodeId;
        if (ScalarLong(connection, transaction, "SELECT count(*) FROM Projects WHERE NodeId = $id;", ("$id", targetProject)) == 0)
            return new CrossPlan(CrossProjectStatus.TargetProjectNotFound);
        if (targetProject == root.ProjectNodeId)
            return new CrossPlan(CrossProjectStatus.SameProject);
        var targetChain = lifecycle.Chain(targetProject);
        if (targetChain is null)
            return new CrossPlan(CrossProjectStatus.TargetProjectNotFound);
        var targetState = NodeLifecycle.Of(targetChain);
        if (targetState.State == LifecycleState.Trashed)
            return new CrossPlan(CrossProjectStatus.TargetProjectTrashed);

        // 7. Destination folder: exists, belongs to the target Project, is active.
        if (FolderProblem(connection, transaction, targetProject, request.TargetFolderId) is { } folderProblem)
        {
            return new CrossPlan(folderProblem switch
            {
                ContentContainerProblem.WrongProject => CrossProjectStatus.TargetFolderWrongProject,
                ContentContainerProblem.FolderTrashed => CrossProjectStatus.TargetFolderTrashed,
                _ => CrossProjectStatus.TargetFolderNotFound,
            });
        }

        // 8-9. The root name at the destination: folders use the existing Workspace name rules and the active-sibling NameKey scope.
        var rootName = root.Name;
        string? rootKey = null;
        if (source.Kind == ContentKind.Folder)
        {
            if (request.RootName is not null)
            {
                if (!WorkspaceName.TryNormalize(request.RootName, out var normalized, out _))
                    return new CrossPlan(CrossProjectStatus.InvalidName) { RootName = request.RootName.Trim() };
                rootName = normalized;
            }

            rootKey = WorkspaceName.Key(rootName);
            if (ActiveFolderKeyExists(connection, transaction, targetProject, request.TargetFolderId, rootKey))
                return new CrossPlan(CrossProjectStatus.NameConflict) { RootName = rootName, SuggestedName = SuggestName(connection, transaction, targetProject, request.TargetFolderId, rootName) };
        }

        var plan = new CrossPlan(CrossProjectStatus.Ready) { RootName = rootName };

        // 10. The exact active subtree (walk through active folders only, bounded; fails closed like Move to Trash).
        if (source.Kind == ContentKind.Folder)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    WITH RECURSIVE subtree (Id, Parent, Depth) AS (
                        SELECT Id, NULL, 0 FROM ProjectFolders WHERE Id = $root
                        UNION ALL
                        SELECT c.Id, c.ParentFolderId, s.Depth + 1
                        FROM ProjectFolders AS c JOIN subtree AS s ON c.ParentFolderId = s.Id
                        WHERE c.TrashEntryId IS NULL AND s.Depth < $maxDepth
                    )
                    SELECT s.Id, s.Parent, s.Depth, f.Name, f.NameKey, f.CreatedAt
                    FROM subtree AS s JOIN ProjectFolders AS f ON f.Id = s.Id
                    ORDER BY s.Depth, s.Id;
                    """;
                command.Parameters.AddWithValue("$root", source.Id);
                command.Parameters.AddWithValue("$maxDepth", _maxDepth);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var depth = (int)reader.GetInt64(2);
                    plan.Folders.Add(new FolderRow(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), depth,
                        depth == 0 ? rootName : reader.GetString(3), depth == 0 ? rootKey! : reader.GetString(4), reader.GetString(5)));
                }
            }

            var deepest = plan.Folders.Where(f => f.Depth >= _maxDepth).Select(f => f.Id).ToList();
            if (deepest.Count > 0 && CountIn(connection, transaction, "SELECT count(*) FROM ProjectFolders WHERE ParentFolderId IN (SELECT value FROM json_each($ids)) AND TrashEntryId IS NULL;", JsonIds(deepest)) > 0)
                return new CrossPlan(CrossProjectStatus.SourceUnverified) { RootName = rootName };

            // 11. Depth after grafting (§6.1): the deepest copied folder must stay within MaxDepth + 1 folders of the Project root,
            // which is what the breadcrumb, ancestry and Move-target walks can read; an unverifiable destination chain fails closed.
            var height = plan.Folders.Max(f => f.Depth) + 1;
            var destinationLevel = request.TargetFolderId is { } targetFolder ? FolderLevel(connection, transaction, targetFolder) : 0;
            if (destinationLevel is null || destinationLevel.Value + height > _maxDepth + 1)
                return new CrossPlan(CrossProjectStatus.DepthExceeded) { RootName = rootName };
        }

        // 12. Counts and the UTF-8 payload, summed in SQL BEFORE any payload is loaded; oversized operations are refused in full.
        var folderIds = JsonIds(plan.Folders.Select(f => f.Id));
        long payload;
        switch (source.Kind)
        {
            case ContentKind.Folder:
                plan.FolderCount = plan.Folders.Count;
                payload = Encoding.UTF8.GetByteCount(rootName)
                          + ScalarLong(connection, transaction, "SELECT coalesce(sum(length(CAST(Name AS BLOB))), 0) FROM ProjectFolders WHERE Id IN (SELECT value FROM json_each($ids)) AND Id <> $root;", ("$ids", folderIds), ("$root", source.Id));
                var (links, linkBytes) = CountAndBytes(connection, transaction, "SELECT count(*), coalesce(sum(length(CAST(Name AS BLOB)) + length(CAST(Target AS BLOB))), 0) FROM ProjectLinks WHERE FolderId IN (SELECT value FROM json_each($ids)) AND TrashEntryId IS NULL;", folderIds);
                var (notes, noteBytes) = CountAndBytes(connection, transaction, "SELECT count(*), coalesce(sum(length(CAST(Title AS BLOB)) + length(CAST(Content AS BLOB))), 0) FROM ProjectNotes WHERE FolderId IN (SELECT value FROM json_each($ids)) AND TrashEntryId IS NULL;", folderIds);
                plan.LinkCount = links;
                plan.NoteCount = notes;
                payload += linkBytes + noteBytes;
                break;
            case ContentKind.Link:
                plan.LinkCount = 1;
                payload = ScalarLong(connection, transaction, "SELECT length(CAST(Name AS BLOB)) + length(CAST(Target AS BLOB)) FROM ProjectLinks WHERE Id = $root;", ("$root", source.Id));
                break;
            default:
                plan.NoteCount = 1;
                payload = ScalarLong(connection, transaction, "SELECT length(CAST(Title AS BLOB)) + length(CAST(Content AS BLOB)) FROM ProjectNotes WHERE Id = $root;", ("$root", source.Id));
                break;
        }

        plan.PayloadBytes = payload;

        // Skipped Trash: every content row BELOW the root, reached through any folder, that carries a marker; each row once.
        if (source.Kind == ContentKind.Folder)
            ReadSkipped(connection, transaction, source.Id, plan);

        // Displayed paths (also bound by the token).
        var sourceCrumbs = root.ContainerFolderId is { } sourceContainer ? ReadBreadcrumb(connection, transaction, sourceContainer) : [];
        var targetCrumbs = request.TargetFolderId is { } targetContainer ? ReadBreadcrumb(connection, transaction, targetContainer) : [];
        plan.SourceProjectName = sourceChain[^1].Name;
        plan.SourceName = root.Name;
        plan.SourcePath = string.Join(" › ", sourceChain.Select(r => r.Name).Concat(sourceCrumbs.Select(c => c.Name)));
        plan.TargetPath = string.Join(" › ", targetChain.Select(r => r.Name).Concat(targetCrumbs.Select(c => c.Name)));
        plan.TargetArchived = targetState.State == LifecycleState.Archived;

        if (plan.Rows > CrossProjectLimits.MaxRows || payload > CrossProjectLimits.MaxPayloadBytes)
        {
            plan.Status = CrossProjectStatus.TooLarge;
            plan.Folders.Clear();
            return plan;
        }

        // 13. The token: everything displayed and copied, in a fixed order, length-prefixed.
        using var token = new StreamingTokenWriter();
        token.Text(TokenVersion).Text(request.Mode.ToString()).Text(source.Kind.ToString()).Integer(source.Id);
        token.Integer(root.ProjectNodeId);
        foreach (var node in sourceChain)
            token.Integer(node.Id).Text(node.Name);
        token.Text(sourceState.State.ToString()).Integer(sourceState.RootId);
        token.Integer(root.ContainerFolderId);
        foreach (var crumb in sourceCrumbs)
            token.Integer(crumb.Id).Text(crumb.Name);
        token.Text("->").Integer(targetProject);
        foreach (var node in targetChain)
            token.Integer(node.Id).Text(node.Name);
        token.Text(targetState.State.ToString()).Integer(targetState.RootId);
        token.Integer(request.TargetFolderId);
        foreach (var crumb in targetCrumbs)
            token.Integer(crumb.Id).Text(crumb.Name);
        // The source root's CURRENT name (shown in the dialog title) and, separately, the name the copy will have (folders: the
        // confirmed destination name). A source rename after the preview is therefore stale even when a custom name was chosen.
        token.Text(root.Name).Text(rootName).Text(rootKey);
        token.Integer(plan.FolderCount).Integer(plan.LinkCount).Integer(plan.NoteCount).Integer(plan.SkippedItems).Integer(plan.SkippedEntries);

        foreach (var folder in plan.Folders)
            token.Text("F").Integer(folder.Id).Integer(folder.Parent).Integer(folder.Depth).Text(folder.Name).Text(folder.NameKey).Text(folder.CreatedAt);

        ReadItems(connection, transaction, source, folderIds,
            source.Kind == ContentKind.Link ? "SELECT Id, FolderId, LinkType, Name, Target, CreatedAt FROM ProjectLinks WHERE Id = $root;"
            : source.Kind == ContentKind.Folder ? "SELECT Id, FolderId, LinkType, Name, Target, CreatedAt FROM ProjectLinks WHERE FolderId IN (SELECT value FROM json_each($ids)) AND TrashEntryId IS NULL ORDER BY Id;"
            : null,
            reader =>
            {
                var isRoot = source.Kind == ContentKind.Link;
                plan.Links.Add(new ItemRow(reader.GetInt64(0), isRoot ? null : reader.GetInt64(1)));
                token.Text("L").Integer(reader.GetInt64(0)).Integer(isRoot ? null : reader.GetInt64(1)).Text(reader.GetString(2)).Text(reader.GetString(3)).Text(reader.GetString(4)).Text(reader.GetString(5));
            });
        ReadItems(connection, transaction, source, folderIds,
            source.Kind == ContentKind.Note ? "SELECT Id, FolderId, Title, Content, CreatedAt FROM ProjectNotes WHERE Id = $root;"
            : source.Kind == ContentKind.Folder ? "SELECT Id, FolderId, Title, Content, CreatedAt FROM ProjectNotes WHERE FolderId IN (SELECT value FROM json_each($ids)) AND TrashEntryId IS NULL ORDER BY Id;"
            : null,
            reader =>
            {
                var isRoot = source.Kind == ContentKind.Note;
                plan.Notes.Add(new ItemRow(reader.GetInt64(0), isRoot ? null : reader.GetInt64(1)));
                token.Text("N").Integer(reader.GetInt64(0)).Integer(isRoot ? null : reader.GetInt64(1)).Text(reader.GetString(2))
                    .Text(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(reader.GetString(3))))).Text(reader.GetString(4));
            });

        if (plan.Links.Count != plan.LinkCount || plan.Notes.Count != plan.NoteCount)
            throw new InvalidDataException("The copied set changed inside one transaction; nothing was copied.");
        plan.Token = token.Hash();
        return plan;
    }

    private static void ReadItems(SqliteConnection connection, SqliteTransaction transaction, ContentRef source, string folderIds, string? sql, Action<SqliteDataReader> row)
    {
        if (sql is null)
            return;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$root", source.Id);
        command.Parameters.AddWithValue("$ids", folderIds);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            row(reader);
    }

    private void ReadSkipped(SqliteConnection connection, SqliteTransaction transaction, long rootId, CrossPlan plan)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH RECURSIVE below (Id, Depth) AS (
                SELECT Id, 0 FROM ProjectFolders WHERE Id = $root
                UNION ALL
                SELECT c.Id, b.Depth + 1 FROM ProjectFolders AS c JOIN below AS b ON c.ParentFolderId = b.Id WHERE b.Depth < $maxDepth
            ),
            marked (Entry) AS (
                SELECT TrashEntryId FROM ProjectFolders WHERE Id IN (SELECT Id FROM below) AND Id <> $root AND TrashEntryId IS NOT NULL
                UNION ALL SELECT TrashEntryId FROM ProjectLinks WHERE FolderId IN (SELECT Id FROM below) AND TrashEntryId IS NOT NULL
                UNION ALL SELECT TrashEntryId FROM ProjectNotes WHERE FolderId IN (SELECT Id FROM below) AND TrashEntryId IS NOT NULL
            )
            SELECT count(*), count(DISTINCT Entry) FROM marked;
            """;
        command.Parameters.AddWithValue("$root", rootId);
        command.Parameters.AddWithValue("$maxDepth", _maxDepth);
        using var reader = command.ExecuteReader();
        reader.Read();
        plan.SkippedItems = (int)reader.GetInt64(0);
        plan.SkippedEntries = (int)reader.GetInt64(1);
    }

    /// <summary>
    /// L(f): folders on the chain from the Project root down to <paramref name="folderId"/>, inclusive (a root folder is 1); null
    /// when the walk does not reach a root folder within the bound (the existing <see cref="CheckAncestry"/> criterion).
    /// </summary>
    private int? FolderLevel(SqliteConnection connection, SqliteTransaction transaction, long folderId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH RECURSIVE chain (Id, ParentFolderId, Depth) AS (
                SELECT Id, ParentFolderId, 0 FROM ProjectFolders WHERE Id = $folder
                UNION ALL
                SELECT f.Id, f.ParentFolderId, c.Depth + 1 FROM ProjectFolders AS f JOIN chain AS c ON f.Id = c.ParentFolderId
                WHERE c.Depth < $maxDepth
            )
            SELECT Depth FROM chain WHERE ParentFolderId IS NULL;
            """;
        command.Parameters.AddWithValue("$folder", folderId);
        command.Parameters.AddWithValue("$maxDepth", _maxDepth);
        return command.ExecuteScalar() is long depth ? (int)depth + 1 : null;
    }

    private static bool ActiveFolderKeyExists(SqliteConnection connection, SqliteTransaction transaction, long projectNodeId, long? parentFolderId, string nameKey) =>
        ScalarLong(connection, transaction, "SELECT EXISTS (SELECT 1 FROM ProjectFolders WHERE ProjectNodeId = $project AND ParentFolderId IS $parent AND NameKey = $key AND TrashEntryId IS NULL);",
            ("$project", projectNodeId), ("$parent", (object?)parentFolderId ?? DBNull.Value), ("$key", nameKey)) == 1;

    /// <summary>"Name (2)", "Name (3)", …: the first candidate that passes the name rules and has no active sibling. A suggestion only.</summary>
    private static string? SuggestName(SqliteConnection connection, SqliteTransaction transaction, long projectNodeId, long? parentFolderId, string name)
    {
        for (var n = 2; n <= 100; n++)
        {
            var suffix = $" ({n.ToString(CultureInfo.InvariantCulture)})";
            var stem = name.Length + suffix.Length > WorkspaceName.MaxLength ? name[..(WorkspaceName.MaxLength - suffix.Length)].TrimEnd() : name;
            if (!WorkspaceName.TryNormalize(stem + suffix, out var candidate, out _))
                continue;
            if (!ActiveFolderKeyExists(connection, transaction, projectNodeId, parentFolderId, WorkspaceName.Key(candidate)))
                return candidate;
        }

        return null;
    }

    private static (int Count, long Bytes) CountAndBytes(SqliteConnection connection, SqliteTransaction transaction, string sql, string ids)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$ids", ids);
        using var reader = command.ExecuteReader();
        reader.Read();
        return ((int)reader.GetInt64(0), reader.GetInt64(1));
    }

    private static long ScalarLong(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command.ExecuteScalar() is long result ? result : 0;
    }

    // ---- Commit helpers ---------------------------------------------------------------------------------------------------

    private static void InsertFolders(SqliteConnection connection, SqliteTransaction transaction, CrossPlan plan, long targetProject, long? targetFolder, string now, Dictionary<long, long> map)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ProjectFolders (ProjectNodeId, ParentFolderId, Name, NameKey, CreatedAt, UpdatedAt)
            VALUES ($project, $parent, $name, $key, $created, $now) RETURNING Id;
            """;
        var project = command.Parameters.AddWithValue("$project", targetProject);
        var parent = command.Parameters.Add("$parent", SqliteType.Integer);
        var name = command.Parameters.Add("$name", SqliteType.Text);
        var key = command.Parameters.Add("$key", SqliteType.Text);
        var created = command.Parameters.Add("$created", SqliteType.Text);
        command.Parameters.AddWithValue("$now", now);
        foreach (var folder in plan.Folders) // ordered by (depth, Id): every parent is inserted before its children
        {
            parent.Value = folder.Parent is { } sourceParent ? map[sourceParent] : (object?)targetFolder ?? DBNull.Value;
            name.Value = folder.Name;
            key.Value = folder.NameKey;
            created.Value = folder.CreatedAt;
            map[folder.Id] = (long)command.ExecuteScalar()!;
        }
    }

    private static List<long> InsertCopies(SqliteConnection connection, SqliteTransaction transaction, List<ItemRow> rows, string table, string sql, long targetProject, long? targetFolder, string now, Dictionary<long, long> map)
    {
        var ids = new List<long>(rows.Count);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$project", targetProject);
        var folder = command.Parameters.Add("$folder", SqliteType.Integer);
        var source = command.Parameters.Add("$source", SqliteType.Integer);
        command.Parameters.AddWithValue("$now", now);
        foreach (var row in rows)
        {
            folder.Value = row.Folder is { } sourceFolder ? map[sourceFolder] : (object?)targetFolder ?? DBNull.Value;
            source.Value = row.Id;
            ids.Add(command.ExecuteScalar() is long id ? id : throw new InvalidOperationException($"{table}: a planned row was not copied; nothing was changed."));
        }

        return ids;
    }

    /// <summary>The self-delimiting P13-FLC / P14-PURGE convention, hashed incrementally (no large intermediate string).</summary>
    private sealed class StreamingTokenWriter : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public StreamingTokenWriter Text(string? value) =>
            Append(value is null ? "N;" : $"S{value.Length.ToString(CultureInfo.InvariantCulture)}:{value};");

        public StreamingTokenWriter Integer(long? value) =>
            Append(value is null ? "N;" : $"I{value.Value.ToString(CultureInfo.InvariantCulture)};");

        public string Hash() => Convert.ToHexStringLower(_hash.GetHashAndReset());

        public void Dispose() => _hash.Dispose();

        private StreamingTokenWriter Append(string text)
        {
            _hash.AppendData(Encoding.UTF8.GetBytes(text));
            return this;
        }
    }
}
