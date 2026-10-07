using System.ComponentModel;
using System.Diagnostics;
using WorkHub.Core.Owner;
using WorkHub.Infrastructure.Launching;

namespace WorkHub.Tests.Launching;

/// <summary>No real process is ever started: a recording fake replaces Process.Start; existence checks are faked.</summary>
public sealed class OwnerLauncherTests
{
    private static readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Example\document.txt",
        @"C:\Tools\tool.exe",
        @"C:\Program Files\Tool Suite\tool.exe",
    };

    private static readonly HashSet<string> Folders = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Example",
        @"C:\Tools",
        @"C:\Work Dir",
        @"C:\Program Files\Tool Suite",
    };

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

    private static (ProcessOwnerLauncher Launcher, RecordingStarter Starter) Create(Exception? fail = null)
    {
        var starter = new RecordingStarter { Throw = fail };
        return (new ProcessOwnerLauncher(starter, Files.Contains, Folders.Contains), starter);
    }

    private static OwnerItemDetails Item(OwnerActionType type, string target, string? arguments = null, string? workingDirectory = null) =>
        new(1, 1, "Item", type, target, arguments, workingDirectory, null, null, null, 0, false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Web_OpensDefaultBrowserViaShell()
    {
        var (launcher, starter) = Create();

        Assert.True(launcher.Launch(Item(OwnerActionType.Web, "https://example.test")).Succeeded);

        var info = Assert.Single(starter.Started);
        Assert.True(info.UseShellExecute);
        Assert.Equal("https://example.test/", info.FileName);
        Assert.Equal(string.Empty, info.Arguments);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("data:text/html,x")]
    [InlineData("https://user:pass@example.test/")]
    public void Web_StoredUnsafeUrl_IsRefusedAtLaunchToo(string url)
    {
        var (launcher, starter) = Create();

        Assert.Equal(OwnerLaunchProblem.InvalidTarget, launcher.Launch(Item(OwnerActionType.Web, url)).Problem);
        Assert.Empty(starter.Started);
    }

    [Fact]
    public void File_OpensWithDefaultApplicationViaShell()
    {
        var (launcher, starter) = Create();

        Assert.True(launcher.Launch(Item(OwnerActionType.File, @"C:\Example\document.txt")).Succeeded);

        var info = Assert.Single(starter.Started);
        Assert.True(info.UseShellExecute);
        Assert.Equal(@"C:\Example\document.txt", info.FileName);
    }

    [Fact]
    public void Folder_OpensInExplorerViaShell()
    {
        var (launcher, starter) = Create();

        Assert.True(launcher.Launch(Item(OwnerActionType.Folder, @"C:\Example")).Succeeded);

        var info = Assert.Single(starter.Started);
        Assert.True(info.UseShellExecute);
        Assert.Equal(@"C:\Example", info.FileName);
    }

    [Fact]
    public void Executable_StartsDirectlyWithArgumentsAndWorkingDirectory()
    {
        var (launcher, starter) = Create();

        Assert.True(launcher.Launch(Item(OwnerActionType.Executable, @"C:\Program Files\Tool Suite\tool.exe",
            "--profile \"Team A\" --out \"C:\\My Files\\out.txt\"", @"C:\Work Dir")).Succeeded);

        var info = Assert.Single(starter.Started);
        Assert.False(info.UseShellExecute);
        Assert.Equal(@"C:\Program Files\Tool Suite\tool.exe", info.FileName);
        Assert.Equal("--profile \"Team A\" --out \"C:\\My Files\\out.txt\"", info.Arguments);
        Assert.Equal(@"C:\Work Dir", info.WorkingDirectory);
        Assert.DoesNotContain("cmd", info.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("powershell", info.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Executable_DefaultsWorkingDirectoryToProgramFolder()
    {
        var (launcher, starter) = Create();

        launcher.Launch(Item(OwnerActionType.Executable, @"C:\Tools\tool.exe"));

        var info = Assert.Single(starter.Started);
        Assert.Equal(@"C:\Tools", info.WorkingDirectory);
        Assert.Equal(string.Empty, info.Arguments);
    }

    [Fact]
    public void Executable_ShellMetacharactersInArgumentsAreNotInterpreted()
    {
        var (launcher, starter) = Create();

        launcher.Launch(Item(OwnerActionType.Executable, @"C:\Tools\tool.exe", "a & calc.exe | del *"));

        var info = Assert.Single(starter.Started);
        // Started directly: the program receives this text; no shell exists to run "calc.exe" or "del".
        Assert.False(info.UseShellExecute);
        Assert.Equal(@"C:\Tools\tool.exe", info.FileName);
        Assert.Equal("a & calc.exe | del *", info.Arguments);
    }

    [Theory]
    [InlineData(OwnerActionType.File, @"C:\Example\missing.txt", "File could not be found.")]
    [InlineData(OwnerActionType.Folder, @"Z:\Company\Tools", "Folder could not be found.")]
    [InlineData(OwnerActionType.Executable, @"C:\Tools\missing.exe", "Program could not be found.")]
    public void MissingTargets_FailCleanlyWithoutStarting(OwnerActionType type, string target, string message)
    {
        var (launcher, starter) = Create();

        var result = launcher.Launch(Item(type, target));

        Assert.Equal(OwnerLaunchProblem.TargetMissing, result.Problem);
        Assert.Equal(message, result.Message);
        Assert.Empty(starter.Started);
    }

    [Fact]
    public void Executable_MissingWorkingDirectory_FailsCleanly()
    {
        var (launcher, starter) = Create();

        var result = launcher.Launch(Item(OwnerActionType.Executable, @"C:\Tools\tool.exe", workingDirectory: @"C:\Gone"));

        Assert.Equal(OwnerLaunchProblem.WorkingDirectoryMissing, result.Problem);
        Assert.Empty(starter.Started);
    }

    [Theory]
    [InlineData(OwnerActionType.Executable, "")]
    [InlineData(OwnerActionType.Executable, "tool.exe")]
    [InlineData(OwnerActionType.Executable, "cmd.exe /c calc")]
    [InlineData(OwnerActionType.File, "relative.txt")]
    [InlineData(OwnerActionType.Folder, "cmd:/c dir")]
    public void MalformedStoredTargets_AreRefused(OwnerActionType type, string target)
    {
        var (launcher, starter) = Create();

        Assert.Equal(OwnerLaunchProblem.InvalidTarget, launcher.Launch(Item(type, target)).Problem);
        Assert.Empty(starter.Started);
    }

    [Fact]
    public void StartFailure_IsReportedNotThrown()
    {
        var (launcher, _) = Create(new Win32Exception(1155, "No application is associated with the specified file."));

        var result = launcher.Launch(Item(OwnerActionType.File, @"C:\Example\document.txt"));

        Assert.Equal(OwnerLaunchProblem.StartFailed, result.Problem);
        Assert.Contains("could not be opened", result.Message);
    }
}
