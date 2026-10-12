using System.Diagnostics;
using Microsoft.Data.Sqlite;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Search;

namespace WorkHub.Infrastructure.Persistence;

/// <summary>Tunables of <see cref="SqliteWorkspaceSearchStore"/>; production uses <see cref="Default"/>, tests shorten them.</summary>
internal sealed record WorkspaceSearchStoreOptions
{
    public static WorkspaceSearchStoreOptions Default { get; } = new();

    /// <summary>Per-level bound of a reveal branch.</summary>
    public int MaxRowsPerLevel { get; init; } = SqliteWorkspaceStore.MaxRowsPerQuery;

    /// <summary>Ancestry walk bound.</summary>
    public int MaxDepth { get; init; } = SqliteWorkspaceStore.MaxDepth;

    /// <summary>Feasibility tests turn it off to measure the provider's own cancellation.</summary>
    public bool CheckTokenInFunction { get; init; } = true;

    /// <summary>Busy timeout of the search/resolve connections only (amendment A1).</summary>
    public int BusyTimeoutSeconds { get; init; } = SqliteWorkspaceSearchStore.BusyTimeoutSeconds;

    /// <summary>Wall-clock budget of one search or resolve, retries included (amendment A1).</summary>
    public TimeSpan OverallBudget { get; init; } = SqliteWorkspaceSearchStore.OverallBudget;

    /// <summary>Pause between a busy attempt and the next one (cancellable).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Test hook: called after attempt N failed with lock contention, before the cancellation check.</summary>
    public Action<int>? AttemptFailed { get; init; }
}

/// <summary>
/// Read-only SQLite implementation of <see cref="IWorkspaceSearchStore"/> (Phase 6). One connection and ONE deferred read
/// transaction per attempt, so every row a successful search or resolve uses comes from the same snapshot; nothing is
/// ever written.
/// <para>
/// Matching folds text with <see cref="WorkspaceSearchText.Fold"/> (NFKC + invariant upper case, the Workspace name rule).
/// Names are matched on their stored keys; other short fields are folded in C#; Note content is folded inside SQLite by
/// the connection-local function <c>wh_mask</c>, only for notes whose title does not already contain every term, so the
/// content of such a note never crosses into .NET.
/// </para>
/// <para>
/// Cancellation (verified by the Phase 6 feasibility tests): <c>wh_mask</c> checks the token on every call and throws,
/// which aborts the running statement, and the token is checked between queries and rows. Microsoft.Data.Sqlite's
/// <c>SqliteCommand.Cancel()</c> does nothing and its busy wait cannot be interrupted, so lock waits are kept short instead:
/// search/resolve connections (only these) use a <see cref="BusyTimeoutSeconds"/>-second busy timeout, and a busy/locked
/// attempt is discarded and the WHOLE read restarted, within <see cref="OverallBudget"/> of wall-clock time (amendment A1).
/// </para>
/// </summary>
public sealed class SqliteWorkspaceSearchStore : IWorkspaceSearchStore
{
    /// <summary>Busy timeout of search/resolve connections. Every other operation keeps the production timeout.</summary>
    public const int BusyTimeoutSeconds = 1;

    /// <summary>Wall-clock cap of one search or resolve, including all retries.</summary>
    public static readonly TimeSpan OverallBudget = TimeSpan.FromSeconds(30);

    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const string Unavailable = "(location unavailable)";
    private const string TopLevel = "Workspace";

    private readonly SqliteConnectionFactory _connections;
    private readonly WorkspaceSearchStoreOptions _options;
    private readonly int _maxRowsPerLevel;
    private readonly int _maxDepth;
    private readonly bool _checkTokenInFunction;

    /// <param name="connections">The application's factory; only its database file is used.</param>
    public SqliteWorkspaceSearchStore(SqliteConnectionFactory connections)
        : this(connections, WorkspaceSearchStoreOptions.Default)
    {
    }

