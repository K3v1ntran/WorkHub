using System.Diagnostics;
using WorkHub.Core.Startup;

namespace WorkHub.Infrastructure.Restore;

/// <summary>
/// The Phase 10 locks of one actor (a starting instance, a running window, a requester) on one data root (§5.1):
/// <list type="bullet">
/// <item><b>G</b> startup Gate, <c>Data\workhub-startup.lock</c>, exclusive. The only lock ever waited for, and only by an
/// actor that holds nothing else.</item>
/// <item><b>P</b> Presence, <c>Data\workhub-instance.lock</c>: shared by every running instance (taken under G, never
/// blocks); exclusive only for an executor, try-only, polled while holding G only.</item>
/// <item><b>R</b> Restore lock, <c>Data\Restore\restore.lock</c>, exclusive, try-only. Required for every change under
/// <c>Data\Restore\</c>. Never held while waiting.</item>
/// </list>
/// Sharing modes are per handle, so two actors in one process exclude each other exactly as two processes do. A killed
/// process releases everything (the OS closes its handles).
/// </summary>
internal sealed class InstanceLocks(WorkHubPaths paths) : IDisposable
{
    private FileStream? _gate;
    private FileStream? _presence;
    private FileStream? _restore;
    private bool _presenceExclusive;

    public bool HoldsGate => _gate is not null;

    public bool HoldsPresence => _presence is not null;

    public bool HoldsPresenceExclusive => _presence is not null && _presenceExclusive;

    public bool HoldsRestoreLock => _restore is not null;

    public bool TryAcquireGate()
    {
        if (_gate is not null)
            return true;
        if (_presence is not null || _restore is not null)
            throw new InvalidOperationException("The startup Gate may only be acquired by an actor that holds no other lock.");
        Directory.CreateDirectory(paths.DataDirectory);
        _gate = TryOpen(paths.StartupGateFile, exclusive: true);
        return _gate is not null;
    }

    public void ReleaseGate()
    {
        _gate?.Dispose();
        _gate = null;
    }

    /// <summary>Under G only: never blocks, because an exclusive holder always holds G.</summary>
    public void TakePresenceShared()
    {
        if (!HoldsGate)
            throw new InvalidOperationException("Presence is registered under the startup Gate.");
        if (_presence is not null)
        {
            if (_presenceExclusive)
                DowngradePresence();
            return;
        }

        _presence = TryOpen(paths.PresenceFile, exclusive: false)
                    ?? throw new IOException("Presence could not be registered although the startup Gate is held.");
        _presenceExclusive = false;
    }

    /// <summary>Try-only, by a holder of G that holds nothing else.</summary>
    public bool TryPresenceExclusive()
    {
        if (!HoldsGate || _restore is not null || _presence is not null)
            throw new InvalidOperationException("Exclusive Presence is tried while holding the startup Gate only.");
        _presence = TryOpen(paths.PresenceFile, exclusive: true);
        _presenceExclusive = _presence is not null;
        return _presenceExclusive;
    }

    /// <summary>Exclusive → shared, still under G (no other actor can take exclusive Presence without G).</summary>
    public void DowngradePresence()
    {
        if (!HoldsGate || !HoldsPresenceExclusive)
            throw new InvalidOperationException("Presence is downgraded under the startup Gate.");
        _presence!.Dispose();
        _presence = null;
        _presenceExclusive = false;
        _presence = TryOpen(paths.PresenceFile, exclusive: false)
                    ?? throw new IOException("Presence could not be downgraded although the startup Gate is held.");
    }

    public void ReleasePresence()
    {
        _presence?.Dispose();
        _presence = null;
        _presenceExclusive = false;
    }

    public bool TryRestoreLock()
    {
        if (_restore is not null)
            return true;
        Directory.CreateDirectory(paths.RestoreDirectory);
        _restore = TryOpen(paths.RestoreLockFile, exclusive: true);
        return _restore is not null;
    }

    /// <summary>Owner-window and waiting-screen writes: a short bounded retry (≤ <paramref name="window"/>), never a wait.</summary>
    public bool TryRestoreLock(TimeSpan window)
    {
        var watch = Stopwatch.StartNew();
        while (!TryRestoreLock())
        {
            if (watch.Elapsed >= window)
                return false;
            Thread.Sleep(20);
        }

        return true;
    }

    public void ReleaseRestoreLock()
    {
        _restore?.Dispose();
        _restore = null;
    }

    public void Dispose()
    {
        ReleaseRestoreLock();
        ReleasePresence();
        ReleaseGate();
    }

    /// <returns>Null when another handle holds the file (sharing violation); other failures throw.</returns>
    private static FileStream? TryOpen(string path, bool exclusive)
    {
        try
        {
            return exclusive
                ? new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
                : new FileStream(path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (IOException ex) when (RestoreIo.IsSharingViolation(ex))
        {
            return null;
        }
    }
}
