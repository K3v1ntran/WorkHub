namespace WorkHub.Presentation.ViewModels;

// Phase 25 (proposal Revision 2 + R2a §2.3): the hit-test contract of the Workspace tree, as a pure classifier. The window walks up
// from the mouse event's original source and describes each element it meets; the FIRST decisive element wins.

/// <summary>What the window met on the way up from the pointer.</summary>
public enum TreeHitElementKind
{
    /// <summary>Anything that decides nothing (text, image, panel, presenter, …).</summary>
    Other,

    /// <summary>A <c>ScrollBar</c> or one of its parts.</summary>
    ScrollBar,

    /// <summary>The <c>ToggleButton</c> named <c>Expander</c> in a tree item's template.</summary>
    Expander,

    /// <summary>The <c>Border</c> named <c>Row</c> in a tree item's template (full width; includes the space right of the label).</summary>
    Row,

    /// <summary>A <c>TreeViewItem</c> itself. Never decisive: an item met without first meeting its <c>Row</c> is not that row.</summary>
    Item,

    /// <summary>The Workspace tree itself.</summary>
    Tree,
}

/// <summary>One element of the walk; <see cref="Node"/> is the tree item's node for Expander / Row / Item.</summary>
public readonly record struct TreeHitElement(TreeHitElementKind Kind, WorkspaceNodeViewModel? Node = null);

/// <summary>The class of a pointer position in the tree.</summary>
public enum TreeHitKind
{
    /// <summary>The walk ended without meeting the tree (not a tree point).</summary>
    Outside,

    ScrollBar,

    Expander,

    /// <summary>A row area: <see cref="TreeHit.Node"/> is that row's node.</summary>
    Row,

    /// <summary>The tree background: no row, expander or scroll bar.</summary>
    Background,
}

public readonly record struct TreeHit(TreeHitKind Kind, WorkspaceNodeViewModel? Node = null)
{
    /// <summary>A row of a real (non-placeholder) node.</summary>
    public bool IsNodeRow => Kind == TreeHitKind.Row && Node is { IsPlaceholder: false };
}

public static class WorkspaceTreeHitTest
{
    /// <summary>
    /// §2.3: stops at the first ScrollBar → scroll bar, Expander → expander of its item, Row → row area of its item; reaching the
    /// tree without any of those → background. <see cref="TreeHitElementKind.Item"/> and <see cref="TreeHitElementKind.Other"/> are
    /// passed over.
    /// </summary>
    public static TreeHit Classify(IEnumerable<TreeHitElement> chainFromPointer)
    {
        ArgumentNullException.ThrowIfNull(chainFromPointer);
        foreach (var element in chainFromPointer)
        {
            switch (element.Kind)
            {
                case TreeHitElementKind.ScrollBar:
                    return new TreeHit(TreeHitKind.ScrollBar);
                case TreeHitElementKind.Expander:
                    return new TreeHit(TreeHitKind.Expander, element.Node);
                case TreeHitElementKind.Row:
                    return new TreeHit(TreeHitKind.Row, element.Node);
                case TreeHitElementKind.Tree:
                    return new TreeHit(TreeHitKind.Background);
            }
        }

        return new TreeHit(TreeHitKind.Outside);
    }
}

/// <summary>Phase 25: schedules the hover-expand expiry; injectable so tests drive it (DH1–DH5).</summary>
public interface IDragHoverClock
{
    /// <summary>Runs <paramref name="callback"/> once after <paramref name="delay"/> on the caller's context; disposing cancels it.</summary>
    IDisposable Schedule(TimeSpan delay, Action callback);
}

/// <summary>The production clock: a delay, then the callback posted to the synchronization context that scheduled it (the UI).</summary>
public sealed class TaskDelayHoverClock : IDragHoverClock
{
    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var context = SynchronizationContext.Current;
        var cancel = new CancellationTokenSource();
        _ = Task.Delay(delay, cancel.Token).ContinueWith(_ =>
        {
            if (cancel.IsCancellationRequested)
                return;
            if (context is null)
                callback();
            else
                context.Post(_ => { if (!cancel.IsCancellationRequested) callback(); }, null);
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        return new Cancellation(cancel);
    }

    private sealed class Cancellation(CancellationTokenSource cancel) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