    internal SqliteWorkspaceSearchStore(SqliteConnectionFactory connections, WorkspaceSearchStoreOptions options)
    {
        _connections = new SqliteConnectionFactory(connections.DatabaseFile, options.BusyTimeoutSeconds);
        _options = options;
        _maxRowsPerLevel = options.MaxRowsPerLevel;
        _maxDepth = options.MaxDepth;
        _checkTokenInFunction = options.CheckTokenInFunction;
    }

    /// <summary>The search/resolve connection factory (short busy timeout); tests clear its pool.</summary>
    internal SqliteConnectionFactory SearchConnections => _connections;

    /// <summary>Calls of <c>wh_mask</c> (feasibility instrumentation).</summary>
    internal long FunctionCallCount => Interlocked.Read(ref _functionCalls);

    /// <summary>Read attempts started (instrumentation for retry tests).</summary>
    internal int AttemptCount => Volatile.Read(ref _attempts);

    private long _functionCalls;
    private int _attempts;

    // ---- Search ---------------------------------------------------------------------------------------------------------

    public Task<WorkspaceSearchResult> SearchAsync(WorkspaceSearchQuery query, int limit, CancellationToken cancellationToken, Action? onBusy = null) =>
        Run(connection =>
        {
            var terms = query.Terms;
            var all = WorkspaceSearchText.AllTerms(terms);
            connection.CreateFunction<string, long>("wh_mask", text =>
            {
                if (_checkTokenInFunction)
                    cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref _functionCalls);
                return WorkspaceSearchText.Mask(text, terms);
            }, isDeterministic: true);

            using var transaction = connection.BeginTransaction(deferred: true);
            var nodes = ReadNodes(connection, transaction, cancellationToken);
            var folders = ReadFolders(connection, transaction, cancellationToken);
            var candidates = new List<Candidate>();

            foreach (var node in nodes.Values)
            {
                if (node.Type == WorkspaceNodeType.Folder)
                {
                    var name = WorkspaceSearchText.MaskOfFolded(node.NameKey, terms);
                    if (name == all)
                        candidates.Add(new Candidate(new SearchItemRef(SearchResultKind.WorkspaceFolder, node.Id), null, node.Name, SearchField.Name, true, null));
                    continue;
                }

                var nameMask = WorkspaceSearchText.MaskOfFolded(node.NameKey, terms);
                var fields = new[]
                {
                    (SearchField.Name, nameMask, (string?)null),
                    (SearchField.Owner, WorkspaceSearchText.Mask(node.Owner, terms), node.Owner),
                    (SearchField.ProjectManager, WorkspaceSearchText.Mask(node.ProjectManager, terms), node.ProjectManager),
                    (SearchField.Description, WorkspaceSearchText.Mask(node.Description, terms), node.Description),
                };
                if (Combine(fields) == all)
                    candidates.Add(Candidate.From(new SearchItemRef(SearchResultKind.Project, node.Id), node.Id, node.Name, nameMask == all, fields));
            }

            foreach (var folder in folders.Values)
            {
                if (!nodes.ContainsKey(folder.ProjectNodeId))
                    continue; // content of a Project in the Workspace Trash is never a candidate
                if (WorkspaceSearchText.MaskOfFolded(folder.NameKey, terms) == all)
                    candidates.Add(new Candidate(new SearchItemRef(SearchResultKind.ProjectFolder, folder.Id), folder.ProjectNodeId, folder.Name, SearchField.Name, true, null) { ContainerFolderId = folder.ParentFolderId });
            }

            cancellationToken.ThrowIfCancellationRequested();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT Id, ProjectNodeId, FolderId, Name, Target FROM ProjectLinks WHERE TrashEntryId IS NULL;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!nodes.ContainsKey(reader.GetInt64(1)))
                        continue; // Project in the Workspace Trash
                    var name = reader.GetString(3);
                    var target = reader.GetString(4);
                    var nameMask = WorkspaceSearchText.Mask(name, terms);
                    var fields = new[] { (SearchField.Name, nameMask, (string?)null), (SearchField.Target, WorkspaceSearchText.Mask(target, terms), target) };
                    if (Combine(fields) == all)
                        candidates.Add(Candidate.From(new SearchItemRef(SearchResultKind.Link, reader.GetInt64(0)), reader.GetInt64(1), name, nameMask == all, fields) with { ContainerFolderId = NullableInt64(reader, 2) });
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var contentHits = new List<Candidate>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                // The title mask decides whether the content has to be folded at all (CASE evaluates lazily).
                command.CommandText = """
                    SELECT Id, ProjectNodeId, FolderId, Title, TitleMask,
                           CASE WHEN TitleMask = $all THEN 0 ELSE wh_mask(Content) END
                    FROM (SELECT Id, ProjectNodeId, FolderId, Title, Content, wh_mask(Title) AS TitleMask FROM ProjectNotes WHERE TrashEntryId IS NULL);
                    """;
                command.Parameters.AddWithValue("$all", all);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var titleMask = (int)reader.GetInt64(4);
                    var contentMask = (int)reader.GetInt64(5);
                    if ((titleMask | contentMask) != all)
                        continue;
                    if (!nodes.ContainsKey(reader.GetInt64(1)))
                        continue; // Project in the Workspace Trash
                    var field = titleMask != 0 ? SearchField.Name : SearchField.Content;
                    var candidate = new Candidate(new SearchItemRef(SearchResultKind.Note, reader.GetInt64(0)), reader.GetInt64(1), reader.GetString(3), field, titleMask == all, null)
                    {
                        ContainerFolderId = NullableInt64(reader, 2),
                    };
                    candidates.Add(candidate);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            candidates.Sort((x, y) => WorkspaceSearchText.Compare(x.Hit(string.Empty), x.NameMatch, y.Hit(string.Empty), y.NameMatch));
            var top = candidates.Take(limit).ToList();

