using WorkHub.Infrastructure.Persistence.Migrations;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Persistence;

/// <summary>
/// The precondition of line-ending-tolerant reference identity (Schema_Fingerprint_LineEndings_Investigation Revision 2,
/// §8.5, LE-12, LE-13). Reference comparison treats CRLF and LF as equal, which is sound only because no frozen migration
/// has a bare CR or a line break inside a quoted string or identifier. Binding for every future migration: a migration
/// that needs a multi-line literal requires a token-aware design first.
/// </summary>
public sealed class FrozenSqlLineEndingGuardTests
{
    [Fact]
    public void EveryFrozenMigration_HasNoBareCr_AndNoLineBreakInsideAQuotedToken()
    {
        foreach (var migration in SchemaMigrations.All)
            Assert.True(Problems(LineEndingMigrations.FrozenSql(migration)).Count == 0,
                $"V{migration.Version:D3}: {string.Join("; ", Problems(LineEndingMigrations.FrozenSql(migration)))}");
    }

    [Theory]
    [InlineData("CHECK (x <> 'a\nb')", "line break inside '…'")]
    [InlineData("CHECK (x <> 'a\r\nb')", "line break inside '…'")]
    [InlineData("CREATE TABLE \"T\nX\" (Id INTEGER)", "line break inside \"…\"")]
    [InlineData("CREATE TABLE [T\r\nX] (Id INTEGER)", "line break inside […]")]
    [InlineData("CREATE TABLE `T\nX` (Id INTEGER)", "line break inside `…`")]
    [InlineData("CHECK (x <> 'it''s\nhere')", "line break inside '…'")]
    [InlineData("CREATE TABLE T (Id INTEGER)\r", "bare CR")]
    [InlineData("CREATE TABLE T (\rId INTEGER)", "bare CR")]
    [InlineData("-- a comment\rCREATE TABLE T (Id INTEGER)", "bare CR")]
    [InlineData("CHECK (x <> 'open", "unterminated '…'")]
    public void TheGuard_FindsBreakingSql(string sql, string expected) =>
        Assert.Contains(Problems(sql), p => p.StartsWith(expected, StringComparison.Ordinal));

    [Theory]
    [InlineData("-- don't be fooled\nCHECK (x IN ('a', 'b'))\n")]
    [InlineData("-- don't be fooled\r\nCHECK (x IN ('a', 'b'))\r\n")]
    [InlineData("/* it's a\nblock comment */ CHECK (x <> 'y')")]
    [InlineData("CHECK (x <> 'it''s')\nAND y <> \"q\"\"r\"\r\n")]
    [InlineData("SELECT RAISE(ABORT, 'WH_X');\nEND;")]
    public void TheGuard_AcceptsLineBreaksOutsideQuotedTokens(string sql) =>
        Assert.Empty(Problems(sql));

    /// <summary>Comment-aware scan of SQL text: bare CRs anywhere, and line breaks inside quoted strings/identifiers.</summary>
    internal static IReadOnlyList<string> Problems(string sql)
    {
        var problems = new List<string>();
        for (var i = 0; i < sql.Length; i++)
        {
            if (sql[i] == '\r' && (i + 1 == sql.Length || sql[i + 1] != '\n'))
                problems.Add($"bare CR at {i}");
        }

        var p = 0;
        while (p < sql.Length)
        {
            var c = sql[p];
            if (c == '-' && p + 1 < sql.Length && sql[p + 1] == '-')
            {
                var end = sql.IndexOf('\n', p);
                p = end < 0 ? sql.Length : end + 1;
            }
            else if (c == '/' && p + 1 < sql.Length && sql[p + 1] == '*')
            {
                var end = sql.IndexOf("*/", p + 2, StringComparison.Ordinal);
                p = end < 0 ? sql.Length : end + 2;
            }
            else if (c is '\'' or '"' or '`' or '[')
            {
                var close = c == '[' ? ']' : c;
                var end = p + 1;
                while (true)
                {
                    end = sql.IndexOf(close, end);
                    if (end >= 0 && close != ']' && end + 1 < sql.Length && sql[end + 1] == close)
                    {
                        end += 2; // doubled quote: an escaped quote character inside the token
                        continue;
                    }

                    break;
                }

                if (end < 0)
                {
                    problems.Add($"unterminated {c}…{close} at {p}");
                    break;
                }

                if (sql.AsSpan(p, end - p + 1).IndexOfAny('\r', '\n') >= 0)
                    problems.Add($"line break inside {c}…{close} at {p}");
                p = end + 1;
            }
            else
            {
                p++;
            }
        }

        return problems;
    }
}
