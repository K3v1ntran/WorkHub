using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;

namespace WorkHub.Infrastructure.Persistence;

/// <summary>One row of a purge plan: (Kind, Id), its parent folder, its Trash marker and (folders) its depth inside the plan.</summary>
internal sealed record PurgePlanRow(ContentKind Kind, long Id, long? ParentFolderId, long EntryId, int Depth);

/// <summary>A ready plan: the preview the user confirms and the exact rows a commit deletes.</summary>
internal sealed record PurgePlan(PurgePreview Preview, IReadOnlyList<long> Closure, IReadOnlyList<PurgePlanRow> Rows);

internal readonly record struct PurgePlanResult(PurgePreviewStatus Status, PurgePlan? Plan)
{
    public static PurgePlanResult Refused(PurgePreviewStatus status) => new(status, null);
}

/// <summary>
/// Phase 14 Revision 2 §5.5–§5.6: plans a permanent deletion from a Project's content Trash inside the CALLER'S transaction
/// (a read snapshot for the preview and the backup, <c>BEGIN IMMEDIATE</c> for the commit), so the checked state is the state
/// the token describes. Evaluation order (first failure wins): Project exists → effective state over the whole Workspace
/// ancestor chain (<see cref="NodeLifecycle"/>; the V007 triggers cover only the Project's own marker, ancestors are a store
/// guarantee) → request → Trash consistency (fails closed) → plan and token.
/// <list type="bullet">
/// <item>An entry's containing entry is the marker of its root's origin folder. An ACTIVE origin never has a trashed
/// ancestor folder; if one does, the data is inconsistent and nothing is planned.</item>
/// <item>The closure of the roots is every entry whose containing-entry chain reaches one of them (any depth).</item>
/// <item>Every closure entry must own exactly <c>ItemCount</c> rows, and its root row must carry its marker.</item>
/// </list>
/// The token (P14-PURGE-2) covers the mode, the Project's identity, name and effective state, the selected roots, every
/// closure entry with its displayed values and origin path (Ids, names, markers), and every planned row.
/// </summary>
internal sealed class ProjectTrashPurgePlanner(int maxDepth)
{
    internal const string TokenVersion = "P14-PURGE-2";

    private sealed class EntryFacts
    {
        public required long Id { get; init; }
        public required ContentKind Kind { get; init; }
        public required long RootId { get; init; }
        public required int ItemCount { get; init; }
        public required string TrashedAt { get; init; }
        public string RootName { get; set; } = string.Empty;
        public long? Parent { get; set; }
        public List<(long Id, string Name, long? Marker)> Path { get; } = [];
    }

