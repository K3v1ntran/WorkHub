using WorkHub.Core.Owner;

namespace WorkHub.Tests.Owner;

public sealed class OwnerValidationTests
{
    private static string? TargetError(OwnerActionType type, string target) =>
        OwnerItemValidator.Validate(new OwnerItemDraft("Name", type, target)).Errors
            .FirstOrDefault(e => e.Field == OwnerItemValidator.Fields.Target)?.Message;

    [Theory]
    [InlineData("https://example.test")]
    [InlineData("http://portal.example.test:8080/path?q=1#top")]
    [InlineData("https://例え.example.test/ページ")]
    public void Web_AcceptsHttpAndHttps(string url) => Assert.Null(TargetError(OwnerActionType.Web, url));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("cmd:/c calc")]
    [InlineData("powershell:Get-Process")]
    [InlineData("https://user:pass@portal.example.test/")]
    [InlineData("https://user@portal.example.test/")]
    [InlineData("ftp://files.example.test/")]
    [InlineData("portal.example.test")]
    [InlineData("https://portal.example.test/a b")]
    [InlineData(@"C:\Windows\System32\calc.exe")]
    public void Web_RejectsNonWebOrCredentialUrls(string url) => Assert.NotNull(TargetError(OwnerActionType.Web, url));

    [Theory]
    [InlineData(OwnerActionType.File, @"C:\Example\document.txt")]
    [InlineData(OwnerActionType.File, @"\\fileserver\share\資料\報告書.xlsx")]
    [InlineData(OwnerActionType.File, @"Z:\Company\Tools\does-not-exist-now.docx")] // existence is not checked at save time
    [InlineData(OwnerActionType.Folder, @"C:\Example")]
    [InlineData(OwnerActionType.Folder, @"C:\Program Files\My App\")]
    [InlineData(OwnerActionType.Folder, @"\\server\share")]
    [InlineData(OwnerActionType.Executable, @"C:\Tools\tool.exe")]
    [InlineData(OwnerActionType.Executable, @"C:\Program Files\Tool Suite\tool.EXE")]
    public void Paths_AcceptFullyQualifiedLocalAndUncPaths(OwnerActionType type, string path) => Assert.Null(TargetError(type, path));

    [Theory]
    [InlineData(OwnerActionType.File, "")]
    [InlineData(OwnerActionType.File, "   ")]
    [InlineData(OwnerActionType.File, "document.txt")]
    [InlineData(OwnerActionType.File, @"Example\document.txt")]
    [InlineData(OwnerActionType.File, @"C:\a|b.txt")]
    [InlineData(OwnerActionType.File, @"C:\a?.txt")]
    [InlineData(OwnerActionType.File, "file:///C:/x.txt")]
    [InlineData(OwnerActionType.Folder, "")]
    [InlineData(OwnerActionType.Folder, @"\\?\C:\x")]
    [InlineData(OwnerActionType.Folder, "cmd:/c dir")]
    [InlineData(OwnerActionType.Executable, "")]
    [InlineData(OwnerActionType.Executable, "tool.exe")]
    [InlineData(OwnerActionType.Executable, @"C:\Tools\script.bat")]
    [InlineData(OwnerActionType.Executable, @"C:\Tools\tool.exe ""--arg""")]
    [InlineData(OwnerActionType.Executable, "cmd.exe /c calc")]
    public void Paths_RejectEmptyRelativeOrMalformed(OwnerActionType type, string path) => Assert.NotNull(TargetError(type, path));

    [Fact]
    public void Name_IsRequiredAndTrimmed()
    {
        Assert.Contains(OwnerItemValidator.Validate(new OwnerItemDraft("  ", OwnerActionType.Web, "https://x.example.test/")).Errors,
            e => e.Field == OwnerItemValidator.Fields.Name);

        var (draft, _) = OwnerItemValidator.Validate(new OwnerItemDraft("  社内ポータル ", OwnerActionType.Web, " https://x.example.test/ "));
        Assert.Equal("社内ポータル", draft!.Name);
        Assert.Equal("https://x.example.test/", draft.Target);
    }

    [Fact]
    public void UnknownActionType_IsRejected()
    {
        var (_, errors) = OwnerItemValidator.Validate(new OwnerItemDraft("X", (OwnerActionType)42, "https://x.example.test/"));

        Assert.Contains(errors, e => e.Field == OwnerItemValidator.Fields.ActionType);
    }

    [Fact]
    public void Executable_ArgumentsKeepQuotesAndSpaces_ButMustBeSingleLine()
    {
        var ok = OwnerItemValidator.Validate(new OwnerItemDraft("T", OwnerActionType.Executable, @"C:\Tools\tool.exe", "--name \"A B\" --path \"C:\\My Files\""));
        Assert.Equal("--name \"A B\" --path \"C:\\My Files\"", ok.Draft!.Arguments);

        var bad = OwnerItemValidator.Validate(new OwnerItemDraft("T", OwnerActionType.Executable, @"C:\Tools\tool.exe", "--a\r\ncalc.exe"));
        Assert.Contains(bad.Errors, e => e.Field == OwnerItemValidator.Fields.Arguments);
    }

    [Fact]
    public void Executable_WorkingDirectoryMustBeAFullPath()
    {
        var (_, errors) = OwnerItemValidator.Validate(new OwnerItemDraft("T", OwnerActionType.Executable, @"C:\Tools\tool.exe", WorkingDirectory: "relative"));

        Assert.Contains(errors, e => e.Field == OwnerItemValidator.Fields.WorkingDirectory);
    }

    [Fact]
    public void Note_IsPreservedVerbatim()
    {
        const string note = "  line 1\n  インデント付き  \n";
        var (draft, _) = OwnerItemValidator.Validate(new OwnerItemDraft("T", OwnerActionType.Web, "https://x.example.test/", Note: note));

        Assert.Equal(note, draft!.Note);
    }

    [Theory]
    [InlineData("Tools", "TOOLS")]
    [InlineData("ｔｏｏｌｓ", "TOOLS")]
    [InlineData("ﾂｰﾙ", "ツール")]
    public void GroupNameKey_FoldsCaseAndWidth(string a, string b) =>
        Assert.Equal(OwnerGroupName.Key(a), OwnerGroupName.Key(b));

    [Fact]
    public void GroupName_DistinctNamesHaveDistinctKeys() =>
        Assert.NotEqual(OwnerGroupName.Key("ツール"), OwnerGroupName.Key("ツールズ"));
}
