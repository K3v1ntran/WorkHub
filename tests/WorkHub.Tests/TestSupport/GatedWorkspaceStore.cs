using System.Collections.Concurrent;
using WorkHub.Core.Workspace;

namespace WorkHub.Tests.TestSupport;

/// <summary>
/// Wraps a real <see cref="IWorkspaceStore"/> and can hold the next call for a key until the test releases or fails it.
/// A held read runs against the database when it is CALLED and delivers that snapshot only when released, which models a
/// slow query whose result arrives after newer changes. Keys: "children:root", "children:{id}", "details:{id}",
/// "createFolder", "createProject", "rename", "update", "move", and the Workspace Trash keys below.
/// </summary>
internal sealed class GatedWorkspaceStore(IWorkspaceStore inner) : IWorkspaceStore
{
    private readonly ConcurrentDictionary<string, StoreGate> _gates = new();
    private readonly ConcurrentQueue<StoreGate> _issued = new();
    private readonly ConcurrentDictionary<string, int> _calls = new();

    /// <summary>Holds the next call for <paramref name="key"/>.</summary>
    public StoreGate Hold(string key) => Issue(_gates[key] = new StoreGate());

    /// <summary>Holds only the RESULT of the next write for <paramref name="key"/>: the write itself commits when called.</summary>
    public StoreGate HoldCommitted(string key) => Issue(_gates[key] = new StoreGate { CommitFirst = true });

    /// <summary>Teardown: releases every gate this store handed out that is still held.</summary>
    public void ReleaseAll()
    {
        foreach (var gate in _issued)
            gate.TryRelease();
    }

    private StoreGate Issue(StoreGate gate)
    {
        _issued.Enqueue(gate);
        return gate;
    }

    public int Calls(string key) => _calls.GetValueOrDefault(key);

    /// <summary>Phase 20: forgets the call counts and recorded calls made while a fixture was built (gates are untouched).</summary>
    public void ResetCalls()
    {
        _calls.Clear();
        UnarchiveBoundCalls.Clear();
    }

    public static string Children(long? parentId) => parentId is { } id ? $"children:{id}" : "children:root";

    public Task<WorkspaceChildrenResult> ListChildrenAsync(long? parentId, CancellationToken cancellationToken = default) =>
        Gated(Children(parentId), () => inner.ListChildrenAsync(parentId, CancellationToken.None));

    public Task<WorkspaceNode> CreateFolderAsync(long? parentId, string name, CancellationToken cancellationToken = default) =>
        Gated("createFolder", () => inner.CreateFolderAsync(parentId, name, cancellationToken), readFirst: false);

    public Task<WorkspaceNode> CreateProjectAsync(long? parentId, string projectId, ProjectDraft draft, CancellationToken cancellationToken = default) =>
        Gated("createProject", () => inner.CreateProjectAsync(parentId, projectId, draft, cancellationToken), readFirst: false);

    public Task<bool> RenameAsync(long nodeId, string name, CancellationToken cancellationToken = default) =>
        Gated("rename", () => inner.RenameAsync(nodeId, name, cancellationToken), readFirst: false);

    public Task<ProjectDetails?> GetProjectAsync(long nodeId, CancellationToken cancellationToken = default) =>
        ScriptProject.TryRemove(nodeId, out var script)
            ? Gated($"details:{nodeId}", script)
            : Gated($"details:{nodeId}", () => inner.GetProjectAsync(nodeId, CancellationToken.None));

    /// <summary>Phase 21: when set, the next "details" call for that node returns this (or throws) instead of reaching the database.</summary>
    public ConcurrentDictionary<long, Func<Task<ProjectDetails?>>> ScriptProject { get; } = new();

    /// <summary>Phase 21 R2a: every draft passed to UpdateProjectAsync, in call order (the "draft sent to the store").</summary>
    public ConcurrentQueue<ProjectDraft> UpdateDrafts { get; } = new();

    public Task<ProjectDetails?> UpdateProjectAsync(long nodeId, ProjectDraft draft, CancellationToken cancellationToken = default)
    {
        UpdateDrafts.Enqueue(draft);
        return Gated("update", () => inner.UpdateProjectAsync(nodeId, draft, cancellationToken), readFirst: false);
    }

    public Task<IReadOnlyList<WorkspaceFolderPath>> ListMoveTargetsAsync(long movingNodeId, CancellationToken cancellationToken = default) =>
        inner.ListMoveTargetsAsync(movingNodeId, cancellationToken);

