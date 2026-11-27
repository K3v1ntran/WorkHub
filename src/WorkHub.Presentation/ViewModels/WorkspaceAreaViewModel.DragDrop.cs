using WorkHub.Core.Workspace;

namespace WorkHub.Presentation.ViewModels;

/// <summary>
/// Phase 25 review fix: what the window reports to WPF for a drop. <see cref="Move"/> only when the drop was accepted at drop time
/// and the write slot was taken; <see cref="None"/> for every rejected drop.
/// </summary>
public enum WorkspaceDropEffect
{
    None,
    Move,
}

/// <summary>Phase 25 drop result (tests and diagnostics).</summary>
public enum WorkspaceDropOutcome
{
    /// <summary>Refused before the store (pre-check, session, slot, search): nothing written, banner and Undo unchanged.</summary>
    Rejected,

    /// <summary>The store call threw: not committed (the store commits as its last step); "Nothing was changed."</summary>
    WriteFailed,

    /// <summary>The store refused with a typed reason; its text is the banner, the refused node or destination is reconciled.</summary>
    Refused,

    /// <summary>Committed as "already there": nothing written, no banner.</summary>
    AlreadyThere,

    /// <summary>Moved and the tree shows it (or the destination is simply not loaded).</summary>
    Moved,

    /// <summary>Moved (committed) but the tree could not be updated; never "Nothing was changed", never a second Move.</summary>
    MovedTreeNotUpdated,
}

/// <summary>
/// Phase 25 (proposal Revision 2 + R2a, D1–D6 approved): drag and drop of Projects and Folders in the tree (R1) and the background
/// click (R2). A drop is the existing Move without the dialog: <see cref="WorkspaceService.MoveAsync"/> with the node and destination
/// Ids; the store decides. No confirmation, a result banner, no Undo (D3, D4). Hover-expand after ~0.8 s (D5(a)), keyed by drag session
/// and Folder Id. Esc unchanged (D6(b)).
/// </summary>
public sealed partial class WorkspaceAreaViewModel
{
    /// <summary>D5(a): how long the pointer rests on a collapsed Folder during a drag before it expands.</summary>
    public static readonly TimeSpan HoverExpandDelay = TimeSpan.FromMilliseconds(800);

    private int _dragSessionCounter;
    private int? _dragSession;
    private long _dragSourceId;
    private bool _isDragActive;
    private (int Session, long FolderId)? _hover;
    private Func<long?>? _hoverProbe;
    private IDisposable? _hoverTimer;
    private long _hoverTurn; // increases with every scheduled hover: a late callback of an earlier turn never touches a newer one

    /// <summary>Tests: the hover-expand clock (production: a delay posted back to the UI context).</summary>
    internal IDragHoverClock HoverClock { get; set; } = new TaskDelayHoverClock();

    /// <summary>The most recent accepted drop; lets tests await it.</summary>
    internal Task<WorkspaceDropOutcome> CurrentDrop { get; private set; } = Task.FromResult(WorkspaceDropOutcome.Rejected);

    /// <summary>A drag session is running (the window shows the "Move to top level" strip).</summary>
    public bool IsDragActive
    {
        get => _isDragActive;
        private set => SetProperty(ref _isDragActive, value);
    }

    private bool IsTreeReplacedBySearch => Search?.IsActive == true;

    // ---- R2: background click ------------------------------------------------------------------------------------------------

    /// <summary>§2.2: a left or right click on the tree background clears the selection (Info closes; New … then creates at the top level).</summary>
    public void ClearSelection() => SelectedNode = null;

    // ---- R1: drag session ---------------------------------------------------------------------------------------------------

    /// <summary>§2.1 step 1: a live, non-placeholder tree node, while writes are allowed and the tree is not replaced by search.</summary>
    public bool CanStartDrag(WorkspaceNodeViewModel? node) =>
        node is { IsPlaceholder: false } && IsRegistered(node) && CanWrite() && !IsTreeReplacedBySearch;

    /// <summary>Starts a drag session of <paramref name="sourceId"/>; null when the node cannot be dragged now.</summary>
    public int? BeginDragSession(long sourceId)
    {
        if (!_nodes.TryGetValue(sourceId, out var source) || !CanStartDrag(source))
            return null;
        EndDragSession(_dragSession);
        _dragSession = ++_dragSessionCounter;
        _dragSourceId = sourceId;
        IsDragActive = true;
        return _dragSession;
    }

    /// <summary>Ends the session (drop done, drag cancelled or left the window): its hover is cancelled.</summary>
    public void EndDragSession(int? session)
    {
        if (session is null || session != _dragSession)
            return;
        CancelDragHover();
        _dragSession = null;
        IsDragActive = false;
    }

    /// <summary>
    /// §2.1 step 3, the UI pre-check (never authoritative): the source and the target are loaded tree nodes; the target is a Folder
    /// (<paramref name="targetFolderId"/>) or the top level (null); it is not the source, not one of its loaded descendants and not
    /// its current parent; the session is current, writes are allowed and the tree is not replaced by search.
    /// </summary>
    public bool CanDrop(int? session, long sourceId, long? targetFolderId) => TryResolveDrop(session, sourceId, targetFolderId, out _, out _);

