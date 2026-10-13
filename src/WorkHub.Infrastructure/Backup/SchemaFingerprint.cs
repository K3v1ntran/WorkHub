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
/// Two-layer schema fingerprint (Phase 9 §4.5). Both layers must equal the reference built from the frozen migrations.
/// <list type="number">
/// <item><b>Definitions</b> (primary): <c>(type, name, tbl_name, sql)</c> with <c>sql</c> compared byte-exact (ordinal). No
/// whitespace, case, comment or quoting normalization — a whitespace-only change is a mismatch (fails closed). This covers
/// columns, types, defaults, NOT NULL, COLLATE, CHECK, keys, FOREIGN KEY actions, STRICT/WITHOUT ROWID, index columns,
/// uniqueness and partial predicates, and trigger timing, events, WHEN clauses and bodies.</item>
/// <item><b>Structure</b> (defense in depth, diagnostics): <c>pragma_table_list</c>, <c>table_xinfo</c>,
/// <c>foreign_key_list</c>, <c>index_list</c>/<c>index_xinfo</c> (including internal autoindexes, which have no SQL) and
/// whether <c>sqlite_sequence</c> exists. It cannot see CHECK expressions, partial predicates or trigger bodies, which is
/// why the definition layer is primary.</item>
/// </list>
/// Equality claims only that the schema text and structure are identical to what the frozen migrations produce on this
/// runtime — never semantic equivalence. Nothing is ever repaired.
/// </summary>
internal sealed class SchemaFingerprint
{
    private const int MaxReportedDifferences = 10;

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

    /// <summary>SHA-256 (hex) of the canonical serialization of both layers. Stable across processes and machines.</summary>
    public string Hash()
    {
        var text = new StringBuilder();
        foreach (var d in Definitions)
            text.Append(d.Type).Append('\u001f').Append(d.Name).Append('\u001f').Append(d.TableName).Append('\u001f').Append(d.Sql ?? "\0").Append('\u001e');
        text.Append('\u001d');
        foreach (var s in Structure)
            text.Append(s.Owner).Append('\u001f').Append(s.Text).Append('\u001e');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// Per-object differences from <paramref name="expected"/>, by name only (never SQL text): <c>missing</c>, <c>extra</c>,
    /// <c>definition differs</c>, <c>structure differs</c>. Empty when both layers are equal.
    /// </summary>
    public IReadOnlyList<string> DifferencesFrom(SchemaFingerprint expected)
    {
        var differences = new List<string>();
        var actualByKey = Definitions.ToDictionary(d => (d.Type, d.Name));
        var expectedByKey = expected.Definitions.ToDictionary(d => (d.Type, d.Name));

        foreach (var e in expected.Definitions)
        {
            if (!actualByKey.TryGetValue((e.Type, e.Name), out var a))
                differences.Add($"missing {e.Type} {e.Name}");
            else if (!string.Equals(a.TableName, e.TableName, StringComparison.Ordinal) || !string.Equals(a.Sql, e.Sql, StringComparison.Ordinal))
                differences.Add($"{e.Type} {e.Name}: definition differs");
        }

        foreach (var a in Definitions.Where(a => !expectedByKey.ContainsKey((a.Type, a.Name))))
            differences.Add($"extra {a.Type} {a.Name}");

        var actualStructure = Structure.GroupBy(s => s.Owner, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(s => s.Text).ToList(), StringComparer.Ordinal);
        var expectedStructure = expected.Structure.GroupBy(s => s.Owner, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(s => s.Text).ToList(), StringComparer.Ordinal);
        foreach (var owner in expectedStructure.Keys.Union(actualStructure.Keys).Order(StringComparer.Ordinal))
        {
            var same = actualStructure.TryGetValue(owner, out var a) && expectedStructure.TryGetValue(owner, out var e) && a.SequenceEqual(e, StringComparer.Ordinal);
            if (!same)
                differences.Add($"{owner}: structure differs");
        }

        return differences.Count <= MaxReportedDifferences ? differences : [.. differences.Take(MaxReportedDifferences), $"… {differences.Count - MaxReportedDifferences} more"];
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
/// </summary>
internal sealed class SchemaReference
{
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
