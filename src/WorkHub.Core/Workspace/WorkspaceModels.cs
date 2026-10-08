namespace WorkHub.Core.Workspace;

/// <summary>Main Workspace tree node kinds. ProjectFolders, Links and Notes are never tree nodes.</summary>
public enum WorkspaceNodeType
{
    Folder,
    Project,
}

/// <summary>Fixed Project status vocabulary. <see cref="Archived"/> is persisted vocabulary only in Phase 4.</summary>
public enum ProjectStatus
{
    Planning,
    Active,
    Maintenance,
    OnHold,
    Completed,
    Archived,
}

public static class ProjectStatuses
{
    public const ProjectStatus Default = ProjectStatus.Active;

    /// <summary>Statuses a user may choose. Archived is not selectable until Archive behavior exists.</summary>
    public static IReadOnlyList<ProjectStatus> Editable { get; } =
        [ProjectStatus.Planning, ProjectStatus.Active, ProjectStatus.Maintenance, ProjectStatus.OnHold, ProjectStatus.Completed];

    public static bool IsEditable(ProjectStatus status) => Editable.Contains(status);

    /// <summary>Stable code stored in <c>Projects.Status</c>. Never change an existing value.</summary>
    public static string StorageCode(ProjectStatus status) => status switch
    {
        ProjectStatus.Planning => "Planning",
        ProjectStatus.Active => "Active",
        ProjectStatus.Maintenance => "Maintenance",
        ProjectStatus.OnHold => "OnHold",
        ProjectStatus.Completed => "Completed",
        ProjectStatus.Archived => "Archived",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static ProjectStatus FromStorageCode(string code)
    {
        foreach (var status in Enum.GetValues<ProjectStatus>())
        {
            if (StorageCode(status) == code)
                return status;
        }

        throw new FormatException($"Unknown project status '{code}'.");
    }

    public static string DisplayName(ProjectStatus status) => status == ProjectStatus.OnHold ? "On Hold" : status.ToString();
}

/// <summary>One row of the main tree as listed under a parent.</summary>
public sealed record WorkspaceNode(long Id, long? ParentId, WorkspaceNodeType Type, string Name, bool HasChildren);

/// <summary>A Project node and its details. Name and timestamps belong to the node.</summary>
public sealed record ProjectDetails(
    long NodeId,
    string ProjectId,
    long? ParentId,
    string Name,
    ProjectStatus Status,
    string? Description,
    string? Owner,
    string? ProjectManager,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>User-editable Project fields. Owner/ProjectManager are free text, unrelated to Owner Manager.</summary>
public sealed record ProjectDraft(
    string? Name,
    ProjectStatus Status,
    string? Description = null,
    string? Owner = null,
    string? ProjectManager = null);

/// <summary>A folder offered as a Move destination, with its path from the root for display.</summary>
public sealed record WorkspaceFolderPath(long Id, string Path);

/// <summary>Outcome of a Workspace write: a value, or a user-facing error (optionally tied to an editor field).</summary>
public sealed record WorkspaceResult<T>(T? Value, string? Field, string? Error)
{
    public bool Succeeded => Error is null;

    public static WorkspaceResult<T> Ok(T value) => new(value, null, null);

    public static WorkspaceResult<T> Failed(string error, string? field = null) => new(default, field, error);
}
