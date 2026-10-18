using System.Text.Json;
using Microsoft.Data.Sqlite;
using WorkHub.Core.Backup;
using WorkHub.Infrastructure.Backup;
using WorkHub.Infrastructure.Persistence.Migrations;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Backup;

/// <summary>
/// Two-layer schema identity against the frozen migrations (Phase 9 §4.5; tests C24–C30, C32). Tampered schemas keep
/// every object name and pass quick_check, integrity_check, foreign_key_check and user_version — only the fingerprint
/// sees them.
/// </summary>
public sealed class SchemaFingerprintTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "WorkHubTests", Guid.NewGuid().ToString("N"));

    public SchemaFingerprintTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        BackupKit.ClearPoolsUnder(_dir);
        Directory.Delete(_dir, recursive: true);
    }

    private static readonly string GoldenPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SchemaFingerprints.json");

    [Fact]
    public void FreshAndUpgradedDatabases_MatchTheReference_ForEveryVersionAndRestartPattern()
    {
        var checkedCount = 0;
        for (var version = 1; version <= SchemaMigrations.LatestVersion; version++)
        {
            var expected = SchemaReference.Frozen.For(version).ExactHash();
            for (var mask = 0; mask < 1 << (version - 1); mask++)
            {
                var file = Path.Combine(_dir, $"v{version}-{mask}.db");
                var stops = Enumerable.Range(1, version - 1).Where(i => (mask & (1 << (i - 1))) != 0).ToHashSet();
                BuildWithRestarts(file, version, stops);
                using (var connection = BackupKit.Open(file, SqliteOpenMode.ReadOnly))
                {
                    var actual = SchemaFingerprint.Read(connection);
                    Assert.True(SchemaReference.Frozen.DifferencesFrom(version, actual).Count == 0, $"v{version} restart pattern {mask} differs from the reference");
                    Assert.True(expected == actual.ExactHash(), $"v{version} restart pattern {mask}: same build, different bytes");
                }

                checkedCount++;
                File.Delete(file);
            }
        }

        Assert.Equal(127, checkedCount);
    }

    [Fact]
    public void PopulatedUpgradeThroughTheRealInitializer_MatchesTheReference()
    {
        using var root = new TempWorkHubRoot();
        Directory.CreateDirectory(root.Paths.DataDirectory);
        BackupKit.BuildSchema(root.Paths.DatabaseFile, 5);
        using (var connection = BackupKit.Open(root.Paths.DatabaseFile))
        {
            BackupKit.Exec(connection, $"INSERT INTO Systems (SystemCode, SystemName, CreatedAt, UpdatedAt) VALUES ('UP001', 'Upgrade', '{Db.Now}', '{Db.Now}');");
            BackupKit.Exec(connection, "INSERT INTO SystemRepositories (SystemId, RepositoryType) VALUES (1, 'SVN_SYSTEM');");
        }

        root.CreateDatabaseInitializer([.. SchemaMigrations.All]).Initialize();

        using var upgraded = BackupKit.Open(root.Paths.DatabaseFile, SqliteOpenMode.ReadOnly);
        Assert.Empty(SchemaReference.Frozen.DifferencesFrom(7, SchemaFingerprint.Read(upgraded)));
    }

    [Fact]
    public void InMemoryReference_EqualsAFileBuiltThroughTheSameMigrations()
    {
        var file = Path.Combine(_dir, "file.db");
        BackupKit.BuildSchema(file, SchemaMigrations.LatestVersion);
        using var connection = BackupKit.Open(file, SqliteOpenMode.ReadOnly);
        Assert.Equal(SchemaReference.Frozen.For(SchemaMigrations.LatestVersion).ExactHash(), SchemaFingerprint.Read(connection).ExactHash());
    }

    /// <summary>
    /// Guards package/runtime upgrades: if SQLite ever stores the migrations' schema text differently, existing user
    /// databases would no longer match. Update the fixture only together with an explicit compatibility decision.
    /// Pins the CANONICAL reference contract (LE-D2): the same values for LF, CRLF and mixed checkouts. Exact hashes are
    /// checkout-dependent by nature and are covered by <see cref="CanonicalHashesAgreeAcrossCheckouts_ExactHashesDoNot"/>.
    /// </summary>
    [Fact]
    public void GoldenFingerprintHashes_EqualTheRuntimeBuiltReferences()
    {
        var golden = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(GoldenPath))!;
        var actual = Enumerable.Range(1, SchemaMigrations.LatestVersion).ToDictionary(v => v.ToString(), v => SchemaReference.Frozen.CanonicalHash(v));
        Assert.True(golden.Count == actual.Count && actual.All(kv => golden.TryGetValue(kv.Key, out var g) && g == kv.Value),
            "Runtime references differ from the golden hashes: " + JsonSerializer.Serialize(actual));
    }

    /// <summary>LE-7: the same frozen SQL compiled from LF, CRLF and mixed checkouts.</summary>
    [Fact]
    public void CanonicalHashesAgreeAcrossCheckouts_ExactHashesDoNot()
    {
        var lf = new SchemaReference(LineEndingMigrations.For(Checkout.Lf));
        var crlf = new SchemaReference(LineEndingMigrations.For(Checkout.Crlf));
        var mixed = new SchemaReference(LineEndingMigrations.For(Checkout.Mixed));

        for (var v = 1; v <= SchemaMigrations.LatestVersion; v++)
        {
            Assert.Equal(lf.CanonicalHash(v), crlf.CanonicalHash(v));
            Assert.Equal(lf.CanonicalHash(v), mixed.CanonicalHash(v));
            Assert.Equal(lf.CanonicalHash(v), SchemaReference.Frozen.CanonicalHash(v));
            Assert.NotEqual(lf.For(v).ExactHash(), crlf.For(v).ExactHash());
            Assert.Empty(crlf.DifferencesFrom(v, lf.For(v)));
            Assert.Empty(lf.DifferencesFrom(v, mixed.For(v)));
        }

        // V001 is LF in the mixed checkout, so v1 alone is byte-identical between LF and mixed; from v2 on they differ.
        Assert.Equal(lf.For(1).ExactHash(), mixed.For(1).ExactHash());
        Assert.NotEqual(lf.For(2).ExactHash(), mixed.For(2).ExactHash());
        Assert.NotEqual(crlf.For(1).ExactHash(), mixed.For(1).ExactHash());
    }

    public static TheoryData<string, string, bool, string> Tampers => new()
    {
        { "no-op guard trigger, same name",
          "DROP TRIGGER TR_WorkspaceNodes_DeleteOnlyEmptyFolder; CREATE TRIGGER TR_WorkspaceNodes_DeleteOnlyEmptyFolder BEFORE DELETE ON WorkspaceNodes BEGIN SELECT 1; END;",
          false, "trigger TR_WorkspaceNodes_DeleteOnlyEmptyFolder: definition differs" },
        { "partial index predicate changed, same name",
          "DROP INDEX UX_WorkspaceNodes_ActiveChildName; CREATE UNIQUE INDEX UX_WorkspaceNodes_ActiveChildName ON WorkspaceNodes (ParentId, NameKey) WHERE ParentId IS NOT NULL;",
          false, "index UX_WorkspaceNodes_ActiveChildName: definition differs" },
        { "unique index made non-unique, same name and predicate",
          "DROP INDEX UX_WorkspaceNodes_ActiveChildName; CREATE INDEX UX_WorkspaceNodes_ActiveChildName ON WorkspaceNodes (ParentId, NameKey) WHERE ParentId IS NOT NULL AND TrashedAt IS NULL;",
          false, "UX_WorkspaceNodes_ActiveChildName: structure differs" },
        { "index key columns changed, same name",
          "DROP INDEX IX_SystemRepositories_RepositoryType; CREATE INDEX IX_SystemRepositories_RepositoryType ON SystemRepositories (RepositoryType);",
          false, "IX_SystemRepositories_RepositoryType: structure differs" },
        { "CHECK removed via writable_schema",
          "UPDATE sqlite_schema SET sql = replace(sql, ' CHECK (IsActive IN (0, 1))', '') WHERE name = 'Systems';",
          true, "table Systems: definition differs" },
        { "FK action changed via writable_schema",
          "UPDATE sqlite_schema SET sql = replace(sql, 'REFERENCES Systems (Id) ON DELETE CASCADE', 'REFERENCES Systems (Id) ON DELETE SET NULL') WHERE name = 'SystemRepositories';",
          true, "SystemRepositories: structure differs" },
        { "table rebuilt (12-step) without a CHECK, same names",
          """
          PRAGMA foreign_keys = OFF;
          CREATE TABLE SystemRepositories_new (
              SystemId INTEGER NOT NULL REFERENCES Systems (Id) ON DELETE CASCADE,
              RepositoryType TEXT NOT NULL CHECK (RepositoryType IN ('SVN_SYSTEM', 'SVN_ORACLE', 'GIT_SYSTEM', 'GIT_ORACLE', 'RELEASE')),
              IsEnabled INTEGER NOT NULL DEFAULT 1 CHECK (IsEnabled IN (0, 1)),
              CustomTarget TEXT NULL,
              PRIMARY KEY (SystemId, RepositoryType)) STRICT, WITHOUT ROWID;
          INSERT INTO SystemRepositories_new SELECT * FROM SystemRepositories;
          DROP TABLE SystemRepositories;
          ALTER TABLE SystemRepositories_new RENAME TO SystemRepositories;
          CREATE INDEX IX_SystemRepositories_RepositoryType ON SystemRepositories (RepositoryType, SystemId);
          """,
          false, "table SystemRepositories: definition differs" },
        { "extra index",
          "CREATE INDEX IX_Extra ON Systems (SystemName);",
          false, "extra index IX_Extra" },
        { "missing trigger",
          "DROP TRIGGER TR_Projects_NoDelete;",
          false, "missing trigger TR_Projects_NoDelete" },
    };

    [Theory]
    [MemberData(nameof(Tampers))]
    public void SameNameTampers_AreRejectedAlthoughEveryOtherCheckPasses(string label, string sql, bool writableSchema, string expectedDifference)
    {
        var file = TamperedCopy(sql, writableSchema);

        using (var connection = BackupKit.Open(file, SqliteOpenMode.ReadOnly))
        {
            // Every other check passes: this is exactly what the fingerprint must catch.
            Assert.Equal("ok", BackupKit.Scalar(connection, "PRAGMA quick_check;"));
            Assert.Equal("ok", BackupKit.Scalar(connection, "PRAGMA integrity_check;"));
            Assert.Equal(0L, BackupKit.Scalar(connection, "SELECT count(*) FROM pragma_foreign_key_check;"));
            Assert.Equal(7L, BackupKit.Scalar(connection, "PRAGMA user_version;"));
            Assert.Contains(expectedDifference, SchemaReference.Frozen.DifferencesFrom(7, SchemaFingerprint.Read(connection)));
        }

        var result = BackupKit.Validator().Check(file, BackupCheckLevel.Full);
        Assert.True(result.Status == BackupStatus.SchemaMismatch, $"{label}: {result.Status}");
        Assert.Contains(expectedDifference, result.Detail);
        Assert.DoesNotContain("CREATE", result.Detail, StringComparison.OrdinalIgnoreCase); // names only, never SQL text
    }

    [Fact]
    public void WhitespaceOnlyTriggerChange_IsAMismatch_NoNormalization()
    {
        string original;
        var reference = Path.Combine(_dir, "ref.db");
        BackupKit.BuildSchema(reference, 7);
        using (var connection = BackupKit.Open(reference, SqliteOpenMode.ReadOnly))
            original = (string)BackupKit.Scalar(connection, "SELECT sql FROM sqlite_schema WHERE name = 'TR_WorkspaceNodes_DeleteOnlyEmptyFolder';")!;

        var file = TamperedCopy("DROP TRIGGER TR_WorkspaceNodes_DeleteOnlyEmptyFolder; " + original.Replace("BEGIN", "BEGIN  ", StringComparison.Ordinal) + ";", false);

        Assert.Equal(BackupStatus.SchemaMismatch, BackupKit.Validator().Check(file, BackupCheckLevel.Quick).Status);
    }

    [Fact]
    public void Diagnostics_AreCappedAndNameOnly()
    {
        var file = TamperedCopy(string.Join(' ', Enumerable.Range(1, 15).Select(i => $"CREATE INDEX IX_Extra{i} ON Systems (SystemName);")), false);
        using var connection = BackupKit.Open(file, SqliteOpenMode.ReadOnly);

        var differences = SchemaReference.Frozen.DifferencesFrom(7, SchemaFingerprint.Read(connection));

        Assert.Equal(11, differences.Count);
        Assert.StartsWith("…", differences[^1]);
    }

    private string TamperedCopy(string sql, bool writableSchema)
    {
        var file = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");
        BackupKit.BuildSchema(file, 7);
        using (var connection = BackupKit.Open(file))
        {
            if (writableSchema)
                BackupKit.Exec(connection, "PRAGMA writable_schema = ON;");
            BackupKit.Exec(connection, sql);
            if (writableSchema)
                BackupKit.Exec(connection, "PRAGMA writable_schema = OFF;");
        }

        return file;
    }

    private static void BuildWithRestarts(string file, int toVersion, HashSet<int> stops)
    {
        var connection = BackupKit.Open(file, SqliteOpenMode.ReadWriteCreate);
        try
        {
            foreach (var migration in SchemaMigrations.All.Where(m => m.Version <= toVersion))
            {
                using (var transaction = connection.BeginTransaction())
                {
                    migration.Apply(connection, transaction);
                    BackupKit.Exec(connection, $"PRAGMA user_version = {migration.Version};", transaction);
                    transaction.Commit();
                }

                if (stops.Contains(migration.Version))
                {
                    connection.Dispose();
                    connection = BackupKit.Open(file);
                }
            }
        }
        finally
        {
            connection.Dispose();
        }
    }
}
