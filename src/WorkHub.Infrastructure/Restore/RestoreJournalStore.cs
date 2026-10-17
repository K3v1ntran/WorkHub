using System.Text.Json;
using System.Text.Json.Serialization;
using WorkHub.Core.Restore;
using WorkHub.Core.Startup;

namespace WorkHub.Infrastructure.Restore;

/// <summary>Result of reading one journal: the journal, or why it cannot be trusted (then everything fails closed).</summary>
internal sealed record JournalRead(RestoreJournal? Journal, string? Problem, bool Missing = false)
{
    public bool IsValid => Journal is not null;
}

/// <summary>
/// Journal version 2 (§8.3). Publication by an R holder only: temp file written through to disk, then renamed over the
/// journal (all-or-nothing under process kills). Readers need no lock: they open with ReadWrite|Delete sharing so a
/// concurrent rename is not blocked, and retry on a sharing violation. Unknown format, version or state fails closed.
/// </summary>
internal sealed class RestoreJournalStore(WorkHubPaths paths, IRestoreFileOps files)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public string JournalPath(string op) => Path.Combine(paths.RestoreOperationDirectory(op), RestoreNames.Journal);

    public string TempPath(string op) => Path.Combine(paths.RestoreOperationDirectory(op), RestoreNames.JournalTemp);

    public JournalRead Read(string op)
    {
        var path = JournalPath(op);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!files.FileExists(path))
                    return new JournalRead(null, "the operation has no journal", Missing: true);
                return Parse(op, files.ReadShared(path));
            }
            catch (IOException ex) when (RestoreIo.IsSharingViolation(ex) && attempt < 50)
            {
                Thread.Sleep(10);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new JournalRead(null, $"the journal could not be read ({ex.GetType().Name})");
            }
        }
    }

    /// <summary>Publishes <paramref name="journal"/>. The caller holds R (enforced by <see cref="IRestoreFileOps"/>).</summary>
    public void Write(RestoreJournal journal)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, Options);
        var temp = TempPath(journal.Op);
        files.WriteThrough(temp, bytes);
        files.Replace(temp, JournalPath(journal.Op));
    }

    internal static JournalRead Parse(string op, byte[] bytes)
    {
        RestoreJournal? journal;
        try
        {
            journal = JsonSerializer.Deserialize<RestoreJournal>(bytes, Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return new JournalRead(null, "the journal is not readable");
        }

        if (journal is null)
            return new JournalRead(null, "the journal is empty");
        if (!string.Equals(journal.Format, RestoreJournal.FormatName, StringComparison.Ordinal) || journal.Version != RestoreJournal.CurrentVersion)
            return new JournalRead(null, "the journal has an unknown format or version");
        if (!Enum.IsDefined(journal.State) || !string.Equals(journal.Op, op, StringComparison.Ordinal) || journal.Revision < 1)
            return new JournalRead(null, "the journal does not belong to its folder or has an unknown state");
        return new JournalRead(journal, null);
    }
}