    public Task<bool> MoveAsync(long nodeId, long? targetParentId, CancellationToken cancellationToken = default) =>
        Gated("move", () => inner.MoveAsync(nodeId, targetParentId, cancellationToken), readFirst: false);

    // ---- Workspace Trash: "trashProject", "restoreProject", "removeFolder" (held writes run on release);
    // "nodeState", "listTrash", "countTrash" (held reads take their snapshot when called).

    public Task<ProjectTrashResult> TrashProjectAsync(long nodeId, CancellationToken cancellationToken = default) =>
        Gated("trashProject", () => inner.TrashProjectAsync(nodeId, cancellationToken), readFirst: false);

    public Task<ProjectRestoreResult> RestoreProjectAsync(long nodeId, CancellationToken cancellationToken = default) =>
        Gated("restoreProject", () => inner.RestoreProjectAsync(nodeId, cancellationToken), readFirst: false);

    public Task<IReadOnlyList<WorkspaceTrashEntry>> ListTrashAsync(CancellationToken cancellationToken = default) =>
        Gated("listTrash", () => inner.ListTrashAsync(CancellationToken.None));

    public Task<int> CountTrashAsync(CancellationToken cancellationToken = default) =>
        Gated("countTrash", () => inner.CountTrashAsync(CancellationToken.None));

    public Task<FolderRemoveResult> RemoveEmptyFolderAsync(long nodeId, CancellationToken cancellationToken = default) =>
        Gated("removeFolder", () => inner.RemoveEmptyFolderAsync(nodeId, cancellationToken), readFirst: false);

    public Task<WorkspaceNodeState?> GetNodeStateAsync(long nodeId, CancellationToken cancellationToken = default) =>
        ScriptNodeState.TryRemove(nodeId, out var script)
            ? Gated("nodeState", script)
            : Gated("nodeState", () => inner.GetNodeStateAsync(nodeId, CancellationToken.None));

    // ---- Project Archive: "archiveProject", "unarchiveProject", "undoArchive" (held writes run on release, or commit first with
    // HoldCommitted); "listArchived", "countArchived" (held reads take their snapshot when called).

    public Task<ProjectArchiveResult> ArchiveProjectAsync(long nodeId, CancellationToken cancellationToken = default) =>
        Gated("archiveProject", () => inner.ArchiveProjectAsync(nodeId, cancellationToken), readFirst: false);

    public Task<ProjectUnarchiveResult> UnarchiveProjectAsync(long nodeId, ProjectStatus status, CancellationToken cancellationToken = default) =>
        Gated("unarchiveProject", () => inner.UnarchiveProjectAsync(nodeId, status, cancellationToken), readFirst: false);

    public Task<ArchiveUndoResult> UndoArchiveAsync(ArchiveEvent archiveEvent, CancellationToken cancellationToken = default) =>
        Gated("undoArchive", () => inner.UndoArchiveAsync(archiveEvent, cancellationToken), readFirst: false);

    public Task<IReadOnlyList<ArchivedProjectEntry>> ListArchivedAsync(CancellationToken cancellationToken = default) =>
        Gated("listArchived", () => inner.ListArchivedAsync(CancellationToken.None));

    public Task<int> CountArchivedAsync(CancellationToken cancellationToken = default) =>
        Gated("countArchived", () => inner.CountArchivedAsync(CancellationToken.None));

    // ---- Phase 19 Bulk Archive: "bulkCandidates" (a held read takes its snapshot when called); "archiveInPlace" (a held write
    // runs on release, or commits first with HoldCommitted). ScriptArchiveInPlace / ScriptNodeState replace one call's result.

    /// <summary>When set, the next "archiveInPlace" call returns this (or throws) instead of reaching the database.</summary>
    public Func<BulkArchiveTarget, Task<BulkArchiveResult>>? ScriptArchiveInPlace { get; set; }

    /// <summary>When set, the next "nodeState" call for that node returns this (or throws) instead of reaching the database.</summary>
    public ConcurrentDictionary<long, Func<Task<WorkspaceNodeState?>>> ScriptNodeState { get; } = new();

    public Task<BulkArchiveCandidates> ListBulkArchiveCandidatesAsync(long? parentId, CancellationToken cancellationToken = default) =>
        Gated("bulkCandidates", () => inner.ListBulkArchiveCandidatesAsync(parentId, CancellationToken.None));

