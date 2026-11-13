using System.Text;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Settings;
using WorkHub.Infrastructure.Settings;

namespace WorkHub.Tests.TestSupport;

/// <summary>Phase 16A: a settings store that records each Update and answers with a scripted status (no file).</summary>
internal sealed class RecordingSettingsStore(List<string>? sharedCalls = null) : ISettingsStore
{
    public List<string> Calls { get; } = sharedCalls ?? [];

    public List<SettingsEdit> Edits { get; } = [];

    public List<SaveBudget?> Budgets { get; } = [];

    /// <summary>The status every Update returns (default Saved).</summary>
    public SettingsUpdateStatus Status { get; set; } = SettingsUpdateStatus.Saved;

    /// <summary>Thrown by Update instead of returning (e.g. an IOException).</summary>
    public Exception? Throw { get; set; }

    public SettingsLoadResult LoadOrCreate() => new(AppSettings.CreateDefault(), SettingsLoadStatus.Loaded);

    public void Save(AppSettings settings) => throw new NotSupportedException();

    public SettingsUpdateResult Update(SettingsEdit edit, AppSettings fallback) => Record(edit, fallback, null);

    public SettingsUpdateResult Update(SettingsEdit edit, AppSettings fallback, SaveBudget budget) => Record(edit, fallback, budget);

    private SettingsUpdateResult Record(SettingsEdit edit, AppSettings fallback, SaveBudget? budget)
    {
        Calls.Add("update");
        Edits.Add(edit);
        Budgets.Add(budget);
        if (Throw is not null)
            throw Throw;
        return Status == SettingsUpdateStatus.Saved ? new SettingsUpdateResult(Status, fallback) : new SettingsUpdateResult(Status);
    }
}

/// <summary>Records every log line (Phase 16A asserts exactly one "not saved" entry).</summary>
internal sealed class ListLog : IAppLog
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
                return _lines.ToList();
        }
    }

    public IReadOnlyList<string> NotSaved => Lines.Where(l => l.Contains("Window state not saved", StringComparison.Ordinal)).ToList();

    public void Info(string message) => Add("INFO " + message);

    public void Warn(string message) => Add("WARN " + message);

    public void Error(string message, Exception? exception = null) => Add("ERROR " + message);

    private void Add(string line)
    {
        lock (_lines)
            _lines.Add(line);
    }
}

/// <summary>
/// Phase 16A (L6, L6c): records the temp-file write and the atomic replace of a real <see cref="JsonSettingsStore"/>, and
/// can run an action while the temp file is being written (e.g. move a fake clock past the deadline).
/// </summary>
internal sealed class SpyFileOps : SettingsFileOps
{
    public List<string> Calls { get; } = [];

    public Action? DuringTempWrite { get; set; }

    public override void WriteAllText(string path, string contents, Encoding encoding)
    {
        Calls.Add("write-temp");
        DuringTempWrite?.Invoke();
        base.WriteAllText(path, contents, encoding);
    }

    public override void Move(string source, string destination, bool overwrite)
    {
        Calls.Add("replace");
        base.Move(source, destination, overwrite);
    }
}

/// <summary>
/// Phase 16 (D12): a clock whose wall clock (UTC) and monotonic timestamp move independently. <see cref="Advance"/> is real
/// time passing (both move); <see cref="JumpUtc"/> is a system-clock change (only UTC moves, forward or back);
/// <see cref="AdvanceMonotonic"/> moves only the timestamp. Thread-safe.
/// </summary>
internal sealed class SplitClock : TimeProvider
{
    private long _utcTicks = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero).UtcTicks;
    private long _timestamp = 1_000_000_000;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

    public void Advance(TimeSpan by)
    {
        AdvanceMonotonic(by);
        JumpUtc(by);
    }

    public void AdvanceMonotonic(TimeSpan by)
    {
        if (by < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(by), "A monotonic timestamp never goes back.");
        Interlocked.Add(ref _timestamp, by.Ticks);
    }

    public void JumpUtc(TimeSpan by) => Interlocked.Add(ref _utcTicks, by.Ticks);
}

internal static class UiStates
{
    public static UiStateChanges Bounds(double left, double top, double width = 1200, double height = 800, bool maximized = false, string? tab = "Workspace") =>
        new() { Left = left, Top = top, Width = width, Height = height, IsMaximized = maximized, LastPrimaryTab = tab };
}
