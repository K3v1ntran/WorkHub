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

    public static string Children(long? parentId) => parentId is { } id ? $"children:{id}" : "children:root";

    public Task<IReadOnlyList<WorkspaceNode>> ListChildrenAsync(long? parentId, CancellationToken cancellationToken = default) =>
        Gated(Children(parentId), () => inner.ListChildrenAsync(parentId, CancellationToken.None));

    public Task<WorkspaceNode> CreateFolderAsync(long? parentId, string name, CancellationToken cancellationToken = default) =>
        Gated("createFolder", () => inner.CreateFolderAsync(parentId, name, cancellationToken), readFirst: false);

    public Task<WorkspaceNode> CreateProjectAsync(long? parentId, string projectId, ProjectDraft draft, CancellationToken cancellationToken = default) =>
        Gated("createProject", () => inner.CreateProjectAsync(parentId, projectId, draft, cancellationToken), readFirst: false);

    public Task<bool> RenameAsync(long nodeId, string name, CancellationToken cancellationToken = default) =>
        Gated("rename", () => inner.RenameAsync(nodeId, name, cancellationToken), readFirst: false);

    public Task<ProjectDetails?> GetProjectAsync(long nodeId, CancellationToken cancellationToken = default) =>
        Gated($"details:{nodeId}", () => inner.GetProjectAsync(nodeId, CancellationToken.None));

    public Task<ProjectDetails?> UpdateProjectAsync(long nodeId, ProjectDraft draft, CancellationToken cancellationToken = default) =>
        Gated("update", () => inner.UpdateProjectAsync(nodeId, draft, cancellationToken), readFirst: false);

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
        Gated("nodeState", () => inner.GetNodeStateAsync(nodeId, CancellationToken.None));

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
