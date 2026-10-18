using System.Text;
using Microsoft.Data.Sqlite;
using WorkHub.Core.Backup;
using WorkHub.Infrastructure.Backup;
using WorkHub.Infrastructure.Persistence.Migrations;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Backup;

/// <summary>Pre-open gates, validation levels and classification (Phase 9 §4.3, §4.6; tests C22, C23, C31, C34).</summary>
public sealed class BackupValidatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "WorkHubTests", Guid.NewGuid().ToString("N"));
    private readonly BackupValidator _validator = BackupKit.Validator();

    public BackupValidatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        BackupKit.ClearPoolsUnder(_dir);
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void StandaloneV7_IsQuickCheckedThenFullyValidated_AndCurrent()
    {
        var file = V7();

        var quick = _validator.Check(file, BackupCheckLevel.Quick);
        var full = _validator.Check(file, BackupCheckLevel.Full);

        Assert.Equal(BackupStatus.QuickChecked, quick.Status);
        Assert.Equal(BackupStatus.FullyValidated, full.Status);
        Assert.Equal(7, full.SchemaVersion);
        Assert.Equal(BackupCompatibility.Current, full.CompatibilityWith(7));
    }

    [Theory]
    [InlineData("-journal")]
    [InlineData("-wal")]
    [InlineData("-shm")]
    public void FileWithASidecar_IsNotStandalone_AndIsNeverOpened(string suffix)
    {
        var file = V7();
        File.WriteAllBytes(file + suffix, [1, 2, 3]);
        var before = Fingerprint(file);

        var result = _validator.Check(file, BackupCheckLevel.Full);

        Assert.Equal(BackupStatus.NotStandalone, result.Status);
        Assert.Equal(before, Fingerprint(file));
        Assert.Equal([suffix], BackupKit.Sidecars(file)); // nothing new appeared
        Assert.Equal(3, new FileInfo(file + suffix).Length);
    }

    [Fact]
    public void WalHeaderFile_IsRefusedBeforeOpening_SoNoWalOrShmIsCreated()
    {
        var file = WalHeaderCopy();
        Assert.Equal(((byte)2, (byte)2), BackupKit.Header(file));
        Assert.Empty(BackupKit.Sidecars(file));
        var before = Fingerprint(file);

        foreach (var level in new[] { BackupCheckLevel.Quick, BackupCheckLevel.Full })
        {
            var result = _validator.Check(file, level);
            Assert.Equal(BackupStatus.NotStandalone, result.Status);
            Assert.Contains("WAL", result.Detail);
        }

        Assert.Empty(BackupKit.Sidecars(file));
        Assert.Equal(before, Fingerprint(file));
    }

    /// <summary>Proves the gate is what prevents the side effect: opening the same file ReadOnly does create sidecars.</summary>
    [Fact]
    public void Control_OpeningAWalHeaderFileReadOnly_LeavesSidecarsBehind()
    {
        var file = WalHeaderCopy();
        using (var connection = BackupKit.Open(file, SqliteOpenMode.ReadOnly))
            BackupKit.Scalar(connection, "SELECT count(*) FROM sqlite_schema;");
        BackupKit.ClearPools(file);

        Assert.NotEmpty(BackupKit.Sidecars(file));
    }

    [Fact]
    public void ValidatingAStandaloneFile_HasNoSideEffects()
    {
        var file = V7();
        var before = Fingerprint(file);

        _validator.Check(file, BackupCheckLevel.Quick);
        _validator.Check(file, BackupCheckLevel.Full);

        Assert.Equal(before, Fingerprint(file));
        Assert.Empty(BackupKit.Sidecars(file));
        File.Delete(file); // no handle left open (unpooled)
    }

    [Fact]
    public void MissingFile_IsUnreadable_AndNotCreated()
    {
        var file = Path.Combine(_dir, "missing.db");
        Assert.Equal(BackupStatus.Unreadable, _validator.Check(file, BackupCheckLevel.Full).Status);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Classification()
    {
        var text = Path.Combine(_dir, "text.db");
        File.WriteAllText(text, new string('x', 4096));
        Assert.Equal(BackupStatus.Unreadable, _validator.Check(text, BackupCheckLevel.Full).Status);

        var empty = Path.Combine(_dir, "empty.db");
        File.WriteAllBytes(empty, []);
        Assert.Equal(BackupStatus.Unreadable, _validator.Check(empty, BackupCheckLevel.Full).Status);

        var foreignV0 = Path.Combine(_dir, "foreign0.db");
        using (var c = BackupKit.Open(foreignV0, SqliteOpenMode.ReadWriteCreate))
            BackupKit.Exec(c, "CREATE TABLE T(x);");
        Assert.Equal(BackupStatus.Foreign, _validator.Check(foreignV0, BackupCheckLevel.Full).Status);

        var foreignV7 = Path.Combine(_dir, "foreign7.db");
        using (var c = BackupKit.Open(foreignV7, SqliteOpenMode.ReadWriteCreate))
            BackupKit.Exec(c, "CREATE TABLE T(x); PRAGMA user_version = 7;");
        var foreign7 = _validator.Check(foreignV7, BackupCheckLevel.Quick);
        Assert.Equal(BackupStatus.SchemaMismatch, foreign7.Status);
        Assert.Equal(BackupCompatibility.NotUsable, foreign7.CompatibilityWith(7));

        var newer = V7();
        using (var c = BackupKit.Open(newer))
            BackupKit.Exec(c, $"CREATE TABLE Future(x); PRAGMA user_version = {SchemaMigrations.LatestVersion + 1};");
        var newerResult = _validator.Check(newer, BackupCheckLevel.Full);
        Assert.Equal(BackupStatus.NewerSchema, newerResult.Status);
        Assert.Equal(BackupCompatibility.Newer, newerResult.CompatibilityWith(7));

        var older = Path.Combine(_dir, "v5.db");
        BackupKit.BuildSchema(older, 5);
        var olderResult = _validator.Check(older, BackupCheckLevel.Full, nameVersion: 5);
        Assert.Equal(BackupStatus.FullyValidated, olderResult.Status);
        Assert.Equal(BackupCompatibility.Older, olderResult.CompatibilityWith(7));

        Assert.Equal(BackupStatus.NameVersionMismatch, _validator.Check(older, BackupCheckLevel.Quick, nameVersion: 7).Status);

        var orphan = V7();
        using (var c = BackupKit.Open(orphan))
        {
            BackupKit.Exec(c, "PRAGMA foreign_keys = OFF;");
            BackupKit.Exec(c, "INSERT INTO SystemRepositories (SystemId, RepositoryType) VALUES (999, 'SVN_SYSTEM');");
        }

        Assert.Equal(BackupStatus.QuickChecked, _validator.Check(orphan, BackupCheckLevel.Quick).Status);
        Assert.Equal(BackupStatus.ForeignKeyViolations, _validator.Check(orphan, BackupCheckLevel.Full).Status);
    }

    [Fact]
    public void TruncatedAndByteFlippedFiles_AreDamaged()
    {
        var truncated = V7(rows: 300);
        var bytes = File.ReadAllBytes(truncated);
        File.WriteAllBytes(truncated, bytes[..(bytes.Length / 2 / 4096 * 4096)]);
        Assert.Equal(BackupStatus.IntegrityFailed, _validator.Check(truncated, BackupCheckLevel.Quick).Status);

        var flipped = V7(rows: 300);
        BackupKit.CorruptPage(flipped, page: 3);
        var damaged = _validator.Check(flipped, BackupCheckLevel.Quick);
        Assert.Equal(BackupStatus.IntegrityFailed, damaged.Status);
        Assert.DoesNotContain("***", damaged.Detail); // the problem line, not SQLite's banner
        Assert.Contains("page", damaged.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>quick_check skips index-to-table consistency; only the full level may count as retention evidence.</summary>
    [Fact]
    public void IndexContentDamage_PassesQuickButFailsFull()
    {
        var file = WithDamagedIndex(_dir);

        Assert.Equal(BackupStatus.QuickChecked, _validator.Check(file, BackupCheckLevel.Quick).Status);
        var full = _validator.Check(file, BackupCheckLevel.Full);
        Assert.Equal(BackupStatus.IntegrityFailed, full.Status);
        Assert.False(full.IsFullyValidated);
    }

    [Fact]
    public void FakeReparsePoint_IsNotOpened()
    {
        var file = V7();
        var files = new FaultyBackupFileOps();
        files.FakeReparsePoints.Add(file);

        var result = new BackupValidator(SchemaReference.Frozen, files).Check(file, BackupCheckLevel.Full);

        Assert.Equal(BackupStatus.Unreadable, result.Status);
        Assert.Equal("not a regular file", result.Detail);
    }

    /// <summary>
    /// A current-schema database whose IX_SystemRepositories_RepositoryType entry no longer matches its table row (the text inside the
    /// index page is changed in place). Structure and order stay valid, so quick_check passes and integrity_check fails.
    /// </summary>
    internal static string WithDamagedIndex(string directory, string? name = null)
    {
        var file = Path.Combine(directory, name ?? Guid.NewGuid().ToString("N") + ".db");
        BackupKit.BuildSchema(file, SchemaMigrations.LatestVersion);
        long rootPage;
        using (var c = BackupKit.Open(file))
        {
            BackupKit.Exec(c, $"INSERT INTO Systems (SystemCode, SystemName, CreatedAt, UpdatedAt) VALUES ('IDX001', 'Index', '{Db.Now}', '{Db.Now}');");
            BackupKit.Exec(c, "INSERT INTO SystemRepositories (SystemId, RepositoryType) VALUES (1, 'GIT_ORACLE');");
            rootPage = (long)BackupKit.Scalar(c, "SELECT rootpage FROM sqlite_schema WHERE name = 'IX_SystemRepositories_RepositoryType';")!;
        }

        BackupKit.ClearPools(file);
        var bytes = File.ReadAllBytes(file);
        var pageSize = (bytes[16] << 8) | bytes[17];
        var start = (int)(rootPage - 1) * pageSize;
        var needle = Encoding.ASCII.GetBytes("GIT_ORACLE");
        var at = bytes.AsSpan(start, pageSize).IndexOf(needle);
        Assert.True(at >= 0, "index entry not found in its root page");
        bytes[start + at + needle.Length - 1] = (byte)'F'; // GIT_ORACLF: same length, still sorts the same
        File.WriteAllBytes(file, bytes);
        return file;
    }

    private string V7(int rows = 0)
    {
        var file = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");
        BackupKit.BuildSchema(file, 7);
        if (rows > 0)
        {
            using var c = BackupKit.Open(file);
            using var t = c.BeginTransaction();
            for (var i = 0; i < rows; i++)
                BackupKit.Exec(c, $"INSERT INTO Systems (SystemCode, SystemName, Description, CreatedAt, UpdatedAt) VALUES ('S{i:D5}', 'System {i}', '{new string('d', 200)}', '{Db.Now}', '{Db.Now}');", t);
            t.Commit();
        }

        return file;
    }

    private string WalHeaderCopy()
    {
        var source = Path.Combine(_dir, Guid.NewGuid().ToString("N") + "-src.db");
        BackupKit.BuildSchema(source, 7);
        var target = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");
        using (var s = BackupKit.Open(source))
        {
            BackupKit.Scalar(s, "PRAGMA journal_mode = WAL;");
            using var t = BackupKit.Open(target, SqliteOpenMode.ReadWriteCreate);
            s.BackupDatabase(t); // a plain copy of a WAL source is a WAL-mode file
        }

        BackupKit.ClearPools(source);
        BackupKit.ClearPools(target);
        return target;
    }

    private static string Fingerprint(string file) => $"{BackupKit.Hash(file)}|{File.GetLastWriteTimeUtc(file):O}";
}
