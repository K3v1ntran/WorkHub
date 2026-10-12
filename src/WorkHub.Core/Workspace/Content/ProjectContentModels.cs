namespace WorkHub.Core.Workspace.Content;

/// <summary>What a Project Link points at. Stored as its stable code (<see cref="ProjectLinkTypes.StorageCode"/>).</summary>
public enum ProjectLinkType
{
    Folder,
    File,
    Web,
}

public static class ProjectLinkTypes
{
    public static IReadOnlyList<ProjectLinkType> All { get; } = [ProjectLinkType.Folder, ProjectLinkType.File, ProjectLinkType.Web];

    /// <summary>Stable code stored in <c>ProjectLinks.LinkType</c>. Never change an existing value.</summary>
    public static string StorageCode(ProjectLinkType type) => type switch
    {
        ProjectLinkType.Folder => "Folder",
        ProjectLinkType.File => "File",
        ProjectLinkType.Web => "Web",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static ProjectLinkType FromStorageCode(string code) => code switch
    {
        "Folder" => ProjectLinkType.Folder,
        "File" => ProjectLinkType.File,
        "Web" => ProjectLinkType.Web,
        _ => throw new FormatException($"Unknown link type '{code}'."),
    };
}

/// <summary>A logical folder inside one Project. <see cref="ParentFolderId"/> null = Project root. Never a disk folder.</summary>
public sealed record ProjectFolder(
    long Id,
    long ProjectNodeId,
    long? ParentFolderId,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A reference to a folder, file or web page. <see cref="FolderId"/> null = Project root. Never moves or changes the target.</summary>
public sealed record ProjectLink(
    long Id,
    long ProjectNodeId,
    long? FolderId,
    ProjectLinkType Type,
    string Name,
    string Target,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>The link is in the Trash (schema v6). Only single-item reads return trashed links; listings never do.</summary>
    public bool IsTrashed { get; init; }

    /// <summary>The link's Project is in the Workspace Trash (schema v7). Set by single-item reads only.</summary>
    public bool IsProjectTrashed { get; init; }
}

/// <summary>User-editable Link fields (validated by <see cref="ProjectLinkValidator"/>).</summary>
public sealed record ProjectLinkDraft(string? Name, ProjectLinkType Type, string? Target);

/// <summary>One step of the path from the Project root to a ProjectFolder (root first).</summary>
public sealed record ProjectFolderCrumb(long Id, string Name);

/// <summary>Where a container is: the Project's name and the folder path below it (root first; empty = Project root).</summary>
public sealed record ProjectContainerPath(string ProjectName, IReadOnlyList<ProjectFolderCrumb> Folders)
{
    /// <summary>"Alpha Portal › Specs", the same form as the Content breadcrumb.</summary>
    public string Display => string.Join(" › ", Folders.Select(f => f.Name).Prepend(ProjectName));
}

/// <summary>The direct contents of one container (Project root or one ProjectFolder): folders, links and note summaries.</summary>
public sealed record ProjectContentListing(
    long ProjectNodeId,
    long? FolderId,
    IReadOnlyList<ProjectFolderCrumb> Breadcrumb,
    IReadOnlyList<ProjectFolder> Folders,
    IReadOnlyList<ProjectLink> Links,
    IReadOnlyList<ProjectNoteSummary> Notes)
{
    /// <summary>How many entries the Project's Trash holds, from the same read snapshot (schema v6).</summary>
    public int TrashCount { get; init; }
}

public enum ContentContainerProblem
{
    ProjectNotFound,
    FolderNotFound,

    /// <summary>The folder exists but belongs to another Project.</summary>
    WrongProject,

    /// <summary>The folder exists but is in the Trash (schema v6): nothing can be listed in, created in or moved into it.</summary>
    FolderTrashed,

    /// <summary>The Project is in the Workspace Trash (schema v7): its content is read-only and not listed.</summary>
    ProjectTrashed,
}

/// <summary>A listing, or why the container could not be listed.</summary>
public sealed record ProjectContentListResult(ProjectContentListing? Listing, ContentContainerProblem? Problem)
{
    public static ProjectContentListResult Ok(ProjectContentListing listing) => new(listing, null);

    public static ProjectContentListResult Failed(ContentContainerProblem problem) => new(null, problem);
}

/// <summary>
/// Store-level outcome of a content move (revision 3 §8.1). Evaluated in this order inside one write transaction:
/// NotFound → ItemTrashed → MovedMeanwhile → DestinationNotFound / WrongProject → DestinationTrashed → Cycle /
/// AncestryUnverified → AlreadyThere → Duplicate / Moved.
/// </summary>
public enum ContentMoveOutcome
{
    Moved,

    /// <summary>Already in the destination; nothing written (no timestamp change).</summary>
    AlreadyThere,

    NotFound,

    /// <summary>The item is in the Trash (schema v6); trashed rows are read-only.</summary>
    ItemTrashed,

    /// <summary>The destination folder is in the Trash.</summary>
    DestinationTrashed,

    /// <summary>The item's current container is not the one the caller observed. Takes precedence over AlreadyThere.</summary>
    MovedMeanwhile,

    DestinationNotFound,

    /// <summary>The destination folder belongs to another Project.</summary>
    WrongProject,

    /// <summary>The destination is the moving folder or inside it.</summary>
    Cycle,

    /// <summary>The ancestry walk hit its depth bound or a broken chain; the move is refused (fails closed).</summary>
    AncestryUnverified,

    /// <summary>The destination already has a sibling folder with the same name key.</summary>
    Duplicate,

    /// <summary>The item's Project is in the Workspace Trash (checked first); nothing written.</summary>
    ProjectTrashed,
}

/// <summary>Service-level move result with a user-facing message for failures.</summary>
public sealed record ContentMoveResult(ContentMoveOutcome Outcome, string? Error)
{
    public bool Succeeded => Outcome is ContentMoveOutcome.Moved or ContentMoveOutcome.AlreadyThere;

    public bool Moved => Outcome == ContentMoveOutcome.Moved;

    /// <summary>The caller acted on a stale view: every retry from the same dialog would conflict again.</summary>
    public bool IsStale => Outcome == ContentMoveOutcome.MovedMeanwhile;
}
