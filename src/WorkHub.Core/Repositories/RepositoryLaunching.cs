using WorkHub.Core.Settings;

namespace WorkHub.Core.Repositories;

public enum LaunchKind
{
    /// <summary>TortoiseProc.exe repository browser (SVN SYSTEM, SVN ORACLE, RELEASE).</summary>
    TortoiseSvn,

    /// <summary>Default system browser (GIT SYSTEM, GIT ORACLE).</summary>
    Browser,
}

public sealed record LaunchRequest(LaunchKind Kind, string Target, string? TortoiseProcPath);

public enum LaunchProblemKind
{
    TortoiseNotConfigured,
    TortoiseNotFound,
    InvalidTarget,
    StartFailed,
}

public sealed record LaunchResult(LaunchProblemKind? Problem, string? Message, SettingsSection? FixIn)
{
    public bool Succeeded => Problem is null;

    public static LaunchResult Ok { get; } = new(null, null, null);
}

/// <summary>Starts external programs for repository targets. Implementations must never go through a command shell.</summary>
public interface IRepositoryLauncher
{
    LaunchResult Launch(LaunchRequest request);
}

public sealed record OpenRepositoryResult(bool Succeeded, string? Target, string? Message, SettingsSection? FixIn);

/// <summary>Resolve, then launch. Keeps target building and process starting out of view models.</summary>
public sealed class RepositoryOpenService(ISettingsService settings, IRepositoryLauncher launcher)
{
    public RepositoryTarget Resolve(RepositoryType type, SystemListItem system) =>
        RepositoryTargetResolver.Resolve(type, system.SystemCode, system.CustomTarget, settings.Current.Repository);

    public async Task<OpenRepositoryResult> OpenAsync(RepositoryType type, SystemListItem system)
    {
        var target = Resolve(type, system);
        if (target.Problem is { } problem)
            return new OpenRepositoryResult(false, null, problem.Message, problem.FixIn);

        var request = new LaunchRequest(
            target.VersionControl == VersionControlKind.Svn ? LaunchKind.TortoiseSvn : LaunchKind.Browser,
            target.Target!,
            settings.Current.Repository.Svn.TortoiseProcPath);

        // Process start can block briefly (file checks, shell association lookup): keep it off the UI thread.
        var result = await Task.Run(() => launcher.Launch(request)).ConfigureAwait(false);
        return new OpenRepositoryResult(result.Succeeded, target.Target, result.Message, result.FixIn);
    }
}
