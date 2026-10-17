using WorkHub.Core.Startup;

namespace WorkHub.Tests.Startup;

/// <summary>
/// <c>--data-root</c> parsing. Uses a fake "default" root under the temp folder, so the real profile is never involved.
/// </summary>
public sealed class StartupOptionsTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "WorkHubTests", Guid.NewGuid().ToString("N"));
    private readonly string _defaultRoot;

    public StartupOptionsTests()
    {
        Directory.CreateDirectory(_sandbox);
        _defaultRoot = Path.Combine(_sandbox, "LocalAppData", "WorkHub");
    }

    public void Dispose() => Directory.Delete(_sandbox, recursive: true);

    private StartupOptionsResult Parse(params string[] args) => StartupOptions.Parse(args, _defaultRoot);

    private string Sandbox(string name) => Path.Combine(_sandbox, name);

    [Fact]
    public void NoArguments_UseTheDefaultRoot()
    {
        var result = Parse();

        Assert.True(result.IsValid);
        Assert.False(result.Options!.IsCustomDataRoot);
        Assert.Equal(Path.GetFullPath(_defaultRoot), result.Options.Paths.Root);
    }

    [Fact]
    public void NewFolderUnderExistingParent_IsAcceptedAndNotCreated()
    {
        var root = Sandbox("TestRoot");

        var result = Parse("--data-root", root);

        Assert.True(result.IsValid, result.Error);
        Assert.True(result.Options!.IsCustomDataRoot);
        Assert.Equal(root, result.Options.Paths.Root);
        Assert.False(Directory.Exists(root)); // parsing never creates anything
    }

    [Fact]
    public void OptionName_IgnoresCase_AndTrailingSeparatorIsNormalized()
    {
        var root = Sandbox("TestRoot");

        var result = Parse("--DATA-ROOT", root + Path.DirectorySeparatorChar);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(root, result.Options!.Paths.Root);
    }

    [Fact]
    public void ExistingEmptyFolder_OrExistingWorkHubRoot_IsAccepted()
    {
        var empty = Directory.CreateDirectory(Sandbox("Empty")).FullName;
        var used = Directory.CreateDirectory(Sandbox("Used")).FullName;
        Directory.CreateDirectory(Path.Combine(used, "Data"));
        Directory.CreateDirectory(Path.Combine(used, "Logs"));
        Directory.CreateDirectory(Path.Combine(used, "Config"));

        Assert.True(Parse("--data-root", empty).IsValid);
        Assert.True(Parse("--data-root", used).IsValid);
    }

    [Fact]
    public void NonEmptyForeignFolder_IsRejected()
    {
        var foreign = Directory.CreateDirectory(Sandbox("Foreign")).FullName;
        File.WriteAllText(Path.Combine(foreign, "notes.txt"), "x");

        var result = Parse("--data-root", foreign);

        Assert.False(result.IsValid);
        Assert.Contains("not a WorkHub data folder", result.Error);
        Assert.Contains("notes.txt", result.Error);
    }

    [Fact]
    public void ExistingFile_IsRejected()
    {
        var file = Sandbox("root.txt");
        File.WriteAllText(file, "x");

        var result = Parse("--data-root", file);

        Assert.False(result.IsValid);
        Assert.Contains("file", result.Error);
    }

    [Fact]
    public void MissingParent_IsRejected_AndNothingIsCreated()
    {
        var root = Path.Combine(_sandbox, "missing", "deeper", "Root");

        var result = Parse("--data-root", root);

        Assert.False(result.IsValid);
        Assert.Contains("parent folder does not exist", result.Error);
        Assert.False(Directory.Exists(Path.Combine(_sandbox, "missing")));
    }

    [Theory]
    [InlineData("relative\\root")]
    [InlineData(".\\root")]
    [InlineData("\\rootless")]
    [InlineData("C:rootless")]
    [InlineData("\\\\server\\share\\WorkHub")]
    [InlineData("\\\\?\\C:\\WorkHub")]
    [InlineData("\\\\.\\C:\\WorkHub")]
    [InlineData("file://C:/WorkHub")]
    [InlineData("C:\\Work|Hub")]
    [InlineData("C:\\")]
    public void MalformedOrUnsupportedPaths_AreRejected(string path)
    {
        var result = Parse("--data-root", path);

        Assert.False(result.IsValid);
        Assert.StartsWith($"--data-root '{path}' cannot be used:", result.Error);
    }

    [Fact]
    public void TheRealRoot_ItsSubfolders_AndItsAncestors_AreRejected_IgnoringCase()
    {
        Directory.CreateDirectory(_defaultRoot);

        foreach (var path in new[]
                 {
                     _defaultRoot,
                     _defaultRoot.ToUpperInvariant(),
                     Path.Combine(_defaultRoot, "Data"),
                     Path.GetDirectoryName(_defaultRoot)!,
                 })
        {
            var result = Parse("--data-root", path);
            Assert.False(result.IsValid, path);
            Assert.Contains("overlaps the normal WorkHub data folder", result.Error);
        }
    }

    [Fact]
    public void SiblingWithCommonPrefix_IsNotTreatedAsInsideTheRealRoot()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_defaultRoot)!);

        var result = Parse("--data-root", _defaultRoot + "Test");

        Assert.True(result.IsValid, result.Error);
    }

    [Theory]
    [InlineData("--data-root")]
    [InlineData("--data-root", "")]
    [InlineData("--data-root", "   ")]
    public void MissingValue_IsRejected(params string[] args)
    {
        var result = Parse(args);

        Assert.False(result.IsValid);
        Assert.Equal("--data-root requires a folder path.", result.Error);
    }

    [Fact]
    public void RepeatedOption_IsRejected()
    {
        var result = Parse("--data-root", Sandbox("A"), "--data-root", Sandbox("B"));

        Assert.False(result.IsValid);
        Assert.Equal("--data-root may be given only once.", result.Error);
    }

    // ---- Phase 10: --restore-continue and relaunch on the same data root --------------------------------------------

    [Fact]
    public void RestoreContinue_TakesAValidOperationId_WithOrWithoutADataRoot()
    {
        var plain = Parse("--restore-continue", "20261016-120000-0123abcd");
        Assert.True(plain.IsValid, plain.Error);
        Assert.Equal("20261016-120000-0123abcd", plain.Options!.RestoreContinueOp);
        Assert.False(plain.Options.IsCustomDataRoot);

        var root = Sandbox("Restore");
        var both = Parse("--data-root", root, "--restore-continue", "20261016-120000-0123abcd");
        Assert.True(both.IsValid, both.Error);
        Assert.Equal(Path.GetFullPath(root), both.Options!.Paths.Root);
        Assert.Equal("20261016-120000-0123abcd", both.Options.RestoreContinueOp);
    }

    [Theory]
    [InlineData("--restore-continue")]
    [InlineData("--restore-continue", "..\\..\\x")]
    [InlineData("--restore-continue", "20261016-120000-0123ABCD")]
    [InlineData("--restore-continue", "20261016-120000-0123abcd", "--restore-continue", "20261016-120000-0123abcd")]
    public void RestoreContinue_RejectsMissingMalformedOrRepeatedValues(params string[] args)
    {
        var result = Parse(args);
        Assert.False(result.IsValid);
        Assert.Contains("--restore-continue", result.Error);
    }

    [Fact]
    public void RelaunchArguments_KeepTheIsolatedDataRoot_AndNeverNameTheDefaultProfile()
    {
        var root = Sandbox("Relaunch");
        var custom = Parse("--data-root", root).Options!;
        Assert.Equal(["--data-root", Path.GetFullPath(root), "--restore-continue", "20261016-120000-0123abcd"], custom.RelaunchArguments("20261016-120000-0123abcd"));
        Assert.Equal(["--data-root", Path.GetFullPath(root)], custom.RelaunchArguments(null));

        var standard = Parse().Options!;
        Assert.Empty(standard.RelaunchArguments(null));
        Assert.Equal(["--restore-continue", "20261016-120000-0123abcd"], standard.RelaunchArguments("20261016-120000-0123abcd"));
        Assert.Throws<ArgumentException>(() => standard.RelaunchArguments("not-an-op"));
    }

    [Theory]
    [InlineData("--verbose")]
    [InlineData("C:\\WorkHubTest")]
    [InlineData("-data-root")]
    public void UnknownArguments_AreRejected(string argument)
    {
        var result = Parse(argument);

        Assert.False(result.IsValid);
        Assert.Contains($"Unknown argument '{argument}'", result.Error);
    }
}
