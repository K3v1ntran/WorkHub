using Microsoft.Data.Sqlite;
using WorkHub.Core.Backup;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Restore;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.Backup;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Infrastructure.Persistence.Migrations;
using WorkHub.Infrastructure.Restore;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Backup;

/// <summary>
/// Checkout line endings (Schema_Fingerprint_LineEndings_Investigation Revision 2, LE-1 to LE-6, LE-11, LE-14). A build
/// compiles the frozen migrations with its checkout's line terminators and SQLite stores them verbatim. Reference
/// identity therefore ignores CRLF vs LF in definitions; source-to-copy fidelity stays exact.
/// <para>A "producer" is a build that created a database or backup, a "consumer" a build that validates it. Each is
/// simulated by <see cref="LineEndingMigrations"/> (its migrations) and a <see cref="SchemaReference"/> over them.</para>
/// </summary>
public sealed class LineEndingCompatibilityTests : IDisposable
{
    private static readonly char Cr = '\r';
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "WorkHubTests", Guid.NewGuid().ToString("N"));
    private readonly SettableClock _clock = new(SettableClock.Tokyo(2026, 10, 18, 12));

    public LineEndingCompatibilityTests()
    {
        DefaultProfileGuard.EnsureIsolated(_dir);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        BackupKit.ClearPoolsUnder(_dir);
        Directory.Delete(_dir, recursive: true);
    }

    public static TheoryData<Checkout, Checkout> Cells()
    {
        var cells = new TheoryData<Checkout, Checkout>();
        foreach (var producer in Enum.GetValues<Checkout>())
            foreach (var consumer in Enum.GetValues<Checkout>())
                cells.Add(producer, consumer);
        return cells;
    }

    /// <summary>The current schema version (v9: V009 Workspace folder lifecycle).</summary>
    private static readonly int Current = SchemaMigrations.LatestVersion;

    /// <summary>LE-14 (current schema, v9): the producer's database and daily backup, validated by the consumer's real paths.</summary>
    [Theory]
    [MemberData(nameof(Cells))]
    public async Task CurrentSchema_IsAcceptedAcrossCheckouts(Checkout producer, Checkout consumer)
    {
        Assert.Equal(9, Current);
        var paths = Produce(producer, Current, "current");
        var daily = Directory.GetFiles(paths.DailyBackupsDirectory, "*.db").Single();
        var build = Consumer(consumer, paths);

        Assert.Equal(BackupStatus.FullyValidated, build.Validator.Check(daily, BackupCheckLevel.Full, Current).Status);
        var entry = (await build.Catalog.ListAsync()).Single(e => e.Kind == BackupKind.Daily);
        Assert.Equal(BackupStatus.FullyValidated, (await build.Catalog.CheckAsync(entry, BackupCheckLevel.Full)).Status);
        Assert.Equal(BackupCompatibility.Current, build.Stage(daily, Current).Compatibility);
        Assert.Equal(new DataClassification(CurrentDataClass.Healthy, Version: Current), build.Classifier.ClassifyReadOnly(paths.DatabaseFile));

        Assert.Equal(DatabaseInitializationOutcome.Opened, build.Initializer.Initialize().Outcome);
        BackupKit.ClearPools(paths.DatabaseFile);
        _clock.Now = _clock.Now.AddDays(1);
        var next = build.Daily.Run();
        Assert.True(next.Outcome == DailyBackupOutcome.Created, $"{producer}->{consumer}: {next}");
    }

    public static TheoryData<Checkout, Checkout, int> OlderCells()
    {
        var cells = new TheoryData<Checkout, Checkout, int>();
        foreach (var version in new[] { 5, 7, 8 })
            foreach (var producer in Enum.GetValues<Checkout>())
                foreach (var consumer in Enum.GetValues<Checkout>())
                    cells.Add(producer, consumer, version);
        return cells;
    }

    /// <summary>
    /// LE-2 / LE-14 (v5, v7, and v8 — the schema every Phase 13B upgrade starts from): Older candidates, and the upgrade to v9
    /// that leaves a cross-build hybrid.
    /// </summary>
    [Theory]
    [MemberData(nameof(OlderCells))]
    public async Task OlderSchema_IsAcceptedAndUpgradedAcrossCheckouts_AndTheHybridIsAcceptedByEveryCheckout(Checkout producer, Checkout consumer, int version)
    {
        var paths = Produce(producer, version, $"v{version}");
        var daily = Directory.GetFiles(paths.DailyBackupsDirectory, "*.db").Single();
        var build = Consumer(consumer, paths);

        Assert.Equal(BackupStatus.FullyValidated, build.Validator.Check(daily, BackupCheckLevel.Full, version).Status);
        Assert.Equal(BackupCompatibility.Older, build.Stage(daily, version).Compatibility);
        Assert.Equal(new DataClassification(CurrentDataClass.Healthy, Version: version), build.Classifier.ClassifyReadOnly(paths.DatabaseFile));

        var upgrade = build.Initializer.Initialize();
        BackupKit.ClearPools(paths.DatabaseFile);
        Assert.Equal(DatabaseInitializationOutcome.Migrated, upgrade.Outcome);
        Assert.Equal(Current, upgrade.SchemaVersion);
        var preMigration = (await build.Catalog.ListAsync()).Single(e => e.Kind == BackupKind.PreMigration);
        Assert.Equal(BackupStatus.FullyValidated, (await build.Catalog.CheckAsync(preMigration, BackupCheckLevel.Full)).Status);

        foreach (var other in Enum.GetValues<Checkout>())
            Assert.Equal(new DataClassification(CurrentDataClass.Healthy, Version: Current), new CurrentDataClassifier(Reference(other)).ClassifyReadOnly(paths.DatabaseFile));

        _clock.Now = _clock.Now.AddDays(1);
        var next = build.Daily.Run();
        Assert.True(next.Outcome == DailyBackupOutcome.Created, $"{producer}->{consumer}: {next}");
    }

    [Fact]
    public void ACrossCheckoutUpgrade_MixesLineEndingsInsideOneObject()
    {
        var paths = Produce(Checkout.Lf, 5, "hybrid");
        new DatabaseInitializer(paths, LineEndingMigrations.For(Checkout.Crlf), _clock, NullAppLog.Instance).Initialize();
        BackupKit.ClearPools(paths.DatabaseFile);

        var sql = SqlOf(paths.DatabaseFile, "WorkspaceNodes");
        Assert.Contains("\r\n", sql);                                 // the V007 column spliced in by ALTER TABLE
        Assert.Contains("(\n", sql);                                  // the V003 text
        Assert.Equal(BackupStatus.FullyValidated, BackupKit.Validator().Check(Snapshot(paths.DatabaseFile), BackupCheckLevel.Full, Current).Status);
    }

    /// <summary>V008 hybrids: a v7 database from one checkout upgraded by a v8 build from the other splices V008's column into the V003 table text.</summary>
    [Theory]
    [InlineData(Checkout.Lf, Checkout.Crlf, "\r\n", "(\n")]
    [InlineData(Checkout.Crlf, Checkout.Lf, "ArchiveSerial INTEGER NOT NULL DEFAULT 0\n", "(\r\n")]
    public void AV7ToV8UpgradeAcrossCheckouts_MixesLineEndingsInTheProjectsTable_AndIsAcceptedEverywhere(Checkout producer, Checkout upgrader, string fromV008, string fromV003)
    {
        var paths = Produce(producer, 7, $"hybrid-{producer}-{upgrader}");
        var result = new DatabaseInitializer(paths, LineEndingMigrations.For(upgrader, 8), _clock, NullAppLog.Instance).Initialize();
        BackupKit.ClearPools(paths.DatabaseFile);
        Assert.Equal((DatabaseInitializationOutcome.Migrated, 7, 8), (result.Outcome, result.PreviousSchemaVersion, result.SchemaVersion));

        var sql = SqlOf(paths.DatabaseFile, "Projects");
        Assert.Contains(fromV008, sql);
        Assert.Contains(fromV003, sql);
        var copy = Snapshot(paths.DatabaseFile);
        foreach (var consumer in Enum.GetValues<Checkout>())
        {
            Assert.Equal(BackupStatus.FullyValidated, new BackupValidator(Reference(consumer), new BackupFileOps()).Check(copy, BackupCheckLevel.Full, 8).Status);
            Assert.Equal(new DataClassification(CurrentDataClass.Healthy, Version: 8), new CurrentDataClassifier(Reference(consumer)).ClassifyReadOnly(paths.DatabaseFile));
        }
    }

    /// <summary>
    /// V009 hybrids: a v8 database from one checkout upgraded by a v9 build from the other splices V009's three columns into the
    /// V003/V007 WorkspaceNodes table text; every checkout accepts the result.
    /// </summary>
    [Theory]
    [InlineData(Checkout.Lf, Checkout.Crlf, "LifecycleSerial INTEGER NOT NULL DEFAULT 0\r\n", "(\n")]
    [InlineData(Checkout.Crlf, Checkout.Lf, "LifecycleSerial INTEGER NOT NULL DEFAULT 0\n", "(\r\n")]
    public void AV8ToV9UpgradeAcrossCheckouts_MixesLineEndingsInTheWorkspaceNodesTable_AndIsAcceptedEverywhere(Checkout producer, Checkout upgrader, string fromV009, string fromV003)
    {
        var paths = Produce(producer, 8, $"hybrid9-{producer}-{upgrader}");
        var result = new DatabaseInitializer(paths, LineEndingMigrations.For(upgrader), _clock, NullAppLog.Instance).Initialize();
        BackupKit.ClearPools(paths.DatabaseFile);
        Assert.Equal((DatabaseInitializationOutcome.Migrated, 8, 9), (result.Outcome, result.PreviousSchemaVersion, result.SchemaVersion));

        var sql = SqlOf(paths.DatabaseFile, "WorkspaceNodes");
        Assert.Contains(fromV009, sql);
        Assert.Contains(fromV003, sql);
        var copy = Snapshot(paths.DatabaseFile);
        foreach (var consumer in Enum.GetValues<Checkout>())
        {
            Assert.Equal(BackupStatus.FullyValidated, new BackupValidator(Reference(consumer), new BackupFileOps()).Check(copy, BackupCheckLevel.Full, Current).Status);
            Assert.Equal(new DataClassification(CurrentDataClass.Healthy, Version: Current), new CurrentDataClassifier(Reference(consumer)).ClassifyReadOnly(paths.DatabaseFile));
        }
    }

    public static TheoryData<string, string> LineEndingOnlyRewrites => new()
    {
        { "every object to CRLF", "1 = 1" },
        { "one trigger", "name = 'TR_WorkspaceNodes_DeleteOnlyEmptyFolder'" },
        { "one partial index", "name = 'UX_WorkspaceNodes_LiveChildName'" },
        { "one table", "name = 'Systems'" },
        { "the V008 trigger", "name = 'TR_Projects_ArchiveSerialTransition'" },
        { "the V008 partial index", "name = 'IX_Projects_Archived'" },
        { "the altered Projects table", "name = 'Projects'" },
        { "a V009 trigger", "name = 'TR_WorkspaceNodes_LifecycleSerialTransition'" },
        { "a V009 partial index", "name = 'IX_WorkspaceNodes_FolderTrashed'" },
        { "the altered WorkspaceNodes table", "name = 'WorkspaceNodes'" },
    };

    /// <summary>LE-1: a same-name rewrite that changes only line terminators passes every reference path.</summary>
    [Theory]
    [MemberData(nameof(LineEndingOnlyRewrites))]
    public void LineEndingOnlyRewrite_PassesEveryReferencePath(string label, string where)
    {
        foreach (var newLine in new[] { "char(13) || char(10)", "char(10)" })
        {
            var root = new WorkHubPaths(Path.Combine(_dir, Guid.NewGuid().ToString("N")));
            new DatabaseInitializer(root, NullAppLog.Instance, InitialSystemsSeed.Apply).Initialize();
            BackupKit.ClearPools(root.DatabaseFile);
            Rewrite(root.DatabaseFile, $"UPDATE sqlite_schema SET sql = replace(replace(sql, char(13) || char(10), char(10)), char(10), {newLine}) WHERE sql IS NOT NULL AND {where};");

            var copy = Snapshot(root.DatabaseFile);
            Assert.True(BackupKit.Validator().Check(copy, BackupCheckLevel.Full, Current).Status == BackupStatus.FullyValidated, $"{label} ({newLine})");
            Assert.Equal(new DataClassification(CurrentDataClass.Healthy, Version: Current), new CurrentDataClassifier(SchemaReference.Frozen).ClassifyReadOnly(root.DatabaseFile));
            Assert.Equal(BackupCompatibility.Current, Consumer(Checkout.Mixed, root, SchemaReference.Frozen).Stage(copy, Current).Compatibility);
            var daily = new DailyBackupService(root, NullAppLog.Instance, _clock, BackupKit.Tokyo, SchemaReference.Frozen, new BackupFileOps()).Run();
            Assert.True(daily.Outcome == DailyBackupOutcome.Created, $"{label} ({newLine}): {daily}");
            BackupKit.ClearPoolsUnder(root.Root);
        }
    }

    public static TheoryData<string, string, string> ReferenceTampers => new()
    {
        { "bare CR in a trigger", "TR_WorkspaceNodes_DeleteOnlyEmptyFolder", "replace(sql, 'BEGIN', char(13) || 'BEGIN')" },
        { "CR CR LF at a line break", "TR_WorkspaceNodes_DeleteOnlyEmptyFolder", "replace(replace(sql, char(13) || char(10), char(10)), 'BEGIN' || char(10), 'BEGIN' || char(13) || char(13) || char(10))" },
        { "CRLF inside a CHECK literal", "SystemRepositories", "replace(sql, '''RELEASE''', '''REL' || char(13) || char(10) || 'EASE''')" },
        { "LF inside a CHECK literal", "SystemRepositories", "replace(sql, '''RELEASE''', '''REL' || char(10) || 'EASE''')" },
        { "tab instead of spaces", "Systems", "replace(sql, '    Id', char(9) || 'Id')" },
        { "trailing space before a line break", "Systems", "replace(replace(sql, char(13) || char(10), char(10)), 'Systems (' || char(10), 'Systems ( ' || char(10))" },
    };

    /// <summary>LE-11: line-terminator tolerance does not reach bare CRs, literals or other whitespace.</summary>
    [Theory]
    [MemberData(nameof(ReferenceTampers))]
    public void NonTerminatorChanges_AreStillRefusedByReferenceIdentity(string label, string name, string newSql)
    {
        var file = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");
        BackupKit.BuildSchema(file, 7);
        var before = SqlOf(file, name);
        Rewrite(file, $"UPDATE sqlite_schema SET sql = {newSql} WHERE name = '{name}';");
        Assert.NotEqual(before, SqlOf(file, name)); // the tamper applied

        var result = BackupKit.Validator().Check(file, BackupCheckLevel.Full, 7);
        Assert.True(result.Status == BackupStatus.SchemaMismatch, $"{label}: {result.Status}");
        Assert.Contains($"{name}: definition differs", result.Detail);
        Assert.Equal(DegradedKind.StructureDiffers, new CurrentDataClassifier(SchemaReference.Frozen).ClassifyReadOnly(file).Sub);
    }

    [Fact]
    public void ATextDefaultWithALineBreak_IsRefusedByReferenceIdentity_InEitherForm()
    {
        foreach (var text in new[] { "a\r\nb", "a\nb" })
        {
            var file = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");
            BackupKit.BuildSchema(file, 7);
            using (var connection = BackupKit.Open(file))
                BackupKit.Exec(connection, $"ALTER TABLE Systems ADD COLUMN Extra TEXT DEFAULT '{text}';");

            Assert.Equal(BackupStatus.SchemaMismatch, BackupKit.Validator().Check(file, BackupCheckLevel.Full, 7).Status);
        }
    }

    /// <summary>
    /// LE-3: a daily-backup candidate whose schema text differs from the source only by CRLF vs LF outside quoted
    /// tokens passes reference identity but fails exact fidelity: it is discarded, never published.
    /// </summary>
    [Fact]
    public void DailyBackup_CandidateDifferingOnlyByLineEndings_FailsExactFidelity()
    {
        using var root = new TempWorkHubRoot();
        BackupKit.CreateDatabase(root);
        var source = SqlOf(root.Paths.DatabaseFile, "Systems");

        var result = BackupKit.Service(root, _clock, hook: stage =>
        {
            if (stage != BackupStage.Normalized)
                return;
            var candidate = Directory.GetFiles(root.Paths.DailyBackupsDirectory, "*.partial").Single();
            Rewrite(candidate, "UPDATE sqlite_schema SET sql = replace(replace(sql, char(13) || char(10), char(10)), char(10), "
                + (source.Contains(Cr) ? "char(10)" : "char(13) || char(10)") + ") WHERE name = 'Systems';");
        }).Run();

        Assert.Equal(DailyBackupOutcome.Failed, result.Outcome);
        Assert.Equal(DailyBackupFailure.CandidateInvalid, result.Failure);
        Assert.Contains("NotEqualToSource", result.Detail);
        Assert.Contains("schema differs from the source", result.Detail);
        Assert.Empty(BackupKit.PublishedBackups(root));
    }

    public static TheoryData<string, string, string> NonReferenceLiteralChanges => new()
    {
        // The DEFAULT case is also visible to the structure layer (dflt_value); CHECK and trigger bodies are not.
        { "DEFAULT literal", "T", "replace(sql, 'a' || char(13) || char(10) || 'b', 'a' || char(10) || 'b')" },
        { "CHECK literal", "T", "replace(sql, 'x' || char(13) || char(10) || 'y', 'x' || char(10) || 'y')" },
        { "trigger body literal", "TR_T", "replace(sql, 'p' || char(13) || char(10) || 'q', 'p' || char(10) || 'q')" },
        { "line break outside quoted tokens", "T", "replace(sql, '(' || char(13) || char(10), '(' || char(10))" },
    };

    /// <summary>
    /// LE-4 / LE-5: preservation of a schema that matches no WorkHub reference (here a foreign v0 database) keeps exact
    /// fidelity. Each pair below has equal definitions under CRLF→LF, so only an exact comparison refuses them.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonReferenceLiteralChanges))]
    public void Preservation_OfANonReferenceSchema_RefusesALineEndingOnlyDifference(string label, string name, string newSql)
    {
        var source = ForeignWithMultiLineLiterals();
        var (candidate, facts) = PreservationCopy(source, 0);
        var before = Fingerprint(candidate);
        Rewrite(candidate, $"UPDATE sqlite_schema SET sql = {newSql} WHERE name = '{name}';");
        var after = Fingerprint(candidate);

        var changed = after.Definitions.Single(d => d.Name == name).Sql!;
        Assert.NotEqual(before.Definitions.Single(d => d.Name == name).Sql, changed);
        Assert.Equal(before.Definitions.Single(d => d.Name == name).Sql!.Replace("\r\n", "\n", StringComparison.Ordinal), changed.Replace("\r\n", "\n", StringComparison.Ordinal));
        if (label != "DEFAULT literal")
            Assert.Equal(before.Structure, after.Structure); // only the definition text can show it

        var result = BackupKit.Validator().CheckPreservation(candidate, facts);
        Assert.True(result.Status == BackupStatus.NotEqualToSource, $"{label}: {result.Status} {result.Detail}");
        Assert.Equal("schema differs from the source", result.Detail);
    }

    [Fact]
    public void Preservation_OfANewerSchema_RefusesACrlfRewriteOutsideQuotedTokens()
    {
        using var root = new TempWorkHubRoot();
        BackupKit.CreateDatabase(root);
        using (var connection = BackupKit.Open(root.Paths.DatabaseFile))
            BackupKit.Exec(connection, $"PRAGMA user_version = {Current + 1};");
        var (candidate, facts) = PreservationCopy(root.Paths.DatabaseFile, Current + 1);
        var source = SqlOf(candidate, "Systems");
        Rewrite(candidate, "UPDATE sqlite_schema SET sql = replace(replace(sql, char(13) || char(10), char(10)), char(10), "
            + (source.Contains(Cr) ? "char(10)" : "char(13) || char(10)") + ") WHERE name = 'Systems';");

        Assert.Equal(BackupStatus.NotEqualToSource, BackupKit.Validator().CheckPreservation(candidate, facts).Status);
    }

    /// <summary>LE-6: unchanged preservation copies of non-reference schemas, from LF- and CRLF-built sources, are preserved.</summary>
    [Theory]
    [InlineData("foreign v0 with multi-line literals", Checkout.Lf)]
    [InlineData("newer v10", Checkout.Lf)]
    [InlineData("newer v10", Checkout.Crlf)]
    [InlineData("extra table", Checkout.Crlf)]
    [InlineData("extra table", Checkout.Mixed)]
    public void Preservation_OfAnUnchangedNonReferenceCopy_Succeeds(string kind, Checkout built)
    {
        string source;
        int version;
        if (kind.StartsWith("foreign", StringComparison.Ordinal))
        {
            (source, version) = (ForeignWithMultiLineLiterals(), 0);
        }
        else
        {
            var paths = Produce(built, Current, Guid.NewGuid().ToString("N"), daily: false);
            source = paths.DatabaseFile;
            version = kind == "newer v10" ? Current + 1 : Current;
            using var connection = BackupKit.Open(source);
            BackupKit.Exec(connection, kind == "newer v10" ? $"PRAGMA user_version = {Current + 1};" : "CREATE TABLE Extra (Id INTEGER PRIMARY KEY, Note TEXT DEFAULT 'r\r\ns');");
        }

        var (candidate, facts) = PreservationCopy(source, version);
        Assert.Equal(BackupStatus.Preserved, BackupKit.Validator().CheckPreservation(candidate, facts).Status);
    }

    /// <summary>A foreign (v0) database whose DEFAULT, CHECK and trigger-body literals and table text contain CRLF.</summary>
    private string ForeignWithMultiLineLiterals()
    {
        var file = Path.Combine(_dir, Guid.NewGuid().ToString("N") + "-foreign.db");
        using var connection = BackupKit.Open(file, SqliteOpenMode.ReadWriteCreate);
        BackupKit.Exec(connection, "CREATE TABLE T (\r\n    s TEXT DEFAULT 'a\r\nb',\r\n    c TEXT CHECK (c <> 'x\r\ny')\r\n);");
        BackupKit.Exec(connection, "CREATE TRIGGER TR_T BEFORE DELETE ON T\r\nBEGIN\r\n    SELECT RAISE(ABORT, 'p\r\nq');\r\nEND;");
        BackupKit.Exec(connection, "INSERT INTO T (c) VALUES ('ok');");
        return file;
    }

    /// <summary>The preservation path's own steps: snapshot with Migration rules, then (by the caller) the check.</summary>
    private (string Candidate, SourceFacts Facts) PreservationCopy(string source, int version)
    {
        var candidate = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".partial");
        var facts = new SqliteSnapshotter(SchemaReference.Frozen, new BackupFileOps(), 10).Copy(source, candidate, new SnapshotRules(version, ValidationRules.Migration));
        BackupKit.ClearPools(source);
        return (candidate, facts);
    }

    private WorkHubPaths Produce(Checkout checkout, int version, string name, bool daily = true)
    {
        var paths = new WorkHubPaths(Path.Combine(_dir, name));
        var migrations = LineEndingMigrations.For(checkout, version);
        new DatabaseInitializer(paths, migrations, _clock, NullAppLog.Instance, InitialSystemsSeed.Apply).Initialize();
        BackupKit.ClearPools(paths.DatabaseFile);
        if (daily)
        {
            var made = new DailyBackupService(paths, NullAppLog.Instance, _clock, BackupKit.Tokyo, new SchemaReference(migrations), new BackupFileOps()).Run();
            Assert.True(made.Outcome == DailyBackupOutcome.Created, $"producer {checkout}: {made}");
        }

        return paths;
    }

    private static SchemaReference Reference(Checkout checkout) => new(LineEndingMigrations.For(checkout));

    private ConsumerBuild Consumer(Checkout checkout, WorkHubPaths paths, SchemaReference? references = null)
    {
        var refs = references ?? Reference(checkout);
        return new ConsumerBuild(
            new BackupValidator(refs, new BackupFileOps()),
            new BackupCatalog(paths, BackupKit.Tokyo, refs, new BackupFileOps(), new ProcessStarter()),
            new CandidateStager(new RestoreFileOps(paths, () => false), new BackupValidator(refs, new BackupFileOps()), refs.LatestVersion),
            new CurrentDataClassifier(refs),
            new DatabaseInitializer(paths, LineEndingMigrations.For(checkout), _clock, NullAppLog.Instance),
            new DailyBackupService(paths, NullAppLog.Instance, _clock, BackupKit.Tokyo, refs, new BackupFileOps()),
            Path.Combine(_dir, "stage"));
    }

    private sealed record ConsumerBuild(
        BackupValidator Validator,
        BackupCatalog Catalog,
        CandidateStager Stager,
        CurrentDataClassifier Classifier,
        DatabaseInitializer Initializer,
        DailyBackupService Daily,
        string StageDirectory)
    {
        /// <summary>Restore candidate validation only: a private copy is checked; nothing is published.</summary>
        public CandidateFacts Stage(string backup, int nameVersion)
        {
            Directory.CreateDirectory(StageDirectory);
            return Stager.Stage(backup, nameVersion, Path.Combine(StageDirectory, Guid.NewGuid().ToString("N") + ".db")).Candidate;
        }
    }

    private string Snapshot(string file)
    {
        var copy = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");
        BackupKit.SnapshotTo(file, copy);
        return copy;
    }

    private static void Rewrite(string file, string update)
    {
        using var connection = BackupKit.Open(file);
        BackupKit.Exec(connection, "PRAGMA writable_schema = ON;");
        BackupKit.Exec(connection, update);
        BackupKit.Exec(connection, "PRAGMA writable_schema = OFF;");
    }

    private static string SqlOf(string file, string name)
    {
        using var connection = BackupKit.Open(file, SqliteOpenMode.ReadOnly);
        return (string)BackupKit.Scalar(connection, $"SELECT sql FROM sqlite_schema WHERE name = '{name}';")!;
    }

    private static SchemaFingerprint Fingerprint(string file)
    {
        using var connection = BackupKit.Open(file, SqliteOpenMode.ReadOnly);
        return SchemaFingerprint.Read(connection);
    }
}
