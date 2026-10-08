using WorkHub.Core.Owner;
using WorkHub.Core.Workspace;

namespace WorkHub.Tests.Workspace;

public sealed class WorkspaceRulesTests
{
    [Theory]
    [InlineData("  Alpha  ", "Alpha")]
    [InlineData("　販売管理　", "販売管理")]
    [InlineData("Repor  Service", "Repor  Service")]
    [InlineData("ｶﾅ Project", "ｶﾅ Project")]
    public void Names_AreTrimmed_ButOtherwiseKeptAsTyped(string raw, string expected)
    {
        Assert.True(WorkspaceName.TryNormalize(raw, out var name, out var error), error);
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("　")]
    [InlineData("Line\nBreak")]
    [InlineData("Tab\tName")]
    public void InvalidNames_AreRejected(string? raw)
    {
        Assert.False(WorkspaceName.TryNormalize(raw, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void LoneSurrogates_AreRejected()
    {
        // Built in code: xUnit would replace a lone surrogate in [InlineData] with U+FFFD.
        var name = "Bad" + (char)0xD800;

        Assert.False(WorkspaceName.TryNormalize(name, out _, out var error));
        Assert.Equal("Name contains invalid characters.", error);
    }

    [Fact]
    public void DisplayName_IsLimitedTo200Characters()
    {
        Assert.True(WorkspaceName.TryNormalize(new string('x', 200), out _, out _));
        Assert.False(WorkspaceName.TryNormalize(new string('x', 201), out _, out var error));
        Assert.Equal("Name must be at most 200 characters.", error);
    }

    [Fact]
    public void LongestValidName_KeyMayExceed400Characters()
    {
        var name = new string('㍿', 200); // ㍿ ×200: 200 UTF-16 units, 200 code points

        Assert.True(WorkspaceName.TryNormalize(name, out var normalized, out var error), error);
        Assert.Equal(name, normalized);

        var key = WorkspaceName.Key(normalized);
        Assert.Equal(800, key.Length);
        Assert.Equal(string.Concat(Enumerable.Repeat("株式会社", 200)), key);
    }

    [Theory]
    [InlineData("Alpha", "ALPHA")]
    [InlineData("alpha", "ＡＬＰＨＡ")]
    [InlineData("ｱﾙﾌｧ", "アルファ")] // half-width vs full-width katakana
    [InlineData("Projekt ß", "PROJEKT ß")]
    public void CaseAndWidthVariants_ShareAKey(string a, string b)
    {
        Assert.Equal(WorkspaceName.Key(a), WorkspaceName.Key(b));
    }

    [Theory]
    [InlineData("あるふぁ", "アルファ")] // hiragana vs katakana
    [InlineData("販売", "販賣")]
    public void DifferentTexts_KeepDifferentKeys(string a, string b)
    {
        Assert.NotEqual(WorkspaceName.Key(a), WorkspaceName.Key(b));
    }

    [Theory]
    [InlineData("Tools")]
    [InlineData("ＴＯＯＬＳ")]
    [InlineData("ｶﾅ")]
    [InlineData("㍿")]
    [InlineData("販売管理")]
    public void Key_MatchesTheAgreedAlgorithmUsedForOwnerGroups(string name)
    {
        // Same NFKC + invariant-uppercase contract as Owner groups, implemented independently in the Workspace domain.
        Assert.Equal(OwnerGroupName.Key(name), WorkspaceName.Key(name));
    }

    [Fact]
    public void ProjectDraft_BlankOptionalTextBecomesNull_AndPersonFieldsAreTrimmed()
    {
        var (draft, _, error) = ProjectDraftValidator.Validate(new ProjectDraft("  計画  ", ProjectStatus.Planning, "   ", "  佐藤  ", "\t"));

        Assert.Null(error);
        Assert.Equal(new ProjectDraft("計画", ProjectStatus.Planning, null, "佐藤", null), draft);
    }

    [Fact]
    public void ProjectDraft_DescriptionKeepsLineBreaksAndSpacing()
    {
        const string description = "Line 1\r\n  Line 2  ";

        var (draft, _, _) = ProjectDraftValidator.Validate(new ProjectDraft("P", ProjectStatus.Active, description));

        Assert.Equal(description, draft!.Description);
    }

    [Theory]
    [InlineData(ProjectDraftValidator.Fields.Name, "", ProjectStatus.Active, null, null, null)]
    [InlineData(ProjectDraftValidator.Fields.Status, "P", ProjectStatus.Archived, null, null, null)]
    [InlineData(ProjectDraftValidator.Fields.Status, "P", (ProjectStatus)42, null, null, null)]
    [InlineData(ProjectDraftValidator.Fields.Owner, "P", ProjectStatus.Active, null, "Line\nBreak", null)]
    [InlineData(ProjectDraftValidator.Fields.ProjectManager, "P", ProjectStatus.Active, null, null, "Tab\tName")]
    public void ProjectDraft_InvalidFieldsAreReported(string field, string name, ProjectStatus status, string? description, string? owner, string? manager)
    {
        var (draft, actualField, error) = ProjectDraftValidator.Validate(new ProjectDraft(name, status, description, owner, manager));

        Assert.Null(draft);
        Assert.Equal(field, actualField);
        Assert.NotNull(error);
    }

    [Fact]
    public void ProjectDraft_LengthLimits()
    {
        Assert.Null(ProjectDraftValidator.Validate(new ProjectDraft("P", ProjectStatus.Active, new string('d', 4000), new string('o', 100), new string('m', 100))).Error);
        Assert.Equal(ProjectDraftValidator.Fields.Description, ProjectDraftValidator.Validate(new ProjectDraft("P", ProjectStatus.Active, new string('d', 4001))).Field);
        Assert.Equal(ProjectDraftValidator.Fields.Owner, ProjectDraftValidator.Validate(new ProjectDraft("P", ProjectStatus.Active, null, new string('o', 101))).Field);
        Assert.Equal(ProjectDraftValidator.Fields.ProjectManager, ProjectDraftValidator.Validate(new ProjectDraft("P", ProjectStatus.Active, null, null, new string('m', 101))).Field);
    }

    [Fact]
    public void Statuses_EditableExcludeArchived_DefaultIsActive_AndCodesAreStable()
    {
        Assert.Equal([ProjectStatus.Planning, ProjectStatus.Active, ProjectStatus.Maintenance, ProjectStatus.OnHold, ProjectStatus.Completed], ProjectStatuses.Editable);
        Assert.Equal(ProjectStatus.Active, ProjectStatuses.Default);
        Assert.Equal("On Hold", ProjectStatuses.DisplayName(ProjectStatus.OnHold));
        Assert.Equal(
            ["Planning", "Active", "Maintenance", "OnHold", "Completed", "Archived"],
            Enum.GetValues<ProjectStatus>().Select(ProjectStatuses.StorageCode));
        foreach (var status in Enum.GetValues<ProjectStatus>())
            Assert.Equal(status, ProjectStatuses.FromStorageCode(ProjectStatuses.StorageCode(status)));
    }

    [Fact]
    public void TreeOrder_FoldersFirst_ThenNameIgnoringCaseAndWidth()
    {
        var nodes = new[]
        {
            (WorkspaceNodeType.Project, "alpha", 1L),
            (WorkspaceNodeType.Folder, "Zeta", 2L),
            (WorkspaceNodeType.Project, "Beta", 3L),
            (WorkspaceNodeType.Folder, "ａｌｐｈａ", 4L),
            (WorkspaceNodeType.Project, "ALPHA", 5L),
        };

        var ordered = nodes.Order(WorkspaceNodeOrder.Instance).Select(n => n.Item3).ToList();

        Assert.Equal([4L, 2L, 5L, 1L, 3L], ordered);
    }
}
