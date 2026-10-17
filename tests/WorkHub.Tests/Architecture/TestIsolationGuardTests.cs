using System.Diagnostics;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Architecture;

/// <summary>
/// Phase 11 guards (proposal §5 A2, C7, C8): no process-wide SQLite pool clears, the real profile resolved only where
/// allowed, and no test root or crash-probe path overlapping the real profile.
/// </summary>
public sealed class TestIsolationGuardTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    /// <summary>Where <c>ForCurrentUser(</c> may appear: the app's composition root, its definition, and two compare-only guards.</summary>
    private static readonly string[] ForCurrentUserAllowed =
    [
        "src/WorkHub.App/App.xaml.cs",
        "src/WorkHub.Core/Startup/WorkHubPaths.cs",
        "tests/WorkHub.Tests/TestSupport/DefaultProfileGuard.cs",
        "tests/WorkHub.CrashProbe/Program.cs",
    ];

    [Fact]
    public void NoSourceClearsEverySqlitePool()
    {
        var hits = Scan(["src", "tests"], "*.cs", "ClearAllPools(").Where(h => !h.StartsWith("tests/WorkHub.Tests/Architecture/", StringComparison.Ordinal));

        Assert.Empty(hits);
    }

    [Fact]
    public void TheRealProfileIsResolvedOnlyInTheAllowedFiles()
    {
        var files = Scan(["src", "tests"], "*.cs", "ForCurrentUser(")
            .Where(h => !h.StartsWith("tests/WorkHub.Tests/Architecture/", StringComparison.Ordinal))
            .Select(h => h[..h.IndexOf(':')])
            .Distinct()
            .Where(f => !ForCurrentUserAllowed.Contains(f));

        Assert.Empty(files);
        Assert.Empty(Scan(["tools"], "*.ps*1", "ForCurrentUser"));
    }

    [Theory]
    [InlineData("SqliteConnection.ClearAllPools();", true)]
    [InlineData("    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();", true)]
    [InlineData("// SqliteConnection.ClearAllPools();", false)]
    [InlineData("/// Never <c>ClearAllPools</c>: other classes run in parallel.", false)]
    [InlineData("Foo(); // then ClearAllPools(); would be wrong", false)]
    public void TheScanner_FindsCalls_AndIgnoresComments(string line, bool found) =>
        Assert.Equal(found, CodePart(line).Contains("ClearAllPools(", StringComparison.Ordinal));

    [Theory]
    [InlineData(@"C:\Profile\WorkHub", true)]                 // equal
    [InlineData(@"c:\profile\workhub\", true)]                // equal, other case, trailing separator
    [InlineData(@"C:\Profile\WorkHub\Data\workhub.db", true)] // inside
    [InlineData(@"C:\Profile", true)]                         // containing
    [InlineData(@"C:\", true)]                                // drive root contains it
    [InlineData(@"C:\Profile\WorkHub2", false)]               // sibling with the same prefix
    [InlineData(@"C:\Profile\WorkHu", false)]                 // shorter sibling
    [InlineData(@"C:\WorkHubTest11\roots\a", false)]          // unrelated
    public void DefaultProfileGuard_Overlap(string path, bool overlaps) =>
        Assert.Equal(overlaps, DefaultProfileGuard.Overlaps(path, @"C:\Profile\WorkHub"));

    [Fact]
    public void EveryTempWorkHubRoot_PassesTheGuard()
    {
        using var root = new TempWorkHubRoot();
        DefaultProfileGuard.EnsureIsolated(root.Paths.Root); // does not throw
    }

    /// <summary>
    /// The probe refuses (exit 3) a path that CONTAINS the real profile before doing anything. %LOCALAPPDATA% is a folder,
    /// so even without the guard the "hold" mode could not open it as a database: nothing in the profile is touched.
    /// </summary>
    [Fact]
    public async Task TheCrashProbe_RefusesAPathOverlappingTheRealProfile()
    {
        var info = new ProcessStartInfo(RestoreKit.ProbePath) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("hold");
        info.ArgumentList.Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            await process.WaitForExitAsync(timeout.Token);

        Assert.Equal(3, process.ExitCode);
        Assert.Contains("refusing", await error);
    }

    private static IEnumerable<string> Scan(string[] folders, string pattern, string token)
    {
        foreach (var folder in folders)
        {
            var directory = Path.Combine(RepoRoot, folder);
            if (!Directory.Exists(directory))
                continue;
            foreach (var file in Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(RepoRoot, file).Replace('\\', '/');
                if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal))
                    continue;
                var number = 0;
                foreach (var line in File.ReadLines(file))
                {
                    number++;
                    if (CodePart(line).Contains(token, StringComparison.Ordinal))
                        yield return $"{relative}:{number}";
                }
            }
        }
    }

    /// <summary>The line without its <c>//</c> comment (doc comments included).</summary>
    private static string CodePart(string line)
    {
        var comment = line.IndexOf("//", StringComparison.Ordinal);
        return comment < 0 ? line : line[..comment];
    }

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WorkHub.sln")))
                return directory.FullName;
        }

        throw new InvalidOperationException("WorkHub.sln not found above the test output folder.");
    }
}
