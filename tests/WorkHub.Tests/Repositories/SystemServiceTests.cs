using Microsoft.Data.Sqlite;
using WorkHub.Core.Repositories;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Repositories;

/// <summary>System persistence and list queries through the application service, against a real SQLite file.</summary>
public sealed class SystemServiceTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly SystemService _service;
    private readonly SqliteSystemStore _store;

    public SystemServiceTests()
    {
        _root.CreateInitializer().Initialize();
        _store = new SqliteSystemStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile));
        _service = new SystemService(_store);
    }

    public void Dispose() => _root.Dispose();

    private static SystemDraft Draft(string code, string name, params RepositoryType[] types) =>
        new(code, name, null, true, types.Select(t => new MembershipDraft(t, true)).ToList());

    private async Task<long> CreateAsync(string code, string name, params RepositoryType[] types)
    {
        var result = await _service.CreateAsync(Draft(code, name, types));
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
        return result.Id!.Value;
    }

    private async Task<IReadOnlyList<SystemListItem>> ListAsync(RepositoryType type, string? search = null, bool includeInactive = false) =>
        (await _service.QueryAsync(new SystemQuery(type, search, includeInactive))).Items;

    [Fact]
    public async Task Create_PersistsSystemAndAllMembershipsOnce()
    {
        var id = await CreateAsync("ABC001", "販売管理システム", RepositoryType.SvnSystem, RepositoryType.GitSystem, RepositoryType.Release);

        var details = await _service.GetAsync(id);
        Assert.NotNull(details);
        Assert.Equal("ABC001", details.SystemCode);
        Assert.Equal("販売管理システム", details.SystemName);
        Assert.True(details.IsActive);
        Assert.Equal([RepositoryType.SvnSystem, RepositoryType.GitSystem, RepositoryType.Release], details.Memberships.Select(m => m.Type).Order());

        using var connection = _root.Open();
        Assert.Equal(1, Db.Count(connection, "Systems"));
        Assert.Equal(3, Db.Count(connection, "SystemRepositories"));
    }

    [Fact]
    public async Task Create_NormalizesInput()
    {
        var result = await _service.CreateAsync(new SystemDraft("  ABC001 ", "  Billing  ", "   ", true,
            [new MembershipDraft(RepositoryType.GitSystem, true, "   ")]));

        var details = await _service.GetAsync(result.Id!.Value);
        Assert.Equal("ABC001", details!.SystemCode);
        Assert.Equal("Billing", details.SystemName);
        Assert.Null(details.Description);
        Assert.Null(details.Memberships.Single().CustomTarget);
    }

    [Fact]
    public async Task Create_RequiresAtLeastOneMembership()
    {
        var result = await _service.CreateAsync(Draft("ABC001", "Billing"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Field == SystemDraftValidator.Fields.Memberships);
        using var connection = _root.Open();
        Assert.Equal(0, Db.Count(connection, "Systems"));
    }

    [Fact]
    public async Task Create_RejectsCaseVariantDuplicateWithFieldError()
    {
        await CreateAsync("ABC001", "First", RepositoryType.SvnSystem);

        var result = await _service.CreateAsync(Draft("abc001", "Second", RepositoryType.GitSystem));

        Assert.False(result.Succeeded);
        Assert.Equal(SystemDraftValidator.Fields.SystemCode, Assert.Single(result.Errors).Field);
        using var connection = _root.Open();
        Assert.Equal(1, Db.Count(connection, "Systems"));
        Assert.Equal(1, Db.Count(connection, "SystemRepositories"));
    }

    [Fact]
    public async Task Create_RollsBackSystemRowWhenAMembershipInsertFails()
    {
        // Bypass the validator to force a DB constraint failure on the second statement of the transaction.
        var bad = new SystemDraft("ABC001", "Billing", null, true,
            [new MembershipDraft(RepositoryType.SvnSystem, true), new MembershipDraft(RepositoryType.GitSystem, true, "   ")]);

        await Assert.ThrowsAsync<SqliteException>(() => _store.CreateAsync(bad));

        using var connection = _root.Open();
        Assert.Equal(0, Db.Count(connection, "Systems"));
        Assert.Equal(0, Db.Count(connection, "SystemRepositories"));
    }

    [Fact]
    public async Task Update_ChangesNameDescriptionAndActiveFlag()
    {
        var id = await CreateAsync("ABC001", "Billing", RepositoryType.SvnSystem);
        var created = await _service.GetAsync(id);

        var result = await _service.UpdateAsync(id, new SystemDraft("ABC001", "Billing (new)", "Main billing", false,
            [new MembershipDraft(RepositoryType.SvnSystem, true)]));

        Assert.True(result.Succeeded);
        var details = await _service.GetAsync(id);
        Assert.Equal("Billing (new)", details!.SystemName);
        Assert.Equal("Main billing", details.Description);
        Assert.False(details.IsActive);
        Assert.Equal(created!.CreatedAt, details.CreatedAt);
    }

    [Fact]
    public async Task Update_AddsAndRemovesMembershipsWithoutDuplicatingTheSystem()
    {
        var id = await CreateAsync("ABC001", "Billing", RepositoryType.SvnSystem, RepositoryType.GitSystem);

        await _service.UpdateAsync(id, Draft("ABC001", "Billing", RepositoryType.GitSystem, RepositoryType.Release));

        Assert.Empty(await ListAsync(RepositoryType.SvnSystem));
        Assert.Single(await ListAsync(RepositoryType.GitSystem));
        Assert.Single(await ListAsync(RepositoryType.Release));
        using var connection = _root.Open();
        Assert.Equal(1, Db.Count(connection, "Systems"));
        Assert.Equal(2, Db.Count(connection, "SystemRepositories"));
    }

    [Fact]
    public async Task Update_KeepsDisabledMembershipWithItsCustomTarget()
    {
        var id = await CreateAsync("ABC001", "Billing", RepositoryType.SvnSystem);
        await _service.UpdateAsync(id, new SystemDraft("ABC001", "Billing", null, true,
        [
            new MembershipDraft(RepositoryType.SvnSystem, true),
            new MembershipDraft(RepositoryType.GitSystem, false, "https://git.example.test/custom/repo"),
        ]));

        Assert.Empty(await ListAsync(RepositoryType.GitSystem));
        var git = (await _service.GetAsync(id))!.Memberships.Single(m => m.Type == RepositoryType.GitSystem);
        Assert.False(git.IsEnabled);
        Assert.Equal("https://git.example.test/custom/repo", git.CustomTarget);
    }

    [Fact]
    public async Task Update_RollsBackWhenAMembershipInsertFails()
    {
        var id = await CreateAsync("ABC001", "Billing", RepositoryType.SvnSystem);
        var bad = new SystemDraft("ABC001", "Renamed", null, true, [new MembershipDraft(RepositoryType.GitSystem, true, "  ")]);

        await Assert.ThrowsAsync<SqliteException>(() => _store.UpdateAsync(id, bad));

        var details = await _service.GetAsync(id);
        Assert.Equal("Billing", details!.SystemName);
        Assert.Equal(RepositoryType.SvnSystem, Assert.Single(details.Memberships).Type);
    }

    [Fact]
    public async Task Update_CanChangeSystemCode_ButNotToAnotherSystemsCode()
    {
        var id = await CreateAsync("ABC001", "Billing", RepositoryType.SvnSystem);
        await CreateAsync("XYZ001", "Other", RepositoryType.SvnSystem);

        Assert.True((await _service.UpdateAsync(id, Draft("ABC002", "Billing", RepositoryType.SvnSystem))).Succeeded);
        var clash = await _service.UpdateAsync(id, Draft("xyz001", "Billing", RepositoryType.SvnSystem));

        Assert.False(clash.Succeeded);
        Assert.Equal(SystemDraftValidator.Fields.SystemCode, clash.Errors.Single().Field);
        Assert.Equal("ABC002", (await _service.GetAsync(id))!.SystemCode);
    }

    [Fact]
    public async Task Update_OfMissingSystem_FailsCleanly()
    {
        var result = await _service.UpdateAsync(999, Draft("ABC001", "Billing", RepositoryType.SvnSystem));

        Assert.False(result.Succeeded);
        using var connection = _root.Open();
        Assert.Equal(0, Db.Count(connection, "SystemRepositories"));
    }

    [Fact]
    public async Task Query_ReturnsOnlyEnabledMembersOfTheSelectedType()
    {
        await CreateAsync("SVN01", "Svn only", RepositoryType.SvnSystem);
        await CreateAsync("GIT01", "Git only", RepositoryType.GitSystem);
        await CreateAsync("BOTH01", "Both", RepositoryType.SvnSystem, RepositoryType.GitSystem);
        var disabledId = await CreateAsync("DIS01", "Disabled git", RepositoryType.SvnSystem);
        await _service.UpdateAsync(disabledId, new SystemDraft("DIS01", "Disabled git", null, true,
            [new MembershipDraft(RepositoryType.SvnSystem, true), new MembershipDraft(RepositoryType.GitSystem, false)]));

        Assert.Equal(["BOTH01", "GIT01"], (await ListAsync(RepositoryType.GitSystem)).Select(i => i.SystemCode));
        Assert.Equal(["BOTH01", "DIS01", "SVN01"], (await ListAsync(RepositoryType.SvnSystem)).Select(i => i.SystemCode));
        Assert.Empty(await ListAsync(RepositoryType.Release));
    }

    [Fact]
    public async Task Deactivate_HidesFromDefaultListButKeepsTheRow()
    {
        var id = await CreateAsync("ABC001", "Billing", RepositoryType.SvnSystem);

        Assert.True(await _service.SetActiveAsync(id, false));

        Assert.Empty(await ListAsync(RepositoryType.SvnSystem));
        var withInactive = Assert.Single(await ListAsync(RepositoryType.SvnSystem, includeInactive: true));
        Assert.False(withInactive.IsActive);
        Assert.NotNull(await _service.GetAsync(id));

        Assert.True(await _service.SetActiveAsync(id, true));
        Assert.Single(await ListAsync(RepositoryType.SvnSystem));
    }

    [Fact]
    public async Task DeletePermanently_RemovesOnlyThatSystemAndItsMemberships()
    {
        var id = await CreateAsync("ABC001", "Billing", RepositoryType.SvnSystem, RepositoryType.GitSystem);
        await CreateAsync("KEEP01", "Keep", RepositoryType.SvnSystem);

        Assert.True(await _service.DeletePermanentlyAsync(id));

        Assert.Null(await _service.GetAsync(id));
        using var connection = _root.Open();
        Assert.Equal(1, Db.Count(connection, "Systems"));
        Assert.Equal(1, Db.Count(connection, "SystemRepositories"));
    }

    [Theory]
    [InlineData("bill", new[] { "ABC002", "ABC004" })]
    [InlineData("BILL", new[] { "ABC002", "ABC004" })]
    [InlineData("002", new[] { "ABC002" })]
    [InlineData("管理", new[] { "ABC001" })]
    [InlineData("abc", new[] { "ABC001", "ABC002", "ABC003", "ABC004" })]
    [InlineData("zzz", new string[0])]
    public async Task Search_ContainsMatchOnCodeOrName(string query, string[] expected)
    {
        await CreateAsync("ABC001", "販売管理システム", RepositoryType.SvnSystem);
        await CreateAsync("ABC002", "Billing System", RepositoryType.SvnSystem);
        await CreateAsync("ABC003", "Payroll", RepositoryType.SvnSystem);
        await CreateAsync("ABC004", "Old Billing", RepositoryType.SvnSystem);
        await CreateAsync("XYZ009", "Billing elsewhere", RepositoryType.GitSystem);

        Assert.Equal(expected, (await ListAsync(RepositoryType.SvnSystem, query)).Select(i => i.SystemCode));
    }

    [Fact]
    public async Task Search_TreatsLikeWildcardsLiterally()
    {
        await CreateAsync("A_B", "Underscore", RepositoryType.SvnSystem);
        await CreateAsync("AXB", "Letter", RepositoryType.SvnSystem);
        await CreateAsync("P1", "100% done", RepositoryType.SvnSystem);

        Assert.Equal(["A_B"], (await ListAsync(RepositoryType.SvnSystem, "_")).Select(i => i.SystemCode));
        Assert.Equal(["P1"], (await ListAsync(RepositoryType.SvnSystem, "%")).Select(i => i.SystemCode));
    }

    [Fact]
    public async Task Query_IsBoundedAndReportsMore()
    {
        for (var i = 0; i < 5; i++)
            await CreateAsync($"S{i:D3}", $"System {i}", RepositoryType.SvnSystem);

        var page1 = await _service.QueryAsync(new SystemQuery(RepositoryType.SvnSystem, Limit: 3));
        var page2 = await _service.QueryAsync(new SystemQuery(RepositoryType.SvnSystem, Limit: 3, Offset: 3));

        Assert.Equal(["S000", "S001", "S002"], page1.Items.Select(i => i.SystemCode));
        Assert.True(page1.HasMore);
        Assert.Equal(["S003", "S004"], page2.Items.Select(i => i.SystemCode));
        Assert.False(page2.HasMore);
    }

    [Fact]
    public async Task Query_ExposesCustomTargetForThatTypeOnly()
    {
        await _service.CreateAsync(new SystemDraft("ABC001", "Billing", null, true,
        [
            new MembershipDraft(RepositoryType.SvnSystem, true),
            new MembershipDraft(RepositoryType.GitSystem, true, "https://git.example.test/special/repo"),
        ]));

        Assert.False(Assert.Single(await ListAsync(RepositoryType.SvnSystem)).HasCustomTarget);
        Assert.Equal("https://git.example.test/special/repo", Assert.Single(await ListAsync(RepositoryType.GitSystem)).CustomTarget);
    }

    [Fact]
    public async Task Usage_CountsOnlyActiveEnabledMemberships()
    {
        await _service.CreateAsync(new SystemDraft("A1", "a", null, true,
        [
            new MembershipDraft(RepositoryType.SvnSystem, true),
            new MembershipDraft(RepositoryType.GitSystem, true, "https://git.example.test/custom"),
            new MembershipDraft(RepositoryType.Release, false),
        ]));
        var inactive = await CreateAsync("B1", "b", RepositoryType.SvnOracle);
        await _service.SetActiveAsync(inactive, false);

        var usage = await _service.GetUsageAsync();

        Assert.Equal([RepositoryType.SvnSystem, RepositoryType.GitSystem], usage.TypesInUse.Order());
        Assert.Equal([RepositoryType.SvnSystem], usage.TypesUsingGeneratedTargets);
    }
}
