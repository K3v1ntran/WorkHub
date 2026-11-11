using System.Diagnostics;
using System.Globalization;
using WorkHub.Core.Backup;
using WorkHub.Core.Workspace.Content;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Workspace;

/// <summary>
/// Phase 14 B7/B8: a real child process (CrashProbe "purge") runs the real ProjectTrashPurge and is KILLED at a backup
/// checkpoint or right after a delete statement inside the write transaction. What remains on disk is what a crash leaves.
/// </summary>
public sealed class ProjectTrashPurgeCrashTests : IDisposable
{
    private static readonly string ProbePath = RestoreKit.ProbePath;

    private readonly PurgeWorld _w = new();

    public void Dispose() => _w.Dispose();

    [Theory]
    [InlineData("candidate-validated")] // staging written, not published
    [InlineData("published")]           // the copy is published, deletion not begun
    [InlineData("purge:notes")]         // inside the write transaction
    [InlineData("purge:folders:0")]
    [InlineData("purge:entries")]
    public async Task KilledAtABoundary_TheDatabaseIsEntirelyBefore_APublishedCopyIsValid_TheNextPurgeCompletes(string point)
    {
        await _w.BuildAsync();
        var before = _w.Dump();
        BackupKit.ClearPoolsUnder(_w.Root.Paths.Root);

        using (var child = Start("purge", _w.Root.Paths.Root, _w.P.ToString(CultureInfo.InvariantCulture), _w.E_FB.ToString(CultureInfo.InvariantCulture), point))
            child.Kill();

        Assert.Equal(before, _w.Dump()); // a hot journal, if any, is rolled back by the next open: entirely before
        _w.AssertIntegrity();
        var files = Directory.GetFiles(_w.Root.Paths.PreDeleteBackupsDirectory).Select(Path.GetFileName).ToList();
        if (point == "candidate-validated")
        {
            Assert.Contains(files, f => PreDeleteNames.IsOwnCandidate(f!));
            Assert.DoesNotContain(_w.PreDeleteFiles(), f => !PreDeleteNames.IsOwnCandidate(f)); // nothing was published
        }
        else
        {
            var published = Assert.Single(_w.PreDeleteFiles());
            Assert.True(BackupKit.Validator().Check(Path.Combine(_w.Root.Paths.PreDeleteBackupsDirectory, published), BackupCheckLevel.Full, 9).IsFullyValidated);
        }

        var request = PurgeRequest.Selected(_w.P, [_w.E_FB]);
        var result = await _w.Content.PurgeAsync(request, (await _w.Content.PreparePurgeAsync(request)).Preview!.Token);
        Assert.Equal(PurgeOutcome.Purged, result.Outcome);
        Assert.DoesNotContain(Directory.GetFiles(_w.Root.Paths.PreDeleteBackupsDirectory), f => PreDeleteNames.IsOwnCandidate(Path.GetFileName(f)));
        _w.AssertIntegrity();
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