    private bool TryResolveDrop(int? session, long sourceId, long? targetFolderId, out WorkspaceNodeViewModel source, out WorkspaceNodeViewModel? target)
    {
        source = null!;
        target = null;
        if (session is null || session != _dragSession || !CanWrite() || IsTreeReplacedBySearch)
            return false;
        if (!_nodes.TryGetValue(sourceId, out var s) || s.IsPlaceholder)
            return false;
        source = s;
        if (targetFolderId is not { } id)
            return s.Parent is not null; // the top level, unless it is already there

        if (!_nodes.TryGetValue(id, out var t) || !t.IsFolder || t.IsPlaceholder)
            return false; // a Project row is not a target (D2)
        if (s.Parent is { } parent && parent.Id == t.Id)
            return false; // its current parent
        for (var n = t; n is not null; n = n.Parent)
        {
            if (n.Id == s.Id)
                return false; // itself or a loaded descendant
        }

        target = t;
        return true;
    }

    // ---- R1: hover-expand (D5(a)) -------------------------------------------------------------------------------------------

    /// <summary>
    /// §2.1 step 7: the pointer is over the row area of Folder <paramref name="folderId"/>. Starts the one pending hover for (session,
    /// Folder Id) when it is a collapsed, valid target; the same pair again keeps the pending one; anything else cancels it first.
    /// </summary>
    /// <param name="folderUnderPointer">
    /// Review fix: asked again when the timer expires — the Folder Id whose row area is under the pointer NOW (the window hit-tests
    /// the current pointer position with the §2.3 classifier), or null. The Id of the last DragOver alone never confirms the position.
    /// </param>
    public void DragHoverEnter(int? session, long folderId, Func<long?> folderUnderPointer)
    {
        ArgumentNullException.ThrowIfNull(folderUnderPointer);
        if (session is null || session != _dragSession)
            return;
        if (_hover is { } current && current.Session == session && current.FolderId == folderId)
            return;
        CancelDragHover();
        if (!_nodes.TryGetValue(folderId, out var folder) || !folder.IsFolder || folder.IsExpanded || !CanDrop(session, _dragSourceId, folderId))
            return;
        var hoverSession = session.Value;
        _hover = (hoverSession, folderId);
        _hoverProbe = folderUnderPointer;
        var turn = ++_hoverTurn;
        _hoverTimer = HoverClock.Schedule(HoverExpandDelay, () => OnDragHoverElapsed(hoverSession, folderId, turn));
    }

    /// <summary>The pointer left the Folder row (to another element that is not a Folder target, or out of the tree).</summary>
    public void DragHoverLeave(int? session)
    {
        if (session is not null && session == _dragSession)
            CancelDragHover();
    }

    private void CancelDragHover()
    {
        _hoverTimer?.Dispose();
        _hoverTimer = null;
        _hover = null;
        _hoverProbe = null;
    }

    /// <summary>
    /// On expiry everything is checked again: the session; the pending (session, Folder Id); the row ACTUALLY under the pointer now
    /// (the probe re-runs the hit test — a layout change or a recycled container without a new DragOver is caught); the node still
    /// registered as a live collapsed Folder; and <see cref="CanDrop"/> for the current source and this Folder (a reconcile that moved
    /// the Folder below the source makes it an invalid target).
    /// </summary>
    private void OnDragHoverElapsed(int session, long folderId, long turn)
    {
        if (session != _dragSession || _hover is not { } current || current.Session != session || current.FolderId != folderId
            || turn != _hoverTurn)
            return; // a late timer of another session, Folder or earlier hover turn: never expands anything and leaves the current hover alone
        var probe = _hoverProbe;
        CancelDragHover(); // this hover has expired: however it ends below, the next valid DragOver on this Folder schedules a new timer
        if (probe?.Invoke() != folderId)
            return; // the pointer is no longer over this Folder's row area
        if (!_nodes.TryGetValue(folderId, out var folder) || !folder.IsFolder || folder.IsPlaceholder || folder.IsExpanded
            || !CanWrite() || IsTreeReplacedBySearch || !CanDrop(session, _dragSourceId, folderId))
            return;
        folder.IsExpanded = true; // loads the children through OnExpanded
    }

    // ---- R1: the drop -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// §2.1 step 4, the drop as the window performs it. Synchronously, on the UI thread, in ONE step: re-check (step 4.1; a rejected
    /// drop changes nothing — no store call, banner and Undo unchanged) and, when accepted, take the slot, then
    /// <see cref="BeginWorkspaceWrite"/> (step 4.2), then start <see cref="MoveByDropCoreAsync"/> (its first await comes after), which
    /// releases the slot in its finally. Returns <see cref="WorkspaceDropEffect.Move"/> exactly when the drop was accepted and the slot
    /// taken; the window reports that effect to WPF and never waits for the store.
    /// </summary>
    public WorkspaceDropEffect Drop(int? session, long sourceId, long? targetFolderId)
    {
        if (!TryResolveDrop(session, sourceId, targetFolderId, out var source, out var target))
            return WorkspaceDropEffect.None;
        CancelDragHover();
        IsLifecycleBusy = true; // the slot, on the UI thread, before the core starts (this method never suspends)
        BeginWorkspaceWrite();  // only now: an accepted drop is a Workspace write (withdraws an offered Undo)
        CurrentDrop = MoveByDropCoreAsync(source, target);
        return WorkspaceDropEffect.Move;
    }

