using WorkHub.Core.Repositories;
using WorkHub.Core.Settings;

namespace WorkHub.Tests.Repositories;

public sealed class RepositoryTargetResolverTests
{
    private static RepositorySettings Configured() => new()
    {
        Svn =
        {
            Host = "https://svn.example.test/svn/",
            OraclePrefix = "ORA_doc_",
        },
        Git =
        {
            Host = "https://git.example.test/gitbucket/root/",
            OraclePrefix = "ORA_src_",
        },
    };

    [Theory]
    [InlineData(RepositoryType.SvnSystem, "https://svn.example.test/svn/GeSL_doc_ABC001")]
    [InlineData(RepositoryType.SvnOracle, "https://svn.example.test/svn/ORA_doc_ABC001")]
    [InlineData(RepositoryType.Release, "https://svn.example.test/svn/GeSL_rel_ABC001")]
    [InlineData(RepositoryType.GitSystem, "https://git.example.test/gitbucket/root/GeSL_src_ABC001")]
    [InlineData(RepositoryType.GitOracle, "https://git.example.test/gitbucket/root/ORA_src_ABC001")]
    public void GeneratedTarget_IsHostPlusPrefixPlusCode(RepositoryType type, string expected)
    {
        var target = RepositoryTargetResolver.Resolve(type, "ABC001", null, Configured());

        Assert.True(target.IsResolved);
        Assert.False(target.IsCustom);
        Assert.Equal(expected, target.Target);
        Assert.Null(target.Problem);
    }

    [Theory]
    [InlineData("https://svn.example.test/svn")]
    [InlineData("https://svn.example.test/svn/")]
    [InlineData("https://svn.example.test/svn///")]
    [InlineData("  https://svn.example.test/svn/  ")]
    public void HostSeparators_AreNormalized(string host)
    {
        var settings = Configured();
        settings.Svn.Host = host;

        var target = RepositoryTargetResolver.Resolve(RepositoryType.SvnSystem, "ABC001", null, settings);

        Assert.Equal("https://svn.example.test/svn/GeSL_doc_ABC001", target.Target);
    }

    [Fact]
    public void CustomTarget_OverridesGeneratedTarget_AndIgnoresGlobalSettings()
    {
        var target = RepositoryTargetResolver.Resolve(RepositoryType.GitSystem, "ABC001", " https://other.example.test/x/repo ", new RepositorySettings());

        Assert.True(target.IsCustom);
        Assert.Equal("https://other.example.test/x/repo", target.Target);
    }

    [Fact]
    public void CustomTarget_IsValidatedForTheRepositoryKind()
    {
        var svnProtocolOnGit = RepositoryTargetResolver.Resolve(RepositoryType.GitSystem, "ABC001", "svn://svn.example.test/repo", Configured());
        var svnProtocolOnSvn = RepositoryTargetResolver.Resolve(RepositoryType.SvnSystem, "ABC001", "svn://svn.example.test/repo", Configured());

        Assert.Equal(TargetProblemKind.CustomTargetInvalid, svnProtocolOnGit.Problem?.Kind);
        Assert.Null(svnProtocolOnGit.Problem!.FixIn);
        Assert.Equal("svn://svn.example.test/repo", svnProtocolOnSvn.Target);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\calc.exe")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:pass@git.example.test/repo")]
    [InlineData("https://git.example.test/a b")]
    public void CustomTarget_CannotPointAtLocalProgramsOrEmbedCredentials(string custom)
    {
        var target = RepositoryTargetResolver.Resolve(RepositoryType.GitSystem, "ABC001", custom, Configured());

        Assert.False(target.IsResolved);
        Assert.Equal(TargetProblemKind.CustomTargetInvalid, target.Problem?.Kind);
    }

    [Theory]
    [InlineData(RepositoryType.SvnSystem, "SVN host is not configured.", SettingsSection.Svn)]
    [InlineData(RepositoryType.Release, "SVN host is not configured.", SettingsSection.Svn)]
    [InlineData(RepositoryType.GitSystem, "GitBucket host is not configured.", SettingsSection.Git)]
    public void MissingHost_IsReported(RepositoryType type, string message, SettingsSection section)
    {
        var target = RepositoryTargetResolver.Resolve(type, "ABC001", null, new RepositorySettings());

        Assert.Equal(TargetProblemKind.HostNotConfigured, target.Problem?.Kind);
        Assert.Equal(message, target.Problem!.Message);
        Assert.Equal(section, target.Problem.FixIn);
    }

    [Theory]
    [InlineData(RepositoryType.SvnOracle, "SVN ORACLE prefix is not configured.")]
    [InlineData(RepositoryType.GitOracle, "GIT ORACLE prefix is not configured.")]
    public void UnconfiguredOraclePrefix_IsReported(RepositoryType type, string message)
    {
        var settings = Configured();
        settings.Svn.OraclePrefix = string.Empty;
        settings.Git.OraclePrefix = string.Empty;

        var target = RepositoryTargetResolver.Resolve(type, "ABC001", null, settings);

        Assert.Equal(TargetProblemKind.PrefixNotConfigured, target.Problem?.Kind);
        Assert.Equal(message, target.Problem!.Message);
    }

    [Fact]
    public void MissingSvnSettings_DoNotAffectGit()
    {
        var settings = Configured();
        settings.Svn.Host = string.Empty;

        Assert.False(RepositoryTargetResolver.Resolve(RepositoryType.SvnSystem, "A1", null, settings).IsResolved);
        Assert.True(RepositoryTargetResolver.Resolve(RepositoryType.GitSystem, "A1", null, settings).IsResolved);
    }

    [Theory]
    [InlineData("svn.example.test/svn")]
    [InlineData("ftp://svn.example.test/")]
    [InlineData("https://svn.example.test/svn?x=1")]
    public void InvalidHost_IsReported(string host)
    {
        var settings = Configured();
        settings.Svn.Host = host;

        Assert.Equal(TargetProblemKind.HostInvalid, RepositoryTargetResolver.Resolve(RepositoryType.SvnSystem, "A1", null, settings).Problem?.Kind);
    }

    [Fact]
    public void InvalidPrefix_IsReported()
    {
        var settings = Configured();
        settings.Svn.SystemPrefix = "bad/prefix";

        Assert.Equal(TargetProblemKind.PrefixInvalid, RepositoryTargetResolver.Resolve(RepositoryType.SvnSystem, "A1", null, settings).Problem?.Kind);
    }

    [Fact]
    public void Target_UsesTheCurrentSystemCode()
    {
        var before = RepositoryTargetResolver.Resolve(RepositoryType.SvnSystem, "OLD01", null, Configured());
        var after = RepositoryTargetResolver.Resolve(RepositoryType.SvnSystem, "NEW01", null, Configured());

        Assert.EndsWith("GeSL_doc_OLD01", before.Target);
        Assert.EndsWith("GeSL_doc_NEW01", after.Target);
    }
}
