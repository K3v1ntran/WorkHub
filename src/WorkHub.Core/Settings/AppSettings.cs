using WorkHub.Core.Repositories;

namespace WorkHub.Core.Settings;

/// <summary>
/// Application configuration persisted in <c>Config\settings.json</c>.
/// Defaults must stay safe: no company hosts, credentials or guessed values.
/// Domain data (systems, owner items, workspace) belongs in SQLite, not here.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSettingsVersion = 1;

    public int SettingsVersion { get; set; } = CurrentSettingsVersion;

    public RepositorySettings Repositories { get; set; } = new();

    public HealthCheckSettings HealthCheck { get; set; } = new();

    public UiSettings Ui { get; set; } = new();

    public static AppSettings CreateDefault() => new();

    /// <summary>Replaces sections that were explicitly null in the file with defaults.</summary>
    public AppSettings Normalize()
    {
        Repositories ??= new();
        Repositories.Normalize();
        HealthCheck ??= new();
        Ui ??= new();
        Ui.LastPrimaryTab ??= UiSettings.DefaultPrimaryTab;
        Ui.Window ??= new();
        return this;
    }
}

public sealed class RepositorySettings
{
    /// <summary>Full path to TortoiseProc.exe. Empty until configured or detected.</summary>
    public string TortoiseProcPath { get; set; } = string.Empty;

    public RepositoryEndpointSettings SvnSystem { get; set; } = new() { Prefix = "GeSL_doc_" };

    /// <summary>Oracle prefix is site configuration; intentionally empty by default.</summary>
    public RepositoryEndpointSettings SvnOracle { get; set; } = new();

    public RepositoryEndpointSettings GitSystem { get; set; } = new() { Prefix = "GeSL_src_" };

    /// <summary>Oracle prefix is site configuration; intentionally empty by default.</summary>
    public RepositoryEndpointSettings GitOracle { get; set; } = new();

    public RepositoryEndpointSettings Release { get; set; } = new() { Prefix = "GeSL_rel_" };

    public RepositoryEndpointSettings For(RepositoryType type) => type switch
    {
        RepositoryType.SvnSystem => SvnSystem,
        RepositoryType.SvnOracle => SvnOracle,
        RepositoryType.GitSystem => GitSystem,
        RepositoryType.GitOracle => GitOracle,
        RepositoryType.Release => Release,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    internal void Normalize()
    {
        var defaults = new RepositorySettings();
        TortoiseProcPath ??= string.Empty;
        SvnSystem = (SvnSystem ?? defaults.SvnSystem).Normalize();
        SvnOracle = (SvnOracle ?? defaults.SvnOracle).Normalize();
        GitSystem = (GitSystem ?? defaults.GitSystem).Normalize();
        GitOracle = (GitOracle ?? defaults.GitOracle).Normalize();
        Release = (Release ?? defaults.Release).Normalize();
    }
}

public sealed class RepositoryEndpointSettings
{
    /// <summary>Base URL of the repository host for this type. Empty until configured.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Repository name prefix placed before the system code. Empty means not configured.</summary>
    public string Prefix { get; set; } = string.Empty;

    internal RepositoryEndpointSettings Normalize()
    {
        BaseUrl ??= string.Empty;
        Prefix ??= string.Empty;
        return this;
    }
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
