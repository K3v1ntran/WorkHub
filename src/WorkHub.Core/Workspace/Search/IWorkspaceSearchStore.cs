namespace WorkHub.Core.Workspace.Search;

/// <summary>
/// Read-only Workspace search (Phase 6). Implementations run off the caller's thread, use one deferred read transaction
/// per call, and never write. Query text and stored content are never logged.
/// </summary>
public interface IWorkspaceSearchStore
{
    /// <summary>
    /// Every item matching all terms, ordered by <see cref="WorkspaceSearchText.Compare"/>; the first
    /// <paramref name="limit"/> are returned with locations and excerpts, and <see cref="WorkspaceSearchResult.Truncated"/>
    /// says whether more matched.
    /// </summary>
    /// <exception cref="OperationCanceledException">The token was cancelled while the search ran.</exception>
    /// <param name="onBusy">Called (on a worker thread) each time an attempt found the database locked and the read will be retried.</param>
    Task<WorkspaceSearchResult> SearchAsync(WorkspaceSearchQuery query, int limit, CancellationToken cancellationToken, Action? onBusy = null);

    /// <summary>
    /// Looks the item up in the table of ITS kind (checking the node type for WorkspaceFolder/Project), then reads its
    /// ancestor chain and every Workspace branch on that chain, all in one read transaction.
    /// </summary>
    Task<RevealResolution> ResolveAsync(SearchItemRef item, CancellationToken cancellationToken);
}
