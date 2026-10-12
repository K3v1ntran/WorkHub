namespace WorkHub.Core.Workspace.Search;

/// <summary>Workspace search use cases. Read-only: nothing here writes, and activation always re-resolves.</summary>
public sealed class WorkspaceSearchService(IWorkspaceSearchStore store)
{
    public const int ResultLimit = 200;

    /// <summary>Null = empty query (show the tree).</summary>
    public static WorkspaceSearchQuery? Parse(string? text) => WorkspaceSearchText.Parse(text);

    /// <param name="onBusy">Called on a worker thread when an attempt met a locked database and is being retried.</param>
    public Task<WorkspaceSearchResult> SearchAsync(WorkspaceSearchQuery query, CancellationToken cancellationToken = default, Action? onBusy = null) =>
        store.SearchAsync(query, ResultLimit, cancellationToken, onBusy);

    public Task<RevealResolution> ResolveAsync(SearchItemRef item, CancellationToken cancellationToken = default) =>
        store.ResolveAsync(item, cancellationToken);
}
