using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Search;

namespace WorkHub.Infrastructure.Persistence;

/// <summary>
/// Phase 21 (proposal Revision 2 + R2a): rename and merge of one tag key across Projects, and the Project editor's tag baseline
/// check. Schema v10, no migration: tag rows stay immutable (plain DELETE then INSERT; never UPDATE, REPLACE or upsert) and V010's
/// triggers stay the backstop.
/// </summary>
public sealed partial class SqliteWorkspaceStore
{
    /// <summary>The token format of a tag change plan (§3.3.1).</summary>
    internal const string TagPlanFormat = "WH-TAGPLAN/1";

    /// <summary>
    /// Phase 21 test seam: runs inside <see cref="ApplyTagChangeAsync"/>'s transaction after all deletes and before the inserts
    /// (TR12: an exception here must roll everything back). Null in production.
    /// </summary>
    internal Action<SqliteConnection, SqliteTransaction>? AfterTagChangeDeletes { get; set; }

    public Task<IReadOnlyList<TagInUse>> ListTagsInUseAsync(CancellationToken cancellationToken = default) =>
        Run<IReadOnlyList<TagInUse>>(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var lifecycle = new NodeLifecycle(connection, transaction, MaxDepth);
            var rows = new List<(long Project, string Key, string Name)>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT ProjectNodeId, NameKey, Name FROM ProjectTags;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
            }

