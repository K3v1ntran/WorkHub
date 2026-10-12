namespace WorkHub.Core.Workspace.Content;

public enum ProjectLinkOpenProblem
{
    /// <summary>The stored target is not valid for its type.</summary>
    InvalidTarget,

    /// <summary>The file or folder is not reachable right now.</summary>
    TargetMissing,

    /// <summary>Windows refused to open it.</summary>
    StartFailed,

    /// <summary>The link no longer exists.</summary>
    LinkNotFound,

    /// <summary>The link is in the Trash (schema v6).</summary>
    LinkTrashed,
}

public sealed record ProjectLinkOpenResult(ProjectLinkOpenProblem? Problem, string? Message)
{
    public bool Succeeded => Problem is null;

    public static ProjectLinkOpenResult Ok { get; } = new(null, null);
}

/// <summary>Opens a Link's target (Explorer, default app, default browser). Never through a command shell; never modifies the target.</summary>
public interface IProjectLinkLauncher
{
    ProjectLinkOpenResult Launch(ProjectLink link);
}

/// <summary>Manual health-check statuses (revision 3 §6.3). Only <see cref="Missing"/> means "confirmed not there".</summary>
public enum LinkHealthStatus
{
    NotChecked,
    Checking,

    /// <summary>Exists and is the expected kind.</summary>
    Found,

    /// <summary>Exists but is the other kind (a folder for a File link, or a file for a Folder link).</summary>
    WrongType,

    /// <summary>The location answered: not found.</summary>
    Missing,

    /// <summary>Access denied or credentials required; existence unknown.</summary>
    Inaccessible,

    /// <summary>The server, share or drive did not answer; existence unknown.</summary>
    Unreachable,

    /// <summary>The OS call started but did not return within the probe timeout; existence unknown.</summary>
    TimedOut,

    /// <summary>Not probed: an earlier probe of the same drive/server is still blocked.</summary>
    LocationBusy,

    /// <summary>Not probed: no execution capacity within the admission bound, or all slots held by stuck calls.</summary>
    CheckerBusy,

    /// <summary>Unexpected error; existence unknown.</summary>
    Unknown,

    /// <summary>The stored target fails the syntax rules; never probed.</summary>
    InvalidTarget,

    /// <summary>Web link: WorkHub never contacts the network.</summary>
    NotCheckedWeb,
}

public static class LinkHealthStatuses
{
    public static string DisplayName(LinkHealthStatus status) => status switch
    {
        LinkHealthStatus.NotChecked => "Not checked",
        LinkHealthStatus.Checking => "Checking…",
        LinkHealthStatus.Found => "Found",
        LinkHealthStatus.WrongType => "Wrong type",
        LinkHealthStatus.Missing => "Missing",
        LinkHealthStatus.Inaccessible => "Inaccessible",
        LinkHealthStatus.Unreachable => "Unreachable",
        LinkHealthStatus.TimedOut => "Timed out",
        LinkHealthStatus.LocationBusy => "Location busy",
        LinkHealthStatus.CheckerBusy => "Checker busy",
        LinkHealthStatus.Unknown => "Unknown",
        LinkHealthStatus.InvalidTarget => "Invalid target",
        LinkHealthStatus.NotCheckedWeb => "Not checked (web)",
        _ => status.ToString(),
    };

    /// <summary>True for statuses that mean "could not be verified" rather than a confirmed answer.</summary>
    public static bool IsUnverified(LinkHealthStatus status) => status is LinkHealthStatus.Inaccessible or LinkHealthStatus.Unreachable
        or LinkHealthStatus.TimedOut or LinkHealthStatus.LocationBusy or LinkHealthStatus.CheckerBusy or LinkHealthStatus.Unknown;
}

/// <summary>
/// One application-wide manual checker for Folder/File link targets (revision 3 §6.4). Read-only; never polls.
/// Cancelling the token removes a queued request; a running OS call cannot be interrupted and its result is discarded.
/// </summary>
public interface ILinkHealthChecker
{
    /// <exception cref="OperationCanceledException">The request was cancelled before it completed.</exception>
    Task<LinkHealthStatus> CheckAsync(ProjectLinkType type, string target, CancellationToken cancellationToken = default);
}
