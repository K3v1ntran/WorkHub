using Microsoft.Data.Sqlite;
using WorkHub.Core.Workspace;

namespace WorkHub.Infrastructure.Persistence;

/// <summary>One Workspace node's own lifecycle fields (schema v9), exactly as stored.</summary>
internal sealed record LifecycleNodeRow(
    long Id,
    long? ParentId,
    WorkspaceNodeType Type,
    string Name,
    string? TrashedAt,
    string? FolderTrashedAt,
    string? FolderArchivedAt,
    long LifecycleSerial,
    string? Status,
    long? ArchiveSerial)
{
    /// <summary>T(x): a trashed Project or a trashed Folder.</summary>
    public bool Trashed => TrashedAt is not null || FolderTrashedAt is not null;

    /// <summary>A(x): an archived Project or an archived Folder.</summary>
    public bool Archived => FolderArchivedAt is not null || Status == "Archived";

    public LifecycleMarkers Markers => new(Id, Type, Trashed, Archived);
}

/// <summary>
/// The store's <c>NodeLifecycle</c> (Phase 13 proposal revision 4 §7.6): reads a node and its ancestor chain inside the CALLER'S
/// transaction and evaluates <see cref="EffectiveLifecycle.Of"/> on it, so a write checks the effective state it is about to
/// change under the same lock. Rows are memoized per instance (one instance per transaction), so listings that evaluate many
/// nodes read each shared ancestor once. A broken or runaway chain fails closed (<see cref="InvalidDataException"/>).
/// </summary>
internal sealed class NodeLifecycle(SqliteConnection connection, SqliteTransaction? transaction, int maxDepth)
{
    private readonly Dictionary<long, LifecycleNodeRow?> _rows = [];

    internal const string Columns =
        "n.Id, n.ParentId, n.NodeType, n.Name, n.TrashedAt, n.FolderTrashedAt, n.FolderArchivedAt, n.LifecycleSerial, p.Status, p.ArchiveSerial";

    internal const string From = "FROM WorkspaceNodes AS n LEFT JOIN Projects AS p ON p.NodeId = n.Id";

    /// <summary>The node's own row, or null when it does not exist.</summary>
    public LifecycleNodeRow? Row(long id)
    {
        if (_rows.TryGetValue(id, out var cached))
            return cached;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Columns} {From} WHERE n.Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        var row = reader.Read() ? Read(reader) : null;
        _rows[id] = row;
        return row;
    }

    /// <summary>Rows read by another query of the same transaction (same column order as <see cref="Columns"/>).</summary>
    public void Remember(LifecycleNodeRow row) => _rows[row.Id] = row;

    /// <summary>The top-level node first, the node itself last; null when the node does not exist.</summary>
    /// <exception cref="InvalidDataException">An ancestor is missing, or the chain exceeds the depth bound.</exception>
    public IReadOnlyList<LifecycleNodeRow>? Chain(long id)
    {
        if (Row(id) is not { } self)
            return null;

        var chain = new List<LifecycleNodeRow> { self };
        var current = self;
        while (current.ParentId is { } parentId)
        {
            if (chain.Count > maxDepth)
                throw new InvalidDataException($"The Workspace ancestry of node {id} could not be verified (depth bound).");
            current = Row(parentId) ?? throw new InvalidDataException($"The Workspace ancestry of node {id} is broken.");
            chain.Add(current);
        }

        chain.Reverse();
        return chain;
    }

    /// <summary>The node's effective lifecycle; null when the node does not exist.</summary>
    public EffectiveLifecycle? Effective(long id) => Chain(id) is { } chain ? Of(chain) : null;

    public static EffectiveLifecycle Of(IReadOnlyList<LifecycleNodeRow> chain) => EffectiveLifecycle.Of(chain.Select(r => r.Markers).ToList());

    /// <summary>The effective lifecycle of the node's parent (its strict ancestors): Live at the top level.</summary>
    public static EffectiveLifecycle OfParent(IReadOnlyList<LifecycleNodeRow> chain) => Of(chain.Take(chain.Count - 1).ToList());

    /// <summary>Node Ids from <paramref name="state"/>'s root down to the last node of the chain; empty when live.</summary>
    public static IReadOnlyList<long> FromRoot(IReadOnlyList<LifecycleNodeRow> chain, EffectiveLifecycle state)
    {
        if (state.RootId is not { } root)
            return [];
        return chain.SkipWhile(r => r.Id != root).Select(r => r.Id).ToList();
    }

    internal static LifecycleNodeRow Read(SqliteDataReader reader, int offset = 0) => new(
        reader.GetInt64(offset),
        reader.IsDBNull(offset + 1) ? null : reader.GetInt64(offset + 1),
        reader.GetString(offset + 2) == "Project" ? WorkspaceNodeType.Project : WorkspaceNodeType.Folder,
        reader.GetString(offset + 3),
        reader.IsDBNull(offset + 4) ? null : reader.GetString(offset + 4),
        reader.IsDBNull(offset + 5) ? null : reader.GetString(offset + 5),
        reader.IsDBNull(offset + 6) ? null : reader.GetString(offset + 6),
        reader.GetInt64(offset + 7),
        reader.IsDBNull(offset + 8) ? null : reader.GetString(offset + 8),
        reader.IsDBNull(offset + 9) ? null : reader.GetInt64(offset + 9));
}