    public PurgePlanResult Plan(SqliteConnection connection, SqliteTransaction transaction, PurgeRequest request)
    {
        var project = request.ProjectNodeId;

        // 1. The Project, then its effective lifecycle over the whole Workspace chain (trash dominates archive).
        if (Scalar(connection, transaction, "SELECT 1 FROM Projects WHERE NodeId = $p;", ("$p", project)) is null)
            return PurgePlanResult.Refused(PurgePreviewStatus.ProjectNotFound);

        var lifecycle = new NodeLifecycle(connection, transaction, maxDepth);
        EffectiveLifecycle state;
        try
        {
            state = lifecycle.Effective(project) ?? throw new InvalidDataException("The Project node is missing.");
        }
        catch (InvalidDataException)
        {
            return PurgePlanResult.Refused(PurgePreviewStatus.AncestryUnverified);
        }

        if (state.State == LifecycleState.Trashed)
            return PurgePlanResult.Refused(PurgePreviewStatus.ProjectTrashed);
        var projectName = lifecycle.Row(project)!.Name;

        // 2. The Project's entries now.
        var entries = new Dictionary<long, EntryFacts>();
        using (var command = Command(connection, transaction, "SELECT Id, RootKind, RootId, ItemCount, TrashedAt FROM ProjectTrashEntries WHERE ProjectNodeId = $p;", ("$p", project)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                entries[reader.GetInt64(0)] = new EntryFacts
                {
                    Id = reader.GetInt64(0),
                    Kind = Enum.Parse<ContentKind>(reader.GetString(1)),
                    RootId = reader.GetInt64(2),
                    ItemCount = (int)reader.GetInt64(3),
                    TrashedAt = reader.GetString(4),
                };
            }
        }

        // 3. The request against that set (no partial purge).
        IReadOnlyList<long> roots;
        if (request.Mode == PurgeMode.SelectedEntries)
        {
            if (request.SelectedEntryIds.Count == 0)
                return PurgePlanResult.Refused(PurgePreviewStatus.EmptySelection);
            if (request.SelectedEntryIds.Any(id => !entries.ContainsKey(id)))
                return PurgePlanResult.Refused(PurgePreviewStatus.EntryNotFound);
            roots = request.SelectedEntryIds;
        }
        else
        {
            if (entries.Count == 0)
                return PurgePlanResult.Refused(PurgePreviewStatus.TrashEmpty);
            roots = entries.Keys.Order().ToList();
        }

        // 4. Every entry's root, origin path and containing entry (fails closed).
        foreach (var entry in entries.Values)
        {
            if (ReadEntryRoot(connection, transaction, project, entry) is { } problem)
                return PurgePlanResult.Refused(problem);
        }

        // 5. Closure: roots plus every entry whose containing-entry chain reaches them.
        var children = entries.Values.Where(e => e.Parent is not null).GroupBy(e => e.Parent!.Value).ToDictionary(g => g.Key, g => g.Select(e => e.Id).ToList());
        var closure = new SortedSet<long>();
        var pending = new Queue<long>(roots);
        while (pending.Count > 0)
        {
            var id = pending.Dequeue();
            if (!closure.Add(id))
                continue;
            foreach (var child in children.GetValueOrDefault(id) ?? [])
                pending.Enqueue(child);
        }

        // 6. The rows carrying a closure marker, folder depth inside the plan, and the ItemCount invariant.
        var rows = ReadRows(connection, transaction, project, closure);
        if (rows is null)
            return PurgePlanResult.Refused(PurgePreviewStatus.AncestryUnverified);
        foreach (var id in closure)
        {
            if (rows.Count(r => r.EntryId == id) != entries[id].ItemCount)
                return PurgePlanResult.Refused(PurgePreviewStatus.TrashDataInconsistent);
        }

        var selected = request.Mode == PurgeMode.EntireTrash ? closure.ToHashSet() : request.SelectedEntryIds.ToHashSet();
        var token = Token(request.Mode, project, projectName, state, roots, closure.Select(id => entries[id]).ToList(), selected, rows);
        var views = Views(projectName, closure, entries, selected);
        var counts = new PurgeCounts(
            closure.Count,
            rows.Count(r => r.Kind == ContentKind.Folder),
            rows.Count(r => r.Kind == ContentKind.Link),
            rows.Count(r => r.Kind == ContentKind.Note));
        var preview = new PurgePreview(request, projectName, state.State, views, counts, token);
        return new PurgePlanResult(PurgePreviewStatus.Ready, new PurgePlan(preview, closure.ToList(), rows));
    }

