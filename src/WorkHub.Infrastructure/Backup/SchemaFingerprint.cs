using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Infrastructure.Persistence.Migrations;

namespace WorkHub.Infrastructure.Backup;

/// <summary>One <c>sqlite_schema</c> row (excluding internal <c>sqlite_%</c> objects).</summary>
internal sealed record SchemaObject(string Type, string Name, string TableName, string? Sql);

/// <summary>One PRAGMA-derived structure line, owned by a table or index (for per-object diagnostics).</summary>
internal sealed record StructureLine(string Owner, string Text);

/// <summary>
/// Two-layer schema fingerprint (Phase 9 §4.5), read raw: nothing is normalized when it is read or stored.
/// <list type="number">
/// <item><b>Definitions</b> (primary): <c>(type, name, tbl_name, sql)</c>. This covers columns, types, defaults, NOT NULL,
/// COLLATE, CHECK, keys, FOREIGN KEY actions, STRICT/WITHOUT ROWID, index columns, uniqueness and partial predicates, and
/// trigger timing, events, WHEN clauses and bodies.</item>
/// <item><b>Structure</b> (defense in depth, diagnostics): <c>pragma_table_list</c>, <c>table_xinfo</c>,
/// <c>foreign_key_list</c>, <c>index_list</c>/<c>index_xinfo</c> (including internal autoindexes, which have no SQL) and
/// whether <c>sqlite_sequence</c> exists. It cannot see CHECK expressions, partial predicates or trigger bodies, which is
/// why the definition layer is primary.</item>
/// </list>
/// Two comparison contracts use it, and each is named at its call site:
/// <list type="bullet">
/// <item><b>Exact source-to-copy fidelity</b>: <see cref="ExactHash"/> — both layers byte-exact, no normalization of any
/// kind. The source can be any database (also foreign, newer or structurally different ones being preserved).</item>
/// <item><b>Reference identity</b>: <see cref="SchemaReference.DifferencesFrom"/> — against the frozen migrations only; see
/// there for its single, line-terminator-only tolerance.</item>
/// </list>
/// Equality claims only that the schema text and structure are identical — never semantic equivalence. Nothing is ever
/// repaired.
/// </summary>
internal sealed class SchemaFingerprint
{
    private SchemaFingerprint(IReadOnlyList<SchemaObject> definitions, IReadOnlyList<StructureLine> structure)
    {
        Definitions = definitions;
        Structure = structure;
    }

    public IReadOnlyList<SchemaObject> Definitions { get; }

    public IReadOnlyList<StructureLine> Structure { get; }

    /// <param name="transaction">The connection's active transaction, if any (all reads then belong to that snapshot).</param>
    public static SchemaFingerprint Read(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        var definitions = new List<SchemaObject>();
        Query(connection, transaction,
            "SELECT type, name, tbl_name, sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name;",
            null,
            r => definitions.Add(new SchemaObject(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3))));
        definitions.Sort((a, b) =>
        {
            var byType = string.CompareOrdinal(a.Type, b.Type);
            return byType != 0 ? byType : string.CompareOrdinal(a.Name, b.Name);
        });

        var structure = new List<StructureLine>();
        Query(connection, transaction,
            "SELECT name, type, ncol, wr, strict FROM pragma_table_list WHERE schema = 'main' AND name NOT LIKE 'sqlite_%' ORDER BY name;",
            null,
            r => structure.Add(new StructureLine(r.GetString(0), "table|" + Join(r))));
        structure.Add(new StructureLine("sqlite_sequence", "sqlite_sequence|" +
            Scalar(connection, transaction, "SELECT count(*) FROM sqlite_schema WHERE name = 'sqlite_sequence';")));

