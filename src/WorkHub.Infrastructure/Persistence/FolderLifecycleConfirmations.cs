using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WorkHub.Core.Workspace;

namespace WorkHub.Infrastructure.Persistence;

/// <summary>
/// <c>ComputeLifecycleConfirmation(rootId)</c> (Phase 13 proposal revision 4 §7.4, version <c>P13-FLC-3</c>). ONE computation
/// produces both the preview the dialog shows and the token: it runs in a read transaction when the dialog opens and again
/// inside the commit's <c>BEGIN IMMEDIATE</c> transaction. The token is SHA-256 over a deterministic, culture-independent
/// serialization in which every string carries its length and every value is self-delimiting, so no two different inputs
/// serialize equally:
/// <list type="bullet">
/// <item>root: Id, ParentId, NodeType, the ORIGINAL Name, FolderArchivedAt, FolderTrashedAt, LifecycleSerial;</item>
/// <item>restore path: the ancestors top-down, (Id, original Name);</item>
/// <item>displayed names: the shown direct children in tree order, (Id, original Name);</item>
/// <item>node set: every descendant by Id: Id, ParentId, NodeType, and its lifecycle identity — a Folder's FolderArchivedAt,
/// FolderTrashedAt and LifecycleSerial; a Project's archived flag, ArchiveSerial and TrashedAt (current state only: a
/// millisecond timestamp is not an event identity, D17);</item>
/// <item>content set: every content row of every descendant Project by (Kind, Id): Kind, Id, ProjectNodeId, TrashEntryId
/// (the exact entry, never reused: AUTOINCREMENT).</item>
/// </list>
/// Content attributes (note bodies, link targets, Project details, names of descendants that are not shown) are deliberately
/// not part of the token: they are allowed edits.
/// </summary>
internal static class FolderLifecycleConfirmations
{
    /// <param name="chain">The Folder's chain, top-down, ending with the Folder (from <see cref="NodeLifecycle.Chain"/>).</param>
    /// <exception cref="InvalidDataException">The subtree exceeds the depth bound (fails closed).</exception>
    public static LifecycleConfirmation Compute(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<LifecycleNodeRow> chain, FolderLifecycleKind kind, int maxDepth)
    {
        var root = chain[^1];
        var ancestors = chain.Take(chain.Count - 1).ToList();
        var nodes = ReadSubtree(connection, transaction, root.Id, maxDepth);
        var projects = nodes.Where(n => n.Type == WorkspaceNodeType.Project).Select(n => n.Id).ToList();
        var content = ReadContent(connection, transaction, projects);

        var children = nodes.Where(n => n.ParentId == root.Id)
            .OrderBy(n => (n.Type, n.Name, n.Id), WorkspaceNodeOrder.Instance)
            .ToList();
        var shown = children.Take(LifecycleConfirmation.MaxShownChildren).ToList();

        var counts = new LifecycleCounts(
            Folders: nodes.Count(n => n.Type == WorkspaceNodeType.Folder),
            Projects: projects.Count,
            ArchivedProjects: nodes.Count(n => n.Type == WorkspaceNodeType.Project && n.Status == "Archived"),
            TrashedProjects: nodes.Count(n => n.Type == WorkspaceNodeType.Project && n.TrashedAt is not null),
            Links: content.Count(r => r.Kind == "Link"),
            Notes: content.Count(r => r.Kind == "Note"),
            ItemsInProjectTrash: content.Count(r => r.TrashEntryId is not null));

        var token = new TokenWriter();
        token.Text(LifecycleConfirmation.Version);
        token.Text("root").Integer(root.Id).Integer(root.ParentId).Text(TypeCode(root.Type)).Text(root.Name)
            .Text(root.FolderArchivedAt).Text(root.FolderTrashedAt).Integer(root.LifecycleSerial);
        token.Text("path").Integer(ancestors.Count);
        foreach (var ancestor in ancestors)
            token.Integer(ancestor.Id).Text(ancestor.Name);
        token.Text("shown").Integer(shown.Count);
        foreach (var child in shown)
            token.Integer(child.Id).Text(child.Name);
        token.Text("nodes").Integer(nodes.Count);
        foreach (var node in nodes) // ordered by Id (the query's ORDER BY)
        {
            token.Integer(node.Id).Integer(node.ParentId).Text(TypeCode(node.Type));
            if (node.Type == WorkspaceNodeType.Folder)
                token.Text(node.FolderArchivedAt).Text(node.FolderTrashedAt).Integer(node.LifecycleSerial);
            else
                token.Integer(node.Status == "Archived" ? 1 : 0).Integer(node.ArchiveSerial).Text(node.TrashedAt);
        }

        token.Text("content").Integer(content.Count);
        foreach (var row in content) // ordered by (Kind, Id)
            token.Text(row.Kind).Integer(row.Id).Integer(row.ProjectNodeId).Integer(row.TrashEntryId);

        return new LifecycleConfirmation(
            root.Id,
            kind,
            root.Name,
            ancestors.Count == 0 ? null : string.Join(" / ", ancestors.Select(a => a.Name)),
            counts,
            shown.Select(c => c.Name).ToList(),
            children.Count - shown.Count,
            token.Hash());
    }