    /// <summary>The root row (with this entry's marker, in this Project), its origin chain and the containing entry.</summary>
    private PurgePreviewStatus? ReadEntryRoot(SqliteConnection connection, SqliteTransaction transaction, long project, EntryFacts entry)
    {
        var sql = entry.Kind switch
        {
            ContentKind.Folder => "SELECT Name, ParentFolderId FROM ProjectFolders WHERE Id = $id AND ProjectNodeId = $p AND TrashEntryId = $e;",
            ContentKind.Link => "SELECT Name, FolderId FROM ProjectLinks WHERE Id = $id AND ProjectNodeId = $p AND TrashEntryId = $e;",
            _ => "SELECT Title, FolderId FROM ProjectNotes WHERE Id = $id AND ProjectNodeId = $p AND TrashEntryId = $e;",
        };
        long? origin;
        using (var command = Command(connection, transaction, sql, ("$id", entry.RootId), ("$p", project), ("$e", entry.Id)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
                return PurgePreviewStatus.TrashDataInconsistent;
            entry.RootName = reader.GetString(0);
            origin = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        }

        if (origin is not { } folder)
            return null; // trashed from the Project root: no containing entry

        using (var command = Command(connection, transaction, """
            WITH RECURSIVE chain (Id, ParentFolderId, Name, TrashEntryId, ProjectNodeId, Depth) AS (
                SELECT Id, ParentFolderId, Name, TrashEntryId, ProjectNodeId, 0 FROM ProjectFolders WHERE Id = $id
                UNION ALL
                SELECT f.Id, f.ParentFolderId, f.Name, f.TrashEntryId, f.ProjectNodeId, c.Depth + 1
                FROM ProjectFolders AS f JOIN chain AS c ON f.Id = c.ParentFolderId
                WHERE c.Depth < $maxDepth
            )
            SELECT Id, ParentFolderId, Name, TrashEntryId, ProjectNodeId FROM chain ORDER BY Depth DESC;
            """, ("$id", folder), ("$maxDepth", maxDepth)))
        using (var reader = command.ExecuteReader())
        {
            var first = true;
            while (reader.Read())
            {
                if (first && !reader.IsDBNull(1))
                    return PurgePreviewStatus.AncestryUnverified; // broken chain or depth bound
                first = false;
                if (reader.GetInt64(4) != project)
                    return PurgePreviewStatus.AncestryUnverified;
                entry.Path.Add((reader.GetInt64(0), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt64(3)));
            }

            if (first)
                return PurgePreviewStatus.AncestryUnverified; // the origin folder does not exist
        }

        entry.Parent = entry.Path[^1].Marker;
        if (entry.Parent is null && entry.Path.Any(p => p.Marker is not null))
            return PurgePreviewStatus.TrashDataInconsistent; // an active origin below a trashed folder
        return null;
    }

    /// <summary>Planned rows ordered by (kind, id); null when folder depths cannot be established within the bound.</summary>
    private List<PurgePlanRow>? ReadRows(SqliteConnection connection, SqliteTransaction transaction, long project, IReadOnlyCollection<long> closure)
    {
        var ids = "[" + string.Join(",", closure.Select(id => id.ToString(CultureInfo.InvariantCulture))) + "]";
        var raw = new List<(ContentKind Kind, long Id, long? Parent, long Entry)>();
        using (var command = Command(connection, transaction, """
            SELECT 'Folder', Id, ParentFolderId, TrashEntryId FROM ProjectFolders WHERE ProjectNodeId = $p AND TrashEntryId IN (SELECT value FROM json_each($ids))
            UNION ALL
            SELECT 'Link', Id, FolderId, TrashEntryId FROM ProjectLinks WHERE ProjectNodeId = $p AND TrashEntryId IN (SELECT value FROM json_each($ids))
            UNION ALL
            SELECT 'Note', Id, FolderId, TrashEntryId FROM ProjectNotes WHERE ProjectNodeId = $p AND TrashEntryId IN (SELECT value FROM json_each($ids));
            """, ("$p", project), ("$ids", ids)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                raw.Add((Enum.Parse<ContentKind>(reader.GetString(0)), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.GetInt64(3)));
        }

        var folderParents = raw.Where(r => r.Kind == ContentKind.Folder).ToDictionary(r => r.Id, r => r.Parent);
        var rows = new List<PurgePlanRow>(raw.Count);
        foreach (var (kind, id, parent, entry) in raw)
        {
            var depth = 0;
            if (kind == ContentKind.Folder)
            {
                var current = parent;
                while (current is { } p && folderParents.TryGetValue(p, out var next))
                {
                    if (++depth > Math.Min(maxDepth, folderParents.Count))
                        return null; // a cycle or a chain beyond the bound
                    current = next;
                }
            }

            rows.Add(new PurgePlanRow(kind, id, parent, entry, depth));
        }

        rows.Sort((a, b) => a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : a.Id.CompareTo(b.Id));
        return rows;
    }

    /// <summary>Display order: top-level entries (no containing entry in the plan) newest first, each followed by its dependents.</summary>
    private static List<PurgeEntryView> Views(string projectName, IReadOnlyCollection<long> closure, IReadOnlyDictionary<long, EntryFacts> entries, IReadOnlySet<long> selected)
    {
        static IOrderedEnumerable<EntryFacts> Newest(IEnumerable<EntryFacts> items) =>
            items.OrderByDescending(e => e.TrashedAt, StringComparer.Ordinal).ThenByDescending(e => e.Id);

        var inPlan = closure.Select(id => entries[id]).ToList();
        var views = new List<PurgeEntryView>(inPlan.Count);
        void Add(EntryFacts entry, int depth)
        {
            views.Add(new PurgeEntryView(
                entry.Id,
                selected.Contains(entry.Id),
                entry.Parent,
                entry.Kind,
                entry.RootId,
                entry.RootName,
                entry.ItemCount,
                DateTimeOffset.Parse(entry.TrashedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                new ProjectContainerPath(projectName, entry.Path.Select(p => new ProjectFolderCrumb(p.Id, p.Name)).ToList()),
                entry.Path.Count > 0 && entry.Path[^1].Marker is not null,
                depth));
            foreach (var child in Newest(inPlan.Where(e => e.Parent == entry.Id)))
                Add(child, depth + 1);
        }

        foreach (var top in Newest(inPlan.Where(e => e.Parent is not { } parent || !closure.Contains(parent))))
            Add(top, 0);
        return views;
    }

    private static string Token(
        PurgeMode mode,
        long project,
        string projectName,
        EffectiveLifecycle state,
        IReadOnlyList<long> roots,
        IReadOnlyList<EntryFacts> closure,
        IReadOnlySet<long> selected,
        IReadOnlyList<PurgePlanRow> rows)
    {
        var token = new TokenWriter();
        token.Text(TokenVersion).Text(mode.ToString()).Integer(project).Text(projectName).Text(state.State.ToString()).Integer(state.RootId);
        token.Text("roots").Integer(roots.Count);
        foreach (var root in roots)
            token.Integer(root);
        token.Text("entries").Integer(closure.Count);
        foreach (var entry in closure.OrderBy(e => e.Id))
        {
            token.Integer(entry.Id).Integer(selected.Contains(entry.Id) ? 1 : 0).Integer(entry.Parent).Text(entry.Kind.ToString()).Integer(entry.RootId)
                .Text(entry.RootName).Integer(entry.ItemCount).Text(entry.TrashedAt);
            token.Integer(entry.Path.Count);
            foreach (var (id, name, marker) in entry.Path)
                token.Integer(id).Text(name).Integer(marker);
        }

        token.Text("rows").Integer(rows.Count);
        foreach (var row in rows)
            token.Text(row.Kind.ToString()).Integer(row.Id).Integer(row.ParentFolderId).Integer(row.EntryId);
        return token.Hash();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteScalar();
    }

    /// <summary>Self-delimiting serialization (the P13-FLC convention): every string carries its length.</summary>
    private sealed class TokenWriter
    {
        private readonly StringBuilder _text = new();

        public TokenWriter Text(string? value)
        {
            if (value is null)
                _text.Append("N;");
            else
                _text.Append('S').Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
            return this;
        }

        public TokenWriter Integer(long? value)
        {
            if (value is null)
                _text.Append("N;");
            else
                _text.Append('I').Append(value.Value.ToString(CultureInfo.InvariantCulture)).Append(';');
            return this;
        }

        public string Hash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_text.ToString())));
    }
}