        foreach (var table in definitions.Where(d => d.Type == "table").Select(d => d.Name).Order(StringComparer.Ordinal))
        {
            Query(connection, transaction,
                "SELECT cid, name, type, \"notnull\", dflt_value, pk, hidden FROM pragma_table_xinfo($t) ORDER BY cid;",
                table, r => structure.Add(new StructureLine(table, "column|" + Join(r))));
            Query(connection, transaction,
                "SELECT id, seq, \"table\", \"from\", \"to\", on_update, on_delete, \"match\" FROM pragma_foreign_key_list($t) ORDER BY id, seq;",
                table, r => structure.Add(new StructureLine(table, "fk|" + Join(r))));

            var indexes = new List<string>();
            Query(connection, transaction,
                "SELECT name, \"unique\", origin, partial FROM pragma_index_list($t) ORDER BY name;",
                table, r =>
                {
                    indexes.Add(r.GetString(0));
                    structure.Add(new StructureLine(r.GetString(0), $"index|{table}|" + Join(r)));
                });
            foreach (var index in indexes)
            {
                Query(connection, transaction,
                    "SELECT seqno, cid, name, \"desc\", coll, \"key\" FROM pragma_index_xinfo($t) ORDER BY seqno;",
                    index, r => structure.Add(new StructureLine(index, "indexcolumn|" + Join(r))));
            }
        }

        return new SchemaFingerprint(definitions, structure);
    }

    /// <summary>
    /// Exact source-to-copy fidelity: SHA-256 (hex) of both layers exactly as read, with no normalization of any kind
    /// (line terminators included). Stable across processes and machines.
    /// </summary>
    public string ExactHash() => HashOf(Definitions, Structure);

    /// <summary>The serialization behind <see cref="ExactHash"/> and <see cref="SchemaReference.CanonicalHash"/>.</summary>
    internal static string HashOf(IEnumerable<SchemaObject> definitions, IEnumerable<StructureLine> structure)
    {
        var text = new StringBuilder();
        foreach (var d in definitions)
            text.Append(d.Type).Append('\u001f').Append(d.Name).Append('\u001f').Append(d.TableName).Append('\u001f').Append(d.Sql ?? "\0").Append('\u001e');
        text.Append('\u001d');
        foreach (var s in structure)
            text.Append(s.Owner).Append('\u001f').Append(s.Text).Append('\u001e');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static string Join(SqliteDataReader reader) =>
        string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(i =>
            reader.IsDBNull(i) ? "<null>" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)));

    private static string Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "<null>";
    }

    private static void Query(SqliteConnection connection, SqliteTransaction? transaction, string sql, string? argument, Action<SqliteDataReader> row)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (argument is not null)
            command.Parameters.AddWithValue("$t", argument);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            row(reader);
    }
}

/// <summary>
/// Reference fingerprints per schema version, built by applying the frozen migrations 1..n (each in its own transaction,
/// as <see cref="DatabaseInitializer"/> does) to a private in-memory database. Built once per version and cached; nothing
/// is stored or embedded, so V001–V007 stay the single source.
/// <para>
/// <b>Reference identity</b> (<see cref="DifferencesFrom"/>, <see cref="CanonicalHash"/>) has exactly one tolerance: in
/// definitions, CRLF equals LF (<c>Replace("\r\n", "\n")</c>). A build compiles the frozen SQL with its checkout's line
/// terminators and SQLite stores them verbatim, so LF, CRLF, mixed and cross-build hybrid databases must all match. This
/// is sound only against the frozen references, which have no bare CR and no line break inside a quoted string or
/// identifier (enforced by <c>FrozenSqlLineEndingGuardTests</c>): equal canonical text then means the same SQLite token
/// stream. Structure, every other whitespace, case, comments and quoting stay exact. Source-to-copy fidelity never uses
/// this tolerance (<see cref="SchemaFingerprint.ExactHash"/>).
/// </para>
/// </summary>
internal sealed class SchemaReference
{
    private const int MaxReportedDifferences = 10;

    private readonly IReadOnlyList<ISchemaMigration> _migrations;
    private readonly Dictionary<int, SchemaFingerprint> _cache = [];
    private readonly Lock _gate = new();

