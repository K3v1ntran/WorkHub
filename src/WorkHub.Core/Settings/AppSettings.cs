using WorkHub.Core.Repositories;

namespace WorkHub.Core.Settings;

/// <summary>
/// Application configuration persisted in <c>Config\settings.json</c>. No credentials are ever stored here.
/// Domain data (systems, owner items, workspace) belongs in SQLite, not here.
/// <para>
/// Two kinds of defaults: <see cref="CreateInitial"/> holds the approved repository configuration and is used only
/// when a NEW settings file is created; the property initializers stay neutral (no hosts) and apply only to values
/// missing from an existing file, so an existing configuration is never filled in or overwritten behind the user's back.
/// </para>
/// </summary>
public sealed class AppSettings
{
    /// <summary>v2: one SVN host and one GitBucket host shared by their repository types (v1 had a URL per type).</summary>
    public const int CurrentSettingsVersion = 2;

    public int SettingsVersion { get; set; } = CurrentSettingsVersion;

    public RepositorySettings Repository { get; set; } = new();

    public HealthCheckSettings HealthCheck { get; set; } = new();

    public UiSettings Ui { get; set; } = new();

    public static AppSettings CreateDefault() => new();

    /// <summary>Approved initial SVN / GitBucket configuration for a new environment (new settings file only).</summary>
    public static AppSettings CreateInitial()
    {
        var settings = new AppSettings();
        settings.Repository.Svn.Host = InitialRepository.SvnHost;
        settings.Repository.Svn.SystemPrefix = InitialRepository.SvnSystemPrefix;
        settings.Repository.Svn.OraclePrefix = InitialRepository.SvnOraclePrefix;
        settings.Repository.Svn.TortoiseProcPath = InitialRepository.TortoiseProcPath;
        settings.Repository.Git.Host = InitialRepository.GitHost;
        settings.Repository.Git.SystemPrefix = InitialRepository.GitSystemPrefix;
        settings.Repository.Git.OraclePrefix = InitialRepository.GitOraclePrefix;
        return settings; // RELEASE keeps its existing default prefix (GeSL_rel_) on the shared SVN host
    }

    /// <summary>Replaces sections and strings that were explicitly null in the file.</summary>
    public AppSettings Normalize()
    {
        Repository ??= new();
        Repository.Normalize();
        HealthCheck ??= new();
        Ui ??= new();
        Ui.LastPrimaryTab ??= UiSettings.DefaultPrimaryTab;
        Ui.Window ??= new();
        return this;
    }

    public AppSettings Clone() => new()
    {
        SettingsVersion = SettingsVersion,
        Repository = Repository.Clone(),
        HealthCheck = new HealthCheckSettings { TimeoutSeconds = HealthCheck.TimeoutSeconds },
        Ui = new UiSettings
        {
            LastPrimaryTab = Ui.LastPrimaryTab,
            Window = new WindowPlacementSettings
            {
                Left = Ui.Window.Left,
                Top = Ui.Window.Top,
                Width = Ui.Window.Width,
                Height = Ui.Window.Height,
                IsMaximized = Ui.Window.IsMaximized,
            },
        },
    };
}

/// <summary>
/// Initial repository configuration approved by the Product Owner. The TortoiseProc path is a starting value, not proof
/// that TortoiseSVN is installed; a missing executable is still reported when saving settings or opening a repository.
/// </summary>
public static class InitialRepository
{
    public const string SvnHost = "http://10.194.105.25/svn/";
    public const string SvnSystemPrefix = "GeSL_doc_";
    public const string SvnOraclePrefix = "GeSL_doc_";
    public const string GitHost = "http://10.194.105.25:8080/gitbucket/root/";
    public const string GitSystemPrefix = "GeSL_src_";
    public const string GitOraclePrefix = "GeSL_src_";
    public const string TortoiseProcPath = "C:/Program Files/TortoiseSVN/bin/TortoiseProc.exe";
}

public sealed class RepositorySettings
{
    public SvnSettings Svn { get; set; } = new();

    public GitSettings Git { get; set; } = new();

    public string HostFor(RepositoryType type) =>
        RepositoryCatalog.Get(type).VersionControl == VersionControlKind.Svn ? Svn.Host : Git.Host;

