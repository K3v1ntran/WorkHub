using System.Collections.Concurrent;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;

namespace WorkHub.Tests.TestSupport;

/// <summary>
/// Wraps a real <see cref="IProjectContentStore"/> and can hold the next call for a key until the test releases or fails it.
/// A held listing reads the database when CALLED and delivers that snapshot only when released (a slow query).
/// Keys: "list:root", "list:{folderId}", "createFolder", "renameFolder", "createLink", "updateLink", "moveFolder", "moveLink".
/// </summary>
internal sealed class GatedProjectContentStore(IProjectContentStore inner) : IProjectContentStore
{
    private readonly ConcurrentDictionary<string, StoreGate> _gates = new();

    public StoreGate Hold(string key) => _gates[key] = new StoreGate();

    public static string List(long? folderId) => folderId is { } id ? $"list:{id}" : "list:root";

    public Task<ProjectContentListResult> ListAsync(long projectNodeId, long? folderId, CancellationToken cancellationToken = default) =>
        Gated(List(folderId), () => inner.ListAsync(projectNodeId, folderId, CancellationToken.None));

    public Task<ProjectFolder> CreateFolderAsync(long projectNodeId, long? parentFolderId, string name, CancellationToken cancellationToken = default) =>
        Gated("createFolder", () => inner.CreateFolderAsync(projectNodeId, parentFolderId, name, cancellationToken), readFirst: false);

    public Task<ProjectFolder?> RenameFolderAsync(long folderId, string name, CancellationToken cancellationToken = default) =>
        Gated("renameFolder", () => inner.RenameFolderAsync(folderId, name, cancellationToken), readFirst: false);

    public Task<ProjectLink> CreateLinkAsync(long projectNodeId, long? folderId, ProjectLinkDraft draft, CancellationToken cancellationToken = default) =>
        Gated("createLink", () => inner.CreateLinkAsync(projectNodeId, folderId, draft, cancellationToken), readFirst: false);

    public Task<ProjectLink?> UpdateLinkAsync(long linkId, ProjectLinkDraft draft, CancellationToken cancellationToken = default) =>
        Gated("updateLink", () => inner.UpdateLinkAsync(linkId, draft, cancellationToken), readFirst: false);

    public Task<ProjectLink?> GetLinkAsync(long linkId, CancellationToken cancellationToken = default) =>
        inner.GetLinkAsync(linkId, cancellationToken);

    public Task<IReadOnlyList<WorkspaceFolderPath>> ListFolderMoveTargetsAsync(long projectNodeId, long? movingFolderId, CancellationToken cancellationToken = default) =>
        inner.ListFolderMoveTargetsAsync(projectNodeId, movingFolderId, cancellationToken);

    public Task<ContentMoveOutcome> MoveFolderAsync(long folderId, long? expectedParentFolderId, long? targetParentFolderId, CancellationToken cancellationToken = default) =>
        Gated("moveFolder", () => inner.MoveFolderAsync(folderId, expectedParentFolderId, targetParentFolderId, cancellationToken), readFirst: false);

    public Task<ContentMoveOutcome> MoveLinkAsync(long linkId, long? expectedFolderId, long? targetFolderId, CancellationToken cancellationToken = default) =>
        Gated("moveLink", () => inner.MoveLinkAsync(linkId, expectedFolderId, targetFolderId, cancellationToken), readFirst: false);

    private async Task<T> Gated<T>(string key, Func<Task<T>> call, bool readFirst = true)
    {
        if (!_gates.TryRemove(key, out var gate))
            return await call();

        Task<T>? early = readFirst ? call() : null;
        if (early is not null)
            await Task.WhenAny(early);
        gate.Entered.Set();
        if (await gate.Released is { } failure)
            throw failure;
        return early is not null ? await early : await call();
    }
}