    public Task<BulkArchiveResult> ArchiveProjectInPlaceAsync(BulkArchiveTarget target, CancellationToken cancellationToken = default)
    {
        if (ScriptArchiveInPlace is { } script)
        {
            ScriptArchiveInPlace = null;
            _calls.AddOrUpdate("archiveInPlace", 1, (_, n) => n + 1);
            return script(target);
        }

        return Gated("archiveInPlace", () => inner.ArchiveProjectInPlaceAsync(target, cancellationToken), readFirst: false);
    }

    // ---- Phase 20 Bulk Unarchive: "bulkUnarchiveCandidates" (a held read takes its snapshot when called); "unarchiveBound" (a held
    // write runs on release, or commits first with HoldCommitted). ScriptUnarchiveBound replaces one call's result; every call's
    // target and status are recorded in order.

    /// <summary>When set, the next "unarchiveBound" call returns this (or throws) instead of reaching the database.</summary>
    public Func<BulkUnarchiveTarget, Task<BulkUnarchiveResult>>? ScriptUnarchiveBound { get; set; }

    /// <summary>Every "unarchiveBound" call, in call order.</summary>
    public ConcurrentQueue<(BulkUnarchiveTarget Target, ProjectStatus Status)> UnarchiveBoundCalls { get; } = new();

    public Task<BulkUnarchiveCandidates> ListBulkUnarchiveCandidatesAsync(CancellationToken cancellationToken = default) =>
        Gated("bulkUnarchiveCandidates", () => inner.ListBulkUnarchiveCandidatesAsync(CancellationToken.None));

    public Task<BulkUnarchiveResult> UnarchiveProjectBoundAsync(BulkUnarchiveTarget target, ProjectStatus status, CancellationToken cancellationToken = default)
    {
        UnarchiveBoundCalls.Enqueue((target, status));
        if (ScriptUnarchiveBound is { } script)
        {
            ScriptUnarchiveBound = null;
            _calls.AddOrUpdate("unarchiveBound", 1, (_, n) => n + 1);
            return script(target);
        }

        return Gated("unarchiveBound", () => inner.UnarchiveProjectBoundAsync(target, status, cancellationToken), readFirst: false);
    }

    public Task<IReadOnlyList<string>> ListTagSuggestionsAsync(long? excludeProjectNodeId = null, int limit = 200, CancellationToken cancellationToken = default) =>
        Gated("listTagSuggestions", () => inner.ListTagSuggestionsAsync(excludeProjectNodeId, limit, CancellationToken.None));

    // ---- Phase 21: "tagsInUse", "prepareTagChange" (held reads take their snapshot when called), "applyTagChange" (a held write
    // runs on release, or commits first with HoldCommitted). Every apply call is recorded in ApplyTagChangeCalls.

    public ConcurrentQueue<(TagChangeRequest Request, string Token)> ApplyTagChangeCalls { get; } = new();

    public Task<IReadOnlyList<TagInUse>> ListTagsInUseAsync(CancellationToken cancellationToken = default) =>
        Gated("tagsInUse", () => inner.ListTagsInUseAsync(CancellationToken.None));

    public Task<TagChangePreview> PrepareTagChangeAsync(TagChangeRequest request, CancellationToken cancellationToken = default) =>
        Gated("prepareTagChange", () => inner.PrepareTagChangeAsync(request, CancellationToken.None));

    public Task<TagChangeResult> ApplyTagChangeAsync(TagChangeRequest request, string token, CancellationToken cancellationToken = default)
    {
        ApplyTagChangeCalls.Enqueue((request, token));
        return Gated("applyTagChange", () => inner.ApplyTagChangeAsync(request, token, CancellationToken.None), readFirst: false);
    }

    // ---- Phase 22: "prepareTagDelete" (a held read takes its snapshot when called), "applyTagDelete" (a held write runs on release,
    // or commits first with HoldCommitted). Every apply call is recorded in ApplyTagDeleteCalls (the store-side Apply counter).

    public ConcurrentQueue<(string FromKey, string Token)> ApplyTagDeleteCalls { get; } = new();

    public Task<TagDeletePreview> PrepareTagDeleteAsync(string fromKey, CancellationToken cancellationToken = default) =>
        Gated("prepareTagDelete", () => inner.PrepareTagDeleteAsync(fromKey, CancellationToken.None));