    /// <summary>Tests: <see cref="Drop"/> and the drop's outcome (<see cref="WorkspaceDropOutcome.Rejected"/> when not accepted).</summary>
    public Task<WorkspaceDropOutcome> MoveByDropAsync(int? session, long sourceId, long? targetFolderId) =>
        Drop(session, sourceId, targetFolderId) == WorkspaceDropEffect.Move ? CurrentDrop : Task.FromResult(WorkspaceDropOutcome.Rejected);

    private async Task<WorkspaceDropOutcome> MoveByDropCoreAsync(WorkspaceNodeViewModel source, WorkspaceNodeViewModel? target)
    {
        var (sourceId, name, oldParent) = (source.Id, source.Name, source.Parent);
        var (targetId, targetName) = (target?.Id, target?.Name);
        try
        {
            // Write phase (step 4.3): the store checks and updates in one BEGIN IMMEDIATE transaction and commits last.
            WorkspaceResult<bool> result;
            try
            {
                result = await _workspace.MoveAsync(sourceId, targetId);
            }
            catch (Exception ex)
            {
                _log.Error("A drag-and-drop move failed before it was committed.", ex); // no names
                BannerMessage = CouldNotMoveText(name);
                return WorkspaceDropOutcome.WriteFailed;
            }

            if (!result.Succeeded)
            {
                var refusal = result.Error ?? "The move was refused.";
                BannerMessage = refusal;
                await ReconcileRefusedDropAsync(result.Failure, sourceId, targetId, refusal);
                return WorkspaceDropOutcome.Refused;
            }

            if (!result.Value)
                return WorkspaceDropOutcome.AlreadyThere; // nothing written, no banner

            // Tree phase (committed): its failure never turns into "Nothing was changed" and never moves again.
            bool shown;
            try
            {
                shown = await ApplyMovedAsync(source, targetId);
            }
            catch (Exception ex)
            {
                _log.Error("The Workspace tree could not be updated after a committed drag-and-drop move.", ex); // no names
                LeaveStalePlace(source, oldParent);
                shown = false;
            }

            BannerMessage = MovedText(name, targetName) + (shown ? string.Empty : TreeNotUpdatedNote);
            return shown ? WorkspaceDropOutcome.Moved : WorkspaceDropOutcome.MovedTreeNotUpdated;
        }
        finally
        {
            IsLifecycleBusy = false; // every branch, after the tree phase or the reconcile finished (step 4.4)
        }
    }

    /// <summary>§2.1 step 5; a reconcile failure keeps the refusal as the banner (it is only logged).</summary>
    private async Task ReconcileRefusedDropAsync(WorkspaceFailure? failure, long sourceId, long? targetId, string refusal)
    {
        try
        {
            switch (failure)
            {
                case WorkspaceFailure.ProjectTrashed or WorkspaceFailure.ProjectArchived or WorkspaceFailure.NodeNotFound or WorkspaceFailure.AncestorNotLive:
                    await ReconcileNodeAsync(sourceId);
                    break;
                case WorkspaceFailure.ParentNotFound or WorkspaceFailure.DestinationNotLive when targetId is { } destination:
                    await ReconcileNodeAsync(destination, DestinationGoneText);
                    break;
                case WorkspaceFailure.MoveCycle when targetId is { } destination:
                    await ReconcileNodeAsync(destination); // DD4: the loaded tree was stale; place the target where it is now
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error("A Workspace node could not be reconciled after a refused drag-and-drop move.", ex); // no names
            BannerMessage = refusal;
        }
    }

    /// <summary>After an exception in the tree phase: the node leaves the place the tree still shows (as when the destination cannot be shown).</summary>
    private void LeaveStalePlace(WorkspaceNodeViewModel node, WorkspaceNodeViewModel? oldParent)
    {
        (oldParent?.Children ?? Roots).Remove(node);
        (node.Parent?.Children ?? Roots).Remove(node);
        if (IsRegistered(node))
            ForgetSubtree(node);
        if (ReferenceEquals(SelectedNode, node))
            SelectedNode = null;
    }

    internal const string DestinationGoneText = "The destination folder no longer exists. Nothing was moved.";

    internal const string TreeNotUpdatedNote = " The Workspace tree could not be updated.";

    internal static string CouldNotMoveText(string name) => $"Could not move “{name}”. Nothing was changed.";

    /// <summary>D3 result banner: "Moved “X” to “F”." / "Moved “X” to the top level."</summary>
    internal static string MovedText(string name, string? targetName) =>
        targetName is null ? $"Moved “{name}” to the top level." : $"Moved “{name}” to “{targetName}”.";
}
