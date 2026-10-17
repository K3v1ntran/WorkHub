using System.Text.RegularExpressions;
using WorkHub.Core.Backup;

namespace WorkHub.Core.Restore;

/// <summary>
/// States of one restore operation (Phase 10 §6.2, §8.2, §9). Exactly one operation may be non-terminal at a time.
/// </summary>
public enum RestoreState
{
    /// <summary>Reserved and unconfirmed; meaningful only while its requester holds the Restore lock. Never executed.</summary>
    Staged,

    /// <summary>The user confirmed exactly the recorded candidate, acknowledgement and filesystem facts.</summary>
    Confirmed,

    /// <summary>X1–X4: candidate revalidation, as-found evidence, classification and preservation. Nothing moved yet.</summary>
    Executing,

    /// <summary>X6: the current set (<c>previousInventory</c>) is being moved into <c>previous\</c>.</summary>
    MovingAside,

    /// <summary>X7: the current set is in <c>previous\</c>; <c>Data\</c> holds no database.</summary>
    MovedAside,

    /// <summary>X10: the candidate is <c>Data\workhub.db</c>; normal initialization follows.</summary>
    Published,

    Completed,
    Cancelled,
    Abandoned,
    Aborted,

    /// <summary>Put back previous data was requested (restart-time). Cannot be cancelled (R25).</summary>
    UndoRequested,

    Undoing,
    RestoredAside,
    Undone,
}

public static class RestoreStates
{
    public static bool IsTerminal(RestoreState state) =>
        state is RestoreState.Completed or RestoreState.Cancelled or RestoreState.Abandoned or RestoreState.Aborted or RestoreState.Undone;

    /// <summary>R25: the original Restore may be cancelled only before anything is moved.</summary>
    public static bool IsCancellable(RestoreState state) =>
        state is RestoreState.Staged or RestoreState.Confirmed or RestoreState.Executing;
}

/// <summary>Current-data classes (§7.2, §7.3). <see cref="NeedsRecovery"/> exists only at request time.</summary>
public enum CurrentDataClass
{
    Healthy,
    Degraded,
    Unreadable,
    Missing,
    NeedsRecovery,
}

public enum DegradedKind
{
    None,
    Newer,
    Foreign,
    StructureDiffers,
    ForeignKeys,
}

/// <param name="Version">Schema version (user_version) when it could be read.</param>
public sealed record DataClassification(CurrentDataClass Class, DegradedKind Sub = DegradedKind.None, int? Version = null)
{
    public static DataClassification Missing { get; } = new(CurrentDataClass.Missing);

    public static DataClassification Unreadable { get; } = new(CurrentDataClass.Unreadable);

    public override string ToString() => Class switch
    {
        CurrentDataClass.Healthy => $"Healthy(v{Version})",
        CurrentDataClass.Degraded => $"Degraded({Sub}, v{Version})",
        _ => Class.ToString(),
    };
}

public enum UndoOrigin
{
    /// <summary>The candidate was published: the restored set (<c>Ic</c> + sidecars) is moved to <c>restored\</c>.</summary>
    AfterPublication,

    /// <summary>The candidate is still staged: the restored set is empty; <c>restored\</c> is never created.</summary>
    BeforePublication,
}

public enum SafetyKind
{
    None,

    /// <summary>A fully validated standalone snapshot (<c>workhub_prerestore_v{N}_…</c>).</summary>
    Validated,

    /// <summary>A faithful preservation copy (<c>workhub_preserved_v{N}_…</c>); not a validated backup.</summary>
    Preserved,
}

/// <summary>One file of an inventory. Files are identified by name and file ID; the size is informational.</summary>
public sealed record InventoryFile(string Name, long Size, string FileId);

public sealed record RestoreBuildInfo(string App, int LatestSchema);

/// <summary>Identity of the <c>Data\</c> directory (volume serial + file ID). A mismatch means a copied folder.</summary>
public sealed record DirectoryIdentity(string Volume, string FileId);

public sealed record FileSystemFacts(string Name, string Serial);

