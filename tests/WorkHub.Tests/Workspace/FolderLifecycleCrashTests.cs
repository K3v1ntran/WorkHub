using System.Diagnostics;
using System.Reflection;
using WorkHub.Core.Workspace;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Tests.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Workspace;

/// <summary>
/// Phase 13 B5 (process kill): a child process (WorkHub.CrashProbe <c>folder-lifecycle</c>) runs the real Folder Archive, Trash
/// or Restore and is KILLED inside its write transaction, right after its marker UPDATE and before COMMIT. The hot journal it
/// leaves is rolled back by the next open: the logical state is entirely the state before. The same operation run to the end
/// gives entirely the state after (exactly one row changed).
/// </summary>
public sealed class FolderLifecycleCrashTests : IDisposable
{
    private static readonly string ProbePath = typeof(FolderLifecycleCrashTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "CrashProbePath").Value!;

    private readonly TempWorkHubRoot _root = new();
    private readonly WorkspaceService _workspace;

    public FolderLifecycleCrashTests()
    {
        _root.CreateInitializer().Initialize();
        _workspace = new WorkspaceService(new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
    }

    public void Dispose() => _root.Dispose();

    private List<string> Data()
    {
        using var connection = _root.Open();
        return ProjectContentMigrationTests.DataSnapshot(connection);
    }

    private void ReleaseFile() => new SqliteConnectionFactory(_root.Paths.DatabaseFile).ClearPools();

    [Theory]
    [InlineData("archive")]
    [InlineData("trash")]
    [InlineData("restore")]
    public async Task KilledInsideTheWriteTransaction_TheStateIsEntirelyBefore_RunToTheEnd_EntirelyAfter(string operation)
    {
        var folder = (await _workspace.CreateFolderAsync(null, "2024")).Value!.Id;
        var inner = (await _workspace.CreateFolderAsync(folder, "Q1")).Value!.Id;
        await _workspace.CreateProjectAsync(inner, new ProjectDraft("Alpha", ProjectStatus.Active));
        if (operation == "restore")
            await _workspace.MarkFolderAsync(folder, FolderLifecycleKind.Trash, (await _workspace.PrepareFolderLifecycleAsync(folder, FolderLifecycleKind.Trash)).Confirmation!.Token);
        var before = Data();
        ReleaseFile();

        using (var child = Start("folder-lifecycle", _root.Paths.DatabaseFile, operation, folder.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            child.Kill(); // parked after its UPDATE, before COMMIT
        Assert.True(File.Exists(_root.Paths.DatabaseFile + "-journal"), "the killed writer should leave a hot journal");

        Assert.Equal(before, Data()); // the next open rolls the hot journal back: entirely before

        // The same operation to the end (in this process): entirely after — exactly the Folder's marker row.
        if (operation == "restore")
            Assert.Equal(FolderClearOutcome.Cleared, (await _workspace.ClearFolderAsync(folder, FolderLifecycleKind.Trash)).Outcome);
        else
        {
            var kind = operation == "archive" ? FolderLifecycleKind.Archive : FolderLifecycleKind.Trash;
            Assert.Equal(FolderLifecycleOutcome.Committed, (await _workspace.MarkFolderAsync(folder, kind, (await _workspace.PrepareFolderLifecycleAsync(folder, kind)).Confirmation!.Token)).Outcome);
        }

        var changed = Assert.Single(Data().Except(before));
        Assert.StartsWith($"WorkspaceNodes|Id={folder}|", changed);
    }

    /// <summary>Starts the probe and returns once it printed READY (it then waits to be killed).</summary>
    private static Child Start(params string[] args)
    {
        var info = new ProcessStartInfo(ProbePath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        var process = Process.Start(info)!;
        var ready = Task.Run(() =>
        {
            string? line;
            while ((line = process.StandardOutput.ReadLine()) is not null)
            {
                if (line == "READY")
                    return true;
            }

            return false;
        });
        if (!ready.Wait(TimeSpan.FromSeconds(60)) || !ready.Result)
        {
            var error = process.HasExited ? process.StandardError.ReadToEnd() : "(still running)";
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"The crash probe did not reach its point: {error}");
        }

        return new Child(process);
    }

    private sealed class Child(Process process) : IDisposable
    {
        public void Kill()
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }

        public void Dispose()
        {
            Kill();
            process.Dispose();
        }
    }
}
