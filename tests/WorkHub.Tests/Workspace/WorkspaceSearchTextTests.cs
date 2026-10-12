using WorkHub.Core.Workspace.Search;

namespace WorkHub.Tests.Workspace;

/// <summary>Query parsing, folding, masks and excerpts (Phase 6 proposal §4.2–§4.4).</summary>
public sealed class WorkspaceSearchTextTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("　\t\r\n")]
    public void EmptyOrWhitespace_IsNoQuery(string? text) => Assert.Null(WorkspaceSearchText.Parse(text));

    [Fact]
    public void Terms_SplitOnAnyWhitespace_IncludingIdeographicSpace_FoldedAndDistinct()
    {
        var query = WorkspaceSearchText.Parse("  kickoff　会議\tKICKOFF  ｋｉｃｋｏｆｆ ")!;

        Assert.Equal(["KICKOFF", "会議"], query.Terms);
        Assert.False(query.WasShortened);
    }

    [Fact]
    public void AtMostEightTerms_TheRestAreDropped_WithAHint()
    {
        var query = WorkspaceSearchText.Parse("a b c d e f g h i j")!;

        Assert.Equal(8, query.Terms.Count);
        Assert.Equal("H", query.Terms[^1]);
        Assert.True(query.WasShortened);
    }

    [Fact]
    public void InputIsCappedAt200Characters_WithoutSplittingASurrogatePair()
    {
        var text = new string('x', 199) + "😀" + " tail";
        var query = WorkspaceSearchText.Parse(text)!;

        Assert.True(query.WasShortened);
        Assert.Equal(new string('X', 199), query.Terms.Single()); // the pair cut by the cap is dropped, never half kept
    }

    [Fact]
    public void LoneSurrogates_AreRemoved_NeverThrow()
    {
        var query = WorkspaceSearchText.Parse("ab\uD800c \uDC00")!;

        Assert.Equal(["ABC"], query.Terms);
        Assert.Equal("ABC", WorkspaceSearchText.Fold("a\uDFFFbc"));
    }

    [Theory]
    [InlineData("abc", "ABC")]
    [InlineData("abc", "ａｂｃ")]
    [InlineData("ABC", "Ａｂｃ")]
    [InlineData("カナ", "ｶﾅ")]
    [InlineData("株式会社", "㍿")]
    [InlineData("会", "東京会議")]
    public void Matching_IsNfkcUpperCaseContains(string term, string text)
    {
        var terms = WorkspaceSearchText.Parse(term)!.Terms;
        Assert.Equal(1, WorkspaceSearchText.Mask(text, terms));
    }

    [Fact]
    public void HiraganaAndKatakana_StayDistinct_LikeWorkspaceNames()
    {
        var terms = WorkspaceSearchText.Parse("かな")!.Terms;
        Assert.Equal(0, WorkspaceSearchText.Mask("カナ", terms));
    }

    [Fact]
    public void Mask_HasOneBitPerContainedTerm()
    {
        var terms = WorkspaceSearchText.Parse("alpha beta gamma")!.Terms;

        Assert.Equal(0b101, WorkspaceSearchText.Mask("Gamma and ALPHA", terms));
        Assert.Equal(0b111, WorkspaceSearchText.AllTerms(terms));
        Assert.Equal(0, WorkspaceSearchText.Mask(null, terms));
    }

    [Fact]
    public void Excerpt_IsTheFirstMatchingLine_TabsAsSpaces_CrLfAndLf()
    {
        var terms = WorkspaceSearchText.Parse("agenda")!.Terms;

        Assert.Equal("the agenda: scope", WorkspaceSearchText.Excerpt("first line\r\n\tthe\tagenda: scope\nlater AGENDA", terms));
        Assert.Null(WorkspaceSearchText.Excerpt("nothing here", terms));
    }

    [Fact]
    public void Excerpt_OfALongLine_IsAWindowAroundTheMatch_MarkedWithEllipses()
    {
        var terms = WorkspaceSearchText.Parse("needle")!.Terms;
        var line = new string('a', 500) + " NEEDLE " + new string('b', 500);

        var excerpt = WorkspaceSearchText.Excerpt(line, terms)!;

        Assert.StartsWith("…", excerpt);
        Assert.EndsWith("…", excerpt);
        Assert.Contains("NEEDLE", excerpt);
        Assert.InRange(excerpt.Length, 100, WorkspaceSearchText.ExcerptLength + 2);
    }

    [Fact]
    public void Excerpt_WithNfkcExpansionBeforeTheMatch_CutsOnOriginalCharacters()
    {
        var terms = WorkspaceSearchText.Parse("target")!.Terms;
        var line = string.Concat(Enumerable.Repeat("㍿", 300)) + "target" + new string('z', 300); // each ㍿ folds to four characters

        var excerpt = WorkspaceSearchText.Excerpt(line, terms)!;

        Assert.Contains("target", excerpt);
        Assert.DoesNotContain("株", excerpt); // original text, not the folded form
    }

    [Fact]
    public void Excerpt_NeverSplitsASurrogatePair()
    {
        var terms = WorkspaceSearchText.Parse("mark")!.Terms;
        var line = string.Concat(Enumerable.Repeat("😀", 200)) + "mark" + string.Concat(Enumerable.Repeat("😀", 200));

        var excerpt = WorkspaceSearchText.Excerpt(line, terms)!.Trim('…');

        Assert.False(char.IsLowSurrogate(excerpt[0]));
        Assert.False(char.IsHighSurrogate(excerpt[^1]));
    }
}
