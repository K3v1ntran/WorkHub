using System.Collections.Concurrent;
using WorkHub.Core.Workspace;

namespace WorkHub.Tests.TestSupport;

/// <summary>
/// Wraps a real <see cref="IWorkspaceStore"/> and can hold the next call for a key until the test releases or fails it.
/// A held read runs against the database when it is CALLED and delivers that snapshot only when released, which models a
/// slow query whose result arrives after newer changes. Keys: "children:root", "children:{id}", "details:{id}",
/// "createFolder", "createProject", "rename", "update", "move".
/// </summary>
internal sealed class GatedWorkspaceStore(IWorkspaceStore inner) : IWorkspaceStore
{
    private readonly ConcurrentDictionary<string, StoreGate> _gates = new();
    private readonly ConcurrentDictionary<string, int> _calls = new();

    /// <summary>Holds the next call for <paramref name="key"/>.</summary>
    public StoreGate Hold(string key) => _gates[key] = new StoreGate();

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

    private async Task<T> Gated<T>(string key, Func<Task<T>> call, bool readFirst = true)
    {
        _calls.AddOrUpdate(key, 1, (_, n) => n + 1);
        if (!_gates.TryRemove(key, out var gate))
            return await call();

        Task<T>? early = readFirst ? call() : null;
        if (early is not null)
            await Task.WhenAny(early); // the read happens now; its result is delivered later
        gate.Entered.Set();
        if (await gate.Released is { } failure)
            throw failure;
        return early is not null ? await early : await call();
    }
}

internal sealed class StoreGate
{
    private readonly TaskCompletionSource<Exception?> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ManualResetEventSlim Entered { get; } = new();

    public Task<Exception?> Released => _release.Task;

    public void Release() => _release.SetResult(null);

    public void Fail(Exception exception) => _release.SetResult(exception);

    public void WaitEntered() => Assert.True(Entered.Wait(TimeSpan.FromSeconds(10)), "the store call was never made");
}
