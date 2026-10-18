using System.Reflection;
using Microsoft.Data.Sqlite;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Infrastructure.Persistence.Migrations;

namespace WorkHub.Tests.TestSupport;

/// <summary>How a build's checkout stores the frozen migration sources.</summary>
public enum Checkout
{
    /// <summary>Every migration LF (for example <c>core.autocrlf=false</c>).</summary>
    Lf,

    /// <summary>Every migration CRLF (a fresh checkout with <c>core.autocrlf=true</c>).</summary>
    Crlf,

    /// <summary>The main working tree at dcfa6bb: V002, V003 and V007 CRLF, the others LF.</summary>
    Mixed,
}

/// <summary>
/// The frozen migrations as another checkout would compile them: the same SQL text with only its line terminators
/// changed. The text is read from each migration's <c>Sql</c> constant, so the frozen files are never edited and the
/// result does not depend on this build's own checkout.
/// </summary>
internal static class LineEndingMigrations
{
    private static readonly int[] MixedCrlfVersions = [2, 3, 7];

    public static ISchemaMigration[] For(Checkout checkout, int toVersion = 7) =>
        [.. SchemaMigrations.All.Where(m => m.Version <= toVersion).Select(m => new Converted(m, NewLine(checkout, m.Version)))];

    /// <summary>The <c>Sql</c> constant of a frozen migration, exactly as this build compiled it.</summary>
    public static string FrozenSql(ISchemaMigration migration) =>
        migration.GetType().GetField("Sql", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetRawConstantValue() as string
        ?? throw new InvalidOperationException($"{migration.GetType().Name} has no Sql constant.");

    private static string NewLine(Checkout checkout, int version) => checkout switch
    {
        Checkout.Lf => "\n",
        Checkout.Crlf => "\r\n",
        _ => MixedCrlfVersions.Contains(version) ? "\r\n" : "\n",
    };

    private sealed class Converted(ISchemaMigration frozen, string newLine) : ISchemaMigration
    {
        private readonly string _sql = FrozenSql(frozen).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newLine, StringComparison.Ordinal);

        public int Version => frozen.Version;

        public string Description => frozen.Description;

        public void Apply(SqliteConnection connection, SqliteTransaction transaction) => connection.ExecuteScript(_sql, transaction);
    }
}
