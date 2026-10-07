using WorkHub.Core.Repositories;

namespace WorkHub.Core.Settings;

public enum IssueSeverity
{
    /// <summary>Shown to the user; does not block saving (e.g. a value needed by a type in use is still empty).</summary>
    Warning,

    /// <summary>Blocks saving: a value was entered but is malformed or points at something missing.</summary>
    Error,
}

public sealed record SettingsIssue(IssueSeverity Severity, SettingsSection Section, string Field, string Message);

/// <summary>
/// Validates Repository settings.
/// <list type="bullet">
/// <item>Errors: values that were entered but are invalid. Saving is refused so nothing invalid is persisted.</item>
/// <item>Warnings: empty values that a repository type <em>currently in use</em> needs. Types nobody uses are ignored,
/// and missing values never block saving the rest, so an unconfigured SVN side cannot stop Git from being configured.</item>
/// </list>
/// </summary>
public static class RepositorySettingsValidator
{
    public static class Fields
    {
        public const string SvnHost = "Svn.Host";
        public const string SvnSystemPrefix = "Svn.SystemPrefix";
        public const string SvnOraclePrefix = "Svn.OraclePrefix";
        public const string ReleasePrefix = "Svn.ReleasePrefix";
        public const string TortoiseProcPath = "Svn.TortoiseProcPath";
        public const string GitHost = "Git.Host";
        public const string GitSystemPrefix = "Git.SystemPrefix";
        public const string GitOraclePrefix = "Git.OraclePrefix";

        public static string PrefixFor(RepositoryType type) => type switch
        {
            RepositoryType.SvnSystem => SvnSystemPrefix,
            RepositoryType.SvnOracle => SvnOraclePrefix,
            RepositoryType.Release => ReleasePrefix,
            RepositoryType.GitSystem => GitSystemPrefix,
            RepositoryType.GitOracle => GitOraclePrefix,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
        };
    }

    public static IReadOnlyList<SettingsIssue> Validate(RepositorySettings settings, RepositoryUsage usage, Func<string, bool> fileExists)
    {
        var s = settings.Trimmed();
        var issues = new List<SettingsIssue>();

        ValidateHost(issues, s.Svn.Host, VersionControlKind.Svn, SettingsSection.Svn, Fields.SvnHost, usage);
        ValidateHost(issues, s.Git.Host, VersionControlKind.Git, SettingsSection.Git, Fields.GitHost, usage);

        foreach (var info in RepositoryCatalog.All)
        {
            var section = SectionFor(info.VersionControl);
            var prefix = s.PrefixFor(info.Type);
            var field = Fields.PrefixFor(info.Type);
            if (!RepositoryUrlRules.IsValidPrefix(prefix))
                issues.Add(new(IssueSeverity.Error, section, field, $"{info.DisplayName} prefix may contain only letters A-Z, digits, '.', '_' and '-'."));
            else if (prefix.Length == 0 && usage.TypesUsingGeneratedTargets.Contains(info.Type))
                issues.Add(new(IssueSeverity.Warning, section, field, $"{info.DisplayName} prefix is not configured."));
        }

        var tortoise = s.Svn.TortoiseProcPath;
        var svnInUse = usage.TypesInUse.Any(t => RepositoryCatalog.Get(t).VersionControl == VersionControlKind.Svn);
        if (tortoise.Length > 0)
        {
            if (!Path.IsPathFullyQualified(tortoise) || !tortoise.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                issues.Add(new(IssueSeverity.Error, SettingsSection.Svn, Fields.TortoiseProcPath, "TortoiseSVN path must be the full path to TortoiseProc.exe."));
            else if (!fileExists(tortoise))
                issues.Add(new(IssueSeverity.Error, SettingsSection.Svn, Fields.TortoiseProcPath, "TortoiseSVN executable was not found."));
        }
        else if (svnInUse)
        {
            issues.Add(new(IssueSeverity.Warning, SettingsSection.Svn, Fields.TortoiseProcPath, "TortoiseSVN executable is not configured."));
        }

        return issues;
    }

    public static SettingsSection SectionFor(VersionControlKind kind) =>
        kind == VersionControlKind.Svn ? SettingsSection.Svn : SettingsSection.Git;

    private static void ValidateHost(List<SettingsIssue> issues, string host, VersionControlKind kind, SettingsSection section, string field, RepositoryUsage usage)
    {
        var label = RepositoryTargetResolver.HostLabel(kind);
        if (host.Length > 0)
        {
            if (!RepositoryUrlRules.IsValidHost(host, kind))
                issues.Add(new(IssueSeverity.Error, section, field, $"{label} must be a full URL ({RepositoryUrlRules.DescribeSchemes(kind)}) without query or fragment."));
        }
        else if (usage.TypesUsingGeneratedTargets.Any(t => RepositoryCatalog.Get(t).VersionControl == kind))
        {
            issues.Add(new(IssueSeverity.Warning, section, field, $"{label} is not configured."));
        }
    }
}
