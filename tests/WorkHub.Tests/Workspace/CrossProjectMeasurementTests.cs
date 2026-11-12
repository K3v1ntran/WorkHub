using System.Diagnostics;
using System.Globalization;
using System.Text;
using WorkHub.Core.Workspace.Content;
using WorkHub.Tests.TestSupport;
using Xunit.Abstractions;
using static WorkHub.Tests.TestSupport.CrossWorld;

namespace WorkHub.Tests.Workspace;

/// <summary>
/// Phase 15 D-15.6 measurements on the real .NET store (Microsoft.Data.Sqlite / e_sqlite3): preview time, the write-lock hold
/// time (from <c>BEGIN IMMEDIATE</c> to <c>COMMIT</c>, through the store's own write-step hook), managed allocation and the peak
/// working set, at the decided bounds (D-15.6: 10,000 rows; 32 MiB = 33,554,432 bytes of UTF-8; the earlier 64 MiB numbers are kept in the
/// Phase 15 report for comparison). Opt-in like the other feasibility measurements
/// (<c>WORKHUB_FEASIBILITY=1</c>); results are written to the test output and, when <c>WORKHUB_MEASURE_OUT</c> names a file,
/// appended to it.
/// </summary>
public sealed class CrossProjectMeasurementTests(ITestOutputHelper output) : IDisposable
{
    private readonly CrossWorld _w = new();

    public void Dispose() => _w.Dispose();

    private long BigFolder(int rows)
    {
        const string at = "2026-11-11T03:00:00.000Z";
        using var connection = _w.Root.Open();
        Db.Exec(connection, "BEGIN;");
        var root = Db.ScalarLongWith(connection, "INSERT INTO ProjectFolders (ProjectNodeId, ParentFolderId, Name, NameKey, CreatedAt, UpdatedAt) VALUES ($p, NULL, 'Big', 'BIG', $at, $at) RETURNING Id;", ("$p", _w.A), ("$at", at));
        Db.Exec(connection, $"WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 99) INSERT INTO ProjectFolders (ProjectNodeId, ParentFolderId, Name, NameKey, CreatedAt, UpdatedAt) SELECT {_w.A}, {root}, 'sub ' || i, 'SUB ' || i, '{at}', '{at}' FROM n;");
        var links = (rows - 100) / 2;
        Db.Exec(connection, $"WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < {links - 1}) INSERT INTO ProjectLinks (ProjectNodeId, FolderId, LinkType, Name, Target, CreatedAt, UpdatedAt) SELECT {_w.A}, {root} + 1 + (i % 99), 'Web', 'link ' || i, 'https://example.test/item/' || i, '{at}', '{at}' FROM n;");
        Db.Exec(connection, $"WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < {rows - 100 - links - 1}) INSERT INTO ProjectNotes (ProjectNodeId, FolderId, Title, Content, CreatedAt, UpdatedAt) SELECT {_w.A}, {root} + 1 + (i % 99), 'note ' || i, substr(hex(zeroblob(100)), 1, 200), '{at}', '{at}' FROM n;");
        Db.Exec(connection, "COMMIT;");
        return root;
    }

    private async Task<long> PayloadFolder()
    {
        var folder = await _w.Folder(_w.A, null, "Payload");
        var remaining = CrossProjectLimits.MaxPayloadBytes - Encoding.UTF8.GetByteCount("Payload");
        for (var i = 0; remaining > 0; i++)
        {
            var (title, content) = remaining <= 200 ? (new string('t', (int)remaining), string.Empty) : ($"p{i}", null as string);
            if (content is null)
            {
                var bytes = (int)Math.Min(remaining - title.Length, 299_997);
                content = new string('日', bytes / 3) + new string('a', bytes % 3);
            }

            await _w.Note(_w.A, folder, title, content);
            remaining -= Encoding.UTF8.GetByteCount(title) + Encoding.UTF8.GetByteCount(content);
        }

        return folder;
    }

    private async Task Measure(string label, long folder, CrossProjectMode mode, long? targetFolder)
    {
        var request = Req(mode, ContentKind.Folder, folder, _w.B, targetFolder);
        GC.Collect();
        var process = Process.GetCurrentProcess();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var preview = Stopwatch.StartNew();
        var plan = await _w.Content.PrepareCrossProjectAsync(request);
        preview.Stop();
        Assert.True(plan.IsReady, $"{plan.Status}");
        var allocatedPreview = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        var locked = new Stopwatch();
        _w.Store.OnWriteStep = step =>
        {
            if (step == "cross:locked")
                locked.Start();
            else if (step == "cross:committed")
                locked.Stop();
        };
        var commit = Stopwatch.StartNew();
        var result = await _w.Content.CrossProjectAsync(request, plan.Token!);
        commit.Stop();
        _w.Store.OnWriteStep = null;
        Assert.True(result.Committed);
        var allocatedTotal = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        process.Refresh();

        var line = string.Format(CultureInfo.InvariantCulture,
            "{0} {1}: rows {2}, payload {3:F2} MiB; preview {4} ms; commit call {5} ms; write lock held {6} ms; allocated preview {7:F1} MiB, total {8:F1} MiB; peak working set {9:F0} MiB; database {10:F1} MiB",
            label, mode, plan.Rows, plan.PayloadBytes / 1048576.0, preview.ElapsedMilliseconds, commit.ElapsedMilliseconds, locked.ElapsedMilliseconds,
            allocatedPreview / 1048576.0, allocatedTotal / 1048576.0, process.PeakWorkingSet64 / 1048576.0, new FileInfo(_w.Root.Paths.DatabaseFile).Length / 1048576.0);
        output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("WORKHUB_MEASURE_OUT") is { Length: > 0 } file)
            File.AppendAllText(file, line + Environment.NewLine);
    }

    [FeasibilityFact]
    public async Task Measure_10000Rows_CopyThenMove()
    {
        await _w.BuildAsync();
        var folder = BigFolder(CrossProjectLimits.MaxRows);
        await Measure("10,000 rows", folder, CrossProjectMode.Copy, null);
        await Measure("10,000 rows", folder, CrossProjectMode.Move, _w.Inbox); // the copy took the name at the root
    }

    [FeasibilityFact]
    public async Task Measure_32MiBPayload_CopyThenMove()
    {
        await _w.BuildAsync();
        var folder = await PayloadFolder();
        await Measure("32 MiB", folder, CrossProjectMode.Copy, null);
        await Measure("32 MiB", folder, CrossProjectMode.Move, _w.Inbox);
    }
}
