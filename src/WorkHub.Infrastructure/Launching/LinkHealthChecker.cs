using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace.Content;

namespace WorkHub.Infrastructure.Launching;

/// <summary>Raw answer of one read-only filesystem probe.</summary>
public sealed record LinkProbeResult(bool Exists, bool IsDirectory, int Win32Error, Exception? Error)
{
    public static LinkProbeResult Found(bool isDirectory) => new(true, isDirectory, 0, null);

    public static LinkProbeResult Failed(int win32Error, Exception? error = null) => new(false, false, win32Error, error);
}

/// <summary>One blocking, read-only existence probe. Implementations never open, lock, create, modify or delete anything.</summary>
public interface ILinkProbe
{
    LinkProbeResult Probe(string path);
}

/// <summary>Reads attributes only (<see cref="File.GetAttributes(string)"/>): no handle on file contents, no sharing conflicts.</summary>
public sealed class FileSystemLinkProbe : ILinkProbe
{
    public LinkProbeResult Probe(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return LinkProbeResult.Found((attributes & FileAttributes.Directory) != 0);
        }
        catch (Exception ex)
        {
            return LinkProbeResult.Failed(Win32Code(ex), ex);
        }
    }

    /// <summary>Win32 error code carried in an exception's HRESULT (FACILITY_WIN32), else 0.</summary>
    internal static int Win32Code(Exception ex) =>
        ((uint)ex.HResult & 0xFFFF0000u) == 0x80070000u ? ex.HResult & 0xFFFF : 0;
}

/// <summary>
/// The application-wide manual link checker (revision 3 §6.4). Bounds:
/// <list type="bullet">
/// <item>At most <see cref="DefaultMaxOutstandingProbes"/> outstanding OS calls. A slot is taken when the OS call starts and
/// released ONLY when that call returns — never on timeout or cancellation — so at most that many threads can ever be
/// blocked by probes.</item>
/// <item>At most one outstanding OS call per location (drive letter, or UNC server).</item>
/// <item>Requests for a target that already has a queued or running probe share it (case-insensitive target).</item>
/// <item>Admission (getting a location slot and a global slot) must happen within <see cref="DefaultAdmissionDeadline"/> of
/// the request, otherwise the request ends as <see cref="LinkHealthStatus.CheckerBusy"/> with no OS call. When every global
/// slot is held by a stuck call, non-admitted requests end as CheckerBusy immediately.</item>
/// <item>An admitted OS call that does not return within <see cref="DefaultProbeTimeout"/> ends its requests as
/// <see cref="LinkHealthStatus.TimedOut"/>; the call keeps its slot and blocks its location until it returns, and its late
/// result is discarded. Requests for a blocked location end as <see cref="LinkHealthStatus.LocationBusy"/> at once.</item>
/// </list>
/// Every request therefore leaves "Checking" within admission deadline + probe timeout (18 s by default).
/// Web links are never probed: no HTTP, DNS or socket call of any kind.
/// </summary>
public sealed class LinkHealthChecker : ILinkHealthChecker
{
    public const int DefaultMaxOutstandingProbes = 4;
    public static readonly TimeSpan DefaultAdmissionDeadline = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(8);

    private readonly object _gate = new();
    private readonly ILinkProbe _probe;
    private readonly TimeProvider _time;
    private readonly IAppLog? _log;
    private readonly int _maxOutstanding;
    private readonly TimeSpan _admissionDeadline;
    private readonly TimeSpan _probeTimeout;

    private readonly LinkedList<ProbeWork> _queue = new();
    private readonly Dictionary<string, ProbeWork> _byTarget = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _busyLocations = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _blockedLocations = new(StringComparer.OrdinalIgnoreCase);
    private int _outstanding;
    private int _stuck;

    public LinkHealthChecker(IAppLog? log = null)
        : this(new FileSystemLinkProbe(), TimeProvider.System, log)
    {
    }

    internal LinkHealthChecker(
        ILinkProbe probe,
        TimeProvider time,
        IAppLog? log = null,
        int maxOutstanding = DefaultMaxOutstandingProbes,
        TimeSpan? admissionDeadline = null,
        TimeSpan? probeTimeout = null)
    {
        _probe = probe;
        _time = time;
        _log = log;
        _maxOutstanding = maxOutstanding;
        _admissionDeadline = admissionDeadline ?? DefaultAdmissionDeadline;
        _probeTimeout = probeTimeout ?? DefaultProbeTimeout;
    }

