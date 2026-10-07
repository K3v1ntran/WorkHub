using System.ComponentModel;
using System.Diagnostics;
using WorkHub.Core.Repositories;
using WorkHub.Core.Settings;
using WorkHub.Infrastructure.Launching;

namespace WorkHub.Tests.Launching;

/// <summary>No real process is ever started: a recording fake replaces Process.Start.</summary>
public sealed class RepositoryLauncherTests
{
    private const string Tortoise = @"C:\Tools\TortoiseSVN\bin\TortoiseProc.exe";

    private sealed class RecordingStarter : IProcessStarter
    {
        public List<ProcessStartInfo> Started { get; } = [];

        public Exception? Throw { get; init; }

        public void Start(ProcessStartInfo startInfo)
        {
            if (Throw is not null)
                throw Throw;
            Started.Add(startInfo);
        }
    }

    private static (ProcessRepositoryLauncher Launcher, RecordingStarter Starter) Create(Exception? fail = null)
    {
        var starter = new RecordingStarter { Throw = fail };
        return (new ProcessRepositoryLauncher(starter, path => path == Tortoise), starter);
    }

    [Fact]
    public void Svn_StartsTortoiseDirectlyWithSeparateArguments()
    {
        var (launcher, starter) = Create();

        var result = launcher.Launch(new LaunchRequest(LaunchKind.TortoiseSvn, "https://svn.example.test/svn/GeSL_doc_ABC001", Tortoise));

        Assert.True(result.Succeeded);
        var info = Assert.Single(starter.Started);
        Assert.Equal(Tortoise, info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.Equal(["/command:repobrowser", "/path:https://svn.example.test/svn/GeSL_doc_ABC001"], info.ArgumentList);
        Assert.Equal(string.Empty, info.Arguments);
    }

    [Fact]
    public void Svn_TargetCannotInjectExtraArguments()
    {
        var (launcher, starter) = Create();

        var result = launcher.Launch(new LaunchRequest(LaunchKind.TortoiseSvn, "https://svn.example.test/repo\" /command:remove \"", Tortoise));

        Assert.Equal(LaunchProblemKind.InvalidTarget, result.Problem);
        Assert.Empty(starter.Started);
    }

    [Fact]
    public void Svn_QuoteInUrlIsEscapedInsideTheSingleArgument()
    {
        var (launcher, starter) = Create();

        launcher.Launch(new LaunchRequest(LaunchKind.TortoiseSvn, "https://svn.example.test/a\"b", Tortoise));

        var info = Assert.Single(starter.Started);
        Assert.Equal(2, info.ArgumentList.Count);
        Assert.Equal("/path:https://svn.example.test/a%22b", info.ArgumentList[1]);
    }

    [Theory]
    [InlineData(null, LaunchProblemKind.TortoiseNotConfigured)]
    [InlineData("", LaunchProblemKind.TortoiseNotConfigured)]
    [InlineData(@"C:\missing\TortoiseProc.exe", LaunchProblemKind.TortoiseNotFound)]
    [InlineData("TortoiseProc.exe", LaunchProblemKind.TortoiseNotFound)]
    public void Svn_MissingTortoise_ReturnsFixableProblemWithoutStarting(string? path, LaunchProblemKind expected)
    {
        var (launcher, starter) = Create();

        var result = launcher.Launch(new LaunchRequest(LaunchKind.TortoiseSvn, "https://svn.example.test/svn/x", path));

        Assert.Equal(expected, result.Problem);
        Assert.Equal("TortoiseSVN is not configured or cannot be found.", result.Message);
        Assert.Equal(SettingsSection.Svn, result.FixIn);
        Assert.Empty(starter.Started);
    }

    [Fact]
    public void Browser_OpensNormalizedHttpUrlThroughShell()
    {
        var (launcher, starter) = Create();

        var result = launcher.Launch(new LaunchRequest(LaunchKind.Browser, "https://git.example.test/gitbucket/root/GeSL_src_ABC001", null));

        Assert.True(result.Succeeded);
        var info = Assert.Single(starter.Started);
        Assert.True(info.UseShellExecute);
        Assert.Equal("https://git.example.test/gitbucket/root/GeSL_src_ABC001", info.FileName);
        Assert.Empty(info.ArgumentList);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\calc.exe")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("svn://svn.example.test/repo")]
    [InlineData("calc")]
    public void Browser_RefusesNonHttpTargets(string target)
    {
        var (launcher, starter) = Create();

        var result = launcher.Launch(new LaunchRequest(LaunchKind.Browser, target, null));

        Assert.Equal(LaunchProblemKind.InvalidTarget, result.Problem);
        Assert.Empty(starter.Started);
    }

    [Fact]
    public void StartFailure_IsReportedNotThrown()
    {
        var (launcher, _) = Create(new Win32Exception(2, "The system cannot find the file specified."));

        var result = launcher.Launch(new LaunchRequest(LaunchKind.TortoiseSvn, "https://svn.example.test/x", Tortoise));

        Assert.Equal(LaunchProblemKind.StartFailed, result.Problem);
        Assert.Contains("TortoiseSVN", result.Message);
    }

    [Fact]
    public void TortoiseLocator_ChecksStandardLocationOnly()
    {
        var checkedPaths = new List<string>();

        var found = TortoiseLocator.Detect(path => { checkedPaths.Add(path); return false; });

        Assert.Null(found);
        Assert.NotEmpty(checkedPaths);
        Assert.All(checkedPaths, p => Assert.EndsWith(@"TortoiseSVN\bin\TortoiseProc.exe", p));
    }
}
