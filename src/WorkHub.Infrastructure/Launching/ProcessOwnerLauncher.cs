using System.ComponentModel;
using System.Diagnostics;
using WorkHub.Core.Owner;

namespace WorkHub.Infrastructure.Launching;

/// <summary>
/// Opens Owner items. Shares only the low-level <see cref="IProcessStarter"/> with the Repository launcher;
/// there is no target resolution here, the stored Target is used as entered.
/// <list type="bullet">
/// <item>Web: http(s) only, handed to the shell as a normalized absolute URI (default browser).</item>
/// <item>File / Folder: shell "open" of the path (default application / Explorer) after an existence check.</item>
/// <item>Executable: started directly (UseShellExecute = false). The program path is the FileName and the user's
/// argument text is passed verbatim as the process command-line tail — no cmd.exe, no PowerShell, no concatenation
/// into a shell command.</item>
/// </list>
/// Missing targets produce a result, never an exception, and never modify or delete anything.
/// </summary>
public sealed class ProcessOwnerLauncher(
    IProcessStarter? starter = null,
    Func<string, bool>? fileExists = null,
    Func<string, bool>? directoryExists = null) : IOwnerLauncher
{
    private readonly IProcessStarter _starter = starter ?? new ProcessStarter();
    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;
    private readonly Func<string, bool> _directoryExists = directoryExists ?? Directory.Exists;

    public OwnerLaunchResult Launch(OwnerItemDetails item)
    {
        var (startInfo, problem) = BuildStartInfo(item, _fileExists, _directoryExists);
        if (problem is not null)
            return problem;

        try
        {
            _starter.Start(startInfo!);
            return OwnerLaunchResult.Ok;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return new OwnerLaunchResult(OwnerLaunchProblem.StartFailed, $"The target could not be opened ({ex.Message}).");
        }
    }

    public static (ProcessStartInfo? StartInfo, OwnerLaunchResult? Problem) BuildStartInfo(
        OwnerItemDetails item, Func<string, bool> fileExists, Func<string, bool> directoryExists)
    {
        var target = item.Target.Trim();
        switch (item.ActionType)
        {
            case OwnerActionType.Web:
                if (!OwnerTargetRules.IsSafeWebUrl(target))
                    return Fail(OwnerLaunchProblem.InvalidTarget, "Only http:// and https:// addresses can be opened.");
                return (new ProcessStartInfo(new Uri(target).AbsoluteUri) { UseShellExecute = true }, null);

            case OwnerActionType.File:
                if (!OwnerTargetRules.IsLocalPath(target))
                    return Fail(OwnerLaunchProblem.InvalidTarget, "The file path is not valid.");
                if (!fileExists(target))
                    return Fail(OwnerLaunchProblem.TargetMissing, "File could not be found.");
                return (new ProcessStartInfo(target) { UseShellExecute = true }, null);

            case OwnerActionType.Folder:
                if (!OwnerTargetRules.IsLocalPath(target))
                    return Fail(OwnerLaunchProblem.InvalidTarget, "The folder path is not valid.");
                if (!directoryExists(target))
                    return Fail(OwnerLaunchProblem.TargetMissing, "Folder could not be found.");
                return (new ProcessStartInfo(target) { UseShellExecute = true }, null);

            case OwnerActionType.Executable:
                if (!OwnerTargetRules.IsExecutablePath(target))
                    return Fail(OwnerLaunchProblem.InvalidTarget, "The program path is not a valid .exe path.");
                if (!fileExists(target))
                    return Fail(OwnerLaunchProblem.TargetMissing, "Program could not be found.");

                var workingDirectory = string.IsNullOrWhiteSpace(item.WorkingDirectory) ? null : item.WorkingDirectory.Trim();
                if (workingDirectory is not null && (!OwnerTargetRules.IsLocalPath(workingDirectory) || !directoryExists(workingDirectory)))
                    return Fail(OwnerLaunchProblem.WorkingDirectoryMissing, "Working directory could not be found.");

                if (item.Arguments is { } args && args.Any(char.IsControl))
                    return Fail(OwnerLaunchProblem.InvalidTarget, "Arguments must be a single line.");

                return (new ProcessStartInfo(target)
                {
                    UseShellExecute = false,
                    Arguments = item.Arguments ?? string.Empty,
                    WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(target)!,
                }, null);

            default:
                return Fail(OwnerLaunchProblem.InvalidTarget, "Unknown launcher type.");
        }
    }

    private static (ProcessStartInfo?, OwnerLaunchResult?) Fail(OwnerLaunchProblem problem, string message) =>
        (null, new OwnerLaunchResult(problem, message));
}