    /// <summary>OS calls that have started and not yet returned (including stuck ones).</summary>
    internal int OutstandingCalls
    {
        get
        {
            lock (_gate)
                return _outstanding;
        }
    }

    /// <summary>Outstanding OS calls that already passed the probe timeout.</summary>
    internal int StuckCalls
    {
        get
        {
            lock (_gate)
                return _stuck;
        }
    }

    internal int QueuedRequests
    {
        get
        {
            lock (_gate)
                return _queue.Count;
        }
    }

    public async Task<LinkHealthStatus> CheckAsync(ProjectLinkType type, string target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var trimmed = (target ?? string.Empty).Trim();
        if (!Enum.IsDefined(type) || trimmed.Length == 0 || ProjectLinkValidator.TargetError(type, trimmed) is not null)
            return LinkHealthStatus.InvalidTarget;
        if (type == ProjectLinkType.Web)
            return LinkHealthStatus.NotCheckedWeb;

        ProbeWork work;
        lock (_gate)
        {
            var location = LocationKey(trimmed);
            if (_blockedLocations.Contains(location))
                return LinkHealthStatus.LocationBusy;

            if (!_byTarget.TryGetValue(trimmed, out work!))
            {
                if (_stuck >= _maxOutstanding)
                    return LinkHealthStatus.CheckerBusy; // no capacity can free up until a stuck call returns

                work = new ProbeWork(trimmed, location);
                _byTarget[trimmed] = work;
                work.QueueNode = _queue.AddLast(work);
                var queued = work;
                work.AdmissionTimer = _time.CreateTimer(_ => OnAdmissionDeadline(queued), null, _admissionDeadline, Timeout.InfiniteTimeSpan);
            }

            work.Waiters++;
            PumpLocked();
        }

        try
        {
            var outcome = await work.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return outcome.Status ?? Classify(type, outcome.Result!);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_gate)
            {
                // A queued probe nobody waits for any more is removed; a running one cannot be interrupted.
                if (--work.Waiters == 0 && work.QueueNode is not null)
                {
                    RemoveQueuedLocked(work);
                    ForgetLocked(work);
                    work.Completion.TrySetCanceled(cancellationToken);
                }
            }

            throw;
        }
    }

    /// <summary>Maps a raw probe answer to a status. Only "not found" answers are reported as Missing.</summary>
    internal static LinkHealthStatus Classify(ProjectLinkType type, LinkProbeResult result)
    {
        if (result.Exists)
            return (type == ProjectLinkType.Folder) == result.IsDirectory ? LinkHealthStatus.Found : LinkHealthStatus.WrongType;

        return result.Win32Error switch
        {
            2 or 3 => LinkHealthStatus.Missing,                                   // FILE_NOT_FOUND, PATH_NOT_FOUND
            5 or 1326 => LinkHealthStatus.Inaccessible,                           // ACCESS_DENIED, LOGON_FAILURE
            21 or 53 or 64 or 67 or 1203 or 1231 or 1232 => LinkHealthStatus.Unreachable, // NOT_READY, BAD_NETPATH, NETNAME_DELETED, BAD_NET_NAME, NO_NET_OR_BAD_PATH, NETWORK/HOST_UNREACHABLE
            _ => result.Error is UnauthorizedAccessException ? LinkHealthStatus.Inaccessible : LinkHealthStatus.Unknown,
        };
    }

    /// <summary>Concurrency location: "C:" for drive paths, "\\SERVER" for UNC paths.</summary>
    internal static string LocationKey(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var end = path.IndexOf('\\', 2);
            return (end < 0 ? path : path[..end]).ToUpperInvariant();
        }

        return path.Length >= 2 && path[1] == ':' ? path[..2].ToUpperInvariant() : path.ToUpperInvariant();
    }

    // ---- Scheduling (all under _gate) ----------------------------------------------------------------------------------

    private void PumpLocked()
    {
        // Requests for a location whose call is stuck end at once; when every slot is stuck, nothing can be admitted.
        for (var node = _queue.First; node is not null;)
        {
            var next = node.Next;
            var work = node.Value;
            if (_blockedLocations.Contains(work.Location))
                CompleteQueuedLocked(work, LinkHealthStatus.LocationBusy);
            else if (_stuck >= _maxOutstanding)
                CompleteQueuedLocked(work, LinkHealthStatus.CheckerBusy);
            node = next;
        }

        // FIFO admission: the oldest request whose location is free gets the next global slot.
        for (var node = _queue.First; node is not null && _outstanding < _maxOutstanding;)
        {
            var next = node.Next;
            var work = node.Value;
            if (!_busyLocations.Contains(work.Location))
            {
                RemoveQueuedLocked(work);
                StartLocked(work);
            }

            node = next;
        }
    }

    private void StartLocked(ProbeWork work)
    {
        _outstanding++;
        _busyLocations.Add(work.Location);
        work.Running = true;
        work.ProbeTimer = _time.CreateTimer(_ => OnProbeTimeout(work), null, _probeTimeout, Timeout.InfiniteTimeSpan);
        _ = Task.Run(() => RunProbe(work));
    }

    private void RunProbe(ProbeWork work)
    {
        LinkProbeResult result;
        try
        {
            result = _probe.Probe(work.Target);
        }
        catch (Exception ex)
        {
            result = LinkProbeResult.Failed(0, ex);
        }

        if (result is { Exists: false, Error: { } error } && Classify(ProjectLinkType.File, result) == LinkHealthStatus.Unknown)
            _log?.Warn($"Link check failed unexpectedly ({error.GetType().Name}: {error.Message}).");

        lock (_gate)
        {
            work.Finished = true;
            work.ProbeTimer?.Dispose();
            _outstanding--; // the slot is released only now, when the OS call has actually returned
            _busyLocations.Remove(work.Location);
            if (work.Stuck)
            {
                _stuck--;
                _blockedLocations.Remove(work.Location); // its late result is discarded
            }
            else
            {
                ForgetLocked(work);
                work.Completion.TrySetResult(new ProbeOutcome(result, null));
            }

            PumpLocked();
        }
    }

    private void OnProbeTimeout(ProbeWork work)
    {
        lock (_gate)
        {
            if (work.Finished || work.Stuck)
                return;
            work.Stuck = true;
            _stuck++;
            _blockedLocations.Add(work.Location);
            ForgetLocked(work);
            work.Completion.TrySetResult(new ProbeOutcome(null, LinkHealthStatus.TimedOut));
            PumpLocked();
        }
    }

    private void OnAdmissionDeadline(ProbeWork work)
    {
        lock (_gate)
        {
            if (work.QueueNode is null)
                return; // admitted, completed or cancelled meanwhile
            CompleteQueuedLocked(work, LinkHealthStatus.CheckerBusy);
        }
    }

    private void CompleteQueuedLocked(ProbeWork work, LinkHealthStatus status)
    {
        RemoveQueuedLocked(work);
        ForgetLocked(work);
        work.Completion.TrySetResult(new ProbeOutcome(null, status));
    }

    private void RemoveQueuedLocked(ProbeWork work)
    {
        if (work.QueueNode is { } node)
            _queue.Remove(node);
        work.QueueNode = null;
        work.AdmissionTimer?.Dispose();
        work.AdmissionTimer = null;
    }

    private void ForgetLocked(ProbeWork work)
    {
        if (_byTarget.TryGetValue(work.Target, out var mapped) && ReferenceEquals(mapped, work))
            _byTarget.Remove(work.Target);
    }

    private readonly record struct ProbeOutcome(LinkProbeResult? Result, LinkHealthStatus? Status);

    private sealed class ProbeWork(string target, string location)
    {
        public string Target { get; } = target;

        public string Location { get; } = location;

        public TaskCompletionSource<ProbeOutcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LinkedListNode<ProbeWork>? QueueNode { get; set; }

        public ITimer? AdmissionTimer { get; set; }

        public ITimer? ProbeTimer { get; set; }

        public int Waiters { get; set; }

        public bool Running { get; set; }

        public bool Finished { get; set; }

        public bool Stuck { get; set; }
    }
}
