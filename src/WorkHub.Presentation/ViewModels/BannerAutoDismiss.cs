namespace WorkHub.Presentation.ViewModels;

/// <summary>Phase 26 (proposal Revision 2 §3.1): how a banner message behaves. Persistent is the default for every message.</summary>
public enum BannerKind
{
    /// <summary>Stays until it is replaced or dismissed (errors, refusals, progress, notices, anything with Undo or Details).</summary>
    Persistent,

    /// <summary>A reviewed success result without any action in it: hidden 5 seconds after it is shown (§2, §3.2).</summary>
    AutoDismiss,
}

/// <summary>Phase 26: schedules a banner's 5-second expiry; injectable so tests fire it by hand (AD1–AD19).</summary>
public interface IBannerClock
{
    /// <summary>Runs <paramref name="callback"/> once after <paramref name="delay"/> on the caller's context; disposing cancels it.</summary>
    IDisposable Schedule(TimeSpan delay, Action callback);
}

/// <summary>The production clock: the same delay-then-post-to-the-UI-context pattern as the Phase 25 hover clock.</summary>
public sealed class TaskDelayBannerClock : IBannerClock
{
    private readonly TaskDelayHoverClock _inner = new();

    public IDisposable Schedule(TimeSpan delay, Action callback) => _inner.Schedule(delay, callback);
}

/// <summary>
/// Phase 26 (proposal Revision 2 §3.2): the auto-dismiss schedule of ONE banner. The owner (a view model) reports every message it
/// shows, and the view reports whether the banner is visible, whether the pointer is over it and whether keyboard focus is inside it.
/// Two counters keep stale callbacks harmless:
/// <list type="bullet">
/// <item>the <b>message turn</b> changes with every shown message (the same text again included) and with Dismiss;</item>
/// <item>the <b>timer arm</b> changes with every scheduling and every cancel, so an earlier arm of the SAME message is stale too.</item>
/// </list>
/// The expiry clears the message only while its turn and arm are both current, the banner is visible, the pointer and focus are both
/// outside it and the owner is active. The view never clears a banner itself.
/// </summary>
public sealed class BannerAutoDismiss
{
    /// <summary>The one delay (§3.2 rule 1).</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(5);

    private readonly Action _clear;
    private IDisposable? _pending;
    private BannerKind _kind;
    private bool _visible;
    private bool _pointerOver;
    private bool _focusWithin;
    private bool _ownerActive = true;
    private bool _shutDown;

    /// <param name="clear">Clears the owner's message exactly as Dismiss does.</param>
    public BannerAutoDismiss(Action clear)
    {
        ArgumentNullException.ThrowIfNull(clear);
        _clear = clear;
    }

    /// <summary>Tests: the clock (production: <see cref="TaskDelayBannerClock"/>).</summary>
    public IBannerClock Clock { get; set; } = new TaskDelayBannerClock();

    /// <summary>The message turn (§3.2).</summary>
    public int Turn { get; private set; }

    /// <summary>The timer arm (§3.2).</summary>
    public int Arm { get; private set; }

    /// <summary>A timer is pending for the current turn and arm.</summary>
    public bool IsArmed => _pending is not null;

    /// <summary>The current message's kind (a Persistent message is never armed).</summary>
    public BannerKind Kind => _kind;

    /// <summary>Rule 4/5: a message was shown (any kind, the same text again included) or the banner was cleared (null).</summary>
    public void OnShown(string? text, BannerKind kind)
    {
        Turn++;
        Cancel();
        _kind = text is null ? BannerKind.Persistent : kind;
        if (CanCount)
            ArmTimer();
    }

    /// <summary>Rule 8: Undo or Details joined the current message — it becomes Persistent and any countdown stops.</summary>
    public void MakePersistent()
    {
        _kind = BannerKind.Persistent;
        Cancel();
    }

    /// <summary>Rules 6, 7, 11: the view's report (visible, pointer over the banner, keyboard focus inside the banner).</summary>
    public void ReportView(bool visible, bool pointerOver, bool focusWithin) =>
        Change(() => (_visible, _pointerOver, _focusWithin) = (visible, pointerOver, focusWithin));

    /// <summary>Rule 7: the owner's surface is active (Workspace tab selected; the content view not deactivated).</summary>
    public void SetOwnerActive(bool active) => Change(() => _ownerActive = active);

    /// <summary>Rule 9: the owner is deactivated for good (content view replaced, window closed). Nothing is armed afterwards.</summary>
    public void Shutdown()
    {
        _shutDown = true;
        Cancel();
    }

    private bool CanCount => !_shutDown && _ownerActive && _visible && !_pointerOver && !_focusWithin;

    private void Change(Action apply)
    {
        var before = CanCount;
        apply();
        var after = CanCount;
        if (before == after)
            return;
        if (after)
            ArmTimer(); // shown again, or the pointer and focus both left: a fresh 5 s (D13(a), D14(a))
        else
            Cancel();   // hidden, hovered or focused: hold
    }

    /// <summary>Rule 1: arm only for an AutoDismiss message; every scheduling gets a new arm number.</summary>
    private void ArmTimer()
    {
        if (_kind != BannerKind.AutoDismiss)
            return;
        Cancel();
        var (turn, arm) = (Turn, ++Arm);
        _pending = Clock.Schedule(Delay, () => OnExpired(turn, arm));
    }

    /// <summary>Rule 2: dispose the pending timer AND retire its arm number, so a callback that still runs is stale.</summary>
    private void Cancel()
    {
        Arm++;
        _pending?.Dispose();
        _pending = null;
    }

    /// <summary>Rule 3.</summary>
    private void OnExpired(int turn, int arm)
    {
        if (turn != Turn || arm != Arm || _kind != BannerKind.AutoDismiss || !CanCount)
            return;
        _pending = null;
        _clear(); // the owner shows null: a new turn
    }
}

/// <summary>
/// Phase 26 (proposal Revision 2 §3.3): the business outcome of a Project restore or a Folder clear, returned next to the notice (display)
/// and the placed node (tree / selection). The Undo callers choose <see cref="BannerKind.AutoDismiss"/> only for <see cref="Restored"/>.
/// </summary>
public enum RestoreResultKind
{
    /// <summary>Restored / unarchived and placed back in the Workspace tree (a not-archived Project, or a cleared Folder).</summary>
    Restored,

    /// <summary>A Project archived by an earlier build came back archived: the notice says where to find it.</summary>
    RestoredArchived,

    /// <summary>The store refused (name conflict, archived or trashed parent, marked again elsewhere): nothing was restored.</summary>
    Refused,

    /// <summary>Another window already restored / unarchived it.</summary>
    AlreadyCleared,

    /// <summary>The node no longer exists.</summary>
    Missing,

    /// <summary>The store call failed: nothing was restored.</summary>
    Failed,
}