public sealed record RestoreSource(
    BackupFolder Folder,
    string FileName,
    BackupKind Kind,
    int? SchemaVersion,
    DateTimeOffset? Taken,
    long Size,
    string Sha256);

public sealed record CandidateFacts(long Size, string Sha256, string FileId, int SchemaVersion, BackupCompatibility Compatibility);

public sealed record SafetyRecord(SafetyKind Kind, string? FileName, string? Sha256);

public sealed record CompletionRecord(string Outcome, int FromVersion, int ToVersion, string? MigrationBackup);

public sealed record UndoRecord(DateTimeOffset RequestedUtc, UndoOrigin Origin, IReadOnlyList<InventoryFile>? RestoredInventory = null);

/// <summary>
/// Journal version 2 (§8.3). Published only by a holder of the Restore lock, through temp + write-through + rename.
/// Every publication increments <see cref="Revision"/> by exactly one; actions taken on what a user saw compare state
/// and revision after re-reading under the Restore lock. The journal stores names only; all paths are derived.
/// </summary>
public sealed record RestoreJournal
{
    public const string FormatName = "WorkHub.Restore";
    public const int CurrentVersion = 2;

    public string Format { get; init; } = FormatName;

    public int Version { get; init; } = CurrentVersion;

    public required string Op { get; init; }

    public required RestoreState State { get; init; }

    public required int Revision { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public required DateTimeOffset UpdatedUtc { get; init; }

    public required RestoreBuildInfo Build { get; init; }

    public required DirectoryIdentity DataDir { get; init; }

    public required FileSystemFacts Fs { get; init; }

    public required RestoreSource Source { get; init; }

    public CandidateFacts? Candidate { get; init; }

    public DataClassification? RequestClass { get; init; }

    public DateTimeOffset? ConfirmedUtc { get; init; }

    public DataClassification? Acknowledgement { get; init; }

    /// <summary>X2, before any SQLite open; evidence only (with <c>asfound\</c>). Never drives moves.</summary>
    public IReadOnlyList<InventoryFile>? AsFoundInventory { get; init; }

    public bool AsFound { get; init; }

    /// <summary>Refreshed at X5 after every X4 connection closed; drives moves and reconciliation.</summary>
    public IReadOnlyList<InventoryFile>? PreviousInventory { get; init; }

    public DataClassification? ExecutionClass { get; init; }

    public SafetyRecord? Safety { get; init; }

    public string? AbortReason { get; init; }

    /// <summary>Required in <see cref="RestoreState.Cancelled"/>, absent otherwise (R25).</summary>
    public RestoreState? CancelledFrom { get; init; }

    public CompletionRecord? Completed { get; init; }

    public UndoRecord? Undo { get; init; }

    /// <summary>The next publication: new state, revision + 1, updated time.</summary>
    public RestoreJournal Next(RestoreState state, DateTimeOffset now) => this with { State = state, Revision = Revision + 1, UpdatedUtc = now };
}

/// <summary>Operation identifiers: <c>{utc:yyyyMMdd-HHmmss}-{8 hex}</c>. Only such names are ever turned into paths.</summary>
public static partial class RestoreOpId
{
    [GeneratedRegex("^[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool IsValid(string? op) => op is not null && Pattern().IsMatch(op);

    public static string New(DateTimeOffset utcNow) =>
        $"{utcNow.UtcDateTime:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
}

/// <summary>Fixed names inside <c>Data\Restore\</c> and in the database set.</summary>
public static class RestoreNames
{
    public const string Journal = "journal.json";
    public const string JournalTemp = "journal.json.tmp";
    public const string Candidate = "candidate.db";
    public const string Previous = "previous";
    public const string Restored = "restored";
    public const string AsFound = "asfound";
    public const string LockFile = "restore.lock";
    public const string Database = "workhub.db";

    /// <summary>The database file and the only sidecars that belong to it.</summary>
    public static IReadOnlyList<string> DatabaseSet { get; } = [Database, Database + "-journal", Database + "-wal", Database + "-shm"];

    public static bool IsSidecar(string name) => name is Database + "-journal" or Database + "-wal" or Database + "-shm";
}