            var eligible = new Dictionary<long, bool>();
            bool IsEligible(long project) =>
                eligible.TryGetValue(project, out var e) ? e : eligible[project] = IsTagEligible(lifecycle, project);
            var list = rows
                .GroupBy(r => r.Key, StringComparer.Ordinal)
                .Select(g =>
                {
                    var changeable = g.Where(r => IsEligible(r.Project)).ToList();
                    // The displayed spelling counts the Projects outside the Trash (as the suggestions do); a key carried only by
                    // Projects in the Trash shows its most used spelling among those.
                    var basis = changeable.Count > 0 ? changeable : g.ToList();
                    var spelling = basis.GroupBy(r => r.Name, StringComparer.Ordinal)
                        .OrderByDescending(s => s.Count()).ThenBy(s => s.Key, StringComparer.Ordinal).First().Key;
                    return new TagInUse(g.Key, spelling, changeable.Count, g.Count() - changeable.Count);
                })
                .OrderBy(t => t.NameKey, StringComparer.Ordinal)
                .ToList();
            transaction.Commit();
            return list;
        }, cancellationToken);

    public Task<TagChangePreview> PrepareTagChangeAsync(TagChangeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ProjectTagName.TryNormalize(request.NewName, out _, out var error))
            throw new ArgumentException(error, nameof(request)); // refused before any SQL
        return Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var plan = BuildTagPlan(connection, transaction, request);
            transaction.Commit();
            return plan.Preview;
        }, cancellationToken);
    }

    public Task<TagChangeResult> ApplyTagChangeAsync(TagChangeRequest request, string token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ProjectTagName.TryNormalize(request.NewName, out _, out var error))
            return Task.FromResult(new TagChangeResult(TagChangeOutcome.InvalidName, null, error)); // step 1: before any SQL
        return Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false); // BEGIN IMMEDIATE: the plan cannot change before the write
            var plan = BuildTagPlan(connection, transaction, request);
            var preview = plan.Preview;
            // Step 2 runs FIRST: a plan that changed after the preview (including the last eligible row gone, or the same spelling
            // applied elsewhere) is Stale with the fresh plan, never NothingToRename / Unchanged.
            if (!string.Equals(preview.Token, token, StringComparison.Ordinal))
                return new TagChangeResult(TagChangeOutcome.Stale, preview); // rolled back by Dispose: nothing written
            if (preview.Replaced + preview.Merged + preview.AlreadySpelled == 0)
                return new TagChangeResult(TagChangeOutcome.NothingToRename, preview);
            if (preview.Operation == TagChangeOperation.SpellingChange && preview.Replaced == 0)
                return new TagChangeResult(TagChangeOutcome.Unchanged, preview);

            // Step 5: all deletes, then all inserts (each Project loses one row before it gains at most one: the 20-tag limit holds).
            var writes = plan.Rows.Where(r => r.Action != TagRowAction.None).ToList();
            foreach (var row in writes)
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM ProjectTags WHERE ProjectNodeId = $id AND NameKey = $key;";
                delete.Parameters.AddWithValue("$id", row.ProjectNodeId);
                delete.Parameters.AddWithValue("$key", preview.FromKey);
                if (delete.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("A planned tag row was not found under the write lock."); // rolls everything back
            }

            AfterTagChangeDeletes?.Invoke(connection, transaction);

            foreach (var row in writes.Where(r => r.Action == TagRowAction.Replace))
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO ProjectTags (ProjectNodeId, NameKey, Name, CreatedAt) VALUES ($id, $key, $name, $createdAt);";
                insert.Parameters.AddWithValue("$id", row.ProjectNodeId);
                insert.Parameters.AddWithValue("$key", preview.NewKey);
                insert.Parameters.AddWithValue("$name", preview.NewName);
                insert.Parameters.AddWithValue("$createdAt", row.CreatedAt); // the replaced source row's stored text, verbatim
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
            var outcome = preview.Operation switch
            {
                TagChangeOperation.SpellingChange => TagChangeOutcome.SpellingChanged,
                TagChangeOperation.Merge => TagChangeOutcome.Merged,
                _ => TagChangeOutcome.Renamed,
            };
            return new TagChangeResult(outcome, preview);
        }, cancellationToken);
    }

    private enum TagRowAction
    {
        /// <summary>Excluded (Trash) or already spelled as confirmed: not written.</summary>
        None,

        /// <summary>Delete the source row and insert the new key / spelling with the source <c>CreatedAt</c>.</summary>
        Replace,

        /// <summary>Delete the source row only: the Project already has the target key (its row stays byte-equal).</summary>
        DropSource,
    }

    private sealed record TagPlanRow(long ProjectNodeId, string Name, string CreatedAt, bool Eligible, bool HasTarget, TagRowAction Action);

    private sealed record TagPlan(TagChangePreview Preview, IReadOnlyList<TagPlanRow> Rows);

    /// <summary>A Project's tags may be changed by a rename / merge when it is not in the Trash, itself or through a Folder (D3).</summary>
    private static bool IsTagEligible(NodeLifecycle lifecycle, long projectNodeId) =>
        lifecycle.Effective(projectNodeId) is { } state && state.State != LifecycleState.Trashed;

    /// <summary>
    /// The ONE plan function of preview and apply (§3.3.1): operation, source rows with eligibility and <c>CreatedAt</c>, target facts,
    /// per-row actions, counts and the token. Same committed data → same plan → same token.
    /// </summary>
    private static TagPlan BuildTagPlan(SqliteConnection connection, SqliteTransaction transaction, TagChangeRequest request)
    {
        if (!ProjectTagName.TryNormalize(request.NewName, out var newName, out var error))
            throw new ArgumentException(error, nameof(request));
        var fromKey = request.FromKey;
        var newKey = ProjectTagName.Key(newName);
        var lifecycle = new NodeLifecycle(connection, transaction, MaxDepth);

        var sources = new List<(long Project, string Name, string CreatedAt)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT ProjectNodeId, Name, CreatedAt FROM ProjectTags WHERE NameKey = $key;";
            command.Parameters.AddWithValue("$key", fromKey);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                sources.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        }

        sources.Sort((x, y) => x.Project.CompareTo(y.Project));

        // newKey == fromKey is decided FIRST: the "target already exists" rule never applies to a spelling change.
        var targets = new HashSet<long>();
        if (!string.Equals(newKey, fromKey, StringComparison.Ordinal))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT ProjectNodeId FROM ProjectTags WHERE NameKey = $key;";
            command.Parameters.AddWithValue("$key", newKey);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                targets.Add(reader.GetInt64(0));
        }

        var operation = string.Equals(newKey, fromKey, StringComparison.Ordinal) ? TagChangeOperation.SpellingChange
            : targets.Count > 0 ? TagChangeOperation.Merge
            : TagChangeOperation.Rename;

        var rows = new List<TagPlanRow>(sources.Count);
        int replaced = 0, merged = 0, spelled = 0, trashed = 0;
        foreach (var (project, name, createdAt) in sources)
        {
            var eligible = IsTagEligible(lifecycle, project);
            var hasTarget = operation != TagChangeOperation.SpellingChange && targets.Contains(project);
            TagRowAction action;
            if (!eligible)
            {
                action = TagRowAction.None;
                trashed++;
            }
            else if (operation == TagChangeOperation.SpellingChange)
            {
                if (string.Equals(name, newName, StringComparison.Ordinal))
                {
                    action = TagRowAction.None;
                    spelled++;
                }
                else
                {
                    action = TagRowAction.Replace;
                    replaced++;
                }
            }
            else if (hasTarget)
            {
                action = TagRowAction.DropSource;
                merged++;
            }
            else
            {
                action = TagRowAction.Replace;
                replaced++;
            }

            rows.Add(new TagPlanRow(project, name, createdAt, eligible, hasTarget, action));
        }

        var token = TagPlanToken(operation, fromKey, newKey, newName, rows);
        return new TagPlan(new TagChangePreview(request.Kind, operation, fromKey, newKey, newName, replaced, merged, spelled, trashed, token), rows);
    }

    /// <summary>
    /// Lowercase hex SHA-256 of the canonical plan bytes (§3.3.1): the format tag, the operation, the length-prefixed source key, new
    /// key and confirmed spelling, then per source row in ProjectNodeId order: id, stored Name, raw CreatedAt text, eligibility E/X
    /// and, for an eligible row of a rename / merge, HasTargetKey 1/0. Nothing else (no time, no other keys, no culture).
    /// </summary>
    private static string TagPlanToken(TagChangeOperation operation, string fromKey, string newKey, string newName, IReadOnlyList<TagPlanRow> rows)
    {
        static string Lp(string s) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Encoding.UTF8.GetByteCount(s)}:{s}");
        var sb = new StringBuilder();
        sb.Append(TagPlanFormat).Append('\n');
        sb.Append(operation.ToString()).Append('\n');
        sb.Append(Lp(fromKey)).Append(Lp(newKey)).Append(Lp(newName)).Append('\n');
        foreach (var row in rows)
        {
            sb.Append(row.ProjectNodeId.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|')
              .Append(Lp(row.Name)).Append(Lp(row.CreatedAt)).Append(row.Eligible ? 'E' : 'X');
            if (row.Eligible && operation != TagChangeOperation.SpellingChange)
                sb.Append(row.HasTarget ? '1' : '0');
            sb.Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    // ---- Phase 22: delete a tag everywhere (proposal Revision 2 + R2a §3.3) ---------------------------------------------------

    /// <summary>The token format of a tag delete plan (§3.3). Distinct from <see cref="TagPlanFormat"/>: tokens are never interchangeable.</summary>
    internal const string TagDeletePlanFormat = "WH-TAGDEL/1";

    /// <summary>
    /// Phase 22 test seam: runs inside <see cref="ApplyTagDeleteAsync"/>'s transaction after the first eligible row was deleted
    /// (TD12: an exception here must roll everything back). Null in production.
    /// </summary>
    internal Action<SqliteConnection, SqliteTransaction>? AfterTagDeleteRows { get; set; }

    public Task<TagDeletePreview> PrepareTagDeleteAsync(string fromKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromKey);
        return Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var plan = BuildTagDeletePlan(connection, transaction, fromKey);
            transaction.Commit();
            return plan.Preview;
        }, cancellationToken);
    }

    public Task<TagDeleteResult> ApplyTagDeleteAsync(string fromKey, string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromKey);
        ArgumentNullException.ThrowIfNull(token);
        return Run(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: false); // BEGIN IMMEDIATE: the plan cannot change before the write
            var plan = BuildTagDeletePlan(connection, transaction, fromKey);
            var preview = plan.Preview;
            // Step 1 runs FIRST: a different token is Stale with the fresh plan, also when that plan is empty (TD10, TD11c, TD11d).
            if (!string.Equals(preview.Token, token, StringComparison.Ordinal))
                return new TagDeleteResult(TagDeleteOutcome.Stale, preview); // rolled back by Dispose: nothing written
            // Step 2: the confirmed plan itself has nothing to delete (TD10b, TD11b).
            if (preview.Deleted == 0)
                return new TagDeleteResult(TagDeleteOutcome.NothingToDelete, preview);

            // Step 3: every eligible row, each exactly once; any surprise rolls everything back.
            var first = true;
            foreach (var row in plan.Rows.Where(r => r.Eligible))
            {
                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM ProjectTags WHERE ProjectNodeId = $id AND NameKey = $key;";
                    delete.Parameters.AddWithValue("$id", row.ProjectNodeId);
                    delete.Parameters.AddWithValue("$key", preview.FromKey);
                    if (delete.ExecuteNonQuery() != 1)
                        throw new InvalidOperationException("A planned tag row was not found under the write lock.");
                }

                if (first)
                {
                    AfterTagDeleteRows?.Invoke(connection, transaction);
                    first = false;
                }
            }

            transaction.Commit();
            return new TagDeleteResult(TagDeleteOutcome.Deleted, preview);
        }, cancellationToken);
    }

    private sealed record TagDeletePlanRow(long ProjectNodeId, string Name, string CreatedAt, bool Eligible);

    private sealed record TagDeletePlan(TagDeletePreview Preview, IReadOnlyList<TagDeletePlanRow> Rows);

    /// <summary>
    /// The ONE plan function of delete preview and apply (§3.3): every row of the key in ProjectNodeId order with its stored Name,
    /// raw <c>CreatedAt</c> text and eligibility (same rule as rename/merge, D4), the counts and the token. Same data → same token.
    /// </summary>
    private TagDeletePlan BuildTagDeletePlan(SqliteConnection connection, SqliteTransaction transaction, string fromKey)
    {
        var lifecycle = new NodeLifecycle(connection, transaction, MaxDepth);
        var sources = new List<(long Project, string Name, string CreatedAt)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT ProjectNodeId, Name, CreatedAt FROM ProjectTags WHERE NameKey = $key;";
            command.Parameters.AddWithValue("$key", fromKey);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                sources.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        }

        sources.Sort((x, y) => x.Project.CompareTo(y.Project));
        var rows = sources
            .Select(s => new TagDeletePlanRow(s.Project, s.Name, s.CreatedAt, IsTagEligible(lifecycle, s.Project)))
            .ToList();
        var deleted = rows.Count(r => r.Eligible);
        var basis = deleted > 0 ? rows.Where(r => r.Eligible) : rows;
        var spelling = basis.GroupBy(r => r.Name, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key).FirstOrDefault() ?? string.Empty;
        var token = TagDeletePlanToken(fromKey, rows);
        return new TagDeletePlan(new TagDeletePreview(fromKey, spelling, deleted, rows.Count - deleted, token), rows);
    }

    /// <summary>
    /// Lowercase hex SHA-256 of the canonical delete plan bytes: the format tag, the length-prefixed key, then per source row in
    /// ProjectNodeId order: id, length-prefixed stored Name, length-prefixed raw CreatedAt text and eligibility E/X. Nothing else.
    /// </summary>
    private static string TagDeletePlanToken(string fromKey, IReadOnlyList<TagDeletePlanRow> rows)
    {
        static string Lp(string s) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Encoding.UTF8.GetByteCount(s)}:{s}");
        var sb = new StringBuilder();
        sb.Append(TagDeletePlanFormat).Append('\n');
        sb.Append(Lp(fromKey)).Append('\n');
        foreach (var row in rows)
        {
            sb.Append(row.ProjectNodeId.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|')
              .Append(Lp(row.Name)).Append(Lp(row.CreatedAt)).Append(row.Eligible ? 'E' : 'X').Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    // ---- Phase 24: Projects with this tag (proposal Revision 2 + R2a §3.3) -----------------------------------------------------

    public Task<TaggedProjectsResult> ListProjectsWithTagAsync(string nameKey, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nameKey);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        return Run(connection =>
        {
            // ONE snapshot: the key's rows, each Project's effective state, name and folder path, all inside one deferred read.
            using var transaction = connection.BeginTransaction(deferred: true);
            var lifecycle = new NodeLifecycle(connection, transaction, MaxDepth);
            var sources = new List<(long Project, string Spelling)>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT ProjectNodeId, Name FROM ProjectTags WHERE NameKey = $key;";
                command.Parameters.AddWithValue("$key", nameKey);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    sources.Add((reader.GetInt64(0), reader.GetString(1)));
            }

            var listed = new List<(TaggedProjectOrder.Key Key, string Spelling)>(sources.Count);
            foreach (var (project, spelling) in sources)
            {
                // A ProjectTags row always has its Project (foreign key); a missing node fails closed like the other tag reads.
                var chain = lifecycle.Chain(project) ?? throw new InvalidDataException("A tagged Project node is missing.");
                var effective = NodeLifecycle.Of(chain);
                var state = effective.State switch
                {
                    LifecycleState.Trashed => TaggedProjectState.Trashed,
                    LifecycleState.Archived => TaggedProjectState.Archived,
                    _ => TaggedProjectState.Live,
                };
                var folders = chain.Take(chain.Count - 1).Select(r => r.Name).ToList();
                listed.Add((new TaggedProjectOrder.Key(state, folders, chain[^1].Name, project), spelling));
            }

            transaction.Commit();

            // Totals over ALL rows before the cap; then the total order (§3.3) on the full set; then the first `limit` rows.
            var trashed = listed.Count(r => r.Key.State == TaggedProjectState.Trashed);
            var rows = listed
                .OrderBy(r => r.Key, TaggedProjectOrder.Instance)
                .Take(limit)
                .Select(r => new TaggedProject(r.Key.ProjectNodeId, r.Key.Name, WorkspaceSearchText.JoinPath(r.Key.Folders), r.Spelling, r.Key.State))
                .ToList();
            return new TaggedProjectsResult(rows, listed.Count - trashed, trashed);
        }, cancellationToken);
    }

    /// <summary>R2a: the stored tag set equals <paramref name="baseline"/> as ordinal (NameKey, Name) pairs (CreatedAt not compared).</summary>
    private static bool TagSetEquals(SqliteConnection connection, SqliteTransaction transaction, long nodeId, IReadOnlyList<ProjectTag> baseline)
    {
        var stored = new HashSet<(string Key, string Name)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT NameKey, Name FROM ProjectTags WHERE ProjectNodeId = $id;";
            command.Parameters.AddWithValue("$id", nodeId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                stored.Add((reader.GetString(0), reader.GetString(1)));
        }

        return stored.SetEquals(baseline.Select(t => (t.NameKey, t.Name)));
    }
}