    public SchemaReference(IReadOnlyList<ISchemaMigration> migrations)
    {
        _migrations = migrations;
    }

    /// <summary>The application's frozen migrations.</summary>
    public static SchemaReference Frozen { get; } = new(SchemaMigrations.All);

    public int LatestVersion => _migrations.Count == 0 ? 0 : _migrations[^1].Version;

    public SchemaFingerprint For(int version)
    {
        if (version < 1 || version > LatestVersion)
            throw new ArgumentOutOfRangeException(nameof(version), version, "No frozen migrations produce this schema version.");

        lock (_gate)
        {
            if (!_cache.TryGetValue(version, out var fingerprint))
                _cache[version] = fingerprint = Build(version);
            return fingerprint;
        }
    }

    /// <summary>
    /// Reference identity: per-object differences of <paramref name="actual"/> from the reference for
    /// <paramref name="version"/>, by name only (never SQL text): <c>missing</c>, <c>extra</c>, <c>definition differs</c>,
    /// <c>structure differs</c>; at most 10, then a "… n more" line. Empty when both layers match.
    /// </summary>
    public IReadOnlyList<string> DifferencesFrom(int version, SchemaFingerprint actual)
    {
        var expected = For(version);
        var differences = new List<string>();
        var actualByKey = actual.Definitions.ToDictionary(d => (d.Type, d.Name));
        var expectedByKey = expected.Definitions.ToDictionary(d => (d.Type, d.Name));

        foreach (var e in expected.Definitions)
        {
            if (!actualByKey.TryGetValue((e.Type, e.Name), out var a))
                differences.Add($"missing {e.Type} {e.Name}");
            else if (!string.Equals(a.TableName, e.TableName, StringComparison.Ordinal) || !string.Equals(Canonical(a.Sql), Canonical(e.Sql), StringComparison.Ordinal))
                differences.Add($"{e.Type} {e.Name}: definition differs");
        }

        foreach (var a in actual.Definitions.Where(a => !expectedByKey.ContainsKey((a.Type, a.Name))))
            differences.Add($"extra {a.Type} {a.Name}");

        var actualStructure = actual.Structure.GroupBy(s => s.Owner, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(s => s.Text).ToList(), StringComparer.Ordinal);
        var expectedStructure = expected.Structure.GroupBy(s => s.Owner, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(s => s.Text).ToList(), StringComparer.Ordinal);
        foreach (var owner in expectedStructure.Keys.Union(actualStructure.Keys).Order(StringComparer.Ordinal))
        {
            var same = actualStructure.TryGetValue(owner, out var a) && expectedStructure.TryGetValue(owner, out var e) && a.SequenceEqual(e, StringComparer.Ordinal);
            if (!same)
                differences.Add($"{owner}: structure differs");
        }

        return differences.Count <= MaxReportedDifferences ? differences : [.. differences.Take(MaxReportedDifferences), $"… {differences.Count - MaxReportedDifferences} more"];
    }

    /// <summary>
    /// The canonical reference contract pinned by the golden fixture: the <see cref="SchemaFingerprint.ExactHash"/>
    /// serialization of the reference with each definition's SQL in canonical form. Equal for LF, CRLF and mixed builds.
    /// </summary>
    public string CanonicalHash(int version)
    {
        var reference = For(version);
        return SchemaFingerprint.HashOf(reference.Definitions.Select(d => d with { Sql = Canonical(d.Sql) }), reference.Structure);
    }

    private static string? Canonical(string? sql) => sql?.Replace("\r\n", "\n", StringComparison.Ordinal);

    private SchemaFingerprint Build(int version)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = ":memory:", ForeignKeys = true, Pooling = false }.ToString());
        connection.Open();
        foreach (var migration in _migrations.Where(m => m.Version <= version))
        {
            using var transaction = connection.BeginTransaction();
            migration.Apply(connection, transaction);
            connection.SetUserVersion(migration.Version, transaction);
            transaction.Commit();
        }

        return SchemaFingerprint.Read(connection);
    }
}
