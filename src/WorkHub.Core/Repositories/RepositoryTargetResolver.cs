using WorkHub.Core.Settings;

namespace WorkHub.Core.Repositories;

/// <summary>Which part of Settings fixes a problem; null when the fix is elsewhere (e.g. the System's custom target).</summary>
public enum SettingsSection
{
    Svn,
    Git,
}

public enum TargetProblemKind
{
    HostNotConfigured,
    HostInvalid,
    PrefixNotConfigured,
    PrefixInvalid,
    CustomTargetInvalid,
}

public sealed record TargetProblem(TargetProblemKind Kind, string Message, SettingsSection? FixIn);

/// <summary>Outcome of resolving one System + RepositoryType. Exactly one of Target/Problem is set.</summary>
public sealed record RepositoryTarget(RepositoryType Type, string? Target, bool IsCustom, TargetProblem? Problem)
{
    public bool IsResolved => Target is not null;

    public VersionControlKind VersionControl => RepositoryCatalog.Get(Type).VersionControl;
}

/// <summary>
/// The only place repository targets are built. Used by repository opening and the Settings preview.
/// Rule: a membership's CustomTarget wins; otherwise <c>Host + "/" + Prefix + SystemCode</c>
/// (trailing slashes on the host are collapsed, so both "…/svn" and "…/svn/" work).
/// </summary>
public static class RepositoryTargetResolver
{
    public static RepositoryTarget Resolve(RepositoryType type, string systemCode, string? customTarget, RepositorySettings settings)
    {
        var info = RepositoryCatalog.Get(type);

        if (!string.IsNullOrWhiteSpace(customTarget))
        {
            var custom = customTarget.Trim();
            return RepositoryUrlRules.IsValidTarget(custom, info.VersionControl)
                ? new RepositoryTarget(type, custom, IsCustom: true, Problem: null)
                : Fail(type, TargetProblemKind.CustomTargetInvalid,
                    $"The custom target for {info.DisplayName} is not a valid URL ({RepositoryUrlRules.DescribeSchemes(info.VersionControl)}). Edit the System to fix it.",
                    fixIn: null, isCustom: true);
        }

        var section = info.VersionControl == VersionControlKind.Svn ? SettingsSection.Svn : SettingsSection.Git;
        var hostLabel = HostLabel(info.VersionControl);
        var host = settings.HostFor(type).Trim();
        var prefix = settings.PrefixFor(type).Trim();

        if (host.Length == 0)
            return Fail(type, TargetProblemKind.HostNotConfigured, $"{hostLabel} is not configured.", section);
        if (!RepositoryUrlRules.IsValidHost(host, info.VersionControl))
            return Fail(type, TargetProblemKind.HostInvalid, $"{hostLabel} is not a valid URL.", section);
        if (prefix.Length == 0)
            return Fail(type, TargetProblemKind.PrefixNotConfigured, $"{info.DisplayName} prefix is not configured.", section);
        if (!RepositoryUrlRules.IsValidPrefix(prefix))
            return Fail(type, TargetProblemKind.PrefixInvalid, $"{info.DisplayName} prefix contains characters that are not allowed.", section);

        return new RepositoryTarget(type, $"{host.TrimEnd('/')}/{prefix}{systemCode}", IsCustom: false, Problem: null);
    }

    public static string HostLabel(VersionControlKind kind) => kind == VersionControlKind.Svn ? "SVN host" : "GitBucket host";

    private static RepositoryTarget Fail(RepositoryType type, TargetProblemKind kind, string message, SettingsSection? fixIn, bool isCustom = false) =>
        new(type, null, isCustom, new TargetProblem(kind, message, fixIn));
}
