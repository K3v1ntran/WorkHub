using System.Text;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.LegacyImport;
using WorkHub.Core.Repositories;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.LegacyImport;

/// <summary>Preview + transactional import against a real SQLite database.</summary>
public sealed class LegacyImportTests : IDisposable
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "LegacyHubData.xml");

    private readonly TempWorkHubRoot _root = new();
    private readonly RecordingLog _log = new();
    private readonly LegacyImportService _import;
    private readonly SystemService _systems;

    public LegacyImportTests()
    {
        _root.CreateInitializer().Initialize();
        _import = _root.CreateLegacyImportService(_log);
        _systems = new SystemService(new SqliteSystemStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
    }

    public void Dispose() => _root.Dispose();

    private string WriteXml(string body)
    {
        Directory.CreateDirectory(_root.Paths.Root);
        var path = Path.Combine(_root.Paths.Root, Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(path, $"<HubData>{body}</HubData>", new UTF8Encoding(false));
        return path;
    }

    private async Task<LegacyImportResult> ImportFileAsync(string path)
    {
        var preview = await _import.PreviewAsync(path);
        Assert.True(preview.Parse.IsValid, preview.Parse.Error);
        var result = await _import.ImportAsync(preview);
        Assert.True(result.Succeeded, result.Error);
        return result;
    }

    /// <summary>Byte-level fingerprint of the database file (opened shared, since pooled connections keep it open).</summary>
    private string HashDatabase()
    {
        using var stream = new FileStream(_root.Paths.DatabaseFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private (long Systems, long Memberships) Counts()
    {
        using var connection = _root.Open();
        return (Db.Count(connection, "Systems"), Db.Count(connection, "SystemRepositories"));
    }

    private async Task<long> CreateSystemAsync(string code, string name, params MembershipDraft[] memberships)
    {
        var result = await _systems.CreateAsync(new SystemDraft(code, name, null, true, memberships));
        Assert.True(result.Succeeded);
        return result.Id!.Value;
    }

    private async Task<Dictionary<RepositoryType, MembershipDetails>> MembershipsAsync(string code)
    {
        long id;
        using (var connection = _root.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id FROM Systems WHERE SystemCode = $code;";
            command.Parameters.AddWithValue("$code", code);
            id = (long)command.ExecuteScalar()!;
        }

        return (await _systems.GetAsync(id))!.Memberships.ToDictionary(m => m.Type);
    }

    // ---- Mapping --------------------------------------------------------------------------------------------------

    [Fact]
    public void Mapping_IsExactlyTheApprovedTypes()
    {
        Assert.Equal([RepositoryType.SvnSystem, RepositoryType.GitSystem, RepositoryType.Release], LegacyMapping.TypesFor(LegacySection.System));
        Assert.Equal([RepositoryType.SvnOracle, RepositoryType.GitOracle], LegacyMapping.TypesFor(LegacySection.Oracle));
    }

    [Fact]
    public async Task SystemEntry_GetsSvnSystemGitSystemRelease_EnabledWithoutCustomTarget()
    {
        await ImportFileAsync(WriteXml("""<SYSTEM><Item SystemCode="4000000375" SystemName="ConSite API" /></SYSTEM>"""));

        var memberships = await MembershipsAsync("4000000375");
        Assert.Equal([RepositoryType.SvnSystem, RepositoryType.GitSystem, RepositoryType.Release], memberships.Keys.Order());
        Assert.All(memberships.Values, m => Assert.True(m.IsEnabled));
        Assert.All(memberships.Values, m => Assert.Null(m.CustomTarget));
    }

    [Fact]
    public async Task OracleEntry_GetsSvnOracleGitOracle_Only()
    {
        await ImportFileAsync(WriteXml("""<ORACLE><Item SystemCode="Oracle_CONSITE" SystemName="CONSITE" /></ORACLE>"""));

        var memberships = await MembershipsAsync("Oracle_CONSITE");
        Assert.Equal([RepositoryType.SvnOracle, RepositoryType.GitOracle], memberships.Keys.Order());
        Assert.All(memberships.Values, m => Assert.True(m.IsEnabled && m.CustomTarget is null));
    }

    [Fact]
    public async Task SameCodeInBothSections_IsOneSystemWithAllFiveMemberships()
    {
        var result = await ImportFileAsync(WriteXml("""
            <SYSTEM><Item SystemCode="ABC001" SystemName="System A" /></SYSTEM>
            <ORACLE><Item SystemCode="ABC001" SystemName="System A" /></ORACLE>
            """));

        Assert.Equal(1, result.Applied!.NewSystems);
        Assert.Equal(5, result.Applied.NewMemberships);
        Assert.Equal((1, 5), Counts());
    }

    [Fact]
    public async Task IdenticalDuplicates_CollapseToOneSystem()
    {
        var result = await ImportFileAsync(WriteXml("""<SYSTEM><Item SystemCode="ABC001" SystemName="Test" /><Item SystemCode="abc001" SystemName="Test" /></SYSTEM>"""));

        Assert.Equal(1, result.Applied!.NewSystems);
        Assert.Equal(1, result.Applied.DuplicateEntriesMerged);
        Assert.Empty(result.Applied.Conflicts);
        Assert.Equal((1, 3), Counts());
    }

    [Theory]
    [InlineData("""<SYSTEM><Item SystemCode="ABC001" SystemName="Test" /><Item SystemCode="ABC001" SystemName="Other" /></SYSTEM>""")]
    [InlineData("""<SYSTEM><Item SystemCode="ABC001" SystemName="System A" /></SYSTEM><ORACLE><Item SystemCode="ABC001" SystemName="Different Name" /></ORACLE>""")]
    public async Task SameCodeDifferentNamesInFile_IsAConflictAndNothingIsCreated(string body)
    {
        var preview = await _import.PreviewAsync(WriteXml(body));

        var conflict = Assert.Single(preview.Plan!.Conflicts);
        Assert.Equal(LegacyConflictKind.NamesDifferInFile, conflict.Kind);
        Assert.Equal("ABC001", conflict.SystemCode);
        Assert.Equal(2, conflict.LegacyNames.Count);
        Assert.False(preview.Plan.HasChanges);

        await _import.ImportAsync(preview);
        Assert.Equal((0, 0), Counts());
    }

    // ---- Validation ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task InvalidEntries_AreReportedAndNeverInserted()
    {
        var preview = await _import.PreviewAsync(WriteXml("""
            <SYSTEM>
              <Item SystemCode="" SystemName="Test" />
              <Item SystemCode="GOOD01" SystemName="Good" />
              <Item SystemCode="NONAME" SystemName="   " />
            </SYSTEM>
            <ORACLE>
              <Item SystemCode="Oracle Test" SystemName="APL" />
              <Item SystemName="Missing code" />
            </ORACLE>
            """));

        var invalid = preview.Plan!.InvalidEntries;
        Assert.Equal(4, invalid.Count);
        Assert.Contains(invalid, i => i.Section == LegacySection.System && i.SystemCode == "" && i.Reason == "System code is required.");
        Assert.Contains(invalid, i => i.SystemCode == "NONAME" && i.Reason == "SystemName is required.");
        Assert.Contains(invalid, i => i.Section == LegacySection.Oracle && i.SystemCode == "Oracle Test" && i.SystemName == "APL");
        Assert.Equal(1, preview.Plan.NewSystems);

        await _import.ImportAsync(preview);
        Assert.Equal((1, 3), Counts());
    }

    [Fact]
    public async Task NamesAreTrimmedButOtherwiseUnchanged()
    {
        await ImportFileAsync(WriteXml("""<SYSTEM><Item SystemCode="  4000000211 " SystemName="  Repor  Service  " /></SYSTEM>"""));

        var item = Assert.Single((await _systems.QueryAsync(new SystemQuery(RepositoryType.SvnSystem))).Items);
        Assert.Equal("4000000211", item.SystemCode);
        Assert.Equal("Repor  Service", item.SystemName);
    }

    [Fact]
    public async Task FileWithNoEntries_HasNothingToImport()
    {
        var preview = await _import.PreviewAsync(WriteXml(""));

        Assert.True(preview.Parse.IsValid);
        Assert.False(preview.Plan!.HasChanges);
        Assert.Equal(0, preview.Plan.SystemEntries + preview.Plan.OracleEntries);
    }

    // ---- Existing data -------------------------------------------------------------------------------------------

    [Fact]
    public async Task ExistingSameNameSystem_GetsOnlyMissingMemberships()
    {
        var id = await CreateSystemAsync("4000000375", "ConSite API", new MembershipDraft(RepositoryType.SvnSystem, true));

        var result = await ImportFileAsync(WriteXml("""<SYSTEM><Item SystemCode="4000000375" SystemName="ConSite API" /></SYSTEM>"""));

        Assert.Equal((0, 1, 2, 1), (result.Applied!.NewSystems, result.Applied.ExistingSystems, result.Applied.NewMemberships, result.Applied.ExistingMemberships));
        Assert.Equal((1, 3), Counts());
        Assert.Equal(id, (await _systems.QueryAsync(new SystemQuery(RepositoryType.Release))).Items.Single().Id);
    }

    [Fact]
    public async Task ExistingSystemMatchedIgnoringCodeCase()
    {
        await CreateSystemAsync("ORACLE_APL", "APL", new MembershipDraft(RepositoryType.SvnOracle, true));

        var result = await ImportFileAsync(WriteXml("""<ORACLE><Item SystemCode="Oracle_APL" SystemName="APL" /></ORACLE>"""));

        Assert.Equal(0, result.Applied!.NewSystems);
        Assert.Equal(1, result.Applied.NewMemberships);
        Assert.Equal((1, 2), Counts());
    }

    [Fact]
    public async Task ExistingDifferentName_IsConflict_CurrentNameKept_NothingAttached()
    {
        await CreateSystemAsync("4000000375", "ConSite API New", new MembershipDraft(RepositoryType.SvnSystem, true));
        var path = WriteXml("""<SYSTEM><Item SystemCode="4000000375" SystemName="ConSite API" /></SYSTEM>""");

        var preview = await _import.PreviewAsync(path);
        var conflict = Assert.Single(preview.Plan!.Conflicts);
        Assert.Equal((LegacyConflictKind.ExistingNameDiffers, "4000000375", "ConSite API New", "ConSite API"),
            (conflict.Kind, conflict.SystemCode, conflict.CurrentName, conflict.LegacyNames.Single()));
        Assert.Contains("Existing SystemName differs", conflict.Reason);

        var result = await _import.ImportAsync(preview);

        Assert.True(result.Succeeded);
        Assert.Single(result.Applied!.Conflicts);
        var stored = (await _systems.QueryAsync(new SystemQuery(RepositoryType.SvnSystem))).Items.Single();
        Assert.Equal("ConSite API New", stored.SystemName);
        Assert.Equal((1, 1), Counts());
    }

    [Fact]
    public async Task ExistingDisabledMembership_StaysDisabled()
    {
        await CreateSystemAsync("ABC001", "System A", new MembershipDraft(RepositoryType.SvnSystem, true), new MembershipDraft(RepositoryType.GitSystem, false));

        var result = await ImportFileAsync(WriteXml("""<SYSTEM><Item SystemCode="ABC001" SystemName="System A" /></SYSTEM>"""));

        Assert.Equal(1, result.Applied!.DisabledMembershipsKept);
        Assert.Equal(2, result.Applied.ExistingMemberships);
        Assert.Equal(1, result.Applied.NewMemberships);
        var memberships = await MembershipsAsync("ABC001");
        Assert.False(memberships[RepositoryType.GitSystem].IsEnabled);
        Assert.True(memberships[RepositoryType.Release].IsEnabled);
    }

    [Fact]
    public async Task ExistingCustomTarget_IsPreserved()
    {
        await CreateSystemAsync("ABC001", "System A", new MembershipDraft(RepositoryType.GitSystem, true, "https://git.example.test/special/abc"));

        await ImportFileAsync(WriteXml("""<SYSTEM><Item SystemCode="ABC001" SystemName="System A" /></SYSTEM>"""));

        var memberships = await MembershipsAsync("ABC001");
        Assert.Equal("https://git.example.test/special/abc", memberships[RepositoryType.GitSystem].CustomTarget);
        Assert.Null(memberships[RepositoryType.SvnSystem].CustomTarget);
    }

    [Fact]
    public async Task PreviewNeverWrites()
    {
        var before = Counts();
        var hash = HashDatabase();

        var preview = await _import.PreviewAsync(FixturePath);

        Assert.Equal(75, preview.Plan!.NewSystems);
        Assert.Equal(before, Counts());
        Assert.Equal(hash, HashDatabase());
    }

    [Fact]
    public async Task ImportRePlansInsideTheTransaction_WhenDataChangedAfterPreview()
    {
        var preview = await _import.PreviewAsync(WriteXml("""<SYSTEM><Item SystemCode="ABC001" SystemName="A" /></SYSTEM>"""));
        await CreateSystemAsync("ABC001", "A", new MembershipDraft(RepositoryType.SvnSystem, true));

        var result = await _import.ImportAsync(preview);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Applied!.NewSystems);
        Assert.Equal(2, result.Applied.NewMemberships);
        Assert.Equal((1, 3), Counts());
    }

    // ---- Atomicity -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task DatabaseFailureMidImport_RollsBackEverything_AndReportsNoSuccess()
    {
        using (var connection = _root.Open())
        {
            // Fails on the 50th System insert, after many rows were already written in the same transaction.
            Db.Exec(connection, """
                CREATE TRIGGER FailMidImport BEFORE INSERT ON Systems
                WHEN NEW.SystemCode = 'Oracle_DPM'
                BEGIN SELECT RAISE(ABORT, 'simulated failure'); END;
                """);
        }

        var preview = await _import.PreviewAsync(FixturePath);
        var result = await _import.ImportAsync(preview);

        Assert.False(result.Succeeded);
        Assert.Null(result.Applied);
        Assert.Contains("rolled back", result.Error);
        Assert.Equal((0, 0), Counts());
        Assert.Contains(_log.Errors, e => e.Contains("rolled back"));
        Assert.DoesNotContain(_log.Infos, i => i.StartsWith("Legacy import completed", StringComparison.Ordinal));
    }

    // ---- Known legacy dataset ------------------------------------------------------------------------------------

    [Fact]
    public async Task KnownLegacyDataset_ImportsExactly75SystemsAnd192Memberships()
    {
        var preview = await _import.PreviewAsync(FixturePath);
        var plan = preview.Plan!;
        Assert.Equal((42, 33, 75, 0, 192, 0, 0, 0),
            (plan.SystemEntries, plan.OracleEntries, plan.NewSystems, plan.ExistingSystems, plan.NewMemberships, plan.ExistingMemberships, plan.Conflicts.Count, plan.InvalidEntries.Count));

        var result = await _import.ImportAsync(preview);

        Assert.True(result.Succeeded);
        Assert.Equal((75, 192), Counts());
        Assert.Equal(42, (await _systems.QueryAsync(new SystemQuery(RepositoryType.SvnSystem))).Items.Count);
        Assert.Equal(42, (await _systems.QueryAsync(new SystemQuery(RepositoryType.GitSystem))).Items.Count);
        Assert.Equal(42, (await _systems.QueryAsync(new SystemQuery(RepositoryType.Release))).Items.Count);
        Assert.Equal(33, (await _systems.QueryAsync(new SystemQuery(RepositoryType.SvnOracle))).Items.Count);
        Assert.Equal(33, (await _systems.QueryAsync(new SystemQuery(RepositoryType.GitOracle))).Items.Count);

        using var connection = _root.Open();
        Assert.Equal(0, Db.ScalarLong(connection, "SELECT count(*) FROM SystemRepositories WHERE IsEnabled = 0 OR CustomTarget IS NOT NULL;"));
        Assert.Equal(75, Db.ScalarLong(connection, "SELECT count(DISTINCT SystemCode) FROM Systems;"));
        Assert.Contains(_log.Infos, i => i == "Legacy import completed: 75 systems added, 0 already existing, 192 memberships added, 0 already existing, 0 conflicts skipped, 0 invalid entries skipped.");
    }

    [Theory]
    [InlineData("4000000211", "Repor Service", RepositoryType.Release)]
    [InlineData("4000000305", "Owner's Site (オーナーズサイト)", RepositoryType.SvnSystem)]
    [InlineData("4000000433", "ConSite Remanufacturing（再生品Web管理システム）", RepositoryType.GitSystem)]
    [InlineData("Sub0000001", "データ連携(ATRAN3)(日本側)", RepositoryType.SvnSystem)]
    [InlineData("Oracle", "Oracle全般", RepositoryType.SvnOracle)]
    [InlineData("Oracle_CN", "中国GeS", RepositoryType.GitOracle)]
    public async Task KnownLegacyDataset_NamesSurviveUnchanged(string code, string name, RepositoryType type)
    {
        await ImportFileAsync(FixturePath);

        var item = Assert.Single((await _systems.QueryAsync(new SystemQuery(type))).Items, i => i.SystemCode == code);
        Assert.Equal(name, item.SystemName);
    }

    [Fact]
    public async Task ImportingTwice_CreatesNoDuplicates()
    {
        await ImportFileAsync(FixturePath);

        var second = await ImportFileAsync(FixturePath);

        Assert.Equal((0, 75, 0, 192), (second.Applied!.NewSystems, second.Applied.ExistingSystems, second.Applied.NewMemberships, second.Applied.ExistingMemberships));
        Assert.Equal((75, 192), Counts());
    }

    [Fact]
    public async Task Import_DoesNotTouchOwnerDataOrCreateVault()
    {
        var owners = new Core.Owner.OwnerService(new SqliteOwnerStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)), new NullOwnerLauncher());
        var (group, _) = await owners.CreateGroupAsync("Tools");
        await owners.CreateItemAsync(group!.Id, new Core.Owner.OwnerItemDraft("Portal", Core.Owner.OwnerActionType.Web, "https://portal.example.test/"));

        await ImportFileAsync(FixturePath);

        Assert.Equal(["Tools"], (await owners.ListGroupsAsync()).Select(g => g.Name));
        Assert.Single(await owners.ListItemsAsync(group.Id));
        Assert.False(Directory.Exists(_root.Paths.SecurityDirectory));
    }

    private sealed class NullOwnerLauncher : Core.Owner.IOwnerLauncher
    {
        public Core.Owner.OwnerLaunchResult Launch(Core.Owner.OwnerItemDetails item) => Core.Owner.OwnerLaunchResult.Ok;
    }

    private sealed class RecordingLog : IAppLog
    {
        public List<string> Infos { get; } = [];

        public List<string> Errors { get; } = [];

        public void Info(string message) => Infos.Add(message);

        public void Warn(string message) => Infos.Add(message);

        public void Error(string message, Exception? exception = null) => Errors.Add(message);
    }
}
