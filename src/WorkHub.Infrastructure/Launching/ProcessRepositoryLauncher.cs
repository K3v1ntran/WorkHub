using System.ComponentModel;
using System.Diagnostics;
using WorkHub.Core.Repositories;

namespace WorkHub.Infrastructure.Launching;

/// <summary>Seam over <see cref="Process.Start(ProcessStartInfo)"/> so tests never launch real programs.</summary>
public interface IProcessStarter
{
    void Start(ProcessStartInfo startInfo);
}

public sealed class ProcessStarter : IProcessStarter
{
    public void Start(ProcessStartInfo startInfo)
    {
        using var _ = Process.Start(startInfo);
    }
}

/// <summary>
/// Opens repository targets. SVN targets go to TortoiseProc.exe started directly (no shell) with each argument
/// passed through <see cref="ProcessStartInfo.ArgumentList"/>, so .NET handles quoting. Browser targets must be
/// http(s) URLs; they are handed to the shell only as a normalized absolute URI, which cannot run a local program.
/// </summary>
public sealed class ProcessRepositoryLauncher(IProcessStarter? starter = null, Func<string, bool>? fileExists = null) : IRepositoryLauncher
{
    public const string TortoiseMissingMessage = "TortoiseSVN is not configured or cannot be found.";

    private readonly IProcessStarter _starter = starter ?? new ProcessStarter();
    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;

    public LaunchResult Launch(LaunchRequest request)
    {
        var (startInfo, problem) = BuildStartInfo(request, _fileExists);
        if (problem is not null)
            return problem;

        try
        {
            _starter.Start(startInfo!);
            return LaunchResult.Ok;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            var program = request.Kind == LaunchKind.TortoiseSvn ? "TortoiseSVN" : "the default browser";
            return new LaunchResult(LaunchProblemKind.StartFailed, $"Could not start {program}: {ex.Message}",
                request.Kind == LaunchKind.TortoiseSvn ? SettingsSection.Svn : null);
        }
    }

    public static (ProcessStartInfo? StartInfo, LaunchResult? Problem) BuildStartInfo(LaunchRequest request, Func<string, bool> fileExists)
    {
        switch (request.Kind)
        {
            case LaunchKind.Browser:
                if (!RepositoryUrlRules.IsValidTarget(request.Target, VersionControlKind.Git))
                    return (null, InvalidTarget("Only http:// and https:// targets can be opened in the browser."));

                return (new ProcessStartInfo(new Uri(request.Target).AbsoluteUri) { UseShellExecute = true }, null);

            case LaunchKind.TortoiseSvn:
                var exe = request.TortoiseProcPath?.Trim().Trim('"') ?? string.Empty;
                if (exe.Length == 0)
                    return (null, new LaunchResult(LaunchProblemKind.TortoiseNotConfigured, TortoiseMissingMessage, SettingsSection.Svn));
                if (!Path.IsPathFullyQualified(exe) || !fileExists(exe))
                    return (null, new LaunchResult(LaunchProblemKind.TortoiseNotFound, TortoiseMissingMessage, SettingsSection.Svn));
                if (!RepositoryUrlRules.IsValidTarget(request.Target, VersionControlKind.Svn))
                    return (null, InvalidTarget("The SVN target is not a valid repository URL."));

                var startInfo = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(exe)!,
                };
                startInfo.ArgumentList.Add("/command:repobrowser");
                startInfo.ArgumentList.Add("/path:" + new Uri(request.Target).AbsoluteUri);
                return (startInfo, null);

            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Kind, null);
        }
    }

    private static LaunchResult InvalidTarget(string message) => new(LaunchProblemKind.InvalidTarget, message, null);
}