    private static string TypeCode(WorkspaceNodeType type) => type == WorkspaceNodeType.Folder ? "Folder" : "Project";

    /// <summary>Every descendant (any depth, any state), ordered by Id.</summary>
    private static List<LifecycleNodeRow> ReadSubtree(SqliteConnection connection, SqliteTransaction transaction, long rootId, int maxDepth)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            WITH RECURSIVE sub (Id, Depth) AS (
                SELECT Id, 1 FROM WorkspaceNodes WHERE ParentId = $root
                UNION ALL
                SELECT c.Id, s.Depth + 1 FROM WorkspaceNodes AS c JOIN sub AS s ON c.ParentId = s.Id
                WHERE s.Depth < $maxDepth
            )
            SELECT {NodeLifecycle.Columns}, s.Depth
            FROM sub AS s JOIN WorkspaceNodes AS n ON n.Id = s.Id LEFT JOIN Projects AS p ON p.NodeId = n.Id
            ORDER BY n.Id;
            """;
        command.Parameters.AddWithValue("$root", rootId);
        command.Parameters.AddWithValue("$maxDepth", maxDepth);
        using var reader = command.ExecuteReader();
        var rows = new List<LifecycleNodeRow>();
        while (reader.Read())
        {
            if (reader.GetInt64(10) >= maxDepth)
                throw new InvalidDataException("The folder's subtree could not be verified (depth bound); nothing was changed.");
            rows.Add(NodeLifecycle.Read(reader));
        }

        return rows;
    }

    private sealed record ContentRow(string Kind, long Id, long ProjectNodeId, long? TrashEntryId);

    /// <summary>Every content row of the given Projects, ordered by (Kind, Id): Folder, Link, Note.</summary>
    private static List<ContentRow> ReadContent(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<long> projects)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 'Folder', Id, ProjectNodeId, TrashEntryId FROM ProjectFolders WHERE ProjectNodeId IN (SELECT value FROM json_each($ids))
            UNION ALL
            SELECT 'Link', Id, ProjectNodeId, TrashEntryId FROM ProjectLinks WHERE ProjectNodeId IN (SELECT value FROM json_each($ids))
            UNION ALL
            SELECT 'Note', Id, ProjectNodeId, TrashEntryId FROM ProjectNotes WHERE ProjectNodeId IN (SELECT value FROM json_each($ids))
            ORDER BY 1, 2;
            """;
        command.Parameters.AddWithValue("$ids", "[" + string.Join(",", projects.Select(id => id.ToString(CultureInfo.InvariantCulture))) + "]");
        using var reader = command.ExecuteReader();
        var rows = new List<ContentRow>();
        while (reader.Read())
            rows.Add(new ContentRow(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetInt64(3)));
        return rows;
    }

    /// <summary>
    /// Self-delimiting tokens: <c>N;</c> for NULL, <c>I{invariant decimal};</c> for integers, <c>S{UTF-16 length}:{text};</c> for
    /// strings (the length decides where the text ends, so separators inside names cannot be confused with structure).
    /// </summary>
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