    public Task<TagDeleteResult> ApplyTagDeleteAsync(string fromKey, string token, CancellationToken cancellationToken = default)
    {
        ApplyTagDeleteCalls.Enqueue((fromKey, token));
        return Gated("applyTagDelete", () => inner.ApplyTagDeleteAsync(fromKey, token, CancellationToken.None), readFirst: false);
    }

    // ---- Phase 24: "taggedProjects" (a held read takes its snapshot when called and delivers it, or the failure, on release).
    // Every call's key is recorded in TaggedProjectsCalls, in call order.

    public ConcurrentQueue<string> TaggedProjectsCalls { get; } = new();

    public Task<TaggedProjectsResult> ListProjectsWithTagAsync(string nameKey, int limit, CancellationToken cancellationToken = default)
    {
        TaggedProjectsCalls.Enqueue(nameKey);
        return Gated("taggedProjects", () => inner.ListProjectsWithTagAsync(nameKey, limit, CancellationToken.None));
    }

    // ---- Folder Archive and Trash (schema v9): "prepareFolder" (a held read takes its snapshot when called); "archiveFolder",
    // "trashFolder", "unarchiveFolder", "restoreFolder", "undoFolder" (held writes run on release, or commit first with HoldCommitted).

    public Task<FolderLifecyclePreview> PrepareFolderLifecycleAsync(long nodeId, FolderLifecycleKind kind, CancellationToken cancellationToken = default) =>
        Gated("prepareFolder", () => inner.PrepareFolderLifecycleAsync(nodeId, kind, CancellationToken.None));

    public Task<FolderLifecycleResult> ArchiveFolderAsync(long nodeId, string confirmationToken, CancellationToken cancellationToken = default) =>
        Gated("archiveFolder", () => inner.ArchiveFolderAsync(nodeId, confirmationToken, cancellationToken), readFirst: false);

    public Task<FolderLifecycleResult> TrashFolderAsync(long nodeId, string confirmationToken, CancellationToken cancellationToken = default) =>
        Gated("trashFolder", () => inner.TrashFolderAsync(nodeId, confirmationToken, cancellationToken), readFirst: false);

    public Task<FolderClearResult> UnarchiveFolderAsync(long nodeId, CancellationToken cancellationToken = default) =>
        Gated("unarchiveFolder", () => inner.UnarchiveFolderAsync(nodeId, cancellationToken), readFirst: false);

    public Task<FolderClearResult> RestoreFolderAsync(long nodeId, CancellationToken cancellationToken = default) =>
        Gated("restoreFolder", () => inner.RestoreFolderAsync(nodeId, cancellationToken), readFirst: false);

    public Task<FolderClearResult> UndoFolderLifecycleAsync(FolderLifecycleEvent lifecycleEvent, CancellationToken cancellationToken = default) =>
        Gated("undoFolder", () => inner.UndoFolderLifecycleAsync(lifecycleEvent, cancellationToken), readFirst: false);

    private async Task<T> Gated<T>(string key, Func<Task<T>> call, bool readFirst = true)
    {
        _calls.AddOrUpdate(key, 1, (_, n) => n + 1);
        if (!_gates.TryRemove(key, out var gate))
            return await call();

        Task<T>? early = readFirst || gate.CommitFirst ? call() : null;
        if (early is not null)
            await Task.WhenAny(early); // the read (or a CommitFirst write) happens now; its result is delivered later
        gate.Entered.Set();
        if (await gate.Released is { } failure)
            throw failure;
        return early is not null ? await early : await call();
    }
}

internal sealed class StoreGate
{
    private readonly TaskCompletionSource<Exception?> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A held WRITE runs (and commits) when called; only its result is delivered on release (the caller has not applied it yet).</summary>
    public bool CommitFirst { get; init; }

    public ManualResetEventSlim Entered { get; } = new();

    public Task<Exception?> Released => _release.Task;

    public void Release() => _release.SetResult(null);

    public void Fail(Exception exception) => _release.SetResult(exception);

    /// <summary>Releases the gate if nobody has (teardown: a failed assertion never leaves a store call parked).</summary>
    public void TryRelease() => _release.TrySetResult(null);

    /// <summary>Blocks, or pumps when called on a <see cref="SingleThreadContext"/> (a held call may need the pump to be made).</summary>
    public void WaitEntered() =>
        Assert.True(SingleThreadContext.WaitOrPump(() => Entered.IsSet, Entered.Wait, TimeSpan.FromSeconds(10)), "the store call was never made");
}