    public string PrefixFor(RepositoryType type) => type switch
    {
        RepositoryType.SvnSystem => Svn.SystemPrefix,
        RepositoryType.SvnOracle => Svn.OraclePrefix,
        RepositoryType.Release => Svn.ReleasePrefix,
        RepositoryType.GitSystem => Git.SystemPrefix,
        RepositoryType.GitOracle => Git.OraclePrefix,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public RepositorySettings Clone() => new()
    {
        Svn = new SvnSettings
        {
            Host = Svn.Host,
            SystemPrefix = Svn.SystemPrefix,
            OraclePrefix = Svn.OraclePrefix,
            ReleasePrefix = Svn.ReleasePrefix,
            TortoiseProcPath = Svn.TortoiseProcPath,
        },
        Git = new GitSettings
        {
            Host = Git.Host,
            SystemPrefix = Git.SystemPrefix,
            OraclePrefix = Git.OraclePrefix,
        },
    };

    /// <summary>Trims every value. Applied before validation and saving.</summary>
    public RepositorySettings Trimmed()
    {
        var copy = Clone();
        copy.Svn.Host = copy.Svn.Host.Trim();
        copy.Svn.SystemPrefix = copy.Svn.SystemPrefix.Trim();
        copy.Svn.OraclePrefix = copy.Svn.OraclePrefix.Trim();
        copy.Svn.ReleasePrefix = copy.Svn.ReleasePrefix.Trim();
        copy.Svn.TortoiseProcPath = copy.Svn.TortoiseProcPath.Trim().Trim('"');
        copy.Git.Host = copy.Git.Host.Trim();
        copy.Git.SystemPrefix = copy.Git.SystemPrefix.Trim();
        copy.Git.OraclePrefix = copy.Git.OraclePrefix.Trim();
        return copy;
    }

    internal void Normalize()
    {
        Svn ??= new();
        Svn.Host ??= string.Empty;
        Svn.SystemPrefix ??= string.Empty;
        Svn.OraclePrefix ??= string.Empty;
        Svn.ReleasePrefix ??= string.Empty;
        Svn.TortoiseProcPath ??= string.Empty;
        Git ??= new();
        Git.Host ??= string.Empty;
        Git.SystemPrefix ??= string.Empty;
        Git.OraclePrefix ??= string.Empty;
    }
}

/// <summary>Shared by SVN SYSTEM, SVN ORACLE and RELEASE (all opened with TortoiseSVN).</summary>
public sealed class SvnSettings
{
    /// <summary>SVN base URL, e.g. <c>https://svn.example.test/svn/</c>. Empty until configured.</summary>
    public string Host { get; set; } = string.Empty;

    public string SystemPrefix { get; set; } = "GeSL_doc_";

    /// <summary>Site configuration; intentionally empty by default.</summary>
    public string OraclePrefix { get; set; } = string.Empty;

    public string ReleasePrefix { get; set; } = "GeSL_rel_";

    /// <summary>Full path to TortoiseProc.exe. Empty until configured or detected.</summary>
    public string TortoiseProcPath { get; set; } = string.Empty;
}

/// <summary>Shared by GIT SYSTEM and GIT ORACLE (opened in the default browser).</summary>
public sealed class GitSettings
{
    /// <summary>GitBucket base URL, e.g. <c>https://git.example.test/gitbucket/root/</c>. Empty until configured.</summary>
    public string Host { get; set; } = string.Empty;

    public string SystemPrefix { get; set; } = "GeSL_src_";

    /// <summary>Site configuration; intentionally empty by default.</summary>
    public string OraclePrefix { get; set; } = string.Empty;
}

public sealed class HealthCheckSettings
{
    public int TimeoutSeconds { get; set; } = 5;
}

public sealed class UiSettings
{
    public const string DefaultPrimaryTab = "Repository";

    public string LastPrimaryTab { get; set; } = DefaultPrimaryTab;

    public WindowPlacementSettings Window { get; set; } = new();
}

public sealed class WindowPlacementSettings
{
    public double? Left { get; set; }

    public double? Top { get; set; }

    public double Width { get; set; } = 1100;

    public double Height { get; set; } = 720;

    public bool IsMaximized { get; set; }
}
