namespace WorkHub.Core.Owner;

public enum OwnerLaunchProblem
{
    /// <summary>The stored target is not valid for its type (should not happen for items saved through validation).</summary>
    InvalidTarget,

    /// <summary>The file, folder or program is not reachable right now (deleted, moved, drive offline).</summary>
    TargetMissing,

    WorkingDirectoryMissing,

    /// <summary>Windows refused to start it (no associated app, access denied, ...).</summary>
    StartFailed,

    /// <summary>The launcher entry was deleted meanwhile.</summary>
    ItemNotFound,
}

public sealed record OwnerLaunchResult(OwnerLaunchProblem? Problem, string? Message)
{
    public bool Succeeded => Problem is null;

    public static OwnerLaunchResult Ok { get; } = new(null, null);
}

/// <summary>
/// Opens an Owner item's target directly (no target resolution). Implementations must never route through a
/// command shell (no cmd.exe /c, no PowerShell).
/// </summary>
public interface IOwnerLauncher
{
    OwnerLaunchResult Launch(OwnerItemDetails item);
}
