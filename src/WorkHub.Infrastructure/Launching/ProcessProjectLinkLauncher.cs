using System.ComponentModel;
using System.Diagnostics;
using WorkHub.Core.Owner;
using WorkHub.Core.Workspace.Content;

namespace WorkHub.Infrastructure.Launching;

/// <summary>
/// Opens Project Links with the same rules as Owner launchers (shares only <see cref="IProcessStarter"/> and
/// <see cref="OwnerTargetRules"/>; Owner behavior is unchanged):
/// <list type="bullet">
/// <item>Web: http(s) only, handed to the shell as a normalized absolute URI (default browser).</item>
/// <item>File / Folder: shell "open" of the stored path (default application / Explorer) after an existence check.</item>
/// </list>
/// Never routes through cmd.exe or PowerShell. Missing targets produce a result, never an exception, and nothing is
/// ever created, modified, moved or deleted.
/// </summary>
public sealed class ProcessProjectLinkLauncher(
    IProcessStarter? starter = null,
    Func<string, bool>? fileExists = null,
    Func<string, bool>? directoryExists = null) : IProjectLinkLauncher
{
    private readonly IProcessStarter _starter = starter ?? new ProcessStarter();
    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;
    private readonly Func<string, bool> _directoryExists = directoryExists ?? Directory.Exists;

    public ProjectLinkOpenResult Launch(ProjectLink link)
    {
        var (startInfo, problem) = BuildStartInfo(link, _fileExists, _directoryExists);
        if (problem is not null)
            return problem;

        try
        {
            _starter.Start(startInfo!);
            return ProjectLinkOpenResult.Ok;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return new ProjectLinkOpenResult(ProjectLinkOpenProblem.StartFailed, $"The target could not be opened ({ex.Message}).");
        }
    }

    public static (ProcessStartInfo? StartInfo, ProjectLinkOpenResult? Problem) BuildStartInfo(
        ProjectLink link, Func<string, bool> fileExists, Func<string, bool> directoryExists)
    {
        var target = link.Target.Trim();
        switch (link.Type)
        {
            case ProjectLinkType.Web:
                if (!OwnerTargetRules.IsSafeWebUrl(target))
                    return Fail(ProjectLinkOpenProblem.InvalidTarget, "Only http:// and https:// addresses can be opened.");
                return (new ProcessStartInfo(new Uri(target).AbsoluteUri) { UseShellExecute = true }, null);

            case ProjectLinkType.File:
                if (!OwnerTargetRules.IsLocalPath(target))
                    return Fail(ProjectLinkOpenProblem.InvalidTarget, "The file path is not valid.");
                if (!fileExists(target))
                    return Fail(ProjectLinkOpenProblem.TargetMissing, "File could not be found.");
                return (new ProcessStartInfo(target) { UseShellExecute = true }, null);

            case ProjectLinkType.Folder:
                if (!OwnerTargetRules.IsLocalPath(target))
                    return Fail(ProjectLinkOpenProblem.InvalidTarget, "The folder path is not valid.");
                if (!directoryExists(target))
                    return Fail(ProjectLinkOpenProblem.TargetMissing, "Folder could not be found.");
                return (new ProcessStartInfo(target) { UseShellExecute = true }, null);

            default:
                return Fail(ProjectLinkOpenProblem.InvalidTarget, "Unknown link type.");
        }
    }

    private static (ProcessStartInfo?, ProjectLinkOpenResult?) Fail(ProjectLinkOpenProblem problem, string message) =>
        (null, new ProjectLinkOpenResult(problem, message));
}
