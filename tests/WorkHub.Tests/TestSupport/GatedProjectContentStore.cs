using System.Collections.Concurrent;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;

namespace WorkHub.Tests.TestSupport;

/// <summary>
/// Wraps a real <see cref="IProjectContentStore"/> and can hold the next call for a key until the test releases or fails it.
/// A held listing reads the database when CALLED and delivers that snapshot only when released (a slow query).
/// Keys: "list:root", "list:{folderId}", "createFolder", "renameFolder", "createLink", "updateLink", "moveFolder", "moveLink", "containerPath", the note keys and the Trash keys below.
/// </summary>
internal sealed class GatedProjectContentStore(IProjectContentStore inner) : IProjectContentStore
{
    private readonly ConcurrentDictionary<string, StoreGate> _gates = new();
    private readonly ConcurrentQueue<StoreGate> _issued = new();

    public StoreGate Hold(string key)
    {
        var gate = _gates[key] = new StoreGate();
        _issued.Enqueue(gate);
        return gate;
    }

    /// <summary>Teardown: releases every gate this store handed out that is still held.</summary>
    public void ReleaseAll()
    {
        foreach (var gate in _issued)
            gate.TryRelease();
    }

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

    /// <summary>Key "containerPath": the note editor's location lookup.</summary>
    public Task<ProjectContainerPath?> GetContainerPathAsync(long projectNodeId, long? folderId, CancellationToken cancellationToken = default) =>
        Gated("containerPath", () => inner.GetContainerPathAsync(projectNodeId, folderId, CancellationToken.None));

    public Task<IReadOnlyList<WorkspaceFolderPath>> ListFolderMoveTargetsAsync(long projectNodeId, long? movingFolderId, CancellationToken cancellationToken = default) =>
        inner.ListFolderMoveTargetsAsync(projectNodeId, movingFolderId, cancellationToken);

    public Task<ContentMoveOutcome> MoveFolderAsync(long folderId, long? expectedParentFolderId, long? targetParentFolderId, CancellationToken cancellationToken = default) =>
        Gated("moveFolder", () => inner.MoveFolderAsync(folderId, expectedParentFolderId, targetParentFolderId, cancellationToken), readFirst: false);

    public Task<ContentMoveOutcome> MoveLinkAsync(long linkId, long? expectedFolderId, long? targetFolderId, CancellationToken cancellationToken = default) =>
        Gated("moveLink", () => inner.MoveLinkAsync(linkId, expectedFolderId, targetFolderId, cancellationToken), readFirst: false);

    public static string Note(long noteId) => $"getNote:{noteId}";

    public Task<ProjectNote?> GetNoteAsync(long noteId, CancellationToken cancellationToken = default) =>
        Gated(Note(noteId), () => inner.GetNoteAsync(noteId, CancellationToken.None));

    /// <summary>Key "getNotes": the reconciliation read (snapshot taken when called, delivered on release).</summary>
    public Task<IReadOnlyList<ProjectNote>> GetNotesAsync(IReadOnlyCollection<long> noteIds, CancellationToken cancellationToken = default) =>
        Gated("getNotes", () => inner.GetNotesAsync(noteIds, CancellationToken.None));

    public Task<ProjectNote> CreateNoteAsync(long projectNodeId, long? folderId, ProjectNoteDraft draft, CancellationToken cancellationToken = default) =>
        Gated("createNote", () => inner.CreateNoteAsync(projectNodeId, folderId, draft, cancellationToken), readFirst: false);

    public Task<NoteStoreSaveResult> SaveNoteAsync(long noteId, long expectedRevision, ProjectNoteDraft draft, CancellationToken cancellationToken = default) =>
        Gated("saveNote", () => inner.SaveNoteAsync(noteId, expectedRevision, draft, cancellationToken), readFirst: false);

    public Task<ProjectNote> SaveNoteAsNewAsync(long originalNoteId, long projectNodeId, long? lastKnownFolderId, ProjectNoteDraft draft, CancellationToken cancellationToken = default) =>
        Gated("saveNoteAsNew", () => inner.SaveNoteAsNewAsync(originalNoteId, projectNodeId, lastKnownFolderId, draft, cancellationToken), readFirst: false);

    public Task<ContentMoveOutcome> MoveNoteAsync(long noteId, long? expectedFolderId, long? targetFolderId, CancellationToken cancellationToken = default) =>
        Gated("moveNote", () => inner.MoveNoteAsync(noteId, expectedFolderId, targetFolderId, cancellationToken), readFirst: false);

    /// <summary>Key "contentState": the fresh read after a refused Rename / Edit / Move (snapshot taken when called, delivered on release).</summary>
    public Task<ContentState?> GetContentStateAsync(ContentRef item, CancellationToken cancellationToken = default) =>
        Gated("contentState", () => inner.GetContentStateAsync(item, CancellationToken.None));

    /// <summary>Key "countTrash": the confirmation count (snapshot taken when called, delivered on release).</summary>
    public Task<TrashCount> CountTrashSetAsync(ContentRef item, CancellationToken cancellationToken = default) =>
        Gated("countTrash", () => inner.CountTrashSetAsync(item, CancellationToken.None));

    /// <summary>Key "trash": the write runs only when released (a held write, as with the other write keys).</summary>
    public Task<TrashResult> TrashAsync(ContentRef item, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _trashCalls);
        return Gated("trash", () => inner.TrashAsync(item, cancellationToken), readFirst: false);
    }

    /// <summary>Key "listTrash".</summary>
    public Task<IReadOnlyList<TrashEntry>> ListTrashAsync(long projectNodeId, CancellationToken cancellationToken = default) =>
        Gated("listTrash", () => inner.ListTrashAsync(projectNodeId, CancellationToken.None));

    /// <summary>Key "restore": Restore from the Trash view and Undo.</summary>
    public Task<RestoreResult> RestoreAsync(long entryId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _restoreCalls);
        return Gated("restore", () => inner.RestoreAsync(entryId, cancellationToken), readFirst: false);
    }

    private int _trashCalls;
    private int _restoreCalls;

    /// <summary>Store calls of Trash / Restore so far (duplicate-prevention checks).</summary>
    public int TrashCalls => Volatile.Read(ref _trashCalls);

    public int RestoreCalls => Volatile.Read(ref _restoreCalls);

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