            // Content excerpts need the text of the returned content-matched notes only (same snapshot).
            var noteContent = ReadNoteContent(connection, transaction, top.Where(c => c.Item.Kind == SearchResultKind.Note && c.Field == SearchField.Content).Select(c => c.Item.Id));
            var hits = top.Select(c =>
            {
                var excerptSource = c.Field == SearchField.Content && c.Item.Kind == SearchResultKind.Note
                    ? noteContent.GetValueOrDefault(c.Item.Id)
                    : c.ExcerptSource;
                var excerpt = c.Field is SearchField.Description or SearchField.Target or SearchField.Content
                    ? WorkspaceSearchText.Excerpt(excerptSource, terms)
                    : null;
                return c.Hit(Location(c, nodes, folders)) with { Excerpt = excerpt };
            }).ToList();

            transaction.Commit();
            return new WorkspaceSearchResult(hits, candidates.Count > limit);
        }, cancellationToken, onBusy);

    private static int Combine((SearchField Field, int Mask, string? Text)[] fields) => fields.Aggregate(0, (mask, f) => mask | f.Mask);

    private string Location(Candidate candidate, IReadOnlyDictionary<long, NodeRow> nodes, IReadOnlyDictionary<long, FolderRow> folders)
    {
        if (candidate.Item.Kind is SearchResultKind.WorkspaceFolder or SearchResultKind.Project)
        {
            var parent = nodes[candidate.Item.Id].ParentId;
            if (parent is null)
                return TopLevel;
            return WorkspacePath(parent.Value, nodes) is { } path ? WorkspaceSearchText.JoinPath(path) : Unavailable;
        }

        if (candidate.ProjectNodeId is not { } projectId || WorkspacePath(projectId, nodes) is not { } projectPath)
            return Unavailable;
        if (candidate.ContainerFolderId is not { } folderId)
            return WorkspaceSearchText.JoinPath(projectPath);
        return FolderPath(folderId, folders) is { } folderPath ? WorkspaceSearchText.JoinPath(projectPath.Concat(folderPath)) : Unavailable;
    }

    /// <summary>Names from the top level down to <paramref name="nodeId"/> (inclusive); null for a broken or runaway chain.</summary>
    private List<string>? WorkspacePath(long nodeId, IReadOnlyDictionary<long, NodeRow> nodes)
    {
        var names = new List<string>();
        long? current = nodeId;
        while (current is { } id)
        {
            if (names.Count >= _maxDepth || !nodes.TryGetValue(id, out var node))
                return null;
            names.Add(node.Name);
            current = node.ParentId;
        }

        names.Reverse();
        return names;
    }

    private List<string>? FolderPath(long folderId, IReadOnlyDictionary<long, FolderRow> folders)
    {
        var names = new List<string>();
        long? current = folderId;
        while (current is { } id)
        {
            if (names.Count >= _maxDepth || !folders.TryGetValue(id, out var folder))
                return null;
            names.Add(folder.Name);
            current = folder.ParentFolderId;
        }

        names.Reverse();
        return names;
    }

    private static Dictionary<long, NodeRow> ReadNodes(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT n.Id, n.ParentId, n.NodeType, n.Name, n.NameKey, p.Owner, p.ProjectManager, p.Description
            FROM WorkspaceNodes AS n LEFT JOIN Projects AS p ON p.NodeId = n.Id
            WHERE n.TrashedAt IS NULL;
            """;
        using var reader = command.ExecuteReader();
        var nodes = new Dictionary<long, NodeRow>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            var row = new NodeRow(
                reader.GetInt64(0),
                NullableInt64(reader, 1),
                reader.GetString(2) == "Project" ? WorkspaceNodeType.Project : WorkspaceNodeType.Folder,
                reader.GetString(3),
                reader.GetString(4),
                NullableString(reader, 5),
                NullableString(reader, 6),
                NullableString(reader, 7));
            nodes[row.Id] = row;
        }

        return nodes;
    }

    private static Dictionary<long, FolderRow> ReadFolders(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Active folders only: trashed folders are never candidates, and an active item never has a trashed ancestor.
        command.CommandText = "SELECT Id, ProjectNodeId, ParentFolderId, Name, NameKey FROM ProjectFolders WHERE TrashEntryId IS NULL;";
        using var reader = command.ExecuteReader();
        var folders = new Dictionary<long, FolderRow>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            var row = new FolderRow(reader.GetInt64(0), reader.GetInt64(1), NullableInt64(reader, 2), reader.GetString(3), reader.GetString(4));
            folders[row.Id] = row;
        }

        return folders;
    }

    private static Dictionary<long, string> ReadNoteContent(SqliteConnection connection, SqliteTransaction transaction, IEnumerable<long> ids)
    {
        var content = new Dictionary<long, string>();
        foreach (var id in ids)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT Content FROM ProjectNotes WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id);
            if (command.ExecuteScalar() is string text)
                content[id] = text;
        }

        return content;
    }

    // ---- Resolve --------------------------------------------------------------------------------------------------------

    public Task<RevealResolution> ResolveAsync(SearchItemRef item, CancellationToken cancellationToken) =>
        Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            long workspaceNodeId;
            ContentRevealTarget? content = null;
            switch (item.Kind)
            {
                case SearchResultKind.WorkspaceFolder or SearchResultKind.Project:
                {
                    var expected = item.Kind == SearchResultKind.Project ? "Project" : "Folder";
                    if (!string.Equals(Scalar(connection, transaction, "SELECT NodeType FROM WorkspaceNodes WHERE Id = $id;", item.Id) as string, expected, StringComparison.Ordinal))
                        return RevealResolution.NotFound;
                    if (Scalar(connection, transaction, "SELECT TrashedAt IS NOT NULL FROM WorkspaceNodes WHERE Id = $id;", item.Id) is 1L)
                        return RevealResolution.Trashed; // a Project in the Workspace Trash is never revealed
                    workspaceNodeId = item.Id;
                    break;
                }

                default:
                {
                    // ONLY the table of the item's kind: Folder, Link and Note Ids are independent.
                    var sql = item.Kind switch
                    {
                        SearchResultKind.ProjectFolder => "SELECT ProjectNodeId, ParentFolderId, TrashEntryId IS NOT NULL FROM ProjectFolders WHERE Id = $id;",
                        SearchResultKind.Link => "SELECT ProjectNodeId, FolderId, TrashEntryId IS NOT NULL FROM ProjectLinks WHERE Id = $id;",
                        SearchResultKind.Note => "SELECT ProjectNodeId, FolderId, TrashEntryId IS NOT NULL FROM ProjectNotes WHERE Id = $id;",
                        _ => throw new ArgumentOutOfRangeException(nameof(item), item.Kind, null),
                    };
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("$id", item.Id);
                    using var reader = command.ExecuteReader();
                    if (!reader.Read())
                        return RevealResolution.NotFound;
                    if (Scalar(connection, transaction, "SELECT TrashedAt IS NOT NULL FROM WorkspaceNodes WHERE Id = $id;", reader.GetInt64(0)) is 1L)
                        return RevealResolution.ProjectTrashed; // content of a Project in the Workspace Trash is never revealed
                    if (reader.GetInt64(2) == 1)
                        return RevealResolution.Trashed; // never revealed; the hit is removed by (Kind, Id)
                    workspaceNodeId = reader.GetInt64(0);
                    content = new ContentRevealTarget(workspaceNodeId, NullableInt64(reader, 1), item.Kind, item.Id);
                    break;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var ancestors = Ancestors(connection, transaction, workspaceNodeId);
            var path = ancestors.Append(workspaceNodeId).ToList();
            var branches = new List<WorkspaceBranchSnapshot>();
            long? parent = null;
            foreach (var next in path)
            {
                cancellationToken.ThrowIfCancellationRequested();
                branches.Add(Branch(connection, transaction, parent, next));
                parent = next;
            }

            transaction.Commit();
            return RevealResolution.Found(new SearchRevealTarget(item, ancestors, workspaceNodeId, branches, content));
        }, cancellationToken);

    /// <summary>Workspace Folder Ids from the top level down to the node's parent. Throws for a broken or runaway chain.</summary>
    private List<long> Ancestors(SqliteConnection connection, SqliteTransaction transaction, long nodeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH RECURSIVE up (Id, ParentId, Depth) AS (
                SELECT Id, ParentId, 0 FROM WorkspaceNodes WHERE Id = $id
                UNION ALL
                SELECT n.Id, n.ParentId, u.Depth + 1 FROM WorkspaceNodes AS n JOIN up AS u ON n.Id = u.ParentId
                WHERE u.Depth < $maxDepth
            )
            SELECT Id, ParentId FROM up ORDER BY Depth;
            """;
        command.Parameters.AddWithValue("$id", nodeId);
        command.Parameters.AddWithValue("$maxDepth", _maxDepth);
        using var reader = command.ExecuteReader();
        var chain = new List<(long Id, long? ParentId)>();
        while (reader.Read())
            chain.Add((reader.GetInt64(0), NullableInt64(reader, 1)));
        if (chain.Count == 0 || chain[^1].ParentId is not null)
            throw new InvalidDataException("The Workspace ancestry of the item could not be verified.");
        return chain.Skip(1).Select(c => c.Id).Reverse().ToList();
    }

    /// <summary>Children of one level (bounded); the path node is always included so it can be placed.</summary>
    private WorkspaceBranchSnapshot Branch(SqliteConnection connection, SqliteTransaction transaction, long? parentId, long pathNodeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"{NodeSelect} WHERE n.ParentId IS $parent AND n.TrashedAt IS NULL LIMIT $limit;";
        command.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", _maxRowsPerLevel + 1);
        var children = ReadWorkspaceNodes(command);
        var complete = children.Count <= _maxRowsPerLevel;
        if (!complete)
        {
            children.RemoveAt(children.Count - 1);
            if (children.All(c => c.Id != pathNodeId))
            {
                using var single = connection.CreateCommand();
                single.Transaction = transaction;
                single.CommandText = $"{NodeSelect} WHERE n.Id = $id;";
                single.Parameters.AddWithValue("$id", pathNodeId);
                children.AddRange(ReadWorkspaceNodes(single));
            }
        }

        return new WorkspaceBranchSnapshot(parentId, children, complete);
    }

    private const string NodeSelect = """
        SELECT n.Id, n.ParentId, n.NodeType, n.Name,
               EXISTS (SELECT 1 FROM WorkspaceNodes AS c WHERE c.ParentId = n.Id AND c.TrashedAt IS NULL) AS HasChildren
        FROM WorkspaceNodes AS n
        """;

    private static List<WorkspaceNode> ReadWorkspaceNodes(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var nodes = new List<WorkspaceNode>();
        while (reader.Read())
        {
            nodes.Add(new WorkspaceNode(
                reader.GetInt64(0),
                NullableInt64(reader, 1),
                reader.GetString(2) == "Project" ? WorkspaceNodeType.Project : WorkspaceNodeType.Folder,
                reader.GetString(3),
                reader.GetInt64(4) == 1));
        }

        return nodes;
    }

    // ---- Plumbing -------------------------------------------------------------------------------------------------------

    private static object? Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql, long id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar();
    }

    private static long? NullableInt64(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>
    /// Runs the WHOLE read on a worker thread, on a fresh short-timeout connection per attempt (amendment A1):
    /// <list type="bullet">
    /// <item>Lock contention (SQLITE_BUSY / SQLITE_LOCKED) discards the attempt — its read transaction and connection are
    /// disposed while the exception unwinds — and restarts the read after <see cref="WorkspaceSearchStoreOptions.RetryDelay"/>.
    /// A result therefore always comes from one complete read transaction.</item>
    /// <item>Another attempt starts only if its full busy timeout still fits in the remaining wall-clock budget; otherwise
    /// <see cref="WorkspaceSearchBusyException"/>. The budget is never extended by a further timeout.</item>
    /// <item>Cancellation is checked before each attempt, after a failed attempt and during the retry delay. A failure
    /// while the token is cancelled (including the provider's wrapping of what <c>wh_mask</c> throws) surfaces as
    /// <see cref="OperationCanceledException"/>. Nothing else (other SQL errors, malformed data, I/O) is retried.</item>
    /// </list>
    /// </summary>
    private Task<T> Run<T>(Func<SqliteConnection, T> work, CancellationToken cancellationToken, Action? onBusy = null) =>
        Task.Run(() =>
        {
            var clock = Stopwatch.StartNew();
            var attemptWait = TimeSpan.FromSeconds(_options.BusyTimeoutSeconds);
            for (var attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (attempt > 1 && _options.OverallBudget - clock.Elapsed < attemptWait)
                    throw new WorkspaceSearchBusyException(_options.OverallBudget);

                Interlocked.Increment(ref _attempts);
                try
                {
                    using var connection = _connections.OpenExisting();
                    return work(connection);
                }
                catch (SqliteException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteBusy or SqliteLocked)
                {
                    _options.AttemptFailed?.Invoke(attempt);
                    onBusy?.Invoke();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (cancellationToken.WaitHandle.WaitOne(_options.RetryDelay))
                        throw new OperationCanceledException(cancellationToken);
                }
            }
        }, cancellationToken);

    private sealed record NodeRow(long Id, long? ParentId, WorkspaceNodeType Type, string Name, string NameKey, string? Owner, string? ProjectManager, string? Description);

    private sealed record FolderRow(long Id, long ProjectNodeId, long? ParentFolderId, string Name, string NameKey);

    private sealed record Candidate(SearchItemRef Item, long? ProjectNodeId, string Name, SearchField Field, bool NameMatch, string? ExcerptSource)
    {
        public long? ContainerFolderId { get; init; }

        /// <summary>The reported field is the first one (Name, Owner, Project manager, Description, Target) that has a term.</summary>
        public static Candidate From(SearchItemRef item, long? projectNodeId, string name, bool nameMatch, (SearchField Field, int Mask, string? Text)[] fields)
        {
            var first = fields.First(f => f.Mask != 0);
            return new Candidate(item, projectNodeId, name, first.Field, nameMatch, first.Text);
        }

        public WorkspaceSearchHit Hit(string location) => new(Item, ProjectNodeId, Name, location, Field, null);
    }
}
