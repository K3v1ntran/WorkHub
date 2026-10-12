using System.Collections.Concurrent;
using WorkHub.Core.Workspace.Search;

namespace WorkHub.Tests.TestSupport;

/// <summary>
/// Wraps a real <see cref="IWorkspaceSearchStore"/>; the next "search" or "resolve" call can be held until released or
/// failed. A held call reads the database when CALLED and delivers that snapshot on release (a slow read). A held search
/// can also report a busy attempt first (<see cref="SearchGate.ReportBusy"/>).
/// </summary>
internal sealed class GatedSearchStore(IWorkspaceSearchStore inner) : IWorkspaceSearchStore
{
    private readonly ConcurrentDictionary<string, SearchGate> _gates = new();
    private int _searches;
    private int _resolves;

    public SearchGate Hold(string key) => _gates[key] = new SearchGate();

    /// <summary>Holds the next search after it reported one busy (locked) attempt, as the real store does before retrying.</summary>
    public SearchGate HoldBusy() => _gates["search"] = new SearchGate { ReportBusy = true };

    public int Searches => Volatile.Read(ref _searches);

    public int Resolves => Volatile.Read(ref _resolves);

    public async Task<WorkspaceSearchResult> SearchAsync(WorkspaceSearchQuery query, int limit, CancellationToken cancellationToken, Action? onBusy = null)
    {
        Interlocked.Increment(ref _searches);
        if (!_gates.TryRemove("search", out var gate))
            return await inner.SearchAsync(query, limit, cancellationToken, onBusy);

        var early = inner.SearchAsync(query, limit, CancellationToken.None);
        await Task.WhenAny(early);
        if (gate.ReportBusy)
            onBusy?.Invoke();
        gate.Entered.Set();
        if (await gate.Released is { } failure)
            throw failure;
        return await early;
    }

    public async Task<RevealResolution> ResolveAsync(SearchItemRef item, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _resolves);
        if (!_gates.TryRemove("resolve", out var gate))
            return await inner.ResolveAsync(item, cancellationToken);

        var early = inner.ResolveAsync(item, CancellationToken.None);
        await Task.WhenAny(early);
        gate.Entered.Set();
        if (await gate.Released is { } failure)
            throw failure;
        return await early;
    }
}

internal sealed class SearchGate
{
    private readonly TaskCompletionSource<Exception?> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool ReportBusy { get; init; }

    public ManualResetEventSlim Entered { get; } = new();

    public Task<Exception?> Released => _release.Task;

    public void Release() => _release.SetResult(null);

    public void Fail(Exception exception) => _release.SetResult(exception);

    public void WaitEntered() => Assert.True(Entered.Wait(TimeSpan.FromSeconds(10)), "the search store call was never made");
}
