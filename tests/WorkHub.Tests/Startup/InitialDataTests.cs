using Microsoft.Data.Sqlite;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Repositories;
using WorkHub.Core.Settings;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure;
using WorkHub.Infrastructure.LegacyImport;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Infrastructure.Persistence.Migrations;
using WorkHub.Infrastructure.Settings;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Startup;

/// <summary>
/// A NEW environment gets the approved initial Systems and repository configuration on its first successful start;
/// existing profiles are never seeded or reconfigured. Uses the real application composition (<see cref="WorkHubServices"/>).
/// </summary>
public sealed class InitialDataTests : IDisposable
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "LegacyHubData.xml");

    private readonly TempWorkHubRoot _root = new();

    public void Dispose() => _root.Dispose();

    /// <summary>The production startup path (settings + database with initial data).</summary>
    private InitializationResult StartApp() => WorkHubServices.Create(_root.Paths).Initializer.Initialize();

    private long Scalar(string sql)
    {
        using var connection = _root.Open();
        return Db.ScalarLong(connection, sql);
    }

    private List<string> Rows(string sql)
    {
        using var connection = _root.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(reader.GetString(0));
        return rows;
    }

    private const string SnapshotSql = """
        SELECT 'S|' || SystemCode || '|' || SystemName || '|' || IsActive FROM Systems
        UNION ALL SELECT 'M|' || s.SystemCode || '|' || r.RepositoryType || '|' || r.IsEnabled || '|' || ifnull(r.CustomTarget, '<null>')
                  FROM SystemRepositories r JOIN Systems s ON s.Id = r.SystemId
        UNION ALL SELECT 'G|' || Name FROM OwnerGroups
        UNION ALL SELECT 'W|' || Name FROM WorkspaceNodes
        ORDER BY 1;
        """;

    // ---- Seed source ------------------------------------------------------------------------------------------------

    [Fact]
    public void EmbeddedSeed_IsByteIdenticalToTheReviewedFixture()
    {
        using var stream = typeof(InitialSystemsSeed).Assembly.GetManifestResourceStream(InitialSystemsSeed.ResourceName)!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);

        Assert.Equal(File.ReadAllBytes(FixturePath), copy.ToArray());
    }

    [Fact]
    public void EmbeddedSeed_ParsesTo42SystemAnd33OracleEntries()
    {
        var parse = InitialSystemsSeed.Read();

        Assert.True(parse.IsValid, parse.Error);
        Assert.Equal(42, parse.SystemEntryCount);
        Assert.Equal(33, parse.OracleEntryCount);
        Assert.Equal(0, parse.IgnoredElements);
    }

    // ---- New environment ------------------------------------------------------------------------------------------------

    [Fact]
    public void FreshEnvironment_HasExactly75Systems_And192EnabledMembershipsWithoutCustomTargets()
    {
        var result = StartApp();

        Assert.Equal(DatabaseInitializationOutcome.Created, result.Database.Outcome);
        Assert.Equal(3, result.Database.SchemaVersion);
        Assert.Equal(75, Scalar("SELECT count(*) FROM Systems;"));
        Assert.Equal(192, Scalar("SELECT count(*) FROM SystemRepositories;"));
        Assert.Equal(192, Scalar("SELECT count(*) FROM SystemRepositories WHERE IsEnabled = 1 AND CustomTarget IS NULL;"));
        Assert.Equal(75, Scalar("SELECT count(*) FROM Systems WHERE IsActive = 1 AND Description IS NULL;"));
        Assert.Equal(0, Scalar("SELECT count(*) FROM OwnerGroups;"));
        Assert.Equal(0, Scalar("SELECT count(*) FROM WorkspaceNodes;"));
        Assert.Equal("ok", Rows("PRAGMA quick_check;").Single());
        Assert.False(File.Exists(_root.Paths.DatabaseFile + ".new"));
    }

    [Fact]
    public void FreshEnvironment_KeepsEveryCodeAndNameExactly_AndMapsSectionsToTheApprovedTypes()
    {
        StartApp();
        var fixture = new LegacyXmlParser().Parse(FixturePath).Entries;

        var expectedSystems = fixture.Select(e => $"{e.SystemCode}|{e.SystemName}").Distinct().Order(StringComparer.Ordinal);
        Assert.Equal(expectedSystems, Rows("SELECT SystemCode || '|' || SystemName FROM Systems;").Order(StringComparer.Ordinal));

        var expectedMemberships = fixture.SelectMany(e => e.Section == Core.LegacyImport.LegacySection.System
                ? new[] { "SVN_SYSTEM", "GIT_SYSTEM", "RELEASE" }.Select(t => $"{e.SystemCode}|{t}")
                : new[] { "SVN_ORACLE", "GIT_ORACLE" }.Select(t => $"{e.SystemCode}|{t}"))
            .Order(StringComparer.Ordinal);
        Assert.Equal(expectedMemberships,
            Rows("SELECT s.SystemCode || '|' || r.RepositoryType FROM SystemRepositories r JOIN Systems s ON s.Id = r.SystemId;").Order(StringComparer.Ordinal));

        Assert.Equal(["MST (Zaxis@net 機種グループ変更)"], Rows("SELECT SystemName FROM Systems WHERE SystemCode = '4000000037';"));
        Assert.Equal(["Repor Service"], Rows("SELECT SystemName FROM Systems WHERE SystemCode = '4000000211';"));
        Assert.Equal(["中国GeS"], Rows("SELECT SystemName FROM Systems WHERE SystemCode = 'Oracle_CN';"));
    }

    [Fact]
    public void Restarting_NeverDuplicatesOrReseeds()
    {
        StartApp();
        var first = Rows(SnapshotSql);

        var second = StartApp();
        var third = StartApp();

        Assert.Equal(DatabaseInitializationOutcome.Opened, second.Database.Outcome);
        Assert.Equal(DatabaseInitializationOutcome.Opened, third.Database.Outcome);
        Assert.Equal(first, Rows(SnapshotSql));
        Assert.Equal(75, Scalar("SELECT count(*) FROM Systems;"));
    }

    [Fact]
    public void SystemsRemovedOrEditedByTheUser_AreNotRecreatedOnRestart()
    {
        StartApp();
        using (var connection = _root.Open())
        {
            Db.Exec(connection, "DELETE FROM Systems WHERE SystemCode = '4000000002';");
            Db.Exec(connection, "UPDATE Systems SET SystemName = 'Renamed' WHERE SystemCode = '4000000006';");
            Db.Exec(connection, "UPDATE SystemRepositories SET IsEnabled = 0, CustomTarget = 'https://svn.example.test/x' WHERE RepositoryType = 'RELEASE' AND SystemId = (SELECT Id FROM Systems WHERE SystemCode = '4000000037');");
        }

        var before = Rows(SnapshotSql);
        StartApp();

        Assert.Equal(before, Rows(SnapshotSql));
        Assert.Equal(74, Scalar("SELECT count(*) FROM Systems;"));
    }

    // ---- Existing profiles ------------------------------------------------------------------------------------------

    [Fact]
    public void ExistingDatabaseWithIntentionallyEmptySystems_IsNotSeeded()
    {
        _root.CreateInitializer().Initialize(); // an existing v3 profile without Systems (no seed)
        Assert.Equal(0, Scalar("SELECT count(*) FROM Systems;"));

        var result = StartApp();

        Assert.Equal(DatabaseInitializationOutcome.Opened, result.Database.Outcome);
        Assert.Equal(0, Scalar("SELECT count(*) FROM Systems;"));
        Assert.Equal(0, Scalar("SELECT count(*) FROM SystemRepositories;"));
    }

    [Fact]
    public void ExistingProfile_KeepsSystemsOwnerWorkspaceAndSettings()
    {
        _root.CreateInitializer().Initialize();
        using (var connection = _root.Open())
        {
            var id = Db.InsertSystem(connection, "4000000002", "M-Find (renamed by user)");
            Db.Exec(connection, "INSERT INTO SystemRepositories (SystemId, RepositoryType, IsEnabled, CustomTarget) VALUES ($id, 'SVN_SYSTEM', 0, 'https://svn.example.test/custom');", ("$id", id));
            Db.Exec(connection, "INSERT INTO OwnerGroups (Name, NameKey, CreatedAt, UpdatedAt) VALUES ('ツール', 'ツール', $n, $n);", ("$n", Db.Now));
            Db.InsertNode(connection, null, "Folder", "Clients");
        }

        var settings = AppSettings.CreateDefault(); // the user's own configuration, including deliberately empty values
        settings.Repository.Svn.Host = "https://svn.example.test/svn/";
        settings.Repository.Git.Host = string.Empty;
        settings.Repository.Svn.OraclePrefix = string.Empty;
        new JsonSettingsStore(_root.Paths.SettingsFile).Save(settings);
        var settingsBefore = File.ReadAllBytes(_root.Paths.SettingsFile);
        var dataBefore = Rows(SnapshotSql);

        var result = StartApp();

        Assert.Equal(DatabaseInitializationOutcome.Opened, result.Database.Outcome);
        Assert.Equal(SettingsLoadStatus.Loaded, result.Settings.Status);
        Assert.Equal(dataBefore, Rows(SnapshotSql));
        Assert.Equal(settingsBefore, File.ReadAllBytes(_root.Paths.SettingsFile));
        Assert.Equal(string.Empty, result.Settings.Settings.Repository.Git.Host);
        Assert.Equal(string.Empty, result.Settings.Settings.Repository.Svn.OraclePrefix);
        Assert.Equal("https://svn.example.test/svn/", result.Settings.Settings.Repository.Svn.Host);
    }

    [Fact]
    public void ExistingV2Profile_IsMigratedButNotSeeded()
    {
        _root.CreateDatabaseInitializer(new V001_InitialSchema(), new V002_OwnerManager()).Initialize();
        using (var connection = _root.Open())
            Db.InsertSystem(connection, "ABC001", "販売管理");

        var result = StartApp();

        Assert.Equal(DatabaseInitializationOutcome.Migrated, result.Database.Outcome);
        Assert.Equal(["ABC001"], Rows("SELECT SystemCode FROM Systems;"));
    }

    // ---- Failure and recovery ----------------------------------------------------------------------------------------

    [Fact]
    public void FailedSeeding_KeepsNothing_AndTheNextStartCreatesTheCompleteDatabase()
    {
        var failing = new DatabaseInitializer(_root.Paths, NullAppLog.Instance, (connection, transaction, now) =>
        {
            InitialSystemsSeed.Apply(connection, transaction, now); // rows written inside the transaction...
            throw new IOException("simulated failure during first start"); // ...then the start fails
        });

        var ex = Assert.Throws<DatabaseStartupException>(failing.Initialize);

        Assert.Equal(DatabaseStartupFailure.NewDatabaseFailed, ex.Failure);
        Assert.Contains("will try again", ex.Message);
        Assert.False(File.Exists(_root.Paths.DatabaseFile)); // no empty database that would block a later seed
        Assert.False(File.Exists(failing.StagingFile));

        var result = StartApp();

        Assert.Equal(DatabaseInitializationOutcome.Created, result.Database.Outcome);
        Assert.Equal(75, Scalar("SELECT count(*) FROM Systems;"));
        Assert.Equal(192, Scalar("SELECT count(*) FROM SystemRepositories;"));
    }

    [Fact]
    public void FailedMigrationOfANewDatabase_KeepsNothing()
    {
        var broken = new DatabaseInitializer(_root.Paths, [new V001_InitialSchema(), new BrokenMigration()], TimeProvider.System,
            NullAppLog.Instance, InitialSystemsSeed.Apply);

        var ex = Assert.Throws<DatabaseStartupException>(broken.Initialize);

        Assert.Equal(DatabaseStartupFailure.NewDatabaseFailed, ex.Failure);
        Assert.False(File.Exists(_root.Paths.DatabaseFile));
        Assert.False(File.Exists(broken.StagingFile));
    }

    [Fact]
    public void Seed_RefusesToApplyToADatabaseThatAlreadyHasSystems_AndWritesNothing()
    {
        _root.CreateInitializer().Initialize();
        using var connection = _root.Open();
        Db.InsertSystem(connection, "4000000002", "M-Find");

        using (var transaction = connection.BeginTransaction())
        {
            var ex = Assert.Throws<InvalidDataException>(() => InitialSystemsSeed.Apply(connection, transaction, DateTimeOffset.UtcNow));
            Assert.Contains("did not apply cleanly", ex.Message);
        } // disposed without commit: rolled back

        Assert.Equal(1, Db.Count(connection, "Systems"));
        Assert.Equal(0, Db.Count(connection, "SystemRepositories"));
    }

    [Fact]
    public void InterruptedEarlierStart_LeftoverStagingFileIsDiscarded()
    {
        // An earlier first start was killed mid-way: only the staging file exists (here: a partial database).
        Directory.CreateDirectory(_root.Paths.DataDirectory);
        var staging = _root.Paths.DatabaseFile + ".new";
        using (var partial = new SqliteConnection($"Data Source={staging};Pooling=False"))
        {
            partial.Open();
            using var command = partial.CreateCommand();
            command.CommandText = "CREATE TABLE Systems (Id INTEGER); INSERT INTO Systems VALUES (1);";
            command.ExecuteNonQuery();
        }

        var result = StartApp();

        Assert.Equal(DatabaseInitializationOutcome.Created, result.Database.Outcome);
        Assert.False(File.Exists(staging));
        Assert.Equal(75, Scalar("SELECT count(*) FROM Systems;"));
    }

    [Fact]
    public void EmptyDatabaseFile_IsTreatedAsNew_AndSeeded()
    {
        Directory.CreateDirectory(_root.Paths.DataDirectory);
        using (var empty = new SqliteConnection($"Data Source={_root.Paths.DatabaseFile};Pooling=False"))
            empty.Open(); // creates a zero-length SQLite file with no objects

        var result = StartApp();

        Assert.Equal(DatabaseInitializationOutcome.Created, result.Database.Outcome);
        Assert.Equal(75, Scalar("SELECT count(*) FROM Systems;"));
    }

    // ---- Initial repository configuration ---------------------------------------------------------------------------

    [Fact]
    public void FreshEnvironment_SettingsResolveTheApprovedTargets()
    {
        var settings = StartApp().Settings.Settings.Repository;
        string Target(RepositoryType type) => RepositoryTargetResolver.Resolve(type, "4000000002", null, settings).Target!;

        Assert.Equal("http://10.194.105.25/svn/GeSL_doc_4000000002", Target(RepositoryType.SvnSystem));
        Assert.Equal("http://10.194.105.25/svn/GeSL_doc_4000000002", Target(RepositoryType.SvnOracle));
        Assert.Equal("http://10.194.105.25:8080/gitbucket/root/GeSL_src_4000000002", Target(RepositoryType.GitSystem));
        Assert.Equal("http://10.194.105.25:8080/gitbucket/root/GeSL_src_4000000002", Target(RepositoryType.GitOracle));
        Assert.Equal("http://10.194.105.25/svn/GeSL_rel_4000000002", Target(RepositoryType.Release)); // existing RELEASE prefix, shared SVN host
        Assert.Equal("C:/Program Files/TortoiseSVN/bin/TortoiseProc.exe", settings.Svn.TortoiseProcPath);
    }

    [Fact]
    public void InitialTortoisePath_IsValidatedLikeAnyOther_WhenTortoiseIsMissing()
    {
        var settings = AppSettings.CreateInitial().Repository;

        var issues = RepositorySettingsValidator.Validate(settings, RepositoryUsage.None, _ => false);

        var issue = Assert.Single(issues, i => i.Field == RepositorySettingsValidator.Fields.TortoiseProcPath);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Equal("TortoiseSVN executable was not found.", issue.Message);
        Assert.DoesNotContain(issues, i => i.Field != RepositorySettingsValidator.Fields.TortoiseProcPath);
    }

    private sealed class BrokenMigration : ISchemaMigration
    {
        public int Version => 2;

        public string Description => "broken test migration";

        public void Apply(SqliteConnection connection, SqliteTransaction transaction) =>
            connection.Execute("CREATE TABLE Partial (Id INTEGER); INSERT INTO DoesNotExist VALUES (1);", transaction);
    }
}
